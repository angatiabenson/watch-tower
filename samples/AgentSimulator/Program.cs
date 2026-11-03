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
          --scenario <name>          smoke (default) | run | alerts | duplicate | outage | portal-down
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
          --verbose                  Log every HTTP request.

        Scenarios:
          smoke        Conformance check of every endpoint; exit code 0 when everything passed.
          run          Behave like an agent: metrics, commands, config and occasional incidents.
          alerts       Send one example of every incident type and alert lifecycle, then exit.
          duplicate    Send batches, events and results more than once and out of order.
          outage       Replay the backlog an agent builds while the portal was unreachable.
          portal-down  Like run, but reports the outbox; stop and start the portal meanwhile.
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

    public async Task<int> SmokeAsync(CancellationToken ct)
    {
        var checks = new List<(string Name, bool Ok, string Note)>();
        void Check(string name, bool ok, string note = "") => checks.Add((name, ok, note));

        agent.Sample(Now.AddSeconds(-5));
        agent.Sample(Now);
        var batch = agent.BuildBatch(Now);
        Check("POST metrics/batch accepted (2xx)", await portal.SendMetricsBatchAsync(batch, ct));
        Check("Same batchId again answered 2xx (deduplicate, do not reject)", await portal.SendMetricsBatchAsync(batch, ct));

        var legacy = batch with { BatchId = null!, Agent = null, EndpointStatuses = null };
        Check("Batch from a 1.0.x agent (no batchId, agent, endpointStatuses) accepted",
            await portal.SendMetricsBatchAsync(legacy, ct));

        Check("Unknown JSON properties ignored (forward compatibility)", await PostWithExtraFieldAsync(batch with { BatchId = Guid.NewGuid().ToString() }, ct));

        var opened = new AgentEventDto(Guid.NewGuid().ToString(), SimulatedAgent.Stamp(Now), (int)IncidentType.CpuThresholdBreached,
            "CPU at 99.6% for over 300s", agent.TopProcesses(), "cpu", false);
        Check("POST events accepted (alert opened)", await portal.SendEventAsync(opened, ct));
        Check("Same eventId again answered 2xx", await portal.SendEventAsync(opened, ct));
        Check("Alert resolved event accepted", await portal.SendEventAsync(new AgentEventDto(Guid.NewGuid().ToString(),
            SimulatedAgent.Stamp(Now.AddSeconds(1)), (int)IncidentType.CpuThresholdBreached,
            "Resolved after 0s: CPU at 99.6% for over 300s", null, "cpu", true), ct));
        Check("One-shot event accepted (alertKey reboot, no resolved)", await portal.SendEventAsync(new AgentEventDto(
            Guid.NewGuid().ToString(), SimulatedAgent.Stamp(Now), (int)IncidentType.ServerRestarted,
            "The server restarted at 2026-11-03 05:58 UTC. WatchTower was stopped cleanly beforehand (orderly shutdown or reboot).",
            null, "reboot", null), ct));
        Check("Incident without alert accepted (no alertKey)", await portal.SendEventAsync(new AgentEventDto(
            Guid.NewGuid().ToString(), SimulatedAgent.Stamp(Now), (int)IncidentType.CpuSpikeDetected,
            "CPU spike detected: 97.2%", agent.TopProcesses(), null, null), ct));

        var count = await agent.PollCommandsAsync(ct);
        Check("GET commands/pending answered (200 array or 204)", true, $"{count} command(s) received and handled");
        Check("Command results accepted", agent.UnsentResults == 0, agent.UnsentResults == 0 ? "" : $"{agent.UnsentResults} result(s) not accepted");

        var applied = await agent.SyncConfigAsync(ct);
        Check("GET config answered (200 or 204)", true, applied ? $"applied version {agent.ConfigVersion}" : "no new configuration");
        if (applied)
            Check("POST config/acknowledge accepted", agent.UnacknowledgedConfigVersion is null);
        if (agent.ConfigVersion is not null)
        {
            var (again, _, _) = await portal.FetchConfigurationAsync(agent.ConfigVersion, ct);
            Check("GET config?version=<current> answered 204", !again, again ? "the same version was sent again (harmless, but rewrites the agent's config file every poll)" : "");
        }

        Check("Wrong token rejected", !await SendWithWrongTokenAsync(batch, ct));

        Console.WriteLine();
        foreach (var (name, ok, note) in checks)
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(note.Length > 0 ? $" - {note}" : "")}");
        var failed = checks.Count(c => !c.Ok);
        Console.WriteLine(failed == 0 ? "\nAll checks passed." : $"\n{failed} check(s) failed.");
        return failed == 0 ? 0 : 1;
    }

    private async Task<bool> PostWithExtraFieldAsync(MetricsBatchDto batch, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToNode(batch, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        })!.AsObject();
        json["fieldFromTheFuture"] = new JsonObject { ["value"] = 42 };
        json["agent"]!["capabilityFromTheFuture"] = true;
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient("PortalClient");
        using var response = await http.PostAsJsonAsync($"{options.BaseUrl.TrimEnd('/')}/api/v1/agent/metrics/batch", json, ct);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> SendWithWrongTokenAsync(MetricsBatchDto batch, CancellationToken ct)
    {
        var wrong = new PortalOptions
        {
            Enabled = true,
            BaseUrl = options.BaseUrl,
            AgentId = options.AgentId,
            AgentToken = "not-the-right-token",
            Tls = options.Tls,
        };
        await using var other = Program.BuildServices(wrong, verbose: false);
        return await other.GetRequiredService<IPortalClient>()
            .SendMetricsBatchAsync(batch with { BatchId = Guid.NewGuid().ToString() }, ct);
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

        agent.EnqueueEvent(IncidentType.CpuSpikeDetected, "CPU spike detected: 97.2%", At(0), topProcesses: agent.TopProcesses());
        agent.EnqueueEvent(IncidentType.CpuThresholdBreached, "CPU at 99.6% for over 300s", At(1), "cpu", false, agent.TopProcesses());
        agent.EnqueueEvent(IncidentType.CpuThresholdBreached, "Resolved after 4 min: CPU at 99.6% for over 300s", At(5), "cpu", true);
        agent.EnqueueEvent(IncidentType.RamSpikeDetected, "RAM spike detected: 95.0%", At(2), topProcesses: agent.TopProcesses());
        agent.EnqueueEvent(IncidentType.RamThresholdBreached, "RAM at 93.4% for over 300s", At(3), "ram", false, agent.TopProcesses());
        agent.EnqueueEvent(IncidentType.RamThresholdBreached, "Resolved after 6 min: RAM at 93.4% for over 300s", At(9), "ram", true);
        agent.EnqueueEvent(IncidentType.DiskCritical, @"Drive C:\ is 91.2% full (11.1 GB free)", At(4), @"disk:C:\", false);
        agent.EnqueueEvent(IncidentType.DiskQueueHigh, "Disk queue on 0 C: is 4.2", At(6), "diskqueue:0 C:", false);
        agent.EnqueueEvent(IncidentType.DiskQueueHigh, "Resolved after 2 min: Disk queue on 0 C: is 4.2", At(8), "diskqueue:0 C:", true);
        agent.EnqueueEvent(IncidentType.NetworkQueueSaturated, "Output queue on Ethernet0 is 12", At(7), "net:Ethernet0", false);
        agent.EnqueueEvent(IncidentType.NetworkQueueSaturated,
            "No longer monitored (network adapter removed); closed after 3 min. Last state: Output queue on Ethernet0 is 12",
            At(10), "net:Ethernet0", true);
        agent.EnqueueEvent(IncidentType.ServiceDown, "Service 'Print Spooler' (Spooler) is Stopped", At(11));
        agent.EnqueueEvent(IncidentType.ServiceRestartFailed, "Service 'Spooler' failed to restart after 3 attempts", At(12), "svc:Spooler", false);
        agent.EnqueueEvent(IncidentType.ServiceRestarted, "Service 'Print Spooler' (Spooler) was found stopped and restarted successfully",
            At(14), "svc:Spooler", true);
        agent.EnqueueEvent(IncidentType.ApplicationPoolDown, "Application pool 'DefaultAppPool' is Stopped", At(15));
        agent.EnqueueEvent(IncidentType.ApplicationPoolStartFailed, "Application pool 'DefaultAppPool' failed to start after 3 attempts",
            At(16), "pool:DefaultAppPool", false);
        agent.EnqueueEvent(IncidentType.ApplicationPoolStarted, "Application pool 'DefaultAppPool' was found stopped and started successfully",
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
        agent.EnqueueEvent(IncidentType.DiskCritical, @"Resolved after 20 min: Drive C:\ is 91.2% full (11.1 GB free)", At(24), @"disk:C:\", true);

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

/// <summary>Occasional realistic incidents for the run scenario: a CPU alert and a service outage.</summary>
internal sealed class IncidentScript(SimulatedAgent agent)
{
    private int _cpuStep = -1, _serviceStep = -1;
    private DateTime _cpuOpenedAt;

    public void Step(DateTime now, bool start)
    {
        if (_cpuStep < 0 && _serviceStep < 0 && start)
        {
            if (Random.Shared.Next(2) == 0)
                _cpuStep = 0;
            else
                _serviceStep = 0;
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