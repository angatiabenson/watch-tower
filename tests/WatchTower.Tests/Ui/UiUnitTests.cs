using System.Net;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;

using WatchTower.Tests.Hosting;
using WatchTower.Ui;

namespace WatchTower.Tests.Ui;

public class UiEndpointPlannerTests
{
    [Fact]
    public void Default_IsLoopbackHttpOnly()
    {
        var plan = UiEndpointPlanner.Plan(new UiOptions());

        plan.Problems.Should().BeEmpty();
        plan.Endpoints.Should().ContainSingle()
            .Which.Should().Match<UiEndpoint>(e => e.IsLoopback && !e.Https && e.Port == 8750);
    }

    [Theory]
    [InlineData("http://0.0.0.0:8750", true, "thumb", "https://")]           // remote must be HTTPS
    [InlineData("https://0.0.0.0:8751", false, "thumb", "AllowRemoteAccess")] // remote must be opted in
    [InlineData("https://127.0.0.1:8751", false, null, "CertificateThumbprint")]
    [InlineData("http://myserver:8750", true, "thumb", "IP address")]
    [InlineData("ftp://127.0.0.1:21", false, null, "not a valid listen URL")]
    public void UnsafeOrInvalidUrls_AreDroppedWithReason(string url, bool allowRemote, string? thumb, string expected)
    {
        var plan = UiEndpointPlanner.Plan(new UiOptions
        {
            Urls = [url],
            AllowRemoteAccess = allowRemote,
            CertificateThumbprint = thumb,
        });

        plan.Endpoints.Should().BeEmpty();
        plan.Problems.Should().Contain(p => p.Contains(expected));
    }

    [Fact]
    public void RemoteHttps_WhenAllowed_IsAccepted()
    {
        var plan = UiEndpointPlanner.Plan(new UiOptions
        {
            Urls = ["https://*:8751", "http://127.0.0.1:8750"],
            AllowRemoteAccess = true,
            CertificateThumbprint = "AB",
        });

        plan.Endpoints.Should().HaveCount(2);
        plan.Endpoints.Should().Contain(e => e.Address.Equals(IPAddress.Any) && e.Https);
    }

    [Fact]
    public void ConfiguredUrls_ReplaceTheDefault_WhenBoundFromConfiguration()
    {
        // Regression: a non-empty default list was appended to by the binder, so the console
        // kept listening on 8750 in addition to the configured address.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ui:Urls:0"] = "http://127.0.0.1:8761" })
            .Build();
        var options = config.GetSection("Ui").Get<UiOptions>()!;

        UiEndpointPlanner.Plan(options).Endpoints.Should().ContainSingle().Which.Port.Should().Be(8761);
    }

    [Fact]
    public void Disabled_HasNoEndpoints()
    {
        UiEndpointPlanner.Plan(new UiOptions { Enabled = false }).Endpoints.Should().BeEmpty();
    }
}

public class PasswordHasherTests
{
    [Fact]
    public void HashThenVerify_RoundTrips_AndRejectsWrongPassword()
    {
        var hash = PasswordHasher.Hash("a-long-enough-password");

        hash.Should().StartWith("pbkdf2-sha256$600000$");
        PasswordHasher.Verify("a-long-enough-password", hash).Should().BeTrue();
        PasswordHasher.Verify("a-long-enough-passworD", hash).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("pbkdf2-sha256$1$AAAA$AAAA")] // work factor too low
    public void Verify_MalformedHashes_ReturnFalse(string? encoded)
    {
        PasswordHasher.Verify("whatever-password", encoded).Should().BeFalse();
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("aaaaaaaaaaaa", false)]
    [InlineData("a-long-enough-password", true)]
    public void CheckStrength(string password, bool ok)
    {
        (PasswordHasher.CheckStrength(password) is null).Should().Be(ok);
    }
}

