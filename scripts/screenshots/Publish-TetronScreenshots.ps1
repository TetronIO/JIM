# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Opens the tetron.io pull request that refreshes its JIM screenshot with a release's capture (#582).

.DESCRIPTION
    Run by the jim-screenshots job in pre-release.yml beside Publish-TourScreenshots.ps1. Updates a checkout of
    tetron.io (TetronAssets.ps1): the homepage hero's assets/images/homepage-dark.png is overwritten with the
    release's capture, after checking the site still shows it. Then commits the result through GitHub's API onto the
    branch screenshots/<Version> and opens a pull request for it (TourPullRequest.ps1), or updates the one already open.

    Nothing merges by itself: tetron.io takes every change through a reviewed pull request, and its preview deployment
    shows the new image in place before anyone merges.

    The token comes from the GH_TOKEN environment variable, never a parameter, so it cannot reach a log. It needs
    Contents and Pull requests write access to the repository.

.PARAMETER ScreenshotPath
    The captured library for the version: Capture-Screenshots.ps1's <OutputPath>/<Version> folder.

.PARAMETER Version
    The release, as a folder name: v0.17.0.

.PARAMETER SitePath
    A checkout of tetron.io at the head of its base branch. It is changed in place.

.PARAMETER RunUrl
    The workflow run whose artefact holds the whole library, linked from the pull request.

.PARAMETER DryRun
    Updates the checkout and lists what would be committed, without touching GitHub.

.EXAMPLE
    $env:GH_TOKEN = '...'
    pwsh ./scripts/screenshots/Publish-TetronScreenshots.ps1 -ScreenshotPath artifacts/screenshots/v0.17.0 -Version v0.17.0 -SitePath ../tetron.io
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ScreenshotPath,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$SitePath,
    [string]$Repository = 'TetronIO/tetron.io',
    [string]$BaseBranch = 'main',
    [string]$RunUrl,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TetronAssets.ps1')
. (Join-Path $PSScriptRoot 'TourPullRequest.ps1')

$result = Update-TetronSiteImage -SitePath $SitePath -ScreenshotPath $ScreenshotPath
$changes = @(Get-SiteChange -SitePath $SitePath)
Write-Host "tetron.io for $Version`: $($changes.Count) file(s) changed."

if ($changes.Count -eq 0) {
    Write-Host 'The site already shows this screenshot; nothing to publish.'
    return
}
if ($DryRun) {
    $changes | ForEach-Object { Write-Host ('  {0} {1}' -f ($(if ($_.Deleted) { 'removed' } else { 'written' }), $_.Path)) }
    return
}
if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is not set; it needs Contents and Pull requests write access to ' + $Repository + '.' }

$branch = "screenshots/$Version"
$replaced = ($result.Published | ForEach-Object { "``$_``" }) -join ', '
$library = if ($RunUrl) { "The whole library the release captured is in [the run's artefact]($RunUrl)." } else { '' }

$body = @"
The JIM screenshot on tetron.io's homepage for JIM $Version, captured by JIM's release workflow from integration Scenario 8 with the portal in Production mode (TetronIO/JIM#582). It is the same image junctional.io's homepage shows.

- **Replaced:** $replaced, the homepage hero.

$library

Before merging:

- [ ] Look at the hero in this pull request's preview deployment.
- [ ] Merge once JIM $Version is published.
"@

$commit = Publish-SiteChangeCommit -Repository $Repository -BaseBranch $BaseBranch -Branch $branch -SitePath $SitePath `
    -Change $changes -Message "Refresh the JIM screenshot for JIM $Version" -Token $env:GH_TOKEN
$url = Open-TourPullRequest -Repository $Repository -BaseBranch $BaseBranch -Branch $branch `
    -Title "JIM screenshot for JIM $Version" -Body $body -Token $env:GH_TOKEN

Write-Host "Committed $commit to $branch; pull request: $url"
if ($env:GITHUB_STEP_SUMMARY) {
    "### tetron.io screenshot`n`n[$Repository pull request]($url) for $Version." |
        Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}
