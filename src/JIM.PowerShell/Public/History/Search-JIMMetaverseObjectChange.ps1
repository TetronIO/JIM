# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Search-JIMMetaverseObjectChange {
    <#
    .SYNOPSIS
        Searches changes to Metaverse Objects across every object.

    .DESCRIPTION
        Finds changes to Metaverse Objects, newest first: by when they were made, by Metaverse Object Type, by display
        name, and by how named attributes changed, for example every Person whose Employee Status changed to Disabled
        this month. Changes to objects since deleted are included.

        Give one attribute condition with -Attribute, -Operator and -Value, or several with -Condition. With several,
        -MatchMode All (the default) requires every condition to be met by the same change, to one Metaverse Object in
        one Activity; -MatchMode Any requires one.

        Only changes JIM recorded can be found: none are recorded while Metaverse Object change tracking is turned off,
        and history retention removes older ones.

    .PARAMETER Attribute
        The name of the Metaverse Attribute whose changes to find.

    .PARAMETER Operator
        How the attribute changed. ChangedTo finds a value added (a single-valued attribute changed to it), ChangedFrom
        a value removed, and AnyChange either.

    .PARAMETER Value
        The value the attribute changed to or from. Leave it out to match any value. Text and Reference values match
        case-insensitively, a Reference value against the referenced Metaverse Object's display name, and * matches
        any run of characters. A date is yyyy-MM-dd, matching the whole day in UTC.

    .PARAMETER Condition
        Several attribute conditions, one hashtable each, with the keys Attribute (a name) or AttributeId, Operator
        (AnyChange when left out) and Value (any value when left out). At most ten.

    .PARAMETER MatchMode
        With several conditions, All requires every condition to be met by the same change; Any requires one.

    .PARAMETER ObjectTypeName
        Only changes to Metaverse Objects of the Metaverse Object Type with this name.

    .PARAMETER ObjectTypeId
        Only changes to Metaverse Objects of the Metaverse Object Type with this id.

    .PARAMETER DisplayName
        Only changes to Metaverse Objects whose display name contains this text.

    .PARAMETER FromDate
        Only changes made on or after this time.

    .PARAMETER ToDate
        Only changes made on or before this time.

    .PARAMETER All
        Returns every page of changes rather than one. It stops after 1,000 pages with a warning; -Force reads on.

    .PARAMETER Force
        With -All, reads every page, past the 1,000-page limit.

    .PARAMETER Page
        The page of changes to return, counting from 1. Ignored with -All.

    .PARAMETER PageSize
        How many changes each page holds.

    .OUTPUTS
        One object per change, newest first, with the attributes it changed and the values added and removed.

    .EXAMPLE
        Search-JIMMetaverseObjectChange -ObjectTypeName 'Person' -Attribute 'Employee Status' -Operator ChangedTo -Value 'Disabled' -FromDate (Get-Date).AddDays(-30)

        Finds every Person whose Employee Status changed to Disabled in the last 30 days.

    .EXAMPLE
        Search-JIMMetaverseObjectChange -Condition @(
            @{ Attribute = 'Employee Status'; Operator = 'ChangedTo'; Value = 'Disabled' },
            @{ Attribute = 'Department'; Operator = 'ChangedTo'; Value = 'Leavers' }
        ) -All

        Finds every change that set Employee Status to Disabled and Department to Leavers at the same time.

    .EXAMPLE
        Search-JIMMetaverseObjectChange -Attribute 'Department' -Operator ChangedFrom -Value 'Fin*' -All |
            Select-Object ChangeTime, MetaverseObjectDisplayName

        Lists when each Metaverse Object moved out of a department starting with "Fin".

    .LINK
        Get-JIMMetaverseObjectChangeHistory

    .LINK
        Get-JIMMetaverseAttribute
    #>
    [CmdletBinding(DefaultParameterSetName = 'Attribute')]
    [OutputType('JIM.MetaverseObjectChangeSearchItem')]
    param(
        [Parameter(ParameterSetName = 'Attribute')]
        [ValidateNotNullOrEmpty()]
        [string]$Attribute,

        [Parameter(ParameterSetName = 'Attribute')]
        [ValidateSet('AnyChange', 'ChangedTo', 'ChangedFrom')]
        [string]$Operator = 'AnyChange',

        [Parameter(ParameterSetName = 'Attribute')]
        [string]$Value,

        [Parameter(Mandatory, ParameterSetName = 'Conditions')]
        [ValidateCount(1, 10)]
        [hashtable[]]$Condition,

        [Parameter()]
        [ValidateSet('All', 'Any')]
        [string]$MatchMode = 'All',

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ObjectTypeName,

        [Parameter()]
        [int]$ObjectTypeId,

        [Parameter()]
        [string]$DisplayName,

        [Parameter()]
        [DateTime]$FromDate,

        [Parameter()]
        [DateTime]$ToDate,

        [Parameter()]
        [switch]$All,

        [Parameter()]
        [switch]$Force,

        [Parameter()]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter()]
        [ValidateRange(1, 100)]
        [int]$PageSize = 25
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $conditions = @()
        if ($PSCmdlet.ParameterSetName -eq 'Conditions') {
            foreach ($entry in $Condition) {
                if ($entry.ContainsKey('AttributeId')) {
                    $attributeId = [int]$entry.AttributeId
                }
                elseif ($entry.ContainsKey('Attribute')) {
                    $attributeId = (Resolve-JIMMetaverseAttribute -Name $entry.Attribute).id
                }
                else {
                    Write-Error "Each -Condition entry needs an Attribute (a name) or an AttributeId." -ErrorAction Stop
                    return
                }

                $conditions += @{
                    attributeId = $attributeId
                    operator    = if ($entry.ContainsKey('Operator')) { [string]$entry.Operator } else { 'AnyChange' }
                    value       = if ($entry.ContainsKey('Value')) { [string]$entry.Value } else { $null }
                }
            }
        }
        elseif ($PSBoundParameters.ContainsKey('Attribute')) {
            $conditions += @{
                attributeId = (Resolve-JIMMetaverseAttribute -Name $Attribute).id
                operator    = $Operator
                value       = if ($PSBoundParameters.ContainsKey('Value')) { $Value } else { $null }
            }
        }

        $criteria = @{
            conditions = $conditions
            matchMode  = $MatchMode
            pageSize   = $PageSize
        }

        if ($PSBoundParameters.ContainsKey('ObjectTypeName')) {
            $criteria.objectTypeId = (Resolve-JIMMetaverseObjectType -Name $ObjectTypeName).id
        }
        elseif ($PSBoundParameters.ContainsKey('ObjectTypeId')) {
            $criteria.objectTypeId = $ObjectTypeId
        }

        if ($PSBoundParameters.ContainsKey('DisplayName')) {
            $criteria.displayNameSearch = $DisplayName
        }
        if ($PSBoundParameters.ContainsKey('FromDate')) {
            $criteria.fromDate = $FromDate.ToUniversalTime().ToString('o')
        }
        if ($PSBoundParameters.ContainsKey('ToDate')) {
            $criteria.toDate = $ToDate.ToUniversalTime().ToString('o')
        }

        # The shared helper owns the -All loop, the page cap and the warnings; a single page is fetched directly.
        $pageRequest = {
            param($p)
            $body = $criteria.Clone()
            $body.page = $p
            Write-Verbose "Searching Metaverse Object changes (Page: $p, PageSize: $PageSize)"
            Invoke-JIMApi -Endpoint '/api/v1/history/metaverse-object-changes/search' -Method 'POST' -Body $body
        }

        if ($All) {
            Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Search-JIMMetaverseObjectChange' -PageSize $PageSize -Force:$Force `
                -ItemNoun 'changes'
        }
        else {
            $response = & $pageRequest $Page
            foreach ($item in $response.items) {
                $item
            }
        }
    }
}
