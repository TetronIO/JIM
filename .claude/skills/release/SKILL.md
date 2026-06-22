---
name: release
description: Create a new JIM release — updates VERSION, CHANGELOG, PowerShell manifest, commits, tags, and pushes
argument-hint: "[version] (optional — e.g., 0.4.0 or 0.4.0-alpha; if omitted, you'll choose after reviewing the changelog)"
---

# Create a JIM Release

Follow the release process defined in `engineering/RELEASE_PROCESS.md` to create a new release of JIM.

> **Releases are tag-driven.** The `release.yml` workflow runs on tag push, not on commits to `main`. The published artefacts are built from the commit the tag points at. The `main` branch is protected and rejects direct pushes — the release commit must land on `main` via a PR. The cleanest sequence is: PR first, merge, then tag the merge commit on `main` and push the tag.

If a version was provided via `$ARGUMENTS`, note it but do NOT act on it yet — the version will be confirmed after changelog validation.

## Pre-Flight Checks

Before starting, verify:

1. **You are on the `main` branch** and it is clean (no uncommitted changes):
   ```
   git status
   git branch --show-current
   ```

2. **Main is up to date with remote**:
   ```
   git fetch origin main
   git log --oneline origin/main..main
   ```
   If behind, pull first. If ahead or diverged, stop and alert the user.

3. **All tests pass** — run the full solution build and test:
   ```
   dotnet build JIM.sln
   dotnet test JIM.sln
   ```
   If any test fails, stop and report. Do NOT proceed with a failing build.

4. **The `[Unreleased]` section of `CHANGELOG.md` has entries**. If it is empty, warn the user — a release with no changelog entries is unusual.

5. **Review pinned Docker dependencies**: Check that base image digests and apt package versions are current. If Dependabot PRs for Docker digests have been merged since the last release, flag this so the user can verify pinned apt versions still match.

## Documentation Review and Update

Before validating the changelog, ensure all documentation reflects the current state of the codebase.

### 1. Identify what changed since the last release

Get the last release tag and list all commits since then:
```
git describe --tags --abbrev=0
git log <last-tag>..HEAD --oneline --no-merges
```

From this list, identify commits that introduced:
- New features or capabilities
- Changed behaviour or configuration
- New or modified API endpoints
- New connectors, components, or services
- Architectural changes (new projects, containers, dependencies)
- Changed deployment or setup steps

### 2. Review and update documentation

Cross-reference the identified changes against the documentation and update any that are out of date.

**Customer-facing site (`docs/`, MkDocs)**:
- **`README.md`** (root) — Feature list, quick-start instructions, architecture overview, prerequisites
- **`docs/index.md`** — MkDocs landing page (feature cards, capabilities)
- **`docs/getting-started/`** — Deployment and first-run guidance
- **`docs/concepts/`** — Architecture, sync pipeline, Sync Rules, JML lifecycle
- **`docs/administration/`** — Configuration env vars, SSO setup, troubleshooting
- **`docs/connectors/`** — LDAP, File, etc.
- **`docs/powershell/`** — Cmdlet reference pages
- **`docs/developer/`** — Architecture, building, testing, contributing
- **`docs/api/`** — REST endpoint docs
- **`docs/reference/`** — Roadmap, glossary
- **Any other `.md` files in `docs/`** that are affected by the changes

**Internal engineering docs (`engineering/`, not part of MkDocs site)**:
- **`engineering/DEVELOPER_GUIDE.md`** — Comprehensive development guide
- **`engineering/DATABASE_GUIDE.md`** — Schema, migrations, connection pooling, backup/restore
- **`engineering/TESTING_STRATEGY.md`** / **`engineering/INTEGRATION_TESTING.md`** — Testing approach
- **`engineering/COMPLIANCE_MAPPING.md`** — Security framework mapping
- **`engineering/CACHING_STRATEGY.md`** — Caching architecture
- **`engineering/CANCELLATION_SAFETY.md`** — Cancellation invariants
- **`engineering/OPTIMISATIONS.md`** — Performance notes
- **`engineering/RELEASE_PROCESS.md`** — Release steps (if the process itself has changed)
- **`engineering/JIM_AI_ASSISTANT_CONTEXT.md`** / **`engineering/JIM_AI_ASSISTANT_INSTRUCTIONS.md`** — AI assistant docs (bump their `Document Version` field if content changes)

