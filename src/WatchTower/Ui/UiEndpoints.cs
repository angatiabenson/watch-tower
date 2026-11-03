using System.Text;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

using WatchTower.Hosting;
using WatchTower.Services.Abstractions;

namespace WatchTower.Ui;

/// <summary>Console HTTP API. The contract is documented in Ui/API.md.</summary>
public static class UiEndpoints
{
    public sealed record PasswordRequest(string? Password);
    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
    public sealed record EnrollRequest(string? Code);
    public sealed record TestNotificationRequest(string? Channel);
    public sealed record MaintenanceRequest(int? Minutes, string? Reason);

    public static void Map(WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").AllowAnonymous();

        auth.MapGet("/state", (HttpContext ctx, AdminCredentials credentials) => Results.Ok(new
        {
            setupRequired = !credentials.IsConfigured,
            authenticated = ctx.User.Identity?.IsAuthenticated == true,
            setupAllowedFromHere = UiHosting.IsLocalRequest(ctx),
        }));

        auth.MapPost("/setup", async (HttpContext ctx, PasswordRequest body, AdminCredentials credentials, CancellationToken ct) =>
        {
            if (credentials.IsConfigured)
                return Error(StatusCodes.Status409Conflict, "An administrator password is already set.");
            if (!UiHosting.IsLocalRequest(ctx))
                return Error(StatusCodes.Status403Forbidden,
                    "Initial setup must be done on the server itself (http://127.0.0.1) or with 'WatchTower set-password'.");
            if (PasswordHasher.CheckStrength(body.Password) is { } weak)
                return Error(StatusCodes.Status400BadRequest, weak);
            if (!await credentials.TrySetInitialAsync(body.Password!, ct))
                return Error(StatusCodes.Status409Conflict, "An administrator password is already set.");

            await SignInAsync(ctx, credentials);
            return Results.NoContent();
        }).RequireRateLimiting(UiHosting.LoginRateLimitPolicy);

        auth.MapPost("/login", async (HttpContext ctx, PasswordRequest body, AdminCredentials credentials, ILoggerFactory logs) =>
        {
            if (!credentials.IsConfigured)
                return Error(StatusCodes.Status409Conflict, "Set an administrator password first.");
            if (!credentials.Verify(body.Password ?? string.Empty))
            {
                logs.CreateLogger("WatchTower.Ui").LogWarning(
                    "Failed console sign-in from {Remote}", ctx.Connection.RemoteIpAddress);
                return Error(StatusCodes.Status401Unauthorized, "Incorrect password.");
            }
            await SignInAsync(ctx, credentials);
            return Results.NoContent();
        }).RequireRateLimiting(UiHosting.LoginRateLimitPolicy);

        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        auth.MapPost("/password", async (HttpContext ctx, ChangePasswordRequest body, AdminCredentials credentials, CancellationToken ct) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();
            if (!credentials.Verify(body.CurrentPassword ?? string.Empty))
                return Error(StatusCodes.Status400BadRequest, "Current password is incorrect.");
            if (PasswordHasher.CheckStrength(body.NewPassword) is { } weak)
                return Error(StatusCodes.Status400BadRequest, weak);

            await credentials.ChangeAsync(body.NewPassword!, ct);
            await SignInAsync(ctx, credentials); // refresh this session's stamp; others are revoked
            return Results.NoContent();
        }).RequireRateLimiting(UiHosting.LoginRateLimitPolicy);

        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/status", (StatusService status) => Results.Ok(status.GetStatus()));

        api.MapGet("/config", (ConfigService config) => Results.Ok(config.GetView()));

        api.MapPut("/config", async (ConfigUpdateRequest body, ConfigService config, CancellationToken ct) =>
        {
            var result = await config.ApplyAsync(body, ct);
            return result.Success
                ? Results.Ok(new { restartRequired = result.RestartRequired, warnings = result.Warnings })
                : Results.BadRequest(new { errors = result.Errors });
        });

