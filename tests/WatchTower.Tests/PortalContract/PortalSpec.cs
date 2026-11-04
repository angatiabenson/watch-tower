using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Corvus.Text.Json;
using Corvus.Text.Json.Yaml;

using CorvusSchema = Corvus.Text.Json.Validator.JsonSchema;

namespace WatchTower.Tests.PortalContract;

/// <summary>How strictly a JSON value is checked against the published schemas.</summary>
public enum SchemaMode
{
    /// <summary>
    /// The schemas exactly as published: what a portal may rely on. Unknown properties and unknown
    /// values of extensible enums are allowed (the forward-compatibility rules).
    /// </summary>
    Published,

    /// <summary>
    /// The published schemas plus <c>additionalProperties: false</c> on every object and the known
    /// values of every extensible enum as a closed list. Used for what the agent sends and for the
    /// examples, so a property or value the spec does not document fails the test.
    /// </summary>
    Strict,
}

/// <summary>One operation of the spec: <c>(method, path)</c> and its OpenAPI object.</summary>
public sealed record SpecOperation(string Method, string PathTemplate, JsonObject Operation)
{
    private readonly Regex _pattern = new(
        "^" + Regex.Replace(Regex.Escape(PathTemplate), @"\\\{[^/]+?}", "[^/]+") + "$",
        RegexOptions.CultureInvariant);

    public string OperationId => (string)Operation["operationId"]!;

    /// <summary>True when <paramref name="path"/> (still percent-encoded, no query) fits the template.</summary>
    public bool Matches(string method, string path) =>
        string.Equals(method, Method, StringComparison.OrdinalIgnoreCase) && _pattern.IsMatch(path);

    /// <summary>The schema of the <c>application/json</c> request body, or null when there is none.</summary>
    public JsonNode? RequestSchema => Operation["requestBody"]?["content"]?["application/json"]?["schema"];

    /// <summary>The schema of a JSON response, or null when that status has no JSON body.</summary>
    public JsonNode? ResponseSchema(string status) =>
        Operation["responses"]?[status]?["content"]?["application/json"]?["schema"];

    public override string ToString() => $"{Method} {PathTemplate}";
}

/// <summary>
/// Loads <c>spec/portal-agent-api.v1.openapi.yaml</c> and validates JSON against its schemas with a
/// JSON Schema 2020-12 validator (OpenAPI 3.1 schemas are 2020-12 schemas).
/// </summary>
/// <remarks>
/// <para>
/// A schema is validated as a standalone 2020-12 document: the requested schema at the root and every
/// <c>components/schemas</c> entry under <c>$defs</c>, with <c>#/components/schemas/X</c> references
/// rewritten to <c>#/$defs/X</c>. Formats (<c>date-time</c>, <c>uuid</c>, <c>uri</c>) are asserted.
/// </para>
/// <para>
/// Extensible enums are written as <c>x-extensible-enum: [known values]</c> without <c>enum</c>, so the
/// published schema accepts any value of the declared type. <see cref="SchemaMode.Strict"/> turns the
/// list into an <c>enum</c>.
/// </para>
/// </remarks>
public static class PortalSpec
{
    public const string SpecRelativePath = "spec/portal-agent-api.v1.openapi.yaml";
    public const string GuidesRelativePath = "guides/portal-api";

    private static readonly string[] HttpMethods = ["get", "put", "post", "delete", "patch", "head", "options", "trace"];
    private static readonly Lazy<JsonObject> LazyDocument = new(Load);
    private static readonly ConcurrentDictionary<string, CorvusSchema> Compiled = new(StringComparer.Ordinal);

    /// <summary>Directory the spec and guides are copied to (see the test project file).</summary>
    public static string ContractDirectory => Path.Combine(AppContext.BaseDirectory, "PortalContract");

    public static string SpecPath => Path.Combine(ContractDirectory, "Spec", Path.GetFileName(SpecRelativePath));

    public static string GuidesDirectory => Path.Combine(ContractDirectory, "Guides");

    /// <summary>The whole spec as JSON. Do not modify it; clone what you change.</summary>
    public static JsonObject Document => LazyDocument.Value;

