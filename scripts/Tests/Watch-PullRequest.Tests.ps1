# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Watch-PullRequest.ps1.

.DESCRIPTION
    Agents enable auto-merge and then wait on the pull request. A waiter that
    only watches for MERGED waits forever on a pull request a failed check or an
    unresolved review thread is holding, while the agent believes it is done.
    These tests pin down what ends the wait: every state that needs the agent
    does, and nothing that is merely in progress does.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Watch-PullRequest.ps1')).Path

    function New-Check {
        param([string]$Name, [string]$Conclusion = 'SUCCESS', [string]$Status = 'COMPLETED', [string]$StartedAt = '2026-10-13T10:00:00Z')
        [pscustomobject]@{ Name = $Name; Status = $Status; Conclusion = $Conclusion; StartedAt = $StartedAt; Url = "https://example.test/$Name" }
    }

    function New-Thread {
        param([string]$Id, [bool]$IsResolved = $false)
        [pscustomobject]@{ Id = $Id; IsResolved = $IsResolved; Author = 'claude'; Path = 'docs/a.md'; Line = 3; Body = "**VOI-1** finding`nmore"; Url = "https://example.test/$Id" }
    }

    function New-Snapshot {
        param(
            [string]$State = 'OPEN', [string]$MergeStateStatus = 'BLOCKED', $Checks = @(), $Threads = @(),
            [bool]$InMergeQueue = $false, $MergeQueuePosition = $null, $QueueRemoval = $null
        )
        [pscustomobject]@{
            State = $State; MergeStateStatus = $MergeStateStatus; Checks = @($Checks); Threads = @($Threads)
            InMergeQueue = $InMergeQueue; MergeQueuePosition = $MergeQueuePosition; QueueRemoval = $QueueRemoval
        }
    }

    function New-Removal {
        param([string]$Reason = 'failed_checks', $Checks = @())
        [pscustomobject]@{ Reason = $Reason; Checks = @($Checks) }
    }

    function Get-Attention {
        param($Snapshot, [string[]]$Acknowledged = @())
        & $script:ScriptPath -Snapshot $Snapshot -AcknowledgedThreadIds $Acknowledged -PassThru
    }
}

