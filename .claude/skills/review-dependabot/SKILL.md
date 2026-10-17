---
name: review-dependabot
description: Review, assess, and merge open Dependabot and jim-automation dependency-bump PRs using JIM's supply chain security and dependency pinning requirements
argument-hint: "[merge|review-only]"
allowed-tools: Bash, Read, Glob, Grep, WebSearch, WebFetch
---

# Review and Merge Dependency-Bump PRs

Review all open Dependabot **and jim-automation** PRs against JIM's supply chain security and dependency governance policies, then merge those that pass assessment.

`jim-automation[bot]` is JIM's own service principal, used by `apt-pin-check` and `tooling-pin-check` to raise PRs for pins Dependabot's ecosystems can't see (production apt packages; `@playwright/mcp` and `dotnet-ef`, pinned outside any Dependabot-readable manifest). These PRs carry the `dependencies` label and land on `automation/*` branches. Treat them as in-scope by default, folded into the same batch as Dependabot's PRs, not a separate pass; see `engineering/DEPENDENCY_PINNING.md` for what each bot covers.

If `$ARGUMENTS` is "review-only", produce the assessment but do NOT merge. Otherwise, present findings and merge approved PRs.

## Step 1: List Open PRs

```
gh pr list --author "app/dependabot" --state open --json number,title,url,createdAt,labels
gh pr list --author "app/jim-automation" --state open --json number,title,url,createdAt,labels
```

If neither `gh` nor its author filter is available (e.g. GitHub access is via MCP tools only), list all open PRs and filter by `user.login` being `dependabot[bot]` or `jim-automation[bot]` instead - a bare author-string search against the GraphQL API can silently return zero results for a bot login, so verify against a full open-PR listing if a search comes back empty and PRs are expected.

If there are no open PRs from either bot, report that and stop.

## Step 2: Gather PR Details

For each PR, collect:
- The diff (`gh pr diff <number>`)
- CI check status (`gh pr checks <number>`)
- The PR body for Dependabot's compatibility score and changelog info

## Step 3: Identify the Update Type

Categorise each PR into one of four ecosystems:

### Docker Base Images
- Check: Does the update stay within the same major.minor version (e.g., 9.0)?
- Check: Is the publisher Microsoft (`mcr.microsoft.com`) or another trusted source?
- Check: Is the change digest-only (`@sha256:` swap) or a tag change?
- **No-op re-tag detection (digest-only swaps)**: Docker Official Images (Postgres, Keycloak, Redis, etc.) are periodically re-tagged upstream without any filesystem change; the manifest is refreshed (re-signed, annotations updated, multi-arch manifest list repacked) but every layer is byte-for-byte identical. Dependabot still raises a PR for these because it tracks the manifest digest. Before investigating further, rule this out:
  1. Pull both digests and compare root filesystem layers:
     ```bash
     docker pull <image>@<old-digest> && docker pull <image>@<new-digest>
     OLD=$(docker inspect <image>@<old-digest> --format '{{range .RootFS.Layers}}{{println .}}{{end}}')
     NEW=$(docker inspect <image>@<new-digest> --format '{{range .RootFS.Layers}}{{println .}}{{end}}')
     diff <(echo "$OLD") <(echo "$NEW")
     ```
  2. If the diff is empty, this is a **no-op re-tag**. Mark it as such in the assessment (`Security: No-op (identical layers)`) and recommend **Merge**; the apt pin check below is redundant because the filesystem is unchanged. Still run a final CI check, but skip deeper investigation.
  3. If any layer differs, treat this as a real image change and continue with the apt-pinning and CVE checks below.
  4. **Best-effort caveat**: if `docker pull` of the *old* digest fails (e.g., `manifest verification failed`), the upstream has garbage-collected it and you cannot confirm. Note this in the assessment ("old digest no longer resolvable, no-op status unconfirmed") and proceed with the normal Docker checks rather than blocking.
