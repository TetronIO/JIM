function Disable-JIMSchedule {
    <#
    .SYNOPSIS
        Disables a Schedule in JIM.

    .DESCRIPTION
        Disables a Schedule so it will not run automatically.
        The schedule can still be triggered manually with Start-JIMSchedule.

    .PARAMETER Id
        The unique identifier (GUID) of the Schedule to disable.

    .PARAMETER PassThru
        If specified, returns the updated Schedule object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Schedule object.

    .EXAMPLE
        Disable-JIMSchedule -Id "12345678-1234-1234-1234-123456789012"

        Disables the specified Schedule.

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

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        if ($PSCmdlet.ShouldProcess($Id, "Disable Schedule")) {
            Write-Verbose "Disabling Schedule: $Id"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/schedules/$Id/disable" -Method 'POST'

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
