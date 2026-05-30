# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Opens (or updates) a pull request bumping the apt package pins that
    check-apt-pins.ps1 -Apply has rewritten in the working tree.

.DESCRIPTION
    Run after `check-apt-pins.ps1 -Apply` has modified one or more Dockerfiles.
    It publishes those changes as a PR for human evaluation.

    Two constraints shape the implementation:

      1. `main` requires signed commits (the "Protect Main" ruleset). A plain
         `git commit` from a GitHub Actions runner is unsigned and cannot be
         merged. Commits created through the GitHub API are signed by GitHub and
         show as "Verified", so this script commits via the GraphQL
         createCommitOnBranch mutation rather than git.

      2. CI does not build the JIM images on a PR, so the bump is not build-tested
         in CI. check-apt-pins.ps1 already validates installability against the
         base image before proposing, which is the mitigation; this script only
         publishes what that step validated.

    Idempotency: the bot uses a single stable branch. On each run the branch is
    reset to the tip of the base branch and a fresh single commit is applied, so
    an already-open PR is updated in place rather than duplicated, and the branch
    never accumulates stale history.

    Requires: gh CLI authenticated with a token carrying `contents: write` and
    `pull-requests: write` (the workflow's GITHUB_TOKEN). $env:GITHUB_REPOSITORY
    must be set (it is in Actions); otherwise pass -Repository owner/repo.

.PARAMETER BodyFile
    Path to a file containing the markdown table of proposed bumps (the `pr_body`
    output of check-apt-pins.ps1). Embedded in the PR description.

.PARAMETER BaseBranch
    The branch to target and reset from. Defaults to 'main'.

.PARAMETER Branch
    The bot's working branch. Defaults to 'automation/apt-pin-updates'.

.PARAMETER Repository
    owner/repo. Defaults to $env:GITHUB_REPOSITORY.

.PARAMETER DryRun
    Print the actions and the GraphQL payload instead of calling the API. For
    local validation.
#>

[CmdletBinding()]
param(
    [string]$BodyFile,
    [string]$BaseBranch = 'main',
    [string]$Branch = 'automation/apt-pin-updates',
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not $Repository) { throw 'Repository not set. Pass -Repository owner/repo or set GITHUB_REPOSITORY.' }

# Files changed by the -Apply step. Scope strictly to Dockerfiles: the bump only
# ever rewrites pinned versions in Dockerfiles, and this guards against
# committing any other stray working-tree artefact (e.g. the generated
# apt-pin-pr-body.md).
$changed = @(git diff --name-only | Where-Object { $_ -match '(^|/)Dockerfile$' })
if ($changed.Count -eq 0) {
    Write-Host 'No working-tree changes; nothing to propose.'
    exit 0
}

Write-Host "Changed files:"; $changed | ForEach-Object { Write-Host "  $_" }

$prBody = if ($BodyFile -and (Test-Path $BodyFile)) { Get-Content -Path $BodyFile -Raw } else { '' }
$commitHeadline = 'chore(deps): bump pinned apt package versions'
$commitBody = @"
Newer versions of apt packages pinned in production Dockerfiles are available in
the Ubuntu archive and have been validated as installable against the pinned base
image. Raised for evaluation by the apt-pin-check workflow.

$prBody
"@

# Build the GraphQL fileChanges additions (path + base64 contents).
$additions = foreach ($path in $changed) {
    @{
        path     = $path
        contents = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path (Get-Location) $path)))
    }
}

# Tip of the base branch: the commit we reset the bot branch to and expect as the
# parent of the new commit.
function Invoke-Gh { param([string[]]$GhArgs) & gh @GhArgs }

$baseSha = (Invoke-Gh @('api', "repos/$Repository/git/ref/heads/$BaseBranch", '--jq', '.object.sha')).Trim()
if (-not $baseSha) { throw "Could not resolve $BaseBranch sha." }
Write-Host "$BaseBranch is at $baseSha"

$mutation = @'
mutation($input: CreateCommitOnBranchInput!) {
  createCommitOnBranch(input: $input) { commit { oid url } }
}
'@

$commitInput = @{
    branch          = @{ repositoryNameWithOwner = $Repository; branchName = $Branch }
    message         = @{ headline = $commitHeadline; body = $commitBody }
    fileChanges     = @{ additions = @($additions) }
    expectedHeadOid = $baseSha
}
$variables = @{ input = $commitInput }
$payload = @{ query = $mutation; variables = $variables } | ConvertTo-Json -Depth 10 -Compress

if ($DryRun) {
    Write-Host ''
    Write-Host '== DRY RUN: would reset branch and create signed commit =='
    Write-Host "  branch:  $Branch (reset to $baseSha)"
    Write-Host "  files:   $($changed -join ', ')"
    Write-Host "  PR:      $Branch -> $BaseBranch"
    Write-Host ''
    Write-Host '== GraphQL payload (createCommitOnBranch) =='
    # Re-expand contents as length only, to keep the dry-run output readable.
    $preview = @{ query = '<<createCommitOnBranch>>'; variables = @{ input = @{
        branch = $commitInput.branch; message = $commitInput.message; expectedHeadOid = $baseSha
        fileChanges = @{ additions = @($additions | ForEach-Object { @{ path = $_.path; contents = "<base64:$($_.contents.Length) chars>" } }) }
    } } } | ConvertTo-Json -Depth 10
    Write-Host $preview
    exit 0
}

# Reset (or create) the bot branch at the base tip so the PR contains exactly one
# fresh commit and updates cleanly in place.
$refPath = "repos/$Repository/git/refs/heads/$Branch"
$exists = $true
try { Invoke-Gh @('api', $refPath, '--silent') } catch { $exists = $false }
if ($exists) {
    Write-Host "Resetting $Branch to $baseSha"
    Invoke-Gh @('api', '-X', 'PATCH', $refPath, '-f', "sha=$baseSha", '-F', 'force=true') | Out-Null
} else {
    Write-Host "Creating $Branch at $baseSha"
    Invoke-Gh @('api', '-X', 'POST', "repos/$Repository/git/refs", '-f', "ref=refs/heads/$Branch", '-f', "sha=$baseSha") | Out-Null
}

# Create the signed commit via GraphQL.
$tmp = New-TemporaryFile
try {
    $payload | Out-File -FilePath $tmp -Encoding utf8
    $commitOid = (Get-Content -Raw $tmp | & gh api graphql --input - --jq '.data.createCommitOnBranch.commit.oid').Trim()
    if (-not $commitOid) { throw 'createCommitOnBranch returned no commit oid.' }
    Write-Host "Created signed commit $commitOid on $Branch"
} finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}

# Open the PR if one is not already open for this branch; otherwise it has been
# updated in place by the branch reset + new commit.
$openPr = (Invoke-Gh @('pr', 'list', '--repo', $Repository, '--head', $Branch, '--state', 'open', '--json', 'number', '--jq', '.[0].number')).Trim()
if ($openPr) {
    Write-Host "Updated existing PR #$openPr"
    Invoke-Gh @('pr', 'edit', $openPr, '--repo', $Repository, '--body', $commitBody) | Out-Null
} else {
    Write-Host 'Opening new PR ...'
    Invoke-Gh @('pr', 'create', '--repo', $Repository, '--base', $BaseBranch, '--head', $Branch,
        '--title', $commitHeadline, '--body', $commitBody, '--label', 'dependencies') | Out-Null
}
