# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Opens (or updates, reopens, or deliberately closes) a pull request publishing
    the version-pin bumps that a `check-*-pins.ps1 -Apply` step has rewritten in
    the working tree.

.DESCRIPTION
    Run after a detection script (check-apt-pins.ps1, check-tooling-pins.ps1, ...)
    has rewritten one or more pinned versions in place. This script publishes
    those changes as a PR for human evaluation. It is pin-source agnostic: what
    set of files it commits, the commit message, the branch, and the PR label are
    all supplied by the caller, so the same signed-commit machinery serves every
    Dependabot-invisible pin bot.

    Two constraints shape the implementation:

      1. `main` requires signed commits (the "Protect Main" ruleset). A plain
         `git commit` from a GitHub Actions runner is unsigned and cannot be
         merged. Commits created through the GitHub API are signed by GitHub and
         show as "Verified", so this script commits via the GraphQL
         createCommitOnBranch mutation rather than git.

      2. CI does not necessarily build/test the bump on a PR. Each detection
         script is responsible for validating its own bumps before proposing
         (e.g. check-apt-pins.ps1 proves installability); this script only
         publishes what that step validated.

    Idempotency: each bot uses a single stable branch carrying exactly one commit
    off the tip of the base branch, so an already-open PR is updated in place
    rather than duplicated, and the branch never accumulates superseded bumps.

    That commit is built on a throwaway staging ref and the bot branch is then
    moved straight from its previous bump commit to the new one, in a single ref
    update. The obvious implementation, resetting the bot branch to the base tip
    and committing on top, is what #1374 was: between those two calls the head
    branch carries no commits ahead of its base, GitHub closes a pull request
    that enters that state, and the close landed in the window. It closed #1364
    mid-run, leaving a security pin unlandable while `main` could not build the
    Worker image at all. The bot branch must therefore never be pointed at the
    base tip, however briefly.

    Belt and braces, because the close is asynchronous and a future change could
    reintroduce a window: a bump PR found closed (and unmerged) at the start of a
    run is reopened rather than duplicated, and the PR's state is confirmed once
    more after the push.

    Requires: GH_TOKEN set to a token carrying `contents: write` and
    `pull-requests: write`. In the pin-check workflows this is a GitHub App
    installation token (a service principal), not a personal or GITHUB_TOKEN
    credential. $env:GITHUB_REPOSITORY must be set (it is in Actions); otherwise
    pass -Repository owner/repo.

.PARAMETER BodyFile
    Path to a file containing the markdown table of proposed bumps (the `pr_body`
    output of the detection script). Embedded in the PR description.

.PARAMETER FilePattern
    Regex matched against `git diff --name-only` to select which changed files to
    commit. Scopes the commit strictly to the pin files and guards against
    committing a stray working-tree artefact (e.g. the generated *-pr-body.md).
    Defaults to Dockerfiles (the apt bot).

.PARAMETER CommitHeadline
    The commit/PR title. Defaults to the apt bot's headline.

.PARAMETER CommitBodyIntro
    The prose paragraph placed above the bump table in the commit/PR body.
    Defaults to the apt bot's intro.

.PARAMETER Label
    The label applied to a newly-created PR. Defaults to 'dependencies'.

.PARAMETER BaseBranch
    The branch to target and build the bump commit on. Defaults to 'main'.

.PARAMETER Branch
    The bot's working branch. Defaults to 'automation/apt-pin-updates'.

.PARAMETER Repository
    owner/repo. Defaults to $env:GITHUB_REPOSITORY.

.PARAMETER CloseStalePr
    When there is nothing to propose, close the bot's open PR with the reason
    stated in a comment. Opt-in, and intended for the workflows, which only reach
    this script with a clean tree when their detection step has succeeded and
    reported no updates. A developer running the script by hand against a clean
    checkout will not close the bot's pull request.

.PARAMETER DryRun
    Print the actions and the GraphQL payload instead of calling the API. For
    local validation.
#>

[CmdletBinding()]
param(
    [string]$BodyFile,
    [string]$FilePattern = '(^|/)Dockerfile$',
    [string]$CommitHeadline = 'chore(deps): bump pinned apt package versions',
    [string]$CommitBodyIntro = @'
Newer versions of apt packages pinned in production Dockerfiles are available in
the Ubuntu archive and have been validated as installable against the pinned base
image. Raised for evaluation by the apt-pin-check workflow.
'@,
    [string]$Label = 'dependencies',
    [string]$BaseBranch = 'main',
    [string]$Branch = 'automation/apt-pin-updates',
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [switch]$CloseStalePr,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not $Repository) { throw 'Repository not set. Pass -Repository owner/repo or set GITHUB_REPOSITORY.' }

