# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemObjectUnresolvedReference {
    <#
    .SYNOPSIS
        Gets the unresolved references on a Connected System Object in JIM, with the likely cause of each.

    .DESCRIPTION
        Retrieves the reference values on a Connected System Object that could not be linked to another
        Connected System Object (for example group members outside Container Scope), each with its likely
        cause, where the fix lies (JIM's configuration or the source system), what is wrong and what to do.

        Likely causes are judged against the Connected System's current Container Scope, so they describe
        the object as it stands now: widening the scope changes the answer the next time you ask.

        By default, returns a single page of references, ordered by attribute name and then value. Use -All
        to paginate through every reference, or -Summary to get the counts by likely cause and every
        distinct next step instead of the references themselves.

        For safety, -All fetches at most 1000 pages and then stops with a warning. Supply -Force to override
        the cap and fetch every page.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System the object belongs to.

    .PARAMETER Id
        The ID of the Connected System Object.

    .PARAMETER Search
        Returns only the references whose value or attribute name contains this text, ignoring case.

    .PARAMETER Summary
        Returns the counts by likely cause and every distinct next step, rather than the references.

    .PARAMETER All
        Fetches every page instead of one. Stops after 1000 pages with a warning; -Force fetches the rest.

    .PARAMETER Force
        With -All, fetches every page, past the 1000-page limit.

    .PARAMETER Page
        The page to get.

    .PARAMETER PageSize
        How many references a page holds.

    .OUTPUTS
        The object's unresolved references, each with its likely cause and what to do; or with -Summary, how they
        split by cause and every distinct next step.

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
    [OutputType('JIM.ConnectedSystemObjectUnresolvedReference', ParameterSetName = 'Page')]
    [OutputType('JIM.ConnectedSystemObjectUnresolvedReference', ParameterSetName = 'All')]
    [OutputType('JIM.ConnectedSystemObjectUnresolvedReferenceSummary', ParameterSetName = 'Summary')]
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
