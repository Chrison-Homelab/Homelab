using System.Globalization;

namespace PowerOrchestrator.Core.Config;

/// <summary>
/// All orchestrator tunables, loaded from the environment (secrets.env / process env),
/// mirroring the <c>TryFromEnvironment()</c> pattern of ProxmoxClientOptions / UnifiClientOptions.
/// <para>
/// <see cref="Armed"/> defaults to <c>false</c> (dry-run): the automatic policy loop only
/// logs + emits metrics, it never powers anything off. This mirrors GamingIdleShutdown's
/// dryRun-default ethos and is load-bearing — desktop-01 still hosts always-on services
/// (cloudflared/forgejo/ERP), so automatic sleep must stay disarmed until those are
/// evacuated + QDevice/quorum is in place (issue #191). Manual operator commands act for real.
/// </para>
/// </summary>
public sealed record OrchestratorOptions
{
    /// <summary>When false (default), the automatic loop is dry-run: it never acts.</summary>
    public bool Armed { get; init; }

    /// <summary>How often the loop samples presence + node state.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long everyone must be away before a sleep is decided (debounce).</summary>
    public TimeSpan AwayDebounce { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The always-on node the orchestrator runs on and sends WoL from.</summary>
    public string SentinelNode { get; init; } = "nuc-01";

    /// <summary>Heavy nodes the orchestrator may sleep/wake.</summary>
    public IReadOnlyList<string> ManagedNodes { get; init; } = ["desktop-01"];

    /// <summary>MACs whose presence on the network counts as "someone home" (e.g. a phone).</summary>
    public IReadOnlyList<string> PresenceMacs { get; init; } = [];

    /// <summary>SSH user for the host poweroff (key-based; matches the proven manual path).</summary>
    public string SshUser { get; init; } = "root";

    /// <summary>Optional explicit SSH identity file; null lets ssh pick its default.</summary>
    public string? SshKeyPath { get; init; }

    /// <summary>WoL UDP port (9 by default; 7 is the legacy alternative).</summary>
    public int WolPort { get; init; } = 9;

    /// <summary>WoL broadcast address. Sender + target must share an L2 broadcast domain.</summary>
    public string WolBroadcast { get; init; } = "255.255.255.255";

    /// <summary>node-name → NIC MAC, for WoL. Seeded to match src/Proxmox/wake-node.sh.</summary>
    public IReadOnlyDictionary<string, string> NodeMacs { get; init; } = DefaultNodeMacs;

    /// <summary>node-name → IP, for the SSH poweroff. Seeded to the legacy /23 addresses.</summary>
    /// <summary>Where to read corosync.conf for the quorum precondition (ORCH_COROSYNC_CONF). The
    /// orchestrator runs on a cluster node, so the default is the live pmxcfs copy.</summary>
    public string CorosyncConfPath { get; init; } = "/etc/pve/corosync.conf";

    public IReadOnlyDictionary<string, string> NodeAddresses { get; init; } = DefaultNodeAddresses;

    /// <summary>Mirror of the wake-node.sh NODE_MACS registry.</summary>
    public static IReadOnlyDictionary<string, string> DefaultNodeMacs { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["desktop-01"] = "18:c0:4d:de:9f:82",
            ["hpe-01"] = "c8:d3:ff:9d:da:02",
            ["nuc-01"] = "b8:ae:ed:72:82:fe",
        };

    /// <summary>
    /// Node addresses BY NAME — the UniFi local-DNS records, never IPs. The nodes moved from the
    /// legacy /23 to VLAN 1000 (#37) and these defaults still carried the old addresses, so with
    /// no ORCH_NODE_ADDRS override the orchestrator was addressing nodes that no longer existed.
    /// A re-address is now a UniFi reservation edit, which is the whole point of the names.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultNodeAddresses { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["desktop-01"] = "desktop-01.homelab.chrison.internal",
            ["hpe-01"] = "hpe-01.homelab.chrison.internal",
            ["nuc-01"] = "nuc-01.homelab.chrison.internal",
        };

    /// <summary>Build options from environment variables, falling back to the defaults above.</summary>
    public static OrchestratorOptions FromEnvironment(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;

        var macs = MergeMap(DefaultNodeMacs, get("ORCH_NODE_MACS"));
        var addrs = MergeMap(DefaultNodeAddresses, get("ORCH_NODE_ADDRS"));

        return new OrchestratorOptions
        {
            Armed = ParseBool(get("ORCH_ARMED"), false),
            PollInterval = TimeSpan.FromSeconds(ParseInt(get("ORCH_POLL_SECONDS"), 60)),
            AwayDebounce = TimeSpan.FromMinutes(ParseInt(get("ORCH_AWAY_DEBOUNCE_MINUTES"), 10)),
            SentinelNode = NonEmpty(get("ORCH_SENTINEL_NODE")) ?? "nuc-01",
            ManagedNodes = SplitList(get("ORCH_MANAGED_NODES"), ["desktop-01"]),
            PresenceMacs = SplitList(get("ORCH_PRESENCE_MACS"), []).Select(NormalizeMac).ToList(),
            SshUser = NonEmpty(get("ORCH_SSH_USER")) ?? "root",
            SshKeyPath = NonEmpty(get("ORCH_SSH_KEY")),
            WolPort = ParseInt(get("ORCH_WOL_PORT"), 9),
            WolBroadcast = NonEmpty(get("ORCH_WOL_BROADCAST")) ?? "255.255.255.255",
            NodeMacs = macs,
            NodeAddresses = addrs,
            CorosyncConfPath = get("ORCH_COROSYNC_CONF") is { Length: > 0 } cc ? cc : "/etc/pve/corosync.conf",
        };
    }

    /// <summary>Lowercase, colon-separated MAC for stable comparison (UniFi reports lowercase).</summary>
    public static string NormalizeMac(string mac) =>
        mac.Replace("-", ":").Trim().ToLowerInvariant();

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool ParseBool(string? s, bool fallback) =>
        bool.TryParse(s, out var b) ? b : fallback;

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    private static IReadOnlyList<string> SplitList(string? csv, IReadOnlyList<string> fallback)
    {
        if (string.IsNullOrWhiteSpace(csv)) return fallback;
        var items = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.Length == 0 ? fallback : items;
    }

    // "node=mac,node2=mac2" merged over the defaults (case-insensitive keys).
    private static IReadOnlyDictionary<string, string> MergeMap(
        IReadOnlyDictionary<string, string> defaults, string? csv)
    {
        var map = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv)) return map;
        foreach (var pair in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            map[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }
        return map;
    }
}
