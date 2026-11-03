using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using WatchTower.Hosting;
using WatchTower.Tests.Hosting;
using WatchTower.Ui;

namespace WatchTower.Tests.Ui;

/// <summary>
/// The real console pipeline (auth, CSRF guard, config API) on an in-memory TestServer, with
/// the monitoring workers removed so tests don't sample the machine.
/// </summary>
internal sealed class ConsoleTestHost : IAsyncDisposable
{
    public const string Password = "correct horse battery";

    private readonly WebApplication _app;

    private ConsoleTestHost(TestDataDirectory dir, WebApplication app)
    {
        Directory = dir;
        _app = app;
    }

    public TestDataDirectory Directory { get; }
    public IServiceProvider Services => _app.Services;

    public static async Task<ConsoleTestHost> StartAsync(bool withPassword = true, bool requestsAreLocal = true, string? settingsJson = null)
    {
        var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var settings = System.Text.Json.Nodes.JsonNode.Parse(settingsJson ?? "{}")!.AsObject();
        if (withPassword)
            ConfigService.SetPath(settings, AdminCredentials.HashKey, PasswordHasher.Hash(Password));
        File.WriteAllText(dir.Paths.SettingsFilePath, settings.ToJsonString());

        var builder = (WebApplicationBuilder)WatchTowerHost.CreateBuilder(dir.Paths);
        builder.WebHost.UseTestServer();
        if (requestsAreLocal)
            builder.Services.AddSingleton<IStartupFilter, LoopbackStartupFilter>();

        // Keep the web host, drop the monitoring workers.
        foreach (var worker in builder.Services
                     .Where(d => d.ServiceType == typeof(IHostedService) &&
                                 d.ImplementationType?.Namespace == "WatchTower.Workers")
                     .ToList())
            builder.Services.Remove(worker);

        var app = (WebApplication)WatchTowerHost.Build(builder);
        await app.StartAsync();
        return new ConsoleTestHost(dir, app);
    }

    /// <summary>A browser-like client: keeps cookies and sends the CSRF header unless told not to.</summary>
    public HttpClient CreateClient(bool csrfHeader = true)
    {
        var handler = new CookieHandler(_app.GetTestServer().CreateHandler());
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8750/") };
        if (csrfHeader)
            client.DefaultRequestHeaders.Add(UiHosting.CsrfHeader, "1");
        return client;
    }

    public async Task<HttpClient> SignedInClientAsync()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("api/auth/login", new { password = Password });
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw new InvalidOperationException($"Login failed: {response.StatusCode}");
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Directory.Dispose();
    }

    private sealed class LoopbackStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext ctx, Func<Task> nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
                return nextMiddleware();
            });
            next(app);
        };
    }

    private sealed class CookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly CookieContainer _cookies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var header = _cookies.GetCookieHeader(request.RequestUri!);
            if (!string.IsNullOrEmpty(header))
                request.Headers.Add("Cookie", header);

            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                foreach (var cookie in setCookies)
                    _cookies.SetCookies(request.RequestUri!, cookie);
            return response;
        }
    }
}