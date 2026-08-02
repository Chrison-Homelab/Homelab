using Homelab.Infrastructure.Shapes;
using ProxmoxSharp;
using ProxmoxSharp.Lxc;
using ProxmoxSharp.Vm;

namespace Homelab.Infrastructure.Converge;

// Orchestrates a converge PLAN: load a stack, order by dependsOn, and for each
// member report the post-create work (config, secret resolution, provisioner
// steps). Apply (live mutation) is the next BL-010 increment.
public sealed class ConvergeRunner
{
    private readonly string _stackDir;
    private readonly SecretsEnv _env;
    private readonly IClusterStateProvider? _stateProvider;
    private readonly INodeExec _exec;
    private readonly ProxmoxClientOptions? _pveOptions;
    private readonly IReadOnlyList<string>? _only;

    public ConvergeRunner(string stackDir, SecretsEnv env,
        IClusterStateProvider? stateProvider = null, INodeExec? exec = null,
        ProxmoxClientOptions? pveOptions = null,
        IReadOnlyList<string>? only = null)
    {
        _stackDir = stackDir;
        _env = env;
        _stateProvider = stateProvider;
        _exec = exec ?? new NodeExec();
        _pveOptions = pveOptions;
        _only = only;
    }

    // Loads + orders the stack, then narrows to `--only` (#306).
    //
    // Order matters: LoadStack merges stack defaults and TopologicalSorter runs over
    // ALL members, so a selected member keeps its defaults and its relative position
    // in the full dependency order. `AllOrdered` is returned alongside because
    // dependency lookups — service-derived secrets, forgejo create-vars — must still
    // see the whole stack even when only one member is being acted on.
    private (ShapeLoader.LoadedStack Loaded, IReadOnlyList<Shape> AllOrdered,
             IReadOnlyList<Shape> Members, IReadOnlyList<VmShape> VmMembers) LoadSelected()
    {
        var loaded = ShapeLoader.LoadStack(_stackDir);
        var allOrdered = TopologicalSorter.Order(loaded.Members);
        var (members, vms) = MemberSelection.Resolve(allOrdered, loaded.VmMembers, _only);
        return (loaded, allOrdered, members, vms);
    }

    // Header line naming what the selection excluded, so a scoped run can never be
    // mistaken in a log for a full-stack one.
    private void ReportSelection(IReadOnlyList<Shape> allOrdered, IReadOnlyList<Shape> members,
        IReadOnlyList<VmShape> allVms, IReadOnlyList<VmShape> vms)
    {
        if (_only is null) return;
        var skipped = (allOrdered.Count - members.Count) + (allVms.Count - vms.Count);
        Console.WriteLine($"--only {string.Join(",", _only)} — {members.Count + vms.Count} member(s) selected, {skipped} skipped.");
        var deps = MemberSelection.UnselectedDependencies(members);
        if (deps.Count > 0)
            Console.WriteLine($"  dependsOn outside the selection: {string.Join(", ", deps)} (must already be converged)");
        Console.WriteLine();
    }

