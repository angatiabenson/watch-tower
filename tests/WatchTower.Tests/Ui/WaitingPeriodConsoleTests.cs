using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using WatchTower.Configuration;

using WatchTower.Services.Alerting;
using WatchTower.Ui;

namespace WatchTower.Tests.Ui;

/// <summary>Waiting periods in the console: pending breaches in the status, and the settings.</summary>
public class WaitingPeriodConsoleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every waiting-period key the console edits, with the default an install without it gets.</summary>
    public static TheoryData<string, int> WaitingKeys() => new()
    {
        { "Monitoring:Cpu:SustainedSeconds", 300 },
        { "Monitoring:Ram:SustainedSeconds", 300 },
        { "Monitoring:Disk:SustainedSeconds", 600 },
        { "Monitoring:Disk:QueueSustainedSeconds", 300 },
        { "Monitoring:Network:SustainedSeconds", 300 },
        { "Monitoring:Services:SustainedSeconds", 120 },
        { "Monitoring:ApplicationPools:SustainedSeconds", 120 },
        { "Monitoring:EndpointDefaults:SustainedSeconds", 120 },
    };

    [Fact]
    public async Task Status_ListsPendingBreaches_ApartFromOpenAlerts()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var alerts = host.Services.GetRequiredService<IAlertManager>();
        alerts.ObserveBreach(AlertKey.DiskQueue("0 C:"), AlertSeverity.Warning, TimeSpan.FromMinutes(5), "Disk Queue High: 0 C: at 3.4");
        var client = await host.SignedInClientAsync();

        var status = await client.GetFromJsonAsync<JsonElement>("api/status", Ct);

        status.GetProperty("alerts").GetArrayLength().Should().Be(0, "a pending breach is not an alert");
        var pending = status.GetProperty("pendingAlerts").EnumerateArray().Should().ContainSingle().Subject;
        pending.GetProperty("key").GetString().Should().Be("diskqueue:0 C:");
        pending.GetProperty("severity").GetString().Should().Be("warning");
        pending.GetProperty("title").GetString().Should().Be("Disk Queue High: 0 C: at 3.4");
        pending.GetProperty("alertAfterSeconds").GetInt64().Should().Be(300);
        (pending.GetProperty("alertsAt").GetDateTime() - pending.GetProperty("since").GetDateTime())
            .Should().Be(TimeSpan.FromMinutes(5));

        alerts.ClearBreach(AlertKey.DiskQueue("0 C:"));
        (await client.GetFromJsonAsync<JsonElement>("api/status", Ct)).GetProperty("pendingAlerts").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(WaitingKeys))]
    public async Task WaitingPeriods_AreEditable_AndRangeChecked(string key, int defaultSeconds)
    {
        ConfigSchema.TryGetField(key, out var field).Should().BeTrue($"{key} is in the console schema");
        field.Type.Should().Be(FieldType.Int);
        (field.Min, field.Max).Should().Be((0d, 86_400d));
        field.Help.Should().Contain("Waiting period");

        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        foreach (var bad in new[] { -1, 86_401 })
        {
            var response = await client.PutAsJsonAsync("api/config", new { changes = new Dictionary<string, object> { [key] = bad } }, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{key} = {bad}");
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty(key, out _).Should().BeTrue();
        }

        (await client.PutAsJsonAsync("api/config", new { changes = new Dictionary<string, object> { [key] = 0 } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK, "0 = alert at once");
        (await client.PutAsJsonAsync("api/config", new { changes = new Dictionary<string, object> { [key] = defaultSeconds } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the default itself");
    }

    [Theory]
    [MemberData(nameof(WaitingKeys))]
    public void WaitingPeriods_DefaultTheSame_InTheShippedSettingsAndWhenAbsent(string key, int defaultSeconds)
    {
        // The shipped appsettings.json (what the console shows) and the options classes (what an
        // install whose files lack the key runs with) agree.
        var shipped = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        shipped[key].Should().Be(defaultSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var empty = new ConfigurationBuilder().Build();
        int Bound() => key switch
        {
            "Monitoring:Cpu:SustainedSeconds" => new CpuMonitorOptions().SustainedSeconds,
            "Monitoring:Ram:SustainedSeconds" => new RamMonitorOptions().SustainedSeconds,
            "Monitoring:Disk:SustainedSeconds" => new DiskMonitorOptions().SustainedSeconds,
            "Monitoring:Disk:QueueSustainedSeconds" => new DiskMonitorOptions().QueueSustainedSeconds,
            "Monitoring:Network:SustainedSeconds" => new NetworkMonitorOptions().SustainedSeconds,
            "Monitoring:Services:SustainedSeconds" => new ServiceMonitorOptions().SustainedSeconds,
            "Monitoring:ApplicationPools:SustainedSeconds" => new ApplicationPoolMonitorOptions().SustainedSeconds,
            "Monitoring:EndpointDefaults:SustainedSeconds" =>
                (empty.GetSection(EndpointMonitorOptions.SectionName).Get<EndpointMonitorOptions>() ?? new()).EndpointDefaults.SustainedSeconds,
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        Bound().Should().Be(defaultSeconds);
    }

    [Fact]
    public async Task EndpointCheck_OwnWaitingPeriod_IsOptional_AndRangeChecked()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        object Check(int? sustained) => sustained is { } s
            ? new { Name = "site", Type = "Http", Target = "https://example.com/", SustainedSeconds = s }
            : new { Name = "site", Type = "Http", Target = "https://example.com/" };
        Task<HttpResponseMessage> Save(int? sustained) => client.PutAsJsonAsync("api/config",
            new { changes = new Dictionary<string, object> { ["Monitoring:Endpoints"] = new[] { Check(sustained) } } }, Ct);

        (await Save(null)).StatusCode.Should().Be(HttpStatusCode.OK, "empty means the default for all checks");
        File.ReadAllText(host.Directory.Paths.SettingsFilePath).Should().NotContain("SustainedSeconds");
        (await Save(-5)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Save(90)).StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(host.Directory.Paths.SettingsFilePath).Should().Contain("\"SustainedSeconds\": 90");
    }
}