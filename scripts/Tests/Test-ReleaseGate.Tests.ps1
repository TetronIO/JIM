# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Test-ReleaseGate.ps1, the release gates' table (scripts/ReleaseGates.psd1) and their place
    in release.yml.

.DESCRIPTION
    A release gate refuses to tag a commit unless a test run posted a `success` commit status of one fixed name on that
    exact SHA. Every gate is a row of scripts/ReleaseGates.psd1, and one script checks any row. Four things are proven:

      - the table: every row is complete, names a workflow that posts its status, and no context appears twice;
      - the decision (Test-ReleaseGateResult), over every state a status can be in and over a commit with several
        statuses for the context;
      - the script end to end, run in-process against a fake `gh` that serves a commit's statuses, so the exit code
        (the contract release.yml and the /release skill rely on) is asserted directly, for every row;
      - release.yml: one gate job, a matrix over exactly the table's rows, which validate needs. That is what keeps the
        -Context parameter from being an override: the script accepts only the table's contexts, and the release
        workflow checks every one of them.

    `gh` is replaced by a PowerShell function, which beats an external command in command resolution and is inherited
    by the script under test, so the script needs no seam of its own. The fake reports failure the way a native
    command does (a non-zero $LASTEXITCODE and a message), never as a PowerShell error record. Its state lives in
    $global: because a function's $script: scope resolves against the script that is running it.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'The fake gh is a function the script under test calls, and $global: is the only scope both sides share (see the description).')]
param()

BeforeDiscovery {
    $script:GateRows = @((Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot '..' 'ReleaseGates.psd1')).Gates |
            ForEach-Object { @{ Context = $_.Context; Workflow = $_.Workflow; EnforcedVariable = $_.EnforcedVariable } })
}

