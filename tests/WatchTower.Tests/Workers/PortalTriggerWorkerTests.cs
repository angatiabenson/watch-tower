using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Storage;
using WatchTower.Services.Abstractions;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

/// <summary>
/// ForceMetricSnapshot / ForceConfigSync must wake their workers immediately rather than being
/// ignored (snapshot) or honoured only after a full poll interval (config sync).
/// </summary>
public class PortalTriggerWorkerTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly Mock<IPortalClient> _portalClient = new();
    private readonly Mock<IOptionsMonitor<PortalOptions>> _options = new();

    public PortalTriggerWorkerTests()
    {
        _options.Setup(o => o.CurrentValue).Returns(new PortalOptions
        {
            Enabled = true,
            Outbox = new OutboxOptions { FlushIntervalSeconds = 3600 },
            RemoteConfig = new RemoteConfigOptions { Enabled = true, PollIntervalSeconds = 3600 },
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task OutboxDispatch_SnapshotTrigger_RunsCycleWithoutWaitingForInterval()
    {
        var cycles = 0;
        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(o => o.GetPendingMessagesAsync(It.IsAny<int>()))
            .Callback(() => Interlocked.Increment(ref cycles))
            .ReturnsAsync(Array.Empty<OutboxMessage>());
        var metrics = new Mock<IMetricsStore>();
        metrics.Setup(m => m.GetCpuMetricsSince(It.IsAny<long>(), It.IsAny<int>()))
            .Returns((0L, (IReadOnlyList<CpuMetric>)Array.Empty<CpuMetric>()));
        metrics.Setup(m => m.GetRamMetricsSince(It.IsAny<long>(), It.IsAny<int>()))
            .Returns((0L, (IReadOnlyList<RamMetric>)Array.Empty<RamMetric>()));
        var trigger = new MetricSnapshotTrigger();

        using var worker = new OutboxDispatchWorker(
            _portalClient.Object, outbox.Object, Mock.Of<IPortalSyncState>(), metrics.Object,
            trigger, _options.Object, _time, NullLogger<OutboxDispatchWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref cycles) == 1);

            trigger.Signal();

            // No fake time has passed: only the trigger can have caused the second cycle.
            await WaitUntilAsync(() => Volatile.Read(ref cycles) == 2);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        cycles.Should().Be(2);
    }

    [Fact]
    public async Task RemoteConfig_SyncTrigger_FetchesWithoutWaitingForInterval()
    {
        var fetches = 0;
        _portalClient.Setup(c => c.FetchConfigurationAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref fetches))
            .ReturnsAsync((false, (RemoteAgentConfigDto?)null, (string?)null));
        var store = new Mock<IRemoteConfigStore>();
        store.Setup(s => s.GetUnacknowledgedAsync()).ReturnsAsync(((RemoteAgentConfigDto?)null, (string?)null));
        var trigger = new ConfigSyncTrigger();
        var paths = new WatchTowerPaths(AppContext.BaseDirectory,
            Path.Combine(Path.GetTempPath(), "wt-tests", Guid.NewGuid().ToString("N")));

        using var worker = new RemoteConfigWorker(
            _portalClient.Object, store.Object, paths, trigger, _options.Object, _time,
            NullLogger<RemoteConfigWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref fetches) == 1);

            trigger.Signal();

            await WaitUntilAsync(() => Volatile.Read(ref fetches) == 2);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        fetches.Should().Be(2);
    }
}