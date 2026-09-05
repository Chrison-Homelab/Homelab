using System.Text.RegularExpressions;
using PowerOrchestrator.Core.Model;

namespace PowerOrchestrator.Core.Policy;

/// <summary>A prerequisite that must hold before automatic sleep can be safely armed.</summary>
public sealed record ArmPrecondition(string Name, bool Met, string Detail);

/// <summary>
/// Gate for arming automatic sleep — the #191 blockers, evaluated LIVE on every poll rather than
/// hard-coded. Two things make an automatic sleep safe:
/// <list type="bullet">
/// <item><b>services-evacuated</b> — no managed node is running a guest. Sleep shuts every guest
/// down, so anything still running is what a sleep would take away. (Guests must also have
/// onboot=0, or the next wake brings them back and the node is never idle again.)</item>
/// <item><b>quorum-majority</b> — the always-on nodes alone hold a strict majority of corosync
/// votes, or a QDevice is configured. Otherwise sleeping the managed nodes can drop the cluster
/// below quorum and freeze every write on the survivors.</item>
/// </list>
/// The status endpoint shows the result; <c>POST /policy/arm</c> refuses (409) while any is unmet.
/// Arming itself stays config-driven (<c>ORCH_ARMED=true</c>).
/// </summary>
public static class ArmGuard
{
    public const string Evacuated = "services-evacuated";
    public const string Quorum = "quorum-majority";

    /// <summary>Before the first poll nothing is known, and unknown is unmet.</summary>
    public static IReadOnlyList<ArmPrecondition> Unknown() =>
    [
        new(Evacuated, Met: false, "not evaluated yet — waiting for the first poll"),
        new(Quorum, Met: false, "not evaluated yet — waiting for the first poll"),
    ];

    /// <summary>Evaluate against the latest managed-node states and the cluster's corosync.conf text.</summary>
    public static IReadOnlyList<ArmPrecondition> Evaluate(
        IReadOnlyList<NodeState> managedStates,
        IReadOnlyCollection<string> managedNodes,
        string? corosyncConf)
    {
        var busy = managedStates.Where(s => s.IsOnline && s.RunningGuests > 0).ToList();
        var evacuated = busy.Count == 0
            ? new ArmPrecondition(Evacuated, true,
                "no guest is running on any managed node — a sleep takes nothing down")
            : new ArmPrecondition(Evacuated, false,
                $"still running guests: {string.Join(", ", busy.Select(b => $"{b.Name}={b.RunningGuests}"))} — " +
                "stop them and set onboot=0 (or a wake restarts them and the node is never idle) — issue #191");

        var (quorumMet, quorumDetail) = CorosyncQuorum.Evaluate(corosyncConf, managedNodes);
        return [evacuated, new ArmPrecondition(Quorum, quorumMet, quorumDetail)];
    }

    /// <summary>True only when every precondition is met. Otherwise <paramref name="unmet"/> lists the gaps.</summary>
    public static bool CanArm(IReadOnlyList<ArmPrecondition> preconditions, out IReadOnlyList<ArmPrecondition> unmet)
    {
        unmet = preconditions.Where(p => !p.Met).ToList();
        return unmet.Count == 0;
    }
}

/// <summary>
/// Quorum arithmetic from <c>/etc/pve/corosync.conf</c>: sums <c>quorum_votes</c> per node block,
/// treats every node not in the managed set as always-on, and asks whether the always-on votes
/// alone are a strict majority — or whether a <c>quorum { device { … } }</c> block (QDevice) exists.
/// Three nodes with one sleeper: 2 of 3, fine. Four nodes with two sleepers: 2 of 4, not fine
/// without a QDevice — which is exactly the case #191 warned about.
/// </summary>
public static class CorosyncQuorum
{
    private static readonly Regex NodeBlock = new(@"node\s*\{(?<body>[^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex Name = new(@"^\s*name:\s*(?<v>\S+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Votes = new(@"^\s*quorum_votes:\s*(?<v>\d+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Device = new(@"quorum\s*\{[^}]*device\s*\{", RegexOptions.Compiled);

    public static (bool Met, string Detail) Evaluate(string? conf, IReadOnlyCollection<string> managedNodes)
    {
        if (string.IsNullOrWhiteSpace(conf))
            return (false, "corosync.conf not readable — cannot prove the always-on nodes keep quorum (set ORCH_COROSYNC_CONF or run on a cluster node)");

        var nodes = NodeBlock.Matches(conf).Select(m =>
        {
            var body = m.Groups["body"].Value;
            var name = Name.Match(body).Groups["v"].Value;
            var votes = Votes.Match(body) is { Success: true } v ? int.Parse(v.Groups["v"].Value) : 1;
            return (Name: name, Votes: votes);
        }).Where(n => n.Name.Length > 0).ToList();
        if (nodes.Count == 0) return (false, "corosync.conf has no node blocks — cannot evaluate quorum");

        var total = nodes.Sum(n => n.Votes);
        var alwaysOn = nodes.Where(n => !managedNodes.Contains(n.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var alwaysOnVotes = alwaysOn.Sum(n => n.Votes);
        var hasDevice = Device.IsMatch(conf);

        if (hasDevice)
            return (true, $"QDevice configured — quorum holds regardless of which of {nodes.Count} node(s) sleep");
        if (alwaysOnVotes * 2 > total)
            return (true, $"always-on nodes ({string.Join(", ", alwaysOn.Select(n => n.Name))}) hold {alwaysOnVotes} of {total} votes — quorum survives every managed node sleeping");
        return (false, $"always-on nodes hold only {alwaysOnVotes} of {total} votes — sleeping the managed node(s) can drop quorum; add a QDevice — issue #191");
    }
}
