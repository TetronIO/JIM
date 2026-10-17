---
name: pr-merge
description: Take a feature branch through to a clean squash-merge: build/test, open PR, resolve code-quality bot feedback, add it to the merge queue, then clean up local + remote refs.
argument-hint: "[optional PR title — under 70 chars; if omitted, derive one from the commit log]"
---

# Take a JIM feature branch from in-progress to merged

This skill drives the full happy-path between "I have committed work on a feature branch" and "the work is on `main` and my workspace is back on a clean main." It encodes the lessons from prior PRs:

- **`main` has a merge queue.** `gh pr merge <n> --auto` adds the PR to it once its checks pass; the queue re-runs every required check against current `main` and merges only if they pass. A branch behind `main` merges as it is, so there is no "update before opening" step. A queue entry that fails is **removed**, and nothing re-adds it.
- **`github-code-quality` runs on every PR.** It almost always finds something. Build that fix loop into the flow rather than treating it as an exception.
- **Queued is not merged.** Unresolved review threads (the documentation review opens one per finding on any customer-docs change), a failed CodeQL analysis, a conflict, an unsigned commit or a failed queue entry each hold it silently. Wait with `scripts/Watch-PullRequest.ps1`, which reports each of them, never with a loop that only watches for `MERGED`.
- **`gh pr merge ... --auto`** is the right command; the immediate failure right after `gh pr create` is expected, not a blocker.
- **Squash-merge means `git branch -D`** is correct for cleanup, not `-d`. The queue squashes too.

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
   gh pr list --head "$(git branch --show-current)" --state open --json number,title,baseRefName,mergeStateStatus,autoMergeRequest
   ```
   - If a PR already exists, skip the "Create PR" step below and resume from "Queue auto-merge" / "Resolve code-quality issues" depending on its state. Note the existing number.

4. **Check for a stack (see "Stacked PRs" below):**
   ```
   gh pr list --state open --json number,headRefName,baseRefName
   ```
   - If this branch's PR has `baseRefName` other than `main`, or another open PR has `baseRefName` equal to this branch, the branch is a **layer in a stack**. Follow "Stacked PRs" below instead of the ordinary flow: stacks rebase rather than merge to stay current, and auto-merge is unsupported.

## Stacked PRs

Per CLAUDE.md ("Stacked PRs for discovered work"), a branch may be a layer in a GitHub native stack: a sequential chain where each PR targets the branch of the PR below it and the bottom PR targets `main`. Landing a stack differs from the ordinary flow in four ways:

- **Bottom layer merges `main`; layers above rebase onto the layer below.** Linear history is required *between* layers, not between the bottom layer and `main`, so the bottom layer follows this skill's "Merge origin/main only when you need to" section unchanged (a merge commit there does not break the stack). Each layer above is then moved onto its new base with `git -c rebase.backend=apply rebase --onto <branch-below> <old-base> <layer>` plus `git push --force-with-lease`, never by merging one layer into another. Do NOT use `gh stack rebase`: it rebases the bottom layer onto `main` as well, replaying its whole history and re-resolving every conflict per commit.
- **No auto-merge.** `--auto` is unsupported for stacked PRs. Instead, wait for green with a background waiter on the checks, then merge. Every PR in the stack is evaluated against `main`'s branch protections (every required check), regardless of its direct base.
- **Land bottom-up, atomically from wherever you merge.** Merging any PR in the stack also merges all unmerged PRs below it in a single operation, ordered bottom-up, each recorded individually. A mid-stack PR cannot merge in isolation. The normal move when the objective is complete is to merge from the **top** of the stack; merge a lower layer earlier only when it is ready and independently valuable. After a partial merge, GitHub automatically rebases the next unmerged PR to target `main`.
- **Merge mechanics:** `gh pr merge` refuses a layer of a linked stack, so once everything below is green use the async merge endpoint, leaving `merge_action` at its default so the stack goes through `main`'s merge queue: `gh api -X PUT repos/TetronIO/JIM/pulls/<n>/merge-async -f merge_method=squash`. The stack joins the queue in order; a result of `pending` or `enqueued` is not a merge, so poll `gh pr view <n> --json state` for `MERGED`. If one layer's queue entry fails, every layer above it leaves the queue too: fix that layer and call `merge-async` again. See the root CLAUDE.md, "Merging a stack layer".
- **Cleanup:** delete every landed layer's local branch with `git branch -D` in the final cleanup step.

## Merge origin/main only when you need to

The merge queue tests every entry against current `main`, so a branch behind `main` merges as it is: do not update it before opening the PR, and do not update it "to be safe" afterwards, since each push spends a full CI run. Merge `origin/main` in only when:

- the branch conflicts with `main` (the watcher reports `Conflict`, or `RemovedFromQueue` for a queue conflict), or
- the branch's work needs something that has since landed on `main`.

**Merge, don't rebase.** Because the PR lands as a squash, the feature branch's pre-merge history is discarded on merge. Rebasing therefore buys linear history that is immediately thrown away, while costing per-commit conflict resolution and a force-push (which the harness gates). A merge resolves each conflict once and needs only a plain `git push`. See CLAUDE.md → "Bringing a feature branch up to date with `main`".

```
git fetch origin --prune
git merge origin/main
git push
```

- `CHANGELOG.md` carries a `merge=union` driver (`.gitattributes`), so concurrent `[Unreleased]` entries combine automatically rather than conflicting. After the merge, eyeball the `[Unreleased]` section for duplicated `###` headers or bullets and tidy if needed.
- **If the merge produces other (non-CHANGELOG) conflicts**, stop and surface them to the user. Do NOT auto-resolve; the user has to decide.
- Opt-in rebase: only if the user explicitly wants linear pre-squash history. Then `git -c rebase.backend=apply rebase origin/main` (the apply backend gets past a misleading "local changes would be overwritten" on a clean tree) followed by `git push --force-with-lease`.

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
gh pr merge <n> --auto
```

- This adds the PR to `main`'s merge queue if its checks have passed, and otherwise arms auto-merge so it joins the queue when they do. The queue sets the method (squash) and the repository deletes the head branch on merge, so `--squash` and `--delete-branch` add nothing.
- An immediate failure right after `gh pr create` is **expected, not a blocker** (checks haven't started). Use `--auto`.
- **NEVER use `--admin` to bypass.** It skips the required checks and the queue; the harness will refuse it, correctly.
- In the Claude desktop app, `mcp__ccd_pr__set_auto_merge` (`enabled: true`, `merge_method: squash`) is the equivalent. It has no `--delete-branch` option and needs none: the repository deletes a PR's head branch when it merges.

## Resolve code-quality bot issues

`github-code-quality` will post review comments within a couple of minutes. Don't assume it'll be silent.

**In the Claude desktop app (Code tab), let the app watch the PR instead of polling for it.** After `gh pr create`, call `mcp__ccd_pr__get_status`, and `mcp__ccd_pr__bind_pr` with the PR's URL if it does not report this PR. Then turn on Auto-fix with `mcp__ccd_pr__set_monitor` (`auto_fix: true`, `address_comments: true`, and the PR's `url`); the app asks the user to approve it. From then on the app wakes the session with a `<ci-monitor-event>` for each failed check, merge conflict and review comment, the bot's included. Skip the polling in step 1 and apply steps 2 to 4 to each event as it arrives. Do not also poll `gh pr checks` or the comments endpoint, and do not reach for ScheduleWakeup, CronCreate or `/loop` to do it. Where those tools are not available (a terminal session, for example), poll as below.

1. **Poll for the bot's review:**
   ```
   gh api repos/TetronIO/JIM/pulls/<n>/comments
   ```
   - Look for comments authored by `github-code-quality[bot]`.
   - Also check `gh pr checks <n>` for any failed required checks (build-and-test, scan-base-images-summary, three CodeQL analyses, changelog-lint).

2. **For each comment, decide whether to apply:**
   - **Apply when** the suggestion is correct on the merits (genuine null-safety, clear redundancy, security issue, or a pattern called out in `src/CLAUDE.md` under "Code Quality"). Common JIM patterns:
     - Replace `nullable.Value` inside LINQ projections with the bare nullable; lifted equality covers it (we already do this in `ConnectedSystemRepository`).
     - After `Assert.That(x, Is.Not.Null)` in tests, use `x!` on every subsequent dereference, not just the first one.
     - "Useless assignment", "Missed opportunity to use Where", redundant null-conditional after early return — fix at source per `src/CLAUDE.md`.
   - **Push back when** the suggestion would change behaviour, hide a real bug, or just be cosmetic noise. Surface those to the user with a short rationale rather than blindly applying.

3. **Apply, build, test, commit, push:**
   - Make the edits with `Edit`.
   - Re-run `dotnet build JIM.sln` and `dotnet test JIM.sln`.
   - Commit as a separate `fix(code-quality): ...` commit (the squash-merge collapses it away). Use a HEREDOC commit message ending with the `Co-Authored-By:` trailer from the session's attribution reminder. Never copy the model name from an older commit or from this file; it goes stale with every model change.
   - `git push`; auto-merge stays armed and re-evaluates against the new HEAD. If the watcher later reports `RemovedFromQueue`, run `gh pr merge <n> --auto` again once fixed.

4. **Repeat until the bot has nothing left to say** and `gh pr checks <n>` shows all required checks passing or pending merge.

## Resolve review threads

Every unresolved review thread holds the merge (`main`'s ruleset requires conversation resolution), auto-merge included, whoever opened it. On a PR that changes customer pages (`docs/`, not `docs/developer/` or any `CLAUDE.md`), the documentation review (`docs-review.yml`, as `claude[bot]`) posts **one thread per finding** about two minutes after the PR opens or is marked ready, then a summary comment headed "**Documentation review: N findings**"; the summary is the sign it has finished. Expect it on any docs change.

For each thread: **fix the text, or reply with why it stays as it is; then resolve the thread.** A reply alone leaves the merge blocked. Answering is the bar, not agreeing: the review's findings are claims to check (it reads the source for ACC-4), and a reasoned "stays as is" is a valid answer. For a generated page (ACC-5), the fix belongs in the source the generator reads (cmdlet help, a DTO comment), as the finding says.

List the open threads, with the ids needed to reply and resolve:

```bash
gh api graphql -f query='query($n:Int!){repository(owner:"TetronIO",name:"JIM"){pullRequest(number:$n){reviewThreads(first:100){nodes{id isResolved comments(first:1){nodes{databaseId author{login} path line body}}}}}}}' -F n=<n> \
  --jq '.data.repository.pullRequest.reviewThreads.nodes[] | select(.isResolved|not) | {id, comment: .comments.nodes[0].databaseId, path: .comments.nodes[0].path, line: .comments.nodes[0].line, author: .comments.nodes[0].author.login, body: .comments.nodes[0].body}'
