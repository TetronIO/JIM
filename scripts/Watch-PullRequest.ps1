# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Waits on a pull request until it merges or needs you, and says which.

.DESCRIPTION
    Turning on auto-merge is not the end of a pull request's work. A failed
    required check (a CodeQL analysis, changelog-lint), a review thread nobody has
    resolved (the documentation review posts one per finding), a merge conflict, a
    branch behind main, or an unsigned commit each hold the merge, and a waiter
    that only watches for MERGED waits on them forever while the agent believes
    it is done.

    This script polls the pull request and exits on the first of:

      Merged             the work is done; clean up.        (exit 0)
      Closed             closed without merging; tell the user.  (exit 1)
      ChecksFailed       a check failed; fix it and push.   (exit 2)
      UnresolvedThreads  a review thread is open; fix the code or reply with
                         why it stays, then resolve it.     (exit 2)
      Conflict           merge main in and resolve.         (exit 2)
      Behind             merge main in, or update the branch.  (exit 2)
      Blocked            every check passed and no thread is open, but GitHub
                         still will not merge: usually an unsigned commit or a
                         required check that never ran.     (exit 2)

    Run it in the background after enabling auto-merge, act on what it reports,
    and run it again. Transient gh failures are retried, never reported as a
    reason. A check that ran more than once (a re-run, or a run cancelled by a
    newer one) counts by its latest run only.

    Needs the gh CLI, signed in. It reads the pull request with one GraphQL
    query; where GraphQL is refused (Claude Code on the web, whose relay allows
    only REST), it falls back to REST and the relay's review-thread route for the
    rest of the run. A thread is identified by its first comment's id in both,
    so -AcknowledgedThreadIds takes the same values either way.

.PARAMETER Number
    The pull request number.

.PARAMETER Repository
    owner/name. Defaults to TetronIO/JIM.

.PARAMETER IntervalSeconds
    How long to wait between polls. Defaults to 30.

.PARAMETER AcknowledgedThreadIds
    Review threads left open on purpose (a security finding waiting on the
    user's decision), by the id this script printed for them (the thread's
    first comment's id). They no longer
    end the wait, and they stop a Blocked report, since they are what blocks.

.PARAMETER Once
    Report the pull request's state once and return, even when nothing needs
    you (Reason Waiting).

.PARAMETER Snapshot
    Evaluate this snapshot instead of asking GitHub, once. For tests.

.PARAMETER Response
    Evaluate this GraphQL response (the parsed JSON of the query below) instead
    of asking GitHub, once. For tests.

.PARAMETER RestResponse
    Evaluate these REST responses instead of asking GitHub, once. For tests. A
    hashtable: Pull (the pull request), CheckRuns, Statuses (the combined
    status's statuses), Threads (the relay's review threads) and Comments (each
    unresolved thread's first comment, by id).

.PARAMETER PassThru
    Return the result object instead of printing it and setting the exit code.

.EXAMPLE
    pwsh ./scripts/Watch-PullRequest.ps1 -Number 2189

    Waits until pull request 2189 merges or needs attention, then prints why.

.EXAMPLE
    pwsh ./scripts/Watch-PullRequest.ps1 -Number 2189 -AcknowledgedThreadIds PRRT_kwDOG31UH86rnLYd

    As above, ignoring one thread deliberately left open for the user.
#>

[CmdletBinding(DefaultParameterSetName = 'Watch')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Watch')]
    [int]$Number,

    [Parameter(ParameterSetName = 'Watch')]
    [string]$Repository = 'TetronIO/JIM',

    [Parameter(ParameterSetName = 'Watch')]
    [ValidateRange(5, 3600)]
    [int]$IntervalSeconds = 30,

    [string[]]$AcknowledgedThreadIds = @(),

    [Parameter(ParameterSetName = 'Watch')]
    [switch]$Once,

    [Parameter(Mandatory, ParameterSetName = 'Snapshot')]
    [object]$Snapshot,

    [Parameter(Mandatory, ParameterSetName = 'Response')]
    [object]$Response,

    [Parameter(Mandatory, ParameterSetName = 'Rest')]
    [hashtable]$RestResponse,

    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'

# Conclusions that hold a merge. SUCCESS, NEUTRAL and SKIPPED do not; ERROR is a
# commit status's failure state.
$script:FailedConclusions = @('FAILURE', 'TIMED_OUT', 'CANCELLED', 'ACTION_REQUIRED', 'STARTUP_FAILURE', 'ERROR')

$script:Query = @'
query($owner: String!, $name: String!, $number: Int!) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      state
      mergeStateStatus
      commits(last: 1) {
        nodes {
          commit {
            statusCheckRollup {
              contexts(first: 100) {
                nodes {
                  __typename
                  ... on CheckRun { name status conclusion startedAt detailsUrl }
                  ... on StatusContext { context state createdAt targetUrl }
                }
              }
            }
          }
        }
      }
      reviewThreads(first: 100) {
        nodes {
          id
          isResolved
          comments(first: 1) {
            nodes { databaseId author { login } path line body url }
          }
        }
      }
    }
  }
}
'@

