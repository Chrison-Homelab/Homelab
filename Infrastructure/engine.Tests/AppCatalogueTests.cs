using Homelab.Infrastructure.Converge;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Homelab.Infrastructure.Tests;

// Drift guard for the BL-013 app→script catalogue (issue #48).
//
// ProvisionerRegistry.Default() hardcodes the app slugs that have a dedicated
// post-create provisioner. schema/app-catalogue.yaml is the language-neutral
// allowlist consumed by both the PowerShell renderer (validation) and this
// engine (dispatch). These two MUST stay in sync: any slug the engine will
// dispatch to a real provisioner has to be a known, validatable app.
//
// This test asserts that invariant without refactoring the working registry to
// read the catalogue at runtime (that would risk the live dispatch path). If a
// new provisioner is registered without a catalogue entry, this test fails.
public sealed class AppCatalogueTests
{
    [Fact]
    public void EveryRegisteredProvisionerAppIsInTheCatalogue()
    {
        var catalogueApps = LoadCatalogueApps();
        var registeredApps = ProvisionerRegistry.Default().RegisteredApps;

        Assert.NotEmpty(registeredApps);

        var missing = registeredApps.Where(a => !catalogueApps.Contains(a)).ToList();
        Assert.True(
            missing.Count == 0,
            $"App slug(s) registered in ProvisionerRegistry.Default() but absent from " +
            $"schema/app-catalogue.yaml: {string.Join(", ", missing)}. " +
            $"Add them to the catalogue so the renderer can validate them. " +
            $"Catalogue knows: {string.Join(", ", catalogueApps.OrderBy(x => x))}.");
    }

    [Fact]
    public void CatalogueEntriesWithAProvisionerNameAreRegistered()
    {
        // The reverse soft-check: every entry that *claims* a provisioner name
        // must actually resolve to a non-default provisioner in the registry,
        // so the catalogue can't advertise dispatch that doesn't exist.
        var entries = LoadCatalogue();
        var registry = ProvisionerRegistry.Default();

        var dangling = entries
            .Where(e => !string.IsNullOrEmpty(e.Provisioner))
            .Where(e => !string.Equals(registry.For(e.Key).App, e.Provisioner, StringComparison.Ordinal))
            .Select(e => $"{e.Key} -> {e.Provisioner}")
            .ToList();

        Assert.True(
            dangling.Count == 0,
            $"Catalogue entries name a provisioner the engine does not register: " +
            $"{string.Join(", ", dangling)}.");
    }

    private static HashSet<string> LoadCatalogueApps() =>
        LoadCatalogue().Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

    private static List<CatalogueEntry> LoadCatalogue()
    {
        var path = FindCataloguePath();
        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var doc = deserializer.Deserialize<CatalogueDoc>(yaml);
        Assert.NotNull(doc?.Apps);

        return doc!.Apps!
            .Select(kv => new CatalogueEntry(kv.Key, kv.Value?.Provisioner))
            .ToList();
    }

    // Walk up from the test assembly until we find Infrastructure/schema/app-catalogue.yaml.
    private static string FindCataloguePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Infrastructure", "schema", "app-catalogue.yaml");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate Infrastructure/schema/app-catalogue.yaml by walking up from " +
            AppContext.BaseDirectory);
    }

    private sealed record CatalogueEntry(string Key, string? Provisioner);

    private sealed class CatalogueDoc
    {
        public Dictionary<string, CatalogueApp?>? Apps { get; set; }
    }

    private sealed class CatalogueApp
    {
        public string? Script { get; set; }
        public string? Channel { get; set; }
        public string? Repo { get; set; }
        public string? Ref { get; set; }
        public string? Provisioner { get; set; }
    }
}
