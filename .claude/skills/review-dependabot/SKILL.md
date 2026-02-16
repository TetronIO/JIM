---
name: review-dependabot
description: Review, assess, and merge open Dependabot PRs using JIM's supply chain security and dependency pinning requirements
argument-hint: "[merge|review-only]"
allowed-tools: Bash, Read, Glob, Grep, WebSearch, WebFetch
---

# Review and Merge Dependabot PRs

Review all open Dependabot PRs against JIM's supply chain security and dependency governance policies, then merge those that pass assessment.

If `$ARGUMENTS` is "review-only", produce the assessment but do NOT merge. Otherwise, present findings and merge approved PRs.

## Step 1: List Open PRs

```
gh pr list --author "app/dependabot" --state open --json number,title,url,createdAt,labels
```

If there are no open Dependabot PRs, report that and stop.

## Step 2: Gather PR Details

For each PR, collect:
- The diff (`gh pr diff <number>`)
- CI check status (`gh pr checks <number>`)
- The PR body for Dependabot's compatibility score and changelog info

## Step 3: Identify the Update Type

Categorise each PR into one of three ecosystems:

### Docker Base Images
- Check: Does the update stay within the same major.minor version (e.g., 9.0)?
- Check: Is the publisher Microsoft (`mcr.microsoft.com`) or another trusted source?
- Check: Is the change digest-only (`@sha256:` swap) or a tag change?
- **CRITICAL - Apt Package Pinning**: JIM Dockerfiles pin functional apt packages to exact versions. Before approving ANY Docker base image digest update:
  1. Identify which Dockerfiles have pinned apt packages by reading the changed Dockerfile
  2. For each pinned package, verify it is still available at the same version in the new base image:
     ```bash
     docker run --rm <image>@<new-digest> bash -c \
       "apt-get update -qq && apt-cache policy <package1> <package2> ..."
     ```
  3. Current pinned packages across services:
     - **JIM.Web** (aspnet base): `libldap-common`, `libldap-2.5-0`, `cifs-utils`
     - **JIM.Worker** (runtime base): `libldap-common`, `libldap-2.5-0`
     - **JIM.Scheduler** (runtime base): No apt packages
  4. If a pinned version is NO LONGER AVAILABLE: **Do NOT merge**. Report the version mismatch and the available versions so the user can decide whether to update the pin.

### NuGet Packages
- Check: Is this a patch or minor update (not major)?
- Check: Review the package changelog for breaking changes
- Check: Is the package from a trusted publisher?
- Check: Run `dotnet list package --vulnerable` if concerned about transitive vulnerabilities
- Check: Does CI build and all tests pass?

### GitHub Actions
- Check: Is this a patch or minor update within the same major version tag?
- Check: Review the action's changelog for changes to inputs, outputs, or behaviour
- Check: Is the action from a trusted publisher (GitHub, Microsoft, well-known org)?

## Step 4: Security Advisory Check

Search for any security advisories related to the updates:
- For .NET updates: Search for ".NET [version] security update [month] [year] CVE"
- For NuGet packages: Check GitHub Advisory Database
- For GitHub Actions: Check the action's security advisories

Note whether the update addresses any known CVEs - this increases urgency to merge.

## Step 5: Present Assessment

Create a summary table with columns:
| PR | Update | Service | CI Status | Pinning Check | Security | Recommendation |

Recommendations should be one of:
- **Merge** - All checks pass
- **Merge (security)** - Addresses a CVE, prioritise
- **Hold** - Needs investigation (explain why)
- **Reject** - Fails assessment (explain why)

## Step 6: Merge

Unless running in review-only mode:

1. Identify any merge ordering constraints (multiple PRs touching the same file)
2. Merge approved PRs in the correct order using `gh pr merge <number> --merge`
3. Include a merge comment noting what was verified (pinning, security advisory, etc.)
4. If a PR has merge conflicts after earlier merges, comment `@dependabot rebase` and wait for the rebase before merging
5. Report final status of all PRs

## Reference: JIM's Supply Chain Security Requirements

- All dependency updates require human review - no auto-merge
- Docker images pinned by digest (`@sha256:...`)
- Functional apt packages pinned to exact versions
- NuGet packages pinned in `.csproj` files
- GitHub Actions pinned by major version tag
- Prefer Microsoft-maintained and well-established packages
- Full details in CLAUDE.md under "Supply Chain Security" and docs/DEVELOPER_GUIDE.md under "Dependency Pinning and Updates"
