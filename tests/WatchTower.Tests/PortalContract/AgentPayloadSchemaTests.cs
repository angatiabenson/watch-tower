using System.Collections;
using System.Reflection;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using AwesomeAssertions;

using WatchTower.Models;
using WatchTower.Portal.Actions;
using WatchTower.Portal.Clients;
using WatchTower.Portal.Contracts;
using WatchTower.Services.Endpoints;
using WatchTower.Updates;
using WatchTower.Workers;

namespace WatchTower.Tests.PortalContract;

/// <summary>
/// (a) What the agent sends validates against the spec, with nothing undocumented: every DTO is
/// serialized with the agent's own wire options, validated with unknown properties forbidden and
/// extensible enums closed to their documented values, and compared field by field with its schema.
/// </summary>
public class AgentPayloadSchemaTests
{
    public enum Direction { AgentToPortal, PortalToAgent }

    /// <summary>Every wire DTO and the schema that documents it.</summary>
    public static readonly (Type Type, string Schema, Direction Direction)[] Contracts =
    [
        (typeof(MetricsBatchDto), "MetricsBatch", Direction.AgentToPortal),
        (typeof(AgentTelemetryDto), "AgentTelemetry", Direction.AgentToPortal),
        (typeof(AgentUpdateResultDto), "AgentUpdateResult", Direction.AgentToPortal),
        (typeof(MaintenanceTelemetryDto), "MaintenanceTelemetry", Direction.AgentToPortal),
        (typeof(CpuMetricDto), "CpuMetric", Direction.AgentToPortal),
        (typeof(RamMetricDto), "RamMetric", Direction.AgentToPortal),
        (typeof(DiskMetricDto), "DiskMetric", Direction.AgentToPortal),
        (typeof(DiskQueueMetricDto), "DiskQueueMetric", Direction.AgentToPortal),
        (typeof(NetworkMetricDto), "NetworkMetric", Direction.AgentToPortal),
        (typeof(ServiceStatusDto), "ServiceStatus", Direction.AgentToPortal),
        (typeof(ApplicationPoolStatusDto), "ApplicationPoolStatus", Direction.AgentToPortal),
        (typeof(EndpointStatusDto), "EndpointStatus", Direction.AgentToPortal),
        (typeof(AgentEventDto), "AgentEvent", Direction.AgentToPortal),
        (typeof(ProcessInfoDto), "ProcessInfo", Direction.AgentToPortal),
        (typeof(AcknowledgeCommandDto), "AcknowledgeCommand", Direction.AgentToPortal),
        (typeof(CommandResultDto), "CommandResult", Direction.AgentToPortal),
        (typeof(AgentCommandDto), "AgentCommand", Direction.PortalToAgent),
        (AgentClientHarness.ConfigFetchResponseType, "ConfigFetchResponse", Direction.PortalToAgent),
        (typeof(RemoteAgentConfigDto), "RemoteAgentConfig", Direction.PortalToAgent),
        (typeof(RemoteCpuConfig), "RemoteCpuConfig", Direction.PortalToAgent),
        (typeof(RemoteRamConfig), "RemoteRamConfig", Direction.PortalToAgent),
        (typeof(RemoteDiskConfig), "RemoteDiskConfig", Direction.PortalToAgent),
        (typeof(RemoteNetworkConfig), "RemoteNetworkConfig", Direction.PortalToAgent),
        (typeof(RemoteServiceConfig), "RemoteServiceConfig", Direction.PortalToAgent),
        (typeof(RemoteAppPoolConfig), "RemoteAppPoolConfig", Direction.PortalToAgent),
        (typeof(RemotePortalConfig), "RemotePortalConfig", Direction.PortalToAgent),
        (typeof(RemoteMaintenanceConfig), "RemoteMaintenanceConfig", Direction.PortalToAgent),
    ];

    /// <summary>
    /// What the agent needs from a portal-sent object; the spec's <c>required</c> must say the same.
    /// commandId: a command without one is ignored. actionType: without one nothing can run.
    /// issuedAt is RECOMMENDED but optional (commands without it are accepted, see AgentCommand).
    /// config + version: a 200 without either is treated as "no change".
    /// </summary>
    private static readonly Dictionary<string, string[]> InboundRequired = new()
    {
        ["AgentCommand"] = ["commandId", "actionType"],
        ["ConfigFetchResponse"] = ["config", "version"],
    };

