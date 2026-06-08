using System.Reflection;
using System.Text.Json;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// Tripwire: every property the schema defines under $defs.lxcSpec MUST have a
// corresponding property on the C# LxcSpec model. This prevents a future schema
// edit from silently bypassing the hand-expanded model (#43). If the schema
// grows a field, this test fails until LxcSpec catches up.
public sealed class SchemaDriftTests
{
    private static string SchemaPath => ShapeValidator.ResolveSchemaPath();

    [Fact]
    public void EveryLxcSpecSchemaProperty_HasA_ModelProperty()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        var props = doc.RootElement
            .GetProperty("$defs")
            .GetProperty("lxcSpec")
            .GetProperty("properties");

        var modelProps = typeof(LxcSpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        foreach (var schemaProp in props.EnumerateObject())
        {
            // Schema keys are camelCase; the model is PascalCase. Compare
            // case-insensitively on the de-cased name (they line up 1:1).
            if (!modelProps.Contains(schemaProp.Name))
                missing.Add(schemaProp.Name);
        }

        Assert.True(
            missing.Count == 0,
            $"LxcSpec is missing model properties for schema lxcSpec fields: {string.Join(", ", missing)}");
    }

    [Fact]
    public void MountModel_CoversSchemaMountDef()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        var props = doc.RootElement
            .GetProperty("$defs")
            .GetProperty("mount")
            .GetProperty("properties");

        var modelProps = typeof(MountSpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = props.EnumerateObject()
            .Where(p => !modelProps.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"MountSpec is missing model properties for schema mount fields: {string.Join(", ", missing)}");
    }

    // Same tripwire for the VM contract (#115): vmSpec + hostPci must each have a
    // 1:1 C# model so the ProxmoxSharp write path can't drift from the schema.
    [Theory]
    [InlineData("vmSpec", typeof(VmSpec))]
    [InlineData("hostPci", typeof(HostPciSpec))]
    [InlineData("vmDisk", typeof(VmDiskSpec))]
    public void Model_CoversSchemaDef(string defName, Type modelType)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        var props = doc.RootElement
            .GetProperty("$defs")
            .GetProperty(defName)
            .GetProperty("properties");

        var modelProps = modelType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = props.EnumerateObject()
            .Where(p => !modelProps.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{modelType.Name} is missing model properties for schema {defName} fields: {string.Join(", ", missing)}");
    }
}
