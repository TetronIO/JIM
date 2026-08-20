# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the shared "Step X of Y: Name" sentence.

.DESCRIPTION
    Three surfaces say where a run or a Schedule has got to: the portal, the REST reads, and this
    module (#1162). They had three ways of building the sentence, and the module built it inline
    inside the Write-Progress helper, where nothing else could reach it. One helper now builds it for
    Get-JIMWorkerTask, Get-JIMScheduleExecution and the live progress display alike.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force

    # The helper is private to the module, so invoke it in the module's own scope.
    $script:JimModule = Get-Module JIM

    function Invoke-StepDisplay {
        param($StepNumber, $TotalSteps, $Name)

        & $script:JimModule {
            param($n, $t, $s)
            Get-JIMStepPositionDisplay -StepNumber $n -TotalSteps $t -StepName $s
        } $StepNumber $TotalSteps $Name
    }
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMStepPositionDisplay' {

    Context 'The sentence' {

        It 'Should read the same as the portal and the live progress display' {
            Invoke-StepDisplay -StepNumber 3 -TotalSteps 7 -Name 'Saving changes' |
                Should -Be 'Step 3 of 7: Saving changes'
        }

        It 'Should give the position alone when there is no name for the step' {
            # A Schedule Execution knows which step group it is on without knowing what to call it.
            Invoke-StepDisplay -StepNumber 2 -TotalSteps 5 -Name $null | Should -Be 'Step 2 of 5'
        }

        It 'Should give the name alone when the position is unknown' {
            # An older server reports a running step without a position; the name still says more
            # than nothing, and inventing a position would be worse than omitting it.
            Invoke-StepDisplay -StepNumber $null -TotalSteps 0 -Name 'Saving changes' |
                Should -Be 'Saving changes'
        }

        It 'Should say nothing at all when neither is known' {
            # A task that is not a Run Profile execution records no steps, and must not display an
            # empty "Step of" skeleton.
            Invoke-StepDisplay -StepNumber $null -TotalSteps 0 -Name $null | Should -BeNullOrEmpty
        }

        It 'Should not report a position it cannot place against a total' {
            Invoke-StepDisplay -StepNumber 3 -TotalSteps 0 -Name 'Saving changes' |
                Should -Be 'Saving changes'
        }
    }
}