        api.MapGet("/system/services", (SystemInventory inventory) => Results.Ok(inventory.ListServices()));
        api.MapGet("/system/apppools", async (SystemInventory inventory, CancellationToken ct) =>
            Results.Ok(await inventory.ListAppPoolsAsync(ct)));
        api.MapGet("/system/timezones", () => Results.Ok(SystemInventory.ListTimeZones()));

        api.MapPost("/actions/test-email", async (IEmailService email, AgentInfo agent, CancellationToken ct) =>
        {
            var html = $"""
                <p>This is a test message from <strong>WatchTower {AgentInfo.Version}</strong> on
                <strong>{System.Net.WebUtility.HtmlEncode(agent.MachineName)}</strong>.</p>
                <p>If you can read this, email alerts are configured correctly.</p>
                """;
            var sent = await email.SendEmailAsync($"WatchTower test email from {agent.MachineName}", html, ct);
            return Results.Ok(sent
                ? new { ok = true, message = "Test email sent. Check the recipients' inboxes (and spam folders)." }
                : new { ok = false, message = "The test email could not be sent. Check the SMTP settings, then the logs for details." });
        });

        api.MapPost("/actions/test-notification", async (
            [Microsoft.AspNetCore.Mvc.FromBody] TestNotificationRequest? body,
            Services.Alerting.NotificationDispatcher notifications, AgentInfo agent, CancellationToken ct) =>
        {
            var channel = string.IsNullOrWhiteSpace(body?.Channel) ? null : body.Channel.Trim();
            var results = await notifications.SendTestAsync(channel, agent.MachineName, ct);
            if (results.Count == 0)
            {
                return channel is null
                    ? Results.Ok(new { ok = false, message = "No notification channel is enabled.", results })
                    : Error(StatusCodes.Status404NotFound, $"No enabled notification channel is named '{channel}'.");
            }

            var failed = results.Count(r => !r.Ok);
            return Results.Ok(new
            {
                ok = failed == 0,
                message = failed == 0
                    ? $"Test notification sent to {results.Count} channel(s)."
                    : $"{failed} of {results.Count} channel(s) failed; see the results and the log.",
                results,
            });
        });