# Native `gh` failures do NOT raise a terminating error in PowerShell, so every
# call below checks $LASTEXITCODE rather than relying on try/catch.
function Invoke-Gh { param([string[]]$GhArgs) & gh @GhArgs }

function Test-BotRef {
    param([string]$Name)
    Invoke-Gh @('api', "repos/$Repository/git/refs/heads/$Name", '--silent') *> $null
    return ($LASTEXITCODE -eq 0)
}

# Create-or-move, so no caller has to care whether the ref already exists.
function Set-BotRef {
    param([string]$Name, [string]$Sha)

    if (Test-BotRef -Name $Name) {
        Invoke-Gh @('api', '-X', 'PATCH', "repos/$Repository/git/refs/heads/$Name", '-f', "sha=$Sha", '-F', 'force=true') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to move $Name to $Sha." }
    } else {
        Invoke-Gh @('api', '-X', 'POST', "repos/$Repository/git/refs", '-f', "ref=refs/heads/$Name", '-f', "sha=$Sha") | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to create $Name at $Sha." }
    }
}

function Remove-BotRef {
    param([string]$Name)
    Invoke-Gh @('api', '-X', 'DELETE', "repos/$Repository/git/refs/heads/$Name") *> $null
    if ($LASTEXITCODE -ne 0) { Write-Host "WARNING: could not delete $Name; a later run will reuse it." }
}

# Every pull request ever raised from the bot branch, newest first, as the raw
# JSON. A string rather than a parsed array, because PowerShell unrolls a
# function's array output: returning one would hand back $null for no pull
# requests and a bare object for one, so every caller would have to re-wrap it.
# Callers parse it themselves, where the shape is plain to read.
function Get-BotPrJson {
    $json = (Invoke-Gh @('pr', 'list', '--repo', $Repository, '--head', $Branch, '--state', 'all',
        '--limit', '10', '--json', 'number,state')) | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Failed to list pull requests for $Branch." }
    if (-not $json.Trim()) { return '[]' }
    return $json.Trim()
}

$ghBaseArgs = @('--repo', $Repository)

# --- Nothing to propose ------------------------------------------------------

# Files changed by the -Apply step. Scope strictly to the pin files via the
# caller-supplied pattern: the bump only ever rewrites pinned versions in those
# files, and this guards against committing any other stray working-tree
# artefact (e.g. the generated *-pr-body.md).
$changed = @(git diff --name-only | Where-Object { $_ -match $FilePattern })
if ($changed.Count -eq 0) {
    Write-Host 'No working-tree changes; nothing to propose.'

    if ($CloseStalePr -and -not $DryRun) {
        # A close here is a deliberate decision with its reason recorded, which
        # is the opposite of #1374's silent close as a side effect of a push.
        $staleComment = @'
Closing deliberately: the versions this pull request proposed are no longer behind, so there is nothing left to land. Either the bump has already landed, or the version it proposed is no longer offered upstream.

The pin check will raise a fresh pull request the next time a bump is available.
'@
        $openPrs = @(@(ConvertFrom-Json (Get-BotPrJson)) | Where-Object { $_.state -eq 'OPEN' })
        foreach ($pr in $openPrs) {
            Write-Host "Closing stale PR #$($pr.number): nothing left to propose."
            Invoke-Gh (@('pr', 'close', "$($pr.number)") + $ghBaseArgs + @('--comment', $staleComment)) | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Failed to close stale PR #$($pr.number)." }
        }
    }

    exit 0
}

Write-Host "Changed files:"; $changed | ForEach-Object { Write-Host "  $_" }

$prBody = if ($BodyFile -and (Test-Path $BodyFile)) { Get-Content -Path $BodyFile -Raw } else { '' }
$commitBody = @"
$CommitBodyIntro

$prBody
"@

# Build the GraphQL fileChanges additions (path + base64 contents).
$additions = foreach ($path in $changed) {
    @{
        path     = $path
        contents = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path (Get-Location) $path)))
    }
}

# Tip of the base branch: the parent the bump commit is built on, so the branch
# carries exactly one commit and its diff is the bump alone.
$baseSha = (Invoke-Gh @('api', "repos/$Repository/git/ref/heads/$BaseBranch", '--jq', '.object.sha'))
if ($LASTEXITCODE -ne 0) { throw "Could not resolve $BaseBranch sha." }
$baseSha = "$baseSha".Trim()
if (-not $baseSha) { throw "Could not resolve $BaseBranch sha." }
Write-Host "$BaseBranch is at $baseSha"

# The staging ref exists only to give createCommitOnBranch somewhere to build on
# that no pull request is watching, so resetting it to the base tip is harmless.
$stagingBranch = "$Branch-staging"

