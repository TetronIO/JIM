# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Test-DeployLabReleaseGate.ps1.

.DESCRIPTION
    The deployment lab gate refuses to tag a commit unless its `jim-deploy-lab` commit status is `success`. Its
    decision and its gh paging are the shared core in scripts/ReleaseGate.ps1, which Test-AdLabReleaseGate.Tests.ps1
    covers in depth (every state, several statuses per context, newest wins, paging, unreadable statuses). What is
    proven here is what this gate adds: it reads `jim-deploy-lab` and nothing else (a green `jim-pre-release` or
    `jim-ad-lab` on the same commit does not open it), its messages name deploy-lab.yml as the remedy, it has no
    override, its exit codes, and that it is wired into release.yml and agrees with the context deploy-lab.yml posts.

    `gh` is replaced by a PowerShell function, as in Test-AdLabReleaseGate.Tests.ps1 (see there for why its state
    lives in $global:).
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'The fake gh is a function the script under test calls, and $global: is the only scope both sides share.')]
param()

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Test-DeployLabReleaseGate.ps1')).Path
    . $script:ScriptPath

    $script:Sha = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    $script:RunUrl = 'https://github.com/TetronIO/JIM/actions/runs/37965163978'
    $script:WorkflowsPath = Join-Path $PSScriptRoot '..' '..' '.github' 'workflows'

    function Get-FakeStatus {
        param(
            [string]$Context = 'jim-deploy-lab',
            [string]$State = 'success',
            [string]$Description = '19 of 19 cells passed',
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

Describe 'Test-DeployLabStatus' {

    It 'passes only a success' {
        $result = Test-DeployLabStatus -Statuses @(Get-FakeStatus) -Sha $script:Sha
        $result.Passed | Should -BeTrue
        $result.Message | Should -BeLike "jim-deploy-lab passed on $($script:Sha)*19 of 19*$($script:RunUrl)*"
    }

    It 'refuses state <State>, naming the state, the run and deploy-lab.yml as the remedy' -TestCases @(
        @{ State = 'failure' }, @{ State = 'error' }, @{ State = 'pending' }
    ) {
        $result = Test-DeployLabStatus -Statuses @(Get-FakeStatus -State $State -Description '18 of 19 cells passed') -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.State | Should -Be $State
        $result.Message | Should -BeLike "The jim-deploy-lab status for $($script:Sha) is $State, not success*"
        $result.Message | Should -BeLike '*18 of 19 cells passed*'
        $result.Message | Should -BeLike "*$($script:RunUrl)*"
        $result.Message | Should -BeLike '*no override*fix the cause*deploy-lab.yml*'
        $result.Message | Should -Not -BeLike '*pre-release.yml*'
    }

    It 'refuses a commit with no jim-deploy-lab status, saying it is not reported and how a run starts' {
        $result = Test-DeployLabStatus -Statuses @() -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.State | Should -Be 'not reported'
        $result.Message | Should -BeLike '*not reported*VERSION*deploy-lab.yml*'
    }

    It 'is not opened by a green pre-release or Active Directory lab status, or any other context' {
        $statuses = @(
            (Get-FakeStatus -Context 'jim-pre-release'),
            (Get-FakeStatus -Context 'jim-ad-lab'),
            (Get-FakeStatus -Context 'ci/build'),
            (Get-FakeStatus -Context 'JIM-Deploy-Lab'),
            (Get-FakeStatus -Context 'jim-deploy-lab-old')
        )
        $result = Test-DeployLabStatus -Statuses $statuses -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.State | Should -Be 'not reported'
    }

    It 'lets the newest jim-deploy-lab status win: a re-run that passed after a failure' {
        $statuses = @(
            (Get-FakeStatus -State 'failure' -UpdatedAt '2026-10-09T19:05:00Z'),
            (Get-FakeStatus -State 'success' -UpdatedAt '2026-10-10T09:00:00Z')
        )
        (Test-DeployLabStatus -Statuses $statuses -Sha $script:Sha).Passed | Should -BeTrue
    }
}

Describe 'Test-DeployLabReleaseGate.ps1' {

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
        $result.Output | Should -BeLike '*jim-deploy-lab passed on*'
        $global:FakeGhCalls[0] | Should -Be "api repos/TetronIO/JIM/commits/$($script:Sha)/status?per_page=100&page=1"
    }

    It 'exits 1 for a failure, printing the state, the run and the remedy' {
        $global:FakeGhStatuses = @(Get-FakeStatus -State 'failure' -Description '18 of 19 cells passed')
        $result = Invoke-Gate @{ Sha = $script:Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*is failure, not success*'
        $result.Output | Should -BeLike "*$($script:RunUrl)*"
        $result.Output | Should -BeLike '*deploy-lab.yml*'
    }

    It 'exits 1, saying not reported, when only the pre-release suite has passed the commit' {
        $global:FakeGhStatuses = @(Get-FakeStatus -Context 'jim-pre-release')
        $result = Invoke-Gate @{ Sha = $script:Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*jim-deploy-lab status is not reported*'
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
        $result.Output | Should -BeLike '*::error title=Release gate: jim-deploy-lab::*'
    }
}

Describe 'the gate in the workflows' {

    BeforeAll {
        $script:Release = Get-Content -LiteralPath (Join-Path $script:WorkflowsPath 'release.yml') -Raw
        $script:DeployLab = Get-Content -LiteralPath (Join-Path $script:WorkflowsPath 'deploy-lab.yml') -Raw
    }

    It 'is a job of release.yml ahead of validate, and validate needs it' {
        $script:Release | Should -Match '(?m)^  jim-deploy-lab-gate:'
        $script:Release.IndexOf('  jim-deploy-lab-gate:') | Should -BeLessThan $script:Release.IndexOf('  validate:')
        $script:Release | Should -Match '(?ms)^  validate:.*?^    needs: \[?[^\n]*\bjim-deploy-lab-gate\b'
    }

    It 'enforces the gate only once JIM_DEPLOY_LAB_GATE_ENFORCED is true, and warns on every release until then' {
        $gateJob = [regex]::Match($script:Release, '(?ms)^  jim-deploy-lab-gate:.*?(?=^  \S)').Value
        $gateJob | Should -Match "(?ms)if: vars\.JIM_DEPLOY_LAB_GATE_ENFORCED == 'true'\s+shell: pwsh.*?Test-DeployLabReleaseGate\.ps1"
        $gateJob | Should -Match "(?ms)if: vars\.JIM_DEPLOY_LAB_GATE_ENFORCED != 'true'.*?::warning"
    }

    It 'reads the context deploy-lab.yml posts, which only a run of every cell posts' {
        # A run of some cells, chosen by name or tag, has not tested the commit, so it must not open the gate.
        $script:DeployLab | Should -Match "(?ms)if \(-not \`$env:CELLS -and -not \`$env:TAGS\)\s*\{\s*\`$context = '$([regex]::Escape($script:DeployLabContext))'"
        $script:DeployLab | Should -Match '-f context="\$context"'
    }

    It 'posts the online cells under a context of their own, which the gate never reads' {
        # The online cells test the published release, not the commit their run checked out.
        $script:DeployLab | Should -Match "(?m)\`$context = 'jim-deploy-lab-online'"
        'jim-deploy-lab-online' | Should -Not -Be $script:DeployLabContext
    }

    It 'has release.yml start the online cells once a stable release is published' {
        $job = [regex]::Match($script:Release, '(?ms)^  deploy-lab-online:.*?(?=^  \S|\z)').Value
        $job | Should -Not -BeNullOrEmpty
        $job | Should -Match '(?m)^    needs: \[?[^\n]*\bcreate-release\b'
        $job | Should -Match "(?m)^    if: \$\{\{ !contains\(github\.ref, '-'\) \}\}"
        $job | Should -Match '(?m)^      actions: write'
        $job | Should -Match 'gh workflow run deploy-lab\.yml --ref main -f tags=online'
    }

    It 'has deploy-lab.yml start by itself when a change to VERSION reaches main, and nightly' {
        $script:DeployLab | Should -Match '(?ms)^on:.*?^  push:\s*\n\s+branches: \[ *"?main"? *\]\s*\n\s+paths: \[ *"?VERSION"? *\]'
        $script:DeployLab | Should -Match '(?ms)^on:.*?^  schedule:\s*\n\s+- cron: '
    }

    It 'never runs deploy-lab.yml for a pull request' {
        $script:DeployLab | Should -Not -Match '(?m)^  pull_request(_target)?:'
    }
}
