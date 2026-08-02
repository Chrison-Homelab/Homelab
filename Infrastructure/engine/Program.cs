using System.Text.Json;
using ProxmoxSharp;
using UnifiSharp;
using UnifiSharp.Legacy;
using Homelab.Infrastructure;
using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Homelab.Infrastructure.Unifi;

// Homelab.Infrastructure engine — the hub's first consumer of the ProxmoxSharp
// package. Reads live Proxmox state via the generated client.
//
// Usage:
//   homelab-infra discover               # dump a structured ClusterSnapshot as JSON
//   homelab-infra discover-diff a.json b.json   # Markdown table of state changes
//   homelab-infra discover-unifi         # dump a UniFi network snapshot as JSON
//   homelab-infra converge-unifi <file>           # plan UniFi network desired-state (dry run)
//   homelab-infra converge-unifi <file> --apply   # reconcile it (add-only, legacy write API)
//   homelab-infra converge <stack-dir>            # state-diff plan (dry run, read-only)
//   homelab-infra converge <stack-dir> --apply    # create + reconcile config + provision
//   homelab-infra converge <stack-dir> --destroy           # destroy plan (read-only)
//   homelab-infra converge <stack-dir> --destroy --yes     # stop + destroy the stack's CTs
//   homelab-infra converge <stack-dir> --only a[,b] [...]  # scope any of the above to
//                                         #   named members — the rest are untouched.
//                                         #   Stack defaults + dependency order still
//                                         #   apply; an unnamed dependency must already
//                                         #   be converged or the member fails (#306).
//   homelab-infra validate <path>        # validate a shape file / stack dir / nodes dir
//                                         #   against shape.schema.json (CI plan gate)
//
// PVE config comes from environment variables:
//   PROXMOX_BASE_URL   e.g. https://hpe-01.homelab.chrison.internal:8006/api2/json
//   PROXMOX_TOKEN_ID   e.g. root@pam!claude-mcp
//   PROXMOX_TOKEN_SECRET
//   PROXMOX_VERIFY_TLS optional, "false" for self-signed nodes

var command = args.Length > 0 ? args[0] : "discover";

switch (command)
{
    case "discover":
        return await DiscoverAsync();
    case "discover-diff":
        return RunDiscoverDiff(args);
    case "discover-unifi":
        return await DiscoverUnifiAsync();
    case "converge":
        return await RunConverge(args);
    case "converge-unifi":
        return await RunConvergeUnifi(args);
    case "validate":
        return RunValidate(args);
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Supported: discover, discover-diff, discover-unifi, converge, converge-unifi, validate");
        return 1;
}

// validate <path>: a single shape file, a stack dir, or Infrastructure/nodes/.
// Read-only plan-before-apply gate; exits non-zero on any failure.
static int RunValidate(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: homelab-infra validate <shape.yaml | stack-dir | nodes-dir>");
        return 2;
    }
    var path = Path.GetFullPath(args[1]);

    List<ShapeValidator.Result> results;
    if (Directory.Exists(path))
        results = ShapeValidator.ValidateDirectory(path, recursive: true).ToList();
    else if (File.Exists(path))
        results = new List<ShapeValidator.Result> { ShapeValidator.ValidateFile(path) };
    else
    {
        Console.Error.WriteLine($"Path not found: {path}");
        return 2;
    }

    if (results.Count == 0)
    {
        Console.Error.WriteLine($"No shape files (*.yaml/*.yml) found under {path}");
        return 2;
    }

    var failed = 0;
    foreach (var r in results.OrderBy(r => r.Path, StringComparer.Ordinal))
    {
        if (r.Valid)
        {
            Console.WriteLine($"OK    {r.Path}");
        }
        else
        {
            failed++;
            Console.WriteLine($"FAIL  {r.Path}");
            foreach (var f in r.Failures)
                Console.WriteLine(f.ToString());
        }
    }

    var ok = results.Count - failed;
    Console.WriteLine($"\n{ok}/{results.Count} shape(s) valid.");
    return failed == 0 ? 0 : 1;
}

static async Task<int> RunConverge(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: homelab-infra converge <stack-dir> [--apply | --destroy [--yes]] [--only <member>[,<member>...]]");
        return 2;
    }
    var stackDir = Path.GetFullPath(args[1]);
    var apply = args.Contains("--apply");
    var destroy = args.Contains("--destroy");
    var confirmed = args.Contains("--yes");

    // --only <member>[,<member>...] scopes every mode to named members (#306).
    IReadOnlyList<string>? only;
    try { only = MemberSelection.Parse(args); }
    catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 2; }

    var secretsPath = FindUp("secrets.env", Directory.GetCurrentDirectory());
    var env = SecretsEnv.Load(secretsPath);

    // PVE creds power the ProxmoxSharp write paths: VM converge (QemuWriter) AND the
    // LXC teardown via PctWriter (#149). null degrades to SSH-only (pct over SSH for
    // LXC; "VM plan/apply skipped").
    var pve = LoadOptions();

    // An unknown --only name is fatal, and must exit non-zero BEFORE anything is
    // touched: converging nothing would otherwise report success on a typo.
    try
    {
        if (destroy)
            return await new ConvergeRunner(stackDir, env, pveOptions: pve, only: only).DestroyAsync(confirmed);
        if (apply)
            return await new ConvergeRunner(stackDir, env, pveOptions: pve, only: only).ApplyAsync();

        // Plan diffs against live cluster state (best-effort; degrades to intent-only
        // if PVE creds are missing or the cluster is unreachable).
        var stateProvider = new ProxmoxClusterStateProvider(LoadOptions);
        return await new ConvergeRunner(stackDir, env, stateProvider, pveOptions: pve, only: only).PlanAsync();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

// Walk up from start looking for a file; returns null if not found.
static string? FindUp(string fileName, string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, fileName);
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}

