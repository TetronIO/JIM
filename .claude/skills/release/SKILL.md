---
name: release
description: Create a new JIM release — updates VERSION, CHANGELOG, PowerShell manifest, commits, tags, and pushes
argument-hint: "<version> (e.g., 0.4.0 or 0.4.0-alpha)"
allowed-tools: Bash, Read, Edit, Write, Glob, Grep
---

# Create a JIM Release

Follow the release process defined in `docs/RELEASE_PROCESS.md` to create a new release of JIM.

The target version is `$ARGUMENTS`. If no version was provided, read the current `VERSION` file, show the user the current version and the `[Unreleased]` section of `CHANGELOG.md`, and ask what version to release.

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

5. **Review tone and quality of existing entries.** The changelog is a customer-facing product document. Entries should:
   - Be written as product changes (benefit/outcome), not developer notes (implementation detail)
   - Lead with an emoji for visual scanning (✨ new, 🐛 fix, ⚡ performance, 🔄 changed, 🗑️ removed, 🔒 security, 📦 deployment, 🖥️ UI/UX)
   - Make JIM appear useful, reliable, sophisticated, and exciting
   - Exclude internal/trivial changes that don't matter to customers
   If existing entries need rewording to meet this standard, propose edits.

6. **If entries need adding or rewording**, update the `[Unreleased]` section of `CHANGELOG.md` with the confirmed changes before proceeding to Step 1.

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

## Step 4: Update Release History Table

Update the release history table in `docs/RELEASE_PROCESS.md`:
- Add a new row at the top of the table with the version, today's date, and a brief summary
- The summary should be a concise description derived from the changelog entries

## Step 5: Present Summary for Review

Show the user:
1. The version being released
2. A diff summary of all changes (`git diff --stat`)
3. The full changelog section for the new version
4. Ask for confirmation before committing

## Step 6: Commit

After user confirmation, commit all changes:

```bash
git add VERSION CHANGELOG.md src/JIM.PowerShell/JIM.psd1 docs/RELEASE_PROCESS.md
git commit -m "Release v<version>"
```

## Step 7: Tag

Create the release tag:
```bash
git tag v<version>
```

## Step 8: Push

**Ask the user for confirmation before pushing.** Then:
```bash
git push origin main --tags
```

Inform the user that the push will trigger the release workflow which:
1. Validates the build and runs all tests
2. Builds and pushes Docker images to `ghcr.io/tetronio/jim-{web,worker,scheduler}:<version>`
3. Publishes the PowerShell module to PSGallery
4. Creates an air-gapped deployment bundle
5. Attaches standalone deployment files to the release (`docker-compose.yml`, `docker-compose.production.yml`, `.env.example`)
6. Creates a GitHub Release with all assets

## Step 9: Post-Release Verification

Tell the user to verify after the workflow completes:
- [ ] GitHub Release page has the bundle, checksums, and standalone deployment files
- [ ] Docker images are available: `ghcr.io/tetronio/jim-web:<version>`, etc.
- [ ] PowerShell module is available on PSGallery
- [ ] The `setup.sh` installer can detect the new release

Provide the Actions URL for monitoring:
```
https://github.com/TetronIO/JIM/actions
```