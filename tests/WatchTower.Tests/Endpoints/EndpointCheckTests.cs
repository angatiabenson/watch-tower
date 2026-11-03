using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Moq;

using WatchTower.Configuration;
using WatchTower.Services.Endpoints;

namespace WatchTower.Tests.Endpoints;

public class EndpointCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EndpointCheck Create(EndpointCheckOptions options)
    {
        EndpointCheck.TryCreate(options, "Monitoring:Endpoints:0", out var check, out var error).Should().BeTrue(error);
        return check;
    }

    // ── Configuration ──────────────────────────────────────────────────────

    [Fact]
    public void Http_UsesDefaultsAndHidesCredentialsAndQuery()
    {
        var check = Create(new EndpointCheckOptions { Name = " site ", Target = "https://user:pw@example.com/health?token=abc" });

        check.Name.Should().Be("site");
        check.Type.Should().Be(EndpointCheckType.Http);
        check.DisplayTarget.Should().Be("https://example.com/health");
        check.Timeout.Should().Be(TimeSpan.FromSeconds(10));
        check.Interval.Should().Be(TimeSpan.FromSeconds(60));
        check.FailuresBeforeAlert.Should().Be(2);
        check.CertificateWarnDays.Should().Be(21);
        check.IsExpectedStatus(200).Should().BeTrue();
        check.IsExpectedStatus(399).Should().BeTrue();
        check.IsExpectedStatus(400).Should().BeFalse();
    }

    [Theory]
    [InlineData("Tcp", "db01:1433", "db01", 1433)]
    [InlineData("tcp", "[::1]:5432", "::1", 5432)]
    [InlineData("Certificate", "example.com", "example.com", 443)]
    [InlineData("Certificate", "example.com:8443", "example.com", 8443)]
    [InlineData("Certificate", "https://example.com:9443/path", "example.com", 9443)]
    public void HostAndPortTargets(string type, string target, string host, int port)
    {
        var check = Create(new EndpointCheckOptions { Name = "x", Type = type, Target = target });

        check.Host.Should().Be(host);
        check.Port.Should().Be(port);
    }

    [Theory]
    [InlineData("Ping", "https://example.com", "Type")]
    [InlineData("Http", "ftp://example.com", "Target")]
    [InlineData("Http", "/relative", "Target")]
    [InlineData("Tcp", "db01", "Target")]
    [InlineData("Tcp", "db01:99999", "Target")]
    [InlineData("Tcp", "fe80::1:80", "Target")]
    [InlineData("Certificate", "http://example.com", "Target")]
    public void InvalidTargets(string type, string target, string field)
    {
        EndpointCheck.TryCreate(new EndpointCheckOptions { Name = "x", Type = type, Target = target }, "K", out _, out var error)
            .Should().BeFalse();
        error.Should().StartWith($"K:{field}");
    }

    [Theory]
    [InlineData("200", true)]
    [InlineData("200,204, 301-302", true)]
    [InlineData("", true)]
    [InlineData("99", false)]
    [InlineData("300-200", false)]
    [InlineData("2xx", false)]
    [InlineData("200-600", false)]
    public void StatusRanges(string value, bool valid) =>
        EndpointCheck.TryParseStatusRanges(value, out _).Should().Be(valid);

    [Fact]
    public void Validate_ReportsRangeProblemsAndDuplicateNames()
    {
        var errors = EndpointCheck.Validate(
        [
            new EndpointCheckOptions { Name = "a", Target = "https://a.example" },
            new EndpointCheckOptions { Name = "A", Target = "https://b.example" },
            new EndpointCheckOptions { Name = "", Target = "https://c.example" },
            new EndpointCheckOptions { Name = "d", Target = "https://d.example", TimeoutSeconds = 0 },
            new EndpointCheckOptions { Name = "e", Target = "https://e.example", IntervalSeconds = 1 },
            new EndpointCheckOptions { Name = "f", Target = "https://f.example", FailuresBeforeAlert = 0 },
        ]);

        errors.Should().HaveCount(5);
        errors[0].Should().Be("Monitoring:Endpoints:1:Name 'A' is used by more than one endpoint.");
    }

    // ── HTTP probe ─────────────────────────────────────────────────────────

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return respond(request, ct);
        }
    }

    private static EndpointProbe Probe(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(EndpointProbe.HttpClientName)).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new EndpointProbe(factory.Object, new FakeTimeProvider(DateTimeOffset.UtcNow));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body = "", string? location = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        if (location is not null)
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    [Fact]
    public async Task Http_ExpectedStatusIsUp_OtherStatusIsDown()
    {
        var ok = await Probe(new ScriptedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK))))
            .ProbeAsync(Create(new EndpointCheckOptions { Name = "x", Target = "https://example.com" }), Ct);
        ok.State.Should().Be(EndpointState.Up);
        ok.Detail.Should().Be("HTTP 200");
        ok.ResponseTimeMs.Should().NotBeNull();

        var bad = await Probe(new ScriptedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable))))
            .ProbeAsync(Create(new EndpointCheckOptions { Name = "x", Target = "https://example.com" }), Ct);
        bad.State.Should().Be(EndpointState.Down);
        bad.Detail.Should().StartWith("Unexpected HTTP 503");
    }

    [Fact]
    public async Task Http_ChecksTheBody()
    {
        var probe = Probe(new ScriptedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "status: healthy"))));

        (await probe.ProbeAsync(Create(new EndpointCheckOptions { Name = "x", Target = "https://e.com", ExpectBodyContains = "healthy" }), Ct))
            .State.Should().Be(EndpointState.Up);
        (await probe.ProbeAsync(Create(new EndpointCheckOptions { Name = "x", Target = "https://e.com", ExpectBodyContains = "Healthy" }), Ct))
            .State.Should().Be(EndpointState.Down);
    }

    [Fact]
    public async Task Http_FollowsSameHostRedirectsOnly()
    {
        var handler = new ScriptedHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/" => Response(HttpStatusCode.Found, location: "/login"),
            "/login" => Response(HttpStatusCode.MovedPermanently, location: "https://sso.other.example/auth"),
            _ => Response(HttpStatusCode.OK),
        }));

        var result = await Probe(handler).ProbeAsync(
            Create(new EndpointCheckOptions { Name = "x", Target = "https://app.example/", ExpectStatus = "200" }), Ct);

        handler.Requests.Select(u => u.AbsoluteUri).Should().Equal("https://app.example/", "https://app.example/login");
        result.State.Should().Be(EndpointState.Down, "the cross-host redirect is the final answer and 301 is not expected");
        result.Detail.Should().Contain("301");
    }

    [Fact]
    public async Task Http_TimeoutIsDown()
    {
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Response(HttpStatusCode.OK);
        });

        var result = await Probe(handler).ProbeAsync(
            Create(new EndpointCheckOptions { Name = "x", Target = "https://example.com", TimeoutSeconds = 1 }), Ct);

        result.State.Should().Be(EndpointState.Down);
        result.Detail.Should().Be("Timed out after 1s");
    }

    // ── TCP probe ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Tcp_ListeningPortIsUp_ClosedPortIsDown()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var probe = Probe(new ScriptedHandler((_, _) => throw new InvalidOperationException()));

        (await probe.ProbeAsync(Create(new EndpointCheckOptions { Name = "x", Type = "Tcp", Target = $"127.0.0.1:{port}" }), Ct))
            .State.Should().Be(EndpointState.Up);

        listener.Stop();
        var closed = await probe.ProbeAsync(
            Create(new EndpointCheckOptions { Name = "x", Type = "Tcp", Target = $"127.0.0.1:{port}", TimeoutSeconds = 5 }), Ct);
        closed.State.Should().Be(EndpointState.Down);
        closed.Detail.Should().Contain("ConnectionRefused");
    }

    // ── Certificates ───────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Classify_ValidLongLivedCertificateIsUp()
    {
        var result = EndpointProbe.Classify(Now.AddDays(90), SslPolicyErrors.None, X509ChainStatusFlags.NoError, "example.com", Now, 21);

        result.State.Should().Be(EndpointState.Up);
        result.CertificateDaysLeft.Should().Be(90);
        result.Detail.Should().Be("Certificate for example.com valid for 90 more days (2026-08-30)");
    }

    [Fact]
    public void Classify_InsideWarningWindowIsExpiring()
    {
        var result = EndpointProbe.Classify(Now.AddDays(10.5), SslPolicyErrors.None, X509ChainStatusFlags.NoError, "example.com", Now, 21);

        result.State.Should().Be(EndpointState.Expiring);
        result.Detail.Should().Contain("expires in 10.5 days");
    }

    [Fact]
    public void Classify_ExpiredOrInvalidIsDown()
    {
        EndpointProbe.Classify(Now.AddDays(-1), SslPolicyErrors.RemoteCertificateChainErrors, X509ChainStatusFlags.NotTimeValid, null, Now, 21)
            .Detail.Should().Be("Certificate expired on 2026-05-31");

        var mismatch = EndpointProbe.Classify(Now.AddDays(200), SslPolicyErrors.RemoteCertificateNameMismatch,
            X509ChainStatusFlags.NoError, "other.example", Now, 21);
        mismatch.State.Should().Be(EndpointState.Down);
        mismatch.Detail.Should().Contain("RemoteCertificateNameMismatch");
    }

    [Fact]
    public async Task Certificate_ReadsTheServerCertificateOverTls()
    {
        using var certificate = SelfSigned("localhost", DateTimeOffset.UtcNow.AddDays(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Ct);
            await using var ssl = new SslStream(client.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(certificate);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                // the client may hang up right after reading the certificate
            }
        }, Ct);

        var result = await new EndpointProbe(Mock.Of<IHttpClientFactory>(), TimeProvider.System).ProbeAsync(
            Create(new EndpointCheckOptions { Name = "x", Type = "Certificate", Target = $"localhost:{port}", TimeoutSeconds = 20 }), Ct);
        await server;

        // Self-signed: untrusted, so Down — but the expiry was read from the handshake.
        result.State.Should().Be(EndpointState.Down);
        result.Detail.Should().Contain("not valid");
        result.CertificateDaysLeft.Should().BeApproximately(10, 0.2);
    }

    private static X509Certificate2 SelfSigned(string subject, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(subject);
        request.CertificateExtensions.Add(san.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), notAfter);
        // SChannel needs a key it can use for the server side of the handshake: round-trip via PFX.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }
}