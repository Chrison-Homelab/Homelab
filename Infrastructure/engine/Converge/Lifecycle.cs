using Homelab.Infrastructure.Shapes;

namespace Homelab.Infrastructure.Converge;

// `spec.manage` — whether converge may WRITE to a member (issue #325).
//
// A guest adopted in place predates the shape that describes it, so its live config will
// never fully match. Converge cannot tell that difference is intentional, so it reported
// the member as drift on EVERY run and every apply wanted to "fix" it. On SmartHome that
// meant an unscoped apply proposing SetConfig on VM 2000 (Home Assistant) — a VM the
// stack's own CLAUDE.md documents as "never re-provisioned by converge --apply".
//
// That guarantee lived only in prose. `--only` (#306) made it possible to avoid, but
// opt-in safety on a destructive default is the wrong shape for something whose failure
// mode is rewriting an irreplaceable VM. `manage: describe-only` moves the constraint
// into the shape, where it cannot be forgotten.
//
// DESCRIBE-ONLY IS NOT THE SAME AS EXCLUDED. The member is still loaded, still shown in
// plans, and still resolvable as a `dependsOn` target — describing it is the point.
public static class Lifecycle
{
    public const string DescribeOnly = "describe-only";

    public static bool IsDescribeOnly(string? manage) =>
        string.Equals(manage, DescribeOnly, StringComparison.OrdinalIgnoreCase);

    public static bool IsDescribeOnly(this Shape s) => IsDescribeOnly(s.Spec.Manage);
    public static bool IsDescribeOnly(this VmShape v) => IsDescribeOnly(v.Spec.Manage);
}
