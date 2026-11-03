using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using WatchTower.Cli;
using WatchTower.Portal;
using WatchTower.Portal.Contracts;
using WatchTower.Tests.Ui;

namespace WatchTower.Tests.Portal;

public class EnrollmentCodeTests
{
    [Fact]
    public void RoundTrips_AndFollowsTheAgreedWireFormat()
    {
        var code = new EnrollmentCode("https://portal.example.com", "srv-01", "eyJhbGciOi.token.sig", RequireTls: true);
        var encoded = code.Encode();

        encoded.Should().StartWith("WT1.").And.NotContain("=").And.NotContain("+").And.NotContain("/");
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(
            encoded[4..].Replace('-', '+').Replace('_', '/').PadRight((encoded.Length - 4 + 3) / 4 * 4, '=')));
        json.Should().Contain("\"v\":1").And.Contain("\"agentId\":\"srv-01\"");

        EnrollmentCode.TryParse(encoded, out var parsed, out _).Should().BeTrue();
        parsed.Should().Be(code);
    }

    [Fact]
    public void PortalWrittenCode_WithoutRequireTls_DerivesItFromTheScheme()
    {
        var json = """{"v":1,"url":"http://portal.corp.local:8080/","agentId":"a","token":"t"}""";
        var encoded = "WT1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        EnrollmentCode.TryParse(encoded, out var code, out _).Should().BeTrue();

        code.RequireTls.Should().BeFalse();
        code.Url.Should().Be("http://portal.corp.local:8080");
    }

    [Theory]
    [InlineData("", "starts with")]
    [InlineData("hello", "starts with")]
    [InlineData("WT1.!!!notbase64", "damaged")]
    [InlineData("WT1.eyJ2IjoyfQ", "newer portal")]                 // {"v":2}
    [InlineData("WT1.eyJ2IjoiMSJ9", "damaged")]                    // {"v":"1"} (wrong type)
    [InlineData("WT1.eyJ2IjoxLCJ1cmwiOiJmdHA6Ly94In0", "portal URL")] // {"v":1,"url":"ftp://x"}
    public void InvalidCodes_AreRejectedWithAHelpfulMessage(string text, string expected)
    {
        EnrollmentCode.TryParse(text, out _, out var error).Should().BeFalse();
        error.Should().Contain(expected);
    }

    [Fact]
    public async Task Cli_Check_ValidatesWithoutChangingAnything()
    {
        var code = new EnrollmentCode("https://portal.example.com", "srv-01", "tok", true).Encode();
        var output = new StringWriter();

        var exit = await WatchTowerCli.RunAsync(["enroll", code, "--check"], output, output);

        exit.Should().Be(0);
        output.ToString().Should().Contain("srv-01");
    }

    [Fact]
    public async Task ConsoleEnroll_ConnectsThePortal_AndStoresTheTokenEncrypted()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();
        var ct = TestContext.Current.CancellationToken;
        var code = new EnrollmentCode("https://portal.example.com", "srv-01", "agent-jwt-secret", true).Encode();

        var response = await client.PostAsJsonAsync("api/actions/enroll", new { code }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("restartRequired").GetBoolean().Should().BeTrue();
        var settings = File.ReadAllText(host.Directory.Paths.SettingsFilePath);
        settings.Should().Contain("srv-01").And.Contain("https://portal.example.com").And.NotContain("agent-jwt-secret");

        (await client.PostAsJsonAsync("api/actions/enroll", new { code = "nope" }, ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

public class AgentTelemetryWireTests
{
    [Fact]
    public void MetricsBatch_CarriesTheAgentObject_InTheAgreedCamelCaseShape()
    {
        var batch = new MetricsBatchDto("srv-01", "b1", "2026-11-03T10:00:00Z", [], [], [], [], [], [], [],
            new AgentTelemetryDto("1.6.0", "SRV-01", "Windows", "2026-11-03T09:00:00Z", 3600, "Available", "1.7.0",
                new AgentUpdateResultDto("1.6.0", true, "2026-11-02T09:00:00Z")));

        var json = JsonSerializer.Serialize(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"agent\":{\"version\":\"1.6.0\",\"machineName\":\"SRV-01\"")
            .And.Contain("\"updatePhase\":\"Available\"").And.Contain("\"availableVersion\":\"1.7.0\"")
            .And.Contain("\"lastUpdateResult\":{\"toVersion\":\"1.6.0\",\"success\":true");
    }
}