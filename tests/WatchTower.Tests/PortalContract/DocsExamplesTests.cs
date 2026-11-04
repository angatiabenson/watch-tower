using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

using AwesomeAssertions;

using WatchTower.Portal;

namespace WatchTower.Tests.PortalContract;

/// <summary>
/// (d, guides part) Every example in <c>guides/portal-api</c> matches the spec, so the guides cannot
/// drift away from it.
/// </summary>
/// <remarks>
/// <para>Labelling convention (documented in <c>guides/portal-api/11-testing-your-portal.md</c>):</para>
/// <list type="bullet">
/// <item><c>&lt;!-- schema: Name --&gt;</c> or <c>&lt;!-- schema: Name[] --&gt;</c> on the line before a
/// <c>```json</c> block (blank lines in between are fine): the block must be valid JSON and must validate in
/// strict mode against <c>components/schemas/Name</c> (or an array of it).</item>
/// <item><c>&lt;!-- schema: none (reason) --&gt;</c>: the explicit opt-out for JSON that is not an API
/// payload (settings files, admin APIs). The block must still be valid JSON, and the reason is mandatory.</item>
/// <item>Every <c>```json</c> block needs one of the two. A label before anything else is an error.</item>
/// <item><c>```http</c> blocks need no label: a request line must be a spec operation with the headers the
/// agent really sends, a status line must be a valid status. A body after the headers needs a label like a
/// JSON block.</item>
/// </list>
/// </remarks>
public partial class DocsExamplesTests
{
    public sealed record DocBlock(string File, int Line, string Language, string Content, string? Label)
    {
        public string Name => $"{File}:{Line}";
    }

    [GeneratedRegex(@"^\s*<!--\s*schema:\s*(?<label>.*?)\s*-->\s*$")]
    private static partial Regex LabelLine();

