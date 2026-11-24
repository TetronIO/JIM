# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Lists English strings that are translated more than one way across JIM's resource files.

.DESCRIPTION
    Portal text and message catalogues are written area by area, often by different people or agents, so the same
    English ("Run Profile", "Save changes", "Operations") can end up translated two ways in two files. This reads every
    src/*/Resources/*.resx file, pairs each English string with its translation, and lists every English string that
    has more than one distinct translation, with the keys that carry each, so a reviewer can settle on one wording.

    It is a review aid, not a check: the same English can rightly read two ways in two places (a heading and a button,
    or a word whose gender follows its noun), so nothing here fails a build.

.PARAMETER Language
    The culture suffix of the translations to compare. Defaults to fr.

.PARAMETER MinimumLength
    Ignore English strings shorter than this many characters. Defaults to 1.

.EXAMPLE
    pwsh ./scripts/Find-InconsistentTranslations.ps1 -Language fr
#>
[CmdletBinding()]
param(
    [string]$Language = 'fr',
    [int]$MinimumLength = 1
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '..' 'src'

function Read-Resx([string]$path) {
    $values = @{}
    [xml]$document = Get-Content -Path $path -Raw -Encoding utf8
    foreach ($data in $document.root.data) {
        $values[$data.name] = [string]$data.value
    }
    return $values
}

$byEnglish = @{}
foreach ($neutral in Get-ChildItem -Path $source -Recurse -Filter '*.resx' | Where-Object { $_.Directory.Name -eq 'Resources' -and $_.BaseName -notmatch '\.' }) {
    $translationPath = Join-Path $neutral.DirectoryName "$($neutral.BaseName).$Language.resx"
    if (-not (Test-Path $translationPath)) { continue }

    $english = Read-Resx $neutral.FullName
    $translated = Read-Resx $translationPath
    foreach ($key in $english.Keys) {
        $text = $english[$key]
        if ($text.Length -lt $MinimumLength -or -not $translated.ContainsKey($key)) { continue }

        if (-not $byEnglish.ContainsKey($text)) { $byEnglish[$text] = @{} }
        $translation = $translated[$key]
        if (-not $byEnglish[$text].ContainsKey($translation)) { $byEnglish[$text][$translation] = [System.Collections.Generic.List[string]]::new() }
        $byEnglish[$text][$translation].Add("$($neutral.BaseName).$key")
    }
}

$inconsistent = $byEnglish.GetEnumerator() | Where-Object { $_.Value.Count -gt 1 } | Sort-Object Name
foreach ($entry in $inconsistent) {
    Write-Output "`"$($entry.Name)`""
    foreach ($translation in $entry.Value.GetEnumerator() | Sort-Object Name) {
        Write-Output "    `"$($translation.Name)`"  <-  $($translation.Value -join ', ')"
    }
}

Write-Host "$(@($inconsistent).Count) English strings have more than one $Language translation." -ForegroundColor Cyan