    public static JsonObject Schemas => Document["components"]!["schemas"]!.AsObject();

    public static IReadOnlyList<SpecOperation> Operations { get; } = LoadOperations();

    /// <summary>Converts YAML 1.2 (core schema) to JSON. Duplicate keys are an error.</summary>
    public static JsonNode ParseYaml(string yaml) =>
        JsonNode.Parse(YamlDocument.ConvertToJsonString(yaml, new YamlReaderOptions
        {
            Schema = YamlSchema.Core,
            DuplicateKeyBehavior = DuplicateKeyBehavior.Error,
        }))!;

    /// <summary>Follows a local <c>$ref</c> (<c>#/components/...</c>) if <paramref name="node"/> is one.</summary>
    public static JsonNode Resolve(JsonNode node)
    {
        while (node is JsonObject o && o["$ref"] is JsonValue r && ((string)r!).StartsWith("#/", StringComparison.Ordinal))
            node = Pointer((string)r!);
        return node;
    }

    /// <summary>Evaluates a JSON pointer (<c>#/a/b</c>) against the spec.</summary>
    public static JsonNode Pointer(string pointer)
    {
        JsonNode current = Document;
        foreach (var raw in pointer.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Replace("~1", "/").Replace("~0", "~");
            current = current[token] ?? throw new KeyNotFoundException($"'{pointer}' does not resolve in {SpecRelativePath}");
        }
        return current;
    }

    /// <summary>
    /// Builds a schema from a label: <c>Name</c> (a <c>components/schemas</c> entry) or <c>Name[]</c> (an
    /// array of them). Throws for unknown names.
    /// </summary>
    public static JsonNode SchemaForLabel(string label)
    {
        var isArray = label.EndsWith("[]", StringComparison.Ordinal);
        var name = isArray ? label[..^2] : label;
        if (!Schemas.ContainsKey(name))
            throw new KeyNotFoundException($"'{name}' is not a schema in components/schemas of {SpecRelativePath}");
        JsonNode reference = new JsonObject { ["$ref"] = $"#/components/schemas/{name}" };
        return isArray ? new JsonObject { ["type"] = "array", ["items"] = reference } : reference;
    }

    /// <summary>Validates <paramref name="json"/> against a named schema (see <see cref="SchemaForLabel"/>).</summary>
    public static IReadOnlyList<string> Validate(string label, string json, SchemaMode mode) =>
        Validate(SchemaForLabel(label), json, mode);

    /// <summary>
    /// Validates <paramref name="json"/> against <paramref name="schema"/> (a node of the spec, or one
    /// built from it). Returns the failures, one line each: <c>instance path | schema path | message</c>.
    /// An empty list means valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(JsonNode schema, string json, SchemaMode mode)
    {
        var validator = Compile(schema, mode);
        using var collector = JsonSchemaResultsCollector.Create(JsonSchemaResultsLevel.Detailed);
        if (validator.Validate(json, collector))
            return [];

        var errors = new List<string>();
        foreach (var result in collector.EnumerateResults())
        {
            if (result.IsMatch)
                continue;
            var message = result.GetMessageText();

            // Skip the "expected to match the subschema" wrappers when a more specific failure exists.
            if (message.StartsWith("The value was expected to match the subschema", StringComparison.Ordinal))
                continue;
            errors.Add($"{Display(result.GetDocumentEvaluationLocationText())} | {result.GetSchemaEvaluationLocationText()} | {message}");
        }
        if (errors.Count == 0)
            errors.Add("(the document does not match the schema; the validator gave no detail)");
        return errors;
    }

    /// <summary>Every <c>components/schemas</c> entry with an <c>x-extensible-enum</c> list, by JSON pointer.</summary>
    public static IEnumerable<(string Pointer, JsonObject Schema)> ExtensibleEnums()
    {
        foreach (var (pointer, node) in WalkSchemas(Schemas, "#/components/schemas", isMap: true))
        {
            if (node["x-extensible-enum"] is not null)
                yield return (pointer, node);
        }
    }