For each file, check whether the changes since the last release have made any content inaccurate, incomplete, or missing. Make the updates directly — do not just flag them.

### 3. Review and update architectural diagrams

#### C4 Model (Structurizr)

1. **Review `engineering/diagrams/structurizr/workspace.dsl`** against the current codebase. Check that:
   - All containers (services, databases) are represented
   - All components within each container are current
   - Relationships between components/containers are accurate
   - Any new connectors, services, or significant components added since the last release are included
   - Removed or renamed components are cleaned up
   - Hard-coded numbers in container/component descriptions (e.g. "Cross-platform module with N cmdlets") still match reality — verify against `src/JIM.PowerShell/JIM.psd1` `FunctionsToExport`

2. **Update the DSL** if any changes are needed.

3. **Regenerate the C4 SVG images**:
   ```
   jim-diagrams
   ```
   This exports both light and dark theme SVGs to `docs/diagrams/images/light/` and `docs/diagrams/images/dark/`.

#### Mermaid Process Diagrams

Review each Mermaid diagram in `docs/developer/diagrams/` against the current codebase behaviour:

- `FULL_IMPORT_FLOW.md` — Object import, duplicate detection, deletion detection
- `FULL_SYNC_CSO_PROCESSING.md` — Per-CSO sync decision tree
- `DELTA_SYNC_FLOW.md` — Delta sync watermark and early exit logic
- `EXPORT_EXECUTION_FLOW.md` — Export batching, parallelism, retry
- `PENDING_EXPORT_LIFECYCLE.md` — Pending export state machine
- `WORKER_TASK_LIFECYCLE.md` — Worker polling, dispatch, heartbeat
- `SCHEDULE_EXECUTION_LIFECYCLE.md` — Schedule step groups and recovery
- `CONNECTOR_LIFECYCLE.md` — Connector interface and lifecycle
- `ACTIVITY_AND_RPEI_FLOW.md` — Activity and RPEI accumulation
- `MVO_DELETION_AND_GRACE_PERIOD.md` — Deletion rules and grace periods

Each diagram has a `> Last updated: <date>, JIM v<version>` line near the top. If the logic still matches, no edit is needed. If you do edit a diagram, update that line to today's date and the version being released.

#### Diagram references in documentation

Verify that pages embedding or linking to diagrams are up to date:
- `README.md` — SVG references in the Architecture section (light/dark variants under `docs/diagrams/images/`)
- `docs/concepts/architecture.md` and other docs/ pages that embed C4 diagrams
- `engineering/DEVELOPER_GUIDE.md` — C4 and Mermaid diagram listings and links

If new diagrams were added or existing ones renamed/removed, update these references accordingly.

### 4. Generate junctional.io site update prompt

Fetch the public marketing site at `https://junctional.io/` and compare its content against the current state of JIM. Areas to inspect:

1. **Feature claims** — Are listed features still accurate? Are significant new features missing?
2. **Architecture descriptions** — Does the site's description of JIM's architecture match the current state?
3. **Connector/integration list** — Are all supported connectors and integrations represented?
4. **Deployment/requirements** — Are prerequisites and deployment descriptions current?
5. **Screenshots or visuals** — Are they representative of the current UI?

> Note: the legacy `tetron.io/jim` page has been retired; do NOT fetch or reference it.

This skill **does not edit the marketing site directly, and does not present marketing recommendations inline in chat**. The junctional.io site has its own dedicated agent which is authoritative on the site's structure, tone, and conventions, and will know more about the site than you do. Your job here is to **prime that agent with content-change suggestions**, not to dictate changes or critique the site's structure.

Produce a markdown prompt document at `/tmp/junctional-site-prompt-v<version>.md` (outside the JIM repo — do NOT add it to git, do NOT place it under the workspace). The user will copy this file out and hand it to the junctional.io site agent in a separate session.

