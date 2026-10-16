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
        # Invoke-RestMethod writes a JSON array as one object rather than as its items. Returned through a variable it
        # unrolls, so a list reaches the caller as its items and an empty list as none, not as one empty item.
        $response = Invoke-RestMethod @request
        return $response
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

<#
.SYNOPSIS
    True once JIM's release for the version is published: a release exists for the tag and is not a draft.
#>
function Test-JimReleasePublished {
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Token,
        [string]$Repository = 'TetronIO/JIM'
    )

    try {
        $release = Invoke-GitHubApi -Method GET -Path "repos/$Repository/releases/tags/$Version" -Token $Token
    }
    catch [GitHubApiNotFoundException] {
        return $false
    }
    return -not $release.draft
}

<#
.SYNOPSIS
    Merges the branch's open screenshot pull request once its checks have passed, then deletes the branch.
.DESCRIPTION
    How a release publishes its screenshots (#582): the release workflow calls this when the release is published,
    and the screenshots job calls it when it opens a pull request after the release already was, so whichever finishes
    last merges. The other side then finds the pull request already merged, which is reported, not treated as failure.

    The checks are read through the pull request's mergeable state, which needs only Pull requests read access: GitHub
    reports it clean once every check on the head commit has passed (tetron.io builds a preview of the change), and
    unstable while one is still running or has failed. So a failed check is waited on until the timeout rather than
    refused at once. A pull request opened moments ago is given a minute for its checks to appear before clean is
    believed. The merge names the commit seen clean, so anything pushed afterwards is not merged unseen, and uses the
    first method the repository allows: squash, then a merge commit, then rebase.
.OUTPUTS
    An object whose Status is Merged, AlreadyMerged or NotOpen (no pull request for the branch yet), with its Url.
#>
function Merge-ScreenshotPullRequest {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][string]$Token,
        [int]$CheckTimeoutSeconds = 900,
        [int]$PollSeconds = 20
    )

    $owner = $Repository.Split('/')[0]
    $pulls = Invoke-GitHubApi -Method GET -Path "repos/$Repository/pulls?head=${owner}:$Branch&state=open" -Token $Token
    $open = @($pulls)
    if ($open.Count -eq 0) {
        $closed = Invoke-GitHubApi -Method GET -Path "repos/$Repository/pulls?head=${owner}:$Branch&state=closed" -Token $Token
        $merged = @(@($closed) | Where-Object { $_.merged_at })
        if ($merged.Count -gt 0) { return [pscustomobject]@{ Status = 'AlreadyMerged'; Url = $merged[0].html_url } }
        return [pscustomobject]@{ Status = 'NotOpen'; Url = $null }
    }
    $pull = $open[0]
    $alreadyMerged = [pscustomobject]@{ Status = 'AlreadyMerged'; Url = $pull.html_url }

    # Invoke-RestMethod may already have turned the timestamp into a date; a cast reads either.
    $age = (Get-Date).ToUniversalTime() - ([datetime]$pull.created_at).ToUniversalTime()
    if ($age.TotalSeconds -lt 60) { Start-Sleep -Seconds ([int](60 - $age.TotalSeconds)) }

    $deadline = (Get-Date).AddSeconds($CheckTimeoutSeconds)
    while ($true) {
        $now = Invoke-GitHubApi -Method GET -Path "repos/$Repository/pulls/$($pull.number)" -Token $Token
        if ($now.merged) { return $alreadyMerged }
        if ($now.state -ne 'open') { throw "Not merging $($pull.html_url): it was closed." }
        $state = $now.mergeable_state
        if ($state -in 'clean', 'has_hooks') { break }
        # Unknown while GitHub works it out; unstable while a check is running or has failed.
        if ($state -notin 'unknown', 'unstable') { throw "Not merging $($pull.html_url): GitHub reports it as $state." }
        if ((Get-Date) -ge $deadline) {
            throw "Not merging $($pull.html_url): its checks had not all passed after $CheckTimeoutSeconds seconds (GitHub reports it as $state)."
        }
        Start-Sleep -Seconds $PollSeconds
    }
    $sha = $now.head.sha

    $refusals = [System.Collections.Generic.List[string]]::new()
    $done = $false
    foreach ($method in 'squash', 'merge', 'rebase') {
        try {
            Invoke-GitHubApi -Method PUT -Path "repos/$Repository/pulls/$($pull.number)/merge" -Token $Token `
                -Body @{ merge_method = $method; sha = $sha } | Out-Null
            $done = $true
            break
        }
        catch [GitHubApiNotFoundException] {
            throw
        }
        catch {
            $refusals.Add("$method`: $_")
            # A method the repository does not allow answers 405, so the next is tried; anything else ends the attempt.
            if ("$_" -notmatch '\(405\)') { break }
        }
    }
    if (-not $done) {
        # The other side may have merged it while this one waited on the checks, and every method then refuses.
        $after = Invoke-GitHubApi -Method GET -Path "repos/$Repository/pulls/$($pull.number)" -Token $Token
        if ($after.merged) { return $alreadyMerged }
        throw "Not merging $($pull.html_url): $($refusals -join ' | ')"
    }

    try {
        Invoke-GitHubApi -Method DELETE -Path "repos/$Repository/git/refs/heads/$Branch" -Token $Token | Out-Null
    }
    catch [GitHubApiNotFoundException] {
        # Already gone: the repository deletes merged branches itself.
    }
    catch {
        # The merge is what publishes; a branch left behind is only untidy.
        Write-Warning "Merged $($pull.html_url), but the branch $Branch was not deleted: $_"
    }
    return [pscustomobject]@{ Status = 'Merged'; Url = $pull.html_url }
}

<#
.SYNOPSIS
    Merges a screenshot pull request just opened, if JIM's release for the version is already published.
.DESCRIPTION
    The screenshots job's half of publishing: when the release came out while the job was still capturing, nothing
    else will merge the pull request, so the job does. Otherwise the release workflow merges it when the release is
    published.
.OUTPUTS
    Merge-ScreenshotPullRequest's result, or nothing when the release is not out yet.
#>
function Invoke-ScreenshotMergeIfReleased {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][string]$ReleaseToken
    )

    if (-not (Test-JimReleasePublished -Version $Version -Token $ReleaseToken)) {
        Write-Host "JIM $Version is not published yet; the release workflow merges the pull request when it is."
        return
    }
    $result = Merge-ScreenshotPullRequest -Repository $Repository -Branch $Branch -Token $Token
    Write-Host "JIM $Version is already published, so the pull request is merged ($($result.Status)): $($result.Url)"
    return $result
}

<#
.SYNOPSIS
    Merges each site's screenshot pull request for a release just published (#582), carrying on past a failure.
.DESCRIPTION
    The release workflow's half of publishing, run once the GitHub release exists. One site's failure (a check that
    failed, a pull request nothing can merge) does not keep the other's screenshots back.
.OUTPUTS
    One object per repository: Repository; Status, which is Merged, AlreadyMerged, NotOpen or Failed; Url; and
    Message, why it failed.
#>
function Invoke-ReleaseScreenshotMerge {
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string[]]$Repository,
        [Parameter(Mandatory)][string]$Token
    )

    foreach ($site in $Repository) {
        try {
            $result = Merge-ScreenshotPullRequest -Repository $site -Branch "screenshots/$Version" -Token $Token
            [pscustomobject]@{ Repository = $site; Status = $result.Status; Url = $result.Url; Message = $null }
        }
        catch {
            [pscustomobject]@{ Repository = $site; Status = 'Failed'; Url = $null; Message = "$_" }
        }
    }
}
