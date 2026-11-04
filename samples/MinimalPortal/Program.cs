// MinimalPortal entry point: hosting, limits and the first agent's enrollment code.
// The agent contract itself lives in PortalApi.cs.

using System.Text.Json.Serialization;

namespace WatchTower.Samples.MinimalPortal;

public static class Program
{
    public static void Main(string[] args)
    {
        var app = MinimalPortalApp.Build(args);
        var state = app.Services.GetRequiredService<PortalState>();

        // One agent is created at start-up so you can enroll a server right away. More can be
        // created with POST /admin/agents (see README.md).
        var agentId = app.Configuration["AgentId"] ?? "agent-01";
        var first = state.CreateAgent(agentId);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            Console.WriteLine();
            Console.WriteLine($"MinimalPortal is listening on {string.Join(", ", app.Urls)}");
            Console.WriteLine($"Public URL (in enrollment codes): {state.Options.PublicUrl}");
            Console.WriteLine($"Admin key (header X-Admin-Key):   {state.Options.AdminKey}");
            Console.WriteLine();
            Console.WriteLine($"Enrollment code for agent '{first.AgentId}':");
            Console.WriteLine($"  {first.EnrollmentCode}");
            Console.WriteLine();
            Console.WriteLine("On the Windows server (elevated):");
            Console.WriteLine($"  WatchTower.exe enroll {first.EnrollmentCode}");
            Console.WriteLine("Or, without Windows, with the simulator:");
            Console.WriteLine($"  dotnet run --project samples/AgentSimulator -- --code {first.EnrollmentCode}");
            Console.WriteLine();
        });

        app.Run();
    }
}

public static class MinimalPortalApp
{
    /// <summary>Request body limit (Kestrel), and by default the limit after decompression.</summary>
    public const long MaxRequestBodyBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Builds the portal. Tests pass <paramref name="configure"/> to swap Kestrel for an in-memory
    /// TestServer; everything else is identical to what <c>dotnet run</c> serves.
    /// </summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Default address when neither --urls nor ASPNETCORE_URLS is given.
        if (string.IsNullOrEmpty(builder.Configuration["urls"]))
            builder.WebHost.UseUrls("http://localhost:5080");

        // A default batch (500 CPU + 500 RAM samples plus snapshots) is well under 1 MB; the
        // agent's Outbox:MaxBatchSize can be raised by operators, so leave generous headroom.
        // A 413 is a failure to the agent (a compressed request is resent uncompressed first): it
        // retries and finally drops the batch.
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxRequestBodyBytes);

        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

        // Optional path prefix, e.g. /watchtower: agents then use BaseUrl https://host/watchtower and
        // call https://host/watchtower/api/v1/agent/... (the agent appends the API path to BaseUrl).
        var pathBase = builder.Configuration["PathBase"]?.TrimEnd('/');

        var options = new PortalOptions();
        var urls = builder.Configuration["urls"] ?? "http://localhost:5080";
        options.PublicUrl = builder.Configuration["PublicUrl"] ?? urls.Split(';')[0].TrimEnd('/') + pathBase;
        if (builder.Configuration["AdminKey"] is { Length: > 0 } adminKey)
            options.AdminKey = adminKey;
        // AcceptCompressedRequests=false: refuse compressed requests with 415, as a portal without
        // support does (to try the agent's fallback).
        if (builder.Configuration["AcceptCompressedRequests"] is { Length: > 0 } acceptCompressed)
            options.AcceptCompressedRequests = bool.Parse(acceptCompressed);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<PortalState>();

        configure?.Invoke(builder);

        var app = builder.Build();
        if (!string.IsNullOrEmpty(pathBase))
        {
            // UsePathBase alone also serves every route without the prefix. Refuse those, so an
            // agent whose BaseUrl lacks the prefix fails here as it would behind a real proxy.
            app.Use(async (context, next) =>
            {
                if (!context.Request.Path.StartsWithSegments(pathBase))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next(context);
            });
            app.UsePathBase(pathBase);
            app.UseRouting(); // after UsePathBase, so routes match below the prefix
        }
        app.UseRequestDecompression();
        app.MapGet("/", () => Results.Text(
            "WatchTower MinimalPortal. Agent API under /api/v1/agent/, admin API under /admin/ (see README.md)."));
        app.MapAgentApi();
        app.MapAdminApi();
        return app;
    }
}