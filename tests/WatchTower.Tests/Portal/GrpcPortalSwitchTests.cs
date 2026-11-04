using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using AwesomeAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using WatchTower.Configuration;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Portal.Contracts.Protobuf;
using WatchTower.Tests.Hosting;
using WatchTower.Tests.PortalContract;

namespace WatchTower.Tests.Portal;

/// <summary>
/// The gRPC client when the VisibilityPortal settings change while the agent runs: every call goes
/// to the BaseUrl, with the credentials and TLS settings, of one snapshot, and a portal never sees
/// another portal's token. The portals are real HTTP/2 servers (Kestrel) that record each call's
/// metadata and the identity in its message.
/// </summary>
public sealed class GrpcPortalSwitchTests
{
    private const string TokenA = "token-of-portal-a";
    private const string TokenB = "token-of-portal-b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] AllMethods =
    [
        "SendMetricsBatch", "SendEvent", "PollCommands", "AcknowledgeCommand", "ReportCommandResult",
        "FetchConfig", "AcknowledgeConfig",
    ];

    private static PortalOptions Options(string baseUrl, string token, string compress = RequestCompressionModes.Gzip) => new()
    {
        Enabled = true,
        Protocol = "Grpc",
        BaseUrl = baseUrl,
        AgentId = "agent-1",
        AgentToken = token,
        CompressRequests = compress,
        Tls = new TlsOptions { RequireTls = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) },
    };

    private static IHostEnvironment Environment(string name)
    {
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns(name);
        return env.Object;
    }

    private static GrpcPortalClient Create(
        IOptionsMonitor<PortalOptions> monitor, RecordingLoggerProvider logs, string environment = "Production") =>
        new(monitor, Environment(environment), logs.CreateLogger<GrpcPortalClient>());

    /// <summary>Makes each of the seven calls once; returns whether each succeeded.</summary>
    private static async Task<bool[]> CallAllAsync(GrpcPortalClient client)
    {
        var results = new List<bool>
        {
            await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct),
            await client.SendEventAsync(AgentPayloads.FullEvent(), Ct),
        };
        await client.PollCommandsAsync(Ct); // no commands queued: success and failure look alike here
        results.Add(true);
        results.Add(await client.AcknowledgeCommandAsync(new AcknowledgeCommandDto("cmd-1", "2026-11-04T10:00:00Z"), Ct));
        results.Add(await client.ReportCommandResultAsync(new CommandResultDto("cmd-1", true, "done", "2026-11-04T10:00:01Z"), Ct));
        await client.FetchConfigurationAsync("v1", Ct);
        results.Add(true);
        results.Add(await client.AcknowledgeConfigAsync("v1", Ct));
        return results.ToArray();
    }

    private static void ShouldAllCarry(IEnumerable<GrpcTestPortal.Call> calls, string token)
    {
        foreach (var call in calls)
        {
            call.Authorization.Should().Be($"Bearer {token}", call.Method);
            call.IdentityToken.Should().Be(token, call.Method);
            call.AgentId.Should().Be("agent-1", call.Method);
        }
    }

    private static IEnumerable<LogEntry> Failures(RecordingLoggerProvider logs) =>
        logs.At(LogLevel.Warning).Where(e => e.Message.StartsWith("gRPC ", StringComparison.Ordinal));

    [Fact]
    public async Task BaseUrlSwitch_EveryCallGoesToTheNewPortal_WithItsToken_AndTheOldPortalNeverSeesIt()
    {
        await using var a = await GrpcTestPortal.StartAsync();
        await using var b = await GrpcTestPortal.StartAsync();
        var monitor = new MutableOptionsMonitor<PortalOptions>(Options(a.BaseUrl, TokenA));
        var logs = new RecordingLoggerProvider();
        using var client = Create(monitor, logs);

        (await CallAllAsync(client)).Should().AllBeEquivalentTo(true);
        monitor.Set(Options(b.BaseUrl, TokenB));
        (await CallAllAsync(client)).Should().AllBeEquivalentTo(true);

        a.Calls.Select(c => c.Method).Should().Equal(AllMethods, "after the switch nothing more goes to A");
        ShouldAllCarry(a.Calls, TokenA);
        b.Calls.Select(c => c.Method).Should().Equal(AllMethods, "polls, acknowledgements and config fetches move too");
        ShouldAllCarry(b.Calls, TokenB);
        b.Calls.Where(c => c.Compressed).Select(c => c.Method).Should().Equal("SendMetricsBatch", "SendEvent");
        Failures(logs).Should().BeEmpty();
    }

    [Fact]
    public async Task BaseUrlSwitch_WithoutANotification_IsStillPickedUpByTheNextCall()
    {
        await using var a = await GrpcTestPortal.StartAsync();
        await using var b = await GrpcTestPortal.StartAsync();
        var monitor = new MutableOptionsMonitor<PortalOptions>(Options(a.BaseUrl, TokenA));
        using var client = Create(monitor, new RecordingLoggerProvider());

        monitor.Value = Options(b.BaseUrl, TokenB); // change notification lost

        (await client.AcknowledgeConfigAsync("v1", Ct)).Should().BeTrue();
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();

        a.Calls.Should().BeEmpty();
        ShouldAllCarry(b.Calls, TokenB);
    }

    [Fact]
    public async Task TokenChange_OnTheSameHost_IsUsedByTheNextCall()
    {
        await using var a = await GrpcTestPortal.StartAsync();
        var monitor = new MutableOptionsMonitor<PortalOptions>(Options(a.BaseUrl, TokenA));
        using var client = Create(monitor, new RecordingLoggerProvider());

        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();
        monitor.Set(Options(a.BaseUrl, TokenB));
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();
        (await client.PollCommandsAsync(Ct)).Should().BeEmpty();

        a.Calls.Select(c => (c.Authorization, c.IdentityToken)).Should().Equal(
            ($"Bearer {TokenA}", TokenA),
            ($"Bearer {TokenB}", TokenB),
            ($"Bearer {TokenB}", TokenB));
    }

    [Fact]
    public async Task EncodingRefusalByTheOldPortal_DoesNotSwitchCompressionOffForTheNewOne()
    {
        await using var a = await GrpcTestPortal.StartAsync();
        await using var b = await GrpcTestPortal.StartAsync();
        a.AcceptsGzip = false;
        var monitor = new MutableOptionsMonitor<PortalOptions>(Options(a.BaseUrl, TokenA));
        var logs = new RecordingLoggerProvider();
        using var client = Create(monitor, logs);

        monitor.Set(Options(b.BaseUrl, TokenB));
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();
        (await client.SendEventAsync(AgentPayloads.FullEvent(), Ct)).Should().BeTrue();

        a.Calls.Should().BeEmpty();
        b.Calls.Select(c => (c.Method, c.Compressed)).Should().Equal(("SendMetricsBatch", true), ("SendEvent", true));
        logs.At(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task CallInFlightDuringTheSwitch_FinishesOnItsOwnPortal_AndItsRefusalOnlyAffectsThatPortal()
    {
        await using var a = await GrpcTestPortal.StartAsync();
        await using var b = await GrpcTestPortal.StartAsync();
        a.AcceptsGzip = false;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.OnCall = async _ =>
        {
            if (arrived.TrySetResult())
                await release.Task;
        };
        var monitor = new MutableOptionsMonitor<PortalOptions>(Options(a.BaseUrl, TokenA));
        var logs = new RecordingLoggerProvider();
        using var client = Create(monitor, logs);

        var inFlight = client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        monitor.Set(Options(b.BaseUrl, TokenB));
        (await client.SendEventAsync(AgentPayloads.FullEvent(), Ct)).Should().BeTrue("B is used while A's call is running");
        release.SetResult();

        (await inFlight).Should().BeTrue("the old connection stays open for its call, then resends uncompressed");
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();

        a.Calls.Select(c => (c.Method, c.Compressed)).Should().Equal(
            ("SendMetricsBatch", true), ("SendMetricsBatch", false));
        ShouldAllCarry(a.Calls, TokenA);
        b.Calls.Select(c => (c.Method, c.Compressed)).Should().Equal(
            ("SendEvent", true), ("SendMetricsBatch", true));
        ShouldAllCarry(b.Calls, TokenB);
        logs.At(LogLevel.Warning).Should().BeEmpty("A's refusal is stale: it neither switches B's compression off nor logs");
    }

    [Fact]
    public async Task BaseUrlChangingBetweenTicketAndConnection_RetakesBoth_AndSendsToTheNewPortalOnly()
    {
        await using var a = await GrpcTestPortal.StartAsync();
        await using var b = await GrpcTestPortal.StartAsync();
        var monitor = new ScriptedOptionsMonitor(Options(a.BaseUrl, TokenA));
        using var client = Create(monitor, new RecordingLoggerProvider());

        // The next read is the compression ticket's (A); right after it the BaseUrl becomes B, so
        // the connection read that follows sees B.
        monitor.AfterNextRead = () => monitor.Value = Options(b.BaseUrl, TokenB);
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeTrue();

        a.Calls.Should().BeEmpty("A's ticket was never paired with B's credentials");
        b.Calls.Select(c => (c.Method, c.Compressed)).Should().Equal(("SendMetricsBatch", true));
        ShouldAllCarry(b.Calls, TokenB);
    }

    [Fact]
    public async Task BaseUrlChangingOnEveryAttempt_FailsTheCallUnsent()
    {
        await using var a = await GrpcTestPortal.StartAsync();
        await using var b = await GrpcTestPortal.StartAsync();
        var optionsA = Options(a.BaseUrl, TokenA);
        var optionsB = Options(b.BaseUrl, TokenB);
        var monitor = new ScriptedOptionsMonitor(optionsA);
        var logs = new RecordingLoggerProvider();
        using var client = Create(monitor, logs);

        // Every read flips the BaseUrl: ticket and connection never agree.
        monitor.OnEveryRead = () => monitor.Value = ReferenceEquals(monitor.Value, optionsA) ? optionsB : optionsA;
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeFalse();
        monitor.OnEveryRead = null;

        a.Calls.Should().BeEmpty();
        b.Calls.Should().BeEmpty();
        Failures(logs).Should().ContainSingle().Which.Message.Should().Contain("not sent").And.Contain("BaseUrl changed");
    }

    [Fact]
    public async Task TlsChange_RebuildsTheChannel_InBothDirections()
    {
        using var certificate = SelfSignedCertificate();
        await using var portal = await GrpcTestPortal.StartAsync(certificate);
        var options = Options(portal.BaseUrl, TokenA);
        options.Tls.RequireTls.Should().BeTrue();
        var monitor = new MutableOptionsMonitor<PortalOptions>(options);
        var logs = new RecordingLoggerProvider();
        using var client = Create(monitor, logs, "Development");

        (await client.AcknowledgeConfigAsync("v1", Ct)).Should().BeFalse("the self-signed certificate is not trusted");

        var untrusted = Options(portal.BaseUrl, TokenA);
        untrusted.Tls.AllowUntrustedCert = true;
        monitor.Set(untrusted);
        (await client.AcknowledgeConfigAsync("v1", Ct)).Should().BeTrue("a new channel skips validation");

        monitor.Set(Options(portal.BaseUrl, TokenA));
        (await client.AcknowledgeConfigAsync("v1", Ct)).Should().BeFalse(
            "a new channel validates again, instead of reusing the open connection");

        portal.Calls.Should().ContainSingle();
        ShouldAllCarry(portal.Calls, TokenA);
    }

    [Fact]
    public async Task TlsChangeThatCannotBeConnectedWith_FailsCallsUnsent_AndNeverFallsBackToTheOldChannel()
    {
        await using var portal = await GrpcTestPortal.StartAsync();
        var monitor = new MutableOptionsMonitor<PortalOptions>(Options(portal.BaseUrl, TokenA));
        var logs = new RecordingLoggerProvider();
        using var client = Create(monitor, logs);
        (await client.AcknowledgeConfigAsync("v1", Ct)).Should().BeTrue();

        var requireTls = Options(portal.BaseUrl, TokenB);
        requireTls.Tls.RequireTls = true; // with an http:// BaseUrl
        monitor.Set(requireTls);
        (await client.AcknowledgeConfigAsync("v1", Ct)).Should().BeFalse();
        (await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct)).Should().BeFalse();

        var untrustedInProduction = Options(portal.BaseUrl, TokenB);
        untrustedInProduction.Tls.AllowUntrustedCert = true;
        monitor.Set(untrustedInProduction);
        (await client.PollCommandsAsync(Ct)).Should().BeEmpty();

        monitor.Set(Options(portal.BaseUrl, TokenB));
        (await client.AcknowledgeConfigAsync("v1", Ct)).Should().BeTrue("a valid configuration connects again");

        portal.Calls.Select(c => c.Authorization).Should().Equal($"Bearer {TokenA}", $"Bearer {TokenB}");
        var notSent = Failures(logs).Select(e => e.Message).ToList();
        notSent.Should().HaveCount(3).And.OnlyContain(m => m.Contains("not sent"));
        notSent[0].Should().Contain("must use HTTPS");
        notSent[2].Should().Contain("AllowUntrustedCert must not be enabled in Production");
    }

    [Fact]
    public async Task ConcurrentCallsDuringSwitches_NeverMixPortalsAndCredentials_AndOldChannelsAreDisposedWhenIdle()
    {
        await using var a = await GrpcTestPortal.StartAsync();
        await using var b = await GrpcTestPortal.StartAsync();
        a.OnCall = b.OnCall = _ => Task.Delay(Random.Shared.Next(0, 15), Ct);
        var optionsA = Options(a.BaseUrl, TokenA);
        var optionsB = Options(b.BaseUrl, TokenB);
        var monitor = new MutableOptionsMonitor<PortalOptions>(optionsA);
        var logs = new RecordingLoggerProvider();
        var handlers = new List<TrackingHandler>();
        var client = new GrpcPortalClient(monitor, Environment("Production"), logs.CreateLogger<GrpcPortalClient>(),
            _ =>
            {
                var handler = new TrackingHandler(new SocketsHttpHandler());
                lock (handlers)
                    handlers.Add(handler);
                return handler;
            });

        // Fetches are made too, but their result can't tell a failure from "no change": only the
        // others are counted.
        var calls = new List<Task<bool>>();
        var fetches = new List<Task>();
        for (var i = 0; i < 120; i++)
        {
            var n = i;
            if (n % 4 == 3)
                fetches.Add(Task.Run(() => client.FetchConfigurationAsync("v1", Ct), Ct));
            else
            {
                calls.Add(Task.Run(async () => (n % 4) switch
                {
                    0 => await client.SendMetricsBatchAsync(AgentPayloads.FullBatch(), Ct),
                    1 => await client.SendEventAsync(AgentPayloads.FullEvent(), Ct),
                    _ => await client.AcknowledgeConfigAsync("v1", Ct),
                }, Ct));
            }
            if (i % 10 == 9)
            {
                monitor.Set(i / 10 % 2 == 0 ? optionsB : optionsA);
                await Task.Delay(2, Ct);
            }
        }
        var results = await Task.WhenAll(calls);
        await Task.WhenAll(fetches);

        ShouldAllCarry(a.Calls, TokenA);
        ShouldAllCarry(b.Calls, TokenB);
        a.Calls.Should().NotBeEmpty();
        b.Calls.Should().NotBeEmpty();
        // A call fails only unsent (the BaseUrl changed twice while it was being prepared), never
        // because its channel was disposed under it.
        Failures(logs).Should().OnlyContain(e => e.Message.Contains("not sent"));
        results.Count(r => r).Should().Be(a.Calls.Concat(b.Calls).Count(c => c.Method != "FetchConfig"));

        lock (handlers)
        {
            handlers.Should().HaveCountGreaterThan(1);
            handlers.Should().OnlyContain(h => !h.SentAfterDispose);
            handlers.SkipLast(1).Should().OnlyContain(h => h.Disposed, "a replaced channel is disposed once idle");
            handlers[^1].Disposed.Should().BeFalse("the current channel stays open");
        }
        client.Dispose();
        lock (handlers)
            handlers[^1].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task CallsAfterDispose_Throw_AndACallRunningAtDispose_Finishes()
    {
        await using var a = await GrpcTestPortal.StartAsync();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.OnCall = async _ =>
        {
            arrived.TrySetResult();
            await release.Task;
        };
        var client = Create(new MutableOptionsMonitor<PortalOptions>(Options(a.BaseUrl, TokenA)), new RecordingLoggerProvider());

        var inFlight = client.AcknowledgeConfigAsync("v1", Ct);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        client.Dispose();
        release.SetResult();

        (await inFlight).Should().BeTrue();
        await FluentActions.Awaiting(() => client.AcknowledgeConfigAsync("v1", Ct)).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Theory]
    [InlineData("https://portal.example", "https://portal.example/watchtower.portal.v1.WatchTowerPortalService/SendEvent", true)]
    [InlineData("https://portal.example/prefix/", "https://portal.example/watchtower.portal.v1.WatchTowerPortalService/SendEvent", true)]
    [InlineData("https://portal.example:443", "https://PORTAL.example/x", true)]
    [InlineData("https://portal.example", "http://portal.example/x", false)]
    [InlineData("https://portal.example", "https://portal.example:8443/x", false)]
    [InlineData("https://portal-a.example", "https://portal-b.example/x", false)]
    [InlineData("https://portal.example", "https://portal.example.evil.example/x", false)]
    [InlineData("", "https://portal.example/x", false)]
    public void CredentialGuard_AcceptsOnlyTheSnapshotsOrigin(string baseUrl, string request, bool expected) =>
        GrpcPortalClient.IsForOrigin(new Uri(request), baseUrl).Should().Be(expected);

    private static X509Certificate2 SelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // server authentication
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // SChannel needs a key it can use for the server side of the handshake: round-trip via PFX.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    /// <summary>An options monitor that runs a hook on reads, to place a change between two reads of one call.</summary>
    private sealed class ScriptedOptionsMonitor(PortalOptions value) : IOptionsMonitor<PortalOptions>
    {
        public PortalOptions Value { get; set; } = value;

        /// <summary>Runs once, right after the next read.</summary>
        public Action? AfterNextRead { get; set; }

        /// <summary>Runs after every read while set.</summary>
        public Action? OnEveryRead { get; set; }

        public PortalOptions CurrentValue
        {
            get
            {
                var value = Value;
                var once = AfterNextRead;
                AfterNextRead = null;
                once?.Invoke();
                OnEveryRead?.Invoke();
                return value;
            }
        }

        public PortalOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<PortalOptions, string?> listener) => null;
    }

    /// <summary>Records whether the channel disposed it, and whether anything was sent through it afterwards.</summary>
    private sealed class TrackingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public volatile bool Disposed;
        public volatile bool SentAfterDispose;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Disposed)
                SentAfterDispose = true;
            return base.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// A real HTTP/2 server (Kestrel; h2c, or TLS with a given certificate) answering the seven unary
