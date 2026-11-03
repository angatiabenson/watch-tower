using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using WatchTower.Configuration.Secrets;
using WatchTower.Ui;

namespace WatchTower.Tests.Ui;

public class ConsoleApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Api_RequiresSignIn()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = host.CreateClient();

        (await client.GetAsync("api/status", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("api/config", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithWrongPassword_IsRejected_AndCorrectPasswordOpensApi()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = host.CreateClient();

        var bad = await client.PostAsJsonAsync("api/auth/login", new { password = "nope-nope-nope" }, Ct);
        bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var good = await client.PostAsJsonAsync("api/auth/login", new { password = ConsoleTestHost.Password }, Ct);
        good.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var status = await client.GetFromJsonAsync<JsonElement>("api/status", Ct);
        status.GetProperty("agent").GetProperty("version").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task StateChangingRequests_WithoutCsrfHeader_AreRejected()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = host.CreateClient(csrfHeader: false);

        var response = await client.PostAsJsonAsync("api/auth/login", new { password = ConsoleTestHost.Password }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task FirstRunSetup_FromServerItself_CreatesPassword_ThenIsClosed()
    {
        await using var host = await ConsoleTestHost.StartAsync(withPassword: false);
        var client = host.CreateClient();

        var state = await client.GetFromJsonAsync<JsonElement>("api/auth/state", Ct);
        state.GetProperty("setupRequired").GetBoolean().Should().BeTrue();

        (await client.PostAsJsonAsync("api/auth/setup", new { password = "short" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("api/auth/setup", new { password = "a-long-enough-password" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Signed in straight away, and setup cannot be repeated.
        (await client.GetAsync("api/status", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.CreateClient().PostAsJsonAsync("api/auth/setup", new { password = "another-long-password" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        File.ReadAllText(host.Directory.Paths.SettingsFilePath).Should().Contain("pbkdf2-sha256$");
    }

    [Fact]
    public async Task DnsRebinding_LoopbackRequestWithForeignHost_IsRejected_SoSetupCannotBeHijacked()
    {
        await using var host = await ConsoleTestHost.StartAsync(withPassword: false);
        var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/setup")
        {
            Content = JsonContent.Create(new { password = "attacker-chosen-pass" }),
        };
        request.Headers.Host = "attacker.example:8750";
        request.Headers.Add("Origin", "http://attacker.example:8750");

        var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.MisdirectedRequest);
        File.ReadAllText(host.Directory.Paths.SettingsFilePath).Should().NotContain("AdminPasswordHash");
    }

    [Theory]
    [InlineData("http://127.0.0.1:8750", true)]
    [InlineData("https://127.0.0.1:8750", false)] // scheme differs
    [InlineData("http://127.0.0.1:9999", false)]  // port differs
    public async Task Csrf_OriginMustMatchCompletely(string origin, bool accepted)
    {
        await using var host = await ConsoleTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = JsonContent.Create(new { password = ConsoleTestHost.Password }),
        };
        request.Headers.Add("Origin", origin);

        var response = await host.CreateClient().SendAsync(request, Ct);

        (response.StatusCode == HttpStatusCode.NoContent).Should().Be(accepted);
    }

    [Fact]
    public async Task FirstRunSetup_FromRemoteAddress_IsForbidden()
    {
        await using var host = await ConsoleTestHost.StartAsync(withPassword: false, requestsAreLocal: false);

        var response = await host.CreateClient().PostAsJsonAsync("api/auth/setup", new { password = "a-long-enough-password" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateConfig_PersistsToOperatorLayer_AndReportsSource()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        var response = await client.PutAsJsonAsync("api/config", new
        {
            changes = new Dictionary<string, object>
            {
                ["Monitoring:Cpu:ThresholdPercent"] = 95.5,
                ["Email:ToAddresses"] = new[] { "ops@example.com", "OPS@example.com", " " },
            },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var file = File.ReadAllText(host.Directory.Paths.SettingsFilePath);
        file.Should().Contain("95.5").And.Contain("ops@example.com");

        await WaitForAsync(async () =>
        {
            var view = await client.GetFromJsonAsync<JsonElement>("api/config", Ct);
            var cpu = view.GetProperty("values").GetProperty("Monitoring:Cpu:ThresholdPercent");
            return cpu.GetProperty("source").GetString() == "operator" && cpu.GetProperty("value").GetDouble() == 95.5;
        });

        var recipients = (await client.GetFromJsonAsync<JsonElement>("api/config", Ct))
            .GetProperty("values").GetProperty("Email:ToAddresses").GetProperty("value");
        recipients.EnumerateArray().Select(e => e.GetString()).Should().Equal("ops@example.com"); // de-duplicated
    }

    [Fact]
    public async Task SavedValue_IsVisibleToTheVeryNextRead()
    {
        // Regression: PUT returned before the debounced reload, so an immediate GET showed the old value.
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        (await client.PutAsJsonAsync("api/config",
            new { changes = new Dictionary<string, object> { ["Monitoring:Ram:ThresholdPercent"] = 77 } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await client.GetFromJsonAsync<JsonElement>("api/config", Ct);

        view.GetProperty("values").GetProperty("Monitoring:Ram:ThresholdPercent").GetProperty("value").GetDouble()
            .Should().Be(77);
    }

    [Fact]
    public async Task UpdateConfig_InvalidValues_AreRejectedWithFieldErrors_AndNothingIsSaved()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();
        var before = File.ReadAllText(host.Directory.Paths.SettingsFilePath);

        var response = await client.PutAsJsonAsync("api/config", new
        {
            changes = new Dictionary<string, object>
            {
                ["Monitoring:Cpu:ThresholdPercent"] = 150,
                ["Email:ToAddresses"] = new[] { "not-an-email" },
                ["Ui:AdminPasswordHash"] = "x", // not editable through the API
            },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.TryGetProperty("Monitoring:Cpu:ThresholdPercent", out _).Should().BeTrue();
        errors.TryGetProperty("Email:ToAddresses", out _).Should().BeTrue();
        errors.TryGetProperty("Ui:AdminPasswordHash", out _).Should().BeTrue();
        File.ReadAllText(host.Directory.Paths.SettingsFilePath).Should().Be(before);
    }

    [Fact]
    public async Task UpdateConfig_CrossFieldRules_AreEnforced()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        var response = await client.PutAsJsonAsync("api/config", new
        {
            changes = new Dictionary<string, object>
            {
                ["Monitoring:Disk:WarningThresholdPercent"] = 90,
                ["Monitoring:Disk:CriticalThresholdPercent"] = 80,
            },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.TryGetProperty("Monitoring:Disk:WarningThresholdPercent", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Secrets_AreEncryptedAtRest_AndNeverReturned()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        var response = await client.PutAsJsonAsync("api/config", new
        {
            changes = new Dictionary<string, object> { ["Email:Password"] = "smtp-secret-123" },
        }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var file = File.ReadAllText(host.Directory.Paths.SettingsFilePath);
        file.Should().NotContain("smtp-secret-123").And.Contain(DpapiSecretProtector.Prefix);

        await WaitForAsync(async () =>
        {
            var json = await client.GetStringAsync("api/config", Ct);
            json.Should().NotContain("smtp-secret-123");
            var field = JsonDocument.Parse(json).RootElement.GetProperty("values").GetProperty("Email:Password");
            return field.GetProperty("hasValue").GetBoolean() && field.GetProperty("value").ValueKind == JsonValueKind.Null;
        });
    }

    [Fact]
    public async Task RestartBoundSetting_ReportsRestartRequired()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        var response = await client.PutAsJsonAsync("api/config", new
        {
            changes = new Dictionary<string, object> { ["Serilog:MinimumLevel:Default"] = "Debug" },
        }, Ct);

        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("restartRequired").GetBoolean()
            .Should().BeTrue();
        host.Services.GetRequiredService<RestartState>().Pending.Should().BeTrue();
    }

    [Fact]
    public async Task ResetField_RemovesItFromOperatorSettings()
    {
        await using var host = await ConsoleTestHost.StartAsync(
            settingsJson: """{ "Display": { "TimezoneLabel": "UTC" } }""");
        var client = await host.SignedInClientAsync();

        var response = await client.PutAsJsonAsync("api/config", new { reset = new[] { "Display:TimezoneLabel" } }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        File.ReadAllText(host.Directory.Paths.SettingsFilePath).Should().NotContain("TimezoneLabel");
    }

    [Fact]
    public async Task ChangingPassword_SignsOutOtherSessions()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var first = await host.SignedInClientAsync();
        var second = await host.SignedInClientAsync();

        var change = await first.PostAsJsonAsync("api/auth/password",
            new { currentPassword = ConsoleTestHost.Password, newPassword = "brand-new-password-1" }, Ct);
        change.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await first.GetAsync("api/status", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.GetAsync("api/status", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Responses_CarrySecurityHeaders()
    {
        await using var host = await ConsoleTestHost.StartAsync();

        var response = await host.CreateClient().GetAsync("api/auth/state", Ct);

        response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'self'");
        response.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");
        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
    }

    [Fact]
    public async Task ConsoleAssets_AreServedFromTheExecutable()
    {
        await using var host = await ConsoleTestHost.StartAsync();

        var response = await host.CreateClient().GetAsync("/", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        // Saved settings reach IConfiguration via the file watcher (debounced ~250 ms).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within 10 s.");
            await Task.Delay(100, Ct);
        }
    }
}