- **CRITICAL - Apt Package Pinning** (skip if the no-op check above confirmed identical layers): JIM Dockerfiles pin functional apt packages to exact versions. Before approving ANY Docker base image digest update:
  1. Identify which Dockerfiles have pinned apt packages by reading the changed Dockerfile
  2. For each pinned package, verify it is still available at the same version in the new base image:
     ```bash
     docker run --rm <image>@<new-digest> bash -c \
       "apt-get update -qq && apt-cache policy <package1> <package2> ..."
     ```
  3. Current pinned packages across services (read the Dockerfiles to
     confirm; this list can drift). As of April 2026:
     - **JIM.Web** (aspnet base): `libldap-common`, `libldap2`
     - **JIM.Worker** (runtime base): `libldap-common`, `libldap2`, `cifs-utils`, `libgssapi-krb5-2`
     - **JIM.Scheduler** (runtime base): No apt packages

     Notes:
     - `libldap-2.5-0` was renamed to `libldap2` in Ubuntu Noble (OpenLDAP
       2.6). The Dockerfiles symlink `libldap.so.2` back to `libldap-2.5.so.0`
       because .NET 10 managed LDAP code loads the old name.
     - Verify ALL pinned packages in a Dockerfile by grepping for `=` in the
       `RUN apt-get install` block, not just the ones in this list.
  4. If a pinned version is NO LONGER AVAILABLE: **Do NOT merge**. Report the version mismatch and the available versions so the user can decide whether to update the pin.
- **CRITICAL - RuntimeFrameworkVersion sync (JIM.Web digest bumps only)**: `src/JIM.Web/JIM.Web.csproj` pins `<RuntimeFrameworkVersion>` to the exact ASP.NET Core version bundled by the pinned base image digests, keeping the JIM.Web lock file deterministic (see `engineering/DEPENDENCY_PINNING.md`). When a digest update to the aspnet/sdk images lands:
  1. Check the .NET version in the new image: `docker run --rm <aspnet-image>@<new-digest> dotnet --list-runtimes`
  2. If the patch version changed (e.g. 10.0.9 -> 10.0.10), the PR must ALSO bump `<RuntimeFrameworkVersion>` in `JIM.Web.csproj` and regenerate lock files (`dotnet restore JIM.sln --force-evaluate`). Dependabot will not do this itself, and CI stays green without it (locked-mode restore still passes against the old pin), so the drift between the lock file and the runtime the production image actually ships is silent; this is a review-time check only. Push that companion commit to the PR branch before merging.
     - The same companion commit must bump `.devcontainer/Dockerfile`'s `dotnet-install.sh --version` SDK pin to the SDK that ships the new runtime (map runtime to SDK via `https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`), or native JIM.Web runs on dev hosts fail at launch after the pin bump while Docker builds keep working. See `engineering/DEPENDENCY_PINNING.md` for the full coupling. A devcontainer rebuild is needed to pick it up.
  3. If the digest bump is a same-version rebuild (no patch change), no action is needed.

