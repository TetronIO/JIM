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
- **`docs/concepts/`** — Architecture, sync pipeline, sync rules, JML lifecycle
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
- **`engineering/EXPRESSIONS_GUIDE.md`** — Expression language reference
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

### 4. Review marketing site content

Fetch the public marketing site at `https://tetron.io/jim/` and compare its content against the current state of JIM:

1. **Feature claims** — Are all listed features still accurate? Are significant new features missing?
2. **Architecture descriptions** — Does the site's description of JIM's architecture match the current state?
3. **Connector/integration list** — Are all supported connectors and integrations listed?
4. **Deployment/requirements** — Are prerequisites and deployment descriptions current?
5. **Screenshots or visuals** — Are they representative of the current UI?

The release agent cannot update the marketing site directly. Instead, present a clear summary of recommended changes to the user, structured so that a website development agent can implement them easily:
- List each recommended change with: **what to change**, **where on the page**, **what the current text/content says**, and **what it should say or show instead**
- Include any new feature descriptions that should be added, written in marketing-appropriate language
- Flag any content that should be removed (e.g., features that were deprecated or renamed)

### 5. Present documentation changes for review

Show the user a summary of all documentation and diagram changes made:
- List of files updated and what changed in each
- Any new diagrams added
- Marketing site recommendations (for manual action)
- Ask the user to confirm before proceeding to changelog validation

Once confirmed, commit the documentation updates on a release branch (do NOT commit to `main` directly — branch protection blocks that). If you are not already on a release branch, create one first:
```bash
git checkout -b release/v<version>
git add README.md docs/ engineering/ docs/diagrams/images/
git commit -m "docs: update documentation and diagrams for v<version> release"
```

## Changelog Validation

Before proceeding with version updates, validate that the changelog is complete.

Per CLAUDE.md, changelog entries should be added with each commit/PR — but things can be missed. This step catches gaps.

1. **Get the last release tag**:
   ```
   git describe --tags --abbrev=0
   ```

2. **List all commits since the last release**, excluding docs-only, CI, and test-only changes:
   ```
   git log <last-tag>..HEAD --oneline --no-merges
   ```

3. **Cross-reference commits against the `[Unreleased]` section** of `CHANGELOG.md`. For each commit, determine if it warrants a changelog entry using the rules from CLAUDE.md:
   - **Needs entry**: New features, bug fixes, performance improvements, changed behaviour, removed functionality — anything a customer or administrator would care about
   - **Does NOT need entry**: Documentation-only (`.md`), CI/CD workflows, dev tooling, refactoring with no user-facing impact, test-only changes, trivial internal changes

4. **Report findings** to the user:
   - List any commits that appear to be missing from the changelog (user-facing changes with no corresponding entry)
   - List any commits that were correctly excluded (docs, CI, tests, refactoring)
   - If gaps are found, propose changelog entries and ask the user to confirm before adding them

5. **Review tone, length, and quality of existing entries.** The changelog is a customer-facing product document. Entries should:
   - Be written as product changes (benefit/outcome), not developer notes (implementation detail)
   - Be succinct: one sentence per entry, two at most. Flag any entry longer than two sentences for tightening
   - Avoid internal class/method/file names, step-by-step fix explanations, framework-default tutorials, and "previously X / now Y" multi-clause sentences (full criteria in `engineering/CLAUDE.md`)
   - Lead with an emoji for visual scanning (✨ new, 🐛 fix, ⚡ performance, 🔄 changed, 🗑️ removed, 🔒 security, 📦 deployment, 🖥️ UI/UX)
   - Make JIM appear useful, reliable, sophisticated, and exciting
   - Exclude internal/trivial changes that don't matter to customers
   If existing entries need rewording or shortening to meet this standard, propose edits.

6. **If entries need adding or rewording**, update the `[Unreleased]` section of `CHANGELOG.md` with the confirmed changes before proceeding.

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

## Recovery: PR rejected after release commit was made

If you created the release commit and tried to push directly to `main` (e.g. with `git push origin main --tags`), the tag will push but `main` will be rejected by branch protection. To recover:

1. The tag is now on the remote and the release workflow has likely already started. **Do not delete the tag** unless the workflow has clearly failed — letting it run is usually safer than re-tagging.
2. Push the release commit to a release branch and open the PR (Step 7 above) so `main` catches up to the published release.
3. Once the PR merges, `main` aligns with what was published.