public class ConfigNormalizationTests
{
    [Theory]
    [InlineData("Monitoring:Cpu:PollIntervalSeconds", "5", true)]
    [InlineData("Monitoring:Cpu:PollIntervalSeconds", "0", false)]
    [InlineData("Monitoring:Cpu:PollIntervalSeconds", "2.5", false)]
    [InlineData("Monitoring:Cpu:ThresholdPercent", "99.5", true)]
    [InlineData("Monitoring:Cpu:ThresholdPercent", "\"90\"", false)]
    [InlineData("Email:Security", "\"starttls\"", true)]
    [InlineData("Email:Security", "\"TLS1.3\"", false)]
    [InlineData("Reporting:DailyReportTime", "\"7:05\"", true)]
    [InlineData("Reporting:DailyReportTime", "\"25:00\"", false)]
    [InlineData("Display:Timezone", "\"Africa/Nairobi\"", true)]
    [InlineData("Display:Timezone", "\"Mars/Olympus\"", false)]
    [InlineData("VisibilityPortal:BaseUrl", "\"https://portal.example.com\"", true)]
    [InlineData("VisibilityPortal:BaseUrl", "\"javascript:alert(1)\"", false)]
    [InlineData("VisibilityPortal:Enabled", "true", true)]
    [InlineData("VisibilityPortal:Enabled", "\"yes\"", false)]
    [InlineData("Monitoring:Services:WatchedServices", "[\"W3SVC\", \"MSSQL$SQLEXPRESS\"]", true)]
    [InlineData("Monitoring:Services:WatchedServices", "[\"bad\\\"name\"]", false)]
    public void TryNormalize_ValidatesByFieldType(string key, string json, bool expected)
    {
        ConfigSchema.TryGetField(key, out var field).Should().BeTrue();

        var ok = ConfigService.TryNormalize(field, JsonDocument.Parse(json).RootElement, out _, out var error);

        ok.Should().Be(expected, error);
    }

    [Fact]
    public void TryNormalize_CanonicalizesEnumsAndTimes()
    {
        ConfigSchema.TryGetField("Email:Security", out var security);
        ConfigService.TryNormalize(security, JsonDocument.Parse("\"starttls\"").RootElement, out var node, out _);
        node!.GetValue<string>().Should().Be("StartTls");

        ConfigSchema.TryGetField("Reporting:DailyReportTime", out var time);
        ConfigService.TryNormalize(time, JsonDocument.Parse("\"7:05\"").RootElement, out node, out _);
        node!.GetValue<string>().Should().Be("07:05");
    }

    [Fact]
    public void Schema_KeysAreUnique_AndListFieldsHaveListTypes()
    {
        var keys = ConfigSchema.Sections.SelectMany(s => s.Fields).Select(f => f.Key).ToList();
        keys.Should().OnlyHaveUniqueItems();
        ConfigSchema.Sections.SelectMany(s => s.Fields)
            .Where(f => f.Key.EndsWith("WatchedServices") || f.Key.EndsWith("ToAddresses"))
            .Should().OnlyContain(f => ConfigSchema.IsListType(f.Type));
    }
}

public class LogTailTests
{
    [Fact]
    public void Read_ReturnsLastLinesOfNewestLog()
    {
        using var dir = new TestDataDirectory();
        dir.Paths.EnsureDirectories();
        var older = Path.Combine(dir.Paths.LogsDirectory, "watchtower-20260101.log");
        var newer = Path.Combine(dir.Paths.LogsDirectory, "watchtower-20260102.log");
        File.WriteAllText(older, "old\n");
        File.WriteAllLines(newer, Enumerable.Range(1, 50).Select(i => $"line {i}"));
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));

        var tail = LogTail.Read(dir.Paths.LogsDirectory, 10);

        tail.File.Should().Be("watchtower-20260102.log");
        tail.Lines.Should().HaveCount(10).And.EndWith("line 50");
    }

    [Fact]
    public void Read_WithNoLogs_ReturnsEmpty()
    {
        using var dir = new TestDataDirectory();
        LogTail.Read(dir.Paths.LogsDirectory, 10).Lines.Should().BeEmpty();
    }
}