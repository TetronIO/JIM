# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Test-AdLabReleaseGate.ps1.

.DESCRIPTION
    The release gate refuses to tag a commit unless its `jim-ad-lab` commit status is `success`. Two things are proven
    here: the decision (Test-AdLabStatus, a pure function, over every state a status can be in and over a commit
    with several statuses for the context), and the script end to end, run in-process against a fake `gh` that
    serves a commit's statuses, so the exit code (the contract release.yml and the /release skill rely on) is
    asserted directly.

    `gh` is replaced by a PowerShell function, which beats an external command in command resolution and is
    inherited by the script under test, so the script needs no seam of its own. The fake reports failure the way a
    native command does (a non-zero $LASTEXITCODE and a message), never as a PowerShell error record. Its state
    lives in $global: because a function's $script: scope resolves against the script that is running it.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'The fake gh is a function the script under test calls, and $global: is the only scope both sides share (see the description).')]
param()

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Test-AdLabReleaseGate.ps1')).Path
    . $script:ScriptPath

    $script:Sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $script:RunUrl = 'https://github.com/TetronIO/JIM/actions/runs/424242'

    function Get-FakeStatus {
        param(
            [string]$Context = 'jim-ad-lab',
            [string]$State = 'success',
            [string]$Description = '23/23 scenarios passed; DC build 26100.4652',
            [string]$TargetUrl = $script:RunUrl,
            [string]$UpdatedAt = '2026-09-29T02:41:00Z'
        )
        return [pscustomobject]@{
            context     = $Context
            state       = $State
            description = $Description
            target_url  = $TargetUrl
            created_at  = $UpdatedAt
            updated_at  = $UpdatedAt
        }
    }

    # Serves a combined-status document for the commit, paged the way GitHub pages it (100 statuses a page), and
    # records every call.
    function global:gh {
        $ghArgs = @($args)
        $global:FakeGhCalls.Add($ghArgs -join ' ') | Out-Null
        $global:LASTEXITCODE = 0

        if ($global:FakeGhFail) {
            $global:LASTEXITCODE = 1
            return $global:FakeGhFail
        }
        if (($ghArgs[0] -ne 'api') -or ($ghArgs[1] -notmatch '^repos/(?<repo>[^/]+/[^/]+)/commits/(?<sha>[0-9a-f]{40})/status\?per_page=100&page=(?<page>\d+)$')) {
            $global:LASTEXITCODE = 1
            return "unexpected gh call: $($ghArgs -join ' ')"
        }
        $page = [int]$Matches['page']
        $slice = @($global:FakeGhStatuses | Select-Object -Skip (($page - 1) * 100) -First 100)
        return ([ordered]@{
                state       = 'pending'
                sha         = $Matches['sha']
                total_count = @($global:FakeGhStatuses).Count
                statuses    = $slice
            } | ConvertTo-Json -Depth 5)
    }

    # Runs the script as release.yml does and returns its exit code and everything it printed.
    function Invoke-Gate {
        param([hashtable]$Arguments)

        $global:LASTEXITCODE = $null
        $output = & $script:ScriptPath @Arguments *>&1 | Out-String
        return [pscustomobject]@{ ExitCode = $global:LASTEXITCODE; Output = $output }
    }
}

