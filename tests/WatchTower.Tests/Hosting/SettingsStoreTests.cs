using System.Text.Json.Nodes;

using AwesomeAssertions;

using WatchTower.Configuration.Secrets;
using WatchTower.Configuration.Settings;

namespace WatchTower.Tests.Hosting;

public class SettingsStoreTests : IDisposable
{
    private readonly TestDataDirectory _dir = new();
    private readonly FakeSecretProtector _protector = new();
    private readonly JsonSettingsStore _store;

    public SettingsStoreTests()
    {
        _store = new JsonSettingsStore(_dir.Paths.SettingsFilePath, _protector);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Read_WhenFileMissing_ReturnsEmptyObject()
    {
        _store.Read().Count.Should().Be(0);
    }

    [Fact]
    public async Task MergePatch_MergesNestedObjects_ReplacesScalarsAndArrays()
    {
        await Patch("""
            { "Monitoring": { "Cpu": { "ThresholdPercent": 90, "PollIntervalSeconds": 5 },
                              "Services": { "WatchedServices": ["A", "B"] } } }
            """);
        await Patch("""
            { "Monitoring": { "Cpu": { "ThresholdPercent": 85 }, "Services": { "WatchedServices": ["C"] } } }
            """);

        var doc = _store.Read();
        doc["Monitoring"]!["Cpu"]!["ThresholdPercent"]!.GetValue<int>().Should().Be(85);
        doc["Monitoring"]!["Cpu"]!["PollIntervalSeconds"]!.GetValue<int>().Should().Be(5);
        doc["Monitoring"]!["Services"]!["WatchedServices"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal("C");
    }

    [Fact]
    public async Task MergePatch_NullRemovesKey()
    {
        await Patch("""{ "Display": { "Timezone": "UTC", "TimezoneLabel": "UTC" } }""");
        await Patch("""{ "Display": { "TimezoneLabel": null } }""");

        var display = _store.Read()["Display"]!.AsObject();
        display.ContainsKey("TimezoneLabel").Should().BeFalse();
        display["Timezone"]!.GetValue<string>().Should().Be("UTC");
    }

    [Fact]
    public async Task MergePatch_KeysAreCaseInsensitive()
    {
        await Patch("""{ "Email": { "SmtpHost": "a" } }""");
        await Patch("""{ "email": { "smtphost": "b" } }""");

        var text = File.ReadAllText(_store.FilePath);
        text.Should().Contain("\"b\"").And.NotContain("\"a\"");
        _store.Read()["Email"]!["SmtpHost"]!.GetValue<string>().Should().Be("b");
    }

    [Fact]
    public async Task Secrets_AreEncryptedOnWrite_AndNotDoubleEncrypted()
    {
        await Patch("""
            { "Email": { "Password": "hunter2", "Username": "ops" },
              "VisibilityPortal": { "AgentToken": "tok" } }
            """);
        await Patch("""{ "Email": { "Username": "ops2" } }""");

        var text = File.ReadAllText(_store.FilePath);
        text.Should().NotContain("hunter2").And.NotContain("\"tok\"").And.Contain("ops2");

        var doc = _store.Read();
        var password = doc["Email"]!["Password"]!.GetValue<string>();
        DpapiSecretProtector.IsProtected(password).Should().BeTrue();
        _protector.Unprotect(password).Should().Be("hunter2"); // still a single layer of encryption
        _protector.Unprotect(doc["VisibilityPortal"]!["AgentToken"]!.GetValue<string>()).Should().Be("tok");
    }

    [Fact]
    public async Task Write_KeepsBackupOfPreviousVersion()
    {
        await Patch("""{ "Display": { "Timezone": "UTC" } }""");
        await Patch("""{ "Display": { "Timezone": "Africa/Nairobi" } }""");

        File.ReadAllText(_store.FilePath + ".bak").Should().Contain("UTC");
        File.Exists(_store.FilePath + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentUpdates_AreSerialized()
    {
        var tasks = Enumerable.Range(0, 20).Select(i =>
            _store.UpdateAsync(doc =>
            {
                var counter = doc["Counter"]?.GetValue<int>() ?? 0;
                doc["Counter"] = counter + 1;
            }, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        _store.Read()["Counter"]!.GetValue<int>().Should().Be(20);
    }

    [Fact]
    public void DpapiProtector_RoundTrips()
    {
        var cipher = DpapiSecretProtector.Instance.Protect("päss wörd");
        cipher.Should().StartWith(DpapiSecretProtector.Prefix);
        DpapiSecretProtector.Instance.Unprotect(cipher).Should().Be("päss wörd");
    }

    private Task Patch(string json) =>
        _store.ApplyMergePatchAsync(Obj(json), TestContext.Current.CancellationToken);

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
}