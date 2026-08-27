using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Homelab.Infrastructure.Shapes;

// Validates homelab/v1 shape documents against the canonical JSON Schema
// (Infrastructure/schema/shape.schema.json, draft-2020-12) — independent of the
// YamlDotNet deserialization path, so unknown/invalid fields are caught even
// though ShapeLoader uses IgnoreUnmatchedProperties (#43).
//
// The shape is authored in YAML; we parse it through YamlDotNet's representation
// model (which preserves scalar STYLE — see ParseToJsonNode), convert to a
// System.Text.Json JsonNode, and run JsonSchema.Net over it. Failures are
// surfaced with the file path + JSON pointer (instance location) + message.
public static class ShapeValidator
{
    public sealed record Failure(string InstanceLocation, string? Keyword, string Message)
    {
        public override string ToString() =>
            $"  {(string.IsNullOrEmpty(InstanceLocation) ? "(root)" : InstanceLocation)}"
            + (Keyword is null ? "" : $" [{Keyword}]")
            + $": {Message}";
    }

    public sealed record Result(string Path, bool Valid, IReadOnlyList<Failure> Failures)
    {
        public static Result Ok(string path) => new(path, true, Array.Empty<Failure>());
    }

    private static readonly IDeserializer YamlToObject = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)  // keep authored keys verbatim
        .Build();

    private static readonly Lazy<JsonSchema> Schema = new(LoadSchema);
    private static readonly EvaluationOptions EvalOptions = new()
    {
        OutputFormat = OutputFormat.List,
    };

    // Resolve the canonical schema: prefer the copy beside the engine binary
    // (csproj copies schema/shape.schema.json to the output dir), then walk up
    // for the repo's Infrastructure/schema/shape.schema.json as a dev fallback.
    public static string ResolveSchemaPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "schema", "shape.schema.json");
        if (File.Exists(beside)) return beside;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Infrastructure", "schema", "shape.schema.json");
            if (File.Exists(candidate)) return candidate;
            var direct = Path.Combine(dir.FullName, "schema", "shape.schema.json");
            if (File.Exists(direct)) return direct;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate shape.schema.json (looked beside the engine and up the tree).");
    }

    private static JsonSchema LoadSchema() => JsonSchema.FromText(File.ReadAllText(ResolveSchemaPath()));

    // Validate a single shape file.
    public static Result ValidateFile(string path)
    {
        JsonNode? node;
        try
        {
            node = ParseToJsonNode(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return new Result(path, false, new[] { new Failure("", "parse", $"YAML parse error: {ex.Message}") });
        }

        // JsonSchema.Net 9 evaluates a JsonElement; round-trip the node.
        var element = node is null
            ? JsonSerializer.SerializeToElement<JsonNode?>(null)
            : JsonSerializer.SerializeToElement(node);
        var eval = Schema.Value.Evaluate(element, EvalOptions);
        if (eval.IsValid) return Result.Ok(path);

        var failures = new List<Failure>();
        Collect(eval, failures);
        if (failures.Count == 0)
            failures.Add(new Failure("", null, "Document did not satisfy the schema."));
        return new Result(path, false, failures);
    }

    private static void Collect(EvaluationResults results, List<Failure> into)
    {
        if (!results.IsValid && results.Errors is { Count: > 0 })
        {
            var loc = results.InstanceLocation.ToString();
            foreach (var err in results.Errors)
                into.Add(new Failure(loc, err.Key, err.Value));
        }
        if (results.Details is { } details)
            foreach (var child in details)
                Collect(child, into);
    }

    // Validate every *.yaml under a directory (non-recursive by convention for
    // nodes/, recursive for stacks via the caller). Returns one Result per file.
    public static IReadOnlyList<Result> ValidateDirectory(string dir, bool recursive = false)
    {
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(dir, "*.yaml", opt)
            .Concat(Directory.EnumerateFiles(dir, "*.yml", opt))
            .Distinct()
            .OrderBy(f => f, StringComparer.Ordinal);
        // Only validate homelab/v1 shapes; skip other YAML (e.g. the app-catalogue,
        // CI/compose files) — mirrors tools/validate-shapes.py's apiVersion gate.
        return files.Where(f => !IsAssetPayload(f)).Where(IsHomelabShape).Select(ValidateFile).ToList();
    }

    // Anything under an `assets/` directory is a PAYLOAD shipped into a guest, not a shape —
    // podman-host config trees, Grafana dashboards, Authentik blueprints. Excluded before the
    // apiVersion probe rather than by it, because that probe deliberately KEEPS files it
    // cannot parse so a typo in a real shape is never silently skipped. Authentik's blueprints
    // are valid YAML 1.1 but use custom tags (`!Env`, `!Find`, `!KeyOf`) that no general
    // parser resolves, so they would trip that safety net on every run while being exactly the
    // kind of file it is not meant to catch.
    internal static bool IsAssetPayload(string path)
    {
        var norm = path.Replace('\\', '/');
        return norm.Contains("/assets/", StringComparison.Ordinal);
    }

    // Cheap apiVersion probe: a directory scan validates only documents that
    // declare `apiVersion: homelab/v1`. Files that fail to parse are kept (so
    // ValidateFile surfaces the parse error rather than silently skipping a shape).
    private static bool IsHomelabShape(string path)
    {
        try
        {
            return YamlToObject.Deserialize<object?>(File.ReadAllText(path)) is IDictionary<object, object> map
                && map.TryGetValue("apiVersion", out var v)
                && string.Equals(Convert.ToString(v), "homelab/v1", StringComparison.Ordinal);
        }
        catch
        {
            return true; // unparseable → let ValidateFile report it
        }
    }

    // --- YAML document → System.Text.Json JsonNode ---
    //
    // Parsed through the REPRESENTATION MODEL rather than Deserialize<object?>, because
    // the object graph throws away the one thing this conversion needs: whether a scalar
    // was QUOTED. YamlDotNet hands every scalar back as a string either way, so the
    // re-typing below cannot otherwise tell `24.04` (a float) from `"24.04"` (a string)
    // — and it re-typed both to a number, which made `osVersion: "24.04"` unauthorable
    // even though the schema accepts a string and the loader already reads it as one
    // (Ubuntu is the first OS in the lab whose version is not an integer, #508).
    internal static JsonNode? ParseToJsonNode(string yamlText)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yamlText);
        stream.Load(reader);
        return stream.Documents.Count == 0 ? null : ToJsonNode(stream.Documents[0].RootNode);
    }

    private static JsonNode? ToJsonNode(YamlNode? node)
    {
        switch (node)
        {
            case YamlMappingNode map:
            {
                var obj = new JsonObject();
                foreach (var kv in map.Children)
                    obj[(kv.Key as YamlScalarNode)?.Value ?? kv.Key.ToString()] = ToJsonNode(kv.Value);
                return obj;
            }
            case YamlSequenceNode seq:
            {
                var arr = new JsonArray();
                foreach (var item in seq.Children)
                    arr.Add(ToJsonNode(item));
                return arr;
            }
            case YamlScalarNode scalar:
            {
                // A quoted (or block) scalar is a string by definition — no re-typing.
                // Only PLAIN scalars carry an implicit type for us to recover.
                if (scalar.Style != ScalarStyle.Plain) return JsonValue.Create(scalar.Value ?? "");
                return ScalarToNode(scalar.Value ?? "");
            }
            default:
                return null;
        }
    }

    private static JsonNode? ScalarToNode(string s)
    {
        if (s.Length == 0) return JsonValue.Create(s);

        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        if (string.Equals(s, "null", StringComparison.Ordinal) || s == "~") return null;

        // Integers (no leading-zero ambiguity, no '+').
        if (long.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var l)
            && l.ToString(System.Globalization.CultureInfo.InvariantCulture) == s)
            return JsonValue.Create(l);

        // Floats.
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)
            && (s.Contains('.') || s.Contains('e') || s.Contains('E')))
            return JsonValue.Create(d);

        return JsonValue.Create(s);
    }

    // Best-effort: also expose the parsed node as JSON text (diagnostics/tests).
    public static string ToJsonText(string yamlPath) =>
        ParseToJsonNode(File.ReadAllText(yamlPath))
            ?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
}
