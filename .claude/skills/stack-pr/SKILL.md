---
name: stack-pr
description: Interrupt feature work to fix a discovered blocker in its own stacked PR; branch the prerequisite off origin/main, implement it to full standard, PR and auto-merge it, fold it back into the feature branch, and retarget the feature PR.
argument-hint: "[one-line description of the discovered blocker]"
---

# Stack a prerequisite PR under the current feature branch

This skill implements the policy in CLAUDE.md ("Stacked PRs for discovered blockers"): something has been found mid-feature that must be solved to deliver the feature but is not semantically part of it. It gets fixed now, in its own PR, stacked under the feature; never folded into the feature branch, never deferred to an issue.

If `$ARGUMENTS` is non-empty, treat it as the blocker description; otherwise derive one from the conversation context. Announce the blocker to the user in one line and proceed; do not ask whether to stack (the policy pre-authorises it). If the prerequisite itself is architecturally significant with multiple sensible approaches, the CLAUDE.md "Ask before significant changes" rule applies to the approach, not to the decision to stack.

## 1. Park the feature work

```
git status
git branch --show-current
```

- You MUST be on a feature branch. If on `main`, stop; there is nothing to stack under.
- Record the feature branch name; call it `<feature>` below.
- If the tree is dirty, commit the WIP to the feature branch: `git add -A && git commit -m "wip: park feature work before stacked prerequisite"`. Prefer a `wip:` commit over stashing; squash-merge collapses it on landing, and a stash is invisible to other sessions.

## 2. Create the prerequisite branch off origin/main

```
git fetch origin --prune
git checkout -b feature/<feature-suffix>-prereq-<desc> origin/main
```

- `<feature-suffix>` is the feature branch name without its `feature/` prefix; `<desc>` is a short kebab-case description of the blocker. Example: feature `feature/csv-connector` with an encoding bug prerequisite becomes `feature/csv-connector-prereq-fix-encoding`.
- Branching off `origin/main` (not the feature branch) is what makes the stack work: the prerequisite PR must contain no feature commits.

## 3. Implement the prerequisite to full standard

Being unplanned lowers no bars. All CLAUDE.md rules apply:

- TDD (failing test first; for a bug the test must reproduce it before the fix).
- Build/test gates: targeted during the loop, `dotnet build JIM.sln` and `dotnet test JIM.sln` clean before the PR (or the documented non-code exceptions).
- Changelog entry under `[Unreleased]` plus public docs if the change is user-facing.
- Commit with a clear message and push: `git push -u origin feature/<feature-suffix>-prereq-<desc>`.

## 4. Open the prerequisite PR and queue auto-merge

Follow `/pr-merge` conventions (title under 70 chars, JIM body template):

```
gh pr create --base main --title "fix: <desc>" --body "..."
gh pr merge <n> --squash --delete-branch --auto
```

- The immediate `gh pr merge` failure right after create is expected; `--auto` queues it.
- Resolve `github-code-quality` feedback per the `/pr-merge` loop. Do not wait for the merge to land before moving on; that is the point of stacking.

## 5. Fold the prerequisite into the feature branch and resume

```
git checkout feature/<feature>
git merge feature/<feature-suffix>-prereq-<desc>
git push
```

The feature branch now contains the fix and work can continue immediately.

## 6. Retarget the feature PR (if one exists)

```
gh pr list --head feature/<feature> --state open --json number
gh pr edit <feature-pr> --base feature/<feature-suffix>-prereq-<desc>
```

- This keeps the feature PR's diff limited to feature work while the prerequisite is open.
- If no feature PR exists yet, nothing to do now; if one is created while the prerequisite PR is still open, pass `--base feature/<feature-suffix>-prereq-<desc>` at creation.

## 7. Arm the landing notification, then resume the feature

Watch for the prerequisite landing with a background waiter (per CLAUDE.md "Closing the loop after `--auto`"):

```
until [ "$(gh pr view <n> --json state -q .state)" = "MERGED" ]; do sleep 30; done
```

Run with `run_in_background: true` and carry on with feature work. When it fires:

1. GitHub auto-retargets the feature PR to `main` (the prerequisite branch was deleted). Verify with `gh pr view <feature-pr> --json baseRefName`; if needed, `gh pr edit <feature-pr> --base main`.
2. On the feature branch:
   ```
   git fetch origin --prune
   git merge origin/main
   git push
   ```
   The squash commit supersedes the prerequisite commits; conflicts, if any, are content-identical and trivial. Eyeball `CHANGELOG.md` `[Unreleased]` for a doubled bullet (union driver duplication) and tidy.
3. Delete the local prerequisite branch: `git branch -D feature/<feature-suffix>-prereq-<desc>`.

## Nesting and edge cases

- **A blocker inside the prerequisite:** apply this skill again with the prerequisite as the parent; stacks nest with the same pattern. Keep stacks shallow and always land the bottom first.
- **Prerequisite CI goes red while you are back on feature work:** the drive-to-green duty from the PR-handling rules applies; fix the prerequisite before adding more feature work on top of it.
- **The blocker is NOT needed to deliver the feature:** this skill does not apply. File an issue with native blocked-by/sub-issue links per CLAUDE.md and stay on the feature.

End the skill (step 6) on a one-line status: prerequisite PR number, feature PR retarget state, and confirmation that feature work has resumed. Step 7's completion is reported when the waiter fires.
