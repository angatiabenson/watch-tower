// AgentSimulator: drive a portal with realistic WatchTower agent traffic from any OS.
// Run `dotnet run --project samples/AgentSimulator -- --help` for the options.

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;

namespace WatchTower.Samples.AgentSimulator;

public static class Program
{
    private const string Usage = """
        WatchTower AgentSimulator: sends realistic agent traffic to a portal using the agent's own
        HTTPS client and wire contracts.

        Usage: AgentSimulator --code <WT1.enrollment code> [options]

          --code <code>              Enrollment code from the portal (or env WATCHTOWER_ENROLLMENT_CODE).
          --scenario <name>          smoke (default) | run | alerts | duplicate | outage | portal-down | endpoint-config | stale-drive
          --duration <seconds>       How long run / portal-down last (default 120 / 600; 0 = until Ctrl+C).
          --interval <seconds>       Outbox flush and command poll interval (agent default 30; here 10).
          --config-interval <sec>    Remote config poll interval (agent default 300; here 30).
          --backlog-minutes <n>      Length of the simulated network outage for 'outage' (default 120).
          --machine <name>           Machine name to report (default SIM-<agentId>).
          --version <v>              Agent version to report (default 0.0.0-sim).
          --available-version <v>    Pretend the update feed offers this version (for command 304).
          --services <a,b>           Locally watched services (default W3SVC,Spooler).
          --pools <a,b>              Locally watched application pools (default DefaultAppPool).
          --allow-portal-managed-targets  Let portal-pushed watch lists widen the command allow-list.
          --require-issued-at        Reject commands without issuedAt, like VisibilityPortal:RequireCommandIssuedAt.
          --verbose                  Log every HTTP request.

        Scenarios:
          smoke        Conformance check of every endpoint (real status codes). Queue a command and push
                       a configuration first, or those checks are skipped. Exit code 0 = all passed,
                       1 = a check failed, 3 = incomplete (nothing failed, but checks were skipped).
          run          Behave like an agent: metrics, commands, config and occasional incidents.
          alerts       Send one example of every incident type and alert lifecycle, then exit.
          duplicate    Send batches, events and results more than once and out of order.
          outage       Replay the backlog an agent builds while the portal was unreachable.
          portal-down  Like run, but reports the outbox; stop and start the portal meanwhile.
          endpoint-config  An endpoint check whose configuration becomes invalid (ConfigurationError),
                       a late re-delivery of an older batch, then the check fixed.
          stale-drive  A fixed drive that goes not ready: kept with its last reading, sent with
                       isStale true after 10 minutes, a late older batch, then the drive ready again.
        """;

    public static async Task<int> Main(string[] args)
    {
        var opts = Args.Parse(args);
        if (opts.Has("help") || opts.Has("h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var codeText = opts.Get("code") ?? Environment.GetEnvironmentVariable("WATCHTOWER_ENROLLMENT_CODE");
        // The agent's own parser: same validation and error messages as `WatchTower.exe enroll`.
        if (!EnrollmentCode.TryParse(codeText, out var code, out var codeError))
        {
            await Console.Error.WriteLineAsync($"error: {codeError}\n\n{Usage}");
            return 2;
        }

        var portalOptions = new PortalOptions
        {
            Enabled = true,
            Protocol = "Https",
            BaseUrl = code.Url,
            AgentId = code.AgentId,
            AgentToken = code.Token,
            Tls = new TlsOptions { RequireTls = code.RequireTls },
        };

        var settings = new SimulatorSettings
        {
            MachineName = opts.Get("machine") ?? $"SIM-{code.AgentId}".ToUpperInvariant(),
            Version = opts.Get("version") ?? "0.0.0-sim",
            AvailableVersion = opts.Get("available-version"),
            AllowPortalManagedTargets = opts.Has("allow-portal-managed-targets"),
            RequireCommandIssuedAt = opts.Has("require-issued-at"),
        };
        if (opts.Get("services") is { } svc)
            settings.WatchedServices = [.. svc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        if (opts.Get("pools") is { } pools)
            settings.WatchedPools = [.. pools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        await using var provider = BuildServices(portalOptions, opts.Has("verbose"));
        IPortalClient portal;
        try
        {
            portal = provider.GetRequiredService<IPortalClient>();
        }
        catch (InvalidOperationException ex)
        {
            // e.g. an http:// URL in a code with requireTls=true: the agent refuses to start too.
            await Console.Error.WriteLineAsync($"error: {ex.Message}");
            return 2;
        }

        var log = provider.GetRequiredService<ILoggerFactory>().CreateLogger("AgentSimulator");
        var agent = new SimulatedAgent(portal, portalOptions, settings, log, TimeProvider.System);
        var scenario = new Scenarios(agent, portal, provider, portalOptions, log)
        {
            Interval = TimeSpan.FromSeconds(opts.Int("interval", 10)),
            ConfigInterval = TimeSpan.FromSeconds(opts.Int("config-interval", 30)),
        };

        log.LogInformation("Agent '{AgentId}' -> {Url} (RequireTls={RequireTls}), scenario {Scenario}",
            code.AgentId, code.Url, code.RequireTls, opts.Get("scenario") ?? "smoke");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return (opts.Get("scenario") ?? "smoke") switch
            {
                "smoke" => await scenario.SmokeAsync(cts.Token),
                "run" => await scenario.RunAsync(TimeSpan.FromSeconds(opts.Int("duration", 120)), reportOutbox: false, cts.Token),
                "portal-down" => await scenario.RunAsync(TimeSpan.FromSeconds(opts.Int("duration", 600)), reportOutbox: true, cts.Token),
                "alerts" => await scenario.AlertsAsync(cts.Token),
                "duplicate" => await scenario.DuplicateAsync(cts.Token),
                "outage" => await scenario.OutageAsync(TimeSpan.FromMinutes(opts.Int("backlog-minutes", 120)), cts.Token),
                "endpoint-config" => await scenario.EndpointConfigurationErrorAsync(cts.Token),
                "stale-drive" => await scenario.StaleDriveAsync(cts.Token),
                var other => Fail($"unknown scenario '{other}'"),
            };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            log.LogInformation("Stopped; {Depth} message(s) left in the simulated outbox", agent.OutboxDepth);
            return 0;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}\n\n{Usage}");
        return 2;
    }

    /// <summary>The same HttpClient pipeline the agent registers in ServiceCollectionExtensions.AddPortal.</summary>
    internal static ServiceProvider BuildServices(PortalOptions portalOptions, bool verbose)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            })
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("System.Net.Http.HttpClient", verbose ? LogLevel.Information : LogLevel.Warning));
        services.AddSingleton<IOptionsMonitor<PortalOptions>>(new FixedOptionsMonitor<PortalOptions>(portalOptions));
        services.AddTransient<AgentAuthHandler>();
        services.AddHttpClient("PortalClient", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => PortalTls.CreateHandler(portalOptions.Tls))
            .AddHttpMessageHandler<AgentAuthHandler>();
        services.AddSingleton<IPortalClient, HttpsPortalClient>();
        return services.BuildServiceProvider();
    }

    private sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

