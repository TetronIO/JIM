# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Test-PreReleaseGate.ps1.

.DESCRIPTION
    The pre-release gate refuses to tag a commit unless its `jim-pre-release` commit status is `success`. Its decision
    and its gh paging are the shared core in scripts/ReleaseGate.ps1, which Test-AdLabReleaseGate.Tests.ps1 covers in
    depth (every state, several statuses per context, newest wins, paging, unreadable statuses). What is proven here
    is what this gate adds: it reads `jim-pre-release` and nothing else (a green `jim-ad-lab` on the same commit does
    not open it), its messages name pre-release.yml as the remedy, it has no override, its exit codes, and that it
    is wired into release.yml and agrees with the context pre-release.yml posts.

    `gh` is replaced by a PowerShell function, as in Test-AdLabReleaseGate.Tests.ps1 (see there for why its state
    lives in $global:).
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'The fake gh is a function the script under test calls, and $global: is the only scope both sides share.')]
param()

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Test-PreReleaseGate.ps1')).Path
    . $script:ScriptPath

    $script:Sha = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    $script:RunUrl = 'https://github.com/TetronIO/JIM/actions/runs/37965163978'
    $script:WorkflowsPath = Join-Path $PSScriptRoot '..' '..' '.github' 'workflows'

    function Get-FakeStatus {
        param(
            [string]$Context = 'jim-pre-release',
            [string]$State = 'success',
            [string]$Description = '53/53 scenario runs passed across 3 directory passes',
            [string]$TargetUrl = $script:RunUrl,
            [string]$UpdatedAt = '2026-10-09T19:05:00Z'
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

    # Serves a combined-status document for the commit, 100 statuses a page, and records every call.
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

Describe 'Test-PreReleaseStatus' {

    It 'passes only a success' {
        $result = Test-PreReleaseStatus -Statuses @(Get-FakeStatus) -Sha $script:Sha
        $result.Passed | Should -BeTrue
        $result.Message | Should -BeLike "jim-pre-release passed on $($script:Sha)*53/53*$($script:RunUrl)*"
    }

    It 'refuses state <State>, naming the state, the run and pre-release.yml as the remedy' -TestCases @(
        @{ State = 'failure' }, @{ State = 'error' }, @{ State = 'pending' }
    ) {
        $result = Test-PreReleaseStatus -Statuses @(Get-FakeStatus -State $State -Description '51/53 scenario runs passed') -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.State | Should -Be $State
        $result.Message | Should -BeLike "The jim-pre-release status for $($script:Sha) is $State, not success*"
        $result.Message | Should -BeLike '*51/53 scenario runs passed*'
        $result.Message | Should -BeLike "*$($script:RunUrl)*"
        $result.Message | Should -BeLike '*no override*fix the cause*pre-release.yml*'
        $result.Message | Should -Not -BeLike '*ad-lab*'
    }

    It 'refuses a commit with no jim-pre-release status, saying it is not reported and how a run starts' {
        $result = Test-PreReleaseStatus -Statuses @() -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.State | Should -Be 'not reported'
        $result.Message | Should -BeLike '*not reported*VERSION*pre-release.yml*'
    }

    It 'is not opened by a green Active Directory lab status, or any other context' {
        $statuses = @(
            (Get-FakeStatus -Context 'jim-ad-lab'),
            (Get-FakeStatus -Context 'ci/build'),
            (Get-FakeStatus -Context 'JIM-Pre-Release'),
            (Get-FakeStatus -Context 'jim-pre-release-old')
        )
        $result = Test-PreReleaseStatus -Statuses $statuses -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.State | Should -Be 'not reported'
    }

    It 'lets the newest jim-pre-release status win: a re-run that passed after a failure' {
        $statuses = @(
            (Get-FakeStatus -State 'failure' -UpdatedAt '2026-10-09T19:05:00Z'),
            (Get-FakeStatus -State 'success' -UpdatedAt '2026-10-10T09:00:00Z')
        )
        (Test-PreReleaseStatus -Statuses $statuses -Sha $script:Sha).Passed | Should -BeTrue
    }
}

Describe 'Test-PreReleaseGate.ps1' {

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
        $result.Output | Should -BeLike '*jim-pre-release passed on*'
        $global:FakeGhCalls[0] | Should -Be "api repos/TetronIO/JIM/commits/$($script:Sha)/status?per_page=100&page=1"
    }

    It 'exits 1 for a failure, printing the state, the run and the remedy' {
        $global:FakeGhStatuses = @(Get-FakeStatus -State 'failure' -Description '51/53 scenario runs passed')
        $result = Invoke-Gate @{ Sha = $script:Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*is failure, not success*'
        $result.Output | Should -BeLike "*$($script:RunUrl)*"
        $result.Output | Should -BeLike '*pre-release.yml*'
    }

    It 'exits 1, saying not reported, when only the Active Directory lab has passed the commit' {
        $global:FakeGhStatuses = @(Get-FakeStatus -Context 'jim-ad-lab')
        $result = Invoke-Gate @{ Sha = $script:Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*jim-pre-release status is not reported*'
    }

    It 'exits 1, and says so, when gh cannot read the status: the gate stays closed' {
        $global:FakeGhFail = 'gh: Not Found (HTTP 404)'
        $result = Invoke-Gate @{ Sha = $script:Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*could not read the commit status*Not Found*'
    }

    It 'exits 1 for a SHA that is not a full 40-character SHA, before calling gh' {
        $result = Invoke-Gate @{ Sha = 'main' }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*40-character SHA*'
        $global:FakeGhCalls.Count | Should -Be 0
    }

    It 'annotates a refusal for the Actions log under its own title when it runs in Actions' {
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
        $result.Output | Should -BeLike '*::error title=Release gate: jim-pre-release::*'
    }
}

Describe 'the gate in the workflows' {

    BeforeAll {
        $script:Release = Get-Content -LiteralPath (Join-Path $script:WorkflowsPath 'release.yml') -Raw
        $script:PreRelease = Get-Content -LiteralPath (Join-Path $script:WorkflowsPath 'pre-release.yml') -Raw
    }

    It 'is a job of release.yml ahead of validate, and validate needs it' {
        $script:Release | Should -Match '(?m)^  jim-pre-release-gate:'
        $script:Release.IndexOf('  jim-pre-release-gate:') | Should -BeLessThan $script:Release.IndexOf('  validate:')
        $script:Release | Should -Match '(?ms)^  validate:.*?^    needs: \[?[^\n]*\bjim-pre-release-gate\b'
    }

    It 'enforces the gate only once JIM_PRE_RELEASE_GATE_ENFORCED is true, and warns on every release until then' {
        $gateJob = [regex]::Match($script:Release, '(?ms)^  jim-pre-release-gate:.*?(?=^  \S)').Value
        $gateJob | Should -Match "(?ms)if: vars\.JIM_PRE_RELEASE_GATE_ENFORCED == 'true'\s+shell: pwsh.*?Test-PreReleaseGate\.ps1"
        $gateJob | Should -Match "(?ms)if: vars\.JIM_PRE_RELEASE_GATE_ENFORCED != 'true'.*?::warning"
    }

    It 'reads the context pre-release.yml posts' {
        $script:PreRelease | Should -Match "context='$([regex]::Escape($script:PreReleaseContext))'"
    }

    It 'has pre-release.yml start by itself when a change to VERSION reaches main' {
        $script:PreRelease | Should -Match '(?ms)^on:.*?^  push:\s*\n\s+branches: \[ *"?main"? *\]\s*\n\s+paths: \[ *"?VERSION"? *\]'
    }
}
