# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

    .PARAMETER InputObject
        A Schedule object from the pipeline (e.g., from Get-JIMSchedule). Its Id is used
        to filter executions, equivalent to specifying -ScheduleId directly.

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

    .EXAMPLE
        Get-JIMSchedule -Name "Weekday Sync" | Get-JIMScheduleExecution -Active

        Gets active (running or queued) executions for the "Weekday Sync" schedule.

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

        # A Schedule object (e.g. from Get-JIMSchedule) exposes Id, not ScheduleId, so it
        # cannot bind to -ScheduleId by property name. Binding the whole object here and
        # reading its Id below is the fix: ScheduleId cannot carry an [Alias('Id')] to
        # cover this, because the -Id parameter above is itself literally named "Id" in
        # this cmdlet (used to look up a specific execution) and PowerShell rejects a
        # parameter alias that collides with another parameter's own name, even across
        # mutually exclusive parameter sets (confirmed: the command fails to bind at all,
        # not just ambiguously, the moment such an alias is declared).
        [Parameter(ParameterSetName = 'List', ValueFromPipeline)]
        [Parameter(ParameterSetName = 'Active', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter(ParameterSetName = 'List')]
        [ValidateSet('Queued', 'InProgress', 'Completed', 'Failed', 'Cancelled')]
        [string]$Status,

        [Parameter(Mandatory, ParameterSetName = 'Active')]
        [switch]$Active
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # -ScheduleId (direct or bound by property name) takes precedence; otherwise fall
        # back to the piped Schedule object's Id (see -InputObject above).
        $effectiveScheduleId = if ($ScheduleId) {
            $ScheduleId
        }
        elseif ($InputObject -and $InputObject.PSObject.Properties['Id']) {
            [guid]$InputObject.Id
        }
        else {
            $null
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
                if ($effectiveScheduleId) {
                    $results = $results | Where-Object { $_.scheduleId -eq $effectiveScheduleId }
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

                if ($effectiveScheduleId) {
                    $queryParams += "scheduleId=$effectiveScheduleId"
                }

                if ($Status) {
                    # Send the enum as its string name; -Status is ValidateSet-constrained
                    # to the exact ScheduleExecutionStatus member names. Keeps the query
                    # string aligned with the API's string-only enum contract (PR #1060).
                    $queryParams += "status=$Status"
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
