# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMSchedule {
    <#
    .SYNOPSIS
        Gets Schedules from JIM.

    .DESCRIPTION
        Retrieves Schedule configurations from JIM. Schedules define automated
        synchronisation workflows that can run on a cron schedule or be triggered manually.

    .PARAMETER Id
        The unique identifier (GUID) of a specific Schedule to retrieve.

    .PARAMETER Name
        Filter schedules by name. Supports wildcards (* and ?).

    .PARAMETER IncludeSteps
        If specified, includes the full step details for each schedule.

    .OUTPUTS
        PSCustomObject representing Schedule(s).

    .EXAMPLE
        Get-JIMSchedule

        Gets all Schedules.

    .EXAMPLE
        Get-JIMSchedule -Id "12345678-1234-1234-1234-123456789012"

        Gets a specific Schedule by ID.

    .EXAMPLE
        Get-JIMSchedule -Name "Delta*"

        Gets all Schedules with names starting with "Delta".

    .EXAMPLE
        Get-JIMSchedule -IncludeSteps

        Gets all Schedules with their step details included.

    .LINK
        New-JIMSchedule
        Set-JIMSchedule
        Remove-JIMSchedule
        Start-JIMSchedule
        Enable-JIMSchedule
        Disable-JIMSchedule
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Alias('ScheduleId')]
        [guid]$Id,

        [Parameter(ParameterSetName = 'List')]
        [SupportsWildcards()]
        [string]$Name,

        [Parameter()]
        [switch]$IncludeSteps
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'ById') {
            Write-Verbose "Getting Schedule by ID: $Id"
            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/schedules/$Id"
                $result
            }
            catch {
                Write-Error "Failed to get Schedule: $_"
            }
        }
        else {
            Write-Verbose "Getting all Schedules"
            try {
                # Get paginated results
                $pageSize = 100
                $page = 1
                $allSchedules = @()

                do {
                    $response = Invoke-JIMApi -Endpoint "/api/v1/schedules?page=$page&pageSize=$pageSize"

                    if ($response.items) {
                        $allSchedules += $response.items
                    }
                    elseif ($response -is [array]) {
                        # Handle non-paginated response
                        $allSchedules = $response
                        break
                    }

                    $page++
                } while ($response.items -and $response.items.Count -eq $pageSize)

                # Filter by name if specified
                if ($Name) {
                    $allSchedules = $allSchedules | Where-Object { $_.name -like $Name }
                }

                # If IncludeSteps, fetch full details for each schedule. The single-Schedule endpoint returns a
                # ScheduleDetailDto built from the Schedule entity, which carries no last-execution projection, so
                # the outcome of the last run would be lost on this path; carry those fields over from the list
                # response so -IncludeSteps returns a superset of the list output rather than a different shape.
                if ($IncludeSteps) {
                    $lastExecutionProperties = @(
                        'LastExecutionId'
                        'LastExecutionStatus'
                        'LastExecutionCurrentStepIndex'
                        'LastExecutionTotalSteps'
                        'LastExecutionCompletedAt'
                        'LastExecutionErrorMessage'
                    )

                    foreach ($schedule in $allSchedules) {
                        $fullSchedule = Invoke-JIMApi -Endpoint "/api/v1/schedules/$($schedule.id)"

                        if ($fullSchedule) {
                            foreach ($property in $lastExecutionProperties) {
                                $fullSchedule | Add-Member -NotePropertyName $property -NotePropertyValue $schedule.$property -Force
                            }
                        }

                        $fullSchedule
                    }
                }
                else {
                    foreach ($schedule in $allSchedules) {
                        $schedule
                    }
                }
            }
            catch {
                Write-Error "Failed to get Schedules: $_"
            }
        }
    }
}
