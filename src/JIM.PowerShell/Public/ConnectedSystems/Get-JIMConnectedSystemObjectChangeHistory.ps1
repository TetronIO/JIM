# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemObjectChangeHistory {
    <#
    .SYNOPSIS
        Gets the change history for a Connected System Object in JIM.

    .DESCRIPTION
        Retrieves a paginated list of change records for the specified Connected System Object,
        ordered by change time descending (most recent first). Each record carries the initiator
        and run profile context, plus the per-attribute value changes.

        By default, returns a single page of results. Use -All to automatically paginate
        through all change records and return every row.

        For safety, -All fetches at most 1000 pages (~50,000 records at the default page
        size of 50) and then stops with a warning. Supply -Force to override the cap and
        fetch every page.

    .PARAMETER ConnectedSystemId
        The unique identifier (integer) of the Connected System the object belongs to.

    .PARAMETER Id
        The unique identifier (GUID) of the Connected System Object whose change history is being retrieved.

    .PARAMETER All
        Automatically paginate through all results and return every change record.
        Cannot be used with -Page. Fetches at most 1000 pages before stopping with a
        warning; use -Force to fetch beyond the cap.

    .PARAMETER Force
        Override the -All page ceiling (1000 pages) and fetch every page regardless of
        how large the change history is. Only valid with -All.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1. Cannot be used with -All.

    .PARAMETER PageSize
        Number of items per page. Defaults to 50. Maximum is 100.

    .OUTPUTS
        PSCustomObject representing change-history record(s).

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
    [OutputType([PSCustomObject])]
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
