function Get-JIMScheduleExecution {
    <#
    .SYNOPSIS
        Gets Schedule Executions from JIM.

    .DESCRIPTION
        Retrieves Schedule Execution records from JIM. Executions represent
        instances of a schedule that have run or are currently running.

    .PARAMETER Id
        The unique identifier (GUID) of a specific Schedule Execution to retrieve.

    .PARAMETER ScheduleId
        Filter executions by Schedule ID.

    .PARAMETER Status
        Filter executions by status:
        - Queued: Waiting to start
        - InProgress: Currently running
        - Completed: Finished successfully
        - Failed: Finished with errors
        - Cancelled: Was cancelled

    .PARAMETER Active
        If specified, returns only currently active (Queued or InProgress) executions.

    .OUTPUTS
        PSCustomObject representing Schedule Execution(s).

    .EXAMPLE
        Get-JIMScheduleExecution

        Gets all Schedule Executions.

    .EXAMPLE
        Get-JIMScheduleExecution -Id "12345678-1234-1234-1234-123456789012"

        Gets a specific Schedule Execution by ID.

    .EXAMPLE
        Get-JIMScheduleExecution -Active

        Gets all currently active (running or queued) executions.

    .EXAMPLE
        Get-JIMSchedule -Name "Delta Sync" | Get-JIMScheduleExecution -Status Failed

        Gets failed executions for the "Delta Sync" schedule.

    .LINK
        Start-JIMSchedule
        Stop-JIMScheduleExecution
        Get-JIMSchedule
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Alias('ExecutionId')]
        [guid]$Id,

        [Parameter(ParameterSetName = 'List', ValueFromPipelineByPropertyName)]
        [Parameter(ParameterSetName = 'Active', ValueFromPipelineByPropertyName)]
        [guid]$ScheduleId,

        [Parameter(ParameterSetName = 'List')]
        [ValidateSet('Queued', 'InProgress', 'Completed', 'Failed', 'Cancelled')]
        [string]$Status,

        [Parameter(Mandatory, ParameterSetName = 'Active')]
        [switch]$Active
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'ById') {
            Write-Verbose "Getting Schedule Execution by ID: $Id"
            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/schedule-executions/$Id"
                $result
            }
            catch {
                Write-Error "Failed to get Schedule Execution: $_"
            }
        }
        elseif ($PSCmdlet.ParameterSetName -eq 'Active') {
            Write-Verbose "Getting active Schedule Executions"
            try {
                $results = Invoke-JIMApi -Endpoint "/api/v1/schedule-executions/active"

                # Filter by ScheduleId if specified
                if ($ScheduleId) {
                    $results = $results | Where-Object { $_.scheduleId -eq $ScheduleId }
                }

                foreach ($execution in $results) {
                    $execution
                }
            }
            catch {
                Write-Error "Failed to get active Schedule Executions: $_"
            }
        }
        else {
            Write-Verbose "Getting Schedule Executions"
            try {
                # Build query parameters
                $queryParams = @()
                $pageSize = 100
                $page = 1

                if ($ScheduleId) {
                    $queryParams += "scheduleId=$ScheduleId"
                }

                if ($Status) {
                    $statusValue = switch ($Status) {
                        'Queued' { 0 }
                        'InProgress' { 1 }
                        'Completed' { 2 }
                        'Failed' { 3 }
                        'Cancelled' { 4 }
                    }
                    $queryParams += "status=$statusValue"
                }

                $allExecutions = @()

                do {
                    $queryParams_page = $queryParams + @("page=$page", "pageSize=$pageSize")
                    $queryString = $queryParams_page -join '&'
                    $endpoint = "/api/v1/schedule-executions"
                    if ($queryString) {
                        $endpoint += "?$queryString"
                    }

                    $response = Invoke-JIMApi -Endpoint $endpoint

                    if ($response.items) {
                        $allExecutions += $response.items
                    }
                    elseif ($response -is [array]) {
                        $allExecutions = $response
                        break
                    }

                    $page++
                } while ($response.items -and $response.items.Count -eq $pageSize)

                foreach ($execution in $allExecutions) {
                    $execution
                }
            }
            catch {
                Write-Error "Failed to get Schedule Executions: $_"
            }
        }
    }
}