AfterAll {
    Remove-Item -Path function:global:gh -ErrorAction SilentlyContinue
    Remove-Variable -Name FakeGhCalls, FakeGhStatuses, FakeGhFail -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Test-AdLabStatus' {

    It 'passes only a success' {
        $result = Test-AdLabStatus -Statuses @(Get-FakeStatus) -Sha $script:Sha
        $result.Passed | Should -BeTrue
        $result.State | Should -Be 'success'
        $result.Message | Should -BeLike "*passed on $($script:Sha)*23/23 scenarios passed*$($script:RunUrl)*"
    }

    It 'refuses state <State>, naming the state, the run and the remedy' -TestCases @(
        @{ State = 'failure' }, @{ State = 'error' }, @{ State = 'pending' }
    ) {
        $result = Test-AdLabStatus -Statuses @(Get-FakeStatus -State $State -Description '21/23 scenarios passed') -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.State | Should -Be $State
        $result.Message | Should -BeLike "*is $State, not success*"
        $result.Message | Should -BeLike "*$($script:RunUrl)*"
        $result.Message | Should -BeLike '*21/23 scenarios passed*'
        $result.Message | Should -BeLike '*dispatch ad-lab.yml*'
        $result.Message | Should -BeLike '*fix the cause*'
        $result.Message | Should -BeLike "*$($script:Sha)*"
    }

    It 'refuses a commit with no jim-ad-lab status, saying it is not reported' {
        $result = Test-AdLabStatus -Statuses @() -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.State | Should -Be 'not reported'
        $result.Message | Should -BeLike '*not reported*'
        $result.Message | Should -BeLike '*dispatch ad-lab.yml*'
    }

    It 'refuses when the statuses are null' {
        (Test-AdLabStatus -Statuses $null -Sha $script:Sha).Passed | Should -BeFalse
    }

    It 'ignores other contexts, even green ones' {
        $statuses = @(
            (Get-FakeStatus -Context 'ci/build' -State 'success'),
            (Get-FakeStatus -Context 'jim-ad-lab-rebuild' -State 'success'),
            (Get-FakeStatus -Context 'jim-ad-lab-old' -State 'success')
        )
        $result = Test-AdLabStatus -Statuses $statuses -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.State | Should -Be 'not reported'
    }

    It 'matches the context exactly, including case' {
        (Test-AdLabStatus -Statuses @((Get-FakeStatus -Context 'JIM-AD-Lab')) -Sha $script:Sha).Passed | Should -BeFalse
        (Test-AdLabStatus -Statuses @((Get-FakeStatus -Context 'jim-ad-lab ')) -Sha $script:Sha).Passed | Should -BeFalse
    }

    It 'finds the jim-ad-lab status among others' {
        $statuses = @((Get-FakeStatus -Context 'ci/build' -State 'failure'), (Get-FakeStatus), (Get-FakeStatus -Context 'other' -State 'error'))
        (Test-AdLabStatus -Statuses $statuses -Sha $script:Sha).Passed | Should -BeTrue
    }

    It 'lets a later failure override an earlier success on the same commit' {
        $statuses = @(
            (Get-FakeStatus -State 'success' -UpdatedAt '2026-09-29T02:00:00Z'),
            (Get-FakeStatus -State 'failure' -UpdatedAt '2026-09-29T09:00:00Z')
        )
        $result = Test-AdLabStatus -Statuses $statuses -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.State | Should -Be 'failure'
    }

    It 'lets a later success override an earlier failure (the dispatched re-run)' {
        $statuses = @(
            (Get-FakeStatus -State 'failure' -UpdatedAt '2026-09-29T02:00:00Z'),
            (Get-FakeStatus -State 'success' -UpdatedAt '2026-09-29T09:00:00Z')
        )
        (Test-AdLabStatus -Statuses $statuses -Sha $script:Sha).Passed | Should -BeTrue
    }

    It 'picks the newest whatever order the statuses arrive in' {
        $older = Get-FakeStatus -State 'success' -UpdatedAt '2026-09-29T02:00:00Z'
        $newer = Get-FakeStatus -State 'failure' -UpdatedAt '2026-09-29T09:00:00Z'
        (Test-AdLabStatus -Statuses @($older, $newer) -Sha $script:Sha).Passed | Should -BeFalse
        (Test-AdLabStatus -Statuses @($newer, $older) -Sha $script:Sha).Passed | Should -BeFalse
    }

    It 'copes with a status that has no description or run link' {
        $bare = [pscustomobject]@{ context = 'jim-ad-lab'; state = 'failure' }
        $result = Test-AdLabStatus -Statuses @($bare) -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.Message | Should -BeLike '*no run link was recorded*'
        (Test-AdLabStatus -Statuses @([pscustomobject]@{ context = 'jim-ad-lab'; state = 'success' }) -Sha $script:Sha).Passed | Should -BeTrue
    }

    It 'refuses a status with no state rather than passing it' {
        $result = Test-AdLabStatus -Statuses @([pscustomobject]@{ context = 'jim-ad-lab' }) -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.State | Should -Be 'unknown'
    }

    It 'takes statuses as hashtables too' {
        $result = Test-AdLabStatus -Statuses @(@{ context = 'jim-ad-lab'; state = 'success'; description = 'ok'; target_url = 'https://example.invalid/run' }) -Sha $script:Sha
        $result.Passed | Should -BeTrue
    }

    It 'is strict about the state text: only exactly success passes' {
        foreach ($state in 'Success', 'SUCCESS', 'succeeded', 'success ') {
            (Test-AdLabStatus -Statuses @((Get-FakeStatus -State $state)) -Sha $script:Sha).Passed | Should -BeFalse
        }
    }
}

