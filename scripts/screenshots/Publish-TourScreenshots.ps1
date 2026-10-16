# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Opens the junctional.io pull request that refreshes the product tour with a release's screenshots (#582).

.DESCRIPTION
    Run by the jim-screenshots job in pre-release.yml once Capture-Screenshots.ps1 has written the library. Updates a
    checkout of junctional.io (TourAssets.ps1): the images the tour shows go into src/assets/tour/<Version>/, both
    dictionaries' imageVersion move to it, the homepage images are copied to their fixed address, and folders no step
    uses are removed. Then commits the result through GitHub's API onto the branch screenshots/<Version> and opens a
    pull request for it (TourPullRequest.ps1), or updates the one already open.

    No one reviews it: the capture has already refused blank images and pairs in the wrong theme, -Build refuses
    images the site will not build with, and the pull request merges itself once JIM's release is published. JIM's
    release workflow merges it then; with -MergeWhenReleased this script merges it straight away if the release is
    already out, which happens when the release was tagged while the screenshots were still being captured.

    The tokens come from environment variables, never parameters, so they cannot reach a log: GH_TOKEN needs Contents
    and Pull requests write access to the repository, and JIM_RELEASE_TOKEN (for -MergeWhenReleased) reads JIM's
    releases.

.PARAMETER ScreenshotPath
    The captured library for the version: Capture-Screenshots.ps1's <OutputPath>/<Version> folder.

.PARAMETER Version
    The release, as a folder name: v0.17.0.

.PARAMETER SitePath
    A checkout of junctional.io at the head of its base branch. It is changed in place.

.PARAMETER RunUrl
    The workflow run whose artefact holds the whole library, linked from the pull request.

.PARAMETER Build
    Builds the site (npm ci, then npm run build) with the new images before anything is committed, and stops if it
    fails. Needs Node.js.

.PARAMETER MergeWhenReleased
    Once the pull request is open, merges it if JIM's release for the version is already published.

.PARAMETER DryRun
    Updates the checkout (and builds it, with -Build) and lists what would be committed, without touching GitHub.

.EXAMPLE
    $env:GH_TOKEN = '...'
    pwsh ./scripts/screenshots/Publish-TourScreenshots.ps1 -ScreenshotPath artifacts/screenshots/v0.17.0 -Version v0.17.0 -SitePath ../junctional.io
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ScreenshotPath,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$SitePath,
    [string]$Repository = 'TetronIO/junctional.io',
    [string]$BaseBranch = 'main',
    [string]$RunUrl,
    [switch]$Build,
    [switch]$MergeWhenReleased,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TourAssets.ps1')
. (Join-Path $PSScriptRoot 'TourPullRequest.ps1')

$result = Update-TourAssetFolder -SitePath $SitePath -ScreenshotPath $ScreenshotPath -Version $Version
$changes = @(Get-SiteChange -SitePath $SitePath)
Write-Host "Tour $($result.PreviousVersion) -> $Version`: $($result.Published.Count) image(s) published, $($changes.Count) file(s) changed."
if ($result.RemovedFolders.Count -gt 0) { Write-Host "Removed folder(s) no step uses: $($result.RemovedFolders -join ', ')." }

if ($changes.Count -eq 0) {
    Write-Host 'The site already shows these screenshots; nothing to publish.'
    return
}
if ($Build) {
    # The changes are listed first, so nothing the build writes can be committed with them.
    Invoke-TourSiteBuild -SitePath $SitePath
    Write-Host 'The site builds with these screenshots.'
}
if ($DryRun) {
    $changes | ForEach-Object { Write-Host ('  {0} {1}' -f ($(if ($_.Deleted) { 'removed' } else { 'written' }), $_.Path)) }
    return
}
if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is not set; it needs Contents and Pull requests write access to ' + $Repository + '.' }
if ($MergeWhenReleased -and -not $env:JIM_RELEASE_TOKEN) { throw 'JIM_RELEASE_TOKEN is not set; -MergeWhenReleased reads JIM''s releases with it.' }

$branch = "screenshots/$Version"
$removed = if ($result.RemovedFolders.Count -gt 0) {
    ($result.RemovedFolders | ForEach-Object { "``src/assets/tour/$_/``" }) -join ', '
}
else { 'nothing' }
$library = if ($RunUrl) { "Every image the capture produced, including those not on the site, is in [the run's artefact]($RunUrl)." } else { '' }

$body = @"
The product tour's screenshots for JIM $Version, captured by JIM's release workflow from integration Scenario 8 with the portal in Production mode (TetronIO/JIM#582).

- **Added:** ``src/assets/tour/$Version/``, the $($result.Published.Count) images the tour shows, with ``imageVersion`` set to ``$Version`` in ``en-GB.ts`` and ``en-US.ts``.
- **Fixed address:** ``public/assets/screenshots/`` holds the homepage images at addresses that do not change, for links from outside the site such as JIM's README.
- **Removed:** $removed, which no step uses any more.

$library

This pull request merges itself once JIM $Version is published and its checks pass; close it to keep these images off the site. Before opening it, the release workflow refused blank images and dark and light pairs in the same theme, and built the site with the new images.

Each step's ``alt`` text is not updated automatically. If an image now shows something its text does not describe, correct the text in both dictionaries.
"@

$commit = Publish-SiteChangeCommit -Repository $Repository -BaseBranch $BaseBranch -Branch $branch -SitePath $SitePath `
    -Change $changes -Message "Refresh the product tour screenshots for JIM $Version" -Token $env:GH_TOKEN
$url = Open-TourPullRequest -Repository $Repository -BaseBranch $BaseBranch -Branch $branch `
    -Title "Product tour screenshots for JIM $Version" -Body $body -Token $env:GH_TOKEN

Write-Host "Committed $commit to $branch; pull request: $url"
$outcome = 'It merges when the release is published.'
if ($MergeWhenReleased) {
    $merged = Invoke-ScreenshotMergeIfReleased -Repository $Repository -Branch $branch -Version $Version `
        -Token $env:GH_TOKEN -ReleaseToken $env:JIM_RELEASE_TOKEN
    if ($merged) { $outcome = 'The release was already published, so it is merged.' }
}
if ($env:GITHUB_STEP_SUMMARY) {
    "### Product tour screenshots`n`n[$Repository pull request]($url) for $Version ($($result.Published.Count) images). $outcome" |
        Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}
