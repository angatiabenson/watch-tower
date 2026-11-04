using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

using AwesomeAssertions;

using Google.Protobuf;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Moq;

using WatchTower.Configuration;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts.Protobuf;
using WatchTower.Tests.Hosting;
using WatchTower.Tests.PortalContract;

namespace WatchTower.Tests.Portal;

/// <summary>
/// <c>CompressRequests=Gzip</c> on the gRPC transport: gRPC's own message compression
/// (<c>grpc-encoding: gzip</c>), and the fallback when the server answers UNIMPLEMENTED for it.
/// The server is a fake HTTP/2 handler speaking the gRPC framing.
/// </summary>
public class GrpcCompressionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (GrpcPortalClient Client, FakeGrpcServer Server, RecordingLoggerProvider Logs) Create(
        string compress, bool serverAcceptsGzip)
    {
        var options = new PortalOptions
        {
            Enabled = true,
            Protocol = "Grpc",
            BaseUrl = "https://portal.example.com",
            AgentId = "agent",
            AgentToken = "token",
            CompressRequests = compress,
        };
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns(Environments.Production);
        var server = new FakeGrpcServer(serverAcceptsGzip);
        var logs = new RecordingLoggerProvider();
        var client = new GrpcPortalClient(new StaticOptionsMonitor<PortalOptions>(options), env.Object,
            logs.CreateLogger<GrpcPortalClient>(), _ => server);
        return (client, server, logs);
    }

    [Fact]
    public async Task Gzip_CompressesBatchesAndEvents_ButNotTheSmallCalls()
    {
        var (client, server, logs) = Create(RequestCompressionModes.Gzip, serverAcceptsGzip: true);
        using var _ = client;

        (await client.SendMetricsBatchAsync(PortalContract.AgentPayloads.FullBatch(), Ct)).Should().BeTrue();
        (await client.SendEventAsync(PortalContract.AgentPayloads.FullEvent(), Ct)).Should().BeTrue();
        (await client.AcknowledgeConfigAsync("v1", Ct)).Should().BeTrue();

        server.Calls.Select(c => (c.Method, c.Encoding, c.CompressedFlag)).Should().Equal(
            ("SendMetricsBatch", "gzip", true),
            ("SendEvent", "gzip", true),
            ("AcknowledgeConfig", null, false));
        MetricsBatchRequest.Parser.ParseFrom(server.Calls[0].Message).Batch.BatchId
            .Should().Be(PortalContract.AgentPayloads.FullBatch().BatchId);
        logs.At(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task Off_SendsEverythingUncompressed()
    {
        var (client, server, _) = Create(RequestCompressionModes.Off, serverAcceptsGzip: true);
        using var _ = client;

        (await client.SendMetricsBatchAsync(PortalContract.AgentPayloads.FullBatch(), Ct)).Should().BeTrue();

        server.Calls.Should().ContainSingle().Which.Encoding.Should().BeNull();
    }

    [Fact]
    public async Task ServerWithoutGzip_AnswersUnimplemented_AndTheCallIsResentUncompressedOnce()
    {
        var (client, server, logs) = Create(RequestCompressionModes.Gzip, serverAcceptsGzip: false);
        using var _ = client;

        (await client.SendMetricsBatchAsync(PortalContract.AgentPayloads.FullBatch(), Ct)).Should().BeTrue();
        (await client.SendEventAsync(PortalContract.AgentPayloads.FullEvent(), Ct)).Should().BeTrue();

        server.Calls.Select(c => (c.Method, c.Encoding)).Should().Equal(
            ("SendMetricsBatch", "gzip"),
            ("SendMetricsBatch", null),
            ("SendEvent", null));
        logs.At(LogLevel.Warning).Where(e => e.Message.Contains(RequestCompression.FallbackWarning))
            .Should().ContainSingle().Which.Message.Should().Contain("Unimplemented");
    }

    /// <summary>A gRPC server stand-in for unary calls: decodes the request frame and answers success.</summary>
    private sealed class FakeGrpcServer(bool acceptsGzip) : HttpMessageHandler
    {
        public sealed record Call(string Method, string? Encoding, bool CompressedFlag, byte[] Message);

        private readonly List<Call> _calls = new();

        public IReadOnlyList<Call> Calls
        {
            get
            {
                lock (_calls)
                    return _calls.ToList();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri!.Segments[^1];
            var encoding = request.Headers.TryGetValues("grpc-encoding", out var values) ? values.Single() : null;
            var frame = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var compressed = frame[0] == 1;
            var length = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(1, 4));
            var message = frame.AsSpan(5, (int)length).ToArray();
            if (compressed)
            {
                using var gzip = new GZipStream(new MemoryStream(message), CompressionMode.Decompress);
                using var plain = new MemoryStream();
                await gzip.CopyToAsync(plain, cancellationToken);
                message = plain.ToArray();
            }
            lock (_calls)
                _calls.Add(new Call(method, encoding, compressed, message));

            if (encoding is not null && !(acceptsGzip && encoding == "gzip"))
            {
                // What ASP.NET Core gRPC answers: trailers-only UNIMPLEMENTED naming the encoding.
                var refusal = Response([]);
                refusal.Headers.Add("grpc-status", "12");
                refusal.Headers.Add("grpc-message", $"Unsupported grpc-encoding value '{encoding}'. Supported encodings: identity");
                refusal.Headers.Add("grpc-accept-encoding", "identity");
                return refusal;
            }

            IMessage reply = method switch
            {
                "SendMetricsBatch" => new MetricsBatchResponse { Accepted = true },
                "SendEvent" => new AgentEventResponse { Accepted = true },
                "AcknowledgeConfig" => new AckConfigResponse { Ok = true },
                _ => throw new NotSupportedException(method),
            };
            var bytes = reply.ToByteArray();
            var body = new byte[5 + bytes.Length];
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1, 4), (uint)bytes.Length);
            bytes.CopyTo(body, 5);
            var response = Response(body);
            response.TrailingHeaders.Add("grpc-status", "0");
            return response;
        }

        private static HttpResponseMessage Response(byte[] body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                Content = new ByteArrayContent(body),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            return response;
        }
    }
}