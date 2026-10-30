# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMSyncRule {
    <#
    .SYNOPSIS
        Gets Synchronisation Rules.

    .DESCRIPTION
        Gets Synchronisation Rules: every rule by default, one by ID, or those matching the filters. The filters
        combine with AND, and the values given to one filter combine with OR. -Name narrows whatever the other
        filters return.

    .PARAMETER Id
        The ID of the Synchronisation Rule to get.

    .PARAMETER ConnectedSystemId
        Returns only the rules for the Connected System with this ID.

    .PARAMETER ConnectedSystemName
        Returns only the rules for the Connected System with this name, matched exactly.

    .PARAMETER InputObject
        A Connected System from Get-JIMConnectedSystem, through the pipeline: returns only its rules, as
        -ConnectedSystemId would.

    .PARAMETER Name
        Returns only the rules whose name matches.

    .PARAMETER Direction
        Returns only the rules of these directions: Import for rules that flow values into the Metaverse, Export for
        rules that flow them out.

    .PARAMETER ActionType
        Returns only the rules that do one of these: Projects for import rules that create Metaverse Objects,
        Provisions for export rules that create Connected System Objects, and FlowOnly for rules that create nothing
        and only flow values.

    .PARAMETER Status
        Returns only the rules that are Enabled or Disabled.

    .OUTPUTS
        The matching rules, or with -Id, the one rule. Each carries what it is waiting on: an import rule's
        AwaitingSynchronisationChangeCount is the saved changes waiting for its Connected System's next
        synchronisation to process every object of the rule's Object Type, and an export rule's
        ExportReviewPendingCount is the Metaverse Objects of its type flagged for review against every export rule
        at the next synchronisation of any Connected System. Each is zero for the other direction.

    .EXAMPLE
        Get-JIMSyncRule

        Gets all Synchronisation Rules.

    .EXAMPLE
        Get-JIMSyncRule -Id 1

        Gets the Synchronisation Rule with ID 1.

    .EXAMPLE
        Get-JIMSyncRule -ConnectedSystemId 1

        Gets all Synchronisation Rules for Connected System ID 1.

    .EXAMPLE
        Get-JIMSyncRule -ConnectedSystemName 'Contoso AD'

        Gets all Synchronisation Rules for the Connected System named 'Contoso AD'.

    .EXAMPLE
        Get-JIMSyncRule -Name "Inbound*"

        Gets all Synchronisation Rules with names starting with "Inbound".

    .EXAMPLE
        Get-JIMSyncRule -Direction Export -Status Enabled

        Gets every enabled outbound Synchronisation Rule.

    .EXAMPLE
        Get-JIMSyncRule -ActionType Projects, Provisions

        Gets the Synchronisation Rules that create objects, either by projecting them into the
        Metaverse or by provisioning them into a Connected System.

    .EXAMPLE
        Get-JIMSyncRule -ConnectedSystemName 'Contoso AD' -Direction Export -Status Disabled

        Gets the disabled outbound Synchronisation Rules for the 'Contoso AD' Connected System.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "HR*" | Get-JIMSyncRule

        Gets all Synchronisation Rules for Connected Systems with names starting with "HR".

    .LINK
        New-JIMSyncRule
        Set-JIMSyncRule
        Remove-JIMSyncRule
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.SyncRuleHeader')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$Id,

        [Parameter(ParameterSetName = 'List', ValueFromPipelineByPropertyName)]
        [Parameter(ParameterSetName = 'ByConnectedSystemId')]
        [int]$ConnectedSystemId,

        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [string]$ConnectedSystemName,

        # A Connected System object (e.g. from Get-JIMConnectedSystem) exposes Id, not
        # ConnectedSystemId, so it cannot bind to -ConnectedSystemId by property name. Binding the
        # whole object here and reading its Id below is the fix: ConnectedSystemId cannot carry an
        # [Alias('Id')] to cover this, because the -Id parameter above is itself literally named
        # "Id" in this cmdlet and PowerShell rejects a parameter alias that collides with another
        # parameter's own name, even across mutually exclusive parameter sets. Same shape and same
        # reasoning as Get-JIMScheduleExecution.
        [Parameter(ParameterSetName = 'List', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ByConnectedSystemId')]
        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [SupportsWildcards()]
        [string]$Name,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ByConnectedSystemId')]
        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [ValidateSet('Import', 'Export')]
        [string[]]$Direction,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ByConnectedSystemId')]
        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [ValidateSet('Projects', 'Provisions', 'FlowOnly')]
        [string[]]$ActionType,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ByConnectedSystemId')]
        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [ValidateSet('Enabled', 'Disabled')]
        [string[]]$Status
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Resolve ConnectedSystemName to ConnectedSystemId if specified
        if ($PSBoundParameters.ContainsKey('ConnectedSystemName')) {
            $connectedSystem = Resolve-JIMConnectedSystem -Name $ConnectedSystemName
            $ConnectedSystemId = $connectedSystem.id
        }

        # -ConnectedSystemId (direct or bound by property name) takes precedence; otherwise fall
        # back to the piped Connected System object's Id (see -InputObject above).
        $filterByConnectedSystem = $ConnectedSystemId -gt 0
        if (-not $filterByConnectedSystem -and $InputObject -and $InputObject.PSObject.Properties['Id']) {
            $ConnectedSystemId = [int]$InputObject.Id
            $filterByConnectedSystem = $ConnectedSystemId -gt 0
        }

        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting Synchronisation Rule with ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$Id"
                $result
            }

            default {
                # The facets are evaluated by the API, which narrows the list through the same
                # SyncRuleFilter the portal uses, so both surfaces return the same rules for the
                # same filters. -Name stays client-side because it supports PowerShell wildcards.
                $baseQueryParams = @('pageSize=100')

                if ($filterByConnectedSystem) {
                    Write-Verbose "Filtering by Connected System ID: $ConnectedSystemId"
                    $baseQueryParams += "connectedSystemIds=$ConnectedSystemId"
                }

                foreach ($directionValue in $Direction) {
                    $baseQueryParams += "directions=$directionValue"
                }

                foreach ($actionTypeValue in $ActionType) {
                    $baseQueryParams += "actionTypes=$actionTypeValue"
                }

                foreach ($statusValue in $Status) {
                    $baseQueryParams += "statuses=$statusValue"
                }

                Write-Verbose "Getting Synchronisation Rules"

                # Page through the whole result set. Synchronisation Rules are configuration rather
                # than object data, so the set is small, but stopping at the first page would
                # silently truncate the list and make the filters look as though they had excluded
                # rules they did not.
                $currentPage = 1
                $pagesFetched = 0
                do {
                    $queryString = (@("page=$currentPage") + $baseQueryParams) -join '&'
                    $response = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules?$queryString"
                    $pagesFetched++

                    # Handle paginated response - check if 'items' property exists (not if it's truthy)
                    $rules = if ($null -ne $response.items) { $response.items } else { $response }

                    # Output each rule individually for pipeline support
                    foreach ($rule in $rules) {
                        if (-not $Name -or $rule.name -like $Name) {
                            $rule
                        }
                    }

                    $hasMore = $response.hasNextPage -eq $true

                    if ($hasMore -and $pagesFetched -ge $script:JIMMaxAllPages) {
                        Write-Warning "Get-JIMSyncRule stopped after $script:JIMMaxAllPages pages; more results remain (total pages: $($response.totalPages)). Narrow the query with -ConnectedSystemId, -Direction, -ActionType or -Status."
                        break
                    }

                    if ($hasMore) {
                        $currentPage++
                        Write-Verbose "Fetching page $currentPage of $($response.totalPages)..."
                    }
                } while ($hasMore)
            }
        }
    }
}