    public static TheoryData<string> ContractNames() => new(Contracts.Select(c => c.Schema));

    public static TheoryData<string> AgentRequests() => new(RequestCases.Keys);

    /// <summary>Every request body the agent sends, through the real client.</summary>
    private static readonly Dictionary<string, Func<IPortalClient, Task>> RequestCases = new()
    {
        ["metrics batch, fully populated"] = c => c.SendMetricsBatchAsync(AgentPayloads.FullBatch(), default),
        ["metrics batch, minimal"] = c => c.SendMetricsBatchAsync(AgentPayloads.MinimalBatch(), default),
        ["metrics batch, minimal telemetry"] = c => c.SendMetricsBatchAsync(
            AgentPayloads.MinimalBatch() with { Agent = AgentPayloads.MinimalTelemetry() }, default),
        ["event, fully populated"] = c => c.SendEventAsync(AgentPayloads.FullEvent(), default),
        ["event, minimal"] = c => c.SendEventAsync(AgentPayloads.MinimalEvent(), default),
        ["command acknowledgement"] = c => c.AcknowledgeCommandAsync(AgentPayloads.Ack("6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10"), default),
        ["command result, success"] = c => c.ReportCommandResultAsync(AgentPayloads.Result("6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10"), default),
        ["command result, refused"] = c => c.ReportCommandResultAsync(AgentPayloads.Result("8e1f3a5c", success: false), default),
        ["config acknowledgement"] = c => c.AcknowledgeConfigAsync("2026-11-03.4", default),
    };

    [Theory]
    [MemberData(nameof(AgentRequests))]
    public async Task EveryRequestBody_ValidatesStrictlyAgainstItsOperation(string name)
    {
        var handler = new RecordingHandler();
        var client = AgentClientHarness.CreateClient(AgentClientHarness.Options("https://portal.example.com"), () => handler);

        await RequestCases[name](client);

        var request = handler.Requests.Should().ContainSingle().Subject;
        var operation = PortalSpec.Operations.Single(o => o.Matches(request.Method, request.RawPath));
        operation.RequestSchema.Should().NotBeNull($"{operation} must document the body the agent sends");
        request.ContentType.Should().Be("application/json; charset=utf-8");
        PortalSpec.Validate(operation.RequestSchema!, request.Body!, SchemaMode.Strict)
            .Should().BeEmpty($"the agent's {name} must match {operation} (body: {request.Body})");
    }

    [Fact]
    public void FullSamples_SetEveryPropertyOfEveryDto()
    {
        // Strict validation only covers the properties that are present, so the "full" samples must
        // really be full: every property of every DTO in the graph appears at least once.
        var seen = new Dictionary<Type, HashSet<string>>();
        Visit(typeof(MetricsBatchDto), Serialize(AgentPayloads.FullBatch()), seen);
        Visit(typeof(AgentEventDto), Serialize(AgentPayloads.FullEvent()), seen);
        Visit(typeof(AgentCommandDto), Serialize(AgentPayloads.FullCommand()), seen);
        Visit(typeof(RemoteAgentConfigDto), Serialize(AgentPayloads.FullConfig()), seen);
        Visit(typeof(AcknowledgeCommandDto), Serialize(AgentPayloads.Ack("c")), seen);
        Visit(typeof(CommandResultDto), Serialize(AgentPayloads.Result("c")), seen);

        var missing = Contracts.Where(c => c.Type != AgentClientHarness.ConfigFetchResponseType)
            .SelectMany(c => JsonNames(c.Type)
                .Where(n => !seen.GetValueOrDefault(c.Type, []).Contains(n))
                .Select(n => $"{c.Type.Name}.{n}"))
            .ToList();
        missing.Should().BeEmpty("AgentPayloads' full samples must set every property");
    }

