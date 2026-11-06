using System.Text.Json;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using WatchTower.Portal.Actions;
using WatchTower.Portal.Contracts;

namespace WatchTower.Tests.PortalContract;

/// <summary>
/// (d, spec part) Every example in the spec validates against the schema it illustrates, in strict
/// mode (documented properties and known enum values only), and the agent can parse the examples a
/// portal would send.
/// </summary>
public class SpecExamplesTests
{
    /// <summary>One example value and the schema it belongs to.</summary>
    public sealed record SpecExample(string Location, JsonNode Schema, JsonNode? Value, string? ComponentExample);

    public static IReadOnlyList<SpecExample> MediaTypeExamples()
    {
        var examples = new List<SpecExample>();
        foreach (var operation in PortalSpec.Operations)
        {
            var contents = new List<(string Where, JsonNode Content)>();
            if (operation.Operation["requestBody"]?["content"] is JsonObject request)
                contents.AddRange(request.Select(c => ($"{operation} request {c.Key}", c.Value!)));
            foreach (var (status, response) in operation.Operation["responses"]!.AsObject())
            {
                if (PortalSpec.Resolve(response!)["content"] is JsonObject content)
                    contents.AddRange(content.Select(c => ($"{operation} {status} {c.Key}", c.Value!)));
            }

            foreach (var (where, content) in contents)
            {
                var schema = content["schema"] ?? throw new InvalidOperationException($"{where} has no schema");
                if (content["example"] is { } single)
                    examples.Add(new SpecExample($"{where} example", schema, single, null));
                foreach (var (name, example) in content["examples"]?.AsObject() ?? [])
                {
                    var reference = (string?)example!["$ref"];
                    var resolved = PortalSpec.Resolve(example);
                    examples.Add(new SpecExample($"{where} examples.{name}", schema, resolved["value"],
                        reference?.Split('/').Last()));
                }
            }
        }
        return examples;
    }

    public static TheoryData<string> MediaTypeExampleNames() => new(MediaTypeExamples().Select(e => e.Location));

    [Theory]
    [MemberData(nameof(MediaTypeExampleNames))]
    public void RequestAndResponseExamples_ValidateStrictly(string location)
    {
        var example = MediaTypeExamples().Single(e => e.Location == location);
        example.Value.Should().NotBeNull($"{location} needs a value");

        PortalSpec.Validate(example.Schema, example.Value!.ToJsonString(), SchemaMode.Strict)
            .Should().BeEmpty($"{location} must match its schema");
    }

    [Fact]
    public void EveryComponentExample_IsUsedByAnOperation()
    {
        // An example no operation refers to has no schema to be checked against.
        var used = MediaTypeExamples().Select(e => e.ComponentExample).OfType<string>().ToHashSet();
        var defined = PortalSpec.Document["components"]!["examples"]!.AsObject().Select(e => e.Key);

        defined.Should().BeSubsetOf(used);
    }

    [Fact]
    public void SchemaAndParameterExamples_ValidateStrictly()
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var (pointer, schema) in PortalSpec.WalkSchemas(PortalSpec.Schemas, "#/components/schemas", isMap: true))
        {
            var values = (schema["examples"] as JsonArray)?.ToList() ?? [];
            if (schema["example"] is { } single)
                values.Add(single);
            foreach (var value in values)
            {
                count++;
                failures.AddRange(PortalSpec.Validate(schema, value!.ToJsonString(), SchemaMode.Strict).Select(e => $"{pointer}: {e}"));
            }
        }

        var parameters = PortalSpec.Operations
            .SelectMany(o => o.Operation["parameters"]?.AsArray() ?? [])
            .Concat(PortalSpec.Document["components"]!["parameters"]!.AsObject().Select(p => p.Value))
            .Select(p => PortalSpec.Resolve(p!))
            .Distinct();
        foreach (var parameter in parameters)
        {
            if (parameter["example"] is not { } value)
                continue;
            count++;
            failures.AddRange(PortalSpec.Validate(parameter["schema"]!, value.ToJsonString(), SchemaMode.Strict)
                .Select(e => $"parameter {parameter["name"]}: {e}"));
        }

        count.Should().BePositive();
        failures.Should().BeEmpty();
    }

    [Fact]
    public void TheAgent_ParsesThePendingCommandsExample()
    {
        var value = PortalSpec.Pointer("#/components/examples/PendingCommands/value").ToJsonString();

        var commands = JsonSerializer.Deserialize<List<AgentCommandDto>>(value, AgentClientHarness.WireOptions)!;

        commands.Should().HaveCount(3).And.OnlyContain(c => !string.IsNullOrWhiteSpace(c.CommandId) && c.IssuedAt != null);
        commands.Select(c => (ActionType)c.ActionType).Should().OnlyContain(a => Enum.IsDefined(a));
        commands[0].Parameters.Should().Contain("ServiceName", "W3SVC").And.Contain("TimeoutSeconds", "60");
        commands[2].ScheduledAt.Should().Be("2026-11-03T23:00:00Z");
    }

    [Theory]
    [InlineData("ConfigThresholds")]
    [InlineData("ConfigMaintenance")]
    [InlineData("ConfigWaitingPeriods")]
    [InlineData("ConfigEmpty")]
    public void TheAgent_ParsesAndAcceptsTheConfigExamples(string example)
    {
        var value = PortalSpec.Pointer($"#/components/examples/{example}/value").ToJsonString();

        var wrapper = JsonSerializer.Deserialize(value, AgentClientHarness.ConfigFetchResponseType, AgentClientHarness.WireOptions)!;
        var config = (RemoteAgentConfigDto?)AgentClientHarness.ConfigFetchResponseType.GetProperty("Config")!.GetValue(wrapper);
        var version = (string?)AgentClientHarness.ConfigFetchResponseType.GetProperty("Version")!.GetValue(wrapper);

        config.Should().NotBeNull();
        version.Should().NotBeNullOrEmpty();
        // The examples are dated 2026-11-03 (a maintenance window must end at most 7 days ahead).
        RemoteConfigValidator.Validate(config!, out var error, new DateTimeOffset(AgentPayloads.Now, TimeSpan.Zero))
            .Should().BeTrue(error);
    }
}