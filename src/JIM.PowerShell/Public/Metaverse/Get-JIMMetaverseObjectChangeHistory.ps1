# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMMetaverseObjectChangeHistory {
    <#
    .SYNOPSIS
        Gets the change history for a Metaverse Object in JIM.

    .DESCRIPTION
        Retrieves a paginated list of change records for the specified Metaverse Object,
        ordered by change time descending (most recent first). Each record carries the
        initiator, Synchronisation Rule, and run profile context, plus the per-attribute value changes.

        By default, returns a single page of results. Use -All to automatically paginate
        through all change records and return every row.

    .PARAMETER Id
        The unique identifier (GUID) of the Metaverse Object whose change history is being retrieved.

    .PARAMETER All
        Automatically paginate through all results and return every change record.
        Cannot be used with -Page.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1. Cannot be used with -All.

    .PARAMETER PageSize
        Number of items per page. Defaults to 50. Maximum is 100.

    .OUTPUTS
        PSCustomObject representing change-history record(s).

    .EXAMPLE
        Get-JIMMetaverseObjectChangeHistory -Id "12345678-1234-1234-1234-123456789abc"

        Gets the first page (50 most recent changes) for the specified Metaverse Object.

    .EXAMPLE
        Get-JIMMetaverseObjectChangeHistory -Id "12345678-1234-1234-1234-123456789abc" -All

        Gets every change record for the specified Metaverse Object, paginating automatically.

    .EXAMPLE
        Get-JIMMetaverseObjectChangeHistory -Id "12345678-1234-1234-1234-123456789abc" -PageSize 100

        Gets the first 100 changes (the maximum page size) for the specified Metaverse Object.

    .EXAMPLE
        Get-JIMMetaverseObject -ObjectTypeName "Group" -Search "Project-Alpha" |
            Get-JIMMetaverseObjectChangeHistory -All

        Pipes a Metaverse Object into the cmdlet to retrieve its full change history.

    .LINK
        Get-JIMMetaverseObject

    .LINK
        Get-JIMMetaverseObjectType
    #>
    [CmdletBinding(DefaultParameterSetName = 'Page')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'All')]
        [switch]$All,

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

        $currentPage = if ($All) { 1 } else { $Page }
        do {
            Write-Verbose "Getting change history for Metaverse Object $Id (Page: $currentPage, PageSize: $PageSize)"
            $endpoint = "/api/v1/metaverse/objects/$Id/change-history?page=$currentPage&pageSize=$PageSize"
            $response = Invoke-JIMApi -Endpoint $endpoint

            foreach ($item in $response.items) {
                $item
            }

            $hasMore = $All -and $response.hasNextPage -eq $true
            if ($hasMore) {
                $currentPage++
                Write-Verbose "Fetching page $currentPage of $($response.totalPages)..."
            }
        } while ($hasMore)
    }
}