function ConvertTo-PullRequestSnapshot {
    # Flattens the GraphQL response into what Get-PullRequestAttention reads.
    param([Parameter(Mandatory)] $Response)

    $pr = $Response.data.repository.pullRequest
    if (-not $pr) { throw 'The GraphQL response has no pullRequest; check the number and repository.' }

    $contexts = @($pr.commits.nodes | Select-Object -First 1 |
        ForEach-Object { $_.commit.statusCheckRollup.contexts.nodes } | Where-Object { $_ })
    $checks = foreach ($c in $contexts) {
        if ($c.__typename -eq 'StatusContext') {
            $pending = $c.state -in @('PENDING', 'EXPECTED')
            [pscustomobject]@{
                Name       = $c.context
                Status     = if ($pending) { 'PENDING' } else { 'COMPLETED' }
                Conclusion = if ($pending) { $null } else { $c.state }
                StartedAt  = $c.createdAt
                Url        = $c.targetUrl
            }
        }
        else {
            [pscustomobject]@{
                Name       = $c.name
                Status     = $c.status
                Conclusion = $c.conclusion
                StartedAt  = $c.startedAt
                Url        = $c.detailsUrl
            }
        }
    }

    $threads = foreach ($t in @($pr.reviewThreads.nodes | Where-Object { $_ })) {
        $first = @($t.comments.nodes)[0]
        [pscustomobject]@{
            Id         = [string]$first.databaseId
            IsResolved = [bool]$t.isResolved
            Author     = $first.author.login
            Path       = $first.path
            Line       = $first.line
            Body       = $first.body
            Url        = $first.url
        }
    }

    [pscustomobject]@{
        State            = $pr.state
        MergeStateStatus = $pr.mergeStateStatus
        Checks           = @($checks)
        Threads          = @($threads)
    }
}

function ConvertFrom-RestPullRequest {
    # The same snapshot, from REST: the pull request, the head commit's check runs
    # and combined status, and the relay's review threads (which carry no thread
    # id, only comment ids; Comments holds each unresolved thread's first comment).
    param([Parameter(Mandatory)] [hashtable]$Rest)

    $pull = $Rest.Pull
    $state = if ($pull.merged) { 'MERGED' } elseif ($pull.state -eq 'closed') { 'CLOSED' } else { 'OPEN' }

    $checks = @(
        foreach ($r in @($Rest.CheckRuns | Where-Object { $_ })) {
            [pscustomobject]@{
                Name       = $r.name
                Status     = ([string]$r.status).ToUpperInvariant()
                Conclusion = if ($r.conclusion) { ([string]$r.conclusion).ToUpperInvariant() } else { $null }
                StartedAt  = $r.started_at
                Url        = $r.html_url
            }
        }
        foreach ($st in @($Rest.Statuses | Where-Object { $_ })) {
            $pending = $st.state -eq 'pending'
            [pscustomobject]@{
                Name       = $st.context
                Status     = if ($pending) { 'PENDING' } else { 'COMPLETED' }
                Conclusion = if ($pending) { $null } else { ([string]$st.state).ToUpperInvariant() }
                StartedAt  = $st.created_at
                Url        = $st.target_url
            }
        }
    )

    $comments = if ($Rest.Comments) { $Rest.Comments } else { @{} }
    $threads = foreach ($t in @($Rest.Threads | Where-Object { $_ })) {
        $id = [string]@($t.comment_ids)[0]
        $first = $comments[$id]
        [pscustomobject]@{
            Id         = $id
            IsResolved = [bool]$t.resolved
            Author     = $first.user.login
            Path       = $t.path
            Line       = $t.line
            Body       = $first.body
            Url        = $first.html_url
        }
    }

    [pscustomobject]@{
        State            = $state
        MergeStateStatus = ([string]$pull.mergeable_state).ToUpperInvariant()
        Checks           = @($checks)
        Threads          = @($threads)
    }
}

