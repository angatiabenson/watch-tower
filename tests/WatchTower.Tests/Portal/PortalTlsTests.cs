using System.Security.Cryptography.X509Certificates;

using AwesomeAssertions;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

using WatchTower.Configuration;
using WatchTower.Portal.Clients;

namespace WatchTower.Tests.Portal;

public class PortalTlsTests
{
    // A syntactically valid SHA-1 thumbprint that will not exist in any store.
    private const string MissingThumbprint = "00112233445566778899AABBCCDDEEFF00112233";

    private static PortalOptions Options(string baseUrl, bool requireTls = true, string? thumbprint = null) => new()
    {
        Enabled = true,
        AgentId = "agent",
        AgentToken = "token",
        BaseUrl = baseUrl,
        Protocol = "Grpc",
        Tls = new TlsOptions { RequireTls = requireTls, ClientCertThumbprint = thumbprint },
    };

    private static GrpcPortalClient CreateGrpcClient(PortalOptions opts)
    {
        var monitor = new Mock<IOptionsMonitor<PortalOptions>>();
        monitor.Setup(m => m.CurrentValue).Returns(opts);
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns(Environments.Production);
        return new GrpcPortalClient(monitor.Object, env.Object, NullLogger<GrpcPortalClient>.Instance);
    }

    [Fact]
    public void Grpc_RequireTlsWithHttpBaseUrl_IsRejected()
    {
        var act = () => CreateGrpcClient(Options("http://portal.local:5000"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*must use HTTPS*");
    }

    [Fact]
    public void Grpc_HttpBaseUrlAllowedWhenRequireTlsDisabled()
    {
        using var client = CreateGrpcClient(Options("http://portal.local:5000", requireTls: false));

        client.Should().NotBeNull();
    }

    [Fact]
    public void Grpc_HttpsBaseUrlWithoutClientCert_IsAccepted()
    {
        using var client = CreateGrpcClient(Options("https://portal.local"));

        client.Should().NotBeNull();
    }

    [Fact]
    public void Grpc_MissingClientCertificate_FailsWithClearError()
    {
        var act = () => CreateGrpcClient(Options("https://portal.local", thumbprint: MissingThumbprint));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*ClientCertThumbprint*{MissingThumbprint}*no such certificate was found*");
    }

    [Fact]
    public void LoadClientCertificate_NoThumbprint_ReturnsNull()
    {
        PortalTls.LoadClientCertificate(new TlsOptions()).Should().BeNull();
        PortalTls.LoadClientCertificate(new TlsOptions { ClientCertThumbprint = "  " }).Should().BeNull();
    }

    [Fact]
    public void LoadClientCertificate_MalformedThumbprint_Throws()
    {
        var act = () => PortalTls.LoadClientCertificate(new TlsOptions { ClientCertThumbprint = "not-a-thumbprint!" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a valid*");
    }

    [Fact]
    public void LoadClientCertificate_MissingCertificate_Throws()
    {
        var act = () => PortalTls.LoadClientCertificate(
            new TlsOptions { ClientCertThumbprint = MissingThumbprint }, StoreLocation.CurrentUser);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no such certificate was found*");
    }

    [Fact]
    public void NormalizeThumbprint_StripsSpacesAndInvisibleMarks()
    {
        // Copy/paste from the certificates MMC snap-in yields spaced lower-case hex prefixed
        // with an invisible U+200E left-to-right mark.
        PortalTls.NormalizeThumbprint("‎ab cd ef 01").Should().Be("ABCDEF01");
    }

    [Fact]
    public void CreateHandler_WithoutThumbprint_HasNoClientCertificate()
    {
        using var handler = PortalTls.CreateHandler(new TlsOptions());

        handler.SslOptions.ClientCertificates.Should().BeNull();
        handler.SslOptions.RemoteCertificateValidationCallback.Should().BeNull();
    }

    [Fact]
    public void EnsureTransportAllowed_HttpsClientShareSamePolicy()
    {
        var act = () => PortalTls.EnsureTransportAllowed(Options("http://portal.local"));

        act.Should().Throw<InvalidOperationException>();
        PortalTls.EnsureTransportAllowed(Options("https://portal.local"));
        PortalTls.EnsureTransportAllowed(Options("http://portal.local", requireTls: false));
    }
}