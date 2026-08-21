# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMPagedItems {
    <#
    .SYNOPSIS
        Retrieves every item from a paginated JIM list endpoint.

    .DESCRIPTION
        Internal helper for the name-to-id resolvers. The list endpoints are paginated with a
        server-side default page size, so reading a single unpaged response only sees the first
        page; a resolver built on that cannot find entities beyond it. This helper requests the
        maximum page size the API allows (100) and follows totalCount across pages until every
        item has been collected, stopping defensively if a page comes back empty.

        Also tolerates a bare-array response (no items envelope). Note that testing
        $response.items for truthiness is not safe there: member enumeration over an array of
        2+ objects yields a truthy array of nulls, so the envelope is detected via
        PSObject.Properties instead.

    .PARAMETER Endpoint
        The list endpoint to read, without paging parameters, e.g. "/api/v1/metaverse/attributes".
        A query string may already be present; paging parameters are appended either way.

    .OUTPUTS
        object[] of every item the endpoint holds.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Endpoint
    )

    $pageSize = 100
    $page = 1
    $items = @()

    do {
        $separator = if ($Endpoint.Contains('?')) { '&' } else { '?' }
        $response = Invoke-JIMApi -Endpoint "$Endpoint$separator`page=$page&pageSize=$pageSize"

        $isEnvelope = $null -ne $response -and $null -ne $response.PSObject -and $null -ne $response.PSObject.Properties['items']
        $pageItems = if ($isEnvelope) { @($response.items) } else { @($response) }
        $items += $pageItems

        if (-not $isEnvelope) {
            # a bare array is the whole result; there is nothing to page through.
            break
        }

        $totalCount = if ($null -ne $response.PSObject.Properties['totalCount']) { [int]$response.totalCount } else { $items.Count }
        $page++
    } while ($items.Count -lt $totalCount -and $pageItems.Count -gt 0)

    # comma operator preserves an empty or single-item result as an array on output
    , $items
}
