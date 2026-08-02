---
name: stack-pr
description: Isolate work discovered mid-feature into the next layer of a GitHub stacked PR; branch off the current feature branch, implement to full standard, open the layer's PR against the branch below, link the stack, and carry on.
argument-hint: "[one-line description of the discovered work]"
---

# Stack a new PR layer on the current feature branch

This skill implements the policy in CLAUDE.md ("Stacked PRs for discovered work"): something has been found mid-feature that must be solved to deliver the objective but is not semantically part of the current branch's work. It gets its own layer in a GitHub native stacked PR (public preview), never folded into the feature branch, never deferred to an issue.

A stack is a **sequential chain**: each layer branches off the branch below it, each PR targets the branch below it, and only the bottom PR targets `main`. Merging is bottom-up; merging an upper PR atomically merges everything below it, each PR recorded individually.

If `$ARGUMENTS` is non-empty, treat it as the description of the discovered work; otherwise derive one from the conversation context. Announce it to the user in one line and proceed; do not ask whether to stack (the policy pre-authorises it). If the work itself is architecturally significant with multiple sensible approaches, the CLAUDE.md "Ask before significant changes" rule applies to the approach, not to the decision to stack.

## 0. One-time setup

The `gh stack` CLI is an extension (requires gh 2.90+):

```
gh extension list | grep -q gh-stack || gh extension install github/gh-stack
```

## 1. Park the current work

```
git status
git branch --show-current
```

- You MUST be on a feature branch. If on `main`, stop; there is nothing to stack on.
- If the tree is dirty, commit the WIP to the current branch: `git add -A && git commit -m "wip: park work before stacking next layer"`. Prefer a `wip:` commit over stashing; squash-merge collapses it on landing, and a stash is invisible to other sessions.
- Push the current branch so the chain's lower layer exists on the remote: `git push` (or `git push -u origin <branch>` if never pushed).

## 2. Create the layer off the current branch

```
git checkout -b feature/<feature-suffix>-stack-<desc>
```

- Branch **from the current feature branch**, never from `main`; the layer must sit on top of the work below it.
- `<feature-suffix>` is the feature branch name without its `feature/` prefix; `<desc>` is a short kebab-case description. Example: on `feature/csv-connector`, an encoding bug becomes `feature/csv-connector-stack-fix-encoding`.
- Alternatively `gh stack add feature/<feature-suffix>-stack-<desc>` creates and tracks the branch in one step if the stack is already initialised.

## 3. Implement the layer to full standard

Being unplanned lowers no bars. All CLAUDE.md rules apply:

- TDD (failing test first; for a bug the test must reproduce it before the fix).
- Build/test gates: targeted during the loop, `dotnet build JIM.sln` and `dotnet test JIM.sln` clean before the PR (or the documented non-code exceptions).
- Changelog entry under `[Unreleased]` plus public docs if the change is user-facing.
- Commit with a clear message and push: `git push -u origin feature/<feature-suffix>-stack-<desc>`.

## 4. Open the layer's PR and link the stack

Follow `/pr-merge` conventions (title under 70 chars, JIM body template), with the base set to the branch below, NOT `main`:

```
gh pr create --base feature/<feature> --title "fix: <desc>" --body "..."
```

- GitHub recognises aligned chains (each PR's base is the head of the PR below) and shows a banner offering to link them into a stack; accept it. With the `gh stack` CLI, `gh stack submit` pushes the layers and creates linked PRs in one step.
- Reviewers see only this layer's diff. CI and all of `main`'s branch protections run for every PR in the stack.
- Do NOT queue `--auto`: auto-merge is unsupported for stacked PRs. Landing is handled by `/pr-merge` when the objective is complete.

## 5. Continue work in the right layer

Code may only depend on its own layer or a lower one:

- **Remaining feature work needs this layer's code** → continue in a new layer on top: `gh stack add` (or branch off this layer), and repeat this pattern.
- **Remaining feature work is independent of this layer** → `git checkout feature/<feature>` and continue below. Any change to a lower layer breaks the stack's linearity; restack before merging with the web "Rebase stack" button, or locally:
  ```
  gh stack rebase
  gh stack push
  ```

## 6. Landing

Use `/pr-merge`; its "Stacked PRs" section covers the differences (rebase to stay current, no auto-merge, bottom-up atomic merge, merging from the top when the objective is done, cleanup of layer branches).

## Notes and edge cases

- Stacks require all branches in the same repository and fully linear history between layers; within a stack, rebase (cascading) replaces the usual merge-don't-rebase rule.
- Keep layers shallow and single-concern; a discovery inside a layer gets its own layer the same way.
- Layer branches belong to the same session and objective as the feature branch; the `-stack-` naming keeps the lineage visible across parallel sessions.
- **The discovery is NOT needed to deliver the objective** → this skill does not apply. File an issue with native blocked-by/sub-issue links per CLAUDE.md and stay on the feature.

End the skill on a one-line status: layer branch name, its PR number and base, and which layer work is continuing in.
