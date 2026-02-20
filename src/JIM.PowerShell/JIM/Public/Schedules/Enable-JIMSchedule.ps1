function Enable-JIMSchedule {
    <#
    .SYNOPSIS
        Enables a Schedule in JIM.

    .DESCRIPTION
        Enables a Schedule so it will run according to its configured trigger.
        For Cron schedules, this means it will run at the scheduled times.

    .PARAMETER Id
        The unique identifier (GUID) of the Schedule to enable.

    .PARAMETER PassThru
        If specified, returns the updated Schedule object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Schedule object.

    .EXAMPLE
        Enable-JIMSchedule -Id "12345678-1234-1234-1234-123456789012"

        Enables the specified Schedule.

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

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        if ($PSCmdlet.ShouldProcess($Id, "Enable Schedule")) {
            Write-Verbose "Enabling Schedule: $Id"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/schedules/$Id/enable" -Method 'POST'

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
