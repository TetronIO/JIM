# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMActivity {
    <#
    .SYNOPSIS
        Gets Activities from JIM.

    .DESCRIPTION
        Retrieves activity history from JIM. Activities track all operations performed
        in JIM, including sync runs, data generation, certificate management, and other
        administrative actions.

    .PARAMETER Id
        The unique identifier (GUID) of a specific Activity to retrieve.

    .PARAMETER Search
        Search query to filter activities by target name or type.

    .PARAMETER Page
        Page number for pagination. Defaults to 1.

    .PARAMETER PageSize
        Number of items per page. Defaults to 20.

    .PARAMETER Operation
        Filter by the operation the Activity performed, for example Execute for a Run Profile execution.
        Accepts several values, which combine with OR.

    .PARAMETER Outcome
        Filter to Activities that recorded at least one of the given outcomes, for example Errors for runs
        that recorded at least one error. Accepts several values, which combine with OR.

    .PARAMETER Status
        Filter by the Activity's status: InProgress, Complete, CompleteWithWarning, CompleteWithError,
        FailedWithError or Cancelled. Accepts several values, which combine with OR.

    .PARAMETER InitiatedById
        Filter to the exact principal (Metaverse Object or API key) that initiated the Activity. Use
        -InitiatedBy to match on the recorded name instead.

    .PARAMETER InitiatedBy
        Filter by part of the initiator's recorded name, case-insensitively.

    .PARAMETER HasChildActivities
        Filter by whether the Activity has child Activities: $true returns only those that do, $false only
        those that do not. Omit to return both.

    .PARAMETER CreatedFrom
        Only return Activities created at or after this time. Converted to UTC before being sent.

    .PARAMETER CreatedTo
        Only return Activities created at or before this time. Converted to UTC before being sent.

    .PARAMETER ConnectedSystem
        Filter by Connected System name, matched against the Activity's target context. Exact match;
        accepts several values, which combine with OR.

    .PARAMETER RunProfile
        Filter by Run Profile name, matched against the Activity's target name. Exact match; accepts
        several values, which combine with OR.

    .PARAMETER ScheduledOnly
        Filter by whether a Schedule produced the Activity: $true returns only work a Schedule produced,
        $false only work nobody scheduled. Omit to return both.

    .PARAMETER ScheduleId
        Filter to the Activities produced by particular Schedules. Accepts several values, which combine
        with OR.

    .PARAMETER ExecutionItems
        If specified with -Id, retrieves the execution items for a run profile activity.
        Items are streamed individually for pipeline support.

    .PARAMETER Follow
        If specified with -Id, follows the Activity's live progress (like tail -f): polls the
        lightweight progress endpoint and renders a progress bar with phase, object counts,
        throughput and estimated time remaining, until the Activity completes. Emits the final
        Activity object when following ends. Press Ctrl+C to stop following early.

    .PARAMETER IntervalSeconds
        Polling interval in seconds when following. Defaults to 2. Range 1-300.

    .PARAMETER MaxPolls
        Maximum number of progress polls before following stops, whether or not the Activity has
        completed. Useful for scripts that must not block indefinitely. If not specified, follows
        until the Activity completes.

    .OUTPUTS
        PSCustomObject representing Activity/Activities or execution items. With -Follow, the
        final Activity object (the same shape as -Id) is emitted when following ends.

    .EXAMPLE
        Get-JIMActivity

        Gets the most recent activities.

    .EXAMPLE
        Get-JIMActivity -Page 2 -PageSize 50

        Gets page 2 of activities with 50 items per page.

    .EXAMPLE
        Get-JIMActivity -Search "Full Import"

        Gets activities matching "Full Import".

    .EXAMPLE
        Get-JIMActivity -Status FailedWithError, CompleteWithError

        Gets the activities that failed or completed with errors.

    .EXAMPLE
        Get-JIMActivity -ConnectedSystem 'Contoso AD' -RunProfile 'Full Import' -Status FailedWithError

        Gets the failed Full Import runs against the 'Contoso AD' Connected System.

    .EXAMPLE
        Get-JIMActivity -ScheduledOnly $true -Outcome Errors -CreatedFrom (Get-Date).AddDays(-7)

        Gets the last week's scheduled work that recorded errors, which answers whether a Schedule has been
        failing repeatedly or only once.

    .EXAMPLE
        Get-JIMSchedule -Name 'Nightly Sync' | ForEach-Object { Get-JIMActivity -ScheduleId $_.Id }

        Gets the Activities the 'Nightly Sync' Schedule produced.

    .EXAMPLE
        Get-JIMActivity -InitiatedBy 'alice' -CreatedFrom (Get-Date).AddDays(-30)

        Gets the last 30 days of activities initiated by anyone whose name contains "alice".

    .EXAMPLE
        Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

        Gets a specific activity by ID.

    .EXAMPLE
        Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems

        Gets the execution items for a specific run profile activity.

    .EXAMPLE
        Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Follow

        Follows the Activity's live progress until it completes, then returns the final Activity.

    .EXAMPLE
        Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -Follow -IntervalSeconds 5 -MaxPolls 60

        Follows the Activity's progress for at most 5 minutes, polling every 5 seconds.

    .LINK
        Get-JIMActivityStats
        Start-JIMRunProfile
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'ExecutionItems', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'Follow', ValueFromPipelineByPropertyName)]
        [guid]$Id,

        [Parameter(ParameterSetName = 'List')]
        [string]$Search,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 20,

        [Parameter(ParameterSetName = 'List')]
        [ValidateSet('Create', 'Read', 'Update', 'Delete', 'Clear', 'Execute', 'ImportHierarchy', 'ImportSchema',
            'Revert', 'Reset', 'Authenticate', 'Preview')]
        [string[]]$Operation,

        [Parameter(ParameterSetName = 'List')]
        [ValidateSet('Added', 'Updated', 'Deleted', 'Projected', 'Joined', 'AttributeFlows', 'Disconnected',
            'DriftCorrections', 'Provisioned', 'Exported', 'Deprovisioned', 'Created', 'PendingExports', 'Errors')]
        [string[]]$Outcome,

        [Parameter(ParameterSetName = 'List')]
        [ValidateSet('InProgress', 'Complete', 'CompleteWithWarning', 'CompleteWithError', 'FailedWithError', 'Cancelled')]
        [string[]]$Status,

        [Parameter(ParameterSetName = 'List')]
        [guid]$InitiatedById,

        [Parameter(ParameterSetName = 'List')]
        [string]$InitiatedBy,

        [Parameter(ParameterSetName = 'List')]
        [bool]$HasChildActivities,

        [Parameter(ParameterSetName = 'List')]
        [datetime]$CreatedFrom,

        [Parameter(ParameterSetName = 'List')]
        [datetime]$CreatedTo,

        [Parameter(ParameterSetName = 'List')]
        [string[]]$ConnectedSystem,

        [Parameter(ParameterSetName = 'List')]
        [string[]]$RunProfile,

        [Parameter(ParameterSetName = 'List')]
        [bool]$ScheduledOnly,

        [Parameter(ParameterSetName = 'List')]
        [guid[]]$ScheduleId,

        [Parameter(Mandatory, ParameterSetName = 'ExecutionItems')]
        [switch]$ExecutionItems,

        [Parameter(Mandatory, ParameterSetName = 'Follow')]
        [switch]$Follow,

        [Parameter(ParameterSetName = 'Follow')]
        [ValidateRange(1, 300)]
        [int]$IntervalSeconds = 2,

        [Parameter(ParameterSetName = 'Follow')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$MaxPolls
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting Activity with ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/activities/$Id"
                $result
            }

            'Follow' {
                Write-Verbose "Following progress for Activity ID: $Id"
                $pollCount = 0
                $hasMaxPolls = $PSBoundParameters.ContainsKey('MaxPolls')
                $lastMessage = ''
                $terminalStatuses = @('Complete', 'CompleteWithWarning', 'CompleteWithError', 'FailedWithError', 'Cancelled')
                $terminal = $false

                while ($true) {
                    $progress = $null
                    try {
                        $progress = Invoke-JIMApi -Endpoint "/api/v1/activities/$Id/progress"
                    }
                    catch {
                        # Transient failures should not end the follow; the next poll retries.
                        Write-Warning "Error reading Activity progress: $_"
                    }
                    $pollCount++

                    if ($progress) {
                        $message = "$($progress.message ?? '')"
                        if ($message -and $message -ne $lastMessage) {
                            Write-Verbose $message
                            $lastMessage = $message
                        }

                        $progressParams = Get-JIMActivityProgressDisplay -Progress $progress -ActivityLabel 'Following Activity'
                        Write-Progress @progressParams

                        if ("$($progress.status)" -in $terminalStatuses) {
                            $terminal = $true
                        }
                    }

                    if ($terminal) {
                        break
                    }

                    if ($hasMaxPolls -and $pollCount -ge $MaxPolls) {
                        Write-Verbose "Stopped following after $pollCount polls (MaxPolls reached); the Activity may still be running."
                        break
                    }

                    Start-Sleep -Seconds $IntervalSeconds
                }

                Write-Progress -Activity 'Following Activity' -Completed

                # Emit the final Activity so callers receive the same shape as -Id.
                Invoke-JIMApi -Endpoint "/api/v1/activities/$Id"
            }

            'ExecutionItems' {
                Write-Verbose "Getting execution items for Activity ID: $Id"
                $currentPage = 1
                $hasMore = $true

                while ($hasMore) {
                    $response = Invoke-JIMApi -Endpoint "/api/v1/activities/$Id/items?page=$currentPage&pageSize=100"

                    # Stream items individually
                    foreach ($item in $response.items) {
                        $item
                    }

                    # Check if there are more pages
                    $hasMore = ($response.items.Count -eq 100) -and ($currentPage * 100 -lt $response.totalCount)
                    $currentPage++
                }
            }

            'List' {
                Write-Verbose "Getting Activities (Page: $Page, PageSize: $PageSize)"

                # The filters are evaluated by the API, which narrows the list through the same Activity
                # query the portal uses, so both surfaces return the same Activities for the same filters.
                # Multi-valued filters repeat their query parameter, one entry per value.
                $queryParams = @("page=$Page", "pageSize=$PageSize")

                if ($Search) {
                    $queryParams += "search=$([System.Uri]::EscapeDataString($Search))"
                }

                foreach ($operationValue in $Operation) {
                    $queryParams += "operation=$operationValue"
                }

                foreach ($outcomeValue in $Outcome) {
                    $queryParams += "outcome=$outcomeValue"
                }

                foreach ($statusValue in $Status) {
                    $queryParams += "status=$statusValue"
                }

                if ($PSBoundParameters.ContainsKey('InitiatedById')) {
                    $queryParams += "initiatedById=$InitiatedById"
                }

                if ($InitiatedBy) {
                    $queryParams += "initiatedBy=$([System.Uri]::EscapeDataString($InitiatedBy))"
                }

                # Bound-parameter checks rather than truthiness: $false is a meaningful value for these two
                # ("only Activities without children", "only unscheduled work"), and would otherwise be
                # indistinguishable from the parameter having been omitted.
                if ($PSBoundParameters.ContainsKey('HasChildActivities')) {
                    $queryParams += "hasChildActivities=$($HasChildActivities.ToString().ToLowerInvariant())"
                }

                if ($PSBoundParameters.ContainsKey('CreatedFrom')) {
                    $queryParams += "createdFrom=$($CreatedFrom.ToUniversalTime().ToString('o'))"
                }

                if ($PSBoundParameters.ContainsKey('CreatedTo')) {
                    $queryParams += "createdTo=$($CreatedTo.ToUniversalTime().ToString('o'))"
                }

                foreach ($connectedSystemValue in $ConnectedSystem) {
                    $queryParams += "connectedSystem=$([System.Uri]::EscapeDataString($connectedSystemValue))"
                }

                foreach ($runProfileValue in $RunProfile) {
                    $queryParams += "runProfile=$([System.Uri]::EscapeDataString($runProfileValue))"
                }

                if ($PSBoundParameters.ContainsKey('ScheduledOnly')) {
                    $queryParams += "scheduledOnly=$($ScheduledOnly.ToString().ToLowerInvariant())"
                }

                foreach ($scheduleIdValue in $ScheduleId) {
                    $queryParams += "scheduleId=$scheduleIdValue"
                }

                $endpoint = "/api/v1/activities?$($queryParams -join '&')"

                $response = Invoke-JIMApi -Endpoint $endpoint

                # Output each activity individually for pipeline support
                foreach ($activity in $response.items) {
                    $activity
                }

                # Add pagination info to verbose output
                Write-Verbose "Returned $($response.items.Count) of $($response.totalCount) total activities"
            }
        }
    }
}
