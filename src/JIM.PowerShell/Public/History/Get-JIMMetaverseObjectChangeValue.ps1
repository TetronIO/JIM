# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMMetaverseObjectChangeValue {
    <#
    .SYNOPSIS
        Gets one attribute's value changes on a Metaverse Object change.

    .DESCRIPTION
        Gets the value changes one Metaverse Object change recorded for one attribute, a page at a time, in the order
        the change recorded them. Get-JIMMetaverseObjectChangeHistory, Search-JIMMetaverseObjectChange and
        Get-JIMActivityExecutionItem return each changed attribute's first ten value changes and its total in
        valueChangeTotal; a change to a large group can carry one value change per member, and this gets all of them.
        The change is found by its ID, so a change to an object since deleted can still be read.

    .PARAMETER ChangeId
        The ID of the change, as Get-JIMMetaverseObjectChangeHistory returns it.

    .PARAMETER AttributeName
        The name of the attribute whose value changes to get, matched case-insensitively.

    .PARAMETER Search
        Returns only the value changes whose value contains this text, including a referenced object's name.

    .PARAMETER Page
        The page to get.

    .PARAMETER PageSize
        How many value changes a page holds.

    .PARAMETER All
        Fetches every page instead of one. Stops after 1000 pages with a warning; -Force fetches the rest.

    .PARAMETER Force
        With -All, fetches every page, past the 1000-page limit.

    .OUTPUTS
        The attribute's value changes.

    .EXAMPLE
        Get-JIMMetaverseObjectChangeValue -ChangeId "a1b2c3d4-..." -AttributeName "Static Members"

        Gets the first page of Static Members value changes the change recorded.

    .EXAMPLE
        Get-JIMMetaverseObjectChangeValue -ChangeId "a1b2c3d4-..." -AttributeName "Static Members" -Search "Smith"

        Gets the Static Members value changes whose member's name contains "Smith".

    .EXAMPLE
        Get-JIMMetaverseObjectChangeValue -ChangeId "a1b2c3d4-..." -AttributeName "Static Members" -All | Where-Object { $_.valueChangeType -eq 'Add' }

        Gets every Static Members value change across all pages, keeping the additions.

    .LINK
        Get-JIMMetaverseObjectChangeHistory
        Search-JIMMetaverseObjectChange
        Get-JIMActivityExecutionItem
    #>
    [CmdletBinding(DefaultParameterSetName = 'Page')]
    [OutputType('JIM.MvoValueChangeDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Page')]
        [Parameter(Mandatory, ParameterSetName = 'All')]
        [guid]$ChangeId,

        [Parameter(Mandatory, ParameterSetName = 'Page')]
        [Parameter(Mandatory, ParameterSetName = 'All')]
        [ValidateNotNullOrEmpty()]
        [string]$AttributeName,

        [Parameter(ParameterSetName = 'Page')]
        [Parameter(ParameterSetName = 'All')]
        [string]$Search,

        [Parameter(ParameterSetName = 'Page')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'Page')]
        [Parameter(ParameterSetName = 'All')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 50,

        [Parameter(Mandatory, ParameterSetName = 'All')]
        [switch]$All,

        [Parameter(ParameterSetName = 'All')]
        [switch]$Force
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $baseEndpoint = "/api/v1/history/metaverse-object-changes/$ChangeId/attributes/$([System.Uri]::EscapeDataString($AttributeName))/values"
        $pageRequest = {
            param($p)
            $endpoint = "$($baseEndpoint)?page=$p&pageSize=$PageSize"
            if ($Search) {
                $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
            }
            Invoke-JIMApi -Endpoint $endpoint
        }

        switch ($PSCmdlet.ParameterSetName) {
            'Page' {
                Write-Verbose "Getting '$AttributeName' value changes on Metaverse Object change $ChangeId (Page: $Page, PageSize: $PageSize)"
                $response = & $pageRequest $Page
                foreach ($item in $response.items) {
                    $item
                }
            }

            'All' {
                Write-Verbose "Getting all '$AttributeName' value changes on Metaverse Object change $ChangeId"
                Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMMetaverseObjectChangeValue' -PageSize $PageSize -Force:$Force `
                    -ItemNoun 'value changes' -NarrowHint 'filter with -Search'
            }
        }
    }
}