$mutation = @'
mutation($input: CreateCommitOnBranchInput!) {
  createCommitOnBranch(input: $input) { commit { oid url } }
}
'@

$commitInput = @{
    branch          = @{ repositoryNameWithOwner = $Repository; branchName = $stagingBranch }
    message         = @{ headline = $CommitHeadline; body = $commitBody }
    fileChanges     = @{ additions = @($additions) }
    expectedHeadOid = $baseSha
}
$payload = @{ query = $mutation; variables = @{ input = $commitInput } } | ConvertTo-Json -Depth 10 -Compress

if ($DryRun) {
    Write-Host ''
    Write-Host '== DRY RUN: would create a signed commit and move the bump branch onto it =='
    Write-Host "  staging: $stagingBranch (at $baseSha, deleted afterwards)"
    Write-Host "  branch:  $Branch (moved straight onto the new commit, never onto $baseSha)"
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

# --- Publish the bump --------------------------------------------------------

Set-BotRef -Name $stagingBranch -Sha $baseSha

# Create the signed commit via GraphQL. Parse the response explicitly so a
# GraphQL-level error (which still exits 0 in some cases) is caught.
$tmp = New-TemporaryFile
try {
    $payload | Out-File -FilePath $tmp -Encoding utf8
    $resp = Get-Content -Raw $tmp | & gh api graphql --input -
    if ($LASTEXITCODE -ne 0) { throw "createCommitOnBranch call failed (exit $LASTEXITCODE): $resp" }
    $commitOid = ("$resp" | ConvertFrom-Json).data.createCommitOnBranch.commit.oid
    if (-not $commitOid) { throw "createCommitOnBranch returned no commit oid: $resp" }
    Write-Host "Created signed commit $commitOid on $stagingBranch"
} finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}

# The whole point: one ref update, from the previous bump commit straight onto
# the new one. The branch is never equal to $BaseBranch, so the pull request
# never becomes empty and is never auto-closed (#1374).
Write-Host "Moving $Branch onto $commitOid"
Set-BotRef -Name $Branch -Sha $commitOid

# Delete the staging ref only after the bump branch holds the commit, so it is
# reachable from a ref throughout.
Remove-BotRef -Name $stagingBranch

# --- Reconcile the pull request ----------------------------------------------

$prs = @(ConvertFrom-Json (Get-BotPrJson))
$openPr = @($prs | Where-Object { $_.state -eq 'OPEN' })[0]
$closedPr = @($prs | Where-Object { $_.state -eq 'CLOSED' })[0]

if ($openPr) {
    Write-Host "Updated existing PR #$($openPr.number)"
    Invoke-Gh (@('pr', 'edit', "$($openPr.number)") + $ghBaseArgs + @('--body', $commitBody)) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to update the body of PR #$($openPr.number)." }
    $prNumber = $openPr.number
} elseif ($closedPr) {
    # The bump is still needed and the branch still exists, so a closed PR is a
    # PR that should not be closed. Reopening beats raising a second one: the
    # evaluation history stays in one place.
    Write-Host "WARNING: PR #$($closedPr.number) was found closed while its bump is still needed; reopening it."
    Invoke-Gh (@('pr', 'reopen', "$($closedPr.number)") + $ghBaseArgs) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to reopen PR #$($closedPr.number)." }
    Invoke-Gh (@('pr', 'edit', "$($closedPr.number)") + $ghBaseArgs + @('--body', $commitBody)) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to update the body of PR #$($closedPr.number)." }
    $prNumber = $closedPr.number
} else {
    Write-Host 'Opening new PR ...'
    Invoke-Gh (@('pr', 'create') + $ghBaseArgs + @('--base', $BaseBranch, '--head', $Branch,
        '--title', $CommitHeadline, '--body', $commitBody, '--label', $Label)) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to open a PR for $Branch." }
    $prNumber = @(@(ConvertFrom-Json (Get-BotPrJson)) | Where-Object { $_.state -eq 'OPEN' })[0].number
}

# Confirm the run ends with the PR open. GitHub's close is asynchronous, so this
# can miss one that lands after the check; the reconciliation above is what
# guarantees the next run recovers. This catches the rest.
$latest = @(ConvertFrom-Json (Get-BotPrJson))
$finalState = @($latest | Where-Object { $_.number -eq $prNumber })[0].state
if ($finalState -ne 'OPEN') {
    Write-Host "WARNING: PR #$prNumber is $finalState after the push; reopening it."
    Invoke-Gh (@('pr', 'reopen', "$prNumber") + $ghBaseArgs) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "PR #$prNumber was $finalState after the push and could not be reopened." }
}

Write-Host "PR #$prNumber is open with the current bump."