        api.MapPost("/actions/maintenance", async (
            MaintenanceRequest body, ConfigService config, Services.Alerting.IMaintenanceState maintenance,
            TimeProvider time, CancellationToken ct) =>
        {
            if (body.Minutes is not (>= 1 and <= Services.Alerting.MaintenanceWindowRules.MaxMinutes))
                return Error(StatusCodes.Status400BadRequest,
                    $"minutes must be between 1 and {Services.Alerting.MaintenanceWindowRules.MaxMinutes}.");
            var reason = body.Reason?.Trim() ?? string.Empty;
            if (reason.Length > Services.Alerting.MaintenanceWindowRules.MaxReasonLength)
                return Error(StatusCodes.Status400BadRequest,
                    $"reason must be at most {Services.Alerting.MaintenanceWindowRules.MaxReasonLength} characters.");

            var until = time.GetUtcNow().AddMinutes(body.Minutes.Value);
            var result = await config.ApplyAsync(new ConfigUpdateRequest
            {
                Changes = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Maintenance:Until"] = System.Text.Json.JsonSerializer.SerializeToElement(
                        Services.Alerting.MaintenanceState.Format(until)),
                    ["Maintenance:Reason"] = System.Text.Json.JsonSerializer.SerializeToElement(reason),
                },
            }, ct);
            return result.Success
                ? Results.Ok(new { maintenance = StatusService.MaintenanceSummary(maintenance.Current) })
                : Results.BadRequest(new { errors = result.Errors });
        });

        api.MapPost("/actions/maintenance/stop", async (
            ConfigService config, Services.Alerting.IMaintenanceState maintenance, CancellationToken ct) =>
        {
            var result = await config.ApplyAsync(new ConfigUpdateRequest { Reset = ["Maintenance:Until", "Maintenance:Reason"] }, ct);
            if (!result.Success)
                return Results.BadRequest(new { errors = result.Errors });

            var current = maintenance.Current;
            return Results.Ok(new
            {
                maintenance = StatusService.MaintenanceSummary(current),
                message = current.Active
                    ? "The maintenance window set by the portal is still active; end it from the portal."
                    : "Maintenance ended.",
            });
        });

        api.MapPost("/actions/enroll", async (EnrollRequest body, ConfigService config, CancellationToken ct) =>
        {
            if (!Portal.EnrollmentCode.TryParse(body.Code, out var code, out var error))
                return Error(StatusCodes.Status400BadRequest, error);

            var changes = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["VisibilityPortal:Enabled"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
                ["VisibilityPortal:Protocol"] = System.Text.Json.JsonSerializer.SerializeToElement("Https"),
                ["VisibilityPortal:BaseUrl"] = System.Text.Json.JsonSerializer.SerializeToElement(code.Url),
                ["VisibilityPortal:AgentId"] = System.Text.Json.JsonSerializer.SerializeToElement(code.AgentId),
                ["VisibilityPortal:AgentToken"] = System.Text.Json.JsonSerializer.SerializeToElement(code.Token),
                ["VisibilityPortal:Tls:RequireTls"] = System.Text.Json.JsonSerializer.SerializeToElement(code.RequireTls),
            };
            var result = await config.ApplyAsync(new ConfigUpdateRequest { Changes = changes }, ct);
            return result.Success
                ? Results.Ok(new
                {
                    portalUrl = code.Url,
                    agentId = code.AgentId,
                    restartRequired = result.RestartRequired,
                    warnings = result.Warnings,
                })
                : Results.BadRequest(new { errors = result.Errors });
        });

        api.MapPost("/actions/restart", (IAgentRestarter restarter) =>
            restarter.TryScheduleRestart("requested from the web console", out var message)
                ? Results.Accepted(value: new { message })
                : Error(StatusCodes.Status409Conflict, message));

        var updates = api.MapGroup("/updates");
        updates.MapGet("", (Updates.UpdateService service) => Results.Ok(service.GetStatus()));
        updates.MapPost("/check", async (Updates.UpdateService service, CancellationToken ct) =>
            Results.Ok(await service.CheckAsync(ct)));
        updates.MapPost("/download", (Updates.UpdateService service) =>
            service.TryStartDownload(countdown: true, out var message)
                ? Results.Accepted(value: new { message, status = service.GetStatus() })
                : Error(StatusCodes.Status409Conflict, message));
        updates.MapPost("/apply", (Updates.UpdateService service) =>
            service.TryInstallNow(out var message)
                ? Results.Accepted(value: new { message })
                : Error(StatusCodes.Status409Conflict, message));
        updates.MapPost("/cancel", (Updates.UpdateService service) =>
        {
            service.CancelCountdown();
            return Results.Ok(service.GetStatus());
        });

        api.MapGet("/logs", (int? lines, WatchTowerPaths paths) =>
            Results.Ok(LogTail.Read(paths.LogsDirectory, Math.Clamp(lines ?? 300, 10, 2000))));
    }

    private static Task SignInAsync(HttpContext ctx, AdminCredentials credentials) =>
        ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            UiHosting.CreatePrincipal(credentials.Stamp),
            new AuthenticationProperties { IsPersistent = false });

    private static IResult Error(int status, string message) =>
        Results.Json(new { error = message }, statusCode: status);
}

/// <summary>Reads the tail of the newest WatchTower log file without locking it.</summary>
public static class LogTail
{
    public sealed record Result(string? File, IReadOnlyList<string> Lines);

    public static Result Read(string logsDirectory, int lineCount)
    {
        if (!Directory.Exists(logsDirectory))
            return new Result(null, []);

        var latest = new DirectoryInfo(logsDirectory)
            .GetFiles("watchtower-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest is null)
            return new Result(null, []);

        using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        // Use the open stream's length: FileInfo.Length comes from directory metadata, which
        // Windows may report stale (even 0) while the logger holds the file open.
        var length = stream.Length;
        // Read at most ~512 bytes per requested line from the end of the file.
        var maxBytes = Math.Min(length, (long)lineCount * 512);
        stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (maxBytes < length && lines.Count > 0)
            lines.RemoveAt(0); // first line is probably partial
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return new Result(latest.Name, lines.TakeLast(lineCount).ToList());
    }
}