function Get-PullRequestAttention {
    # Decides whether the pull request is done, needs attention, or is still in
    # progress. Pure: reads only the snapshot.
    param(
        [Parameter(Mandatory)] $Snapshot,
        [string[]]$AcknowledgedThreadIds = @()
    )

    $result = {
        param([string]$Reason, [string]$Action, $Checks = @(), $Threads = @())
        [pscustomobject]@{ Reason = $Reason; Action = $Action; Checks = @($Checks); Threads = @($Threads) }
    }

    switch ($Snapshot.State) {
        'MERGED' { return & $result 'Merged' 'Clean up the local branch.' }
        'CLOSED' { return & $result 'Closed' 'It was closed without merging; tell the user rather than cleaning up.' }
    }

    # A check that ran more than once counts by its latest run.
    $latest = @($Snapshot.Checks | Where-Object { $_ } | Group-Object Name | ForEach-Object {
        $_.Group | Sort-Object { if ($_.StartedAt) { [datetime]$_.StartedAt } else { [datetime]::MinValue } } | Select-Object -Last 1
    })

    $failed = @($latest | Where-Object { $_.Status -eq 'COMPLETED' -and $_.Conclusion -in $script:FailedConclusions })
    if ($failed.Count -gt 0) {
        return & $result 'ChecksFailed' 'Read each failed check''s log, fix the cause, and push.' -Checks $failed
    }

    $open = @($Snapshot.Threads | Where-Object { $_ -and -not $_.IsResolved })
    $unacknowledged = @($open | Where-Object { $_.Id -notin $AcknowledgedThreadIds })
    if ($unacknowledged.Count -gt 0) {
        return & $result 'UnresolvedThreads' 'For each thread: fix it, or reply with why it stays; then resolve it. A reply alone does not clear the merge.' -Threads $unacknowledged
    }

    switch ($Snapshot.MergeStateStatus) {
        'DIRTY' { return & $result 'Conflict' 'Merge origin/main into the branch, resolve the conflict, and push.' }
        'BEHIND' { return & $result 'Behind' 'Merge origin/main into the branch and push (or gh pr update-branch).' }
        'BLOCKED' {
            # No checks at all means CI has not registered yet (just after the pull
            # request opens, or a push), not that GitHub is holding a passed one.
            $pending = @($latest | Where-Object { $_.Status -ne 'COMPLETED' })
            if ($latest.Count -gt 0 -and $pending.Count -eq 0 -and $open.Count -eq 0) {
                return & $result 'Blocked' 'Every check passed and no thread is open: check the head commit is signed, and that every required check ran.'
            }
        }
    }

    & $result 'Waiting' 'Nothing needs you yet.'
}

function Invoke-GhJson {
    param([string[]]$Arguments)
    $out = & gh @Arguments 2>&1
    $text = ($out | ForEach-Object { "$_" }) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        $err = [System.Exception]::new("gh $($Arguments[0..1] -join ' ') failed (exit $LASTEXITCODE): $text")
        $err.Data['Output'] = $text
        throw $err
    }
    $text | ConvertFrom-Json
}

