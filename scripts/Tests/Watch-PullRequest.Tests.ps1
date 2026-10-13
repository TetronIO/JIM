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
        param([string]$State = 'OPEN', [string]$MergeStateStatus = 'BLOCKED', $Checks = @(), $Threads = @())
        [pscustomobject]@{ State = $State; MergeStateStatus = $MergeStateStatus; Checks = @($Checks); Threads = @($Threads) }
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

    It 'reports Behind for a pull request behind main' {
        (Get-Attention (New-Snapshot -MergeStateStatus BEHIND -Checks @((New-Check build-and-test)))).Reason | Should -Be 'Behind'
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
    { "id": "PRRT_1", "isResolved": false, "comments": { "nodes": [ { "author": { "login": "claude" }, "path": "docs/a.md", "line": 7, "body": "**SAFE-1** x", "url": "https://x/3" } ] } }
  ] }
} } } }
'@ | ConvertFrom-Json
        $a = & $script:ScriptPath -Response $json -PassThru
        $a.Reason | Should -Be 'UnresolvedThreads'
        $a.Threads[0].Id | Should -Be 'PRRT_1'
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
}