    // State-diff PLAN (issue #45). For each desired shape, report the per-member
    // state vs live cluster: create (CT absent), up-to-date (exists, matches),
    // or drift (exists, listing the differing fields). Live state is best-effort:
    // if discovery can't connect, degrade to the intent-only plan with a warning.
    // Always READ-ONLY.
    public async Task<int> PlanAsync(CancellationToken ct = default)
    {
        var (loaded, allOrdered, ordered, vmMembers) = LoadSelected();
        var resolver = new SecretResolver(_env);
        var registry = ProvisionerRegistry.Default();

        var stackName = loaded.Stack?.Metadata.Name ?? Path.GetFileName(_stackDir);
        Console.WriteLine($"Converge plan — stack '{stackName}'  ({ordered.Count} member(s), dependency order)\n");
        ReportSelection(allOrdered, ordered, loaded.VmMembers, vmMembers);

        // Best-effort live cluster state. Null → degrade to intent-only.
        ClusterState? state = _stateProvider is null ? null : await _stateProvider.TryGetAsync(ct);
        if (_stateProvider is null)
            Console.WriteLine("(intent-only plan — no live cluster state provider configured)\n");
        else if (state is null)
            Console.WriteLine("⚠ live cluster state unavailable (no PVE creds / unreachable / discovery error)\n" +
                              "  → falling back to intent-only plan; per-shape state (create/drift) NOT computed.\n");
        else
            Console.WriteLine($"(diffing against live cluster state — {state.Count} container(s) discovered)\n");

        var blocked = 0;
        int toCreate = 0, drifted = 0, upToDate = 0, describeOnly = 0;
        foreach (var s in ordered)
        {
            var sp = s.Spec;
            var ch = sp.Source?.Channel ?? "stable";
            Console.WriteLine($"▸ {s.Metadata.Name}  (ctid {sp.Ctid}, app '{sp.App}', node {sp.Node}, channel {ch})");
            if (sp.DependsOn.Count > 0)
                Console.WriteLine($"    dependsOn: {string.Join(", ", sp.DependsOn)}");

            if (sp.Config.Count > 0)
                Console.WriteLine($"    config:    {string.Join(", ", sp.Config.Keys)}");

            // describe-only: the shape documents an adopted guest, so a mismatch is
            // EXPECTED, not drift. Reporting it as drift is what made every plan noisy and
            // every apply want to write to it (#325). Still listed — describing it is the
            // point — but it must not inflate the drift count or imply pending work.
            if (s.IsDescribeOnly())
            {
                describeOnly++;
                Console.WriteLine("    state:     DESCRIBE-ONLY (adopted; documented, never written)");
                Console.WriteLine();
                continue;
            }

            // State line — only when we have live state.
            if (state is not null)
            {
                var diff = StateDiffer.Diff(s, state);
                switch (diff.Status)
                {
                    case ShapeDiffStatus.Create:
                        toCreate++;
                        Console.WriteLine("    state:     CREATE (CT absent on cluster)");
                        break;
                    case ShapeDiffStatus.UpToDate:
                        upToDate++;
                        Console.WriteLine("    state:     UP-TO-DATE (CT exists, comparable config matches)");
                        break;
                    case ShapeDiffStatus.Drift:
                        drifted++;
                        Console.WriteLine($"    state:     DRIFT (CT exists; {diff.Fields.Count} field(s) differ)");
                        foreach (var f in diff.Fields)
                            Console.WriteLine($"      drift:   {f.Field}: desired {f.Desired} ≠ live {f.Live}");
                        break;
                    default:
                        Console.WriteLine("    state:     UNKNOWN (ctid not numeric — cannot correlate to live state)");
                        break;
                }
            }

            var secrets = resolver.Plan(sp);
            foreach (var r in secrets)
            {
                var mark = r.Ready ? "✓" : "✗";
                if (!r.Ready) blocked++;
                Console.WriteLine($"    secret {mark} {r.Name}: {r.Description}");
            }

            foreach (var step in registry.For(sp.Provisioner ?? sp.App).PlanSteps(s))
                Console.WriteLine($"    post-create: {step}");

            Console.WriteLine();
        }

        if (state is not null)
            Console.WriteLine($"State summary — {toCreate} to create, {drifted} drifted, {upToDate} up-to-date"
                              + (describeOnly > 0 ? $", {describeOnly} describe-only." : "."));

        // kind: VM members — planned via ProxmoxSharp (read-only).
        if (vmMembers.Count > 0)
        {
            Console.WriteLine($"\nVM members ({vmMembers.Count}) — via ProxmoxSharp:\n");
            if (_pveOptions is null)
                Console.WriteLine("  (VM plan skipped — no PVE credentials configured)\n");
            else
            {
                var writer = QemuWriter.Create(_pveOptions);
                foreach (var vm in vmMembers)
                {
                    // VM 2000 is the member that motivated #325: its plan was a SetConfig
                    // against an adopted HAOS install. Don't even ask the writer.
                    if (vm.IsDescribeOnly())
                    {
                        Console.WriteLine($"▸ {vm.Metadata.Name}  (vmid {vm.Spec.Vmid}, node {vm.Spec.Node}) — DESCRIBE-ONLY (adopted; documented, never written)");
                        Console.WriteLine();
                        continue;
                    }
                    try
                    {
                        var plan = await VmConverger.PlanAsync(writer, vm, ct);
                        Console.WriteLine($"▸ {vm.Metadata.Name}  (vmid {vm.Spec.Vmid}, node {vm.Spec.Node}) — {plan.Kind}");
                        foreach (var c in plan.Changes) Console.WriteLine($"    {c}");
                        if (!plan.HasChanges) Console.WriteLine("    (desired state already satisfied)");
                    }
                    catch (Exception ex) { Console.WriteLine($"▸ {vm.Metadata.Name}: PLAN ERROR — {ex.Message}"); }
                    Console.WriteLine();
                }
            }
        }

        Console.WriteLine(blocked == 0
            ? "Plan OK — all declared secrets resolvable. (Run with --apply to converge.)"
            : $"Plan OK — {blocked} secret input(s) not resolvable in this context "
              + "(service-derived from a not-yet-deployed dependency, or env not set here). "
              + "These are enforced at --apply, not in a dry-run.");
        // Dry-run never fails on secret resolvability: service-derived secrets
        // (e.g. a Forgejo runner token) can't resolve until their dependency is
        // live, and a plan must run without apply-time credentials. ApplyAsync
        // re-checks required env secrets and fails there if any are missing.
        return 0;
    }