function Get-RestSnapshot {
    param([string]$Repository, [int]$Number)
    $base = "repos/$Repository"
    $pull = Invoke-GhJson @('api', "$base/pulls/$Number")
    $sha = $pull.head.sha
    $runs = Invoke-GhJson @('api', '--paginate', '--slurp', "$base/commits/$sha/check-runs?per_page=100")
    $status = Invoke-GhJson @('api', "$base/commits/$sha/status")
    $threads = @(Invoke-GhJson @('api', "$base/pulls/$Number/ccr/review_threads"))
    $comments = @{}
    foreach ($t in @($threads | Where-Object { $_ -and -not $_.resolved })) {
        $id = [string]@($t.comment_ids)[0]
        if ($id) { $comments[$id] = Invoke-GhJson @('api', "$base/pulls/comments/$id") }
    }
    ConvertFrom-RestPullRequest -Rest @{
        Pull      = $pull
        CheckRuns = @($runs | ForEach-Object { $_.check_runs })
        Statuses  = @($status.statuses)
        Threads   = $threads
        Comments  = $comments
    }
}

$script:UseRest = $false
function Get-PullRequestSnapshot {
    param([string]$Repository, [int]$Number)
    if (-not $script:UseRest) {
        $owner, $name = $Repository.Split('/', 2)
        try {
            $response = Invoke-GhJson @('api', 'graphql', '-F', "owner=$owner", '-F', "name=$name", '-F', "number=$Number", '-f', "query=$script:Query")
            return ConvertTo-PullRequestSnapshot -Response $response
        }
        catch {
            # Claude Code on the web refuses GraphQL outright; switch to REST for
            # the rest of the run. Anything else is a real failure.
            if ("$($_.Exception.Data['Output'])" -notmatch 'GraphQL is not available') { throw }
            $script:UseRest = $true
        }
    }
    Get-RestSnapshot -Repository $Repository -Number $Number
}

function Write-Attention {
    param($Attention, [string]$Label)
    Write-Output "$Label`: $($Attention.Reason)"
    foreach ($c in $Attention.Checks) {
        Write-Output "  check  $($c.Name): $($c.Conclusion)  $($c.Url)"
    }
    foreach ($t in $Attention.Threads) {
        $where = if ($t.Line) { "$($t.Path):$($t.Line)" } else { $t.Path }
        $firstLine = (($t.Body -split "`n") | Where-Object { $_.Trim() } | Select-Object -First 1)
        Write-Output "  thread $($t.Id) by $($t.Author) at $where"
        Write-Output "         $firstLine"
        Write-Output "         $($t.Url)"
    }
    Write-Output "Next: $($Attention.Action)"
}

function Get-ExitCode {
    param([string]$Reason)
    switch ($Reason) {
        'Merged' { 0 }
        'Closed' { 1 }
        'Waiting' { 0 }
        default { 2 }
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Response') {
    $Snapshot = ConvertTo-PullRequestSnapshot -Response $Response
}
if ($PSCmdlet.ParameterSetName -eq 'Rest') {
    $Snapshot = ConvertFrom-RestPullRequest -Rest $RestResponse
}
if ($PSCmdlet.ParameterSetName -in @('Snapshot', 'Response', 'Rest')) {
    $attention = Get-PullRequestAttention -Snapshot $Snapshot -AcknowledgedThreadIds $AcknowledgedThreadIds
    if ($PassThru) { return $attention }
    Write-Attention -Attention $attention -Label 'Pull request'
    exit (Get-ExitCode $attention.Reason)
}

$label = "$Repository#$Number"
while ($true) {
    try {
        $snap = Get-PullRequestSnapshot -Repository $Repository -Number $Number
    }
    catch {
        if ($Once) { throw }
        Write-Warning "Could not read $label ($($_.Exception.Message)); retrying in $IntervalSeconds seconds."
        Start-Sleep -Seconds $IntervalSeconds
        continue
    }

    $attention = Get-PullRequestAttention -Snapshot $snap -AcknowledgedThreadIds $AcknowledgedThreadIds
    if ($attention.Reason -ne 'Waiting' -or $Once) {
        if ($PassThru) { return $attention }
        Write-Attention -Attention $attention -Label $label
        exit (Get-ExitCode $attention.Reason)
    }
    Start-Sleep -Seconds $IntervalSeconds
}
