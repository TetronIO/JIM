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
        Bypasses confirmation prompts.

    .PARAMETER PassThru
        If specified, returns the cancelled Schedule Execution object.

    .OUTPUTS
        If -PassThru is specified, returns the cancelled Schedule Execution object.

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
    [OutputType([PSCustomObject])]
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
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        if ($Force -or $PSCmdlet.ShouldProcess($Id, "Stop Schedule Execution")) {
            Write-Verbose "Stopping Schedule Execution: $Id"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/schedule-executions/$Id/cancel" -Method 'POST'

                Write-Verbose "Stopped Schedule Execution: $Id"

                if ($PassThru) {
                    # Return the updated execution
                    Invoke-JIMApi -Endpoint "/api/v1/schedule-executions/$Id"
                }
            }
            catch {
                Write-Error "Failed to stop Schedule Execution: $_"
            }
        }
    }
}
