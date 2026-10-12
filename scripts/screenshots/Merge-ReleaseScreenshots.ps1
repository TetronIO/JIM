# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Publishes a release's screenshots: merges junctional.io's and tetron.io's screenshot pull requests (#582).

.DESCRIPTION
    Run by release.yml's publish-screenshots job once the GitHub release exists, so the sites show a release's
    screenshots the moment it is published and never before. The jim-screenshots job in pre-release.yml opened the pull
    requests (Publish-TourScreenshots.ps1, Publish-TetronScreenshots.ps1) on the branch screenshots/<Version>; each is
    merged once its checks pass (TourPullRequest.ps1, Merge-ScreenshotPullRequest).

    That job can still be capturing when the release is published. A site with no pull request yet is reported as a
    notice, not a failure: the job merges the pull request itself when it opens it and finds the release out.

    A pull request that cannot be merged is reported as a warning naming the reason, and the script exits 1; the
    workflow step carries on regardless, since the release is already published.

    The token comes from the GH_TOKEN environment variable, never a parameter, so it cannot reach a log. It needs
    Contents and Pull requests write access to every repository named.

.PARAMETER Version
    The release, as its tag: v0.17.0.

.PARAMETER Repository
    The sites whose screenshot pull requests to merge.

.EXAMPLE
    $env:GH_TOKEN = '...'
    pwsh ./scripts/screenshots/Merge-ReleaseScreenshots.ps1 -Version v0.17.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string[]]$Repository = @('TetronIO/junctional.io', 'TetronIO/tetron.io')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TourPullRequest.ps1')

if (-not $env:GH_TOKEN) { throw "GH_TOKEN is not set; it needs Contents and Pull requests write access to $($Repository -join ', ')." }

# A workflow command ends at a newline and reads % as an escape.
function ConvertTo-AnnotationText([string]$Text) { $Text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A') }

$results = @(Invoke-ReleaseScreenshotMerge -Version $Version -Repository $Repository -Token $env:GH_TOKEN)
$summary = foreach ($result in $results) {
    switch ($result.Status) {
        'Merged' {
            Write-Host "$($result.Repository): merged $($result.Url)"
            "- **$($result.Repository):** merged [the pull request]($($result.Url))."
        }
        'AlreadyMerged' {
            Write-Host "$($result.Repository): already merged, $($result.Url)"
            "- **$($result.Repository):** [the pull request]($($result.Url)) was already merged."
        }
        'NotOpen' {
            $text = "$($result.Repository) has no open screenshots pull request for $Version. If the jim-screenshots job is still running, it merges the pull request itself when it opens it; if that job failed, re-run it."
            Write-Host "::notice title=No screenshots pull request on $($result.Repository)::$(ConvertTo-AnnotationText $text)"
            "- **$($result.Repository):** $text"
        }
        default {
            $text = "$($result.Message) JIM $Version is published regardless; merge the pull request once it can be, or re-run this job."
            Write-Host "::warning title=Screenshots not published to $($result.Repository)::$(ConvertTo-AnnotationText $text)"
            "- **$($result.Repository):** not merged. $text"
        }
    }
}

if ($env:GITHUB_STEP_SUMMARY) {
    "### Screenshots for $Version`n`n$($summary -join "`n")" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}
if (@($results | Where-Object Status -eq 'Failed').Count -gt 0) { exit 1 }