    /// <summary>
    /// Every schema object reachable from <paramref name="root"/> through schema keywords (not through
    /// <c>examples</c> or other instance data), with its JSON pointer.
    /// </summary>
    public static IEnumerable<(string Pointer, JsonObject Schema)> WalkSchemas(JsonNode root, string pointer, bool isMap = false)
    {
        if (isMap)
        {
            foreach (var (name, child) in root.AsObject())
            {
                foreach (var item in WalkSchemas(child!, $"{pointer}/{Escape(name)}"))
                    yield return item;
            }
            yield break;
        }
        if (root is not JsonObject schema)
            yield break;

        yield return (pointer, schema);
        foreach (var keyword in new[] { "properties", "$defs", "patternProperties" })
        {
            if (schema[keyword] is JsonObject map)
            {
                foreach (var item in WalkSchemas(map, $"{pointer}/{keyword}", isMap: true))
                    yield return item;
            }
        }
        foreach (var keyword in new[] { "items", "additionalProperties", "unevaluatedProperties", "not", "if", "then", "else", "contains", "propertyNames" })
        {
            if (schema[keyword] is JsonObject child)
            {
                foreach (var item in WalkSchemas(child, $"{pointer}/{keyword}"))
                    yield return item;
            }
        }
        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf", "prefixItems" })
        {
            if (schema[keyword] is JsonArray list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    foreach (var item in WalkSchemas(list[i]!, $"{pointer}/{keyword}/{i}"))
                        yield return item;
                }
            }
        }
    }

    private static CorvusSchema Compile(JsonNode schema, SchemaMode mode)
    {
        var root = schema.DeepClone();
        if (root is JsonObject o)
            o.Remove("examples"); // instance data, not needed for validation
        var rootText = RewriteRefs(root.ToJsonString());
        var key = mode + ":" + rootText;
        return Compiled.GetOrAdd(key, _ =>
        {
            var document = JsonNode.Parse(rootText)!;
            var defs = JsonNode.Parse(RewriteRefs(Schemas.ToJsonString()))!.AsObject();
            if (document is JsonObject docObject)
            {
                docObject["$schema"] = "https://json-schema.org/draft/2020-12/schema";
                docObject["$defs"] = defs;
            }
            else
            {
                // A boolean schema (true/false) has nowhere to put $defs and needs none.
                document = new JsonObject
                {
                    ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                    ["allOf"] = new JsonArray(document),
                    ["$defs"] = defs,
                };
            }
            if (mode == SchemaMode.Strict)
                MakeStrict(document);

            var uri = $"https://watchtower.invalid/contract/{mode.ToString().ToLowerInvariant()}/{Hash(key)}.json";
            return CorvusSchema.FromText(document.ToJsonString(), uri, new CorvusSchema.Options(alwaysAssertFormat: true));
        });
    }

    private static void MakeStrict(JsonNode document)
    {
        foreach (var (_, schema) in WalkSchemas(document, "#").ToList())
        {
            if (schema["properties"] is JsonObject && schema["additionalProperties"] is null && schema["unevaluatedProperties"] is null)
                schema["additionalProperties"] = false;
            if (schema["x-extensible-enum"] is JsonArray known && schema["enum"] is null)
                schema["enum"] = known.DeepClone();
        }
    }

    private static string RewriteRefs(string json) =>
        json.Replace("\"#/components/schemas/", "\"#/$defs/", StringComparison.Ordinal);

    private static string Display(string location) => string.IsNullOrEmpty(location) ? "(root)" : location;

    private static string Escape(string token) => token.Replace("~", "~0").Replace("/", "~1");

    private static string Hash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    private static JsonObject Load()
    {
        if (!File.Exists(SpecPath))
            throw new FileNotFoundException($"{SpecRelativePath} was not copied to the test output", SpecPath);
        return ParseYaml(File.ReadAllText(SpecPath)).AsObject();
    }

    private static List<SpecOperation> LoadOperations()
    {
        var operations = new List<SpecOperation>();
        foreach (var (path, item) in Document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                if (HttpMethods.Contains(method))
                    operations.Add(new SpecOperation(method.ToUpperInvariant(), path, operation!.AsObject()));
            }
        }
        return operations;
    }
}