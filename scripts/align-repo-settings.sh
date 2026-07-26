#!/usr/bin/env bash
#
# align-repo-settings.sh — assert the ADR-0010 GitHub settings convention on every
# repo in the homelab org.
#
#   • Merge strategy: rebase (default) + squash (escape hatch), NEVER a merge commit.
#   • delete_branch_on_merge: on — stops stale-branch accumulation at the source.
#   • Idempotent: reports each repo as OK (already correct) or UPDATED. Safe to re-run.
#   • Read-then-write: only PATCHes repos that actually differ, so a no-op run makes
#     zero mutating API calls.
#
# WHY a script and not a checklist: a fresh GitHub repo starts at DEFAULTS (merge
# commits allowed, merged branches kept), and this project creates a repo every time a
# stack is extracted (ADR-0008). A prose convention drifts on the next extraction —
# which is exactly what issue #296 found across four repos with three different configs.
#
# Run it after creating any new repo, or any time to re-assert the convention.
#
# Usage:
#   scripts/align-repo-settings.sh              # apply to every repo in the org
#   scripts/align-repo-settings.sh --dry-run    # report drift, change nothing
#   scripts/align-repo-settings.sh <repo>...    # limit to named repos (bare names)
#
# Requires: gh (authenticated, admin on the target repos).
set -euo pipefail

ORG="${HOMELAB_GH_ORG:-Chrison-Homelab}"
DRY_RUN=0
REPOS=()

for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    -h|--help) sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) echo "unknown flag: $arg" >&2; exit 2 ;;
    *) REPOS+=("$arg") ;;
  esac
done

command -v gh >/dev/null 2>&1 || { echo "error: gh not found" >&2; exit 1; }

# The convention (ADR-0010). Keep in sync with CLAUDE.md's Git-workflow section.
WANT_REBASE=true
WANT_SQUASH=true
WANT_MERGE_COMMIT=false
WANT_DELETE_BRANCH=true

if [ ${#REPOS[@]} -eq 0 ]; then
  # --source excludes forks; we only own sources here.
  # while-read rather than `mapfile`: macOS ships bash 3.2, which has no mapfile.
  while IFS= read -r _name; do
    [ -n "$_name" ] && REPOS+=("$_name")
  done < <(gh repo list "$ORG" --limit 200 --source --json name --jq '.[].name' | sort)
fi

[ ${#REPOS[@]} -gt 0 ] || { echo "error: no repos found in org '$ORG'" >&2; exit 1; }

echo "ADR-0010 settings sweep — org '$ORG', ${#REPOS[@]} repo(s)$([ $DRY_RUN -eq 1 ] && echo ' [DRY RUN]')"
printf '%s\n' "---"

changed=0 ok=0 failed=0

for repo in "${REPOS[@]}"; do
  # `.github` is an org profile repo with no PRs — merge settings are meaningless there.
  if [ "$repo" = ".github" ]; then
    printf '  %-38s SKIP (org profile repo, no PRs)\n' "$repo"
    continue
  fi

  if ! cur=$(gh api "/repos/$ORG/$repo" \
        --jq '"\(.archived) \(.allow_rebase_merge) \(.allow_squash_merge) \(.allow_merge_commit) \(.delete_branch_on_merge)"' 2>/dev/null); then
    printf '  %-38s FAILED (cannot read; admin rights?)\n' "$repo"
    failed=$((failed + 1))
    continue
  fi
  read -r is_archived have_rebase have_squash have_merge have_delete <<<"$cur"

  # Archived repos are read-only — any PATCH returns 403 "Repository was archived".
  # That is CORRECT state for a retired stack (Komodo, ServArr), not drift, so skip it
  # rather than counting it as a failure and exiting non-zero.
  if [ "$is_archived" = "true" ]; then
    printf '  %-38s SKIP (archived, read-only)\n' "$repo"
    continue
  fi

  drift=()
  [ "$have_rebase" = "$WANT_REBASE" ]        || drift+=("rebase=$have_rebase -> $WANT_REBASE")
  [ "$have_squash" = "$WANT_SQUASH" ]        || drift+=("squash=$have_squash -> $WANT_SQUASH")
  [ "$have_merge"  = "$WANT_MERGE_COMMIT" ]  || drift+=("merge_commit=$have_merge -> $WANT_MERGE_COMMIT")
  [ "$have_delete" = "$WANT_DELETE_BRANCH" ] || drift+=("delete_on_merge=$have_delete -> $WANT_DELETE_BRANCH")

  if [ ${#drift[@]} -eq 0 ]; then
    printf '  %-38s OK\n' "$repo"
    ok=$((ok + 1))
    continue
  fi

  if [ $DRY_RUN -eq 1 ]; then
    printf '  %-38s DRIFT  %s\n' "$repo" "${drift[*]}"
    changed=$((changed + 1))
    continue
  fi

  # -F sends real JSON booleans; -f would send the STRING "true" and 422.
  if gh api -X PATCH "/repos/$ORG/$repo" \
      -F allow_rebase_merge="$WANT_REBASE" \
      -F allow_squash_merge="$WANT_SQUASH" \
      -F allow_merge_commit="$WANT_MERGE_COMMIT" \
      -F delete_branch_on_merge="$WANT_DELETE_BRANCH" >/dev/null 2>&1; then
    printf '  %-38s UPDATED  %s\n' "$repo" "${drift[*]}"
    changed=$((changed + 1))
  else
    printf '  %-38s FAILED to update  %s\n' "$repo" "${drift[*]}"
    failed=$((failed + 1))
  fi
done

printf '%s\n' "---"
echo "$ok already correct, $changed $([ $DRY_RUN -eq 1 ] && echo 'drifted' || echo 'updated'), $failed failed"
[ $failed -eq 0 ] || exit 1
