# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMCausalCohortMember {
    <#
    .SYNOPSIS
        Gets the members of one cohort of an execution item's consequences.

    .DESCRIPTION
        Get-JIMActivityExecutionItem -Consequences groups what an execution item went on to cause into cohorts with
        exact counts ("4218 Groups") and returns only the first members of each. This pages through every member of
        one cohort, earliest first and then by name, optionally narrowed to members whose name contains the search
        text.

        The cohort is named by its CohortKey, as the consequences returned it. Consequence rows can be piped straight
        in: their ExecutionItemId and CohortKey bind by name.

    .PARAMETER ExecutionItemId
        The execution item whose consequences the cohort belongs to.

    .PARAMETER CohortKey
        The cohort's key, from Get-JIMActivityExecutionItem -Consequences, whose rows can be piped straight in.

    .PARAMETER Search
        Text a member's name must contain, case-insensitively.

    .PARAMETER Page
        The page to return.

    .PARAMETER PageSize
        How many members per page. With -All, 100 unless it is given.

    .PARAMETER All
        Return every member, fetching page after page.

    .PARAMETER Force
        With -All, fetches every page however many there are.

    .OUTPUTS
        Each member of the cohort, earliest first and then by name.

    .EXAMPLE
        Get-JIMCausalCohortMember -ExecutionItemId "b2c3d4e5-f6a7-8901-bcde-f12345678901" -CohortKey $key -All

        Lists every member of one cohort.

    .EXAMPLE
        Get-JIMActivityExecutionItem -Id "b2c3d4e5-f6a7-8901-bcde-f12345678901" -Consequences |
            Where-Object { $_.Level -eq 1 -and $_.Attribute -eq 'Static Members' } |
            Get-JIMCausalCohortMember -All |
            Select-Object DisplayName, Occurred

        Lists every Group a deletion removed a member from.

    .LINK
        Get-JIMActivityExecutionItem
    #>
    [CmdletBinding(DefaultParameterSetName = 'Page')]
    [OutputType('JIM.CausalConsequenceMemberDto')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$ExecutionItemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [ValidateNotNullOrEmpty()]
        [string]$CohortKey,

        [string]$Search,

        [Parameter(ParameterSetName = 'Page')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

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

        $endpoint = "/api/v1/activities/items/$ExecutionItemId/consequences/cohorts/$([uri]::EscapeDataString($CohortKey))/members"
        $searchSuffix = if ($Search) { "&search=$([uri]::EscapeDataString($Search))" } else { '' }

        if ($All) {
            # -All asks for whole pages at the largest size unless the caller chose one.
            $allPageSize = if ($PSBoundParameters.ContainsKey('PageSize')) { $PageSize } else { 100 }
            Write-Verbose "Getting every member of consequence cohort $CohortKey of execution item $ExecutionItemId"
            $pageRequest = {
                param($p)
                Invoke-JIMApi -Endpoint "$($endpoint)?page=$p&pageSize=$allPageSize$searchSuffix"
            }

            Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMCausalCohortMember' -PageSize $allPageSize -Force:$Force `
                -ItemNoun 'cohort members' -NarrowHint 'narrow it with -Search'
            return
        }

        Write-Verbose "Getting consequence cohort members (Page: $Page, PageSize: $PageSize)"
        $response = Invoke-JIMApi -Endpoint "$($endpoint)?page=$Page&pageSize=$PageSize$searchSuffix"
        foreach ($item in $response.items) {
            $item
        }
    }
}
