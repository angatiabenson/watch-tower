using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;

using WatchTower.Configuration;
using WatchTower.Ui;

namespace WatchTower.Tests.Ui;

/// <summary>Monitoring:Endpoints as an editable list of objects in the console schema.</summary>
public class EndpointSchemaTests
{
    private const string Key = "Monitoring:Endpoints";

    private static ConfigField Field()
    {
        ConfigSchema.TryGetField(Key, out var field).Should().BeTrue();
        return field;
    }

    private static (JsonArray? List, Dictionary<string, string> Errors) Normalize(string json)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = ConfigService.NormalizeObjectList(Field(), JsonDocument.Parse(json).RootElement, errors);
        return (list, errors);
    }

    [Fact]
    public void Field_IsAnObjectList_WithEveryEndpointCheckOption()
    {
        var field = Field();

        field.Type.Should().Be(FieldType.ObjectList);
        field.UniqueBy.Should().Be("Name");
        field.RestartRequired.Should().BeFalse(); // the worker reads IOptionsMonitor.CurrentValue
        var properties = typeof(EndpointCheckOptions).GetProperties().Select(p => p.Name);
        field.Fields!.Select(f => f.Key).Should().BeEquivalentTo(properties);
    }

    [Fact]
    public void ItemDefaults_MatchTheOptionsClass()
    {
        // Absent properties are not written, so the binder default applies; the console shows
        // the schema default for them. The two must agree.
        var defaults = new EndpointCheckOptions();
        foreach (var sub in Field().Fields!.Where(f => f.Default is not null))
        {
            var expected = typeof(EndpointCheckOptions).GetProperty(sub.Key)!.GetValue(defaults);
            sub.Default.Should().Be(expected, sub.Key);
        }
    }

    [Fact]
    public void ValidList_IsNormalized_AndAbsentOptionalPropertiesAreLeftOut()
    {
        var (list, errors) = Normalize("""
            [
              { "name": " intranet ", "type": "http", "target": "https://intranet.example/health", "timeoutSeconds": 5 },
              { "Name": "sql", "Type": "TCP", "Target": "sql01:1433" }
            ]
            """);

        errors.Should().BeEmpty();
        list.Should().HaveCount(2);
        var first = list![0]!.AsObject();
        first["Name"]!.GetValue<string>().Should().Be("intranet");
        first["Type"]!.GetValue<string>().Should().Be("Http");
        first["TimeoutSeconds"]!.GetValue<long>().Should().Be(5);
        first.ContainsKey("IntervalSeconds").Should().BeFalse();
        list[1]!["Type"]!.GetValue<string>().Should().Be("Tcp");
    }

    [Fact]
    public void MissingRequiredFields_AreRejectedPerProperty()
    {
        var (list, errors) = Normalize("""[ { "type": "Http" }, { "name": "", "target": "  " } ]""");

        list.Should().BeNull();
        errors.Should().ContainKey($"{Key}:0:Name").And.ContainKey($"{Key}:0:Target")
            .And.ContainKey($"{Key}:1:Name").And.ContainKey($"{Key}:1:Target");
        errors[$"{Key}:0:Name"].Should().Be("Name is required.");
    }

    [Fact]
    public void DuplicateNames_AreRejected_CaseInsensitively()
    {
        var (list, errors) = Normalize("""
            [ { "name": "api", "target": "https://a.example" }, { "name": "API ", "target": "https://b.example" } ]
            """);

        list.Should().BeNull();
        errors.Should().ContainSingle().Which.Key.Should().Be($"{Key}:1:Name");
        errors[$"{Key}:1:Name"].Should().Contain("entry 1");
    }

    [Theory]
    [InlineData("""[ { "name": "a", "target": "x", "timeoutSeconds": 0 } ]""", "0:TimeoutSeconds")]
    [InlineData("""[ { "name": "a", "target": "x", "timeoutSeconds": 121 } ]""", "0:TimeoutSeconds")]
    [InlineData("""[ { "name": "a", "target": "x", "intervalSeconds": 4 } ]""", "0:IntervalSeconds")]
    [InlineData("""[ { "name": "a", "target": "x", "failuresBeforeAlert": 101 } ]""", "0:FailuresBeforeAlert")]
    [InlineData("""[ { "name": "a", "target": "x", "certificateWarnDays": 0 } ]""", "0:CertificateWarnDays")]
    [InlineData("""[ { "name": "a", "target": "x", "type": "Ping" } ]""", "0:Type")]
    [InlineData("""[ { "name": "a", "target": "x", "intervalSeconds": "60" } ]""", "0:IntervalSeconds")]
    [InlineData("""[ { "name": "a", "target": "x", "colour": "red" } ]""", "0")]
    [InlineData("""[ "https://example.com" ]""", "0")]
    public void InvalidProperties_AreRejectedWithTheirKey(string json, string expectedKey)
    {
        var (list, errors) = Normalize(json);

        list.Should().BeNull();
        errors.Should().ContainKey($"{Key}:{expectedKey}");
    }

    [Fact]
    public void NameLongerThanTheLimit_IsRejected()
    {
        var name = new string('n', WatchTower.Services.Endpoints.EndpointCheck.MaxNameLength + 1);
        var (_, errors) = Normalize($$"""[ { "name": "{{name}}", "target": "x" } ]""");

        errors[$"{Key}:0:Name"].Should().Contain("at most 100 characters");
    }

    [Fact]
    public void NonArrays_AndTooManyEntries_AreRejected()
    {
        Normalize("""{ "name": "a" }""").Errors.Should().ContainKey(Key);

        var many = "[" + string.Join(',', Enumerable.Range(0, ConfigSchema.MaxEndpointChecks + 1)
            .Select(i => $$"""{ "name": "e{{i}}", "target": "h:1" }""")) + "]";
        Normalize(many).Errors[Key].Should().Contain("At most 100");
    }

    [Theory]
    [InlineData("Monitoring:Endpoints:2:Target must be host:port.", "Monitoring:Endpoints:2:Target", "Target must be host:port.")]
    [InlineData("Monitoring:Endpoints:0:Name 'a' is used by more than one endpoint.", "Monitoring:Endpoints:0:Name", "Name 'a' is used by more than one endpoint.")]
    [InlineData("Monitoring:Disk:CriticalThresholdPercent must be above the warning.", "Monitoring:Disk:CriticalThresholdPercent", "Monitoring:Disk:CriticalThresholdPercent must be above the warning.")]
    [InlineData("Something unrelated failed.", "_", "Something unrelated failed.")]
    public void ValidatorMessages_MapToTheEntryTheyMention(string message, string key, string text)
    {
        ConfigService.MapError(message).Should().Be((key, text));
    }

    [Fact]
    public void ReadValue_ReturnsEntriesInOrder_WithDefaultsForAbsentProperties()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monitoring:Endpoints:0:Name"] = "first",
            ["Monitoring:Endpoints:0:Target"] = "https://a.example",
            ["Monitoring:Endpoints:0:TimeoutSeconds"] = "7",
            ["Monitoring:Endpoints:10:Name"] = "eleventh",
            ["Monitoring:Endpoints:2:Name"] = "third",
        }).Build();

        var value = ConfigService.ReadValue(config, Field())!.AsArray();

        value.Select(i => i!["Name"]!.GetValue<string>()).Should().Equal("first", "third", "eleventh");
        value[0]!["TimeoutSeconds"]!.GetValue<long>().Should().Be(7);
        value[0]!["IntervalSeconds"]!.GetValue<int>().Should().Be(60);
        value[1]!["Type"]!.GetValue<string>().Should().Be("Http");
    }
}

