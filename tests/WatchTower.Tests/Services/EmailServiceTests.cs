using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

using WatchTower.Configuration;
using WatchTower.Services;

namespace WatchTower.Tests.Services;

public class EmailServiceTests
{
    private static EmailService Create(EmailOptions opts)
    {
        var monitor = new Mock<IOptionsMonitor<EmailOptions>>();
        monitor.Setup(m => m.CurrentValue).Returns(opts);
        return new EmailService(monitor.Object, NullLogger<EmailService>.Instance);
    }

    private static EmailOptions Complete() => new()
    {
        SmtpHost = "smtp.example.com",
        FromAddress = "watchtower@example.com",
        ToAddresses = new List<string> { "ops@example.com" },
    };

    [Fact]
    public void IsConfigured_CompleteSettings_IsTrue()
    {
        Create(Complete()).IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_MissingHost_IsFalse()
    {
        var opts = Complete();
        opts.SmtpHost = " ";
        Create(opts).IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_NoRecipients_IsFalse()
    {
        var opts = Complete();
        opts.ToAddresses = new List<string> { "" };
        Create(opts).IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_MissingSender_IsFalse()
    {
        var opts = Complete();
        opts.FromAddress = string.Empty;
        Create(opts).IsConfigured.Should().BeFalse();
    }
}