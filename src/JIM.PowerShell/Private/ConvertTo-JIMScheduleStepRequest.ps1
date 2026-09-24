# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function ConvertTo-JIMScheduleStepRequest {
    <#
    .SYNOPSIS
        Converts a Schedule step, as the API returns it, into the step body of a Schedule update request.

    .DESCRIPTION
        The Schedule update endpoint replaces the whole step list, so every cmdlet that changes one step has to send
        the others back. This is the one place that turns a returned step into a request step, so they all send the
        same thing:

        - The step's id, so the API updates the existing step rather than deleting it and creating a new one.
        - Its onFailure verbatim (FollowSchedule, Stop or Continue): the step's own setting.
        - Never continueOnFailure. On a read that is the step's EFFECTIVE behaviour (its own setting, or its
          Schedule's when it follows the Schedule), so resending it asks the API to reinterpret it; onFailure says
          exactly what the step is set to, and nothing is left to a legacy mapping (#1787).
        - Every other field of a step request, including the timeout and SQL settings, which some callers used to
          drop.

        Enum values pass through as the names the API returned; the API rejects numeric enum values (#1060).

    .PARAMETER Step
        A step object from a Schedule returned by the API (camelCase or the module's PascalCase; member access is
        case-insensitive).

    .PARAMETER StepIndex
        Overrides the step's index, for callers that renumber the steps they send back.

    .PARAMETER OnFailure
        Overrides the step's failure setting, for the caller that is changing it.

    .OUTPUTS
        A hashtable ready to include in the steps array of a Schedule update request.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory, ValueFromPipeline)]
        [PSObject]$Step,

        [Parameter()]
        [int]$StepIndex,

        [Parameter()]
        [ValidateSet('FollowSchedule', 'Stop', 'Continue')]
        [string]$OnFailure
    )

    process {
        @{
            id                  = $Step.id
            stepIndex           = if ($PSBoundParameters.ContainsKey('StepIndex')) { $StepIndex } else { [int]$Step.stepIndex }
            name                = $Step.name
            stepType            = $Step.stepType
            executionMode       = $Step.executionMode
            onFailure           = if ($PSBoundParameters.ContainsKey('OnFailure')) { $OnFailure } else { $Step.onFailure }
            timeoutSeconds      = $Step.timeoutSeconds
            connectedSystemId   = $Step.connectedSystemId
            runProfileId        = $Step.runProfileId
            scriptPath          = $Step.scriptPath
            arguments           = $Step.arguments
            executablePath      = $Step.executablePath
            workingDirectory    = $Step.workingDirectory
            sqlConnectionString = $Step.sqlConnectionString
            sqlScriptPath       = $Step.sqlScriptPath
        }
    }
}
