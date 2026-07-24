# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMActivityChildren {
    <#
    .SYNOPSIS
        Gets child Activities for a parent Activity in JIM.

    .DESCRIPTION
        Retrieves child activities that were spawned by a parent activity. For example,
        a schedule execution activity may have child activities for each individual
        run profile step. Returns a paginated summary of child Activities, newest-first
        creation order ascending. Use -All to page automatically and return every child
        Activity.

        The API returns a paginated response envelope, but this cmdlet unwraps it
        internally and still emits one object per child Activity to the pipeline, so
        existing scripts that pipe its output are unaffected by the change to a
        paginated response shape.

    .PARAMETER Id
        The unique identifier (GUID) of the parent Activity.

    .PARAMETER All
        Automatically paginate through all child Activities and return every one. Cannot be used with -Page.
        Fetches at most 1000 pages before stopping with a warning; use -Force to fetch beyond the cap.

    .PARAMETER Force
        Override the -All page ceiling (1000 pages) and fetch every page regardless of how many child
        Activities there are. Only valid with -All.

    .PARAMETER Page
        Page number for the child Activity list. Defaults to 1. Cannot be used with -All.

    .PARAMETER PageSize
        Number of child Activities per page. Defaults to 50. Maximum is 100.

    .OUTPUTS
        PSCustomObject representing each child Activity.

    .EXAMPLE
        Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

        Gets the first page of child activities for the specified parent activity.

    .EXAMPLE
        Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -All

        Returns every child activity for the specified parent activity, paginating automatically.

    .EXAMPLE
        Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -All -Force

        Returns every child activity, overriding the 1000-page safety cap for a very large parent Activity.

    .EXAMPLE
        Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" |
            Get-JIMActivityChildren

        Gets child activities for a parent activity via pipeline.

    .LINK
        Get-JIMActivity
        Get-JIMActivityStats
    #>
    [CmdletBinding(DefaultParameterSetName = 'Page')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$Id,

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
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        try {
            # The shared helper owns the -All loop, the page cap and the warnings (issue #487); a single
            # page is fetched directly. Both paths unwrap the paginated response envelope so callers keep
            # receiving one object per child Activity.
            $pageRequest = {
                param($p)
                Write-Verbose "Getting child activities for Activity: $Id (Page: $p, PageSize: $PageSize)"
                Invoke-JIMApi -Endpoint "/api/v1/activities/$Id/children?page=$p&pageSize=$PageSize"
            }

            if ($All) {
                Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMActivityChildren' -PageSize $PageSize -Force:$Force `
                    -ItemNoun 'child activities'
            }
            else {
                $response = & $pageRequest $Page
                foreach ($item in $response.items) {
                    $item
                }
            }
        }
        catch {
            Write-Error "Failed to get child activities: $_"
        }
    }
}
