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
// `manage: retired` — the member is GONE and must not come back (issue #362).
//
// Retirement was previously recorded as a `retired` tag plus a comment. The engine reads
// neither, so a retired member whose CT had been deleted looked exactly like a member that
// had never been created: converge saw CREATE and made a new one. That is not theoretical —
// a Media deploy rebuilt CT 5113 (youtarr) months after it was superseded by the podman
// host, leaving a fresh Docker host running nothing with the youtube NFS share mounted in.
//
// Two other shapes carried the same tag with their CTs already deleted (monitoring CT 4000,
// leapmotor-mate CT 4100), so the next full converge of either stack would have done it
// again. Comments do not stop a converge; this does.
//
// WHY NOT JUST DELETE THE SHAPE: these shapes are the rollback record for the in-flight
// Podman migration (ADR-0009) and carry the reasoning for how the thing was built. Keeping
// them costs nothing once converge refuses to act on them, and the file going missing is a
// worse audit trail than a file that says "this is retired, here is what replaced it".
public static class Lifecycle
{
    public const string DescribeOnly = "describe-only";
    public const string Retired = "retired";

    public static bool IsDescribeOnly(string? manage) =>
        string.Equals(manage, DescribeOnly, StringComparison.OrdinalIgnoreCase);

    public static bool IsDescribeOnly(this Shape s) => IsDescribeOnly(s.Spec.Manage);
    public static bool IsDescribeOnly(this VmShape v) => IsDescribeOnly(v.Spec.Manage);

    public static bool IsRetired(string? manage) =>
        string.Equals(manage, Retired, StringComparison.OrdinalIgnoreCase);

    public static bool IsRetired(this Shape s) => IsRetired(s.Spec.Manage);
    public static bool IsRetired(this VmShape v) => IsRetired(v.Spec.Manage);

    // Both stances mean "converge must not write to this member". Anything gating a WRITE
    // should ask this, so a new stance cannot be added without the write paths honouring it.
    public static bool IsReadOnly(this Shape s) => s.IsDescribeOnly() || s.IsRetired();
    public static bool IsReadOnly(this VmShape v) => v.IsDescribeOnly() || v.IsRetired();
}
