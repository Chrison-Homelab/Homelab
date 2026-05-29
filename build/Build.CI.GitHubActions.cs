using Fallout.Common.CI.GitHubActions;

// CI: validate the /Infrastructure shapes on every PR and push to main.
// Linux-only, fast feedback. No submodule checkout needed — shape validation
// only touches the hub. (The private-submodule PAT is scoped to the release
// snapshot workflow, which does need stack contents.)
[GitHubActions(
    "ci",
    GitHubActionsImage.UbuntuLatest,
    FetchDepth = 0,
    OnPushBranches = new[] { "main" },
    OnPullRequestBranches = new[] { "main" },
    InvokedTargets = new[] { nameof(ValidateShapes) })]
partial class Build
{
}
