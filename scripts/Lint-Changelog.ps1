# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Lints a CHANGELOG.md section against JIM's changelog quality rules.

.DESCRIPTION
    Validates the entries in a single changelog section (default: the
    [Unreleased] section) against the rules in engineering/CLAUDE.md:

      - HARD FAIL: every entry must lead with one of the canonical emojis
        (the only ones a customer-facing changelog should use). This is the
        mechanical backstop that catches non-customer-facing entries which
        reach for an off-list emoji such as a test tube or lipstick.
      - WARNING: entries that look internal (reference test scenarios,
        integration tests, EF Core internals, *Async method names, or
        "refactor") are flagged for a human/agent to confirm they belong in
        customer release notes at all.
      - WARNING: entries longer than the recommended length are flagged for
        tightening to one or two sentences.

    Warnings do not fail the run unless -WarningsAsErrors is set. The emoji
    whitelist always fails the run, because it has no false positives.

.PARAMETER Path
    Path to the changelog file. Defaults to CHANGELOG.md in the repo root.

.PARAMETER Section
    The section to lint: "Unreleased" (default) or a version like "0.11.0".

.PARAMETER WarningsAsErrors
    Treat warnings as failures (non-zero exit). Useful at release time.

.EXAMPLE
    ./scripts/Lint-Changelog.ps1
    Lints the [Unreleased] section (the PR-time check).

.EXAMPLE
    ./scripts/Lint-Changelog.ps1 -Section 0.11.0 -WarningsAsErrors
    Strictly lints a released version section.
#>
[CmdletBinding()]
param(
    [string]$Path = "CHANGELOG.md",
    [string]$Section = "Unreleased",
    [switch]$WarningsAsErrors
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Path)) {
    Write-Error "Changelog not found at '$Path'."
    exit 2
}

# The only emojis a customer-facing changelog entry may lead with
# (see engineering/CLAUDE.md). Variation selector U+FE0F is allowed but optional.
$canonicalEmoji = "^(?:✨|🐛|⚡|🔄|🗑|🔒|📦|🖥)️?\s"

# Heuristics that suggest an entry is internal rather than customer-facing.
$internalPatterns = @(
    @{ Label = "references a test scenario"; Pattern = "\bScenario\s+\d" },
    @{ Label = "references integration/unit tests"; Pattern = "\b(integration|unit)\s+test" },
    @{ Label = "names an internal *Async method"; Pattern = "\b\w+Async\b" },
    @{ Label = "describes a refactor"; Pattern = "\brefactor" },
    @{ Label = "exposes EF Core / persistence internals"; Pattern = "\b(EF Core|change tracker|NoTracking|SaveChanges|DbContext)\b" }
)

$maxEntryLength = 280  # characters; longer than this reads as "too verbose" for a changelog

$lines = Get-Content -Path $Path
$header = if ($Section -eq 'Unreleased') { '## [Unreleased]' } else { "## [$Section]" }

# Locate the section and collect its top-level entries (lines starting "- ").
$inSection = $false
$entries = [System.Collections.Generic.List[object]]::new()
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if (-not $inSection) {
        if ($line.StartsWith($header)) { $inSection = $true }
        continue
    }
    if ($line -match '^##\s') { break }   # next version section
    if ($line -match '^- ') {
        $entries.Add([pscustomobject]@{ Number = $i + 1; Text = $line.Substring(2).Trim() })
    }
}

if (-not $inSection) {
    Write-Error "Section '$header' not found in '$Path'."
    exit 2
}

$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

foreach ($entry in $entries) {
    $loc = "$Path`:$($entry.Number)"

    if ($entry.Text -notmatch $canonicalEmoji) {
        $errors.Add("$loc  entry does not lead with a canonical emoji (use one of: new feature, fix, performance, changed, removed, security, deployment, UI/UX): `"$($entry.Text)`"")
    }

    if ($entry.Text.Length -gt $maxEntryLength) {
        $warnings.Add("$loc  entry is $($entry.Text.Length) chars; tighten to one or two sentences (<= $maxEntryLength).")
    }

    foreach ($h in $internalPatterns) {
        if ($entry.Text -match $h.Pattern) {
            $warnings.Add("$loc  entry $($h.Label); confirm it is customer-facing or remove it.")
        }
    }
}

Write-Host "Linted $($entries.Count) entr$(if ($entries.Count -eq 1) {'y'} else {'ies'}) in $header of $Path."

foreach ($w in $warnings) { Write-Host "WARNING: $w" -ForegroundColor Yellow }
foreach ($e in $errors)   { Write-Host "ERROR:   $e" -ForegroundColor Red }

$failed = $errors.Count -gt 0 -or ($WarningsAsErrors -and $warnings.Count -gt 0)
if ($failed) {
    Write-Host "`nChangelog lint FAILED ($($errors.Count) error(s), $($warnings.Count) warning(s))." -ForegroundColor Red
    exit 1
}

Write-Host "`nChangelog lint passed ($($warnings.Count) warning(s))." -ForegroundColor Green
exit 0
