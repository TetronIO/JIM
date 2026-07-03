# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMSchedule {
    <#
    .SYNOPSIS
        Removes a Schedule from JIM.

    .DESCRIPTION
        Deletes a Schedule and all its associated steps from JIM.
        This action cannot be undone.

    .PARAMETER Id
        The unique identifier (GUID) of the Schedule to remove.

    .PARAMETER ChangeReason
        An optional reason for the deletion, recorded against the change history.

    .PARAMETER Force
        Bypasses confirmation prompts.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMSchedule -Id "12345678-1234-1234-1234-123456789012"

        Removes the specified Schedule (with confirmation).

    .EXAMPLE
        Remove-JIMSchedule -Id "12345678-..." -Force -ChangeReason "Decommissioned (CHG0123)"

        Removes the specified Schedule without confirmation and records a reason against the change history.

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

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$Force
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
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

            # The reason is supplied as a query parameter because HTTP DELETE bodies are awkward for clients.
            $deleteEndpoint = "/api/v1/schedules/$Id"
            if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                $deleteEndpoint += "?changeReason=$([System.Uri]::EscapeDataString($ChangeReason))"
            }

            try {
                Invoke-JIMApi -Endpoint $deleteEndpoint -Method 'DELETE'
                Write-Verbose "Removed Schedule: $Id"
            }
            catch {
                Write-Error "Failed to remove Schedule: $_"
            }
        }
    }
}