```

Reply to a thread (by its first comment's `databaseId`), then resolve it (by the thread `id`):

```bash
gh api repos/TetronIO/JIM/pulls/<n>/comments/<comment-databaseId>/replies -f body='Fixed in <sha>: ...'
gh api graphql -f query='mutation($id:ID!){resolveReviewThread(input:{threadId:$id}){thread{isResolved}}}' -F id=<thread-id>
```

In Claude Code on the web GraphQL is refused, so use the relay's routes instead; they key a thread by its first comment's id, which is the id `Watch-PullRequest.ps1` prints:

```bash
gh api repos/TetronIO/JIM/pulls/<n>/ccr/review_threads                        # resolved, path, line, comment_ids
gh api repos/TetronIO/JIM/pulls/<n>/comments/<comment-id>/replies -f body='...'
gh api -X POST repos/TetronIO/JIM/pulls/<n>/ccr/comments/<comment-id>/resolve
```

When you push a fix, reply naming the commit, then resolve. To ask for a fresh docs review after more commits, remove and re-add the `docs-review` label; it raises nothing an earlier run raised.

## Wait for the merge to land

Enabling auto-merge is not the end: a failed check, an unresolved thread, a conflict, an unsigned commit or a failed queue entry each hold the merge, and nothing tells you unless you wait on the right thing. Use the watcher, in a Bash background command (not ScheduleWakeup or sleep-polling):

```
pwsh ./scripts/Watch-PullRequest.ps1 -Number <n>
```

Run with `run_in_background: true`. It exits on the first thing that needs you, saying which, and the harness notifies you. Do not poll proactively in the meantime.

- `Merged`: clean up (below).
- `Closed`: someone closed the PR without merging it. Stop and tell the user rather than cleaning up.
- `ChecksFailed`: read the failed check's log, fix, push (back to "Resolve code-quality bot issues" for bot findings).
- `UnresolvedThreads`: "Resolve review threads" above.
- `RemovedFromQueue`: the merge queue dropped the PR, and nothing re-adds it. For failed `merge_group` checks (it lists them), read their logs; they ran against current `main` plus the entries ahead, so a failure the PR's own run did not show is a real interaction with `main`. Fix and push. For a queue conflict, merge `origin/main` in ("Merge origin/main only when you need to"). Then `gh pr merge <n> --auto` again. If someone removed it by hand, ask the user before re-adding.
- `Conflict`: merge `origin/main` in and push.
- `Blocked`: every check passed and no thread is open; usually an unsigned commit (re-sign and force-push, per the root CLAUDE.md) or a required check that never ran.

A PR waiting in the queue, or merely behind `main`, needs nothing and does not end the wait. After acting, run it again, until `Merged`. A transient `gh` failure is retried, never reported. A thread deliberately left open for the user goes in `-AcknowledgedThreadIds <id>` so it stops ending the wait. Arm it even with the desktop app's Auto-fix on: the app reports failures, conflicts and comments, not the merge itself.

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
- **In a linked git worktree, skip the checkout and the branch delete.** The desktop app gives each session its own worktree; `git rev-parse --git-dir` differs from `git rev-parse --git-common-dir` when you are in one. There `git checkout main` fails because `main` is checked out in another worktree, and the branch is the one the worktree itself is on. Do not force either, and never run `git checkout` in the repository's main checkout to get round it. Run `git fetch --prune`, confirm the remote branch is gone (`git ls-remote --heads origin <feature-branch>` prints nothing), and leave the worktree and its branch to the app.

End the skill on a one-line confirmation: PR number, squash-commit SHA on `main`, and the cleaned-up branch name.

## Out of scope

- **Releasing.** This skill ends at "merged to main". Cutting a tagged release is `/release`.
- **Force-merging past failed checks.** If a required check stays red after a real fix attempt, stop and ask the user. Do not use `--admin`.
- **Retrying flaky checks.** If a single check fails for an obviously transient reason (e.g. infrastructure timeout), surface it; don't auto-rerun without confirmation.
