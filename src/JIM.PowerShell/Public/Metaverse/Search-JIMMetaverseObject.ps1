# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Search-JIMMetaverseObject {
    <#
    .SYNOPSIS
        Searches for Metaverse Objects using a predefined search, returning lightweight headers.

    .DESCRIPTION
        Searches for Metaverse Objects using a predefined search definition. Returns lightweight
        headers with only the attributes configured in the predefined search, making it
        significantly faster than Get-JIMMetaverseObject for large datasets (100k+ objects).

        Use this cmdlet for fast list views and searches. Use Get-JIMMetaverseObject when you
        need full object details or custom attribute selection.

        By default, returns a single page of results. Use -All to automatically
        paginate through all results and return every matching object.

        For safety, -All fetches at most 1000 pages (~100,000 objects at the default
        page size of 100) and then stops with a warning. Supply -Force to override the
        cap and fetch every page. When -All is used on a large result set, a warning is
        emitted so a long-running sequential fetch is not a surprise.

    .PARAMETER PredefinedSearchUri
        The URI of the Predefined Search to run, such as users or groups. It decides which Object Type is searched
        and which attributes are returned.

    .PARAMETER Search
        Text to look for in the objects' text attribute values, ignoring case; part of a value is enough.

    .PARAMETER HasAttribute
        Returns only the objects that hold a value for this Metaverse Attribute, named exactly but ignoring case. A
        multi-valued attribute counts once. A name JIM does not recognise returns nothing.

    .PARAMETER SortBy
        The attribute to sort by. Without it, objects are sorted by when they were created.

    .PARAMETER SortDirection
        The sort order. desc is the default.

    .PARAMETER All
        Returns every page of results rather than one. It stops after 1,000 pages, about 100,000 objects at the
        default page size, with a warning; -Force reads on. Cannot be used with -Page.

    .PARAMETER Force
        With -All, reads every page, past the 1,000-page limit.

    .PARAMETER Page
        The page of results to return, counting from 1.

    .PARAMETER PageSize
        How many objects each page holds.

    .OUTPUTS
        One object in summary per match, with its display name, Object Type and the attributes the Predefined Search
        lists.

    .EXAMPLE
        Search-JIMMetaverseObject -PredefinedSearchUri "users"

        Searches for users using the "users" predefined search (first page).

    .EXAMPLE
        Search-JIMMetaverseObject -PredefinedSearchUri "users" -All

        Gets all users, automatically paginating through all results.

    .EXAMPLE
        Search-JIMMetaverseObject -PredefinedSearchUri "users" -All -Force

        Gets all users, overriding the 1000-page safety cap for very large result sets
        (over ~100,000 objects).

    .EXAMPLE
        Search-JIMMetaverseObject -PredefinedSearchUri "users" -Search "Young"

        Searches for users with "Young" in any attribute value.

    .EXAMPLE
        Search-JIMMetaverseObject -PredefinedSearchUri "users" -HasAttribute "costCentre"

        Gets users that hold a value for the costCentre attribute.

    .EXAMPLE
        Search-JIMMetaverseObject -PredefinedSearchUri "groups" -SortBy "Display Name"

        Gets groups sorted by display name.

    .EXAMPLE
        Search-JIMMetaverseObject -PredefinedSearchUri "users" -PageSize 10 -Page 2

        Gets page 2 of users with 10 results per page.

    .LINK
        Get-JIMMetaverseObject
        Get-JIMMetaverseObjectType
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.MetaverseObjectHeaderDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'List')]
        [Parameter(Mandatory, ParameterSetName = 'ListAll')]
        [ValidateNotNullOrEmpty()]
        [string]$PredefinedSearchUri,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [string]$Search,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [string]$HasAttribute,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [string]$SortBy,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateSet('asc', 'desc')]
        [string]$SortDirection = 'desc',

        [Parameter(Mandatory, ParameterSetName = 'ListAll')]
        [switch]$All,

        [Parameter(ParameterSetName = 'ListAll')]
        [switch]$Force,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 100
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Searching Metaverse Objects via predefined search: $PredefinedSearchUri"

        $encodedUri = [System.Uri]::EscapeDataString($PredefinedSearchUri)

        # Build base query parameters (excluding page, which varies during pagination)
        $baseQueryParams = @(
            "pageSize=$PageSize",
            "sortDirection=$SortDirection"
        )

        if ($Search) {
            $baseQueryParams += "search=$([System.Uri]::EscapeDataString($Search))"
        }

        if ($HasAttribute) {
            $baseQueryParams += "hasAttribute=$([System.Uri]::EscapeDataString($HasAttribute))"
        }

        if ($SortBy) {
            $baseQueryParams += "sortBy=$([System.Uri]::EscapeDataString($SortBy))"
        }

        # One page-request closure serves both the single-page (List) and auto-paginating (ListAll)
        # paths; the shared helper owns the -All loop, the page cap and the warnings (issue #487).
        $pageRequest = {
            param($p)
            $queryParams = @("page=$p") + $baseQueryParams
            Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/search/$encodedUri`?$($queryParams -join '&')"
        }

        if ($All) {
            Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Search-JIMMetaverseObject' -PageSize $PageSize -Force:$Force `
                -ItemNoun 'objects' -NarrowHint 'narrow the query with -Search or -HasAttribute'
        }
        else {
            $response = & $pageRequest $Page
            # Handle paginated response
            $objects = if ($null -ne $response.items) { $response.items } else { $response }
            # Output each object individually for pipeline support
            foreach ($obj in $objects) {
                $obj
            }
        }
    }
}