/// <summary>The traffic patterns. Each returns the process exit code.</summary>
internal sealed class Scenarios(
    SimulatedAgent agent, IPortalClient portal, ServiceProvider services, PortalOptions options, ILogger log)
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ConfigInterval { get; init; } = TimeSpan.FromSeconds(30);

    private static DateTime Now => DateTime.UtcNow;

    // ── smoke ───────────────────────────────────────────────────────────────

    private enum Outcome { Pass, Fail, Skip }

    /// <summary>The agent's wire options (HttpsPortalClient): camelCase, nulls omitted, case-sensitive reads.</summary>
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record ConfigFetchResponse(RemoteAgentConfigDto? Config, string? Version);

    /// <summary>An HTTP exchange: the status code (0 when no response arrived) and the body or error.</summary>
    private sealed record Exchange(int Status, string Body)
    {
        public bool Is2xx => Status is >= 200 and < 300;

        public override string ToString() => Status == 0 ? $"no response ({Body})" : $"HTTP {Status}";
    }

    /// <summary>
    /// Conformance check of every operation. Unlike the agent's client (which only reports
    /// success or failure), every request here is sent through the same authenticated
    /// HttpClient pipeline and its real status code is checked. Operations that need data from
    /// the portal (a pending command, a pushed configuration) are reported as SKIP when there is
    /// none, and the run is then INCOMPLETE (exit code 3) instead of passing.
    /// </summary>
    public async Task<int> SmokeAsync(CancellationToken ct)
    {
        var checks = new List<(string Name, Outcome Outcome, string Note)>();
        void Check(string name, bool ok, string note) => checks.Add((name, ok ? Outcome.Pass : Outcome.Fail, note));
        void Skip(string name, string why) => checks.Add((name, Outcome.Skip, why));
        void Expect2xx(string name, Exchange e) => Check(name, e.Is2xx, e.ToString());

        // Telemetry
        agent.Sample(Now.AddSeconds(-5));
        agent.Sample(Now);
        var batch = agent.BuildBatch(Now);
        Expect2xx("POST metrics/batch accepted (2xx)", await PostAsync("metrics/batch", batch, ct));
        Expect2xx("Same batchId again answered 2xx (deduplicate, do not reject)", await PostAsync("metrics/batch", batch, ct));
        var legacy = batch with { BatchId = null!, Agent = null, EndpointStatuses = null };
        Expect2xx("Batch from a 1.0.18 agent (no batchId, agent, endpointStatuses) accepted", await PostAsync("metrics/batch", legacy, ct));
        var future = JsonSerializer.SerializeToNode(batch with { BatchId = Guid.NewGuid().ToString() }, WireJson)!.AsObject();
        future["fieldFromTheFuture"] = new JsonObject { ["value"] = 42 };
        future["agent"]!["capabilityFromTheFuture"] = true;
        future["serviceStatuses"]!.AsArray().Add(new JsonObject
        {
            ["timestamp"] = SimulatedAgent.Stamp(Now),
            ["serviceName"] = "FutureSvc",
            ["displayName"] = "Future service",
            ["status"] = "StatusFromTheFuture",
            ["isRunning"] = false,
        });
        Expect2xx("Unknown properties and enum values ignored (forward compatibility)", await PostAsync("metrics/batch", future, ct));
        var misconfigured = batch with
        {
            BatchId = Guid.NewGuid().ToString(),
            BatchedAt = SimulatedAgent.Stamp(Now.AddSeconds(1)),
            EndpointStatuses = [.. batch.EndpointStatuses!.Select(e =>
                SimulatedAgent.MarkConfigurationError(e, "Monitoring:Endpoints:0:Target must be an absolute http(s) URL."))],
        };
        Expect2xx("Endpoint status ConfigurationError accepted (same timestamp, later batch)", await PostAsync("metrics/batch", misconfigured, ct));

        var opened = new AgentEventDto(Guid.NewGuid().ToString(), SimulatedAgent.Stamp(Now), (int)IncidentType.CpuThresholdBreached,
            "CPU at 99.6% for over 300s", agent.TopProcesses(), "cpu", false);
        Expect2xx("POST events accepted (alert opened)", await PostAsync("events", opened, ct));
        Expect2xx("Same eventId again answered 2xx", await PostAsync("events", opened, ct));
        Expect2xx("Alert resolved event accepted", await PostAsync("events", new AgentEventDto(Guid.NewGuid().ToString(),
            SimulatedAgent.Stamp(Now.AddSeconds(1)), (int)IncidentType.CpuThresholdBreached,
            "Resolved after 0s: CPU at 99.6% for over 300s", null, "cpu", true), ct));
        Expect2xx("One-shot event accepted (alertKey reboot, no resolved)", await PostAsync("events", new AgentEventDto(
            Guid.NewGuid().ToString(), SimulatedAgent.Stamp(Now), (int)IncidentType.ServerRestarted,
            "The server restarted at 2026-11-03 05:58 UTC. WatchTower was stopped cleanly beforehand (orderly shutdown or reboot).",
            null, "reboot", null), ct));
        Expect2xx("Incident without alert accepted (no alertKey)", await PostAsync("events", new AgentEventDto(
            Guid.NewGuid().ToString(), SimulatedAgent.Stamp(Now), (int)IncidentType.CpuSpikeDetected,
            "CPU hit 97.2% (threshold: 95%)", agent.TopProcesses(), null, null), ct));
        Expect2xx("Unknown incident type accepted", await PostAsync("events", new AgentEventDto(
            Guid.NewGuid().ToString(), SimulatedAgent.Stamp(Now), 999, "An incident type from a later agent", null, null, null), ct));

        await SmokeCommandsAsync(Check, Skip, Expect2xx, ct);
        await SmokeConfigAsync(Check, Skip, Expect2xx, ct);

        var wrongToken = await PostAsync("metrics/batch", batch with { BatchId = Guid.NewGuid().ToString() }, ct, token: "not-the-right-token");
        Check("Wrong token answered exactly 401", wrongToken.Status == 401, wrongToken.ToString());

        Console.WriteLine();
        foreach (var (name, outcome, note) in checks)
            Console.WriteLine($"  [{outcome.ToString().ToUpperInvariant()}] {name}{(note.Length > 0 ? $" - {note}" : "")}");
        var failed = checks.Count(c => c.Outcome == Outcome.Fail);
        var skipped = checks.Count(c => c.Outcome == Outcome.Skip);
        if (failed > 0)
        {
            Console.WriteLine($"\nFAILED: {failed} check(s) failed{(skipped > 0 ? $", {skipped} skipped" : "")}.");
            return 1;
        }
        if (skipped > 0)
        {
            Console.WriteLine($"\nINCOMPLETE: no failures, but {skipped} check(s) were skipped (each SKIP names the fixture it needs).");
            return 3;
        }
        Console.WriteLine("\nAll checks passed.");
        return 0;
    }

    private async Task SmokeCommandsAsync(
        Action<string, bool, string> check, Action<string, string> skip, Action<string, Exchange> expect2xx, CancellationToken ct)
    {
        const string Fixture = "queue a command for this agent first (e.g. ForceMetricSnapshot, actionType 301) and run smoke again";
        var poll = await SendAsync(HttpMethod.Get, "commands/pending", null, ct);
        List<AgentCommandDto>? commands = null;
        var parseError = "";
        if (poll.Status == 200)
        {
            try
            {
                // Parsed like the agent does: the whole array at once, case-sensitive camelCase.
                commands = JsonSerializer.Deserialize<List<AgentCommandDto>>(poll.Body, WireJson) ?? [];
            }
            catch (JsonException ex)
            {
                parseError = ex.Message;
            }
        }
        else if (poll.Status == 204)
        {
            commands = [];
        }

        check("GET commands/pending answered 200 (JSON array) or 204", commands is not null,
            commands is null
                ? parseError.Length > 0 ? $"HTTP 200, but the agent cannot parse it: {parseError}" : poll.ToString()
                : $"{poll}, {commands.Count} command(s)");

        var command = commands?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.CommandId));
        if (commands is { Count: > 0 } && command is null)
            check("Pending commands have a commandId", false, "none has one (property names are case-sensitive camelCase)");
        if (command is null)
        {
            skip("POST commands/{id}/acknowledge accepted", Fixture);
            skip("POST commands/{id}/result accepted", Fixture);
            skip("Same result again answered 2xx", Fixture);
            return;
        }

        command = command.Parameters is null ? command with { Parameters = [] } : command;
        var fresh = agent.IsFresh(command, out var reason);
        if (string.IsNullOrWhiteSpace(command.IssuedAt))
            check("Pending command carries issuedAt", false, "accepted without it, but issuedAt SHOULD be sent (REQUIRED in v2)");
        else
            check("Pending command is fresh (issuedAt)", fresh, fresh ? command.IssuedAt : $"the agent refuses it: {reason}");

        var id = Uri.EscapeDataString(command.CommandId);
        expect2xx("POST commands/{id}/acknowledge accepted", await SendAsync(HttpMethod.Post, $"commands/{id}/acknowledge",
            new AcknowledgeCommandDto(command.CommandId, SimulatedAgent.Stamp(Now)), ct));

        var result = fresh
            ? agent.Run(command)
            : new CommandResultDto(command.CommandId, false, $"Rejected: {reason}", SimulatedAgent.Stamp(Now));
        expect2xx("POST commands/{id}/result accepted", await SendAsync(HttpMethod.Post, $"commands/{id}/result", result, ct));
        expect2xx("Same result again answered 2xx", await SendAsync(HttpMethod.Post, $"commands/{id}/result", result, ct));
    }

    private async Task SmokeConfigAsync(
        Action<string, bool, string> check, Action<string, string> skip, Action<string, Exchange> expect2xx, CancellationToken ct)
    {
        const string Fixture = "push a configuration for this agent first and run smoke again";
        var fetch = await SendAsync(HttpMethod.Get, "config", null, ct);
        ConfigFetchResponse? response = null;
        var problem = fetch.ToString();
        if (fetch.Status == 200)
        {
            try
            {
                response = JsonSerializer.Deserialize<ConfigFetchResponse>(fetch.Body, WireJson);
                if (response?.Config is null || string.IsNullOrEmpty(response.Version))
                {
                    problem = "HTTP 200 needs both config and version (the agent treats this as no change)";
                    response = null;
                }
            }
            catch (JsonException ex)
            {
                problem = $"HTTP 200, but the agent cannot parse it: {ex.Message}";
            }
        }

        check("GET config answered 200 ({config, version}) or 204", fetch.Status == 204 || response is not null,
            response is not null ? $"{fetch}, version {response.Version}" : problem);
        if (response is null)
        {
            skip("Configuration passes the agent's validation", Fixture);
            skip("POST config/acknowledge accepted", Fixture);
            skip("GET config?version=<current> answered 204", Fixture);
            return;
        }

        var valid = agent.TryApply(response.Config!, response.Version!, out var error);
        check("Configuration passes the agent's validation", valid, valid ? "" : $"the agent would neither apply nor acknowledge it: {error}");
        if (!valid)
        {
            skip("POST config/acknowledge accepted", "the agent does not acknowledge a configuration it rejects");
            skip("GET config?version=<current> answered 204", "no valid configuration was applied");
            return;
        }

        var ack = await SendAsync(HttpMethod.Post, "config/acknowledge", new { version = response.Version }, ct);
        expect2xx("POST config/acknowledge accepted", ack);
        if (ack.Is2xx)
            agent.MarkConfigAcknowledged(response.Version!);

        var again = await SendAsync(HttpMethod.Get, $"config?version={Uri.EscapeDataString(response.Version!)}", null, ct);
        check("GET config?version=<current> answered 204", again.Status == 204,
            again.Status == 200 ? "HTTP 200: the same version again (works, but rewrites the agent's config file every poll)" : again.ToString());
    }

    private Task<Exchange> PostAsync(string path, object body, CancellationToken ct, string? token = null) =>
        SendAsync(HttpMethod.Post, path, body, ct, token);

    /// <summary>
    /// Sends one request to <c>{BaseUrl}/api/v1/agent/{path}</c> through the agent's HttpClient
    /// pipeline (auth headers, TLS policy, 30 s timeout), serialized like the agent does.
    /// </summary>
    private async Task<Exchange> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string? token = null)
    {
        ServiceProvider? other = null;
        try
        {
            var provider = services;
            if (token is not null)
            {
                other = Program.BuildServices(new PortalOptions
                {
                    Enabled = true,
                    BaseUrl = options.BaseUrl,
                    AgentId = options.AgentId,
                    AgentToken = token,
                    Tls = options.Tls,
                }, verbose: false);
                provider = other;
            }
            var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("PortalClient");
            using var request = new HttpRequestMessage(method, $"{options.BaseUrl.TrimEnd('/')}/api/v1/agent/{path}");
            if (body is not null)
                request.Content = JsonContent.Create(body, body.GetType(), options: WireJson);
            using var response = await http.SendAsync(request, ct);
            return new Exchange((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new Exchange(0, ex.Message);
        }
        finally
        {
            if (other is not null)
                await other.DisposeAsync();
        }
    }

    // ── run / portal-down ───────────────────────────────────────────────────

    public async Task<int> RunAsync(TimeSpan duration, bool reportOutbox, CancellationToken ct)
    {
        var end = duration == TimeSpan.Zero ? DateTime.MaxValue : Now + duration;
        var nextFlush = Now;
        var nextPoll = Now;
        var nextConfig = Now;
        var tick = 0;
        var incident = new IncidentScript(agent);

        while (Now < end)
        {
            ct.ThrowIfCancellationRequested();
            agent.Sample(Now);

            if (++tick % 6 == 0)
                incident.Step(Now, Random.Shared.NextDouble() < 0.25);

            if (Now >= nextFlush || agent.SnapshotRequested)
            {
                agent.SnapshotRequested = false;
                agent.EnqueueBatch(Now);
                var report = await agent.FlushAsync(ct);
                if (reportOutbox || report.Failed)
                    log.LogInformation("Outbox: sent {Sent}, dropped {Dropped}, queued {Queued}{Down}", report.Sent, report.Dropped,
                        report.Remaining, report.Failed ? " (portal unreachable or refusing)" : "");
                nextFlush = Now + agent.Interval(p => p.OutboxFlushIntervalSeconds, Interval);
            }

            if (Now >= nextPoll)
            {
                await agent.PollCommandsAsync(ct);
                nextPoll = Now + agent.Interval(p => p.CommandPollIntervalSeconds, Interval);
            }

            if (Now >= nextConfig || agent.ConfigSyncRequested)
            {
                agent.ConfigSyncRequested = false;
                await agent.SyncConfigAsync(ct);
                nextConfig = Now + agent.Interval(p => p.RemoteConfigPollIntervalSeconds, ConfigInterval);
            }

            if (agent.RestartRequested)
            {
                log.LogWarning("Simulating an agent restart (uptime resets, outbox and pending results survive)");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                agent.CompleteRestart();
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct); // the agent's CPU/RAM sample interval
        }

        log.LogInformation("Done; {Depth} message(s) left in the simulated outbox", agent.OutboxDepth);
        return 0;
    }

    // ── alerts ──────────────────────────────────────────────────────────────

    public async Task<int> AlertsAsync(CancellationToken ct)
    {
        var t = Now.AddMinutes(-30);
        DateTime At(int minute) => t.AddMinutes(minute);

        agent.EnqueueEvent(IncidentType.CpuSpikeDetected, "CPU hit 97.2% (threshold: 95%)", At(0), topProcesses: agent.TopProcesses());
        agent.EnqueueEvent(IncidentType.CpuThresholdBreached, "CPU at 99.6% for over 300s", At(1), "cpu", false, agent.TopProcesses());
        agent.EnqueueEvent(IncidentType.CpuThresholdBreached, "Resolved after 4 min: CPU at 99.6% for over 300s", At(5), "cpu", true);
        agent.EnqueueEvent(IncidentType.RamSpikeDetected, "RAM hit 95.0% (819 MB available, threshold: 90%)", At(2), topProcesses: agent.TopProcesses());
        agent.EnqueueEvent(IncidentType.RamThresholdBreached, "RAM at 93.4% (1081 MB available) for over 300s", At(3), "ram", false, agent.TopProcesses());
        agent.EnqueueEvent(IncidentType.RamThresholdBreached, "Resolved after 6 min: RAM at 93.4% (1081 MB available) for over 300s", At(9), "ram", true);
        agent.EnqueueEvent(IncidentType.DiskCritical, @"Drive C:\ at 91.2% (11.1 GB free of 126.4 GB)", At(4), @"disk:C:\", false);
        agent.EnqueueEvent(IncidentType.DiskQueueHigh, "Disk '0 C:' queue length at 4.2 (threshold: 2)", At(6), "diskqueue:0 C:", false);
        agent.EnqueueEvent(IncidentType.DiskQueueHigh, "Resolved after 2 min: Disk '0 C:' queue length at 4.2 (threshold: 2)", At(8), "diskqueue:0 C:", true);
        agent.EnqueueEvent(IncidentType.NetworkQueueSaturated, "Network interface 'Ethernet0' output queue at 12 (threshold: > 0)", At(7), "net:Ethernet0", false);
        agent.EnqueueEvent(IncidentType.NetworkQueueSaturated,
            "No longer monitored (Network adapter Ethernet0 is no longer present); closed after 3 min. Last state: Network interface 'Ethernet0' output queue at 12 (threshold: > 0)",
            At(10), "net:Ethernet0", true);
        agent.EnqueueEvent(IncidentType.ServiceDown, "Service 'Print Spooler' (Spooler) is Stopped", At(11));
        agent.EnqueueEvent(IncidentType.ServiceRestartFailed, "Service 'Spooler' failed to restart after 3 attempts", At(12), "svc:Spooler", false);
        agent.EnqueueEvent(IncidentType.ServiceRestarted, "Service 'Print Spooler' (Spooler) was found stopped and restarted successfully",
            At(14), "svc:Spooler", true);
        agent.EnqueueEvent(IncidentType.ApplicationPoolDown, "IIS application pool 'DefaultAppPool' is Stopped", At(15));
        agent.EnqueueEvent(IncidentType.ApplicationPoolStartFailed, "IIS application pool 'DefaultAppPool' failed to start after 3 attempts",
            At(16), "pool:DefaultAppPool", false);
        agent.EnqueueEvent(IncidentType.ApplicationPoolStarted, "IIS application pool 'DefaultAppPool' was found stopped and started successfully",
            At(17), "pool:DefaultAppPool", true);
        agent.EnqueueEvent(IncidentType.EndpointDown, "Endpoint 'shop-home' (Http https://shop.example.com/) is down: HTTP 503 (3 consecutive failed checks)",
            At(18), "endpoint:shop-home", false);
        agent.EnqueueEvent(IncidentType.EndpointRecovered, "Endpoint 'shop-home' (Http https://shop.example.com/) is OK again: HTTP 200 in 182 ms",
            At(20), "endpoint:shop-home", true);
        agent.EnqueueEvent(IncidentType.CertificateExpiring, "Endpoint 'api-cert' (api.example.com:443): certificate expires in 12.5 days",
            At(21), "endpoint:api-cert", false);
        agent.EnqueueEvent(IncidentType.ServerRestarted,
            "The server restarted at 2026-11-03 05:58 UTC. WatchTower was not stopped cleanly before it: possible crash, power loss or forced reset.",
            At(22), "reboot");
        agent.EnqueueEvent(IncidentType.DiskCritical, @"Resolved after 20 min: Drive C:\ at 91.2% (11.1 GB free of 126.4 GB)", At(24), @"disk:C:\", true);

        return await DrainAsync(ct) ? 0 : 1;
    }

    // ── duplicate ───────────────────────────────────────────────────────────

    public async Task<int> DuplicateAsync(CancellationToken ct)
    {
        agent.Sample(Now.AddSeconds(-10));
        agent.Sample(Now.AddSeconds(-5));
        var batch = agent.BuildBatch(Now);
        var ok = true;
        for (var i = 1; i <= 3; i++)
        {
            var accepted = await portal.SendMetricsBatchAsync(batch, ct);
            log.LogInformation("Batch {BatchId} delivery {N}: {Result}", batch.BatchId, i, accepted ? "2xx" : "FAILED");
            ok &= accepted;
        }

        var open = new AgentEventDto(Guid.NewGuid().ToString(), SimulatedAgent.Stamp(Now.AddMinutes(-2)), (int)IncidentType.EndpointDown,
            "Endpoint 'shop-home' (Http https://shop.example.com/) is down: timeout (3 consecutive failed checks)", null, "endpoint:shop-home", false);
        var close = new AgentEventDto(Guid.NewGuid().ToString(), SimulatedAgent.Stamp(Now.AddMinutes(-1)), (int)IncidentType.EndpointRecovered,
            "Endpoint 'shop-home' (Http https://shop.example.com/) is OK again: HTTP 200 in 140 ms", null, "endpoint:shop-home", true);

        // Out of order: the close arrives first (its open was still in backoff), then the open
        // twice. A correct portal ends with the alert CLOSED (compare timestamps, not arrival).
        foreach (var (evt, label) in new[] { (close, "close"), (open, "open"), (open, "open again") })
        {
            var accepted = await portal.SendEventAsync(evt, ct);
            log.LogInformation("Event {Label} {EventId}: {Result}", label, evt.EventId, accepted ? "2xx" : "FAILED");
            ok &= accepted;
        }

        log.LogInformation(ok
            ? "All duplicates were answered 2xx. Check the portal stored ONE batch, TWO events and that alert endpoint:shop-home is closed."
            : "Some duplicates were refused; the agent would keep retrying them.");
        return ok ? 0 : 1;
    }

    // ── endpoint-config ─────────────────────────────────────────────────────

    /// <summary>
    /// An endpoint check whose configuration becomes invalid: the agent stops running it and
    /// reports its last result with status ConfigurationError and an UNCHANGED timestamp. A late
    /// re-delivery of the older batch must not bring the old status back, and once fixed the
    /// check reports a new result at once.
    /// </summary>
    public async Task<int> EndpointConfigurationErrorAsync(CancellationToken ct)
    {
        var start = Now.AddMinutes(-3);
        var ok = true;

        async Task SendAsync(MetricsBatchDto batch, string label, string expect)
        {
            var accepted = await portal.SendMetricsBatchAsync(batch, ct);
            var row = batch.EndpointStatuses?.FirstOrDefault();
            log.LogInformation("{Label}: shop-home {Status} (timestamp {Timestamp}, isUp {IsUp}), batch {BatchId}: {Result}. Expected in the portal: {Expect}",
                label, row?.Status, row?.Timestamp, row?.IsUp, batch.BatchId, accepted ? "2xx" : "FAILED", expect);
            ok &= accepted;
        }

        agent.Sample(start);
        var before = agent.BuildBatch(start);
        await SendAsync(before, "1. Check runs", "Up");

        agent.SetEndpointConfigurationError("Monitoring:Endpoints:0:Target must be an absolute http(s) URL.");
        agent.Sample(start.AddMinutes(1));
        await SendAsync(agent.BuildBatch(start.AddMinutes(1)), "2. Configuration made invalid",
            "ConfigurationError with the error in its detail, shown as not checked (isUp is the LAST result's)");

        await SendAsync(before, "3. Batch 1 delivered again (late)", "still ConfigurationError: batch 1 is older (batchedAt)");

        agent.SetEndpointConfigurationError(null);
        agent.Sample(start.AddMinutes(2));
        await SendAsync(agent.BuildBatch(start.AddMinutes(2)), "4. Configuration fixed, check runs at once", "Up with a new timestamp");

        return ok ? 0 : 1;
    }

    // ── stale-drive ─────────────────────────────────────────────────────────

    /// <summary>
    /// A fixed drive that goes not ready: still listed, so it keeps its last reading and
    /// timestamp, and is sent with isStale true once that reading is older than its window
    /// (max(3 x 60 s, 10 min)). A late re-delivery of an earlier batch must not make it fresh
    /// again; when the drive is ready again it is read at once and is fresh.
    /// </summary>
    public async Task<int> StaleDriveAsync(CancellationToken ct)
    {
        var start = Now.AddMinutes(-20);
        var ok = true;

        async Task SendAsync(MetricsBatchDto batch, string label, string expect)
        {
            var accepted = await portal.SendMetricsBatchAsync(batch, ct);
            var d = batch.DiskMetrics.Single(m => m.DriveName == @"D:\");
            log.LogInformation("{Label}: D:\\ timestamp {Timestamp}, isStale {IsStale}, batch {BatchId}: {Result}. Expected in the portal: {Expect}",
                label, d.Timestamp, d.IsStale, batch.BatchId, accepted ? "2xx" : "FAILED", expect);
            ok &= accepted;
        }

        agent.Sample(start);
        await SendAsync(agent.BuildBatch(start), "1. Drive read", "fresh");

        agent.SetDriveDReady(false);
        agent.Sample(start.AddMinutes(5));
        var early = agent.BuildBatch(start.AddMinutes(5));
        await SendAsync(early, "2. Not ready for 5 min", "fresh: the same reading, still inside its 10 min window");

        agent.Sample(start.AddMinutes(15));
        await SendAsync(agent.BuildBatch(start.AddMinutes(15)), "3. Not ready for 15 min",
            "stale (no reading since step 1), with the last reading unchanged");

        await SendAsync(early, "4. Batch 2 delivered again (late)", "still stale: batch 2 is older (batchedAt)");

        agent.SetDriveDReady(true);
        agent.Sample(start.AddMinutes(16));
        await SendAsync(agent.BuildBatch(start.AddMinutes(16)), "5. Drive ready again", "fresh, with a new timestamp");

        return ok ? 0 : 1;
    }

    // ── outage ──────────────────────────────────────────────────────────────

    public async Task<int> OutageAsync(TimeSpan backlog, CancellationToken ct)
    {
        // What the real agent's outbox holds after the portal was unreachable for `backlog`:
        // one batch per flush interval (30 s) with the CPU/RAM samples of that interval
        // (every 5 s), plus the incidents that happened meanwhile, all with past timestamps.
        var start = Now - backlog;
        var batches = 0;
        for (var at = start; at < Now; at = at.AddSeconds(30))
        {
            for (var s = 0; s < 30; s += 5)
                agent.Sample(at.AddSeconds(s));
            agent.EnqueueBatch(at.AddSeconds(30));
            batches++;
            if (batches == 20)
                agent.EnqueueEvent(IncidentType.CpuThresholdBreached, "CPU at 99.1% for over 300s", at, "cpu", false, agent.TopProcesses());
            if (batches == 40)
                agent.EnqueueEvent(IncidentType.CpuThresholdBreached, "Resolved after 10 min: CPU at 99.1% for over 300s", at, "cpu", true);
        }
        log.LogInformation("Replaying {Batches} batches and 2 events covering {Minutes} minutes (events go first)",
            batches, (int)backlog.TotalMinutes);

        var started = Now;
        var ok = await DrainAsync(ct);
        log.LogInformation("Backlog delivered in {Seconds:0.0}s", (Now - started).TotalSeconds);
        return ok ? 0 : 1;
    }

    /// <summary>Flushes until the outbox is empty, waiting between cycles like the agent.</summary>
    private async Task<bool> DrainAsync(CancellationToken ct)
    {
        var failures = 0;
        while (agent.OutboxDepth > 0)
        {
            var report = await agent.FlushAsync(ct);
            log.LogInformation("Outbox: sent {Sent}, dropped {Dropped}, queued {Queued}", report.Sent, report.Dropped, report.Remaining);
            if (report.Failed && ++failures >= 10)
            {
                log.LogError("The portal kept refusing messages; giving up with {Depth} queued", agent.OutboxDepth);
                return false;
            }
            if (agent.OutboxDepth > 0)
                await Task.Delay(report.Failed ? Interval : TimeSpan.FromMilliseconds(200), ct);
        }
        return true;
    }
}

/// <summary>
/// Occasional realistic incidents for the run scenario: a CPU alert, a service outage, or an
/// endpoint check whose configuration is invalid for a while (ConfigurationError).
/// </summary>
internal sealed class IncidentScript(SimulatedAgent agent)
{
    private int _cpuStep = -1, _serviceStep = -1, _endpointStep = -1;
    private DateTime _cpuOpenedAt;

    public void Step(DateTime now, bool start)
    {
        if (_cpuStep < 0 && _serviceStep < 0 && _endpointStep < 0 && start)
        {
            switch (Random.Shared.Next(3))
            {
                case 0: _cpuStep = 0; break;
                case 1: _serviceStep = 0; break;
                default: _endpointStep = 0; break;
            }
        }

        switch (_endpointStep)
        {
            case 0:
                agent.SetEndpointConfigurationError("Monitoring:Endpoints:0:IntervalSeconds must be between 5 and 86400.");
                _endpointStep = 1;
                break;
            case >= 1 and < 3:
                _endpointStep++;
                break;
            case 3:
                agent.SetEndpointConfigurationError(null);
                _endpointStep = -1;
                break;
        }

        switch (_cpuStep)
        {
            case 0:
                agent.CpuLevel = 99.6;
                agent.EnqueueEvent(IncidentType.CpuThresholdBreached, "CPU at 99.6% for over 300s", now, "cpu", false, agent.TopProcesses());
                _cpuOpenedAt = now;
                _cpuStep = 1;
                break;
            case >= 1 and < 3:
                agent.CpuLevel = 99.6;
                _cpuStep++;
                break;
            case 3:
                agent.CpuLevel = 20;
                var minutes = (int)(now - _cpuOpenedAt).TotalMinutes;
                agent.EnqueueEvent(IncidentType.CpuThresholdBreached,
                    $"Resolved after {(minutes < 1 ? $"{(int)(now - _cpuOpenedAt).TotalSeconds}s" : $"{minutes} min")}: CPU at 99.6% for over 300s",
                    now, "cpu", true);
                _cpuStep = -1;
                break;
        }

        switch (_serviceStep)
        {
            case 0:
                agent.SetService("Spooler", running: false);
                agent.EnqueueEvent(IncidentType.ServiceDown, "Service 'Print Spooler' (Spooler) is Stopped", now);
                _serviceStep = 1;
                break;
            case 1:
                agent.EnqueueEvent(IncidentType.ServiceRestartFailed, "Service 'Spooler' failed to restart after 3 attempts", now, "svc:Spooler", false);
                _serviceStep = 2;
                break;
            case 2:
                agent.SetService("Spooler", running: true);
                agent.EnqueueEvent(IncidentType.ServiceRestarted,
                    "Service 'Print Spooler' (Spooler) was found stopped and restarted successfully", now, "svc:Spooler", true);
                _serviceStep = -1;
                break;
        }
    }
}

/// <summary>Minimal "--name value" / "--flag" parser.</summary>
internal sealed class Args
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] args)
    {
        var parsed = new Args();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith('-'))
                continue;
            var name = args[i].TrimStart('-');
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            parsed._values[name] = value;
        }
        return parsed;
    }

    public bool Has(string name) => _values.ContainsKey(name);
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public int Int(string name, int fallback) =>
        int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}