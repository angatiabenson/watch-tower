using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using WatchTower.Configuration;
using WatchTower.Configuration.Secrets;
using WatchTower.Configuration.Settings;
using WatchTower.Hosting;
using WatchTower.Models;
using WatchTower.Portal.Actions.Handlers;
using WatchTower.Services.Alerting;
using WatchTower.Tests.Hosting;
using WatchTower.Tests.Ui;

namespace WatchTower.Tests.Alerting;

public class NotificationChannelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SecretUrl = "https://hooks.example.com/services/T000/B000/s3cr3t-token";

    private static AlertNotification Notification(
        AlertNotificationKind kind = AlertNotificationKind.Opened, AlertSeverity severity = AlertSeverity.Critical) => new(
        kind, "cpu", severity, "CPU Emergency: 99.5%", "CPU at 99.5% for over 300s", "SRV-01",
        new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        OpenedAtUtc: new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
        Duration: TimeSpan.FromHours(1),
        Incident: new Incident(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), IncidentType.CpuThresholdBreached, "x",
            [new ProcessInfo("sqlservr", 1234, 50, 2048, 80)]));

    // ── Channel resolution ─────────────────────────────────────────────────

    [Fact]
    public void Resolver_EnablesFixedChannelsWithUrls_AndListEntriesOverrideByName()
    {
        var options = new NotificationOptions
        {
            Teams = new WebhookNotificationSettings { Url = SecretUrl, MinSeverity = "Warning" },
            Webhook = new WebhookNotificationSettings { Url = "", Secret = "ignored" },
            Channels =
            [
                new NotificationChannelOptions { Name = "teams", Type = "Teams", Url = SecretUrl, MinSeverity = "Critical" },
                new NotificationChannelOptions { Name = "ops-hook", Type = "webhook", Url = SecretUrl, Secret = "k" },
                new NotificationChannelOptions { Type = "Slack", Url = SecretUrl },
                new NotificationChannelOptions { Name = "off", Type = "Slack", Url = SecretUrl, Enabled = false },
                new NotificationChannelOptions { Name = "bad", Type = "Pigeon", Url = SecretUrl },
            ],
        };

        var channels = NotificationChannelResolver.Resolve(options);

        channels.Select(c => (c.Name, c.Type, c.MinSeverity)).Should().Equal(
            ("email", NotificationChannelType.Email, AlertSeverity.Info),
            ("teams", NotificationChannelType.Teams, AlertSeverity.Critical),
            ("ops-hook", NotificationChannelType.Webhook, AlertSeverity.Info),
            ("slack-2", NotificationChannelType.Slack, AlertSeverity.Info));
        channels.Single(c => c.Name == "ops-hook").Secret.Should().Be("k");
        channels.Single(c => c.Name == "teams").ToString().Should().NotContain("hooks.example.com");
    }

    [Fact]
    public void Resolver_EmailCanBeTurnedOff()
    {
        NotificationChannelResolver.Resolve(new NotificationOptions { Email = new EmailNotificationSettings { Enabled = false } })
            .Should().BeEmpty();
    }

    // ── Dispatcher ─────────────────────────────────────────────────────────

    private sealed class FakeChannel(NotificationChannelType type, Func<CancellationToken, Task<ChannelSendResult>> send)
        : INotificationChannel
    {
        public List<(string Channel, AlertNotification Notification)> Calls { get; } = [];
        public bool Available { get; set; } = true;
        public NotificationChannelType Type => type;
        public bool IsAvailable(NotificationChannel channel) => Available;

        public Task<ChannelSendResult> SendAsync(NotificationChannel channel, AlertNotification notification, CancellationToken ct)
        {
            lock (Calls)
                Calls.Add((channel.Name, notification));
            return send(ct);
        }
    }

    private static NotificationDispatcher Dispatcher(NotificationOptions options, params INotificationChannel[] channels) =>
        new(channels, new TestOptionsMonitor<NotificationOptions>(options), NullLogger<NotificationDispatcher>.Instance);

    [Fact]
    public async Task Dispatch_FiltersBySeverity_AndOneFailingChannelDoesNotBlockOthers()
    {
        var email = new FakeChannel(NotificationChannelType.Email, _ => throw new InvalidOperationException("smtp exploded"));
        var teams = new FakeChannel(NotificationChannelType.Teams, _ => Task.FromResult(ChannelSendResult.Ok()));
        var slack = new FakeChannel(NotificationChannelType.Slack, _ => Task.FromResult(ChannelSendResult.Ok()));
        var options = new NotificationOptions
        {
            Teams = new WebhookNotificationSettings { Url = SecretUrl, MinSeverity = "Warning" },
            Slack = new WebhookNotificationSettings { Url = SecretUrl, MinSeverity = "Critical" },
        };
        var dispatcher = Dispatcher(options, email, teams, slack);

        (await dispatcher.DispatchAsync(Notification(severity: AlertSeverity.Warning), Ct)).Should().Be(2);
        email.Calls.Should().ContainSingle();
        teams.Calls.Should().ContainSingle();
        slack.Calls.Should().BeEmpty("Warning is below Slack's Critical minimum");

        (await dispatcher.DispatchAsync(Notification(severity: AlertSeverity.Critical), Ct)).Should().Be(3);
    }

    [Fact]
    public async Task Dispatch_UnavailableChannelsAreNotCounted()
    {
        var email = new FakeChannel(NotificationChannelType.Email, _ => Task.FromResult(ChannelSendResult.Ok())) { Available = false };

        (await Dispatcher(new NotificationOptions(), email).DispatchAsync(Notification(), Ct)).Should().Be(0);
        email.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Dispatch_SlowWebhookTimesOutWithoutDelayingTheOthersBeyondTheTimeout()
    {
        var slow = new FakeChannel(NotificationChannelType.Slack, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ChannelSendResult.Ok();
        });
        var email = new FakeChannel(NotificationChannelType.Email, _ => Task.FromResult(ChannelSendResult.Ok()));
        var options = new NotificationOptions { TimeoutSeconds = 1, Slack = new WebhookNotificationSettings { Url = SecretUrl } };

        var results = await Dispatcher(options, email, slow).SendTestAsync(null, "SRV-01", Ct);

        results.Should().BeEquivalentTo(new[]
        {
            new ChannelTestResult("email", "Email", true, "Delivered"),
            new ChannelTestResult("slack", "Slack", false, "Timed out after 1s"),
        });
    }

    [Fact]
    public async Task SendTest_IgnoresSeverity_SelectsByName_AndReportsUnavailableChannels()
    {
        var email = new FakeChannel(NotificationChannelType.Email, _ => Task.FromResult(ChannelSendResult.Ok())) { Available = false };
        var teams = new FakeChannel(NotificationChannelType.Teams, _ => Task.FromResult(ChannelSendResult.Ok()));
        var options = new NotificationOptions
        {
            Email = new EmailNotificationSettings { MinSeverity = "Critical" },
            Teams = new WebhookNotificationSettings { Url = SecretUrl, MinSeverity = "Critical" },
        };
        var dispatcher = Dispatcher(options, email, teams);

        var all = await dispatcher.SendTestAsync(null, "SRV-01", Ct);
        all.Select(r => (r.Channel, r.Ok)).Should().Equal(("email", false), ("teams", true));
        all[0].Message.Should().Contain("not configured");
        teams.Calls.Single().Notification.Kind.Should().Be(AlertNotificationKind.Test);

        (await dispatcher.SendTestAsync("TEAMS", "SRV-01", Ct)).Should().ContainSingle().Which.Channel.Should().Be("teams");
        (await dispatcher.SendTestAsync("nope", "SRV-01", Ct)).Should().BeEmpty();
    }

    // ── HTTP channels ──────────────────────────────────────────────────────

    private sealed class CapturingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public byte[] Body { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(status);
        }
    }

    private static IHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpNotificationChannel.HttpClientName)).Returns(() => new HttpClient(handler, disposeHandler: false));
        return factory.Object;
    }

    [Fact]
    public async Task Webhook_PostsJsonAndSignsTheExactBody()
    {
        var handler = new CapturingHandler();
        var channel = new WebhookNotificationChannel(Factory(handler));
        var target = new NotificationChannel("hook", NotificationChannelType.Webhook, AlertSeverity.Info, SecretUrl, "s3cret");

        var result = await channel.SendAsync(target, Notification(AlertNotificationKind.Resolved), Ct);

        result.Success.Should().BeTrue();
        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), handler.Body));
        handler.Request.Headers.GetValues("X-WatchTower-Signature").Single().Should().Be(expected);
        handler.Request.Headers.GetValues("X-WatchTower-Event").Single().Should().Be("resolved");

        var json = JsonDocument.Parse(handler.Body).RootElement;
        json.GetProperty("schema").GetString().Should().Be("watchtower.alert.v1");
        json.GetProperty("kind").GetString().Should().Be("resolved");
        json.GetProperty("alertKey").GetString().Should().Be("cpu");
        json.GetProperty("severity").GetString().Should().Be("critical");
        json.GetProperty("server").GetString().Should().Be("SRV-01");
        json.GetProperty("durationSeconds").GetInt64().Should().Be(3600);
        json.GetProperty("incidentType").GetString().Should().Be("CpuThresholdBreached");
        json.GetProperty("topProcesses")[0].GetProperty("name").GetString().Should().Be("sqlservr");
    }

    [Fact]
    public async Task Webhook_WithoutSecret_IsUnsigned_AndFailuresNeverMentionTheUrl()
    {
        var handler = new CapturingHandler(HttpStatusCode.NotFound);
        var channel = new WebhookNotificationChannel(Factory(handler));
        var target = new NotificationChannel("hook", NotificationChannelType.Webhook, AlertSeverity.Info, SecretUrl);

        var result = await channel.SendAsync(target, Notification(), Ct);

        handler.Request!.Headers.Contains("X-WatchTower-Signature").Should().BeFalse();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("404").And.NotContain("hooks.example.com");
    }

    [Fact]
    public void WebhookSignature_MatchesAKnownVector()
    {
        // HMAC-SHA256("key", "The quick brown fox jumps over the lazy dog")
        WebhookNotificationChannel.Sign("key", Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog"))
            .Should().Be("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8");
    }

    [Fact]
    public void Teams_BuildsAnAdaptiveCardMessage()
    {
        var payload = JsonNode.Parse(new TeamsNotificationChannel(Mock.Of<IHttpClientFactory>()).BuildPayload(Notification()))!;

        payload["type"]!.GetValue<string>().Should().Be("message");
        var attachment = payload["attachments"]![0]!;
        attachment["contentType"]!.GetValue<string>().Should().Be("application/vnd.microsoft.card.adaptive");
        var card = attachment["content"]!;
        card["type"]!.GetValue<string>().Should().Be("AdaptiveCard");
        card["body"]![0]!["text"]!.GetValue<string>().Should().Be("CPU Emergency: 99.5%");
        card["body"]![0]!["color"]!.GetValue<string>().Should().Be("Attention");
        card["body"]![2]!["facts"]!.AsArray().Select(f => f!["title"]!.GetValue<string>())
            .Should().Contain(["Server", "Alert", "Severity", "Top processes"]);
    }

    [Fact]
    public void Slack_BuildsBlockKitAndEscapesControlCharacters()
    {
        var notification = Notification() with { Description = "pool <b>&</b> down", Server = "SRV<1>" };

        var payload = JsonNode.Parse(new SlackNotificationChannel(Mock.Of<IHttpClientFactory>()).BuildPayload(notification))!;

        payload["text"]!.GetValue<string>().Should().Contain("SRV&lt;1&gt;");
        payload["mrkdwn"]!.GetValue<bool>().Should().BeFalse();
        var blocks = payload["blocks"]!.AsArray();
        blocks[0]!["type"]!.GetValue<string>().Should().Be("header");
        // plain_text is shown verbatim by Slack, so it is not entity-escaped.
        blocks[1]!["text"]!["type"]!.GetValue<string>().Should().Be("plain_text");
        blocks[1]!["text"]!["text"]!.GetValue<string>().Should().Contain("pool <b>&</b> down");
        blocks[2]!["fields"]!.AsArray().Count.Should().BeInRange(1, 10);
    }

    /// <summary>Text a monitored server controls: an HTTP reason phrase, a service or process name.</summary>
    private static AlertNotification Malicious() => Notification() with
    {
        Title = "Endpoint Down: *urgent* [click](https://attacker.example)",
        Description = "Unexpected HTTP 503 [instructions](https://attacker.example) _now_ ~gone~ `code`\n- item\n1. step\n# Heading",
        Server = "SRV-[01](https://attacker.example)",
        Incident = new Incident(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), IncidentType.EndpointDown, "x",
            [new ProcessInfo("[evil](https://attacker.example)", 1, 1, 1, 1)]),
    };

    [Fact]
    public void Teams_EscapesMarkdownInEveryDynamicString()
    {
        var payload = JsonNode.Parse(new TeamsNotificationChannel(Mock.Of<IHttpClientFactory>()).BuildPayload(Malicious()))!;
        var body = payload["attachments"]![0]!["content"]!["body"]!.AsArray();

        var headline = body[0]!["text"]!.GetValue<string>();
        var description = body[1]!["text"]!.GetValue<string>();
        var facts = body[2]!["facts"]!.AsArray().Select(f => f!["value"]!.GetValue<string>()).ToList();

        headline.Should().Be(@"Endpoint Down: \*urgent\* \[click\]\(https://attacker.example\)");
        description.Should().Be(
            @"Unexpected HTTP 503 \[instructions\]\(https://attacker.example\) \_now\_ \~gone\~ \`code\`" + "\n" +
            @"\- item" + "\n" + @"1\. step" + "\n" + @"\# Heading");
        facts.Should().Contain(@"SRV-\[01\]\(https://attacker.example\)");
        facts.Should().Contain(v => v.StartsWith(@"\[evil\]\(https://attacker.example\) \(PID 1"));
        foreach (var text in facts.Append(headline).Append(description))
            text.Should().NotContain("](", "no Markdown link may survive");
    }

    [Theory]
    [InlineData("plain text 99.5% 2026-01-01", "plain text 99.5% 2026-01-01")]
    [InlineData(@"disk:C:\", @"disk:C:\\")]
    [InlineData("  - indented", @"  \- indented")]
    [InlineData("12. not a list?", @"12\. not a list?")]
    [InlineData("a 1. b", "a 1. b")]
    [InlineData("<b>x</b>!", @"\<b\>x\</b\>\!")]
    [InlineData("", "")]
    public void Teams_EscapeMarkdown(string input, string expected) =>
        TeamsNotificationChannel.EscapeMarkdown(input).Should().Be(expected);

    [Fact]
    public void Slack_NeverSendsServerTextAsMrkdwn()
    {
        var payload = JsonNode.Parse(new SlackNotificationChannel(Mock.Of<IHttpClientFactory>()).BuildPayload(Malicious()))!;

        payload["mrkdwn"]!.GetValue<bool>().Should().BeFalse("the fallback text must not be formatted");
        payload["text"]!.GetValue<string>().Should().NotContain("<").And.NotContain(">");
        var textObjects = payload["blocks"]!.AsArray()
            .SelectMany(b => b!["fields"]?.AsArray().Select(f => f!) ?? [b!["text"]!])
            .ToList();
        textObjects.Should().HaveCountGreaterThan(4);
        textObjects.Should().OnlyContain(t => t["type"]!.GetValue<string>() == "plain_text");
        textObjects.Select(t => t["text"]!.GetValue<string>()).Should().Contain(t => t.Contains("[instructions](https://attacker.example)"),
            "plain_text keeps the text verbatim");
    }

    // ── Secrets ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Notifications:Teams:Url", true)]
    [InlineData("Notifications:Slack:Url", true)]
    [InlineData("Notifications:Webhook:Url", true)]
    [InlineData("Notifications:Webhook:Secret", true)]
    [InlineData("Notifications:Channels:0:Url", true)]
    [InlineData("notifications:channels:12:secret", true)]
    [InlineData("Notifications:Channels:0:Name", false)]
    [InlineData("Notifications:Channels:0:Url:Extra", false)]
    [InlineData("Notifications:Teams:MinSeverity", false)]
    public void SecretSettings_CoverWebhookUrlsAndListItems(string key, bool secret) =>
        SecretSettings.IsSecret(key).Should().Be(secret);

    [Fact]
    public async Task SettingsStore_EncryptsSecretsInsideLists()
    {
        using var dir = new TestDataDirectory();
        var store = new JsonSettingsStore(dir.Paths.SettingsFilePath, new FakeSecretProtector());

        await store.ApplyMergePatchAsync(JsonNode.Parse($$"""
            { "Notifications": { "Channels": [
                { "Name": "a", "Type": "Webhook", "Url": "{{SecretUrl}}", "Secret": "hmac-key" },
                { "Name": "b", "Type": "Email" } ] } }
            """)!.AsObject(), Ct);

        var text = File.ReadAllText(dir.Paths.SettingsFilePath);
        text.Should().NotContain("s3cr3t-token").And.NotContain("hmac-key").And.Contain("dpapi:");
        JsonSettingsStore.ContainsPlaintextSecret(store.Read()).Should().BeFalse();
        JsonSettingsStore.ContainsPlaintextSecret(JsonNode.Parse(
            $$"""{ "Notifications": { "Channels": [ { "Url": "{{SecretUrl}}" } ] } }""")!.AsObject()).Should().BeTrue();
    }

    [Fact]
    public void Validator_RejectsBadChannelsWithoutEchoingUrls()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:Teams:Url"] = "not a url s3cr3t-token",
            ["Notifications:Slack:MinSeverity"] = "Loud",
            ["Notifications:Channels:0:Type"] = "Pigeon",
            ["Notifications:Channels:1:Type"] = "Webhook",
            ["Notifications:Channels:1:Url"] = "",
            ["Notifications:Channels:2:Type"] = "Slack",
            ["Notifications:Channels:2:Url"] = SecretUrl,
            ["Notifications:Channels:2:Name"] = "x",
            ["Notifications:Channels:3:Type"] = "Teams",
            ["Notifications:Channels:3:Url"] = SecretUrl,
            ["Notifications:Channels:3:Name"] = "X",
        }).Build();

        var errors = StartupValidator.Validate(config).Errors;

        errors.Should().Contain(e => e.Contains("Notifications:Teams:Url"));
        errors.Should().Contain(e => e.Contains("Notifications:Slack:MinSeverity"));
        errors.Should().Contain(e => e.Contains("Notifications:Channels:0:Type"));
        errors.Should().Contain(e => e.Contains("Notifications:Channels:1:Url"));
        errors.Should().Contain(e => e.Contains("Notifications:Channels:3:Name"));
        errors.Should().NotContain(e => e.Contains("s3cr3t-token"));
    }

    // ── Console API and portal action ──────────────────────────────────────

    [Fact]
    public async Task ConsoleApi_NeverReturnsWebhookUrls_AndTestActionReportsPerChannel()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        (await client.PutAsJsonAsync("api/config", new
        {
            changes = new Dictionary<string, object>
            {
                ["Notifications:Teams:Url"] = SecretUrl,
                ["Notifications:Teams:MinSeverity"] = "Warning",
            },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        File.ReadAllText(host.Directory.Paths.SettingsFilePath).Should().NotContain("s3cr3t-token");
        var view = await client.GetStringAsync("api/config", Ct);
        view.Should().NotContain("s3cr3t-token");
        var teams = JsonDocument.Parse(view).RootElement.GetProperty("values").GetProperty("Notifications:Teams:Url");
        teams.GetProperty("hasValue").GetBoolean().Should().BeTrue();
        teams.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);

        var unknown = await client.PostAsJsonAsync("api/actions/test-notification", new { channel = "nope" }, Ct);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Email is enabled by default but SMTP is not configured: reported, not thrown.
        var response = await client.PostAsJsonAsync("api/actions/test-notification", new { channel = "email" }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("ok").GetBoolean().Should().BeFalse();
        body.GetProperty("results")[0].GetProperty("channel").GetString().Should().Be("email");
    }

    [Fact]
    public async Task PortalAction_SendsTestThroughEveryChannel()
    {
        var teams = new FakeChannel(NotificationChannelType.Teams, _ => Task.FromResult(ChannelSendResult.Ok()));
        var email = new FakeChannel(NotificationChannelType.Email, _ => Task.FromResult(ChannelSendResult.Failed("SMTP down")));
        var dispatcher = Dispatcher(new NotificationOptions { Teams = new WebhookNotificationSettings { Url = SecretUrl } }, email, teams);
        var handler = new SendTestNotificationHandler(dispatcher, NullLogger<SendTestNotificationHandler>.Instance);

        handler.ActionType.Should().Be((WatchTower.Portal.Actions.ActionType)501);
        var all = await handler.ExecuteAsync(new Dictionary<string, string>(), Ct);
        all.Success.Should().BeFalse();
        all.Message.Should().Contain("email (Email): SMTP down").And.Contain("teams (Teams): ok").And.NotContain("hooks.example.com");

        var one = await handler.ExecuteAsync(new Dictionary<string, string> { ["Channel"] = "teams" }, Ct);
        one.Success.Should().BeTrue();

        (await handler.ExecuteAsync(new Dictionary<string, string> { ["Channel"] = "missing" }, Ct)).Success.Should().BeFalse();
    }
}