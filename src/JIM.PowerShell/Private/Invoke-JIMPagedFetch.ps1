# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Invoke-JIMPagedFetch {
    <#
    .SYNOPSIS
        Auto-paginating fetch loop shared by the -All modes of the paginated Get-* cmdlets.

    .DESCRIPTION
        Drives the page-by-page fetch for a paginated list endpoint, emitting each item to the
        pipeline. Applies JIM's pagination safety limits (see issue #487): it fetches at most
        $script:JIMMaxAllPages pages before stopping with a warning, unless -Force is supplied, and
        warns up front when the endpoint reports a total result set larger than
        $script:JIMAllWarningThreshold.

        Centralising this here keeps every cmdlet's -All behaviour, wording and cap identical, so a
        new paginated cmdlet cannot accidentally ship an unbounded -All that hammers the API
        sequentially or trips the API's own page-depth cap (PaginationRequest.MaxPage) mid-loop.

        The caller supplies a -PageRequest script block that takes a single page number and returns
        the raw paginated response envelope (with .items, .hasNextPage, .totalPages and, where the
        endpoint provides it, .totalCount). This helper owns the loop, the item emission, the cap and
        the warnings; the caller owns only how a single page is fetched. It is only ever called from a
        cmdlet's -All code path; single-page fetches do not go through it.

    .PARAMETER PageRequest
        Script block invoked as & $PageRequest $pageNumber, returning the paginated response envelope
        for that page.

    .PARAMETER CmdletName
        The calling cmdlet's name, used verbatim in the warning messages.

    .PARAMETER PageSize
        The page size in use, used only to estimate the item count in the cap warning.

    .PARAMETER Force
        Override the page ceiling and fetch every page regardless of how large the result set is.

    .PARAMETER ItemNoun
        Plural noun for the items being fetched (e.g. "objects", "attribute values"), used in the
        warning messages. Defaults to "items".

    .PARAMETER NarrowHint
        Optional clause appended to the cap warning suggesting how to narrow the query (e.g.
        "narrow the query with -Search, -Status or -ObjectTypeId"). Omit for endpoints with no
        meaningful narrowing parameters.

    .OUTPUTS
        The items from every fetched page, one object at a time, for pipeline support.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [scriptblock]$PageRequest,

        [Parameter(Mandatory)]
        [string]$CmdletName,

        [Parameter(Mandatory)]
        [int]$PageSize,

        [switch]$Force,

        [string]$ItemNoun = 'items',

        [string]$NarrowHint
    )

    $currentPage = 1
    $pagesFetched = 0
    $warnedLargeSet = $false
    do {
        $response = & $PageRequest $currentPage
        $pagesFetched++

        # Most endpoints return a paginated envelope with an .items collection; fall back to the raw
        # response for any that return a bare array.
        $items = if ($null -ne $response.items) { $response.items } else { $response }

        # Warn up front (once) when -All is auto-paginating a large result set, so a long-running
        # sequential fetch is not a surprise. Only endpoints that report a total can trigger this.
        if (-not $warnedLargeSet -and $null -ne $response.totalCount -and $response.totalCount -ge $script:JIMAllWarningThreshold) {
            Write-Warning "$CmdletName -All is fetching a large result set ($($response.totalCount) $ItemNoun across $($response.totalPages) pages); this may take a while."
            $warnedLargeSet = $true
        }

        foreach ($item in $items) {
            $item
        }

        $hasMore = $response.hasNextPage -eq $true

        # Enforce the -All page ceiling unless -Force is supplied, so a runaway fetch cannot hammer the
        # API sequentially without bound (and cannot trip the API's own page-depth cap mid-loop). Stop
        # clearly rather than truncating silently.
        if ($hasMore -and -not $Force -and $pagesFetched -ge $script:JIMMaxAllPages) {
            $hint = if ($NarrowHint) { ", or $NarrowHint" } else { '' }
            Write-Warning "$CmdletName -All stopped after $script:JIMMaxAllPages pages (~$($script:JIMMaxAllPages * $PageSize) $ItemNoun); more results remain (total pages: $($response.totalPages)). Re-run with -Force to fetch everything$hint."
            break
        }

        if ($hasMore) {
            $currentPage++
            Write-Verbose "Fetching page $currentPage of $($response.totalPages)..."
        }
    } while ($hasMore)
}
