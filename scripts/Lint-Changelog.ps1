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
      - HARD FAIL: two entries in the same subsection with identical text.
      - HARD FAIL: two entries in the same subsection that are near-identical:
        three quarters or more of their words in common. CHANGELOG.md carries
        a `merge=union` driver, so an entry reworded on one branch while
        another still carries the original is re-added alongside the version
        that replaced it. Such a pair differs in a handful of words out of
        dozens; two genuinely different changes never read that alike. The
        closest genuine pair in the file's history scores 0.56 and the
        re-adds 0.63 to 1.0; the threshold sits in that gap nearer the
        re-adds, because a genuine pair that failed could only be silenced by
        rewording, whereas a heavier reword that slips under it still warns
        on its opening clause.
      - WARNING: two entries in the same subsection that open with the same
        eight words but then diverge. A weaker signal of the same re-add,
        which also fires on two genuinely different fixes to the same
        component; that is why it warns rather than fails.
      - HARD FAIL: an [Unreleased] entry identical to, or near-identical to,
        one in a released section. This is the other half of what
        `merge=union` costs: union merging re-adds lines rather than
        reconciling them, so a branch that merges or rebases across a release
        brings the entries it contributed back into [Unreleased], where they
        read as unshipped work and would ship a second time in the next
        release notes. An entry that has already shipped is never a judgement
        call, and neither is one that has been reworded since it shipped.
      - WARNING: an [Unreleased] entry opening with the same eight words as a
        released one. Usually the same re-add after a heavier reword;
        occasionally a genuine follow-up that revisits shipped work, which is
        why it warns.

    Warnings do not fail the run unless -WarningsAsErrors is set. The emoji
    whitelist, the identical-entry checks and the near-identical checks always
    fail the run, because none of them has a false positive in the file's
    history.

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
$duplicatePrefixWords = 8  # opening words compared when looking for a re-added entry
# Share of words two entries must have in common (Sørensen-Dice on their word
# bags) to count as one entry written twice. Measured against the file's
# history: every genuine pair scored 0.56 or less, every union-merge re-add
# 0.63 or more, and the resurrections that reached main scored 0.97 and 1.0.
# Set nearer the re-adds than the genuine pairs: a genuine pair failing here
# could only be silenced by rewording, while a re-add below the threshold is
# still caught as a warning by its opening clause.
$nearIdenticalThreshold = 0.75

$lines = Get-Content -Path $Path
$header = if ($Section -eq 'Unreleased') { '## [Unreleased]' } else { "## [$Section]" }

# Walk the whole file once, collecting every top-level entry (lines starting
# "- ") with the section and subsection it sits under. The whole file rather
# than just the target section because an entry that has already shipped is
# only visible by looking at the released sections too.
# NOTE: deliberately NOT named $section. PowerShell variable names are
# case-insensitive, so that would silently overwrite the $Section parameter
# and leave the script linting whichever version section came last in the file.
$currentSection = $null
$subsection = '(none)'
$allEntries = [System.Collections.Generic.List[object]]::new()
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match '^##\s+\[([^\]]+)\]') {
        $currentSection = $Matches[1].Trim()
        $subsection = '(none)'
        continue
    }
    if ($null -eq $currentSection) { continue }
    if ($line -match '^###\s+(.+)$') { $subsection = $Matches[1].Trim(); continue }
    if ($line -match '^- ') {
        $allEntries.Add([pscustomobject]@{
            Number     = $i + 1
            Text       = $line.Substring(2).Trim()
            Section    = $currentSection
            Subsection = $subsection
        })
    }
}

$targetSection = if ($Section -eq 'Unreleased') { 'Unreleased' } else { $Section }
if (-not ($lines | Where-Object { $_.StartsWith($header) })) {
    Write-Error "Section '$header' not found in '$Path'."
    exit 2
}

$entries = @($allEntries | Where-Object { $_.Section -eq $targetSection })
$otherSectionEntries = @($allEntries | Where-Object { $_.Section -ne $targetSection })

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

# Duplicate detection. Compare entries within a subsection on their normalised
# text: identical entries are always a mistake; a shared opening clause is the
# signature of the merge=union re-add described above.
function Get-NormalisedEntryText {
    param([string]$Text)
    # Strip the leading emoji, lower-case, and reduce to words so punctuation,
    # backticks and issue references do not mask an otherwise identical opener.
    $stripped = $Text -replace '^\W+', ''
    return (($stripped.ToLowerInvariant() -replace '[^a-z0-9 ]', ' ') -split '\s+' | Where-Object { $_ })
}

# Everything the duplicate checks need from an entry, computed once: its
# normalised words, the exact and opening-clause keys, and a word bag (word to
# count) for the similarity comparison.
function Get-EntryShape {
    param([pscustomobject]$Entry)
    $words = @(Get-NormalisedEntryText -Text $Entry.Text)
    if ($words.Count -eq 0) { return $null }
    $bag = @{}
    foreach ($w in $words) { $bag[$w] = 1 + $bag[$w] }
    return [pscustomobject]@{
        Entry  = $Entry
        Count  = $words.Count
        Exact  = ($words -join ' ')
        Prefix = if ($words.Count -ge $duplicatePrefixWords) { ($words | Select-Object -First $duplicatePrefixWords) -join ' ' } else { $null }
        Bag    = $bag
    }
}

