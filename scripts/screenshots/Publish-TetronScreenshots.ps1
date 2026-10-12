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

    No one reviews it: the capture has already refused a blank image, the pull request's own check builds the site
    with it, and the pull request merges itself once JIM's release is published and that check has passed. JIM's
    release workflow merges it then; with -MergeWhenReleased this script merges it straight away if the release is
    already out.

    The tokens come from environment variables, never parameters, so they cannot reach a log: GH_TOKEN needs Contents
    and Pull requests write access to the repository, and JIM_RELEASE_TOKEN (for -MergeWhenReleased) reads JIM's
    releases.

.PARAMETER ScreenshotPath
    The captured library for the version: Capture-Screenshots.ps1's <OutputPath>/<Version> folder.

.PARAMETER Version
    The release, as a folder name: v0.17.0.

.PARAMETER SitePath
    A checkout of tetron.io at the head of its base branch. It is changed in place.

.PARAMETER RunUrl
    The workflow run whose artefact holds the whole library, linked from the pull request.

.PARAMETER MergeWhenReleased
    Once the pull request is open, merges it if JIM's release for the version is already published.

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
    [switch]$MergeWhenReleased,
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
if ($MergeWhenReleased -and -not $env:JIM_RELEASE_TOKEN) { throw 'JIM_RELEASE_TOKEN is not set; -MergeWhenReleased reads JIM''s releases with it.' }

$branch = "screenshots/$Version"
$replaced = ($result.Published | ForEach-Object { "``$_``" }) -join ', '
$library = if ($RunUrl) { "The whole library the release captured is in [the run's artefact]($RunUrl)." } else { '' }

$body = @"
The JIM screenshot on tetron.io's homepage for JIM $Version, captured by JIM's release workflow from integration Scenario 8 with the portal in Production mode (TetronIO/JIM#582). It is the same image junctional.io's homepage shows.

- **Replaced:** $replaced, the homepage hero.

$library

This pull request merges itself once JIM $Version is published and its checks pass (the preview deployment builds the site with the new image); close it to keep the image off the site.
"@

$commit = Publish-SiteChangeCommit -Repository $Repository -BaseBranch $BaseBranch -Branch $branch -SitePath $SitePath `
    -Change $changes -Message "Refresh the JIM screenshot for JIM $Version" -Token $env:GH_TOKEN
$url = Open-TourPullRequest -Repository $Repository -BaseBranch $BaseBranch -Branch $branch `
    -Title "JIM screenshot for JIM $Version" -Body $body -Token $env:GH_TOKEN

Write-Host "Committed $commit to $branch; pull request: $url"
$outcome = 'It merges when the release is published.'
if ($MergeWhenReleased) {
    $merged = Invoke-ScreenshotMergeIfReleased -Repository $Repository -Branch $branch -Version $Version `
        -Token $env:GH_TOKEN -ReleaseToken $env:JIM_RELEASE_TOKEN
    if ($merged) { $outcome = 'The release was already published, so it is merged.' }
}
if ($env:GITHUB_STEP_SUMMARY) {
    "### tetron.io screenshot`n`n[$Repository pull request]($url) for $Version. $outcome" |
        Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}