The prompt should:
- Open with context: this is an advisory list of content suggestions arising from the JIM v<version> release; the site agent is authoritative and should treat each item as a suggestion to evaluate, refine, or reject as it sees fit
- Be written in second person addressed to the site agent ("you may want to consider …"), not as instructions to a human
- Group suggestions by area (features, connectors, architecture, deployment, visuals, removals) so the site agent can prioritise
- For each suggestion, include: **what area it relates to**, **what changed in JIM** (with a brief technical reason or pointer to the changelog entry), **what the site currently says or shows** (if you observed it during the fetch), and **a suggestion for what could be added/changed/removed** — phrased as "consider …", "you may want to …", or "this might be worth refreshing because …", never as "change X to Y"
- Highlight any deprecations or removals that the site might still be advertising
- Close with a note that screenshots/visuals are out of scope of this prompt; the site agent should request fresh screenshots from the user separately if it decides any are needed

After writing the file, tell the user the absolute path to the prompt and remind them it is intentionally outside the repo and should be handed to the junctional.io site agent. Do NOT stage or commit the file.

### 5. Present documentation changes for review

Show the user a summary of all documentation and diagram changes made:
- List of files updated and what changed in each
- Any new diagrams added
- The path to the junctional.io site prompt file (e.g. `/tmp/junctional-site-prompt-v<version>.md`), noted as out-of-repo content for the user to hand to the junctional.io site agent
- Ask the user to confirm before proceeding to changelog validation

Once confirmed, commit the documentation updates on a release branch (do NOT commit to `main` directly — branch protection blocks that). If you are not already on a release branch, create one first:
```bash
git checkout -b release/v<version>
git add README.md docs/ engineering/ docs/diagrams/images/
git commit -m "docs: update documentation and diagrams for v<version> release"
```

## Changelog Validation

Before proceeding with version updates, validate AND prune the `[Unreleased]` section. Entries are written at PR time by whoever made the change, optimised for "did I capture my change" rather than "would a customer care", so the section accumulates test-only, internal, and over-long entries. **The default failure mode of this step is letting those through.** It is therefore removal- and tightening-biased, not just gap-finding. Do NOT characterise the section as "fine" or "well-maintained" without running every entry through the checks below.

1. **Run the mechanical lint first** (zero false positives; catches off-list emojis):
   ```
   pwsh -File ./scripts/Lint-Changelog.ps1 -WarningsAsErrors
   ```
   Every **ERROR** must be resolved before release: an entry leading with an off-list emoji (e.g. a test tube or lipstick) is almost always a non-customer-facing entry that should be **removed**, not re-emojied. Treat every **WARNING** (too long; references a test scenario/integration test; exposes EF Core internals; names an internal `*Async` method; describes a refactor) as a removal-or-rewrite candidate to resolve, not noise to skip.

2. **Get the last release tag and list commits since it**:
   ```
   git describe --tags --abbrev=0
   git log <last-tag>..HEAD --oneline --no-merges
   ```

3. **Audit EVERY existing `[Unreleased]` entry for removal (mandatory pass).** For each entry, classify keep/remove against the "When NOT to add an entry" rules in `engineering/CLAUDE.md`. **Remove** (do not keep "just in case") anything that is:
   - test-only — integration/unit test scenarios or helpers (a `🧪`-style entry should essentially never ship to customers);
   - dev tooling, CI/CD, or build infrastructure;
   - internal refactoring with no user-facing impact (e.g. an entry whose substance is "now routes through the lightweight `SomethingAsync` variant");
   - a trivial UI or internal tweak a customer would not notice or care about.
   Present the proposed removals to the user with a one-line reason each, and confirm before deleting.

4. **Find missing entries.** Cross-reference the commit list against what remains: list any user-facing change (feature, fix, performance, behaviour change, removal) with no corresponding entry, propose wording, and confirm before adding. Correctly-excluded commits (docs, CI, tests, refactoring) need no entry.

5. **Tighten every kept entry (default, not optional).** Rewrite each remaining entry to one sentence, two at most: benefit-led, dropping internal class/method/file names, "previously X / now Y" chains, and step-by-step fix narration (full criteria in `engineering/CLAUDE.md`). Lead with a canonical emoji (✨ new, 🐛 fix, ⚡ performance, 🔄 changed, 🗑️ removed, 🔒 security, 📦 deployment, 🖥️ UI/UX). Present the rewrites. Accuracy is not the bar here, customer-readability is — do not leave an over-long entry because it is "correct".

