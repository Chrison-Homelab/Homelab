using Homelab.Infrastructure.Converge;
using Xunit;

namespace Homelab.Infrastructure.Tests;

public sealed class OpenBaoProvisionerTests
{
    private static string Status(bool init, bool sealed_, int n, int t) =>
        $"{{\"type\":\"shamir\",\"initialized\":{init.ToString().ToLowerInvariant()},\"sealed\":{sealed_.ToString().ToLowerInvariant()},\"t\":{t},\"n\":{n}}}";

    [Fact]
    public void Sealed_AfterRestart_IsNotAFailure()
    {
        // Manual unseal is the design. A converge must not go red because nobody has
        // typed the shares yet after a reboot.
        var r = OpenBaoProvisioner.Evaluate(Status(true, true, 3, 2), (3, 2));
        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        Assert.Contains("SEALED", r.Message);
    }

    [Fact]
    public void Unsealed_Hardened_Applies()
    {
        var r = OpenBaoProvisioner.Evaluate(Status(true, false, 3, 2), (3, 2));
        Assert.Equal(ApplyOutcome.Applied, r.Outcome);
        Assert.Contains("unsealed", r.Message);
    }

    [Fact]
    public void InstallerDefault_SingleShare_Fails()
    {
        // community-scripts inits 1-of-1; that must never read as converged.
        var r = OpenBaoProvisioner.Evaluate(Status(true, false, 1, 1), (3, 2));
        Assert.Equal(ApplyOutcome.Failed, r.Outcome);
    }

    [Fact]
    public void Uninitialised_Fails()
    {
        Assert.Equal(ApplyOutcome.Failed, OpenBaoProvisioner.Evaluate(Status(false, true, 0, 0), (3, 2)).Outcome);
    }

    [Fact]
    public void Probe_OnlyCountsVariableNames_NeverPrintsValues()
    {
        // The probe's output lands in converge logs (and CI). It may name the problem, never
        // echo the secret: grep -c on the NAMES, no cat/sed of the file.
        Assert.Contains("grep -cE '^BAO_(UNSEAL_KEY|ROOT_TOKEN)='", OpenBaoProvisioner.HardeningProbe);
        Assert.DoesNotContain("cat ", OpenBaoProvisioner.HardeningProbe);
        Assert.Contains(OpenBaoProvisioner.UnsealDropIn, OpenBaoProvisioner.HardeningProbe);
    }

    [Fact]
    public void Findings_EmptyOutput_MeansHardened()
    {
        Assert.Empty(OpenBaoProvisioner.UnhardenedFindings("\n  \n"));
        Assert.Equal(2, OpenBaoProvisioner.UnhardenedFindings("a\nb\n").Count);
    }

    [Fact]
    public void Config_UsesRaft_NeverTheRemovedFileBackend()
    {
        // OpenBao 2.7.0 removed `storage "file"`, which the 2.7.0 .deb still ships, so the
        // unit could not start (CT 3007, 2026-10-01).
        var s = new Homelab.Infrastructure.Shapes.Shape();
        s.Spec.Ctid = "3007";
        s.Spec.Network = new() { Reservation = new() { FixedIp = "10.10.30.7", LocalDnsRecord = "openbao.devops.chrison.internal" } };
        var cfg = OpenBaoProvisioner.RenderConfig(s);
        Assert.Contains("storage \"raft\"", cfg);
        Assert.DoesNotContain("storage \"file\"", cfg);
        Assert.Contains("node_id = \"openbao-3007\"", cfg);
        Assert.Contains("api_addr     = \"https://openbao.devops.chrison.internal:8200\"", cfg);
        Assert.Contains("tls_cert_file", cfg);   // never a plaintext listener
    }

    [Fact]
    public void ConfigScript_OnlyRestartsOnDrift()
    {
        // A restart SEALS the store, so a no-op converge must never restart it.
        var script = OpenBaoProvisioner.RenderConfigScript("x");
        Assert.Contains("if ! cmp -s", script);
        Assert.True(script.IndexOf("cmp -s", StringComparison.Ordinal) < script.IndexOf("systemctl restart", StringComparison.Ordinal));
    }
}
