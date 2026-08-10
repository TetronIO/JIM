# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMDataFlow {
    <#
    .SYNOPSIS
        Gets attribute data flows from JIM.

    .DESCRIPTION
        Retrieves a system-wide map of every attribute data flow, in both directions: what contributes
        each Metaverse Attribute, and what each Connected System attribute is written from. One flow
        per Synchronisation Rule Attribute Flow mapping.

        An Import flow reads Connected System attributes and writes a single Metaverse Attribute, so
        it carries a priority and a "Null is a value" flag. An Export flow reads Metaverse Attributes
        and writes a single Connected System attribute, so it carries the owning rule's Enforce State.
        The properties that do not apply to a flow's direction are null rather than defaulted, so you
        never have to guess which are meaningful.

        Import flows also carry contributorCount: how many flows contribute to the same Metaverse
        Attribute, counted across the whole configuration rather than the filtered results, so
        filtering to one Connected System does not make a shared attribute look like a sole
        contributor.

        The filters combine with AND. -SyncRuleName narrows whatever the other filters left, so
        removing it returns those results.

    .PARAMETER Direction
        Filter flows by direction: Import for flows into the Metaverse, Export for flows out of it.

    .PARAMETER ConnectedSystemId
        Filter flows by Connected System ID.

    .PARAMETER ConnectedSystemName
        Filter flows by Connected System name. Must be an exact match.

    .PARAMETER InputObject
        A Connected System object from the pipeline (e.g. from Get-JIMConnectedSystem). Its Id is
        used to filter flows, equivalent to specifying -ConnectedSystemId directly.

    .PARAMETER MetaverseObjectTypeId
        Filter flows by the Metaverse Object Type they apply to.

    .PARAMETER ConnectedSystemObjectTypeId
        Filter flows by the Connected System Object Type they apply to.

    .PARAMETER MetaverseAttributeId
        Filter flows that read or write this Metaverse Attribute, whichever side it sits on for the
        direction in question. A flow whose Metaverse side is an expression cannot match: an
        expression's attribute references live in its text and are not modelled. Use -Search to find
        those.

    .PARAMETER ConnectedSystemAttributeId
        Filter flows that read or write this Connected System attribute, whichever side it sits on
        for the direction in question. Subject to the same expression limitation as
        -MetaverseAttributeId.

    .PARAMETER MultipleContributorsOnly
        Return only Import flows whose target Metaverse Attribute has more than one contributor,
        which are the only flows whose priority order decides anything.

    .PARAMETER Search
        Free-text term matched case-insensitively against the Synchronisation Rule, the Connected
        System, both object types, every attribute named on either side, and any expression text.

    .PARAMETER SyncRuleName
        Filter flows by the name of the owning Synchronisation Rule. Supports wildcards
        (e.g. "HR*"). Evaluated client-side.

    .OUTPUTS
        PSCustomObject representing attribute data flow(s).

    .EXAMPLE
        Get-JIMDataFlow

        Gets every attribute data flow, in both directions.

    .EXAMPLE
        Get-JIMDataFlow -Direction Import -MultipleContributorsOnly

        Gets the inbound flows whose target Metaverse Attribute has more than one contributor: the
        attributes whose priority order decides which value wins.

    .EXAMPLE
        Get-JIMDataFlow -Search 'department'

        Answers "what touches department?" across both directions, including any expression that
        mentions it.

    .EXAMPLE
        Get-JIMDataFlow -ConnectedSystemName 'Contoso AD' -Direction Export

        Answers "what does JIM write to Contoso AD, and from where?".

    .EXAMPLE
        Get-JIMDataFlow -Direction Import | Where-Object { $_.nullIsValue } |
            Select-Object targetMetaverseAttributeName, syncRuleName, priority

        Lists every contribution that asserts an authoritative "no value", which is the setting most
        worth reviewing deliberately.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "HR*" | Get-JIMDataFlow

        Gets the flows for Connected Systems with names starting with "HR".

    .LINK
        Get-JIMSyncRule
        Get-JIMSyncRuleMapping
        Get-JIMMetaverseAttributePriority
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [ValidateSet('Import', 'Export')]
        [string]$Direction,

        [Parameter(ParameterSetName = 'List', ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByConnectedSystemName')]
        [string]$ConnectedSystemName,

        # A Connected System object (e.g. from Get-JIMConnectedSystem) exposes Id, not
        # ConnectedSystemId, so it cannot bind to -ConnectedSystemId by property name. Binding the
        # whole object here and reading its Id below is the fix, the same shape and reasoning as
        # Get-JIMSyncRule.
        [Parameter(ParameterSetName = 'List', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [int]$MetaverseObjectTypeId,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [int]$ConnectedSystemObjectTypeId,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [int]$MetaverseAttributeId,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [int]$ConnectedSystemAttributeId,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [switch]$MultipleContributorsOnly,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [string]$Search,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ByConnectedSystemName')]
        [SupportsWildcards()]
        [string]$SyncRuleName
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

        # The filters are evaluated by the API, so this cmdlet and the portal's Data Flow page return
        # the same flows for the same filters. -SyncRuleName stays client-side because it supports
        # PowerShell wildcards.
        $baseQueryParams = @('pageSize=100')

        if ($Direction) {
            $baseQueryParams += "direction=$Direction"
        }

        if ($filterByConnectedSystem) {
            $baseQueryParams += "connectedSystemId=$ConnectedSystemId"
        }

        if ($MetaverseObjectTypeId -gt 0) {
            $baseQueryParams += "metaverseObjectTypeId=$MetaverseObjectTypeId"
        }

        if ($ConnectedSystemObjectTypeId -gt 0) {
            $baseQueryParams += "connectedSystemObjectTypeId=$ConnectedSystemObjectTypeId"
        }

        if ($MetaverseAttributeId -gt 0) {
            $baseQueryParams += "metaverseAttributeId=$MetaverseAttributeId"
        }

        if ($ConnectedSystemAttributeId -gt 0) {
            $baseQueryParams += "connectedSystemAttributeId=$ConnectedSystemAttributeId"
        }

        if ($MultipleContributorsOnly) {
            $baseQueryParams += 'multipleContributorsOnly=true'
        }

        if ($Search) {
            $baseQueryParams += "search=$([System.Uri]::EscapeDataString($Search))"
        }

        Write-Verbose "Getting attribute data flows"

        # Page through the whole result set. Data flows are configuration rather than object data, so
        # the set is small, but stopping at the first page would silently truncate the map and make
        # the filters look as though they had excluded flows they did not.
        $currentPage = 1
        $pagesFetched = 0
        do {
            $queryString = (@("page=$currentPage") + $baseQueryParams) -join '&'
            $response = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/data-flows?$queryString"
            $pagesFetched++

            # Handle paginated response - check if 'items' property exists (not if it's truthy)
            $flows = if ($null -ne $response.items) { $response.items } else { $response }

            # Output each flow individually for pipeline support
            foreach ($flow in $flows) {
                if (-not $SyncRuleName -or $flow.syncRuleName -like $SyncRuleName) {
                    $flow
                }
            }

            $hasMore = $response.hasNextPage -eq $true

            if ($hasMore -and $pagesFetched -ge $script:JIMMaxAllPages) {
                Write-Warning "Get-JIMDataFlow stopped after $script:JIMMaxAllPages pages; more results remain (total pages: $($response.totalPages)). Narrow the query with -Direction, -ConnectedSystemId, -MetaverseObjectTypeId or -Search."
                break
            }

            if ($hasMore) {
                $currentPage++
                Write-Verbose "Fetching page $currentPage of $($response.totalPages)..."
            }
        } while ($hasMore)
    }
}
