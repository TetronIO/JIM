# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Add-JIMWorkerTaskStepDisplay {
    <#
    .SYNOPSIS
        Adds a StepDisplay property to a Worker Task, naming the step its run is on.

    .DESCRIPTION
        The sentence the portal shows in the queue and Start-JIMRunProfile -Wait shows live (#1162),
        so a run reads the same wherever it is being watched from. A task that is not a Run Profile
        execution records no steps and gets an empty StepDisplay rather than no property, so the shape
        a script sees does not change with the kind of task it happens to be looking at.

    .PARAMETER Task
        A Worker Task object as returned by the API.

    .OUTPUTS
        The same object, with StepDisplay added.
    #>
    [CmdletBinding()]
    param($Task)

    if (-not $Task) { return $Task }

    $steps = $Task.steps
    $display = Get-JIMStepPositionDisplay `
        -StepNumber $steps.currentStepNumber `
        -TotalSteps ([int]($steps.totalSteps ?? 0)) `
        -StepName "$($steps.currentStepName)"

    $Task | Add-Member -NotePropertyName 'StepDisplay' -NotePropertyValue $display -Force -PassThru
}

function Add-JIMScheduleExecutionStepDisplay {
    <#
    .SYNOPSIS
        Adds a StepDisplay property to a Schedule Execution, naming the step group it has reached.

    .DESCRIPTION
        The detail read carries the execution's progress, including what each step group is called, so
        the sentence can name the step. The list and active reads carry only the recorded position, so
        there the sentence is the position alone rather than a name guessed from somewhere else.

    .PARAMETER Execution
        A Schedule Execution object as returned by the API.

    .OUTPUTS
        The same object, with StepDisplay added.
    #>
    [CmdletBinding()]
    param($Execution)

    if (-not $Execution) { return $Execution }

    $progress = $Execution.progress
    if ($progress) {
        $stepNumber = $progress.currentStepNumber
        $totalSteps = [int]($progress.totalSteps ?? 0)
        $stepName = if ($null -ne $stepNumber) {
            ($progress.steps | Where-Object { $_.stepIndex -eq ([int]$stepNumber - 1) } | Select-Object -First 1).name
        }
    }
    else {
        # CurrentStepIndex is 0-based and counts step groups, the same unit TotalSteps counts.
        $stepNumber = if ($null -ne $Execution.currentStepIndex) { [int]$Execution.currentStepIndex + 1 }
        $totalSteps = [int]($Execution.totalSteps ?? 0)
        $stepName = $null
    }

    $display = Get-JIMStepPositionDisplay -StepNumber $stepNumber -TotalSteps $totalSteps -StepName "$stepName"

    $Execution | Add-Member -NotePropertyName 'StepDisplay' -NotePropertyValue $display -Force -PassThru
}
