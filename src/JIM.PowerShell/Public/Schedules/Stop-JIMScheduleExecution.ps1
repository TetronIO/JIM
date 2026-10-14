# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Stop-JIMScheduleExecution {
    <#
    .SYNOPSIS
        Stops a running Schedule Execution in JIM.

    .DESCRIPTION
        Cancels a running or queued Schedule Execution. Any steps that have
        already completed will remain completed; pending steps will be cancelled.

    .PARAMETER Id
        The unique identifier (GUID) of the Schedule Execution to stop.

    .PARAMETER Force
        Suppresses the confirmation prompt.

    .PARAMETER PassThru
        Returns the cancelled Schedule Execution.

    .OUTPUTS
        With -PassThru, the cancelled Schedule Execution; otherwise nothing.

        JIM.ScheduleExecution
        StepDisplay (string): The step group the execution has reached, as one sentence naming it, for example Step
            2 of 5: 2 in parallel, matching what the portal shows in Admin > Operations > Queue.

    .EXAMPLE
        Stop-JIMScheduleExecution -Id "12345678-1234-1234-1234-123456789012"

        Stops the specified Schedule Execution (with confirmation).

    .EXAMPLE
        Get-JIMScheduleExecution -Active | Stop-JIMScheduleExecution -Force

        Stops all active Schedule Executions without confirmation.

    .LINK
        Get-JIMScheduleExecution
        Start-JIMSchedule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType('JIM.ScheduleExecution')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('ExecutionId')]
        [guid]$Id,

        [switch]$Force,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if (Confirm-JIMAction -Cmdlet $PSCmdlet -Target $Id -Action "Stop Schedule Execution" -Force:$Force) {
            Write-Verbose "Stopping Schedule Execution: $Id"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/schedule-executions/$Id/cancel" -Method 'POST'

                Write-Verbose "Stopped Schedule Execution: $Id"

                if ($PassThru) {
                    # Return the updated execution, in the shape Get-JIMScheduleExecution -Id gives it
                    Add-JIMScheduleExecutionStepDisplay -Execution (Invoke-JIMApi -Endpoint "/api/v1/schedule-executions/$Id")
                }
            }
            catch {
                Write-Error "Failed to stop Schedule Execution: $_"
            }
        }
    }
}