Describe 'Watch-PullRequest: what ends the wait' {
    It 'reports Merged for a merged pull request' {
        (Get-Attention (New-Snapshot -State MERGED -MergeStateStatus UNKNOWN)).Reason | Should -Be 'Merged'
    }

    It 'reports Closed for a pull request closed without merging' {
        (Get-Attention (New-Snapshot -State CLOSED -MergeStateStatus UNKNOWN)).Reason | Should -Be 'Closed'
    }

    It 'keeps waiting while checks are still running' {
        $snap = New-Snapshot -Checks @((New-Check build-and-test -Status IN_PROGRESS -Conclusion $null), (New-Check changelog-lint))
        (Get-Attention $snap).Reason | Should -Be 'Waiting'
    }

    It 'keeps waiting when every check passed and auto-merge has not fired yet' {
        $snap = New-Snapshot -MergeStateStatus CLEAN -Checks @((New-Check build-and-test), (New-Check docs-review -Conclusion SKIPPED))
        (Get-Attention $snap).Reason | Should -Be 'Waiting'
    }

    It 'reports ChecksFailed for a failed check, naming it, even while others still run' {
        $snap = New-Snapshot -Checks @((New-Check 'Analyze (csharp)' -Conclusion FAILURE), (New-Check build-and-test -Status IN_PROGRESS -Conclusion $null))
        $a = Get-Attention $snap
        $a.Reason | Should -Be 'ChecksFailed'
        $a.Checks.Name | Should -Be @('Analyze (csharp)')
    }

    It 'treats a failed commit status (ERROR) as a failed check' {
        $snap = New-Snapshot -Checks @((New-Check legacy-status -Conclusion ERROR))
        (Get-Attention $snap).Reason | Should -Be 'ChecksFailed'
    }

    It 'judges a check that ran twice by its latest run' {
        $snap = New-Snapshot -MergeStateStatus CLEAN -Checks @(
            (New-Check docs-review -Conclusion CANCELLED -StartedAt '2026-10-13T10:00:00Z'),
            (New-Check docs-review -Conclusion SUCCESS -StartedAt '2026-10-13T10:05:00Z'))
        (Get-Attention $snap).Reason | Should -Be 'Waiting'
    }

    It 'still reports a check whose latest run failed after an earlier success' {
        $snap = New-Snapshot -Checks @(
            (New-Check build-and-test -Conclusion SUCCESS -StartedAt '2026-10-13T10:00:00Z'),
            (New-Check build-and-test -Conclusion FAILURE -StartedAt '2026-10-13T10:05:00Z'))
        (Get-Attention $snap).Reason | Should -Be 'ChecksFailed'
    }

    It 'reports UnresolvedThreads for an open review thread, while checks still run' {
        $snap = New-Snapshot -Checks @((New-Check build-and-test -Status IN_PROGRESS -Conclusion $null)) -Threads @((New-Thread T1), (New-Thread T2 -IsResolved $true))
        $a = Get-Attention $snap
        $a.Reason | Should -Be 'UnresolvedThreads'
        $a.Threads.Id | Should -Be @('T1')
    }

    It 'keeps waiting on a thread acknowledged as deliberately open' {
        $snap = New-Snapshot -Threads @((New-Thread T1)) -Checks @((New-Check build-and-test -Status IN_PROGRESS -Conclusion $null))
        (Get-Attention $snap -Acknowledged 'T1').Reason | Should -Be 'Waiting'
    }

    It 'does not report Blocked when an acknowledged thread is what blocks' {
        $snap = New-Snapshot -MergeStateStatus BLOCKED -Threads @((New-Thread T1)) -Checks @((New-Check build-and-test))
        (Get-Attention $snap -Acknowledged 'T1').Reason | Should -Be 'Waiting'
    }

    It 'reports Conflict for a pull request that no longer merges cleanly' {
        (Get-Attention (New-Snapshot -MergeStateStatus DIRTY)).Reason | Should -Be 'Conflict'
    }

    It 'keeps waiting on a pull request behind main, since the merge queue tests it against main itself' {
        (Get-Attention (New-Snapshot -MergeStateStatus BEHIND -Checks @((New-Check build-and-test)))).Reason | Should -Be 'Waiting'
    }

    It 'reports Blocked when every check passed, no thread is open, and GitHub still holds the merge' {
        $snap = New-Snapshot -MergeStateStatus BLOCKED -Checks @((New-Check build-and-test)) -Threads @((New-Thread T1 -IsResolved $true))
        (Get-Attention $snap).Reason | Should -Be 'Blocked'
    }

    It 'reports a failed check before an open thread' {
        $snap = New-Snapshot -Checks @((New-Check changelog-lint -Conclusion FAILURE)) -Threads @((New-Thread T1))
        (Get-Attention $snap).Reason | Should -Be 'ChecksFailed'
    }
}

Describe 'Watch-PullRequest: the merge queue' {
    It 'keeps waiting on a queued pull request whose own checks all passed, rather than reporting Blocked' {
        $snap = New-Snapshot -MergeStateStatus BLOCKED -InMergeQueue $true -MergeQueuePosition 2 -Checks @((New-Check build-and-test))
        $a = Get-Attention $snap
        $a.Reason | Should -Be 'Waiting'
        $a.Action | Should -Match 'position 2'
    }

    It 'reports RemovedFromQueue for an entry whose merge_group checks failed, naming the failed ones' {
        $removal = New-Removal -Reason failed_checks -Checks @((New-Check 'Analyze (csharp)' -Conclusion FAILURE), (New-Check build-and-test))
        $a = Get-Attention (New-Snapshot -MergeStateStatus CLEAN -Checks @((New-Check build-and-test)) -QueueRemoval $removal)
        $a.Reason | Should -Be 'RemovedFromQueue'
        $a.Checks.Name | Should -Be @('Analyze (csharp)')
        $a.Action | Should -Match 'gh pr merge'
    }

    It 'reports RemovedFromQueue for an entry that conflicted with main, saying to merge main in' {
        $a = Get-Attention (New-Snapshot -MergeStateStatus DIRTY -QueueRemoval (New-Removal -Reason merge_conflict))
        $a.Reason | Should -Be 'RemovedFromQueue'
        $a.Action | Should -Match 'origin/main'
    }

    It 'reports RemovedFromQueue for a manual removal, saying to ask before re-adding' {
        $a = Get-Attention (New-Snapshot -MergeStateStatus CLEAN -QueueRemoval (New-Removal -Reason manual))
        $a.Reason | Should -Be 'RemovedFromQueue'
        $a.Action | Should -Match 'user'
    }

    It 'reports a failed check on the pull request itself before a queue removal' {
        $snap = New-Snapshot -Checks @((New-Check changelog-lint -Conclusion FAILURE)) -QueueRemoval (New-Removal)
        (Get-Attention $snap).Reason | Should -Be 'ChecksFailed'
    }
}

