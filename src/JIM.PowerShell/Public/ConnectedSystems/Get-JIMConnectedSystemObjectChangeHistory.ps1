# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemObjectChangeHistory {
    <#
    .SYNOPSIS
        Gets a Connected System Object's change history.

    .DESCRIPTION
        Gets the changes made to a Connected System Object, most recent first: when each was made, who or what made
        it, and what it changed. Returns one page unless -All is given.

        Each changed attribute carries its first ten value changes and, in valueChangeTotal, how many there are. A
        change to a large group can carry one per member; Get-JIMConnectedSystemObjectChangeValue gets the rest.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System the object belongs to.

    .PARAMETER Id
        The ID of the Connected System Object.

    .PARAMETER All
        Fetches every page instead of one. Stops after 1000 pages with a warning; -Force fetches the rest.

    .PARAMETER Force
        With -All, fetches every page, past the 1000-page limit.

    .PARAMETER Page
        The page to get.

    .PARAMETER PageSize
        How many changes a page holds.

    .OUTPUTS
        The changes, most recent first.

    .EXAMPLE
        Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId 1 -Id "12345678-1234-1234-1234-123456789abc"

        Gets the first page (50 most recent changes) for the specified CSO.

    .EXAMPLE
        Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId 1 -Id "12345678-1234-1234-1234-123456789abc" -All

        Gets every change record for the specified CSO, paginating automatically.

    .EXAMPLE
        Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId 1 -Id "12345678-1234-1234-1234-123456789abc" -All -Force

        Gets every change record, overriding the 1000-page safety cap for a very long change history.

    .EXAMPLE
        Get-JIMConnectedSystemObjectChangeHistory -ConnectedSystemId 1 -Id "12345678-1234-1234-1234-123456789abc" -PageSize 100

        Gets the first 100 changes (the maximum page size) for the specified CSO.

    .LINK
        Get-JIMConnectedSystemObject

    .LINK
        Get-JIMConnectedSystem
    #>
    [CmdletBinding(DefaultParameterSetName = 'Page')]
    [OutputType('JIM.CsoChangeHistoryDto')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'All')]
        [switch]$All,

        [Parameter(ParameterSetName = 'All')]
        [switch]$Force,

        [Parameter(ParameterSetName = 'Page')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'Page')]
        [Parameter(ParameterSetName = 'All')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 50
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # The shared helper owns the -All loop, the page cap and the warnings (issue #487); a single
        # page is fetched directly.
        $pageRequest = {
            param($p)
            Write-Verbose "Getting change history for CSO $Id in connected system $ConnectedSystemId (Page: $p, PageSize: $PageSize)"
            Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$Id/change-history?page=$p&pageSize=$PageSize"
        }

        if ($All) {
            Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMConnectedSystemObjectChangeHistory' -PageSize $PageSize -Force:$Force `
                -ItemNoun 'change records'
        }
        else {
            $response = & $pageRequest $Page
            foreach ($item in $response.items) {
                $item
            }
        }
    }
}
