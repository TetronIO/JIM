# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemObjectUnresolvedReference {
    <#
    .SYNOPSIS
        Gets the unresolved references on a Connected System Object in JIM, with the likely cause of each.

    .DESCRIPTION
        Retrieves the reference values on a Connected System Object that could not be linked to another
        Connected System Object (for example group members outside Container Scope), each with its likely
        cause, where the fix lies (JIM's configuration or the source system) and the next step in words.

        Likely causes are judged against the Connected System's current Container Scope, so they describe
        the object as it stands now: widening the scope changes the answer the next time you ask.

        By default, returns a single page of references, ordered by attribute name and then value. Use -All
        to paginate through every reference, or -Summary to get the counts by likely cause and every
        distinct next step instead of the references themselves.

        For safety, -All fetches at most 1000 pages and then stops with a warning. Supply -Force to override
        the cap and fetch every page.

    .PARAMETER ConnectedSystemId
        The unique identifier (integer) of the Connected System the object belongs to.

    .PARAMETER Id
        The unique identifier (GUID) of the Connected System Object.

    .PARAMETER Search
        Optional case-insensitive text matched against each reference's value and attribute name.

    .PARAMETER Summary
        Return the summary instead of the references: TotalCount, Causes (each a Cause, FixLocation and
        Count), Fixes (every distinct next step with its Count and Suggestion, most values first) and
        LargestFix (the change to JIM's configuration that would address the most values, or null).

    .PARAMETER All
        Automatically paginate through all results and return every reference. Cannot be used with -Page.
        Fetches at most 1000 pages before stopping with a warning; use -Force to fetch beyond the cap.

    .PARAMETER Force
        Override the -All page ceiling (1000 pages). Only valid with -All.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1. Cannot be used with -All.

    .PARAMETER PageSize
        Number of items per page. Defaults to 50. Maximum is 100.

    .OUTPUTS
        Without -Summary, PSCustomObject per reference: AttributeName, Value, Cause, FixLocation,
        ContainerExternalId, PartitionExternalId, CandidateObjectTypeNames and Suggestion.
        With -Summary, one PSCustomObject: TotalCount, Causes, Fixes and LargestFix.

    .EXAMPLE
        Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId 1 -Id "12345678-1234-1234-1234-123456789abc"

        Gets the first page of unresolved references on the object, with the likely cause of each.

    .EXAMPLE
        Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId 1 -Id "12345678-1234-1234-1234-123456789abc" -Summary

        Gets how many references are unresolved, by likely cause, and every distinct next step.

    .EXAMPLE
        Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId 1 -Id "12345678-1234-1234-1234-123456789abc" -All |
            Where-Object { $_.FixLocation -eq 'Jim' } |
            Group-Object ContainerExternalId

        Gets every unresolved reference that a change to JIM's configuration could fix, grouped by the
        Container concerned.

    .EXAMPLE
        Get-JIMConnectedSystemObjectUnresolvedReference -ConnectedSystemId 1 -Id "12345678-1234-1234-1234-123456789abc" -Search "OU=Contractors"

        Gets the unresolved references whose value or attribute name contains "OU=Contractors".

    .LINK
        Get-JIMConnectedSystemObject

    .LINK
        Get-JIMConnectedSystemUnresolvedReferenceCount
    #>
    [CmdletBinding(DefaultParameterSetName = 'Page')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(ParameterSetName = 'Page')]
        [Parameter(ParameterSetName = 'All')]
        [string]$Search,

        [Parameter(Mandatory, ParameterSetName = 'Summary')]
        [switch]$Summary,

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

        $baseEndpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$Id/unresolved-references"

        if ($Summary) {
            Write-Verbose "Getting the unresolved reference summary for CSO $Id in connected system $ConnectedSystemId"
            Invoke-JIMApi -Endpoint "$baseEndpoint/summary"
            return
        }

        # The shared helper owns the -All loop, the page cap and the warnings (issue #487); a single page is
        # fetched directly.
        $pageRequest = {
            param($p)
            Write-Verbose "Getting unresolved references for CSO $Id in connected system $ConnectedSystemId (Page: $p, PageSize: $PageSize)"
            $endpoint = "$($baseEndpoint)?page=$p&pageSize=$PageSize"
            if ($Search) {
                $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
            }
            Invoke-JIMApi -Endpoint $endpoint
        }

        if ($All) {
            Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMConnectedSystemObjectUnresolvedReference' -PageSize $PageSize -Force:$Force `
                -ItemNoun 'unresolved references' -NarrowHint 'filter with -Search'
        }
        else {
            $response = & $pageRequest $Page
            foreach ($item in $response.items) {
                $item
            }
        }
    }
}