6. **Apply the confirmed removals, additions, and rewrites to `[Unreleased]`, then re-run the lint clean** (`pwsh -File ./scripts/Lint-Changelog.ps1 -WarningsAsErrors` exits 0) before proceeding.

## Version Confirmation

Now that the changelog is complete and validated, the user can make an informed versioning decision.

1. Read the current `VERSION` file to show the current version
2. Present the **final `[Unreleased]` section** of `CHANGELOG.md` to the user — this is what will become the release notes
3. If a version was provided via `$ARGUMENTS`, propose it. Otherwise, suggest a version based on the scope of changes (patch for fixes only, minor for new features, major for breaking changes)
4. **Ask the user to confirm or choose the version** before proceeding

Do NOT proceed to Step 1 until the user has confirmed the version number.

## Step 1: Update VERSION File

Write the new version (without `v` prefix) to the `VERSION` file:
```
echo "<version>" > VERSION
```

## Step 2: Update CHANGELOG.md

1. Read the current `CHANGELOG.md`
2. Replace `## [Unreleased]` with:
   ```
   ## [Unreleased]

   ## [<version>] - <today's date in YYYY-MM-DD format>
   ```
   This preserves an empty Unreleased section above the new version.
3. Update the comparison links at the bottom of the file:
   - Change the `[Unreleased]` link to compare against the new tag:
     ```
     [Unreleased]: https://github.com/TetronIO/JIM/compare/v<version>...HEAD
     ```
   - Add a new version comparison link between `[Unreleased]` and the previous version:
     ```
     [<version>]: https://github.com/TetronIO/JIM/compare/v<previous-version>...v<version>
     ```

## Step 3: Update PowerShell Manifest

Edit `src/JIM.PowerShell/JIM.psd1`:
- Update `ModuleVersion` to the numeric part of the version (e.g., `0.4.0`)
- If the version has a prerelease suffix (e.g., `0.4.0-alpha`), uncomment/set `Prerelease = 'alpha'`
- If the version is stable (no suffix), ensure `Prerelease` is commented out

## Step 4: Present Summary for Review

Show the user:
1. The version being released
2. A diff summary of all changes (`git diff --stat`)
3. The full changelog section for the new version
4. Ask for confirmation before committing

## Step 5: Commit on a Release Branch

The `main` branch is protected and rejects direct pushes. The release commit must land on `main` via a PR.

If you have not already created a release branch (e.g. for documentation updates earlier), create one now and commit the release files:

```bash
git checkout -b release/v<version>
git add VERSION CHANGELOG.md src/JIM.PowerShell/JIM.psd1
git commit -m "Release v<version>"
```

If you are already on `release/v<version>` from the documentation step, add the release files to a separate commit on the same branch.

## Step 6: Open the Release PR

Push the release branch and open a PR to `main`:

```bash
git push -u origin release/v<version>
gh pr create --title "Release v<version>" --body "$(cat <<'EOF'
## Summary

Release commit for v<version>.

Updates:
- `VERSION`: <previous> → <version>
- `CHANGELOG.md`: new `[<version>] - <date>` section + comparison links
- `src/JIM.PowerShell/JIM.psd1`: `ModuleVersion` → `<version>`

## Test plan

- [ ] CI passes (build, test, scan, code review, container scan, dependency scan)
- [ ] Tag `v<version>` will be created on the merge commit after this PR is merged
EOF
)"
```

Wait for all required status checks to pass (currently 7), then merge the PR. Use a **merge commit** or **squash** — do not rebase, because the tag in Step 8 must point at a commit that is on `main`. After merge, switch back to `main` and pull:

```bash
git checkout main
git pull origin main
```

## Step 7: Tag the Merge Commit and Push the Tag

The release workflow runs on tag push. It will build and publish artefacts from whatever commit the tag points at, so the tag must point at a commit that is on `main`.

JIM enforces signed tags (`tag.forceSignAnnotated=true`), so use `git tag -a -m`, not a lightweight tag:

```bash
git tag -a v<version> -m "Release v<version>"
```

