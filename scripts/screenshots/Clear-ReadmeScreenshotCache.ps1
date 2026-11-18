# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Makes JIM's README show a release's homepage screenshots: purges GitHub's cached copies once junctional.io serves
    them (#582).

.DESCRIPTION
    The README reads its homepage screenshots from fixed junctional.io addresses, through GitHub's image proxy, which
    keeps its own copy. Run once junctional.io's screenshot pull request for the release has merged: by release.yml's
    publish-screenshots job, and by pre-release.yml's jim-screenshots job when that job merges the pull request itself.

    When the pull request has not merged yet, nothing is purged and the script says so: whichever job merges it runs
    this again. An image the site never starts serving, or that the proxy still serves stale after the purge, is
    reported as an error and the script exits 1, so the job fails.

    GH_TOKEN (read access to the site's repository) and README_TOKEN (read access to JIM's) come from the environment,
    never parameters, so they cannot reach a log.

.PARAMETER Version
    The release, as its tag: v0.18.0.

.EXAMPLE
    $env:GH_TOKEN = '...'; $env:README_TOKEN = '...'
    pwsh ./scripts/screenshots/Clear-ReadmeScreenshotCache.ps1 -Version v0.18.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$SiteRepository = 'TetronIO/junctional.io',
    [string]$ReadmeRepository = 'TetronIO/JIM',
    [int]$TimeoutSeconds = 1800
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ReadmeImageCache.ps1')

if (-not $env:GH_TOKEN) { throw "GH_TOKEN is not set; it needs read access to $SiteRepository." }

function ConvertTo-AnnotationText([string]$Text) { $Text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A') }
function Write-Summary([string]$Text) {
    if ($env:GITHUB_STEP_SUMMARY) {
        "### README screenshots for $Version`n`n$Text" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
    }
}

$owner = $SiteRepository.Split('/')[0]
$closed = Invoke-GitHubApi -Method GET -Path "repos/$SiteRepository/pulls?head=${owner}:screenshots/$Version&state=closed" -Token $env:GH_TOKEN
if (@(@($closed) | Where-Object { $_.merged_at }).Count -eq 0) {
    $text = "$SiteRepository's screenshots pull request for $Version has not merged, so the README's images were left alone. The job that merges it clears them."
    Write-Host "::notice title=README screenshots not refreshed::$(ConvertTo-AnnotationText $text)"
    Write-Summary $text
    exit 0
}

$html = Get-RenderedReadme -Repository $ReadmeRepository -Token $env:README_TOKEN
$results = @(Clear-ReadmeImageCache -Html $html -SiteRepository $SiteRepository -Token $env:GH_TOKEN -TimeoutSeconds $TimeoutSeconds)
$lines = foreach ($result in $results) {
    if ($result.Status -eq 'Cleared') {
        Write-Host "$($result.Origin): GitHub's copy refreshed."
        "- ``$($result.Origin)``: refreshed."
    }
    else {
        Write-Host "::error title=README screenshot not refreshed::$(ConvertTo-AnnotationText $result.Message)"
        "- ``$($result.Origin)``: **not refreshed.** $($result.Message)"
    }
}
Write-Summary ($lines -join "`n")
if (@($results | Where-Object Status -ne 'Cleared').Count -gt 0) { exit 1 }