Describe 'Test-AdLabReleaseGate.ps1' {

    BeforeEach {
        $global:FakeGhCalls = New-Object System.Collections.ArrayList
        $global:FakeGhStatuses = @()
        $global:FakeGhFail = $null
    }

    It 'has no override: its only parameters are the commit and the repository' {
        $parameters = @((Get-Command $script:ScriptPath).Parameters.Keys | Where-Object { $_ -notin [System.Management.Automation.PSCmdlet]::CommonParameters })
        ($parameters | Sort-Object) | Should -Be @('Repository', 'Sha')
    }

    It 'exits 0 for a success' {
        $global:FakeGhStatuses = @(Get-FakeStatus)
        $result = Invoke-Gate @{ Sha = $script:Sha }
        $result.ExitCode | Should -Be 0
        $result.Output | Should -BeLike '*passed on*'
    }

    It 'exits 1 for a failure, printing the state, the run and the remedy' {
        $global:FakeGhStatuses = @(Get-FakeStatus -State 'failure' -Description '20/23 scenarios passed')
        $result = Invoke-Gate @{ Sha = $script:Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*is failure, not success*'
        $result.Output | Should -BeLike "*$($script:RunUrl)*"
        $result.Output | Should -BeLike '*dispatch ad-lab.yml*'
    }

    It 'exits 1 for a pending run, which is not a pass' {
        $global:FakeGhStatuses = @(Get-FakeStatus -State 'pending')
        (Invoke-Gate @{ Sha = $script:Sha }).ExitCode | Should -Be 1
    }

    It 'exits 1, saying not reported, for a commit the lab has not run on' {
        $global:FakeGhStatuses = @(Get-FakeStatus -Context 'ci/build')
        $result = Invoke-Gate @{ Sha = $script:Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*not reported*'
    }

    It 'asks GitHub for that commit in that repository, and takes the repository as a parameter' {
        $global:FakeGhStatuses = @(Get-FakeStatus)
        $null = Invoke-Gate @{ Sha = $script:Sha }
        $global:FakeGhCalls[0] | Should -Be "api repos/TetronIO/JIM/commits/$($script:Sha)/status?per_page=100&page=1"

        $global:FakeGhCalls.Clear()
        $null = Invoke-Gate @{ Sha = $script:Sha; Repository = 'Example/Fork' }
        $global:FakeGhCalls[0] | Should -Be "api repos/Example/Fork/commits/$($script:Sha)/status?per_page=100&page=1"
    }

    It 'finds the jim-ad-lab status on a later page of a commit with more than 100 statuses' {
        $filler = @(1..100 | ForEach-Object { Get-FakeStatus -Context "ci/check-$_" -State 'success' })
        $global:FakeGhStatuses = $filler + @(Get-FakeStatus)
        $result = Invoke-Gate @{ Sha = $script:Sha }
        $result.ExitCode | Should -Be 0
        $global:FakeGhCalls.Count | Should -Be 2
        $global:FakeGhCalls[1] | Should -BeLike '*page=2'
    }

    It 'exits 1, and says so, when gh cannot read the status: the gate stays closed' {
        $global:FakeGhFail = 'gh: Not Found (HTTP 404)'
        $result = Invoke-Gate @{ Sha = $script:Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*could not read the commit status*'
        $result.Output | Should -BeLike '*Not Found*'
    }

    It 'exits 1 for a SHA that is not a full 40-character SHA: <Sha>' -TestCases @(
        @{ Sha = 'main' }, @{ Sha = 'abc1234' }, @{ Sha = ('a' * 39) }, @{ Sha = ('a' * 41) }, @{ Sha = ('g' * 40) }, @{ Sha = '' }
    ) {
        $global:FakeGhStatuses = @(Get-FakeStatus)
        $result = Invoke-Gate @{ Sha = $Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*40-character SHA*'
        $global:FakeGhCalls.Count | Should -Be 0
    }

    It 'exits 1 for a repository that is not OWNER/NAME, before calling gh' {
        $result = Invoke-Gate @{ Sha = $script:Sha; Repository = 'TetronIO/JIM; rm -rf /' }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*OWNER/NAME*'
        $global:FakeGhCalls.Count | Should -Be 0
    }

    It 'annotates a refusal for the Actions log when it runs in Actions' {
        $global:FakeGhStatuses = @(Get-FakeStatus -State 'failure')
        $previous = $env:GITHUB_ACTIONS
        try {
            $env:GITHUB_ACTIONS = 'true'
            $result = Invoke-Gate @{ Sha = $script:Sha }
        }
        finally {
            $env:GITHUB_ACTIONS = $previous
        }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*::error title=Release gate: jim-ad-lab::*'
    }

    It 'prints nothing to annotate outside Actions' {
        $global:FakeGhStatuses = @(Get-FakeStatus -State 'failure')
        $previous = $env:GITHUB_ACTIONS
        try {
            $env:GITHUB_ACTIONS = $null
            $result = Invoke-Gate @{ Sha = $script:Sha }
        }
        finally {
            $env:GITHUB_ACTIONS = $previous
        }
        $result.Output | Should -Not -BeLike '*::error*'
    }
}

Describe 'the gate in the release workflow' {

    It 'is the first job of release.yml, and validate needs it' {
        $workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..' '..' '.github' 'workflows' 'release.yml') -Raw
        $workflow | Should -Match '(?m)^  jim-ad-lab-gate:'
        $workflow.IndexOf('jim-ad-lab-gate:') | Should -BeLessThan $workflow.IndexOf('  validate:')
        $workflow | Should -Match '(?ms)^  validate:.*?^    needs: jim-ad-lab-gate'
        $workflow | Should -Match 'Test-AdLabReleaseGate\.ps1'
    }

    It 'enforces the gate only once JIM_AD_LAB_GATE_ENFORCED is true, and warns on every release until then' {
        $workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..' '..' '.github' 'workflows' 'release.yml') -Raw
        $gateJob = [regex]::Match($workflow, '(?ms)^  jim-ad-lab-gate:.*?(?=^  validate:)').Value
        $gateJob | Should -Match "(?ms)if: vars\.JIM_AD_LAB_GATE_ENFORCED == 'true'\s+shell: pwsh.*?Test-AdLabReleaseGate\.ps1"
        $gateJob | Should -Match "(?ms)if: vars\.JIM_AD_LAB_GATE_ENFORCED != 'true'.*?::warning"
    }
}
