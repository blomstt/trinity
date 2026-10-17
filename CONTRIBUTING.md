# Contributing

Thanks for wanting to help. One condition first, because Trinity is offered both non-commercially and under paid commercial licences (see [COMMERCIAL.md](COMMERCIAL.md)):

**By submitting a contribution (code, shaders, assets, documentation), you confirm that you wrote it or have the right to submit it, and you grant blomst a perpetual, worldwide, non-exclusive, royalty-free, irrevocable licence to use, modify and distribute it under any licence terms, including commercial ones.** You keep the copyright to your work.

If you can't agree to that, please open an issue describing the change instead of a pull request.

Practicalities:

- Keep it light. Trinity follows BRUTAL's approach: nothing ships or runs that games don't need. Say in the PR what a new dependency is for, and how it affects build size, memory or startup.
- `dotnet build` and `dotnet test tests/Trinity.Tests` should pass. Shaders need `glslc` (from shaderc).
- Don't paste in third-party code or assets unless their licence allows commercial use and redistribution. Credit them in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) or the sample's `CREDITS.md`.

## Review and merge

The maintainers (blomst and the collaborators invited to the repo) keep `main` releasable with a light but firm process:

- **Every change lands through a pull request.** No direct pushes to `main`, by anyone, maintainers included.
- **Someone other than the author approves before it merges.** You never approve or merge your own work; a change in another maintainer's area waits for that maintainer. Re-request the review after new commits — an approval is dismissed when the branch changes, so the latest push is the one that was approved.
- **The author merges after approval,** squash so `main` keeps one commit per change, and only with CI green once CI exists. A maintainer merges without waiting only to fix a broken `main` or a security issue, and says so in the PR.
- Keep PRs small and focused, with a short note of what changed and why.

These are enforced by a branch ruleset on `main` (a repo admin sets it under Settings → Rules → Rulesets): require a pull request, require an approving review from someone other than the author, dismiss stale approvals on a new push, and block force-pushes and branch deletion.