Describe 'Watch-PullRequest: reading the GraphQL response' {
    It 'reads check runs, commit statuses and review threads' {
        $json = @'
{ "data": { "repository": { "pullRequest": {
  "state": "OPEN", "mergeStateStatus": "BLOCKED",
  "commits": { "nodes": [ { "commit": { "statusCheckRollup": { "contexts": { "nodes": [
    { "__typename": "CheckRun", "name": "build-and-test", "status": "COMPLETED", "conclusion": "SUCCESS", "startedAt": "2026-10-13T10:00:00Z", "detailsUrl": "https://x/1" },
    { "__typename": "StatusContext", "context": "legacy", "state": "PENDING", "createdAt": "2026-10-13T10:00:00Z", "targetUrl": "https://x/2" }
  ] } } } } ] },
  "reviewThreads": { "nodes": [
    { "id": "PRRT_1", "isResolved": false, "comments": { "nodes": [ { "databaseId": 4252088407, "author": { "login": "claude" }, "path": "docs/a.md", "line": 7, "body": "**SAFE-1** x", "url": "https://x/3" } ] } }
  ] }
} } } }
'@ | ConvertFrom-Json
        $a = & $script:ScriptPath -Response $json -PassThru
        $a.Reason | Should -Be 'UnresolvedThreads'
        # Keyed by the first comment's id, which the REST route also gives, so one
        # -AcknowledgedThreadIds value works whichever API answered.
        $a.Threads[0].Id | Should -Be '4252088407'
        $a.Threads[0].Path | Should -Be 'docs/a.md'
    }

    It 'treats a pending commit status as still running, not as failed' {
        $json = @'
{ "data": { "repository": { "pullRequest": {
  "state": "OPEN", "mergeStateStatus": "BLOCKED",
  "commits": { "nodes": [ { "commit": { "statusCheckRollup": { "contexts": { "nodes": [
    { "__typename": "StatusContext", "context": "legacy", "state": "PENDING", "createdAt": "2026-10-13T10:00:00Z", "targetUrl": "https://x/2" }
  ] } } } } ] },
  "reviewThreads": { "nodes": [] }
} } } }
'@ | ConvertFrom-Json
        (& $script:ScriptPath -Response $json -PassThru).Reason | Should -Be 'Waiting'
    }

    It 'keeps waiting on a commit with no checks yet, rather than reporting Blocked' {
        $json = '{ "data": { "repository": { "pullRequest": { "state": "OPEN", "mergeStateStatus": "BLOCKED", "commits": { "nodes": [ { "commit": { "statusCheckRollup": null } } ] }, "reviewThreads": { "nodes": [] } } } } }' | ConvertFrom-Json
        (& $script:ScriptPath -Response $json -PassThru).Reason | Should -Be 'Waiting'
    }

    Context 'the merge queue' {
        BeforeAll {
            # A green pull request with the given queue fields and latest queue event.
            function New-QueueResponse {
                param([string]$QueueFields, [string]$LastQueueEvent)
                $events = if ($LastQueueEvent) { $LastQueueEvent } else { '' }
                @"
{ "data": { "repository": { "pullRequest": {
  "state": "OPEN", "mergeStateStatus": "BLOCKED", $QueueFields,
  "commits": { "nodes": [ { "commit": { "statusCheckRollup": { "contexts": { "nodes": [
    { "__typename": "CheckRun", "name": "build-and-test", "status": "COMPLETED", "conclusion": "SUCCESS", "startedAt": "2026-10-13T10:00:00Z", "detailsUrl": "https://x/1" }
  ] } } } } ] },
  "reviewThreads": { "nodes": [] },
  "timelineItems": { "nodes": [ $events ] }
} } } }
"@ | ConvertFrom-Json
            }
            $script:FailedRemoval = @'
{ "__typename": "RemovedFromMergeQueueEvent", "reason": "failed_checks", "beforeCommit": { "oid": "5594bca", "statusCheckRollup": { "contexts": { "nodes": [
  { "__typename": "CheckRun", "name": "Analyze (csharp)", "status": "COMPLETED", "conclusion": "FAILURE", "startedAt": "2026-10-13T11:00:00Z", "detailsUrl": "https://x/mg1" },
  { "__typename": "CheckRun", "name": "build-and-test", "status": "COMPLETED", "conclusion": "SUCCESS", "startedAt": "2026-10-13T11:00:00Z", "detailsUrl": "https://x/mg2" }
] } } } }
'@
        }

        It 'reads a queued pull request as waiting, with its position' {
            $json = New-QueueResponse -QueueFields '"isInMergeQueue": true, "mergeQueueEntry": { "state": "AWAITING_CHECKS", "position": 1 }, "autoMergeRequest": null' -LastQueueEvent '{ "__typename": "AddedToMergeQueueEvent" }'
            $a = & $script:ScriptPath -Response $json -PassThru
            $a.Reason | Should -Be 'Waiting'
            $a.Action | Should -Match 'position 1'
        }

        It 'reads a removal for failed checks, with the merge_group checks that failed' {
            $json = New-QueueResponse -QueueFields '"isInMergeQueue": false, "mergeQueueEntry": null, "autoMergeRequest": null' -LastQueueEvent $script:FailedRemoval
            $a = & $script:ScriptPath -Response $json -PassThru
            $a.Reason | Should -Be 'RemovedFromQueue'
            $a.Checks.Name | Should -Be @('Analyze (csharp)')
            $a.Checks[0].Url | Should -Be 'https://x/mg1'
        }

        It 'stops reporting a removal once auto-merge is armed again' {
            $json = New-QueueResponse -QueueFields '"isInMergeQueue": false, "mergeQueueEntry": null, "autoMergeRequest": { "enabledAt": "2026-10-13T12:00:00Z" }' -LastQueueEvent $script:FailedRemoval
            # Armed and green, GitHub re-queues it at once; if it stays BLOCKED
            # instead, Blocked (an unsigned commit, say) is the right report.
            (& $script:ScriptPath -Response $json -PassThru).Reason | Should -Not -Be 'RemovedFromQueue'
        }

        It 'keeps waiting when it was added back to the queue after a removal' {
            $json = New-QueueResponse -QueueFields '"isInMergeQueue": false, "mergeQueueEntry": null, "autoMergeRequest": null' -LastQueueEvent '{ "__typename": "AddedToMergeQueueEvent" }'
            # Added, and not yet showing as queued: treat it as queued, not as Blocked or removed.
            (& $script:ScriptPath -Response $json -PassThru).Reason | Should -Be 'Waiting'
        }
    }
}

