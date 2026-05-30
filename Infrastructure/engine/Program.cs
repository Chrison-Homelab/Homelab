using System.Text.Json;
using ProxmoxSharp;

// Homelab.Infrastructure engine — the hub's first consumer of the ProxmoxSharp
// package. Reads live Proxmox state via the generated client.
//
// Usage:
//   homelab-infra discover        # dump a structured ClusterSnapshot as JSON
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
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Supported: discover");
        return 1;
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
