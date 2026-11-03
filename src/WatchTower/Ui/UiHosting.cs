using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;

using WatchTower.Hosting;

namespace WatchTower.Ui;

/// <summary>Wires the configuration console into the web host.</summary>
public static class UiHosting
{
    public const string CsrfHeader = "X-WatchTower-Request";
    public const string StampClaim = "wt:stamp";
    public const string LoginRateLimitPolicy = "login";

    /// <summary>
    /// Plans listen endpoints and, when <paramref name="probe"/> is set, drops endpoints whose
    /// certificate is missing or whose port is already taken — a busy port must never stop the
    /// monitoring service itself from starting.
    /// </summary>
    public static (IReadOnlyList<UiEndpoint> Endpoints, IReadOnlyList<string> Problems, X509Certificate2? Certificate)
        PlanEndpoints(UiOptions options, bool probe)
    {
        var plan = UiEndpointPlanner.Plan(options);
        var problems = plan.Problems.ToList();
        var endpoints = plan.Endpoints.ToList();
        X509Certificate2? certificate = null;

        if (endpoints.Any(e => e.Https))
        {
            certificate = FindCertificate(options.CertificateThumbprint!);
            if (certificate is null)
            {
                problems.Add($"Certificate '{options.CertificateThumbprint}' was not found in LocalMachine\\My; HTTPS URLs ignored.");
                endpoints.RemoveAll(e => e.Https);
            }
        }

        if (probe)
        {
            foreach (var endpoint in endpoints.ToList())
            {
                if (IsPortFree(endpoint))
                    continue;
                problems.Add($"{endpoint.Display} is already in use by another program; change Ui:Urls.");
                endpoints.Remove(endpoint);
            }
        }

        return (endpoints, problems, certificate);
    }

    public static void AddWatchTowerUi(
        this WebApplicationBuilder builder, WatchTowerPaths paths, UiOptions options,
        IReadOnlyList<UiEndpoint> endpoints, X509Certificate2? certificate)
    {
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
            foreach (var endpoint in endpoints)
            {
                kestrel.Listen(endpoint.Address, endpoint.Port, listen =>
                {
                    if (endpoint.Https)
                        listen.UseHttps(certificate!);
                });
            }
        });

        var services = builder.Services;

        services.AddDataProtection()
            .SetApplicationName("WatchTower")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.DataDirectory, "keys")))
            .ProtectKeysWithDpapi(protectToLocalMachine: true);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = "wt_session";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.SlidingExpiration = true;
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(Math.Clamp(options.SessionTimeoutMinutes, 5, 1440));
                cookie.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                cookie.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
                cookie.Events.OnValidatePrincipal = async ctx =>
                {
                    // Changing the password changes the stamp, which revokes every other session.
                    var credentials = ctx.HttpContext.RequestServices.GetRequiredService<AdminCredentials>();
                    if (ctx.Principal?.FindFirstValue(StampClaim) != credentials.Stamp || !credentials.IsConfigured)
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });
        services.AddAuthorization();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(LoginRateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
        });

        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            json.SerializerOptions.DictionaryKeyPolicy = null; // config keys ("Email:SmtpHost") stay verbatim
        });

        services.AddSingleton<AdminCredentials>();
        services.AddSingleton<RestartState>();
        services.AddSingleton<ConfigService>();
        services.AddSingleton<StatusService>();
        services.AddSingleton<SystemInventory>();
    }

    public static WebApplication UseWatchTowerUi(this WebApplication app)
    {
        app.Use(HostGuard);
        app.Use(SecurityHeaders);
        app.Use(CsrfGuard);

        var assets = TryGetAssets(app.Logger);
        if (assets is not null)
        {
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = assets });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = assets,
                // Assets are embedded per build; revalidate so an update never serves stale JS.
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
            });
        }

        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        UiEndpoints.Map(app);
        return app;
    }

    /// <summary>
    /// DNS-rebinding defence. A page on attacker.example can make its DNS name resolve to
    /// 127.0.0.1; the victim's browser then talks to the console over loopback while sending
    /// <c>Host: attacker.example</c>, and the page can read the responses. Loopback connections
    /// must therefore address the console AS loopback (127.0.0.1 / localhost / [::1]); with
    /// <see cref="UiOptions.AllowedHosts"/> set, remote connections are limited to those names too.
    /// </summary>
    private static async Task HostGuard(HttpContext context, Func<Task> next)
    {
        var host = context.Request.Host.Host;
        bool allowed;
        if (IsLocalRequest(context))
        {
            allowed = IsLoopbackHost(host);
        }
        else
        {
            var allowedHosts = context.RequestServices
                .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<UiOptions>>().CurrentValue.AllowedHosts;
            allowed = allowedHosts.Count == 0 ||
                      allowedHosts.Any(h => string.Equals(h.Trim(), host, StringComparison.OrdinalIgnoreCase));
        }

        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"This console does not answer to the host name '{host}'. " +
                        "Open it at http://127.0.0.1 on the server, or add the name to Ui:AllowedHosts.",
            });
            return;
        }
        await next();
    }

    internal static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static async Task SecurityHeaders(HttpContext context, Func<Task> next)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy =
            "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; " +
            "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        if (context.Request.Path.StartsWithSegments("/api"))
            headers.CacheControl = "no-store";
        await next();
    }

    /// <summary>
    /// State-changing API calls must carry a custom header. Browsers cannot attach custom
    /// headers to cross-site requests without a CORS preflight (which this server never
    /// approves), so this blocks CSRF in addition to the SameSite=Strict cookie.
    /// </summary>
    private static async Task CsrfGuard(HttpContext context, Func<Task> next)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/api") &&
            !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            var hasHeader = request.Headers.TryGetValue(CsrfHeader, out var value) && value == "1";
            // Compare the complete origin (scheme + host + port), not only the authority.
            var origin = request.Headers.Origin.ToString();
            var originOk = string.IsNullOrEmpty(origin) ||
                           string.Equals(origin.TrimEnd('/'), $"{request.Scheme}://{request.Host.Value}",
                               StringComparison.OrdinalIgnoreCase);
            if (!hasHeader || !originOk)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid request header." });
                return;
            }
        }
        await next();
    }

    private static IFileProvider? TryGetAssets(ILogger logger)
    {
        try
        {
            return new ManifestEmbeddedFileProvider(typeof(UiHosting).Assembly, "Ui/wwwroot");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Web console assets are missing from this build");
            return null;
        }
    }

    internal static ClaimsPrincipal CreatePrincipal(string stamp) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"), new Claim(StampClaim, stamp)],
            CookieAuthenticationDefaults.AuthenticationScheme));

    internal static bool IsLocalRequest(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        return remote is not null && IPAddress.IsLoopback(remote);
    }

    private static X509Certificate2? FindCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var normalized = thumbprint.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
        return matches.Count > 0 ? matches[0] : null;
    }

    private static bool IsPortFree(UiEndpoint endpoint)
    {
        try
        {
            using var socket = new Socket(endpoint.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(endpoint.Address, endpoint.Port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}