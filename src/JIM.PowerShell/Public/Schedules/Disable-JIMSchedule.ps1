# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Disable-JIMSchedule {
    <#
    .SYNOPSIS
        Disables a Schedule in JIM.

    .DESCRIPTION
        Disables a Schedule so it will not run automatically.
        The schedule can still be triggered manually with Start-JIMSchedule.

    .PARAMETER Id
        The unique identifier (GUID) of the Schedule to disable.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Schedule's change history.

    .PARAMETER PassThru
        If specified, returns the updated Schedule object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Schedule object.

    .EXAMPLE
        Disable-JIMSchedule -Id "12345678-1234-1234-1234-123456789012"

        Disables the specified Schedule.

    .EXAMPLE
        Disable-JIMSchedule -Id "12345678-..." -ChangeReason "Paused for data centre move (CHG0077)"

        Disables the Schedule and records a reason against its change history.

    .EXAMPLE
        Get-JIMSchedule | Where-Object { $_.name -like "*Test*" } | Disable-JIMSchedule

        Disables all Schedules with "Test" in their name.

    .LINK
        Enable-JIMSchedule
        Get-JIMSchedule
        Start-JIMSchedule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('ScheduleId')]
        [guid]$Id,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess($Id, "Disable Schedule")) {
            Write-Verbose "Disabling Schedule: $Id"

            # The reason travels as a query parameter because the disable action has no request body.
            $disableEndpoint = "/api/v1/schedules/$Id/disable"
            if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                $disableEndpoint += "?changeReason=$([System.Uri]::EscapeDataString($ChangeReason))"
            }

            try {
                $result = Invoke-JIMApi -Endpoint $disableEndpoint -Method 'POST'

                Write-Verbose "Disabled Schedule: $Id"

                if ($PassThru) {
                    # Return the updated schedule
                    Invoke-JIMApi -Endpoint "/api/v1/schedules/$Id"
                }
            }
            catch {
                Write-Error "Failed to disable Schedule: $_"
            }
        }
    }
}
