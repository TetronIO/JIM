# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Fails a PR that ships a user-facing change without updating public docs.

.DESCRIPTION
    The mechanical backstop for the "Keeping Documentation Current" rule in
    engineering/CLAUDE.md. A PR that adds a user-facing entry to the CHANGELOG
    [Unreleased] section (a ✨ new feature or 🔄 changed behaviour) must also
    update the public documentation under docs/, so customer-facing docs never
    drift behind the product again.

    Only ✨ and 🔄 trigger the requirement; fixes, performance, security, and
    the rest do not (a bug fix rarely changes documented behaviour), though they
    may of course still update docs.

    The requirement applies ONLY to entries THIS PR adds. The [Unreleased]
    section accumulates entries until the next release, so a feature PR's ✨
    entry sits there for every subsequent PR until the version is cut. Without a
    base comparison, an unrelated PR (a Dependabot bump, a CI tweak) inherits
    that entry and is forced to update docs for a change it had nothing to do
    with. We therefore diff the [Unreleased] user-facing entries against the
    base branch's version and require docs only for the newly added ones. The
    PR that actually introduced the entry already satisfied (or opted out of)
    the check when it landed.

    Engineering reference documentation (engineering/) is deliberately NOT
    enforced here: "did this change make a design doc stale?" is a judgement
    call that cannot be derived from a changelog emoji. The PR template prompts
    for it instead.

    Inputs are taken from environment variables so the workflow does not have to
    quote multi-line values on the command line:
      CHANGED_FILES  newline-separated list of files changed in the PR
      PR_BODY        the PR description (for the opt-out line)
      PR_LABELS      comma-separated PR label names (for the opt-out label)
      BASE_SHA       the PR's base commit, used to read the base CHANGELOG

    Escape hatches (either satisfies the check when no docs/ file changed):
      - a PR label named 'docs-not-needed'
      - a reasoned opt-out line in the PR body:  Docs: n/a - <reason>
        ('-', ':' or an em dash separate the marker from the reason; a reason is
        required, so the opt-out is a deliberate, reviewable statement).

.PARAMETER Path
    Path to the changelog file (the PR's head version). Defaults to CHANGELOG.md
    in the repo root.

.PARAMETER BaseRef
    Git ref (commit SHA) of the PR's base. The base CHANGELOG is read via
    'git show <BaseRef>:CHANGELOG.md' to determine which [Unreleased] entries
    already existed. Defaults to the BASE_SHA environment variable. When neither
    BaseRef nor BasePath resolves to base content, every head entry is treated
    as newly added (the conservative fallback for local, ref-less runs).

.PARAMETER BasePath
    Explicit path to the base version of the changelog. Takes precedence over
    BaseRef and needs no git repository; primarily for tests.

.EXAMPLE
    CHANGED_FILES="docs/x.md`nsrc/y.cs" pwsh -File ./scripts/Lint-DocsCoupling.ps1
#>
[CmdletBinding()]
param(
    [string]$Path = "CHANGELOG.md",
    [string]$ChangedFiles = $env:CHANGED_FILES,
    [string]$PrBody = $env:PR_BODY,
    [string]$PrLabels = $env:PR_LABELS,
    [string]$BaseRef = $env:BASE_SHA,
    [string]$BasePath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Path)) {
    Write-Error "Changelog not found at '$Path'."
    exit 2
}

# User-facing tells: ✨ new feature or 🔄 changed behaviour. Variation selector
# U+FE0F is allowed but optional, mirroring Lint-Changelog.ps1.
$userFacingEmoji = "^(?:✨|🔄)️?\s"

# Collect the [Unreleased] section's top-level user-facing entries from a set of
# changelog lines (mirrors Lint-Changelog.ps1's section walk).
function Get-UserFacingUnreleasedEntries {
    param([string[]]$Lines)

    $entries = [System.Collections.Generic.List[string]]::new()
    $inSection = $false
    foreach ($line in $Lines) {
        if (-not $inSection) {
            if ($line.StartsWith('## [Unreleased]')) { $inSection = $true }
            continue
        }
        if ($line -match '^##\s') { break }   # next version section
        if ($line -match '^- ') {
            $text = $line.Substring(2).Trim()
            if ($text -match $userFacingEmoji) { $entries.Add($text) }
        }
    }
    return $entries
}

