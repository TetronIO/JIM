# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMSyncRule {
    <#
    .SYNOPSIS
        Gets Synchronisation Rules from JIM.

    .DESCRIPTION
        Retrieves Synchronisation Rule configurations from JIM. Can retrieve all rules, a specific
        rule by ID, or narrow the list by Connected System, Direction, Action type and Status.

        The filters combine with AND and each accepts several values, which combine with OR. The
        -Name filter narrows whatever the other filters left, so removing it returns those results.

    .PARAMETER Id
        The unique identifier of a specific Synchronisation Rule to retrieve.

    .PARAMETER ConnectedSystemId
        Filter Synchronisation Rules by Connected System ID.

    .PARAMETER ConnectedSystemName
        Filter Synchronisation Rules by Connected System name. Must be an exact match.

    .PARAMETER Name
        Filter Synchronisation Rules by name. Supports wildcards (e.g., "Inbound*").

    .PARAMETER Direction
        Filter Synchronisation Rules by direction: Import for inbound rules, Export for outbound
        rules. Accepts several values.

    .PARAMETER ActionType
        Filter Synchronisation Rules by the action they perform: Projects for Import rules that
        project new Metaverse Objects, Provisions for Export rules that provision new Connected
        System Objects, and FlowOnly for rules that create no objects and only flow attribute
        values. Accepts several values.

    .PARAMETER Status
        Filter Synchronisation Rules by state: Enabled or Disabled. Accepts several values.

    .OUTPUTS
        PSCustomObject representing Synchronisation Rule(s).

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
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$Id,

        [Parameter(ParameterSetName = 'List', ValueFromPipelineByPropertyName)]
        [Parameter(ParameterSetName = 'ByConnectedSystemId')]
        [int]$ConnectedSystemId,

        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [string]$ConnectedSystemName,

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

                if ($PSBoundParameters.ContainsKey('ConnectedSystemId') -or $PSBoundParameters.ContainsKey('ConnectedSystemName')) {
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
