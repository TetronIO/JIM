# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Enable-JIMSchedule {
    <#
    .SYNOPSIS
        Enables a Schedule in JIM.

    .DESCRIPTION
        Enables a Schedule so it will run according to its configured trigger.
        For Cron schedules, this means it will run at the scheduled times.

    .PARAMETER Id
        The unique identifier (GUID) of the Schedule to enable.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Schedule's change history.

    .PARAMETER PassThru
        If specified, returns the updated Schedule object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Schedule object.

    .EXAMPLE
        Enable-JIMSchedule -Id "12345678-1234-1234-1234-123456789012"

        Enables the specified Schedule.

    .EXAMPLE
        Enable-JIMSchedule -Id "12345678-..." -ChangeReason "Maintenance window over (CHG0042)"

        Enables the Schedule and records a reason against its change history.

    .EXAMPLE
        Get-JIMSchedule -Name "Delta*" | Enable-JIMSchedule

        Enables all Schedules with names starting with "Delta".

    .LINK
        Disable-JIMSchedule
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

        if ($PSCmdlet.ShouldProcess($Id, "Enable Schedule")) {
            Write-Verbose "Enabling Schedule: $Id"

            # The reason travels as a query parameter because the enable action has no request body.
            $enableEndpoint = "/api/v1/schedules/$Id/enable"
            if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                $enableEndpoint += "?changeReason=$([System.Uri]::EscapeDataString($ChangeReason))"
            }

            try {
                $result = Invoke-JIMApi -Endpoint $enableEndpoint -Method 'POST'

                Write-Verbose "Enabled Schedule: $Id"

                if ($PassThru) {
                    # Return the updated schedule
                    Invoke-JIMApi -Endpoint "/api/v1/schedules/$Id"
                }
            }
            catch {
                Write-Error "Failed to enable Schedule: $_"
            }
        }
    }
}