    [Theory]
    [InlineData("MetricsBatch")]
    [InlineData("AgentEvent")]
    [InlineData("AgentCommand")]
    [InlineData("RemoteAgentConfig")]
    [InlineData("ConfigFetchResponse")]
    public void FullSamples_ValidateStrictly(string schema)
    {
        var json = schema switch
        {
            "MetricsBatch" => Serialize(AgentPayloads.FullBatch()),
            "AgentEvent" => Serialize(AgentPayloads.FullEvent()),
            "AgentCommand" => Serialize(AgentPayloads.FullCommand()),
            "RemoteAgentConfig" => Serialize(AgentPayloads.FullConfig()),
            _ => JsonSerializer.Serialize(
                Activator.CreateInstance(AgentClientHarness.ConfigFetchResponseType, AgentPayloads.FullConfig(), "2026-11-03.4"),
                AgentClientHarness.ConfigFetchResponseType, AgentClientHarness.WireOptions),
        };

        PortalSpec.Validate(schema, json, SchemaMode.Strict).Should().BeEmpty(json);
    }

    [Theory]
    [MemberData(nameof(ContractNames))]
    public void DtoProperties_AreExactlyTheSchemaProperties(string schema)
    {
        var contract = Contracts.Single(c => c.Schema == schema);
        var specProperties = PortalSpec.Schemas[schema]!["properties"]!.AsObject().Select(p => p.Key).ToHashSet();

        JsonNames(contract.Type).Should().BeEquivalentTo(specProperties,
            $"{contract.Type.Name} (as the agent serializes it) and components/schemas/{schema} must list the same properties");
    }

    [Theory]
    [MemberData(nameof(ContractNames))]
    public void RequiredProperties_MatchWhatTheAgentAlwaysSendsOrNeeds(string schema)
    {
        var contract = Contracts.Single(c => c.Schema == schema);
        var spec = PortalSpec.Schemas[schema]!.AsObject();
        var required = (spec["required"]?.AsArray().Select(n => (string)n!) ?? []).ToHashSet();

        if (contract.Direction == Direction.PortalToAgent)
        {
            required.Should().BeEquivalentTo(InboundRequired.GetValueOrDefault(schema, []),
                $"components/schemas/{schema}.required must list exactly what the agent cannot do without");
            return;
        }

        // Agent to portal: a property the agent can omit must not be required, and a property it always
        // sends but the spec does not require must be one that older agents (up to 1.0.18) omit.
        var alwaysSent = AlwaysSent(contract.Type);
        required.Except(alwaysSent).Should().BeEmpty($"the agent may omit these, so {schema} must not require them");
        var optionalButAlwaysSent = alwaysSent.Except(required)
            .Where(p => (string?)spec["properties"]![p]!["x-since"] != "post-1.0.18");
        optionalButAlwaysSent.Should().BeEmpty(
            $"the agent always sends these: require them in {schema}, or mark them x-since: post-1.0.18 if older agents omit them");
    }

    [Fact]
    public void ExtensibleEnums_AreOpenInThePublishedSchemas()
    {
        // JSON Schema has no "open enum": an `enum` keyword rejects every other value, which would
        // contradict the rule that portals MUST accept values they do not know. Known values are listed
        // in x-extensible-enum instead.
        var enums = PortalSpec.ExtensibleEnums().ToList();
        enums.Should().NotBeEmpty();
        foreach (var (pointer, schema) in enums)
        {
            schema["enum"].Should().BeNull($"{pointer} is extensible, so it must not have a closed `enum`");
            schema["x-extensible-enum"].Should().BeOfType<JsonArray>($"{pointer}: x-extensible-enum lists the known values")
                .Which.Should().NotBeEmpty();
        }
    }