Describe 'Watch-PullRequest: reading the REST responses (when GraphQL is refused)' {
    BeforeAll {
        function New-Rest {
            param(
                [string]$State = 'open', [bool]$Merged = $false, [string]$MergeableState = 'blocked',
                $CheckRuns = @(), $Statuses = @(), $Threads = @(), $Comments = @{}
            )
            @{
                Pull      = [pscustomobject]@{ state = $State; merged = $Merged; mergeable_state = $MergeableState }
                CheckRuns = @($CheckRuns | ForEach-Object { [pscustomobject]$_ })
                Statuses  = @($Statuses | ForEach-Object { [pscustomobject]$_ })
                Threads   = @($Threads | ForEach-Object { [pscustomobject]$_ })
                Comments  = $Comments
            }
        }
        function Invoke-Rest { param($Rest, [string[]]$Acknowledged = @()) & $script:ScriptPath -RestResponse $Rest -AcknowledgedThreadIds $Acknowledged -PassThru }
    }

    It 'reads a merged pull request as Merged' {
        (Invoke-Rest (New-Rest -State closed -Merged $true -MergeableState unknown)).Reason | Should -Be 'Merged'
    }

    It 'reads a closed, unmerged pull request as Closed' {
        (Invoke-Rest (New-Rest -State closed -MergeableState blocked)).Reason | Should -Be 'Closed'
    }

    It 'reads a failed check run, in lower case as REST gives it' {
        $rest = New-Rest -CheckRuns @(@{ name = 'Analyze (csharp)'; status = 'completed'; conclusion = 'failure'; started_at = '2026-10-13T10:00:00Z'; html_url = 'https://x/1' })
        $a = Invoke-Rest $rest
        $a.Reason | Should -Be 'ChecksFailed'
        $a.Checks.Name | Should -Be @('Analyze (csharp)')
    }

    It 'reads a pending commit status as still running' {
        $rest = New-Rest -MergeableState blocked -CheckRuns @(@{ name = 'build-and-test'; status = 'completed'; conclusion = 'success'; started_at = '2026-10-13T10:00:00Z'; html_url = 'https://x/1' }) -Statuses @(@{ context = 'legacy'; state = 'pending'; created_at = '2026-10-13T10:00:00Z'; target_url = 'https://x/2' })
        (Invoke-Rest $rest).Reason | Should -Be 'Waiting'
    }

    It 'reads an unresolved thread, with its first comment''s author and text' {
        $rest = New-Rest -Threads @(
            @{ resolved = $false; outdated = $false; path = 'docs/a.md'; line = 37; comment_ids = @(4252088407, 4252099999) },
            @{ resolved = $true; outdated = $false; path = 'docs/a.md'; line = 39; comment_ids = @(4252088576) }
        ) -Comments @{ '4252088407' = [pscustomobject]@{ user = [pscustomobject]@{ login = 'claude[bot]' }; body = '**ACC-4** x'; html_url = 'https://x/c' } }
        $a = Invoke-Rest $rest
        $a.Reason | Should -Be 'UnresolvedThreads'
        $a.Threads.Id | Should -Be @('4252088407')
        $a.Threads[0].Author | Should -Be 'claude[bot]'
        $a.Threads[0].Line | Should -Be 37
    }

    It 'honours an acknowledged thread by its first comment''s id' {
        $rest = New-Rest -MergeableState blocked -CheckRuns @(@{ name = 'build-and-test'; status = 'in_progress'; conclusion = $null; started_at = '2026-10-13T10:00:00Z'; html_url = 'https://x/1' }) -Threads @(@{ resolved = $false; outdated = $false; path = 'docs/a.md'; line = 37; comment_ids = @(4252088407) })
        (Invoke-Rest $rest -Acknowledged '4252088407').Reason | Should -Be 'Waiting'
    }

    It 'reads mergeable_state behind as still waiting' {
        $rest = New-Rest -MergeableState behind -CheckRuns @(@{ name = 'build-and-test'; status = 'completed'; conclusion = 'success'; started_at = '2026-10-13T10:00:00Z'; html_url = 'https://x/1' })
        (Invoke-Rest $rest).Reason | Should -Be 'Waiting'
    }

    Context 'the merge queue' {
        BeforeAll {
            $script:GreenRun = @{ name = 'build-and-test'; status = 'completed'; conclusion = 'success'; started_at = '2026-10-13T10:00:00Z'; html_url = 'https://x/1' }
            function New-QueueRest {
                param([string[]]$Events, $AutoMerge = $null, $MergeGroupCheckRuns = @())
                $rest = New-Rest -MergeableState blocked -CheckRuns @($script:GreenRun)
                $rest.Pull | Add-Member -NotePropertyName auto_merge -NotePropertyValue $AutoMerge
                $rest.Timeline = @($Events | ForEach-Object { [pscustomobject]@{ event = $_; created_at = '2026-10-13T11:00:00Z' } })
                $rest.MergeGroupCheckRuns = @($MergeGroupCheckRuns | ForEach-Object { [pscustomobject]$_ })
                $rest
            }
        }

        It 'reads a pull request last added to the queue as waiting, not Blocked' {
            (Invoke-Rest (New-QueueRest -Events 'committed', 'added_to_merge_queue')).Reason | Should -Be 'Waiting'
        }

        It 'reads a removal with the merge_group checks that failed' {
            $rest = New-QueueRest -Events 'added_to_merge_queue', 'removed_from_merge_queue' -MergeGroupCheckRuns @(
                @{ name = 'changelog-lint'; status = 'completed'; conclusion = 'failure'; started_at = '2026-10-13T11:00:00Z'; html_url = 'https://x/mg' },
                $script:GreenRun)
            $a = Invoke-Rest $rest
            $a.Reason | Should -Be 'RemovedFromQueue'
            $a.Checks.Name | Should -Be @('changelog-lint')
        }

        It 'stops reporting a removal once auto-merge is armed again' {
            $rest = New-QueueRest -Events 'added_to_merge_queue', 'removed_from_merge_queue' -AutoMerge ([pscustomobject]@{ merge_method = 'squash' })
            (Invoke-Rest $rest).Reason | Should -Not -Be 'RemovedFromQueue'
        }
    }
}
