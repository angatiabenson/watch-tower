using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Services;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Services.Endpoints;
using WatchTower.Tests.Alerting;
using WatchTower.Tests.Hosting;
using WatchTower.Workers;

namespace WatchTower.Tests.Endpoints;

public class EndpointMonitorWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Mock<IEndpointProbe> _probe = new();
    private readonly Mock<IMetricsStore> _store = new();
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly TestOptionsMonitor<EndpointMonitorOptions> _options = new(new EndpointMonitorOptions
    {
        Endpoints =
        [
            new EndpointCheckOptions { Name = "site", Target = "https://example.com", IntervalSeconds = 60, FailuresBeforeAlert = 2 },
        ],
    });

    private EndpointMonitorWorker CreateWorker() => new(
        _probe.Object, _store.Object, TestAlerts.Create(_store.Object, _time, _dispatcher), _options, _time,
        NullLogger<EndpointMonitorWorker>.Instance);

    private void ProbeReturns(EndpointState state, string detail = "HTTP 200", double? daysLeft = null) =>
        _probe.Setup(p => p.ProbeAsync(It.IsAny<EndpointCheck>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EndpointProbeResult(state, detail, 12.3, daysLeft));

    private async Task PollAfter(EndpointMonitorWorker worker, int seconds)
    {
        _time.Advance(TimeSpan.FromSeconds(seconds));
        await worker.PollOnceAsync(Ct);
    }

    [Fact]
    public async Task ChecksRunOnTheirOwnInterval_AndStatusIsRecorded()
    {
        ProbeReturns(EndpointState.Up);
        var worker = CreateWorker();

        await worker.PollOnceAsync(Ct);
        await PollAfter(worker, 30);
        await PollAfter(worker, 30);

        _probe.Verify(p => p.ProbeAsync(It.IsAny<EndpointCheck>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _store.Verify(s => s.AddEndpointStatus(It.Is<EndpointStatus>(e =>
            e.Name == "site" && e.Type == "Http" && e.Target == "https://example.com/" && e.Status == "Up" && e.IsUp &&
            e.ResponseTimeMs == 12.3)), Times.Exactly(2));
    }

    [Fact]
    public async Task DownAlertsAfterConsecutiveFailures_AndRecoveryResolves()
    {
        ProbeReturns(EndpointState.Down, "Unexpected HTTP 503");
        var worker = CreateWorker();

        await worker.PollOnceAsync(Ct);
        _dispatcher.Sent.Should().BeEmpty("one failure is below FailuresBeforeAlert");
        await PollAfter(worker, 60);
        await PollAfter(worker, 60);

        var opened = _dispatcher.Sent.Should().ContainSingle().Subject;
        opened.AlertKey.Should().Be("endpoint:site");
        opened.Severity.Should().Be(AlertSeverity.Critical);
        opened.Title.Should().Be("Endpoint Down: site");
        _store.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.EndpointDown && i.Description.Contains("503"))), Times.Once);

        ProbeReturns(EndpointState.Up);
        await PollAfter(worker, 60);

        _dispatcher.Sent[^1].Kind.Should().Be(AlertNotificationKind.Resolved);
        _store.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.EndpointRecovered)), Times.Once);
    }

    [Fact]
    public async Task SingleFailureBetweenSuccesses_DoesNotAlert()
    {
        var worker = CreateWorker();
        foreach (var state in new[] { EndpointState.Down, EndpointState.Up, EndpointState.Down, EndpointState.Up })
        {
            ProbeReturns(state);
            await PollAfter(worker, 60);
        }

        _dispatcher.Sent.Should().BeEmpty();
        _store.Verify(s => s.AddIncident(It.IsAny<Incident>()), Times.Never);
    }

    [Fact]
    public async Task ExpiringCertificate_WarnsImmediately_AndDownToExpiringReplacesTheCriticalAlert()
    {
        _options.CurrentValue = new EndpointMonitorOptions
        {
            Endpoints = [new EndpointCheckOptions { Name = "tls", Type = "Certificate", Target = "example.com", FailuresBeforeAlert = 1 }],
        };
        var worker = CreateWorker();

        ProbeReturns(EndpointState.Down, "Certificate expired");
        await PollAfter(worker, 60);
        ProbeReturns(EndpointState.Expiring, "Certificate expires in 5 days", daysLeft: 5);
        await PollAfter(worker, 60);
        await PollAfter(worker, 60);

        _dispatcher.Sent.Select(n => (n.Kind, n.Severity)).Should().Equal(
            (AlertNotificationKind.Opened, AlertSeverity.Critical),
            (AlertNotificationKind.Resolved, AlertSeverity.Critical),
            (AlertNotificationKind.Opened, AlertSeverity.Warning));
        _store.Verify(s => s.AddIncident(It.Is<Incident>(i => i.Type == IncidentType.CertificateExpiring)), Times.Once);
        _store.Verify(s => s.AddEndpointStatus(It.Is<EndpointStatus>(e => e.Status == "Expiring" && e.IsUp && e.CertificateDaysLeft == 5)),
            Times.Exactly(2));
    }

    [Fact]
    public async Task InvalidEntriesAreSkipped_ValidOnesStillRun()
    {
        _options.CurrentValue = new EndpointMonitorOptions
        {
            Endpoints =
            [
                new EndpointCheckOptions { Name = "bad", Type = "Ping", Target = "x" },
                new EndpointCheckOptions { Name = "db", Type = "Tcp", Target = "db01:1433" },
            ],
        };
        ProbeReturns(EndpointState.Up, "Connected");

        await CreateWorker().PollOnceAsync(Ct);

        _probe.Verify(p => p.ProbeAsync(It.Is<EndpointCheck>(c => c.Name == "db"), It.IsAny<CancellationToken>()), Times.Once);
        _probe.VerifyNoOtherCalls();
    }

    [Fact]
    public void IncidentTypeValues_AreAppendOnly()
    {
        ((int)IncidentType.EndpointDown).Should().Be(13);
        ((int)IncidentType.EndpointRecovered).Should().Be(14);
        ((int)IncidentType.CertificateExpiring).Should().Be(15);
    }

    [Fact]
    public void SqliteStore_KeepsLatestEndpointStatus_AndPurgeClearsIt()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var store = new SqliteMetricsStore(dir.Paths.DatabasePath);
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        store.AddEndpointStatus(new EndpointStatus(t, "tls", "Certificate", "example.com:443", "Up", true, 50, 80.5, "ok"));
        store.AddEndpointStatus(new EndpointStatus(t.AddMinutes(1), "tls", "Certificate", "example.com:443", "Expiring", true, null, 20, null));

        store.GetLatestEndpointStatuses().Should().ContainSingle().Which.Should().Be(
            new EndpointStatus(t.AddMinutes(1), "tls", "Certificate", "example.com:443", "Expiring", true, null, 20, null));

        store.PurgeReportedData(0, 0, t);
        store.GetLatestEndpointStatuses().Should().BeEmpty();
    }

    [Fact]
    public void MetricsBatch_EndpointStatusesAreOptionalOnTheWire()
    {
        var json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        var without = new MetricsBatchDto("a", "b", "c", [], [], [], [], [], [], []);
        var with = without with
        {
            EndpointStatuses = [new EndpointStatusDto("t", "site", "Http", "https://example.com/", "Down", false, null, null, "HTTP 503")],
        };

        JsonSerializer.Serialize(without, json).Should().NotContain("endpointStatuses");
        var element = JsonDocument.Parse(JsonSerializer.Serialize(with, json)).RootElement.GetProperty("endpointStatuses")[0];
        element.GetProperty("name").GetString().Should().Be("site");
        element.GetProperty("isUp").GetBoolean().Should().BeFalse();
        element.TryGetProperty("certificateDaysLeft", out _).Should().BeFalse();

        var proto = GrpcPortalClient.ToProto(with.EndpointStatuses[0]);
        proto.Status.Should().Be("Down");
        proto.HasResponseTimeMs.Should().BeFalse();
        proto.Detail.Should().Be("HTTP 503");
    }
}