# Sørensen-Dice similarity of two word bags: twice the words in common over
# the words in both. Order-insensitive, so a reword that moves a clause still
# scores as the same entry; count-sensitive, so repeated words are not
# over-credited.
function Get-WordBagSimilarity {
    param([pscustomobject]$A, [pscustomobject]$B)
    # An upper bound from the lengths alone: if even a perfect overlap of the
    # shorter entry could not reach the threshold, skip the intersection.
    $shorter = [Math]::Min($A.Count, $B.Count)
    if ((2.0 * $shorter) / ($A.Count + $B.Count) -lt $nearIdenticalThreshold) { return 0.0 }

    $small, $large = if ($A.Bag.Count -le $B.Bag.Count) { $A.Bag, $B.Bag } else { $B.Bag, $A.Bag }
    $common = 0
    foreach ($word in $small.Keys) {
        if ($large.ContainsKey($word)) { $common += [Math]::Min($small[$word], $large[$word]) }
    }
    return (2.0 * $common) / ($A.Count + $B.Count)
}

function Format-Similarity {
    param([double]$Similarity)
    return "$([Math]::Round($Similarity * 100))% of their words in common"
}

foreach ($group in $entries | Group-Object Subsection) {
    $byExact  = @{}
    $byPrefix = @{}
    $seen     = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $group.Group) {
        $shape = Get-EntryShape -Entry $entry
        if ($null -eq $shape) { continue }

        if ($byExact.ContainsKey($shape.Exact)) {
            $errors.Add("$Path`:$($entry.Number)  entry is identical to the one at line $($byExact[$shape.Exact]) under '$($group.Name)'; remove one: `"$($entry.Text)`"")
            continue
        }
        $byExact[$shape.Exact] = $entry.Number

        # Near-identical: the re-add after a reword. Checked before the opening
        # clause so the pair is reported once, as the failure it is.
        $twin = $null
        foreach ($earlier in $seen) {
            $similarity = Get-WordBagSimilarity -A $shape -B $earlier
            if ($similarity -ge $nearIdenticalThreshold) { $twin = @{ Shape = $earlier; Similarity = $similarity }; break }
        }
        $seen.Add($shape)
        if ($null -ne $twin) {
            $errors.Add("$Path`:$($entry.Number)  entry is near-identical to the one at line $($twin.Shape.Entry.Number) under '$($group.Name)' ($(Format-Similarity $twin.Similarity)); they are one change written twice, so keep the version you want and delete the other (CHANGELOG.md merges by union, so a replaced entry can come back): `"$($entry.Text)`"")
            continue
        }

        if ($null -ne $shape.Prefix) {
            if ($byPrefix.ContainsKey($shape.Prefix)) {
                $warnings.Add("$Path`:$($entry.Number)  entry opens with the same $duplicatePrefixWords words as the one at line $($byPrefix[$shape.Prefix]) under '$($group.Name)'; if they describe the same change, keep the version you want and delete the other (CHANGELOG.md merges by union, so a replaced entry can come back).")
            }
            else { $byPrefix[$shape.Prefix] = $entry.Number }
        }
    }
}

# Entries that have already shipped. Union merging re-adds lines rather than
# reconciling them, so a branch that merges or rebases across a release brings
# the entries it contributed back into the section it is being linted against.
# Compared across the whole file rather than within a subsection, because the
# re-add lands wherever the merge put it, which need not be where it shipped.
$shippedByExact  = @{}
$shippedByPrefix = @{}
$shippedShapes   = [System.Collections.Generic.List[object]]::new()
foreach ($other in $otherSectionEntries) {
    $shape = Get-EntryShape -Entry $other
    if ($null -eq $shape) { continue }
    $shippedShapes.Add($shape)

    if (-not $shippedByExact.ContainsKey($shape.Exact)) { $shippedByExact[$shape.Exact] = $other }
    if ($null -ne $shape.Prefix -and -not $shippedByPrefix.ContainsKey($shape.Prefix)) { $shippedByPrefix[$shape.Prefix] = $other }
}

foreach ($entry in $entries) {
    $shape = Get-EntryShape -Entry $entry
    if ($null -eq $shape) { continue }

    if ($shippedByExact.ContainsKey($shape.Exact)) {
        $shipped = $shippedByExact[$shape.Exact]
        $errors.Add("$Path`:$($entry.Number)  entry has already shipped in [$($shipped.Section)] (line $($shipped.Number)); delete it from $header rather than releasing it twice (CHANGELOG.md merges by union, so merging or rebasing across a release re-adds the entries the branch contributed): `"$($entry.Text)`"")
        continue
    }

    $twin = $null
    foreach ($shippedShape in $shippedShapes) {
        $similarity = Get-WordBagSimilarity -A $shape -B $shippedShape
        if ($similarity -ge $nearIdenticalThreshold) { $twin = @{ Shape = $shippedShape; Similarity = $similarity }; break }
    }
    if ($null -ne $twin) {
        $shipped = $twin.Shape.Entry
        $errors.Add("$Path`:$($entry.Number)  entry is near-identical to one that already shipped in [$($shipped.Section)] (line $($shipped.Number), $(Format-Similarity $twin.Similarity)); delete it from $header rather than releasing it twice (CHANGELOG.md merges by union, so merging or rebasing across a release re-adds the entries the branch contributed): `"$($entry.Text)`"")
        continue
    }

    if ($null -ne $shape.Prefix -and $shippedByPrefix.ContainsKey($shape.Prefix)) {
        $shipped = $shippedByPrefix[$shape.Prefix]
        $warnings.Add("$Path`:$($entry.Number)  entry opens with the same $duplicatePrefixWords words as one that already shipped in [$($shipped.Section)] (line $($shipped.Number)); delete it unless it genuinely describes further work on the same thing.")
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
