using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Homelab.Infrastructure.Shapes;

// Validates homelab/v1 shape documents against the canonical JSON Schema
// (Infrastructure/schema/shape.schema.json, draft-2020-12) — independent of the
// YamlDotNet deserialization path, so unknown/invalid fields are caught even
// though ShapeLoader uses IgnoreUnmatchedProperties (#43).
//
// The shape is authored in YAML; we parse it to a plain object graph, convert to
// a System.Text.Json JsonNode, and run JsonSchema.Net over it. Failures are
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
            var obj = YamlToObject.Deserialize<object?>(File.ReadAllText(path));
            node = ToJsonNode(obj);
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
        return files.Where(IsHomelabShape).Select(ValidateFile).ToList();
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

    // --- YAML object graph → System.Text.Json JsonNode ---
    private static JsonNode? ToJsonNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case IDictionary<object, object> map:
            {
                var obj = new JsonObject();
                foreach (var kv in map)
                    obj[Convert.ToString(kv.Key) ?? ""] = ToJsonNode(kv.Value);
                return obj;
            }
            case IEnumerable<object?> list when value is not string:
            {
                var arr = new JsonArray();
                foreach (var item in list)
                    arr.Add(ToJsonNode(item));
                return arr;
            }
            case string s:
            {
                // YamlDotNet hands everything back as strings. Re-type scalars so
                // the schema's integer/number/boolean constraints evaluate
                // correctly (a YAML `ctid: 3000` arrives as "3000").
                return ScalarToNode(s);
            }
            default:
                return JsonValue.Create(value);
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
    public static string ToJsonText(string yamlPath)
    {
        var obj = YamlToObject.Deserialize<object?>(File.ReadAllText(yamlPath));
        var node = ToJsonNode(obj);
        return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
    }
}
