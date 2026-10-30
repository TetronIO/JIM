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

        Each changed attribute carries its first ten value changes and, in valueChangeTotal, how many there are. A
        change to a large group can carry one per member; Get-JIMMetaverseObjectChangeValue gets the rest.

        By default, returns a single page of results. Use -All to automatically paginate
        through all change records and return every row.

        For safety, -All fetches at most 1000 pages (~50,000 records at the default page
        size of 50) and then stops with a warning. Supply -Force to override the cap and
        fetch every page.

    .PARAMETER Id
        The ID of the Metaverse Object.

    .PARAMETER All
        Returns every page of changes rather than one. It stops after 1,000 pages, about 50,000 changes at the
        default page size, with a warning; -Force reads on. Cannot be used with -Page.

    .PARAMETER Force
        With -All, reads every page, past the 1,000-page limit.

    .PARAMETER Page
        The page of changes to return, counting from 1.

    .PARAMETER PageSize
        How many changes each page holds.

    .OUTPUTS
        One object per change, newest first.

    .EXAMPLE
        Get-JIMMetaverseObjectChangeHistory -Id "12345678-1234-1234-1234-123456789abc"

        Gets the first page (50 most recent changes) for the specified Metaverse Object.

    .EXAMPLE
        Get-JIMMetaverseObjectChangeHistory -Id "12345678-1234-1234-1234-123456789abc" -All

        Gets every change record for the specified Metaverse Object, paginating automatically.

    .EXAMPLE
        Get-JIMMetaverseObjectChangeHistory -Id "12345678-1234-1234-1234-123456789abc" -All -Force

        Gets every change record, overriding the 1000-page safety cap for a very long change history.

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
    [OutputType('JIM.MvoChangeHistoryDto')]
    param(
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
            Write-Verbose "Getting change history for Metaverse Object $Id (Page: $p, PageSize: $PageSize)"
            Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$Id/change-history?page=$p&pageSize=$PageSize"
        }

        if ($All) {
            Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMMetaverseObjectChangeHistory' -PageSize $PageSize -Force:$Force `
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