**Ask the user for confirmation before pushing the tag.** This is the point of no return — pushing the tag triggers PSGallery publishing, container image push, and GitHub Release creation, all of which are externally visible.

```bash
git push origin v<version>
```

The tag-push triggers the release workflow which:
1. Validates the build and runs all tests
2. Builds and pushes Docker images to `ghcr.io/tetronio/jim-{web,worker,scheduler}:<version>`
3. Publishes the PowerShell module to PSGallery
4. Creates an air-gapped deployment bundle
5. Attaches standalone deployment files to the release (`docker-compose.yml`, `docker-compose.production.yml`, `.env.example`)
6. Creates a GitHub Release with all assets

## Step 8: Post-Release Verification

Tell the user to verify after the workflow completes:
- [ ] GitHub Release page has the bundle, checksums, and standalone deployment files
- [ ] Docker images are available: `ghcr.io/tetronio/jim-web:<version>`, etc.
- [ ] PowerShell module is available on PSGallery: `Install-Module JIM -RequiredVersion <version>`
- [ ] The `setup.sh` installer detects the new release

Provide the Actions URL for monitoring:
```
https://github.com/TetronIO/JIM/actions
```

## Step 9: Long-form Release Announcement (GitHub Discussions)

The curated `CHANGELOG.md` / GitHub Release notes are intentionally terse and customer-facing. The detail that does NOT belong there (performance work, test-coverage expansion, internal hardening) has a home here instead: a long-form announcement in GitHub Discussions, written for an interested-but-external reader. This is where the entries the Changelog Validation step removed can resurface, reframed as outcomes.

**Prerequisites:** Discussions enabled on the repo with an **Announcements** category (maintainer-post-only, which suits release notes), and `gh` authenticated with permission to create discussions. If Discussions is disabled or the user does not want an announcement for this release, skip this step; it is additive.

1. **Draft the announcement.** You write it (you have the full context at release time). Synthesise it from the curated `[<version>]` changelog section, the full commit range (`git log <previous-tag>..v<version> --no-merges`), and the merged PR titles/bodies in that range. Write it in the JIM product-team voice, more expansive than the changelog, with sections roughly like:
   - a one-paragraph headline ("what's in v<version>");
   - **What's new** — the customer-facing features, each expanded with why it matters and a short example where useful;
   - **Fixes** — notable corrected behaviour;
   - **Under the hood** — the engineering, performance, quality, and testing improvements that were (rightly) kept out of the changelog, reframed as outcomes ("X is now faster / more robust") rather than internal method names;
   - **Upgrade notes** — anything an operator must know (e.g. a licence change, a config change);
   - links to the GitHub Release and the full changelog.
   Write it to `/tmp/release-discussion-v<version>.md` (outside the repo; do NOT commit it).

2. **Human review is mandatory.** This is public, customer-facing, AI-drafted content. Present the draft path to the user and ask them to review and edit `/tmp/release-discussion-v<version>.md` before anything is posted. Do NOT post unreviewed; there is no "draft" state in GitHub Discussions, so review happens before publishing, not after.

3. **Post it once approved:**
   ```
   # Dry run first to confirm the repository and Announcements category resolve:
   pwsh -File ./scripts/Publish-ReleaseDiscussion.ps1 -Title "JIM v<version>" -BodyFile /tmp/release-discussion-v<version>.md -WhatIf
   # Then post:
   pwsh -File ./scripts/Publish-ReleaseDiscussion.ps1 -Title "JIM v<version>" -BodyFile /tmp/release-discussion-v<version>.md
   ```
   The script resolves the category and prints the published discussion URL.

4. **Cross-link** (optional): append the discussion URL to the GitHub Release notes (`gh release edit v<version>`) so readers can find the long-form write-up, and report the URL to the user.

## Recovery: PR rejected after release commit was made

If you created the release commit and tried to push directly to `main` (e.g. with `git push origin main --tags`), the tag will push but `main` will be rejected by branch protection. To recover:

1. The tag is now on the remote and the release workflow has likely already started. **Do not delete the tag** unless the workflow has clearly failed — letting it run is usually safer than re-tagging.
2. Push the release commit to a release branch and open the PR (Step 7 above) so `main` catches up to the published release.
3. Once the PR merges, `main` aligns with what was published.