    [GeneratedRegex(@"^(?<indent>\s*)(?<fence>`{3,}|~{3,})\s*(?<lang>[^\s`]*)")]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"^none \((?<reason>.+)\)$")]
    private static partial Regex OptOut();

    [GeneratedRegex(@"WT1\.(?<payload>[A-Za-z0-9_-]{20,})(?<truncated>…|\.\.\.)?")]
    private static partial Regex EnrollmentCodeText();

    private static readonly Lazy<(List<DocBlock> Blocks, List<string> Errors)> Parsed = new(ParseGuides);

    public static TheoryData<string> JsonBlocks() =>
        new(Parsed.Value.Blocks.Where(b => b.Language == "json").Select(b => b.Name));

    public static TheoryData<string> HttpBlocks() =>
        new(Parsed.Value.Blocks.Where(b => b.Language == "http").Select(b => b.Name));

    [Fact]
    public void TheGuides_AreFoundAndEveryLabelIsPlacedCorrectly()
    {
        Directory.GetFiles(PortalSpec.GuidesDirectory, "*.md", SearchOption.AllDirectories).Should().HaveCountGreaterThan(10);
        Parsed.Value.Blocks.Count(b => b.Language == "json").Should().BeGreaterThan(20);
        Parsed.Value.Errors.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(JsonBlocks))]
    public void JsonExamples_ValidateAgainstTheirLabelledSchema(string name)
    {
        var block = Parsed.Value.Blocks.Single(b => b.Name == name);
        block.Label.Should().NotBeNull($"{name}: label the block with <!-- schema: Name --> or <!-- schema: none (reason) -->");

        CheckBody(block, block.Content);
    }

    [Theory]
    [MemberData(nameof(HttpBlocks))]
    public async Task HttpExamples_AreSpecOperationsWithTheHeadersTheAgentSends(string name)
    {
        var block = Parsed.Value.Blocks.Single(b => b.Name == name);
        var (head, body) = SplitHttp(block.Content);
        var start = head[0];

        var status = Regex.Match(start, @"^HTTP/1\.1 (?<code>\d{3}) [A-Za-z ]+$");
        if (status.Success)
        {
            int.Parse(status.Groups["code"].Value).Should().BeInRange(100, 599, name);
        }
        else
        {
            var request = Regex.Match(start, @"^(?<method>[A-Z]+) (?<target>/\S*) HTTP/1\.1$");
            request.Success.Should().BeTrue($"{name}: '{start}' is neither a request line nor a status line");
            var method = request.Groups["method"].Value;
            var target = request.Groups["target"].Value;
            var path = target.Split('?')[0];
            var operation = PortalSpec.Operations.SingleOrDefault(o => o.Matches(method, path));
            operation.Should().NotBeNull($"{name}: {method} {path} is not an operation of the spec");

            var query = HttpUtility.ParseQueryString(target.Contains('?') ? target[target.IndexOf('?')..] : "");
            var declared = (operation!.Operation["parameters"]?.AsArray() ?? [])
                .Select(p => PortalSpec.Resolve(p!)).Where(p => (string)p["in"]! == "query").Select(p => (string)p["name"]!);
            query.AllKeys.Should().BeSubsetOf(declared, name);

            var headers = head.Skip(1).Select(h => h.Split(':', 2)).ToDictionary(h => h[0].Trim(), h => h[1].Trim(), StringComparer.OrdinalIgnoreCase);
            headers.Should().ContainKey("Authorization").WhoseValue.Should().StartWith("Bearer ", name);
            headers.Should().ContainKey("X-Agent-Id", name);

            // Compare the documented body headers with a real request of that operation.
            var sent = await RecordAsync(operation);
            if (sent.ContentType is null)
            {
                headers.Should().NotContainKey("Content-Type", $"{name}: the agent sends no body on {operation}");
            }
            else
            {
                headers.Should().ContainKey("Content-Type").WhoseValue.Should().Be(sent.ContentType, name);
                if (headers.ContainsKey("Transfer-Encoding") || headers.ContainsKey("Content-Length"))
                {
                    headers.ContainsKey("Transfer-Encoding").Should().Be(sent.ContentLength is null,
                        $"{name}: the agent streams its JSON body (chunked) without a Content-Length");
                }
            }
        }

        if (body.Length > 0)
        {
            block.Label.Should().NotBeNull($"{name} has a body: label it with <!-- schema: Name -->");
            CheckBody(block, body);
        }
    }

    [Fact]
    public void EnrollmentCodes_InTheGuides_DecodeWithTheAgentsParser()
    {
        var found = 0;
        foreach (var file in Directory.GetFiles(PortalSpec.GuidesDirectory, "*.md", SearchOption.AllDirectories))
        {
            foreach (Match match in EnrollmentCodeText().Matches(File.ReadAllText(file)))
            {
                if (match.Groups["truncated"].Success)
                    continue; // "WT1.eyJ2…" placeholders
                found++;
                var code = "WT1." + match.Groups["payload"].Value;
                EnrollmentCode.TryParse(code, out _, out var error).Should().BeTrue($"{Relative(file)}: {code}: {error}");

                var payload = Encoding.UTF8.GetString(Base64Url(match.Groups["payload"].Value));
                PortalSpec.Validate("EnrollmentCodePayload", payload, SchemaMode.Strict).Should().BeEmpty($"{Relative(file)}: {payload}");
            }
        }
        found.Should().BePositive();
    }

    [Fact]
    public void TheChecker_RejectsABrokenExample()
    {
        // The safety net itself: a typo'd property and an unknown action type must fail.
        var errors = PortalSpec.Validate("AgentCommand[]",
            """[{"commandId":"c1","actionType":777,"paramters":{},"issuedAt":"2026-11-03T10:40:00Z"}]""", SchemaMode.Strict);

        errors.Should().Contain(e => e.StartsWith("/0/actionType")).And.Contain(e => e.Contains("paramters") || e.Contains("additionalProperties"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static void CheckBody(DocBlock block, string content)
    {
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new Xunit.Sdk.XunitException($"{block.Name}: not valid JSON ({ex.Message}). Use another fence language for pseudo-JSON.");
        }

        var optOut = OptOut().Match(block.Label!);
        if (optOut.Success)
            return;
        block.Label.Should().NotStartWith("none", $"{block.Name}: an opt-out needs a reason: <!-- schema: none (reason) -->");

        PortalSpec.Validate(block.Label!, json!.ToJsonString(), SchemaMode.Strict)
            .Should().BeEmpty($"{block.Name} must match components/schemas/{block.Label}");
    }

    private static (string[] Head, string Body) SplitHttp(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var blank = Array.FindIndex(lines, string.IsNullOrWhiteSpace);
        var head = (blank < 0 ? lines : lines[..blank]).ToArray();
        var body = blank < 0 ? "" : string.Join('\n', lines[(blank + 1)..]).Trim();
        return (head, body);
    }

    private static async Task<RecordedRequest> RecordAsync(SpecOperation operation)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = AgentClientHarness.CreateClient(AgentClientHarness.Options("https://portal.example.com"), () => handler);
        Task call = operation.OperationId switch
        {
            "sendMetricsBatch" => client.SendMetricsBatchAsync(AgentPayloads.MinimalBatch(), default),
            "sendEvent" => client.SendEventAsync(AgentPayloads.MinimalEvent(), default),
            "pollCommands" => client.PollCommandsAsync(default),
            "acknowledgeCommand" => client.AcknowledgeCommandAsync(AgentPayloads.Ack("c1"), default),
            "reportCommandResult" => client.ReportCommandResultAsync(AgentPayloads.Result("c1"), default),
            "fetchConfig" => client.FetchConfigurationAsync("v1", default),
            "acknowledgeConfig" => client.AcknowledgeConfigAsync("v1", default),
            _ => throw new InvalidOperationException($"No agent call for {operation.OperationId}"),
        };
        await call;
        return handler.Requests.Single();
    }

    private static (List<DocBlock>, List<string>) ParseGuides()
    {
        var blocks = new List<DocBlock>();
        var errors = new List<string>();
        foreach (var file in Directory.GetFiles(PortalSpec.GuidesDirectory, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var name = Relative(file);
            var lines = File.ReadAllLines(file);
            string? pendingLabel = null;
            var labelLine = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var fence = FenceLine().Match(line);
                if (fence.Success)
                {
                    var marker = fence.Groups["fence"].Value;
                    var language = fence.Groups["lang"].Value.ToLowerInvariant();
                    var content = new StringBuilder();
                    var start = i + 1;
                    for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(marker, StringComparison.Ordinal); i++)
                        content.AppendLine(lines[i]);
                    if (i >= lines.Length)
                        errors.Add($"{name}:{start}: unclosed code fence");

                    if (pendingLabel is not null && language is not ("json" or "http"))
                        errors.Add($"{name}:{labelLine}: a schema label must be followed by a ```json or ```http block, not ```{language}");
                    blocks.Add(new DocBlock(name, start, language, content.ToString(), pendingLabel));
                    pendingLabel = null;
                    continue;
                }

                var label = LabelLine().Match(line);
                if (label.Success)
                {
                    if (pendingLabel is not null)
                        errors.Add($"{name}:{labelLine}: schema label without a code block");
                    pendingLabel = label.Groups["label"].Value;
                    labelLine = i + 1;
                    continue;
                }
                if (pendingLabel is not null && !string.IsNullOrWhiteSpace(line))
                {
                    errors.Add($"{name}:{labelLine}: schema label without a code block");
                    pendingLabel = null;
                }
            }
            if (pendingLabel is not null)
                errors.Add($"{name}:{labelLine}: schema label without a code block");
        }
        return (blocks, errors);
    }

    private static string Relative(string file) =>
        PortalSpec.GuidesRelativePath + "/" + Path.GetRelativePath(PortalSpec.GuidesDirectory, file).Replace('\\', '/');

    private static byte[] Base64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
    }
}