    // Live converge — idempotent per provisioner. Guards: the CT must already
    // exist (create stays with the renderer for now); required env secrets must
    // be present. Provisioners that aren't idempotent-safe yet report Skipped.
    public async Task<int> ApplyAsync()
    {
        var (loaded, allOrdered, ordered, vmMembers) = LoadSelected();
        var resolver = new SecretResolver(_env);
        var registry = ProvisionerRegistry.Default();
        var exec = _exec;
        // byName spans the WHOLE stack, never just the selection: service-derived
        // secrets and forgejo create-vars resolve dependencies by name, and a scoped
        // apply must still be able to reach a dependency it isn't itself converging.
        var byName = allOrdered.ToDictionary(s => s.Metadata.Name, StringComparer.Ordinal);
        var deriver = new SecretDeriver(_env, exec, byName);
        var ctx = new ConvergeContext(exec, _env, byName, deriver,
            Progress: m => Console.WriteLine($"    {m}"));
        var creator = new CommunityScriptsCreator(exec);
        var reconciler = new CtConfigReconciler(exec);
        var mountReconciler = new MountReconciler(exec);
        var ct = CancellationToken.None;

        var stackName = loaded.Stack?.Metadata.Name ?? Path.GetFileName(_stackDir);
        Console.WriteLine($"Converge APPLY — stack '{stackName}'  ({ordered.Count} member(s), dependency order)\n");
        ReportSelection(allOrdered, ordered, loaded.VmMembers, vmMembers);

        // Guard for `--only` (#306): a dependency left out of the selection is being
        // ASSUMED already converged. Verify that rather than trusting it — applying a
        // member whose dependency doesn't exist yet fails in confusing, half-applied
        // ways deep inside a provisioner.
        var unselectedDeps = MemberSelection.UnselectedDependencies(ordered);
        var missingDeps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dep in unselectedDeps)
        {
            if (!byName.TryGetValue(dep, out var d) || d.Spec.Node is not { } dn || d.Spec.Ctid is not { } dc)
                continue; // not correlatable to a live CT — nothing to assert
            if (!await creator.ExistsAsync(dn, dc, ct)) missingDeps.Add(dep);
        }

