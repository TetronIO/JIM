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

    Engineering reference documentation (engineering/) is deliberately NOT
    enforced here: "did this change make a design doc stale?" is a judgement
    call that cannot be derived from a changelog emoji. The PR template prompts
    for it instead.

    Inputs are taken from environment variables so the workflow does not have to
    quote multi-line values on the command line:
      CHANGED_FILES  newline-separated list of files changed in the PR
      PR_BODY        the PR description (for the opt-out line)
      PR_LABELS      comma-separated PR label names (for the opt-out label)

    Escape hatches (either satisfies the check when no docs/ file changed):
      - a PR label named 'docs-not-needed'
      - a reasoned opt-out line in the PR body:  Docs: n/a - <reason>
        ('-', ':' or an em dash separate the marker from the reason; a reason is
        required, so the opt-out is a deliberate, reviewable statement).

.PARAMETER Path
    Path to the changelog file. Defaults to CHANGELOG.md in the repo root.

.EXAMPLE
    CHANGED_FILES="docs/x.md`nsrc/y.cs" pwsh -File ./scripts/Lint-DocsCoupling.ps1
#>
[CmdletBinding()]
param(
    [string]$Path = "CHANGELOG.md",
    [string]$ChangedFiles = $env:CHANGED_FILES,
    [string]$PrBody = $env:PR_BODY,
    [string]$PrLabels = $env:PR_LABELS
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Path)) {
    Write-Error "Changelog not found at '$Path'."
    exit 2
}

# User-facing tells: ✨ new feature or 🔄 changed behaviour. Variation selector
# U+FE0F is allowed but optional, mirroring Lint-Changelog.ps1.
$userFacingEmoji = "^(?:✨|🔄)️?\s"

# Parse the [Unreleased] section's top-level entries (mirrors Lint-Changelog.ps1).
$lines = Get-Content -Path $Path
$inSection = $false
$userFacing = [System.Collections.Generic.List[string]]::new()
foreach ($line in $lines) {
    if (-not $inSection) {
        if ($line.StartsWith('## [Unreleased]')) { $inSection = $true }
        continue
    }
    if ($line -match '^##\s') { break }   # next version section
    if ($line -match '^- ') {
        $text = $line.Substring(2).Trim()
        if ($text -match $userFacingEmoji) { $userFacing.Add($text) }
    }
}

$plural = if ($userFacing.Count -eq 1) { 'y' } else { 'ies' }

if ($userFacing.Count -eq 0) {
    Write-Host "No user-facing (✨/🔄) changelog entries in [Unreleased]; public docs coupling not required." -ForegroundColor Green
    exit 0
}

# Did the PR touch public documentation?
$changed = @($ChangedFiles -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$docsChanged = @($changed | Where-Object { $_ -like 'docs/*' })

if ($docsChanged.Count -gt 0) {
    Write-Host "Public docs updated alongside $($userFacing.Count) user-facing changelog entr$($plural):" -ForegroundColor Green
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
$userFacing | ForEach-Object { Write-Host "  - $_" }
Write-Host ""
Write-Host "Do one of:" -ForegroundColor Red
Write-Host "  1. Update the relevant page(s) under docs/ in this PR (the expected fix)."
Write-Host "  2. If docs genuinely are not needed, add a line to the PR description:"
Write-Host "       Docs: n/a - <reason>"
Write-Host "  3. Or have a maintainer apply the 'docs-not-needed' label."
exit 1
