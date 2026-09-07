using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// Unattended security updates baseline (#436). Fake exec; assertions are about the commands
// issued and how the in-guest script's report is read.
public sealed class SecurityUpdatesReconcilerTests
{
    private static Shape Guest(bool? optOut = null)
    {
        var s = new Shape { Metadata = new ShapeMetadata { Name = "sonarr" } };
        s.Spec.Node = "hpe-01"; s.Spec.Ctid = "5101";
        if (optOut is not null) s.Spec.Config[SecurityUpdatesReconciler.ConfigKey] = !optOut.Value;
        return s;
    }

    private sealed class FakeExec : INodeExec
    {
        private readonly string _status; private readonly string _scriptOut; private readonly int _scriptRc;
        public List<string> Commands { get; } = new();
        public FakeExec(string status = "status: running", string scriptOut = "changed=0\n", int scriptRc = 0)
        { _status = status; _scriptOut = scriptOut; _scriptRc = scriptRc; }
        public Task<ExecResult> OnNodeAsync(string node, string cmd, CancellationToken ct = default)
        { Commands.Add(cmd); return Task.FromResult(new ExecResult(0, _status, "")); }
        public Task<ExecResult> InContainerAsync(string node, string ctid, string cmd, CancellationToken ct = default)
        { Commands.Add(cmd); return Task.FromResult(new ExecResult(_scriptRc, _scriptOut, _scriptRc == 0 ? "" : "boom")); }
    }

    [Fact]
    public async Task FirstRun_InstallsAndReportsEveryChange()
    {
        var exec = new FakeExec(scriptOut: "installed unattended-upgrades\nwrote /etc/apt/apt.conf.d/20auto-upgrades\nwrote /etc/apt/apt.conf.d/52homelab-security-updates\nenabled apt-daily-upgrade.timer\nchanged=4\n");
        var r = await new SecurityUpdatesReconciler(exec).ReconcileAsync(Guest());

        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        Assert.Contains("installed unattended-upgrades", r.Message);
        Assert.Contains("enabled apt-daily-upgrade.timer", r.Message);
        Assert.Equal(2, exec.Commands.Count);                      // pct status, then the script
        Assert.Contains("pct status 5101", exec.Commands[0]);
    }

    [Fact]
    public async Task InPlace_IsNoChange()
    {
        var r = await new SecurityUpdatesReconciler(new FakeExec()).ReconcileAsync(Guest());
        Assert.Equal(ApplyOutcome.NoChange, r.Outcome);
    }

    [Fact]
    public async Task StoppedGuest_IsSkippedNotStarted()
    {
        var exec = new FakeExec(status: "status: stopped");
        var r = await new SecurityUpdatesReconciler(exec).ReconcileAsync(Guest());
        Assert.Equal(ApplyOutcome.Skipped, r.Outcome);
        Assert.Single(exec.Commands);                               // only the status probe
    }

    [Fact]
    public async Task OptOut_IsSkippedWithoutTouchingTheGuest()
    {
        var exec = new FakeExec();
        var r = await new SecurityUpdatesReconciler(exec).ReconcileAsync(Guest(optOut: true));
        Assert.Equal(ApplyOutcome.Skipped, r.Outcome);
        Assert.Empty(exec.Commands);
        Assert.Contains("OPTED OUT", SecurityUpdatesReconciler.PlanStep(Guest(optOut: true)));
    }

    [Fact]
    public async Task NonAptGuest_IsSkipped()
    {
        var r = await new SecurityUpdatesReconciler(new FakeExec(scriptOut: "not-apt\nchanged=0\n")).ReconcileAsync(Guest());
        Assert.Equal(ApplyOutcome.Skipped, r.Outcome);
    }

    [Fact]
    public async Task ScriptFailure_IsFailedWithTheError()
    {
        var r = await new SecurityUpdatesReconciler(new FakeExec(scriptOut: "", scriptRc: 100)).ReconcileAsync(Guest());
        Assert.Equal(ApplyOutcome.Failed, r.Outcome);
        Assert.Contains("boom", r.Message);
    }

    [Fact]
    public void Script_PinsThePolicyWeRelyOn()
    {
        // Origins are cleared and pinned to the SECURITY pockets — Debian's default also admits
        // the plain stable pocket; no auto-reboot; and nothing here runs a dist-upgrade — that
        // stays a person's act via upgrade-guests.sh.
        var s = SecurityUpdatesReconciler.Script;
        Assert.Contains("Unattended-Upgrade::Automatic-Reboot \"false\"", s);
        Assert.Contains("APT::Periodic::Unattended-Upgrade \"1\"", s);
        Assert.Contains("#clear Unattended-Upgrade::Allowed-Origins;", s);
        Assert.Contains("#clear Unattended-Upgrade::Origins-Pattern;", s);
        Assert.Contains("label=Debian-Security", s);
        // key=value patterns go in Origins-Pattern, distro:archive in Allowed-Origins — never mixed.
        var pattern = s[s.IndexOf("Origins-Pattern {", StringComparison.Ordinal)..s.IndexOf("Allowed-Origins {", StringComparison.Ordinal)];
        Assert.DoesNotContain("${distro_id}:", pattern);
        var allowed = s[s.IndexOf("Allowed-Origins {", StringComparison.Ordinal)..s.IndexOf("Automatic-Reboot", StringComparison.Ordinal)];
        Assert.DoesNotContain("origin=", allowed);
        Assert.Contains("${distro_id}:${distro_codename}-security", s);
        Assert.DoesNotContain("label=Debian\"", s);                 // the plain stable pocket is NOT admitted
        Assert.DoesNotContain("-updates\"", s);
        Assert.DoesNotContain("dist-upgrade", s);
        Assert.DoesNotContain("apt-get -qq -y upgrade", s);
        Assert.Contains("cmp -s", s);                                // rewrite only on change
    }
}
