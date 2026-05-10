---
name: pr-merge
description: Take a feature branch through to a clean squash-merge — rebase on origin/main, build/test, open PR, resolve code-quality bot feedback, queue auto-merge, then clean up local + remote refs.
argument-hint: "[optional PR title — under 70 chars; if omitted, derive one from the commit log]"
---

# Take a JIM feature branch from in-progress to merged

This skill drives the full happy-path between "I have committed work on a feature branch" and "the work is on `main` and my workspace is back on a clean main." It encodes the lessons from prior PRs:

- **Strict mode is on for `main`.** A PR opened while the feature branch is behind `origin/main` will sit in `mergeStateStatus: BEHIND` and `--auto` cannot land it. Get the branch up to date *before* the PR exists.
- **`github-code-quality` runs on every PR.** It almost always finds something. Build that fix loop into the flow rather than treating it as an exception.
- **`gh pr merge ... --auto`** is the right command; the immediate failure right after `gh pr create` is expected, not a blocker.
- **Squash-merge means `git branch -D`** is correct for cleanup, not `-d`.

If `$ARGUMENTS` is non-empty, treat it as the proposed PR title (still under 70 chars). Otherwise derive a title from the commit log.

## Pre-flight

1. **Verify the working state:**
   ```
   git status
   git branch --show-current
   ```
   - You MUST be on a feature branch (not `main`). If on `main`, stop and ask the user which branch they meant.
   - Working tree MUST be clean. If there are uncommitted changes, stop and ask whether they should be committed first or stashed.

2. **Check there is something to PR:**
   ```
   git fetch origin --prune
   git log --oneline origin/main..HEAD
   ```
   - If empty, stop: there's nothing to merge.

3. **Check whether a PR already exists for this branch:**
   ```
   gh pr list --head "$(git branch --show-current)" --state open --json number,title,mergeStateStatus,autoMergeRequest
   ```
   - If a PR already exists, skip the "Create PR" step below and resume from "Queue auto-merge" / "Resolve code-quality issues" depending on its state. Note the existing number.

## Get the branch up to date with origin/main (CRITICAL)

This is the step previously missed. Strict mode means it has to happen *before* the PR exists, otherwise the PR is born BEHIND and `--auto` is blocked.

1. **Update local `main`:**
   ```
   git checkout main
   git pull --ff-only
   git checkout -
   ```

2. **Decide whether the branch is already up to date:**
   ```
   git log --oneline HEAD..origin/main
   ```
   - If empty, the branch is already current — skip to the build/test step.
   - If non-empty, rebase onto `origin/main`:
     ```
     git rebase origin/main
     ```
   - **If the rebase produces conflicts**, stop and surface them to the user. Do NOT auto-resolve; the user has to decide.
   - After a successful rebase, force-push with lease:
     ```
     git push --force-with-lease
     ```

   Fallback: if for some reason the rebase route is unavailable (e.g. user has explicitly asked not to rewrite history), create the PR first and then `gh pr update-branch <n>` immediately. This produces a merge commit on the feature branch, but the squash-merge collapses it away.

## Build and test

Per JIM CLAUDE.md, never open a PR with a failing build or tests.

```
dotnet build JIM.sln
dotnet test JIM.sln
```

- Both must complete with **zero errors and zero warnings**.
- If either fails, stop and report. Do not paper over warnings — fix them.
- Exception: changes that only touch scripts / static assets / docs / config / CI workflows / diagrams skip these per the CLAUDE.md exception list. UI-only Razor changes need `dotnet build` but not `dotnet test`. Use judgement on the actual diff.

## Create the PR

Skip if a PR already exists for this branch.