/// WatchTowerPortalService methods with success. It records each call's metadata and the identity
/// in its message, and refuses gzip-compressed messages like ASP.NET Core gRPC when
/// <see cref="AcceptsGzip"/> is false.
/// </summary>
internal sealed class GrpcTestPortal : IAsyncDisposable
{
    public sealed record Call(
        string Method, string? Authorization, string? AgentId, string? Encoding, bool Compressed, string IdentityToken);

    private readonly WebApplication _app;
    private readonly List<Call> _calls = [];

    private GrpcTestPortal(WebApplication app) => _app = app;

    public volatile bool AcceptsGzip = true;

    /// <summary>Runs after a call is recorded, before it is answered.</summary>
    public Func<Call, Task>? OnCall { get; set; }

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_calls)
                return _calls.ToList();
        }
    }

    public string BaseUrl => _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    public static async Task<GrpcTestPortal> StartAsync(X509Certificate2? certificate = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o =>
        {
            o.Protocols = HttpProtocols.Http2;
            if (certificate is not null)
                o.UseHttps(certificate);
        }));
        var app = builder.Build();
        var portal = new GrpcTestPortal(app);
        app.MapPost("/watchtower.portal.v1.WatchTowerPortalService/{method}", portal.HandleAsync);
        await app.StartAsync();
        return portal;
    }

    private async Task HandleAsync(HttpContext context, string method)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        var frame = body.ToArray(); // 1 byte compressed flag, 4 bytes big-endian length, message
        var compressed = frame[0] == 1;
        var message = frame.AsSpan(5, BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(1, 4))).ToArray();
        if (compressed)
        {
            using var gzip = new GZipStream(new MemoryStream(message), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            await gzip.CopyToAsync(plain, context.RequestAborted);
            message = plain.ToArray();
        }

        // Every request message carries AgentIdentity as field 1.
        var identity = PollCommandsRequest.Parser.ParseFrom(message).Identity;
        var headers = context.Request.Headers;
        var call = new Call(method, headers.Authorization.FirstOrDefault(), headers["x-agent-id"].FirstOrDefault(),
            headers["grpc-encoding"].FirstOrDefault(), compressed, identity?.AgentToken ?? "");
        lock (_calls)
            _calls.Add(call);
        if (OnCall is { } onCall)
            await onCall(call);

        context.Response.ContentType = "application/grpc";
        if (compressed && !AcceptsGzip)
        {
            // What ASP.NET Core gRPC answers: trailers-only UNIMPLEMENTED naming the encoding.
            context.Response.Headers["grpc-status"] = "12";
            context.Response.Headers["grpc-message"] = "Unsupported grpc-encoding value 'gzip'. Supported encodings: identity";
            return;
        }

        // accepted / ok = true is field 1 set; PollCommands and FetchConfig answer "nothing".
        byte[] reply = method is "PollCommands" or "FetchConfig" ? [] : [0x08, 0x01];
        var output = new byte[5 + reply.Length];
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(1, 4), reply.Length);
        reply.CopyTo(output, 5);
        await context.Response.Body.WriteAsync(output, context.RequestAborted);
        context.Response.AppendTrailer("grpc-status", "0");
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}