# Read the base version of the changelog so pre-existing entries can be excluded.
# BasePath wins (test/local use); otherwise read BaseRef out of git; otherwise
# treat the base as empty so every head entry counts as new.
$baseLines = @()
if ($BasePath) {
    if (Test-Path $BasePath) {
        $baseLines = Get-Content -Path $BasePath
    } else {
        Write-Host "Base changelog '$BasePath' not found; treating all [Unreleased] entries as newly added." -ForegroundColor Yellow
    }
} elseif (-not [string]::IsNullOrWhiteSpace($BaseRef)) {
    # git show returns non-zero (and, under PowerShell 7.4 native error handling,
    # would throw) when the ref or path is absent; soften that to the empty-base
    # fallback rather than failing the lint on an infrastructure hiccup.
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $shown = & git show "$($BaseRef):CHANGELOG.md" 2>$null
        if ($LASTEXITCODE -eq 0 -and $shown) {
            $baseLines = $shown
        } else {
            Write-Host "Could not read CHANGELOG.md at base '$BaseRef'; treating all [Unreleased] entries as newly added." -ForegroundColor Yellow
        }
    } catch {
        # e.g. git not on PATH; degrade to the empty base (stricter, never laxer).
        Write-Host "Could not invoke git to read base '$BaseRef' ($($_.Exception.Message)); treating all [Unreleased] entries as newly added." -ForegroundColor Yellow
    } finally {
        $ErrorActionPreference = $previousEap
    }
}

$headEntries = Get-UserFacingUnreleasedEntries -Lines (Get-Content -Path $Path)
$baseEntries = Get-UserFacingUnreleasedEntries -Lines $baseLines

# Explicit adds rather than the IEnumerable constructor: HashSet<string> also
# has a single-arg IEqualityComparer constructor, so seeding from an empty
# collection trips an ambiguous-overload error.
$baseSet = [System.Collections.Generic.HashSet[string]]::new()
foreach ($e in $baseEntries) { [void]$baseSet.Add($e) }

# Only entries present in head but not in base are this PR's responsibility.
$newlyAdded = [System.Collections.Generic.List[string]]::new()
foreach ($e in $headEntries) {
    if (-not $baseSet.Contains($e)) { $newlyAdded.Add($e) }
}

$plural = if ($newlyAdded.Count -eq 1) { 'y' } else { 'ies' }

if ($newlyAdded.Count -eq 0) {
    Write-Host "No newly added user-facing (✨/🔄) [Unreleased] entries in this PR; public docs coupling not required." -ForegroundColor Green
    exit 0
}

# Did the PR touch public documentation?
$changed = @($ChangedFiles -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$docsChanged = @($changed | Where-Object { $_ -like 'docs/*' })

if ($docsChanged.Count -gt 0) {
    Write-Host "Public docs updated alongside $($newlyAdded.Count) newly added user-facing changelog entr$($plural):" -ForegroundColor Green
    $docsChanged | ForEach-Object { Write-Host "  docs: $_" }
    exit 0
}

# Escape hatch 1: a maintainer label.
$labels = @($PrLabels -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($labels -contains 'docs-not-needed') {
    Write-Host "No public docs change, but the 'docs-not-needed' label is set; allowing." -ForegroundColor Yellow
    exit 0
}

# Escape hatch 2: a reasoned opt-out line in the PR body.
#   Docs: n/a - <reason>   (separator may be a hyphen, a colon, or an em dash;
#   an em dash (U+2014) is matched via the \u2014 escape, keeping a literal one out of our source).
$optOut = [regex]::Match($PrBody, '(?im)^\s*Docs:\s*(?:n/?a|none|not\s+needed)\b\s*[-:\u2014]\s*(\S.*)$')
if ($optOut.Success) {
    Write-Host "No public docs change, but the PR body opts out with a reason: $($optOut.Groups[1].Value.Trim())" -ForegroundColor Yellow
    exit 0
}

# Otherwise: fail.
Write-Host "Docs coupling FAILED." -ForegroundColor Red
Write-Host ""
Write-Host "This PR adds user-facing changelog entr$plural but changes no public documentation under docs/:" -ForegroundColor Red
$newlyAdded | ForEach-Object { Write-Host "  - $_" }
Write-Host ""
Write-Host "Do one of:" -ForegroundColor Red
Write-Host "  1. Update the relevant page(s) under docs/ in this PR (the expected fix)."
Write-Host "  2. If docs genuinely are not needed, add a line to the PR description:"
Write-Host "       Docs: n/a - <reason>"
Write-Host "  3. Or have a maintainer apply the 'docs-not-needed' label."
exit 1
