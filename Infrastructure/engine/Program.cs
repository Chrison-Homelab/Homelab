using System.Text.Json;
using ProxmoxSharp;
using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;

// Homelab.Infrastructure engine — the hub's first consumer of the ProxmoxSharp
// package. Reads live Proxmox state via the generated client.
//
// Usage:
//   homelab-infra discover               # dump a structured ClusterSnapshot as JSON
//   homelab-infra converge <stack-dir>   # plan post-create converge for a stack (dry run)
//   homelab-infra validate <path>        # validate a shape file / stack dir / nodes dir
//                                         #   against shape.schema.json (CI plan gate)
//
// PVE config comes from environment variables:
//   PROXMOX_BASE_URL   e.g. https://192.168.179.3:8006/api2/json
//   PROXMOX_TOKEN_ID   e.g. root@pam!claude-mcp
//   PROXMOX_TOKEN_SECRET
//   PROXMOX_VERIFY_TLS optional, "false" for self-signed nodes

var command = args.Length > 0 ? args[0] : "discover";

switch (command)
{
    case "discover":
        return await DiscoverAsync();
    case "converge":
        return await RunConverge(args);
    case "validate":
        return RunValidate(args);
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Supported: discover, converge, validate");
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
        Console.Error.WriteLine("usage: homelab-infra converge <stack-dir> [--apply]");
        return 2;
    }
    var stackDir = Path.GetFullPath(args[1]);
    var apply = args.Contains("--apply");

    var secretsPath = FindUp("secrets.env", Directory.GetCurrentDirectory());
    var env = SecretsEnv.Load(secretsPath);
    var runner = new ConvergeRunner(stackDir, env);
    return apply ? await runner.ApplyAsync() : runner.Plan();
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
    var snapshot = await new ProxmoxDiscovery(client).DiscoverAsync();

    Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    }));
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
