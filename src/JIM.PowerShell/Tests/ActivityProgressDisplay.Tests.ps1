# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the shared Write-Progress display used by Get-JIMActivity -Follow and
    Start-JIMRunProfile -Wait.

.DESCRIPTION
    A run's object counter restarts between steps, so without the step an operator watching a
    scripted run sees the bar refill from zero with no explanation (#454). These tests cover how the
    step reaches Write-Progress, and that a server which reports no steps still displays sensibly.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force

    # The helper is private to the module, so invoke it in the module's own scope.
    $script:JimModule = Get-Module JIM

    function Invoke-ProgressDisplay {
        param($Progress, [string]$ActivityLabel = 'Full Import', [int]$ElapsedSeconds = -1)

        & $script:JimModule {
            param($p, $label, $elapsed)
            Get-JIMActivityProgressDisplay -Progress $p -ActivityLabel $label -ElapsedSeconds $elapsed
        } $Progress $ActivityLabel $ElapsedSeconds
    }

    function New-ProgressSnapshot {
        param(
            [string]$Status = 'InProgress',
            [int]$ObjectsProcessed = 145,
            [int]$ObjectsToProcess = 500,
            [string]$Message = 'Imported 145 objects',
            $CurrentPhase = $null,
            $CurrentPhaseNumber = $null,
            [int]$TotalPhases = 0
        )

        [PSCustomObject]@{
            status             = $Status
            objectsProcessed   = $ObjectsProcessed
            objectsToProcess   = $ObjectsToProcess
            percentComplete    = 29
            message            = $Message
            currentPhase       = $CurrentPhase
            currentPhaseNumber = $CurrentPhaseNumber
            totalPhases        = $TotalPhases
        }
    }
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMActivityProgressDisplay' {

    Context 'Run steps' {

        It 'Shows the current step and its position in the run' {
            $snapshot = New-ProgressSnapshot -CurrentPhase ([PSCustomObject]@{ name = 'Saving changes' }) `
                -CurrentPhaseNumber 5 -TotalPhases 7

            $result = Invoke-ProgressDisplay -Progress $snapshot

            $result.Status | Should -BeLike '*Step 5 of 7: Saving changes*'
        }

        It 'Shows the step name alone when the server reports no position' {
            $snapshot = New-ProgressSnapshot -CurrentPhase ([PSCustomObject]@{ name = 'Saving changes' })

            $result = Invoke-ProgressDisplay -Progress $snapshot

            $result.Status | Should -BeLike '*Saving changes*'
            $result.Status | Should -Not -BeLike '*Step *of*'
        }

        It 'Keeps the message when it says more than the step name does' {
            $snapshot = New-ProgressSnapshot -Message 'Parsed 50,000 rows...' `
                -CurrentPhase ([PSCustomObject]@{ name = 'Reading the file' }) -CurrentPhaseNumber 2 -TotalPhases 7

            $result = Invoke-ProgressDisplay -Progress $snapshot

            $result.Status | Should -BeLike '*Step 2 of 7: Reading the file*'
            $result.Status | Should -BeLike '*Parsed 50,000 rows...*'
        }

        It 'Does not repeat the step name when the message is only the step name' {
            $snapshot = New-ProgressSnapshot -Message 'Saving changes' `
                -CurrentPhase ([PSCustomObject]@{ name = 'Saving changes' }) -CurrentPhaseNumber 5 -TotalPhases 7

            $result = Invoke-ProgressDisplay -Progress $snapshot

            ([regex]::Matches($result.Status, 'Saving changes')).Count | Should -Be 1
        }

        It 'Displays normally for a run that reports no steps at all' {
            # Runs that predate step recording, and Activities that are not Run Profile executions.
            $snapshot = New-ProgressSnapshot

            $result = Invoke-ProgressDisplay -Progress $snapshot

            $result.Status | Should -BeLike '*145 of 500 objects*'
            $result.Status | Should -BeLike '*Imported 145 objects*'
            $result.Status | Should -Not -BeLike '*Step *'
        }
    }

    Context 'Counts and completion' {

        It 'Reports the object counts and percentage complete' {
            $result = Invoke-ProgressDisplay -Progress (New-ProgressSnapshot)

            $result.Status | Should -BeLike '*145 of 500 objects*'
            $result.PercentComplete | Should -Be 29
        }

        It 'Falls back to elapsed time when the run has no countable total' {
            $snapshot = New-ProgressSnapshot -ObjectsProcessed 0 -ObjectsToProcess 0

            $result = Invoke-ProgressDisplay -Progress $snapshot -ElapsedSeconds 42

            $result.Status | Should -BeLike '*Elapsed: 42s*'
            $result.PercentComplete | Should -Be -1
        }
    }
}
