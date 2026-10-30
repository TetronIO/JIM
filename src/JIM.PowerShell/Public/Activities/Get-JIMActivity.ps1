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
        The Activity to get.

    .PARAMETER Search
        Return only the Activities whose target name or type contains this text; "Active Directory", for example,
        finds the Activities of that Connected System.

    .PARAMETER Page
        The page of Activities to return.

    .PARAMETER PageSize
        How many Activities per page.

    .PARAMETER Operation
        Return only the Activities that performed one of these operations, for example Execute for a Run Profile
        execution, or Receive for the requests a SCIM client sent to a Connected System's SCIM endpoint.

    .PARAMETER Outcome
        Return only the Activities that recorded at least one of these outcomes, for example Errors for runs that
        recorded an error.

    .PARAMETER Status
        Return only the Activities with one of these statuses.

    .PARAMETER InitiatedById
        Return only the Activities initiated by this principal (a Metaverse Object or API key). -InitiatedBy matches
        on the recorded name instead.

    .PARAMETER InitiatedBy
        Return only the Activities whose initiator's recorded name contains this text, case-insensitively.

    .PARAMETER HasChildActivities
        Return only the Activities that have child Activities ($true), or only those that have none ($false). Omit
        it to return both.

    .PARAMETER CreatedFrom
        Return only the Activities created at or after this time, which is converted to UTC.

    .PARAMETER CreatedTo
        Return only the Activities created at or before this time, which is converted to UTC.

    .PARAMETER ConnectedSystem
        Return only the Activities of these Connected Systems, by name, ignoring case.

    .PARAMETER RunProfile
        Return only the executions of these Run Profiles, by name, ignoring case.

    .PARAMETER ScheduledOnly
        Return only the Activities a Schedule produced ($true), or only those nobody scheduled ($false). Omit it to
        return both.

    .PARAMETER ScheduleId
        Return only the Activities these Schedules produced.

    .PARAMETER ExecutionItems
        Return the Activity's Run Profile Execution Items: what happened to each object.

    .PARAMETER Follow
        Follow the Activity's progress until it finishes, like tail -f, showing the step it is on, object counts,
        throughput and the estimated time remaining. Ctrl+C stops following early.

    .PARAMETER IntervalSeconds
        How many seconds to wait between reads of the progress.

    .PARAMETER MaxPolls
        How many times to read the progress before following stops, whether or not the Activity has finished, for a
        script that must not wait indefinitely. Omit it to follow until the Activity finishes.

    .OUTPUTS
        A list returns each Activity's header, and -Id the Activity in full. -ExecutionItems returns each of the
        Activity's Run Profile Execution Items. -Follow shows progress while it follows, then returns the Activity
        in full.

        An Activity read in full carries ConfigurationChangeSynchronisations. For a configuration change Activity
        it has one entry per Object Type the change affects, saying whether a synchronisation has applied the
        change yet. For a synchronisation run it has one entry per Object Type the run processed in full because of
        changes saved before it started, with the ObjectCount processed and whether it applied them. It is empty
        for every other Activity.

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
    [OutputType('JIM.ActivityHeader', ParameterSetName = 'List')]
    [OutputType('JIM.ActivityDetailDto', ParameterSetName = 'ById')]
    [OutputType('JIM.ActivityDetailDto', ParameterSetName = 'Follow')]
    [OutputType('JIM.ActivityRunProfileExecutionItemHeader', ParameterSetName = 'ExecutionItems')]
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
            'Revert', 'Reset', 'Authenticate', 'Preview', 'Receive')]
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