1. **Derive the title and body:**
   - If `$ARGUMENTS` is set, use it as the title (still verify it is under 70 characters).
   - Otherwise, look at `git log --oneline origin/main..HEAD` to summarise. Lead with the dominant theme. Conventional prefixes (`perf:`, `feat:`, `fix:`, `docs:`, `chore:`) are encouraged.
   - Title MUST be under 70 characters.
   - Body MUST follow the JIM template:
     ```
     ## Summary
     <2–6 bullet points covering what changed and why>

     ## Test plan
     - [x] `dotnet build JIM.sln` clean
     - [x] `dotnet test JIM.sln` clean
     - [x] <feature-specific manual verification, if applicable>

     🤖 Generated with [Claude Code](https://claude.com/claude-code)
     ```
   - Use a HEREDOC for the body so the formatting survives:
     ```
     gh pr create --title "<title>" --body "$(cat <<'EOF'
     ## Summary
     ...
     EOF
     )"
     ```
   - Capture the PR number from the returned URL.

## Queue auto-merge

```
gh pr merge <n> --squash --delete-branch --auto
```

- An immediate failure right after `gh pr create` is **expected, not a blocker** (checks haven't started). The `--auto` flag queues the merge for when checks pass.
- **NEVER use `--admin` to bypass.** The harness will refuse it, correctly.

## Resolve code-quality bot issues

`github-code-quality` will post review comments within a couple of minutes. Don't assume it'll be silent.

1. **Poll for the bot's review:**
   ```
   gh api repos/TetronIO/JIM/pulls/<n>/comments
   ```
   - Look for comments authored by `github-code-quality[bot]`.
   - Also check `gh pr checks <n>` for any failed required checks (build-and-test, scan-base-images-summary, three CodeQL analyses, claude-review).

2. **For each comment, decide whether to apply:**
   - **Apply when** the suggestion is correct on the merits (genuine null-safety, clear redundancy, security issue, or a pattern called out in `src/CLAUDE.md` under "Code Quality"). Common JIM patterns:
     - Replace `nullable.Value` inside LINQ projections with the bare nullable; lifted equality covers it (we already do this in `ConnectedSystemRepository`).
     - After `Assert.That(x, Is.Not.Null)` in tests, use `x!` on every subsequent dereference, not just the first one.
     - "Useless assignment", "Missed opportunity to use Where", redundant null-conditional after early return — fix at source per `src/CLAUDE.md`.
   - **Push back when** the suggestion would change behaviour, hide a real bug, or just be cosmetic noise. Surface those to the user with a short rationale rather than blindly applying.

3. **Apply, build, test, commit, push:**
   - Make the edits with `Edit`.
   - Re-run `dotnet build JIM.sln` and `dotnet test JIM.sln`.
   - Commit as a separate `fix(code-quality): ...` commit (the squash-merge collapses it away). Use a HEREDOC commit message with the `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` trailer.
   - `git push` — the auto-merge stays queued and re-evaluates against the new HEAD.

4. **Repeat until the bot has nothing left to say** and `gh pr checks <n>` shows all required checks passing or pending merge.

## Wait for the merge to land

Use a Bash background command, not ScheduleWakeup or sleep-polling:

```
until [ "$(gh pr view <n> --json state -q .state)" = "MERGED" ]; do sleep 30; done
```

Run with `run_in_background: true`. The harness will notify you when it exits. Do not poll proactively in the meantime.

If the PR transitions to BEHIND while waiting (e.g. another PR landed on `main` first), run `gh pr update-branch <n>` to re-trigger checks, then re-arm the waiter.

## Clean up

Once the merge notification fires:

```
git checkout main
git pull --ff-only
git fetch --prune
git branch -D <feature-branch>
```

- Use `-D` (capital), not `-d`. We squash-merge, so the feature branch's commits are not ancestors of `main` and `git branch -d` would refuse with "not fully merged".
- If you were checked out on the feature branch when the auto-delete fired, the local ref may already be gone; `-D` will then report "branch not found", which is success.

End the skill on a one-line confirmation: PR number, squash-commit SHA on `main`, and the cleaned-up branch name.

## Out of scope

- **Releasing.** This skill ends at "merged to main". Cutting a tagged release is `/release`.
- **Force-merging past failed checks.** If a required check stays red after a real fix attempt, stop and ask the user. Do not use `--admin`.
- **Retrying flaky checks.** If a single check fails for an obviously transient reason (e.g. infrastructure timeout), surface it; don't auto-rerun without confirmation.
