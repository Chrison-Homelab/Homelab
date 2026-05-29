using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fallout.Common.IO;
using Serilog;
using YamlDotNet.Serialization;
using static Fallout.Common.IO.PathConstruction;

// Structural validation of the /Infrastructure shape YAMLs. Mirrors the required
// fields of Infrastructure/schema/shape.schema.json. (Full JSON-Schema evaluation
// is a later upgrade; this catches the common breakage cheaply.)
partial class Build
{
    static readonly string[] ValidKinds = { "Node", "LXC", "VM", "NASShare" };

    Target ValidateShapes => _ => _
        .Description("Validate the /Infrastructure shape YAMLs against the contract")
        .Executes(() =>
        {
            var dir = RootDirectory / "Infrastructure";
            var files = dir.GlobFiles("**/*.yaml");
            var deserializer = new DeserializerBuilder().Build();
            var failures = new List<string>();

            foreach (var file in files)
            {
                var errs = new List<string>();
                try
                {
                    var doc = deserializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                    if (doc == null)
                    {
                        errs.Add("empty document");
                    }
                    else
                    {
                        if (!doc.TryGetValue("apiVersion", out var av) || av?.ToString() != "homelab/v1")
                            errs.Add("apiVersion must be 'homelab/v1'");
                        if (!doc.TryGetValue("kind", out var kind) || !ValidKinds.Contains(kind?.ToString()))
                            errs.Add($"kind must be one of: {string.Join(", ", ValidKinds)}");
                        if (!doc.TryGetValue("metadata", out var meta)
                            || meta is not IDictionary<object, object> m
                            || !m.Keys.Any(k => k?.ToString() == "name"))
                            errs.Add("metadata.name is required");
                        if (!doc.ContainsKey("spec"))
                            errs.Add("spec is required");
                    }
                }
                catch (Exception ex)
                {
                    errs.Add($"parse error: {ex.Message}");
                }

                if (errs.Count == 0)
                {
                    Log.Information("VALID:   {File}", file);
                }
                else
                {
                    foreach (var e in errs)
                        failures.Add($"{file}: {e}");
                    Log.Error("INVALID: {File} — {Errors}", file, string.Join("; ", errs));
                }
            }

            if (failures.Count > 0)
                throw new Exception($"{failures.Count} shape validation failure(s) across {files.Count} file(s).");

            Log.Information("All {Count} shape file(s) valid.", files.Count);
        });
}
