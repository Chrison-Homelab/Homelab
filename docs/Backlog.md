# Homelab Backlog

> **The backlog now lives on GitHub.** This file is a pointer.

- **Board:** [Homelab Backlog (Project #7)](https://github.com/users/ChrisonSimtian/projects/7)
  — the Idea → Planned → In Progress → Done view, with **Stage** and **Priority** fields.
- **Items:** [GitHub Issues](https://github.com/Chrison-dev/Homelab/issues) — one
  per `BL-xxx`. Labels carry priority (`priority:high|medium|low`) and tags
  (`iac`, `proxmox`, `networking`, `ci-cd`, …).
- **Plans:** the detailed implementation plans stay in-repo under
  [`docs/plans/`](plans/) and are linked from their issues.

## Conventions

- New backlog items → open an issue titled `BL-### — <summary>`, add it to the
  board, and set Stage + Priority. Reference the issue from commits/PRs (`#NN`).
- Substantial items get a `docs/plans/BL-###-<name>.md` plan, linked from the issue.
- Discovered cluster drift is tracked separately in
  [`docs/discovered-state.json`](discovered-state.json) (auto-PR'd by the
  `discover-drift` workflow).

_Migrated from a flat Markdown backlog on 2026-05-31; see git history for the
prior `BL-001`…`BL-014` write-ups (also captured in the issues)._
