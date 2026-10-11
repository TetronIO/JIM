# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Commits a junctional.io tour refresh through GitHub's API and opens its pull request (#582).

.DESCRIPTION
    The refresh is made in a local checkout of the site (TourAssets.ps1), then sent to GitHub through the Git Data API
    rather than pushed: a commit created through the API with a GitHub App's token is signed by GitHub, so it satisfies
    a branch rule requiring signed commits, which a push from the runner would not. The same reason, and the same
    approach, as regenerate-nuget-lock-files.yml.

    One branch and one pull request per release version: running again moves the branch to a fresh commit on top of the
    base branch and updates the open pull request's description, rather than opening a second one.
#>

Set-StrictMode -Version Latest

# Raised for a resource GitHub reports as absent, so callers can tell "not there yet" from a real failure.
class GitHubApiNotFoundException : System.Exception {
    GitHubApiNotFoundException([string]$message) : base($message) {}
}

# One GitHub REST call. $Path is relative to https://api.github.com/; $Body, where given, is sent as JSON.
function Invoke-GitHubApi {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        $Body,
        [Parameter(Mandatory)][string]$Token
    )

    $request = @{
        Method  = $Method
        Uri     = "https://api.github.com/$Path"
        Headers = @{
            Authorization          = "Bearer $Token"
            Accept                 = 'application/vnd.github+json'
            'X-GitHub-Api-Version' = '2022-11-28'
        }
    }
    if ($null -ne $Body) {
        $request.Body = $Body | ConvertTo-Json -Depth 10 -Compress
        $request.ContentType = 'application/json'
    }

    try {
        return Invoke-RestMethod @request
    }
    catch [Microsoft.PowerShell.Commands.HttpResponseException] {
        $status = [int]$_.Exception.Response.StatusCode
        # Updating a branch that does not exist answers 422 "Reference does not exist", not 404.
        if ($status -eq 404 -or ($status -eq 422 -and "$($_.ErrorDetails)" -match 'Reference does not exist')) {
            throw [GitHubApiNotFoundException]::new("$Method $Path`: $($_.ErrorDetails)")
        }
        throw "GitHub refused $Method $Path ($status): $($_.ErrorDetails)"
    }
}

# git status --porcelain=v1 lines as the paths a commit must add, change or remove.
function ConvertFrom-GitStatusLine {
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Line)

    foreach ($entry in $Line | Where-Object { $_ }) {
        $state = $entry.Substring(0, 2)
        if ($state -match '[RC]') { throw "Unexpected rename or copy in the site checkout: $entry" }
        [pscustomobject]@{ Path = $entry.Substring(3); Deleted = $state.Contains('D') }
    }
}

# What the refresh changed in a site checkout, relative to its last commit.
function Get-SiteChange {
    param([Parameter(Mandatory)][string]$SitePath)

    $lines = @(& git -C $SitePath -c core.quotePath=false status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw "git status failed in $SitePath (exit $LASTEXITCODE)." }
    return @(ConvertFrom-GitStatusLine -Line $lines)
}

<#
.SYNOPSIS
    Commits changes made in a local site checkout onto a branch of the repository, through the Git Data API.
.DESCRIPTION
    The commit's parent is the base branch's head, and its tree is the base branch's tree with the changes applied:
    each added or changed file uploaded as a blob, each removed one dropped. The branch is created at the commit, or
    moved to it if it already exists.
.OUTPUTS
    The new commit's SHA.
#>
function Publish-SiteChangeCommit {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$BaseBranch,
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][string]$SitePath,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Change,
        [Parameter(Mandatory)][string]$Message,
        [Parameter(Mandatory)][string]$Token
    )

    $repo = "repos/$Repository"
    $baseSha = (Invoke-GitHubApi -Method GET -Path "$repo/git/ref/heads/$BaseBranch" -Token $Token).object.sha
    $baseTree = (Invoke-GitHubApi -Method GET -Path "$repo/git/commits/$baseSha" -Token $Token).tree.sha

    $entries = foreach ($item in $Change) {
        $sha = $null
        if (-not $item.Deleted) {
            $bytes = [System.IO.File]::ReadAllBytes((Join-Path $SitePath $item.Path))
            $blob = Invoke-GitHubApi -Method POST -Path "$repo/git/blobs" -Token $Token `
                -Body @{ content = [Convert]::ToBase64String($bytes); encoding = 'base64' }
            $sha = $blob.sha
        }
        [ordered]@{ path = $item.Path; mode = '100644'; type = 'blob'; sha = $sha }
    }

    $tree = Invoke-GitHubApi -Method POST -Path "$repo/git/trees" -Token $Token `
        -Body @{ base_tree = $baseTree; tree = @($entries) }
    $commit = Invoke-GitHubApi -Method POST -Path "$repo/git/commits" -Token $Token `
        -Body @{ message = $Message; tree = $tree.sha; parents = @($baseSha) }

    try {
        Invoke-GitHubApi -Method PATCH -Path "$repo/git/refs/heads/$Branch" -Token $Token `
            -Body @{ sha = $commit.sha; force = $true } | Out-Null
    }
    catch [GitHubApiNotFoundException] {
        Invoke-GitHubApi -Method POST -Path "$repo/git/refs" -Token $Token `
            -Body @{ ref = "refs/heads/$Branch"; sha = $commit.sha } | Out-Null
    }

    return $commit.sha
}

<#
.SYNOPSIS
    Opens the branch's pull request, or updates the one already open.
.OUTPUTS
    The pull request's address.
#>
function Open-TourPullRequest {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$BaseBranch,
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][string]$Body,
        [Parameter(Mandatory)][string]$Token
    )

    $owner = $Repository.Split('/')[0]
    $open = @(Invoke-GitHubApi -Method GET -Path "repos/$Repository/pulls?head=${owner}:$Branch&state=open" -Token $Token)
    if ($open.Count -gt 0) {
        $updated = Invoke-GitHubApi -Method PATCH -Path "repos/$Repository/pulls/$($open[0].number)" -Token $Token `
            -Body @{ title = $Title; body = $Body }
        return $updated.html_url
    }

    $created = Invoke-GitHubApi -Method POST -Path "repos/$Repository/pulls" -Token $Token `
        -Body @{ title = $Title; head = $Branch; base = $BaseBranch; body = $Body }
    return $created.html_url
}
