# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConfigurationChangePreviewDelta {
    <#
    .SYNOPSIS
        Reads the object-level detail behind a Configuration Change Preview.

    .DESCRIPTION
        Returns the individual objects a preview says would be affected: which object, what would happen
        to it, and the values on either side of the change.

        These rows may be a sample. Unless the preview was started with -FullDataSet, each summary group
        keeps at most a capped number of rows, and a group's own ObjectCount is the exact figure. Check
        the group's DeltasSampled flag (from Get-JIMConfigurationChangePreview) before treating a list of
        rows as the complete set of affected objects.

    .PARAMETER ActivityId
        The preview's Activity id.

    .PARAMETER GroupId
        Restrict the rows to one summary group, as listed in the preview's Groups collection. Omitted
        returns rows across every group.

    .PARAMETER Search
        Restrict the rows to those matching this text across the columns the detail carries.

    .PARAMETER Page
        The page of rows to return. Defaults to 1.

    .PARAMETER PageSize
        Rows per page, up to 100. Defaults to 50.

    .PARAMETER All
        Fetch every page. Prefer -GroupId or -Search first: a full preview can carry a great many rows.

    .PARAMETER Force
        Lift the client-side page cap that -All otherwise stops at.

    .OUTPUTS
        PSCustomObject per row, with ObjectDisplayName, ObjectTypeName, AttributeName, OldValue,
        NewValue, TransitionType and the identifiers of the objects concerned.

    .EXAMPLE
        Get-JIMConfigurationChangePreviewDelta -ActivityId $activityId

        Reads the first page of object-level detail for a preview.

    .EXAMPLE
        $preview = Get-JIMConfigurationChangePreview -ActivityId $activityId
        $group = $preview.Groups | Where-Object { $_.TransitionType -eq 'WouldBecomeDeletionEligible' }
        Get-JIMConfigurationChangePreviewDelta -ActivityId $activityId -GroupId $group.Id -All

        Lists the objects that would become eligible for deletion.

    .LINK
        New-JIMConfigurationChangePreview
        Get-JIMConfigurationChangePreview
    #>
    [CmdletBinding(DefaultParameterSetName = 'Page')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$ActivityId,

        [Parameter()]
        [guid]$GroupId,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$Search,

        [Parameter(ParameterSetName = 'Page')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter()]
        [ValidateRange(1, 100)]
        [int]$PageSize = 50,

        [Parameter(Mandatory, ParameterSetName = 'All')]
        [switch]$All,

        [Parameter(ParameterSetName = 'All')]
        [switch]$Force
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $baseQueryParams = @("pageSize=$PageSize")

        if ($PSBoundParameters.ContainsKey('GroupId')) {
            $baseQueryParams += "groupId=$GroupId"
        }

        if ($Search) {
            $baseQueryParams += "search=$([System.Uri]::EscapeDataString($Search))"
        }

        # One page-request closure serves both the single-page and auto-paginating paths; the shared
        # helper owns the -All loop, the page cap and the warnings.
        $pageRequest = {
            param($p)
            $queryParams = @("page=$p") + $baseQueryParams
            Invoke-JIMApi -Endpoint "/api/v1/previews/$ActivityId/deltas?$($queryParams -join '&')"
        }

        try {
            if ($All) {
                Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMConfigurationChangePreviewDelta' `
                    -PageSize $PageSize -Force:$Force -ItemNoun 'preview detail rows' `
                    -NarrowHint 'narrow the query with -GroupId or -Search'
            }
            else {
                $response = & $pageRequest $Page
                # Check the property exists rather than that it is truthy: an empty page is a valid answer.
                $rows = if ($null -ne $response.items) { $response.items } else { $response }
                foreach ($row in $rows) {
                    $row
                }
            }
        }
        catch {
            Write-Error "Failed to retrieve object-level detail for preview ${ActivityId}: $_"
        }
    }
}