public class EndpointConfigApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Check(string name, string type, string target) => new { name, type, target };

    private static async Task<JsonElement> EndpointsValueAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("api/config", Ct))
            .GetProperty("values").GetProperty("Monitoring:Endpoints");

    [Fact]
    public async Task Schema_DescribesTheEntryProperties_ForTheUi()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        var view = await client.GetFromJsonAsync<JsonElement>("api/config", Ct);
        var section = view.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "endpoints");
        var fields = section.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
        fields.Keys.Should().BeEquivalentTo(["Monitoring:Endpoints", "Monitoring:EndpointDefaults:SustainedSeconds"]);
        var field = fields["Monitoring:Endpoints"];

        field.GetProperty("type").GetString().Should().Be("ObjectList");
        field.GetProperty("uniqueBy").GetString().Should().Be("Name");
        field.GetProperty("max").GetDouble().Should().Be(ConfigSchema.MaxEndpointChecks);
        var subs = field.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
        subs["Name"].GetProperty("required").GetBoolean().Should().BeTrue();
        subs["Type"].GetProperty("default").GetString().Should().Be("Http");
        subs["IntervalSeconds"].GetProperty("default").GetInt32().Should().Be(60);
        subs["ExpectStatus"].GetProperty("visibleWhen").GetProperty("values")[0].GetString().Should().Be("Http");
        subs["IntervalSeconds"].TryGetProperty("visibleWhen", out _).Should().BeFalse(); // null extras are omitted

        // Waiting period: optional per check (no default: empty means the section-wide default).
        subs["SustainedSeconds"].GetProperty("label").GetString().Should().Be("Alert after (s)");
        subs["SustainedSeconds"].TryGetProperty("default", out _).Should().BeFalse();
        subs["SustainedSeconds"].GetProperty("min").GetDouble().Should().Be(0);
        fields["Monitoring:EndpointDefaults:SustainedSeconds"].GetProperty("type").GetString().Should().Be("Int");

        // Other fields are unchanged on the wire.
        var smtp = view.GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("key").GetString() == "Email:SmtpPort");
        smtp.TryGetProperty("fields", out _).Should().BeFalse();
        smtp.TryGetProperty("required", out _).Should().BeFalse();
    }

    [Fact]
    public async Task EndpointChecks_CanBeAddedEditedAndRemoved()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        var add = await client.PutAsJsonAsync("api/config", new
        {
            changes = new Dictionary<string, object>
            {
                ["Monitoring:Endpoints"] = new object[]
                {
                    Check("intranet", "Http", "https://intranet.example/health"),
                    new { name = "sql", type = "Tcp", target = "sql01.example:1433", intervalSeconds = 30 },
                    Check("portal-tls", "Certificate", "portal.example"),
                },
            },
        }, Ct);

        add.StatusCode.Should().Be(HttpStatusCode.OK, await add.Content.ReadAsStringAsync(Ct));
        var value = await EndpointsValueAsync(client);
        value.GetProperty("source").GetString().Should().Be("operator");
        value.GetProperty("hasValue").GetBoolean().Should().BeTrue();
        var items = value.GetProperty("value").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("Name").GetString()).Should().Equal("intranet", "sql", "portal-tls");
        items[1].GetProperty("IntervalSeconds").GetInt32().Should().Be(30);
        items[0].GetProperty("ExpectStatus").GetString().Should().Be("200-399"); // default shown

        // Edit one and remove the others: the saved list replaces the previous one entirely.
        var edit = await client.PutAsJsonAsync("api/config", new
        {
            changes = new Dictionary<string, object>
            {
                ["Monitoring:Endpoints"] = new object[]
                {
                    new { name = "sql", type = "Tcp", target = "sql02.example:1433", intervalSeconds = 45 },
                },
            },
        }, Ct);

        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        items = (await EndpointsValueAsync(client)).GetProperty("value").EnumerateArray().ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("Target").GetString().Should().Be("sql02.example:1433");
        items[0].GetProperty("IntervalSeconds").GetInt32().Should().Be(45);

        var clear = await client.PutAsJsonAsync("api/config",
            new { changes = new Dictionary<string, object> { ["Monitoring:Endpoints"] = Array.Empty<object>() } }, Ct);

        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        value = await EndpointsValueAsync(client);
        value.GetProperty("value").GetArrayLength().Should().Be(0);
        value.GetProperty("hasValue").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("Http", "intranet.example/health", "absolute http(s) URL")]
    [InlineData("Http", "ftp://files.example", "absolute http(s) URL")]
    [InlineData("Tcp", "sql01.example:70000", "host:port")]
    [InlineData("Tcp", "sql01.example", "host:port")]
    [InlineData("Certificate", "portal.example:99999", "host, host:port or an https URL")]
    public async Task InvalidTargets_AreRejectedOnTheEntry_AndNothingIsSaved(string type, string target, string expected)
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();
        var before = File.ReadAllText(host.Directory.Paths.SettingsFilePath);

        var response = await client.PutAsJsonAsync("api/config", new
        {
            changes = new Dictionary<string, object>
            {
                ["Monitoring:Endpoints"] = new object[] { Check("ok", "Http", "https://ok.example"), Check("bad", type, target) },
            },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.GetProperty("Monitoring:Endpoints:1:Target").GetString().Should().StartWith("Target must be").And.Contain(expected);
        File.ReadAllText(host.Directory.Paths.SettingsFilePath).Should().Be(before);
    }

    [Fact]
    public async Task InvalidStatusRanges_AreRejectedOnTheEntry()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();

        var response = await client.PutAsJsonAsync("api/config", new
        {
            changes = new Dictionary<string, object>
            {
                ["Monitoring:Endpoints"] = new object[]
                {
                    new { name = "api", type = "Http", target = "https://api.example", expectStatus = "200-700" },
                },
            },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.TryGetProperty("Monitoring:Endpoints:0:ExpectStatus", out _).Should().BeTrue();
    }

    [Fact]
    public async Task DuplicateNames_AndMissingFields_AreRejected()
    {
        await using var host = await ConsoleTestHost.StartAsync();
        var client = await host.SignedInClientAsync();
        var before = File.ReadAllText(host.Directory.Paths.SettingsFilePath);

        var response = await client.PutAsJsonAsync("api/config", new
        {
            changes = new Dictionary<string, object>
            {
                ["Monitoring:Endpoints"] = new object[]
                {
                    Check("api", "Http", "https://a.example"),
                    Check("Api", "Http", "https://b.example"),
                    new { type = "Tcp" },
                },
            },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.TryGetProperty("Monitoring:Endpoints:1:Name", out _).Should().BeTrue();
        errors.TryGetProperty("Monitoring:Endpoints:2:Name", out _).Should().BeTrue();
        errors.TryGetProperty("Monitoring:Endpoints:2:Target", out _).Should().BeTrue();
        File.ReadAllText(host.Directory.Paths.SettingsFilePath).Should().Be(before);
    }
}