---
name: adopt-branch
description: Adopt a branch authored elsewhere (e.g. a Claude Code desktop worktree) into this checkout for local testing — prune any orphaned worktree, check it out, back it up, optionally bring it up to date with main, then build/test and offer to rebuild the stack.
argument-hint: "[branch name] (optional — if omitted, lists adoptable branches to choose from)"
---

# Adopt a branch into this checkout for testing

Use this when work was committed on a branch somewhere this checkout can't directly run it — most commonly a branch Claude Code desktop created in its own git worktree (`.claude/worktrees/...`) on another machine. The branch's commits are in the shared object store, but the worktree itself is gone or unreachable, so the goal is to get the branch checked out *here* (the devcontainer's normal working tree) so the `jim-*` aliases can build and run it.

This skill does **not** automate conflict resolution or manual testing. It sequences the safe mechanical steps and stops at the decisions only a human should make. Lessons it encodes:

- **An orphaned worktree blocks checkout.** If the branch is still "checked out" in a dead worktree, `git checkout` refuses with *"already checked out"*. `git worktree prune` clears the stale registration first.
- **An unpushed branch is the only copy.** Desktop-authored branches are frequently never pushed. Back it up to `origin` *before* doing anything that could lose commits (a merge gone wrong, a bad reset).
- **Merge, never rebase, to bring it up to date.** See CLAUDE.md → "Bringing a feature branch up to date with `main`". `main` squash-merges, so rebasing buys nothing and costs per-commit conflict pain.
- **Conflicts are semantic, not mechanical.** Stop and resolve them with judgement (and the user) — never auto-pick a side. The expensive part of these merges is figuring out *where* code belongs after the other side refactored.
- **A merge commit can silently omit later working-tree edits.** If you resolve/port files *after* the auto-merge staged them, `git commit` won't include those edits. Re-stage and `git commit --amend` before declaring done.

## Step 1 — Resolve which branch to adopt

```
git fetch origin --prune
git worktree list
```

**If `$ARGUMENTS` names a branch**, use it. Validate it exists (`git rev-parse --verify <branch>` or `git rev-parse --verify origin/<branch>`); if it only exists on the remote, you'll create a local tracking branch in Step 3.

**If `$ARGUMENTS` is empty**, list adoptable branches and ask the user to pick. Adoptable = a branch that is not the current branch, not `main`, and not already up to date / merged. Surface, in priority order:

1. **Branches in prunable worktrees** (from `git worktree list` — lines marked `prunable`). These are the classic desktop-worktree case.
2. **Unmerged local/remote branches**, especially `claude/*`:
   ```
   git branch -a --no-merged main --format='%(refname:short) %(upstream:track)'
   ```
   Exclude the current branch and `main`. For each candidate, show how far it is ahead/behind `main` so the user can judge:
   ```
   git rev-list --left-right --count main...<branch>
   ```
   Present as a short list (branch — ahead N / behind M — last commit subject) and ask which to adopt. Do not adopt without an explicit choice.

## Step 2 — Pre-flight the current checkout

- Working tree MUST be clean (`git status --porcelain`). If dirty, stop and ask whether to commit, stash, or abort — adopting will switch branches and would otherwise lose or carry over changes.
- Note the current branch so you can offer to return to it later if the user only wanted a quick look.

## Step 3 — Prune and check out

```
git worktree prune
git checkout <branch>          # or: git checkout -b <branch> --track origin/<branch>
```

`git worktree prune` is safe and idempotent; it only removes registrations whose directories no longer exist. If checkout still reports the branch is checked out elsewhere, the worktree is genuinely live somewhere reachable — stop and tell the user rather than forcing it.

## Step 4 — Back it up to origin

```
git rev-parse --verify origin/<branch>
```

- If the remote branch does **not** exist, this branch is the only copy. Push it now: `git push -u origin <branch>`.
- If it exists but the local tip is ahead, mention that the backup is stale and offer to push.
- If it's already in sync, say so and move on.

This step is the cheapest insurance in the whole flow; do not skip it.

## Step 5 — Offer to bring it up to date with `main`

Report the divergence (`git rev-list --left-right --count main...HEAD`) and **ask** whether to merge `origin/main` in. If the user declines, skip to Step 6.

If they accept, follow CLAUDE.md → "Bringing a feature branch up to date with `main`":

```
git fetch origin --prune
git merge origin/main
```

- **On conflicts:** stop. Resolve each one with judgement — read what *both* sides did (`git diff <merge-base> HEAD -- <file>` for the branch's own changes; inspect main's version for refactors). When one side moved/renamed/split a file, port the other side's intent into the new structure rather than `--ours`/`--theirs`-ing blindly. Loop in the user for anything non-obvious. The `CHANGELOG.md` union driver resolves itself; eyeball it for duplicate headings after.
- **After resolving:** re-stage every file you touched, including ones edited *after* the auto-merge. Then build + test **before** committing (see Step 6); only commit the merge once green. If you amend in late edits, confirm they made it in (`git show HEAD:<file> | grep ...`).
- Push the updated branch (plain `git push`, no force — a merge fast-forwards the remote).

## Step 6 — Build, test, and confirm

Run the standard pre-commit gate from CLAUDE.md (zero errors *and* warnings):

```
dotnet build JIM.sln
dotnet test JIM.sln
```

Scope can be narrowed to the touched projects for a quick loop, but a merge that pulled in broad changes warrants the full solution. If you committed a merge in Step 5, this gate must have passed first.

## Step 7 — Offer to run the stack for manual testing

Ask whether to rebuild and start the stack so the user can test the change in the running app. Don't assume — some adoptions are just for a code read.

- Full stack: `jim-build` (rebuilds + starts all services; applies any new EF migrations on startup).
- A single service is enough when only one changed: `jim-build-web` / `jim-build-worker` / `jim-build-scheduler`.
- Or delegate to `/run` / `/verify` if the user wants guided manual verification.

After it's up, confirm container health, surface the web URL (`docker port jim.web`), scan web/worker logs for startup or migration errors, and — if a migration came in — verify it actually applied (`__EFMigrationsHistory` + the expected column/table) rather than assuming.

## What this skill deliberately does NOT do

- **Resolve conflicts for you.** It stops and hands them over.
- **Create a PR or merge to `main`.** That's `/pr-merge`, and only on explicit instruction.
- **Decide the manual test plan.** It gets the app running; you (or `/verify`) drive the testing.