    [Theory]
    [InlineData("IncidentType")]
    [InlineData("ActionType")]
    [InlineData("AgentTelemetry/properties/updatePhase")]
    [InlineData("ServiceStatus/properties/status")]
    [InlineData("ApplicationPoolStatus/properties/status")]
    [InlineData("EndpointStatus/properties/type")]
    [InlineData("EndpointStatus/properties/status")]
    public void ExtensibleEnums_ListExactlyTheValuesTheAgentProduces(string location)
    {
        var schema = PortalSpec.Pointer("#/components/schemas/" + location).AsObject();
        var known = schema["x-extensible-enum"]!.AsArray().Select(v => v!.ToJsonString().Trim('"')).ToList();

        IEnumerable<string> agent = location switch
        {
            "IncidentType" => Enum.GetValues<IncidentType>().Select(v => ((int)v).ToString()),
            "ActionType" => Enum.GetValues<ActionType>().Select(v => ((int)v).ToString()),
            "AgentTelemetry/properties/updatePhase" => Enum.GetNames<UpdatePhase>(),
            "ServiceStatus/properties/status" => [.. Enum.GetNames<ServiceControllerStatus>(), "NotFound"],
            // IIS ObjectState names (appcmd / WebAdministration), plus the agent's own two values.
            "ApplicationPoolStatus/properties/status" =>
                ["Starting", "Started", "Stopping", "Stopped", ApplicationPoolStatus.UnknownStatus, ApplicationPoolStatus.NotFoundStatus],
            "EndpointStatus/properties/type" => Enum.GetNames<EndpointCheckType>(),
            // The probe's results, plus the status of a configured but invalid check.
            "EndpointStatus/properties/status" => [.. Enum.GetNames<EndpointState>(), EndpointStatus.ConfigurationError],
            _ => throw new ArgumentOutOfRangeException(nameof(location)),
        };

        known.Should().BeEquivalentTo(agent, $"{location} must document every value the agent sends, and no others");
        if (location is "IncidentType" or "ActionType")
        {
            var names = schema["x-enum-names"]!.AsArray().Select(n => (string)n!).ToList();
            IEnumerable<string> agentNames = location == "IncidentType" ? Enum.GetNames<IncidentType>() : Enum.GetNames<ActionType>();
            names.Should().Equal(agentNames, "x-enum-names must name the values in the same order");
        }
    }

