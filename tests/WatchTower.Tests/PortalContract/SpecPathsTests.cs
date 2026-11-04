using System.Net;
using System.Web;

using AwesomeAssertions;

using WatchTower.Portal.Clients;

namespace WatchTower.Tests.PortalContract;

/// <summary>
/// (b) The <c>(method, path)</c> pairs the agent's real HTTPS client calls are exactly the spec's
/// operations: no call the spec does not document, no documented operation the agent never makes.
/// </summary>
public class SpecPathsTests
{
    private const string CommandId = "6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10";

    /// <summary>Calls every method of <see cref="IPortalClient"/> once (twice for GET config: with and without a version).</summary>
    private static async Task<IReadOnlyList<RecordedRequest>> RecordEveryCallAsync(string baseUrl, string commandId = CommandId)
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/commands/pending", StringComparison.Ordinal) => RecordingHandler.Json("[]"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });
        var client = AgentClientHarness.CreateClient(AgentClientHarness.Options(baseUrl), () => handler);

        await client.SendMetricsBatchAsync(AgentPayloads.MinimalBatch(), default);
        await client.SendEventAsync(AgentPayloads.MinimalEvent(), default);
        await client.PollCommandsAsync(default);
        await client.AcknowledgeCommandAsync(AgentPayloads.Ack(commandId), default);
        await client.ReportCommandResultAsync(AgentPayloads.Result(commandId), default);
        await client.FetchConfigurationAsync(null, default);
        await client.FetchConfigurationAsync("2026-11-03.4", default);
        await client.AcknowledgeConfigAsync("2026-11-03.4", default);
        return handler.Requests;
    }

    [Fact]
    public void TheClientInterface_HasOneMethodPerSpecOperation()
    {
        // A new IPortalClient method means a new call: it must come with a spec operation (and a case
        // in RecordEveryCallAsync).
        typeof(IPortalClient).GetMethods().Should().HaveCount(PortalSpec.Operations.Count);
    }

    [Theory]
    [InlineData("https://portal.example.com", "")]
    [InlineData("https://portal.example.com/", "")]
    [InlineData("https://portal.example.com/watchtower", "/watchtower")]
    [InlineData("https://portal.example.com/tenants/acme//", "/tenants/acme")]
    public async Task ClientCalls_EqualTheSpecOperations(string baseUrl, string prefix)
    {
        var requests = await RecordEveryCallAsync(baseUrl);

        var called = new HashSet<SpecOperation>();
        foreach (var request in requests)
        {
            request.Uri.GetLeftPart(UriPartial.Authority).Should().Be("https://portal.example.com");
            request.RawPath.Should().StartWith(prefix + "/api/v1/agent/", "the agent appends the API path to BaseUrl");
            var path = request.RawPath[prefix.Length..];
            var matches = PortalSpec.Operations.Where(o => o.Matches(request.Method, path)).ToList();
            matches.Should().ContainSingle($"{request.Method} {path} must be exactly one spec operation");
            called.Add(matches[0]);
        }

        called.Select(o => o.ToString()).Should().BeEquivalentTo(PortalSpec.Operations.Select(o => o.ToString()));
    }

    [Fact]
    public async Task EveryRequest_CarriesTheDocumentedCredentialsAndOnlyDocumentedQueryParameters()
    {
        var requests = await RecordEveryCallAsync("https://portal.example.com");

        // Security requirement: bearer token (agentToken) AND X-Agent-Id (agentId) on every request.
        var schemes = PortalSpec.Document["components"]!["securitySchemes"]!;
        schemes["agentToken"]!["scheme"]!.GetValue<string>().Should().Be("bearer");
        var idHeader = schemes["agentId"]!["name"]!.GetValue<string>();

        foreach (var request in requests)
        {
            request.Headers["Authorization"].Should().Be($"Bearer {AgentClientHarness.AgentToken}");
            request.Headers[idHeader].Should().Be(AgentClientHarness.AgentId);

            var operation = PortalSpec.Operations.Single(o => o.Matches(request.Method, request.RawPath));
            var declared = (operation.Operation["parameters"]?.AsArray() ?? [])
                .Select(p => PortalSpec.Resolve(p!))
                .Where(p => (string)p["in"]! == "query")
                .Select(p => (string)p["name"]!)
                .ToHashSet();
            var query = HttpUtility.ParseQueryString(request.Uri.Query);
            query.AllKeys.Should().BeSubsetOf(declared, $"{operation} documents these query parameters");

            if (operation.RequestSchema is null)
                request.Body.Should().BeNull($"{operation} has no request body");
            else
                request.Body.Should().NotBeNullOrEmpty($"{operation} has a request body");
        }

        requests.Where(r => r.Uri.AbsolutePath.EndsWith("/config", StringComparison.Ordinal))
            .Select(r => HttpUtility.ParseQueryString(r.Uri.Query)["version"])
            .Should().Equal(new string?[] { null, "2026-11-03.4" });
    }

    [Theory]
    [InlineData("cmd/with slash", "cmd%2Fwith%20slash")]
    [InlineData("a?b#c", "a%3Fb%23c")]
    [InlineData("café-1", "caf%C3%A9-1")]
    [InlineData("A-Z_a.z~0", "A-Z_a.z~0")]
    public async Task CommandIds_ArePercentEncodedIntoOnePathSegment(string commandId, string encoded)
    {
        // The spec's CommandId parameter: percent-encoded, RFC 3986 unreserved characters left as is.
        var requests = await RecordEveryCallAsync("https://portal.example.com", commandId);

        var commandCalls = requests.Where(r => r.RawPath.Contains("/commands/", StringComparison.Ordinal) &&
                                               !r.RawPath.EndsWith("/pending", StringComparison.Ordinal)).ToList();
        commandCalls.Select(r => r.RawPath).Should().Equal(
            $"/api/v1/agent/commands/{encoded}/acknowledge",
            $"/api/v1/agent/commands/{encoded}/result");
        foreach (var call in commandCalls)
            PortalSpec.Operations.Should().ContainSingle(o => o.Matches(call.Method, call.RawPath));
    }
}