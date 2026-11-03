using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using WatchTower.Configuration;
using WatchTower.Models;
using WatchTower.Services.Abstractions;
using WatchTower.Services.Alerting;
using WatchTower.Templates;

namespace WatchTower.Tests.Alerting;

public class AlertEmailTests
{
    private readonly Mock<IEmailService> _email = new();
    private readonly Mock<IReportBuilder> _reportBuilder = new();
    private readonly TestOptionsMonitor<AlertingOptions> _options = new(new AlertingOptions());

    public AlertEmailTests()
    {
        _email.Setup(e => e.IsConfigured).Returns(true);
        _email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _reportBuilder.Setup(r => r.RenderAlertHtml(It.IsAny<AlertNotification>())).Returns("<short/>");
        _reportBuilder.Setup(r => r.BuildReport(It.IsAny<TimeSpan>())).Returns(new ServerReport());
        _reportBuilder.Setup(r => r.RenderEmergencyReportHtml(It.IsAny<ServerReport>(), It.IsAny<Incident>())).Returns("<full/>");
    }

    private EmailNotificationDispatcher Dispatcher() =>
        new(_email.Object, _reportBuilder.Object, _options, NullLogger<EmailNotificationDispatcher>.Instance);

    private static AlertNotification Opened(Incident? incident = null) => new(
        AlertNotificationKind.Opened, "cpu", AlertSeverity.Critical, "CPU Emergency: 99.5%", "CPU at 99.5% for over 300s",
        "SRV-01", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Incident: incident ?? new Incident(DateTime.UtcNow, IncidentType.CpuThresholdBreached, "CPU at 99.5%"));

    [Fact]
    public async Task ShortAlertEmailByDefault()
    {
        (await Dispatcher().DispatchAsync(Opened(), CancellationToken.None)).Should().Be(1);

        _email.Verify(e => e.SendEmailAsync("[WatchTower] CPU Emergency: 99.5% (SRV-01)", "<short/>", It.IsAny<CancellationToken>()), Times.Once);
        _reportBuilder.Verify(r => r.BuildReport(It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact]
    public async Task FullReportWhenConfigured()
    {
        _options.CurrentValue = new AlertingOptions { IncludeFullReport = true };

        await Dispatcher().DispatchAsync(Opened(), CancellationToken.None);

        _email.Verify(e => e.SendEmailAsync(It.IsAny<string>(), "<full/>", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SmtpNotConfigured_CountsAsNoChannel()
    {
        _email.Setup(e => e.IsConfigured).Returns(false);

        (await Dispatcher().DispatchAsync(Opened(), CancellationToken.None)).Should().Be(0);

        _email.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailedDelivery_StillCountsAsHandedToTheChannel()
    {
        _email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        (await Dispatcher().DispatchAsync(Opened(), CancellationToken.None)).Should().Be(1);
    }

    [Theory]
    [InlineData(AlertNotificationKind.Reminder, "[WatchTower] Still open: CPU Emergency: 99.5% (SRV-01)")]
    [InlineData(AlertNotificationKind.Resolved, "[WatchTower] Resolved: CPU Emergency: 99.5% (SRV-01)")]
    [InlineData(AlertNotificationKind.Test, "[WatchTower] Test notification (SRV-01)")]
    public void Subjects(AlertNotificationKind kind, string expected) =>
        EmailNotificationDispatcher.Subject(Opened() with { Kind = kind }).Should().Be(expected);

    [Fact]
    public void RenderAlert_EncodesEverySystemValue()
    {
        var incident = new Incident(DateTime.UtcNow, IncidentType.CpuThresholdBreached, "x",
            [new ProcessInfo("<img src=x onerror=alert(1)>", 42, 1, 100)]);
        var notification = Opened(incident) with
        {
            Kind = AlertNotificationKind.Resolved,
            Title = "<b>title</b>",
            Description = "<script>alert('d')</script>",
            Server = "SRV&<x>",
            AlertKey = "svc:<k>",
            Duration = TimeSpan.FromMinutes(90),
            OpenedAtUtc = new DateTime(2025, 12, 31, 23, 0, 0, DateTimeKind.Utc),
        };

        var html = EmailTemplates.RenderAlert(notification, TimeZoneInfo.Utc, "<UTC>");

        html.Should().NotContain("<script>").And.NotContain("<img").And.NotContain("<b>title").And.NotContain("<UTC>")
            .And.NotContain("svc:<k>");
        html.Should().Contain("&lt;script&gt;").And.Contain("&lt;img src=x").And.Contain("SRV&amp;&lt;x&gt;");
        html.Should().Contain("RESOLVED").And.Contain("1 h 30 min");
    }
}