    [Fact]
    public void ConfigurationError_KeepsTheLastResult_InTheDocumentedShape()
    {
        var last = new EndpointStatus(AgentPayloads.Now.AddMinutes(-20), "status-api", "Http", "https://api.example.com/health",
            nameof(EndpointState.Down), false, 95, null, "HTTP 503");
        const string error = "Monitoring:Endpoints:2:Target must be an absolute http(s) URL.";

        // The agent's own marking (EndpointMonitorWorker): everything but status and detail is the last result.
        var marked = EndpointMonitorWorker.MarkConfigurationError(last, error);
        marked.Should().Be(last with
        {
            Status = EndpointStatus.ConfigurationError,
            Detail = $"Configuration error: {error} Last result: Down: HTTP 503",
        });
        EndpointMonitorWorker.MarkConfigurationError(marked, "Another error.").Detail
            .Should().Be("Configuration error: Another error. Last result: Down: HTTP 503", "a new error keeps the last real result");
        EndpointMonitorWorker.MarkConfigurationError(last with { Status = nameof(EndpointState.Up), IsUp = true, Detail = null }, error).Detail
            .Should().Be($"Configuration error: {error} Last result: Up", "the \": <detail>\" part is omitted without a detail");

        // The spec documents that shape and isUp's meaning for it.
        var status = PortalSpec.Pointer("#/components/schemas/EndpointStatus/properties/status").AsObject();
        status["x-extensible-enum"]!.AsArray().Select(v => (string)v!).Should().Contain(marked.Status);
        Collapse(status["description"]!.GetValue<string>()).Should().Contain("\"Configuration error: <error> Last result: <status>[: <detail>]\"");
        Collapse(PortalSpec.Pointer("#/components/schemas/EndpointStatus/properties/isUp/description").GetValue<string>())
            .Should().Contain("With ConfigurationError it is the value of the last result");

        // On the wire (HTTPS as OutboxDispatchWorker maps it, and gRPC) it is a valid EndpointStatus.
        var dto = new EndpointStatusDto(marked.Timestamp.ToString("O"), marked.Name, marked.Type, marked.Target, marked.Status,
            marked.IsUp, marked.ResponseTimeMs, marked.CertificateDaysLeft, marked.Detail);
        var json = Serialize(dto);
        PortalSpec.Validate("EndpointStatus", json, SchemaMode.Strict).Should().BeEmpty(json);
        GrpcPortalClient.ToProto(dto).Status.Should().Be(EndpointStatus.ConfigurationError);
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void PublishedSchemas_AcceptWhatLaterAgentsMaySend_StrictModeDoesNot()
    {
        // The forward-compatibility rules of the spec: unknown properties and unknown enum values
        // are valid input for a portal. Strict mode (used for the agent's own payloads and the
        // examples) must reject both, or the other tests would prove nothing.
        var batch = JsonNode.Parse(Serialize(AgentPayloads.FullBatch()))!;
        batch["someFutureSection"] = new JsonArray(1, 2);
        batch["agent"]!["updatePhase"] = "RollingBack";
        batch["serviceStatuses"]![0]!["status"] = "Exotic";
        batch["endpointStatuses"]![0]!["type"] = "Dns";
        var evt = JsonNode.Parse(Serialize(AgentPayloads.FullEvent()))!;
        evt["incidentType"] = 99;
        evt["severity"] = "high";
        var commands = new JsonArray(JsonNode.Parse(Serialize(AgentPayloads.FullCommand() with { ActionType = 999 })));
        commands[0]!["priority"] = 1;

        foreach (var (label, json) in new[] { ("MetricsBatch", batch), ("AgentEvent", evt), ("AgentCommand[]", commands) })
        {
            PortalSpec.Validate(label, json.ToJsonString(), SchemaMode.Published).Should().BeEmpty(label);
            PortalSpec.Validate(label, json.ToJsonString(), SchemaMode.Strict).Should().NotBeEmpty(label);
        }
    }

    [Fact]
    public void StrictMode_RejectsAMissingRequiredPropertyAndAWrongType()
    {
        var batch = JsonNode.Parse(Serialize(AgentPayloads.FullBatch()))!.AsObject();
        batch.Remove("batchedAt");
        batch["cpuMetrics"]![0]!["usagePercent"] = "12.5";

        var errors = PortalSpec.Validate("MetricsBatch", batch.ToJsonString(), SchemaMode.Strict);

        errors.Should().Contain(e => e.Contains("batchedAt")).And.Contain(e => e.StartsWith("/cpuMetrics/0/usagePercent"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, AgentClientHarness.WireOptions);

    private static JsonTypeInfo TypeInfo(Type type) => AgentClientHarness.WireOptions.GetTypeInfo(type);

    internal static HashSet<string> JsonNames(Type type) => TypeInfo(type).Properties.Select(p => p.Name).ToHashSet();

    /// <summary>Properties that are never null, so the agent's WhenWritingNull options always write them.</summary>
    private static HashSet<string> AlwaysSent(Type type)
    {
        var nullability = new NullabilityInfoContext();
        var result = new HashSet<string>();
        foreach (var property in TypeInfo(type).Properties)
        {
            var info = type.GetProperty(property.AttributeProvider is PropertyInfo p ? p.Name : property.Name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)!;
            var nullable = Nullable.GetUnderlyingType(info.PropertyType) is not null ||
                           (!info.PropertyType.IsValueType && nullability.Create(info).ReadState != NullabilityState.NotNull);
            if (!nullable)
                result.Add(property.Name);
        }
        return result;
    }

    private static void Visit(Type type, string json, Dictionary<Type, HashSet<string>> seen) =>
        Visit(type, JsonNode.Parse(json), seen);

    private static void Visit(Type type, JsonNode? node, Dictionary<Type, HashSet<string>> seen)
    {
        if (node is null)
            return;
        var element = ElementType(type);
        if (element is not null && node is JsonArray array)
        {
            foreach (var item in array)
                Visit(element, item, seen);
            return;
        }
        if (node is not JsonObject obj || type == typeof(string) || typeof(IDictionary).IsAssignableFrom(type))
            return;
        foreach (var property in TypeInfo(type).Properties)
        {
            if (!obj.TryGetPropertyValue(property.Name, out var value) || value is null)
                continue;
            if (!seen.TryGetValue(type, out var names))
                seen[type] = names = [];
            names.Add(property.Name);
            Visit(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType, value, seen);
        }
    }

    private static Type? ElementType(Type type) =>
        type == typeof(string) || typeof(IDictionary).IsAssignableFrom(type)
            ? null
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];
}