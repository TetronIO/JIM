function Remove-JIMSchedule {
    <#
    .SYNOPSIS
        Removes a Schedule from JIM.

    .DESCRIPTION
        Deletes a Schedule and all its associated steps from JIM.
        This action cannot be undone.

    .PARAMETER Id
        The unique identifier (GUID) of the Schedule to remove.

    .PARAMETER Force
        Bypasses confirmation prompts.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMSchedule -Id "12345678-1234-1234-1234-123456789012"

        Removes the specified Schedule (with confirmation).

    .EXAMPLE
        Remove-JIMSchedule -Id "12345678-..." -Force

        Removes the specified Schedule without confirmation.

    .EXAMPLE
        Get-JIMSchedule -Name "Old*" | Remove-JIMSchedule -Force

        Removes all Schedules with names starting with "Old".

    .LINK
        Get-JIMSchedule
        New-JIMSchedule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('ScheduleId')]
        [guid]$Id,

        [switch]$Force
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        # Get schedule name for confirmation message
        $scheduleName = $Id
        try {
            $schedule = Invoke-JIMApi -Endpoint "/api/v1/schedules/$Id"
            $scheduleName = $schedule.name
        }
        catch {
            # Continue with ID if we can't get the name
        }

        if ($Force -or $PSCmdlet.ShouldProcess($scheduleName, "Remove Schedule")) {
            Write-Verbose "Removing Schedule: $Id ($scheduleName)"

            try {
                Invoke-JIMApi -Endpoint "/api/v1/schedules/$Id" -Method 'DELETE'
                Write-Verbose "Removed Schedule: $Id"
            }
            catch {
                Write-Error "Failed to remove Schedule: $_"
            }
        }
    }
}
