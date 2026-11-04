using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Alerting;
using WatchTower.Workers;

namespace WatchTower.Tests.Workers;

public class RebootDetectionWorkerTests
{
    private readonly InMemoryAlertStateStore _state = new();
    private readonly Mock<IMetricsStore> _metrics = new();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-03-01T10:00:00Z"));
    private DateTimeOffset _boot = DateTimeOffset.Parse("2026-03-01T09:00:00Z");

    private RebootDetectionWorker Create()
    {
        var bootTime = new Mock<IBootTimeProvider>();
        bootTime.Setup(b => b.GetBootTimeUtc()).Returns(() => _boot);
        return new RebootDetectionWorker(_state, TestAlerts.Create(_metrics.Object, _time, _dispatcher, state: _state),
            bootTime.Object, _time, NullLogger<RebootDetectionWorker>.Instance);
    }

    [Fact]
    public async Task FirstRun_OnlyRecordsTheBootTime()
    {
        await Create().CheckAsync(CancellationToken.None);

        _dispatcher.Sent.Should().BeEmpty();
        _state.GetValue("system.last_boot_utc").Should().NotBeNull();
    }

    [Fact]
    public async Task AgentRestartWithinTheSameBoot_IsNotAReboot()
    {
        await Create().CheckAsync(CancellationToken.None);
        _boot = _boot.AddSeconds(90); // tick/clock jitter between two agent starts

        await Create().CheckAsync(CancellationToken.None);

        _dispatcher.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task UncleanReboot_RaisesWarningOnce()
    {
        await Create().CheckAsync(CancellationToken.None); // agent ran on the previous boot, never stopped cleanly
        _boot = DateTimeOffset.Parse("2026-03-02T03:15:00Z");

        await Create().CheckAsync(CancellationToken.None);
        await Create().CheckAsync(CancellationToken.None); // agent restarted again on the same boot

        var notification = _dispatcher.Sent.Should().ContainSingle().Subject;
        notification.Kind.Should().Be(AlertNotificationKind.Event);
        notification.Severity.Should().Be(AlertSeverity.Warning);
        notification.Title.Should().Be("Server restarted unexpectedly");
        notification.Description.Should().Contain("2026-03-02 03:15 UTC");
        _metrics.Verify(m => m.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.ServerRestarted)), Times.Once);
    }

    [Fact]
    public async Task RebootAfterCleanStop_IsInfo()
    {
        var before = Create();
        await before.CheckAsync(CancellationToken.None);
        before.MarkCleanShutdown();
        _boot = _boot.AddDays(1);

        await Create().CheckAsync(CancellationToken.None);

        var notification = _dispatcher.Sent.Should().ContainSingle().Subject;
        notification.Severity.Should().Be(AlertSeverity.Info);
        notification.Title.Should().Be("Server restarted");
    }

    [Fact]
    public void IncidentTypeValue_IsAppendOnly() => ((int)IncidentType.ServerRestarted).Should().Be(16);
}