static async Task<int> DiscoverAsync()
{
    var options = LoadOptions();
    if (options is null)
    {
        Console.Error.WriteLine(
            "Missing PVE config. Set PROXMOX_BASE_URL, PROXMOX_TOKEN_ID, PROXMOX_TOKEN_SECRET " +
            "(and optionally PROXMOX_VERIFY_TLS=false for self-signed nodes).");
        return 2;
    }

    var client = ProxmoxApi.Create(options);
    // Normalize before serializing so an unchanged cluster yields byte-identical
    // JSON across runs — otherwise the drift workflow opens a churny PR every time.
    var snapshot = SnapshotNormalizer.Normalize(await new ProxmoxDiscovery(client).DiscoverAsync());

    Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    }));
    return 0;
}

// discover-diff <before.json> <after.json>: render a Markdown table of what
// changed between two discover snapshots (used for the drift PR body). Pure I/O
// over local files — no cluster access.
static int RunDiscoverDiff(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: discover-diff <before.json> <after.json>");
        return 2;
    }

    var opts = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    var before = JsonSerializer.Deserialize<ClusterSnapshot>(File.ReadAllText(args[1]), opts)
        ?? new ClusterSnapshot { Nodes = [] };
    var after = JsonSerializer.Deserialize<ClusterSnapshot>(File.ReadAllText(args[2]), opts)
        ?? new ClusterSnapshot { Nodes = [] };

    Console.WriteLine(DriftSummary.Render(before, after));
    return 0;
}

static async Task<int> DiscoverUnifiAsync()
{
    var options = UnifiClientOptions.TryFromEnvironment();
    if (options is null)
    {
        Console.Error.WriteLine(
            "Missing UniFi config. Set UNIFI_BASE_URL and UNIFI_API_KEY " +
            "(and optionally UNIFI_VERIFY_TLS=false for self-signed consoles).");
        return 2;
    }

    var client = UnifiApi.Create(options);
    var snapshot = await new UnifiDiscovery(client).DiscoverAsync();

    Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    }));
    return 0;
}

// converge-unifi <file> [--apply]: reconcile a UnifiNetwork desired-state file
// (port-forwards) via UnifiSharp's legacy write adapter. Dry-run by default;
// --apply creates missing resources (add-only). Auth is the classic controller
// SESSION — UNIFI_LEGACY_BASE_URL / UNIFI_USERNAME / UNIFI_PASSWORD.
static async Task<int> RunConvergeUnifi(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: homelab-infra converge-unifi <network.yaml> [--apply]");
        return 2;
    }
    var path = Path.GetFullPath(args[1]);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"file not found: {path}");
        return 2;
    }
    var apply = args.Contains("--apply");

    var options = UnifiLegacyOptions.TryFromEnvironment();
    if (options is null)
    {
        Console.Error.WriteLine(
            "Missing UniFi legacy config. Set UNIFI_LEGACY_BASE_URL (…/proxy/network/api/s/default), " +
            "UNIFI_USERNAME, UNIFI_PASSWORD (and optionally UNIFI_VERIFY_TLS=false for self-signed).");
        return 2;
    }

    var doc = UnifiConverge.Load(path);
    Console.WriteLine($"converge-unifi: {doc.Metadata.Name} ({doc.Spec.PortForwards.Count} port-forward(s) declared) — {(apply ? "APPLY" : "dry-run")}");

#pragma warning disable CS0618 // legacy adapter is intentionally obsolete (ADR-0003)
    using var client = new UnifiLegacyClient(options);
    var result = await UnifiConverge.ReconcileAsync(doc, client, apply);
#pragma warning restore CS0618

    foreach (var name in result.Plan.AlreadyPresent)
        Console.WriteLine($"  = {name} (present)");
    foreach (var pf in result.Plan.ToCreate)
        Console.WriteLine($"  {(apply ? "+" : "~")} {pf.Name} → {pf.Interface} :{pf.DestinationPort} ⇒ {pf.ForwardIp}:{pf.ForwardPort}/{pf.Protocol}{(apply ? " (created)" : " (would create)")}");

    Console.WriteLine(result.Plan.ToCreate.Count == 0
        ? "All declared port-forwards present — nothing to do."
        : apply
            ? $"Applied: created {result.Created.Count}."
            : $"Plan: {result.Plan.ToCreate.Count} to create. Re-run with --apply to write.");
    return 0;
}

static ProxmoxClientOptions? LoadOptions()
{
    var baseUrl = Environment.GetEnvironmentVariable("PROXMOX_BASE_URL");
    var tokenId = Environment.GetEnvironmentVariable("PROXMOX_TOKEN_ID");
    var secret = Environment.GetEnvironmentVariable("PROXMOX_TOKEN_SECRET");
    if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(tokenId) || string.IsNullOrEmpty(secret))
    {
        return null;
    }

    var verifyTls = !string.Equals(
        Environment.GetEnvironmentVariable("PROXMOX_VERIFY_TLS"), "false", StringComparison.OrdinalIgnoreCase);

    return new ProxmoxClientOptions
    {
        BaseUrl = new Uri(baseUrl),
        TokenId = tokenId,
        TokenSecret = secret,
        VerifyTls = verifyTls,
    };
}