        int failed = 0, applied = 0, nochange = 0, skipped = 0;
        foreach (var s in ordered)
        {
            var sp = s.Spec;
            Console.WriteLine($"▸ {s.Metadata.Name}  (ctid {sp.Ctid}, app '{sp.App}', node {sp.Node})");

            // describe-only members are never written — not by a full apply, and not by an
            // apply that names them in --only either. Making --only able to override this
            // would put the guarantee back in the operator's hands, which is the thing #325
            // set out to stop.
            if (s.IsDescribeOnly())
            {
                Console.WriteLine("    SKIPPED: describe-only (adopted member — converge documents it, never writes it)");
                skipped++; Console.WriteLine(); continue;
            }

            var unmet = sp.DependsOn.Where(missingDeps.Contains).ToList();
            if (unmet.Count > 0)
            {
                Console.WriteLine($"    FAILED: dependsOn {string.Join(", ", unmet)} — outside --only and NOT converged. " +
                                  "Converge the dependency first, or include it in --only.");
                failed++; Console.WriteLine(); continue;
            }

            // Lifecycle: ensure the CT exists (create via community-scripts if absent).
            if (sp.Node is { } node && sp.Ctid is { } ctid)
            {
                if (await creator.ExistsAsync(node, ctid, ct))
                {
                    Console.WriteLine($"    CT {ctid} exists");
                }
                else
                {
                    IReadOnlyDictionary<string, string>? extra = null;
                    if (sp.App == "forgejo-runner")
                    {
                        try { extra = await ForgejoRunnerCreateVarsAsync(s, ctx, ct); }
                        catch (Exception ex) { Console.WriteLine($"    FAILED: create vars — {ex.Message}"); failed++; Console.WriteLine(); continue; }
                    }
                    var created = await creator.EnsureAsync(s, extra, ct);
                    Console.WriteLine($"    CREATE {created.Outcome.ToString().ToUpperInvariant()}: {created.Message}");
                    if (created.Outcome == ApplyOutcome.Failed) { failed++; Console.WriteLine(); continue; }
                    if (created.Outcome == ApplyOutcome.Applied) applied++;
                }
            }

            // Update reconciliation: bring host-level CT config (cores/memory/tags)
            // in line with the shape, in place. Idempotent — no-op when matched.
            if (sp.Node is not null && sp.Ctid is not null)
            {
                try
                {
                    var cfg = await reconciler.ReconcileAsync(s, ct);
                    switch (cfg.Outcome)
                    {
                        case ApplyOutcome.Applied:
                            applied++; Console.WriteLine($"    config APPLIED: {cfg.Message}"); break;
                        case ApplyOutcome.Failed:
                            failed++; Console.WriteLine($"    config FAILED: {cfg.Message}"); Console.WriteLine(); continue;
                        default:
                            Console.WriteLine($"    config: {cfg.Message}"); break;
                    }
                }
                catch (Exception ex) { Console.WriteLine($"    config FAILED: {ex.Message}"); failed++; Console.WriteLine(); continue; }
            }

            // Mounts + hookscript: apply declared mpN entries (e.g. the shared /data NFS
            // path-bind). Idempotent — no-op when already in place. Community-scripts create
            // does NOT provision mounts, so this is where they land.
            if (sp.Node is not null && sp.Ctid is not null && (sp.Mounts.Count > 0 || sp.Hookscript is not null))
            {
                try
                {
                    var mnt = await mountReconciler.ReconcileAsync(s, ct);
                    switch (mnt.Outcome)
                    {
                        case ApplyOutcome.Applied:
                            applied++; Console.WriteLine($"    mounts APPLIED: {mnt.Message}"); break;
                        case ApplyOutcome.Failed:
                            failed++; Console.WriteLine($"    mounts FAILED: {mnt.Message}"); Console.WriteLine(); continue;
                        default:
                            Console.WriteLine($"    mounts: {mnt.Message}"); break;
                    }
                }
                catch (Exception ex) { Console.WriteLine($"    mounts FAILED: {ex.Message}"); failed++; Console.WriteLine(); continue; }
            }

            // Guard: required env secrets must be present.
            var missing = resolver.Plan(sp).Where(r => !r.Ready).ToList();
            if (missing.Count > 0)
            {
                foreach (var m in missing) Console.WriteLine($"    FAILED: secret '{m.Name}' — {m.Description}");
                failed++; Console.WriteLine(); continue;
            }

            try
            {
                var result = await registry.For(sp.Provisioner ?? sp.App).ApplyAsync(s, ctx);
                Console.WriteLine($"    {result.Outcome.ToString().ToUpperInvariant()}: {result.Message}");
                switch (result.Outcome)
                {
                    case ApplyOutcome.Applied: applied++; break;
                    case ApplyOutcome.NoChange: nochange++; break;
                    case ApplyOutcome.Skipped: skipped++; break;
                    default: failed++; break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    FAILED: {ex.Message}");
                failed++;
            }
            Console.WriteLine();
        }

        // kind: VM members — applied via ProxmoxSharp (create if absent, else set changed keys).
        if (vmMembers.Count > 0)
        {
            if (_pveOptions is null)
                Console.WriteLine("VM members: SKIPPED (no PVE credentials configured)\n");
            else
            {
                var writer = QemuWriter.Create(_pveOptions);
                foreach (var vm in vmMembers)
                {
                    Console.WriteLine($"▸ {vm.Metadata.Name}  (vmid {vm.Spec.Vmid}, node {vm.Spec.Node})");
                    if (vm.IsDescribeOnly())
                    {
                        Console.WriteLine("    SKIPPED: describe-only (adopted member — converge documents it, never writes it)");
                        skipped++; Console.WriteLine(); continue;
                    }
                    try
                    {
                        var res = await VmConverger.ApplyAsync(writer, vm);
                        Console.WriteLine($"    {res.Outcome.ToString().ToUpperInvariant()}: {res.Message}");
                        switch (res.Outcome)
                        {
                            case ApplyOutcome.Applied: applied++; break;
                            case ApplyOutcome.NoChange: nochange++; break;
                            case ApplyOutcome.Skipped: skipped++; break;
                            default: failed++; break;
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"    FAILED: {ex.Message}"); failed++; }
                    Console.WriteLine();
                }
            }
        }

        Console.WriteLine($"Apply summary — {applied} applied, {nochange} no-change, {skipped} skipped, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    // Destroy lifecycle (issue #101). Tears down the stack's CTs in REVERSE
    // dependency order (dependents before their dependencies). Gated: without
    // `confirmed` it's a read-only destroy PLAN (what exists, what would go);
    // with `confirmed` it stops + destroys each CT.
    //
    // ADD-ONLY guardrail (CLAUDE.md): destroy is CT-scoped only. It NEVER removes
    // shared external resources — Cloudflare tunnels/DNS, GitHub/Forgejo runner
    // registrations — even though converge creates them. Those are torn down by
    // hand if ever needed.
    public async Task<int> DestroyAsync(bool confirmed, CancellationToken ct = default)
    {
        var (loaded, allOrdered, ordered, vmMembers) = LoadSelected();
        var teardown = Enumerable.Reverse(ordered).ToList(); // dependents first
        var creator = new CommunityScriptsCreator(_exec);

        var stackName = loaded.Stack?.Metadata.Name ?? Path.GetFileName(_stackDir);
        Console.WriteLine($"Converge DESTROY — stack '{stackName}'  ({teardown.Count} member(s), reverse dependency order)\n");
        ReportSelection(allOrdered, ordered, loaded.VmMembers, vmMembers);
        // `--only` narrows destroy too. Scoping matters MORE here than on apply: the
        // unscoped form takes out every CT in the stack, so being able to name one is
        // the difference between retiring a member and flattening the stack. Note the
        // reverse-order guarantee only holds WITHIN the selection — a dependency left
        // out of it is not torn down, which is the intent.
        if (!confirmed)
            Console.WriteLine("(dry-run destroy plan — re-run with --yes to actually stop + destroy)\n");
        Console.WriteLine("Note: external resources (Cloudflare tunnels/DNS, runner registrations) are\n" +
                          "shared + ADD-ONLY — destroy does NOT remove them. CT teardown only.\n");

        int destroyed = 0, absent = 0, failed = 0, planned = 0, refused = 0;
        foreach (var s in teardown)
        {
            var sp = s.Spec;

            // Refused outright, plan or apply. "Adopted" generally means older and less
            // replaceable than the stack describing it, so destroy is the one verb where
            // getting this wrong is unrecoverable (#325).
            if (s.IsDescribeOnly())
            {
                Console.WriteLine($"▸ {s.Metadata.Name}  (ctid {sp.Ctid}): REFUSED — describe-only (adopted; destroy would remove a guest converge never created)");
                refused++;
                continue;
            }

            if (sp.Node is not { } node || sp.Ctid is not { } ctid)
            {
                Console.WriteLine($"▸ {s.Metadata.Name}: SKIP (no node/ctid)");
                continue;
            }

            if (!confirmed)
            {
                var exists = await creator.ExistsAsync(node, ctid, ct);
                Console.WriteLine($"▸ {s.Metadata.Name}  (ctid {ctid}, node {node}): " +
                                  (exists ? "would STOP + DESTROY" : "absent — nothing to do"));
                if (exists) planned++; else absent++;
                continue;
            }

            var res = await DestroyCtAsync(creator, node, ctid, ct);
            Console.WriteLine($"▸ {s.Metadata.Name}  (ctid {ctid}, node {node}): " +
                              $"{res.Outcome.ToString().ToUpperInvariant()} — {res.Message}");
            switch (res.Outcome)
            {
                case ApplyOutcome.Applied: destroyed++; break;
                case ApplyOutcome.NoChange: absent++; break;
                default: failed++; break;
            }
        }

        Console.WriteLine();
        var refusedNote = refused > 0 ? $", {refused} refused (describe-only)" : "";
        Console.WriteLine(confirmed
            ? $"Destroy summary — {destroyed} destroyed, {absent} absent, {failed} failed{refusedNote}."
            : $"Destroy plan — {planned} to destroy, {absent} absent{refusedNote}. (Re-run with --yes to apply.)");
        return failed == 0 ? 0 : 1;
    }

    // CT teardown. Prefers the ProxmoxSharp API write path (PctWriter, mirroring the
    // VM path via QemuWriter) when PVE credentials are present — force+purge destroys
    // even a running CT in one call. Falls back to community-scripts `pct` over SSH
    // when there are no creds or the ctid isn't numeric. Exists is checked first so an
    // already-absent CT reads as NoChange instead of an API error (#149 / #101).
    private async Task<ApplyResult> DestroyCtAsync(CommunityScriptsCreator creator, string node, string ctid, CancellationToken ct)
    {
        if (!await creator.ExistsAsync(node, ctid, ct))
            return ApplyResult.NoChange($"CT {ctid} absent");
        if (_pveOptions is null || !int.TryParse(ctid, out var id))
            return await creator.DestroyAsync(node, ctid, ct);
        try
        {
            var pct = PctWriter.Create(_pveOptions);
            var upid = await pct.DeleteAsync(node, id, force: true, purge: true, ct: ct);
            if (upid is null) return ApplyResult.Applied($"CT {ctid} destroyed (no task returned)");
            var exit = await pct.WaitForTaskAsync(node, upid, ct: ct);
            return string.Equals(exit, "OK", StringComparison.OrdinalIgnoreCase)
                ? ApplyResult.Applied($"CT {ctid} destroyed via ProxmoxSharp (task {exit})")
                : ApplyResult.Failed($"CT {ctid} destroy task ended: {exit ?? "(no exit status)"}");
        }
        catch (Exception ex)
        {
            return ApplyResult.Failed($"CT {ctid} destroy via ProxmoxSharp failed: {ex.Message}");
        }
    }

    // The forgejo-runner DEV script registers AT create time, so it needs the
    // instance URL + a derived runner token (+ uuid + labels) as create vars.
    // Only computed when the CT is absent (avoids minting an unused token).
    private static async Task<IReadOnlyDictionary<string, string>> ForgejoRunnerCreateVarsAsync(
        Shapes.Shape s, ConvergeContext ctx, CancellationToken ct)
    {
        var depName = s.Spec.DependsOn.FirstOrDefault() ?? "forgejo";
        if (!ctx.ByName.TryGetValue(depName, out var dep) || dep.Spec.Node is not { } dn || dep.Spec.Ctid is not { } dc)
            throw new InvalidOperationException($"dependency '{depName}' not resolvable");
        var ip = await ctx.Exec.InContainerAsync(dn, dc, "hostname -I | awk '{print $1}'", ct);
        if (!ip.Ok || ip.Stdout.Length == 0) throw new InvalidOperationException("could not resolve forgejo address");

        var sec = s.Spec.Secrets.FirstOrDefault(x => x.ValueFrom.Service is not null)
            ?? throw new InvalidOperationException("no service-derived runner token declared");
        var token = await ctx.Deriver.ResolveAsync(sec.ValueFrom, ct);
        var labels = s.Spec.Config.TryGetValue("runnerLabels", out var l) ? ConfigExt.Describe(l) : "homelab";

        return new Dictionary<string, string>
        {
            ["var_forgejo_instance"] = $"http://{ip.Stdout.Trim()}:3000",
            ["var_forgejo_runner_token"] = token,
            ["var_forgejo_runner_uuid"] = Guid.NewGuid().ToString(),
            ["var_runner_labels"] = labels,
        };
    }
}