### NuGet Packages
- Check: Is this a patch or minor update (not major)?
- Check: Review the package changelog for breaking changes
- Check: Is the package from a trusted publisher?
- Check: Run `dotnet list package --vulnerable` if concerned about transitive vulnerabilities
- Check: Does CI build and all tests pass?
- **Lock files**: JIM enforces locked-mode NuGet restore in CI, so every NuGet PR needs its `packages.lock.json` file(s) updated to match. Dependabot does not reliably do this itself (dependabot-core#12318, dependabot-core#10863); the `regenerate-nuget-lock-files` workflow pushes an automated `chore: regenerate NuGet lock files for Dependabot update` commit to the PR branch when needed, so **a red first CI run on a fresh Dependabot NuGet PR is expected**, not a failure to investigate. Before assessing CI status:
  1. Confirm the regeneration commit is present (check the PR's commit list) and that CI is green on the current head, not the original Dependabot commit.
  2. If the regeneration workflow itself failed (check its run log) and no lock-file commit landed, regenerate manually from a local checkout of the branch: `dotnet restore JIM.sln --force-evaluate -p:Configuration=Release`, review the diff, commit, and push. Keep `Configuration=Release`: a Debug regeneration stamps a per-minute `dev.*` version into every project entry, so the real change drowns in churn. If files outside the bumped project's dependency chain still show only project-version changes, revert those to keep the diff to what actually moved.
  3. The failure to expect is `NU1004: The project references jim.web whose dependencies has changed` on `JIM.Web.Api.Tests` / `JIM.Web.Tests`. Dependabot updates the bumped project's own lock file but never its consumers'.
  4. See `engineering/DEPENDENCY_PINNING.md` for the full policy and mechanism.

### GitHub Actions
- Check: Is this a patch or minor update within the same major version tag?
- Check: Review the action's changelog for changes to inputs, outputs, or behaviour
- Check: Is the action from a trusted publisher (GitHub, Microsoft, well-known org)?

### Dev Tooling / Apt Pins (jim-automation)
PRs from `apt-pin-check` (production apt packages in Dockerfiles) and `tooling-pin-check` (`@playwright/mcp`, `dotnet-ef`, pinned in `.devcontainer/setup.sh` / `.mcp.json`).
- Check: The PR body's table lists old and new versions per tool/package and file - confirm every location of a given pin moved to the *same* version (a pin duplicated across files, e.g. `@playwright/mcp` in both `.devcontainer/setup.sh` and `.mcp.json`, must not drift apart).
- Check: The raising workflow already validated installability/publication before opening the PR (see `.github/scripts/check-apt-pins.ps1` / `check-tooling-pins.ps1`); this is a lighter review than a Dependabot NuGet bump, not a rubber stamp - still read the diff.
- Check: Does CI pass? These are script/config-only changes (no `.cs`), so per root `CLAUDE.md`'s build/test exceptions no local `dotnet build`/`test` is required either by you or by CI to approve; rely on whatever CI does run (e.g. `changelog-lint`, the workflow's own dry-run step).
- No `packages.lock.json` involved - these pins live outside any NuGet/npm manifest Dependabot or a lock file would cover.
- If a pin's new version is flagged by the workflow as no-longer-published (exit code 3 in `check-tooling-pins.ps1`/`check-apt-pins.ps1`, surfaced as a failing step rather than folded into the PR silently): **Hold**, same as a Dependabot pinning failure.

## Step 4: Security Advisory Check

Search for any security advisories related to the updates:
- For .NET updates: Search for ".NET [version] security update [month] [year] CVE"
- For NuGet packages: Check GitHub Advisory Database
- For GitHub Actions: Check the action's security advisories

Note whether the update addresses any known CVEs - this increases urgency to merge.

## Step 5: Present Assessment

Create a summary table with columns:
| PR | Update | Service | CI Status | Pinning Check | Security | Recommendation |

The `Security` column should note one of: CVEs addressed (list them), `No-op (identical layers)` for confirmed upstream re-tags, `No-op unconfirmed (old digest unresolvable)` if the upstream GC'd the prior digest, or `None` if no CVE implications.

Recommendations should be one of:
- **Merge** - All checks pass
- **Merge (security)** - Addresses a CVE, prioritise
- **Merge (no-op re-tag)** - Layer-identical upstream manifest refresh, safe to merge without further investigation
- **Hold** - Needs investigation (explain why)
- **Reject** - Fails assessment (explain why)

## Step 6: Merge (arm the whole batch)

Unless running in review-only mode. `main` has a merge queue, so a PR no longer has to be up to date with `main` to merge: the queue tests each entry against current `main` plus the entries ahead of it, and merges them one at a time. One merge therefore no longer invalidates the rest of the batch, and there is no train to run by hand. Arm every PR the assessment approved, in one pass.

1. **Get each PR green on its own head first.** For NuGet PRs that means the lock-file regeneration commit is present and CI is green on it (Step 3, NuGet Packages); fix any lock-file problem in ONE push, since each push costs a full CI run. Do not update a branch from `main` just because it is behind: that spends a CI run for nothing.
2. **Arm in priority order**: security fixes first, then oldest. The queue takes PRs in the order they join it.
   ```
   gh pr merge <number> --auto
   ```
   This joins the queue straight away when the PR's checks have passed, and otherwise as soon as they do. Include a merge comment noting what was verified (pinning, security advisory) where the assessment found anything noteworthy.
3. **Hold back PRs that will conflict with each other.** Two bumps that edit the same lines (one `.csproj`, one Dockerfile `FROM`, or two NuGet PRs that both regenerate the same `packages.lock.json`) will conflict once the first one merges. Arm the first, and arm the other only after the first has landed and the other has been brought up to date (step 5). Everything else goes in together.
4. **Watch every armed PR** with `pwsh ./scripts/Watch-PullRequest.ps1 -Number <n>`, each in its own Bash background command (root `CLAUDE.md`, "Closing the loop after `--auto`"). A PR waiting in the queue is not something to act on.
5. **Update a branch only when the watcher says so** (`Conflict`, or `RemovedFromQueue` because it conflicted with `main`), or when a held-back PR's turn comes:
   - **Dependabot PRs whose branch carries only Dependabot's own commits**: comment `@dependabot rebase` and wait up to ~5 minutes (it normally responds in under a minute). Lock files may still need regenerating on the rebased head.
   - **jim-automation PRs**: there is no bot listening for a rebase comment (`open-pin-pr.ps1` opens the PR and stops), so update locally; do not comment `@dependabot rebase` on a jim-automation branch.
   - **Otherwise** (the branch has external commits, such as the lock-file regeneration, or Dependabot does not respond in time): `git fetch origin`, `git checkout <branch>`, `git pull --no-rebase` (absorbs any remote drift), `git merge origin/main --no-edit`. **Merge, don't rebase**: `main` squash-merges so branch history is discarded anyway, and a merge needs only a plain push (root `CLAUDE.md`). Dependabot stops maintaining a branch once it has external commits; that is expected.
   - **Conflicts**: resolve conflicted `.csproj` files manually, keeping BOTH sides' version bumps (`git diff HEAD origin/main -- <file>` shows what each side changed). Never hand-merge a `packages.lock.json` conflict; lock content derives from the csproj set, so regenerate instead.
   - **Regenerate and verify before committing**: run `dotnet restore JIM.sln --force-evaluate` as its own command and check its exit status directly; do not bury it in a `&&`/pipe chain where a piped `tail` masks the exit code. Then `grep -rln '<<<<<<<' src/ test/` must return nothing (a marker-laden csproj otherwise gets committed silently; this happened). Then `git add -A`, commit, plain push, and `gh pr merge <number> --auto` again.
6. **`RemovedFromQueue` for failed checks**: the queue ran the PR's checks against `main` plus the entries ahead of it, and something failed there. Read the failed `merge_group` check the watcher names; a failure the PR's own run did not show usually means it clashes with a PR that merged just ahead of it. Fix, push, re-arm. If it is clearly transient, surface it rather than re-arming blind.
7. **Unattended (scheduled) runs: verify, never fire-and-forget.** Nobody can wait or re-prompt, so arming is not the end of the step. Before finishing, read each PR once (`Watch-PullRequest.ps1 -Number <n> -Once`) and end the run with each PR's state: merged, queued, armed and waiting on its checks, or stuck and why. Where the sandbox has no `gh` or checkout, `update_pull_request_branch` is acceptable for a branch that genuinely needs updating (step 5's conditions), since external commits mean the run owns the branch from then on.
8. **Avoid the API "Update branch" endpoint (`update_pull_request_branch`) on branches Dependabot still maintains**: the merge commit it creates makes Dependabot ignore all subsequent `@dependabot rebase`/`recreate` commands. On a branch that already has external commits it is functionally equivalent to the local merge in step 5, but prefer the local merge for conflict control and the single-push discipline.
9. **Recovery (last resort)**: for a branch that is truly wedged, a manual rebase replays only the version-bump commit on top of current `main`:
   ```bash
   git fetch origin
   git checkout <dependabot-branch-name>
   git rebase origin/main
   git push --force-with-lease origin <dependabot-branch-name>
   ```
   Prefer the merge path in step 5; force-push only when a plain push cannot represent the fix, and expect the harness to require explicit user approval for it.
10. Report final status of all PRs
## Reference: JIM's Supply Chain Security Requirements

- All dependency updates require human review - no auto-merge
- Docker images pinned by digest (`@sha256:...`)
- Functional apt packages pinned to exact versions
- NuGet packages pinned in `.csproj` files, transitive dependencies locked via `packages.lock.json` and enforced `RestoreLockedMode`
- GitHub Actions pinned by immutable commit SHA (with a `# vX.Y.Z` version comment)
- Prefer Microsoft-maintained and well-established packages
- Full details in CLAUDE.md under "Third-Party Dependency Governance", engineering/DEVELOPER_GUIDE.md under "Dependency Pinning and Updates", and engineering/DEPENDENCY_PINNING.md (canonical policy page)
