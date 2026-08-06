# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMStepPositionDisplay {
    <#
    .SYNOPSIS
        Builds the "Step 3 of 7: Saving changes" sentence.

    .DESCRIPTION
        The one place this module composes a step position (#1162), shared by the live progress
        display, Get-JIMWorkerTask and Get-JIMScheduleExecution so that a run reads identically
        wherever it is being watched from, and identically to the portal.

        Both halves are optional and each degrades on its own terms: a Schedule Execution knows which
        step group it is on without knowing what to call it, and a run can name the step it is on
        without the server reporting a position. Neither is invented to complete the sentence.

    .PARAMETER StepNumber
        The step's 1-based position, or $null where it is unknown.

    .PARAMETER TotalSteps
        How many steps there are. Zero where it is unknown.

    .PARAMETER StepName
        What the step is called, or $null where it has no name.

    .OUTPUTS
        String; empty when nothing is known about the position.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        $StepNumber,

        [int]$TotalSteps = 0,

        [string]$StepName
    )

    $hasPosition = $null -ne $StepNumber -and $TotalSteps -gt 0
    $position = if ($hasPosition) { "Step $([int]$StepNumber) of $TotalSteps" } else { '' }

    if ($position -and $StepName) { return "${position}: $StepName" }
    if ($position) { return $position }
    if ($StepName) { return $StepName }

    return ''
}