BeforeAll {
    $script:RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Test-ReleaseGate.ps1')).Path
    . $script:ScriptPath
    $script:Gates = @((Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot '..' 'ReleaseGates.psd1')).Gates)

    $script:Sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $script:RunUrl = 'https://github.com/TetronIO/JIM/actions/runs/424242'

    function Get-FakeStatus {
        param(
            [string]$Context = 'jim-ad-lab',
            [string]$State = 'success',
            [string]$Description = '23/23 scenarios passed',
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

    function Get-ReleaseWorkflow {
        return Get-Content -LiteralPath (Join-Path $script:RepositoryRoot '.github' 'workflows' 'release.yml') -Raw
    }
}

AfterAll {
    Remove-Item -Path function:global:gh -ErrorAction SilentlyContinue
    Remove-Variable -Name FakeGhCalls, FakeGhStatuses, FakeGhFail -Scope Global -ErrorAction SilentlyContinue
}

Describe 'the gate table' {

    It 'lists every gate a release needs' {
        @($script:Gates.Context) | Should -Contain 'jim-ad-lab'
        @($script:Gates.Context) | Should -Contain 'jim-ad-lab-adfs'
        @($script:Gates.Context) | Should -Contain 'jim-pre-release'
        @($script:Gates.Context) | Should -Contain 'jim-deploy-lab'
    }

    It 'names each context once' {
        @($script:Gates.Context | Group-Object | Where-Object Count -gt 1) | Should -BeNullOrEmpty
        @($script:Gates.EnforcedVariable | Group-Object | Where-Object Count -gt 1) | Should -BeNullOrEmpty
    }

    It '<Context> is complete' -TestCases $script:GateRows {
        $gate = Get-ReleaseGateDefinition -Context $Context
        foreach ($field in 'Name', 'Workflow', 'EnforcedVariable', 'Remedy', 'NotReportedReason', 'NotEnforcedMessage') {
            "$($gate[$field])" | Should -Not -BeNullOrEmpty -Because "$Context needs $field"
        }
        $gate.EnforcedVariable | Should -Match '^JIM_[A-Z0-9_]+_GATE_ENFORCED$'
        $gate.Remedy | Should -BeLike '*There is no override*'
        $gate.Remedy | Should -BeLike '*{0}*' -Because 'the remedy names the commit'
    }

    It '<Context> is posted by <Workflow>' -TestCases $script:GateRows {
        $path = Join-Path $script:RepositoryRoot '.github' 'workflows' $Workflow
        Test-Path -LiteralPath $path | Should -BeTrue
        Get-Content -LiteralPath $path -Raw | Should -Match ([regex]::Escape($Context))
    }

    It 'refuses a context that is not in the table, naming the ones that are' {
        { Get-ReleaseGateDefinition -Context 'jim-something-green' } | Should -Throw '*jim-ad-lab*'
    }
}

Describe 'Test-ReleaseGateResult' {

    It 'passes only a success' {
        $result = Test-ReleaseGateResult -Context 'jim-ad-lab' -Statuses @(Get-FakeStatus) -Sha $script:Sha
        $result.Passed | Should -BeTrue
        $result.State | Should -Be 'success'
    }

    It 'refuses state <State>, naming the state, the run and the remedy' -TestCases @(
        @{ State = 'failure' }, @{ State = 'error' }, @{ State = 'pending' }
    ) {
        $result = Test-ReleaseGateResult -Context 'jim-ad-lab' -Statuses @(Get-FakeStatus -State $State) -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.Message | Should -BeLike "*is $State, not success*"
        $result.Message | Should -BeLike "*$($script:RunUrl)*"
        $result.Message | Should -BeLike "*There is no override*$($script:Sha)*"
    }

    It 'refuses a commit with no status for the context, saying it is not reported' {
        $result = Test-ReleaseGateResult -Context 'jim-ad-lab' -Statuses @(Get-FakeStatus -Context 'ci/build') -Sha $script:Sha
        $result.Passed | Should -BeFalse
        $result.Message | Should -BeLike '*not reported*'
    }

    It 'refuses when the statuses are null' {
        (Test-ReleaseGateResult -Context 'jim-ad-lab' -Statuses $null -Sha $script:Sha).Passed | Should -BeFalse
    }

    It 'ignores other contexts, even green ones, including the other gates' {
        $statuses = @(
            (Get-FakeStatus -Context 'ci/build' -State 'success'),
            (Get-FakeStatus -Context 'jim-ad-lab-rebuild' -State 'success'),
            (Get-FakeStatus -Context 'jim-ad-lab-adfs' -State 'success'),
            (Get-FakeStatus -Context 'jim-ad-lab-old' -State 'success')
        )
        (Test-ReleaseGateResult -Context 'jim-ad-lab' -Statuses $statuses -Sha $script:Sha).Passed | Should -BeFalse
    }

    It 'matches the context exactly, including case' {
        (Test-ReleaseGateResult -Context 'jim-ad-lab' -Statuses @((Get-FakeStatus -Context 'JIM-AD-Lab')) -Sha $script:Sha).Passed | Should -BeFalse
        (Test-ReleaseGateResult -Context 'jim-ad-lab' -Statuses @((Get-FakeStatus -Context 'jim-ad-lab ')) -Sha $script:Sha).Passed | Should -BeFalse
    }

    It 'lets a later failure override an earlier success on the same commit' {
        $statuses = @(
            (Get-FakeStatus -State 'success' -UpdatedAt '2026-09-29T02:41:00Z'),
            (Get-FakeStatus -State 'failure' -UpdatedAt '2026-09-29T09:15:00Z')
        )
        (Test-ReleaseGateResult -Context 'jim-ad-lab' -Statuses $statuses -Sha $script:Sha).Passed | Should -BeFalse
    }

    It 'lets a later success override an earlier failure (the dispatched re-run)' {
        $statuses = @(
            (Get-FakeStatus -State 'failure' -UpdatedAt '2026-09-29T02:41:00Z'),
            (Get-FakeStatus -State 'success' -UpdatedAt '2026-09-29T09:15:00Z')
        )
        (Test-ReleaseGateResult -Context 'jim-ad-lab' -Statuses $statuses -Sha $script:Sha).Passed | Should -BeTrue
    }

    It 'refuses a status with no state rather than passing it' {
        $status = Get-FakeStatus
        $status.state = $null
        (Test-ReleaseGateResult -Context 'jim-ad-lab' -Statuses @($status) -Sha $script:Sha).Passed | Should -BeFalse
    }

    It 'is strict about the state text: only exactly success passes' {
        foreach ($state in 'Success', 'SUCCESS', ' success', 'succeeded', 'passed') {
            (Test-ReleaseGateResult -Context 'jim-ad-lab' -Statuses @(Get-FakeStatus -State $state) -Sha $script:Sha).Passed | Should -BeFalse -Because "'$state' is not success"
        }
    }
}

Describe 'Test-ReleaseGate.ps1' {

    BeforeEach {
        $global:FakeGhCalls = New-Object System.Collections.ArrayList
        $global:FakeGhStatuses = @()
        $global:FakeGhFail = $null
    }

    It 'has no override: its only parameters are the gate, the commit and the repository' {
        $parameters = @((Get-Command $script:ScriptPath).Parameters.Keys | Where-Object { $_ -notin [System.Management.Automation.PSCmdlet]::CommonParameters })
        ($parameters | Sort-Object) | Should -Be @('Context', 'Repository', 'Sha')
    }

    It 'exits 0 for a success of <Context>' -TestCases $script:GateRows {
        $global:FakeGhStatuses = @(Get-FakeStatus -Context $Context)
        $result = Invoke-Gate @{ Context = $Context; Sha = $script:Sha }
        $result.ExitCode | Should -Be 0
        $result.Output | Should -BeLike "*$Context passed on*"
    }

    It 'exits 1 for a failure of <Context>, printing the run and its remedy, which names <Workflow>' -TestCases $script:GateRows {
        $global:FakeGhStatuses = @(Get-FakeStatus -Context $Context -State 'failure')
        $result = Invoke-Gate @{ Context = $Context; Sha = $script:Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*is failure, not success*'
        $result.Output | Should -BeLike "*$($script:RunUrl)*"
        $result.Output | Should -BeLike "*$Workflow*"
    }

    It 'exits 1 for a context that is not a gate, before calling gh' {
        $global:FakeGhStatuses = @(Get-FakeStatus -Context 'ci/build')
        $result = Invoke-Gate @{ Context = 'ci/build'; Sha = $script:Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*not a release gate*'
        $global:FakeGhCalls.Count | Should -Be 0
    }

    It 'exits 1 for a pending run, which is not a pass' {
        $global:FakeGhStatuses = @(Get-FakeStatus -State 'pending')
        (Invoke-Gate @{ Context = 'jim-ad-lab'; Sha = $script:Sha }).ExitCode | Should -Be 1
    }

    It 'exits 1, saying not reported, for a commit the run has not tested' {
        $global:FakeGhStatuses = @(Get-FakeStatus -Context 'ci/build')
        $result = Invoke-Gate @{ Context = 'jim-ad-lab'; Sha = $script:Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*not reported*'
    }

    It 'asks GitHub for that commit in that repository, and takes the repository as a parameter' {
        $global:FakeGhStatuses = @(Get-FakeStatus)
        $null = Invoke-Gate @{ Context = 'jim-ad-lab'; Sha = $script:Sha }
        $global:FakeGhCalls[0] | Should -Be "api repos/TetronIO/JIM/commits/$($script:Sha)/status?per_page=100&page=1"

        $global:FakeGhCalls.Clear()
        $null = Invoke-Gate @{ Context = 'jim-ad-lab'; Sha = $script:Sha; Repository = 'Example/Fork' }
        $global:FakeGhCalls[0] | Should -Be "api repos/Example/Fork/commits/$($script:Sha)/status?per_page=100&page=1"
    }

    It 'finds the status on a later page of a commit with more than 100 statuses' {
        $filler = @(1..100 | ForEach-Object { Get-FakeStatus -Context "ci/check-$_" -State 'success' })
        $global:FakeGhStatuses = $filler + @(Get-FakeStatus)
        $result = Invoke-Gate @{ Context = 'jim-ad-lab'; Sha = $script:Sha }
        $result.ExitCode | Should -Be 0
        $global:FakeGhCalls.Count | Should -Be 2
        $global:FakeGhCalls[1] | Should -BeLike '*page=2'
    }

    It 'exits 1, and says so, when gh cannot read the status: the gate stays closed' {
        $global:FakeGhFail = 'gh: Not Found (HTTP 404)'
        $result = Invoke-Gate @{ Context = 'jim-ad-lab'; Sha = $script:Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*could not read the commit status*'
        $result.Output | Should -BeLike '*Not Found*'
    }

    It 'exits 1 for a SHA that is not a full 40-character SHA: <Sha>' -TestCases @(
        @{ Sha = 'main' }, @{ Sha = 'abc1234' }, @{ Sha = ('a' * 39) }, @{ Sha = ('a' * 41) }, @{ Sha = ('g' * 40) }, @{ Sha = '' }
    ) {
        $global:FakeGhStatuses = @(Get-FakeStatus)
        $result = Invoke-Gate @{ Context = 'jim-ad-lab'; Sha = $Sha }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*40-character SHA*'
        $global:FakeGhCalls.Count | Should -Be 0
    }

    It 'exits 1 for a repository that is not OWNER/NAME, before calling gh' {
        $result = Invoke-Gate @{ Context = 'jim-ad-lab'; Sha = $script:Sha; Repository = 'TetronIO/JIM; rm -rf /' }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*OWNER/NAME*'
        $global:FakeGhCalls.Count | Should -Be 0
    }

    It 'annotates a refusal for the Actions log when it runs in Actions' {
        $global:FakeGhStatuses = @(Get-FakeStatus -Context 'jim-deploy-lab' -State 'failure')
        $previous = $env:GITHUB_ACTIONS
        try {
            $env:GITHUB_ACTIONS = 'true'
            $result = Invoke-Gate @{ Context = 'jim-deploy-lab'; Sha = $script:Sha }
        }
        finally {
            $env:GITHUB_ACTIONS = $previous
        }
        $result.ExitCode | Should -Be 1
        $result.Output | Should -BeLike '*::error title=Release gate: jim-deploy-lab::*'
    }

    It 'prints nothing to annotate outside Actions' {
        $global:FakeGhStatuses = @(Get-FakeStatus -State 'failure')
        $previous = $env:GITHUB_ACTIONS
        try {
            $env:GITHUB_ACTIONS = $null
            $result = Invoke-Gate @{ Context = 'jim-ad-lab'; Sha = $script:Sha }
        }
        finally {
            $env:GITHUB_ACTIONS = $previous
        }
        $result.Output | Should -Not -BeLike '*::error*'
    }
}

Describe 'Write-ReleaseGateNotEnforced' {

    It 'warns, naming the variable, and adds the warning to the run summary' {
        $summary = Join-Path ([System.IO.Path]::GetTempPath()) "gate-summary-$([guid]::NewGuid()).md"
        $previous = $env:GITHUB_STEP_SUMMARY
        try {
            $env:GITHUB_STEP_SUMMARY = $summary
            $output = Write-ReleaseGateNotEnforced -Context 'jim-pre-release' *>&1 | Out-String
        }
        finally {
            $env:GITHUB_STEP_SUMMARY = $previous
        }
        $output | Should -BeLike '*::warning title=Pre-release integration gate not enforced::*JIM_PRE_RELEASE_GATE_ENFORCED*'
        Get-Content -LiteralPath $summary -Raw | Should -BeLike '*### Pre-release integration gate not enforced*'
        Remove-Item -LiteralPath $summary -Force
    }
}

Describe 'the gates in the release workflow' {

    It 'has one gate job, which validate needs, before validate' {
        $workflow = Get-ReleaseWorkflow
        $workflow | Should -Match '(?m)^  release-gates:'
        $workflow.IndexOf('  release-gates:') | Should -BeLessThan $workflow.IndexOf('  validate:')
        $workflow | Should -Match '(?ms)^  validate:.*?^    needs: \[?[^\n]*\brelease-gates\b'
    }

    It 'runs the gate job for exactly the table''s gates, with their variables' {
        $gateJob = [regex]::Match((Get-ReleaseWorkflow), '(?ms)^  release-gates:.*?(?=^  \S)').Value
        $listed = @([regex]::Matches($gateJob, '(?m)^\s+- context: (\S+)\s*\r?\n\s+variable: (\S+)') |
                ForEach-Object { "$($_.Groups[1].Value)=$($_.Groups[2].Value)" })
        $expected = @($script:Gates | ForEach-Object { "$($_.Context)=$($_.EnforcedVariable)" })
        ($listed | Sort-Object) | Should -Be ($expected | Sort-Object)
        $gateJob | Should -Match 'fail-fast: false'
    }

    It 'enforces each gate only once its variable is true, and warns on every release until then' {
        $gateJob = [regex]::Match((Get-ReleaseWorkflow), '(?ms)^  release-gates:.*?(?=^  \S)').Value
        $gateJob | Should -Match "(?ms)if: vars\[matrix\.variable\] == 'true'\s+shell: pwsh.*?Test-ReleaseGate\.ps1 -Context"
        $gateJob | Should -Match "(?ms)if: vars\[matrix\.variable\] != 'true'.*?Write-ReleaseGateNotEnforced"
    }

    It 'leaves no per-gate script behind anywhere in the repository' {
        $old = 'Test-AdLabReleaseGate', 'Test-AdLabFederationReleaseGate', 'Test-PreReleaseGate', 'Test-DeployLabReleaseGate'
        foreach ($name in $old) {
            Test-Path -LiteralPath (Join-Path $script:RepositoryRoot 'scripts' "$name.ps1") | Should -BeFalse -Because "$name is a row of ReleaseGates.psd1 now"
        }
        $pattern = ($old | ForEach-Object { [regex]::Escape($_) }) -join '|'
        # This file names them to look for them, so it is left out.
        $hits = @(git -C $script:RepositoryRoot grep -l -E $pattern -- . ':!engineering/plans/done' ':!engineering/prd/done' ':!CHANGELOG.md' ':!scripts/Tests/Test-ReleaseGate.Tests.ps1' 2>$null)
        $hits | Should -BeNullOrEmpty -Because 'nothing may still call a per-gate script'
    }
}
