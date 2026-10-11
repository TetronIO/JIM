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

    Nothing merges by itself: the pull request is for a person to look through the images, check each step's alt text
    still describes its image, and merge once the release is published.

    The token comes from the GH_TOKEN environment variable, never a parameter, so it cannot reach a log. It needs
    Contents and Pull requests write access to the repository.

.PARAMETER ScreenshotPath
    The captured library for the version: Capture-Screenshots.ps1's <OutputPath>/<Version> folder.

.PARAMETER Version
    The release, as a folder name: v0.17.0.

.PARAMETER SitePath
    A checkout of junctional.io at the head of its base branch. It is changed in place.

.PARAMETER RunUrl
    The workflow run whose artefact holds the whole library, linked from the pull request.

.PARAMETER DryRun
    Updates the checkout and lists what would be committed, without touching GitHub.

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
if ($DryRun) {
    $changes | ForEach-Object { Write-Host ('  {0} {1}' -f ($(if ($_.Deleted) { 'removed' } else { 'written' }), $_.Path)) }
    return
}
if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is not set; it needs Contents and Pull requests write access to ' + $Repository + '.' }

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

Before merging:

- [ ] Look through the images (the Files tab shows each one).
- [ ] Check each step's ``alt`` text still describes its image, in both dictionaries.
- [ ] Merge once JIM $Version is published.
"@

$commit = Publish-SiteChangeCommit -Repository $Repository -BaseBranch $BaseBranch -Branch $branch -SitePath $SitePath `
    -Change $changes -Message "Refresh the product tour screenshots for JIM $Version" -Token $env:GH_TOKEN
$url = Open-TourPullRequest -Repository $Repository -BaseBranch $BaseBranch -Branch $branch `
    -Title "Product tour screenshots for JIM $Version" -Body $body -Token $env:GH_TOKEN

Write-Host "Committed $commit to $branch; pull request: $url"
if ($env:GITHUB_STEP_SUMMARY) {
    "### Product tour screenshots`n`n[$Repository pull request]($url) for $Version ($($result.Published.Count) images)." |
        Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}
