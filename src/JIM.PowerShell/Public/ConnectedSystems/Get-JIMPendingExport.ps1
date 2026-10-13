# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMPendingExport {
    <#
    .SYNOPSIS
        Gets a Connected System's Pending Exports.

    .DESCRIPTION
        Gets the Pending Exports waiting to be written to a Connected System: a page of them, every one with -All,
        one by ID, or how many there are with -Count.

        A Pending Export read by ID carries at most 10 value changes for each multi-valued attribute. -AttributeName
        pages through the rest of one attribute's changes.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER Id
        The ID of the Pending Export to get.

    .PARAMETER AttributeName
        With -Id, gets the value changes for this attribute a page at a time, for a multi-valued attribute such as
        member whose changes the Pending Export read by ID cuts short.

    .PARAMETER Search
        Returns only the Pending Exports whose target object, source Metaverse Object or error matches this text; or
        with -AttributeName, only the changes whose value matches it.

    .PARAMETER Page
        The page to get.

    .PARAMETER PageSize
        How many Pending Exports or changes a page holds.

    .PARAMETER All
        Fetches every page instead of one. Stops after 1000 pages with a warning; -Force fetches the rest.

    .PARAMETER Force
        With -All, fetches every page, past the 1000-page limit.

    .PARAMETER Count
        Returns how many Pending Exports there are, rather than the Pending Exports.

    .PARAMETER ChangeType
        With -Count, counts only the Pending Exports that make this kind of change.

    .PARAMETER Status
        With -Count, counts only the Pending Exports with this status.

    .OUTPUTS
        The Pending Exports, the one Pending Export, its value changes for -AttributeName, or with -Count how many
        there are.

    .EXAMPLE
        Get-JIMPendingExport -ConnectedSystemId 2

        Gets the first page of pending exports for Connected System 2.

    .EXAMPLE
        Get-JIMPendingExport -ConnectedSystemId 2 -All

        Gets all pending exports for Connected System 2 (auto-paginates).

    .EXAMPLE
        Get-JIMPendingExport -ConnectedSystemId 2 -All -Force

        Gets all pending exports, overriding the 1000-page safety cap for a very large export backlog.

    .EXAMPLE
        Get-JIMPendingExport -Id "15aa3e6f-9f82-44a8-a04d-0245d3c76198"

        Gets the detail of a specific pending export with capped attribute changes.

    .EXAMPLE
        Get-JIMPendingExport -Id "15aa3e6f-9f82-44a8-a04d-0245d3c76198" -AttributeName "member"

        Gets paginated attribute changes for the "member" attribute on a pending export.

    .EXAMPLE
        Get-JIMPendingExport -Id "15aa3e6f-9f82-44a8-a04d-0245d3c76198" -AttributeName "member" -All

        Gets all attribute changes for the "member" attribute (auto-paginates).

    .EXAMPLE
        Get-JIMPendingExport -ConnectedSystemId 2 -Count

        Gets the total count of pending exports for Connected System 2.

    .EXAMPLE
        Get-JIMPendingExport -ConnectedSystemId 2 -Count -ChangeType Create

        Gets the count of pending Create exports for Connected System 2.

    .EXAMPLE
        Get-JIMPendingExport -ConnectedSystemId 2 -Count -Status Failed

        Gets the count of failed pending exports for Connected System 2.

    .LINK
        Get-JIMConnectedSystem
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.PendingExportHeader', ParameterSetName = 'List')]
    [OutputType('JIM.PendingExportHeader', ParameterSetName = 'ListAll')]
    [OutputType('JIM.PendingExportDetailDto', ParameterSetName = 'ById')]
    [OutputType('JIM.PendingExportAttributeValueChangeDto', ParameterSetName = 'AttributeChanges')]
    [OutputType('JIM.PendingExportAttributeValueChangeDto', ParameterSetName = 'AttributeChangesAll')]
    [OutputType([int], ParameterSetName = 'Count')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'List', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'ListAll', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'Count', ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [Parameter(Mandatory, ParameterSetName = 'AttributeChanges')]
        [Parameter(Mandatory, ParameterSetName = 'AttributeChangesAll')]
        [guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'AttributeChanges')]
        [Parameter(Mandatory, ParameterSetName = 'AttributeChangesAll')]
        [ValidateNotNullOrEmpty()]
        [string]$AttributeName,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [Parameter(ParameterSetName = 'AttributeChanges')]
        [Parameter(ParameterSetName = 'AttributeChangesAll')]
        [string]$Search,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'AttributeChanges')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [Parameter(ParameterSetName = 'AttributeChanges')]
        [Parameter(ParameterSetName = 'AttributeChangesAll')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 50,

        [Parameter(Mandatory, ParameterSetName = 'ListAll')]
        [Parameter(Mandatory, ParameterSetName = 'AttributeChangesAll')]
        [switch]$All,

        [Parameter(ParameterSetName = 'ListAll')]
        [Parameter(ParameterSetName = 'AttributeChangesAll')]
        [switch]$Force,

        [Parameter(Mandatory, ParameterSetName = 'Count')]
        [switch]$Count,

        [Parameter(ParameterSetName = 'Count')]
        [ValidateSet('Create', 'Update', 'Delete')]
        [string]$ChangeType,

        [Parameter(ParameterSetName = 'Count')]
        [ValidateSet('Pending', 'ExportNotConfirmed', 'Executing', 'Failed', 'Exported')]
        [string]$Status
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        switch ($PSCmdlet.ParameterSetName) {
            'Count' {
                Write-Verbose "Getting pending exports count for Connected System $ConnectedSystemId"

                $queryParams = @()

                if ($ChangeType) {
                    $queryParams += "changeType=$ChangeType"
                }

                if ($Status) {
                    $queryParams += "status=$Status"
                }

                $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/pending-exports/count"
                if ($queryParams.Count -gt 0) {
                    $endpoint += "?" + ($queryParams -join '&')
                }

                $result = Invoke-JIMApi -Endpoint $endpoint
                $result
            }

            'ById' {
                Write-Verbose "Getting Pending Export: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/pending-exports/$Id"
                $result
            }

            'List' {
                Write-Verbose "Getting Pending Exports for Connected System $ConnectedSystemId (Page: $Page, PageSize: $PageSize)"
                $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/pending-exports?page=$Page&pageSize=$PageSize"
                if ($Search) {
                    $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
                }

                $response = Invoke-JIMApi -Endpoint $endpoint
                foreach ($item in $response.items) {
                    $item
                }
            }

            'ListAll' {
                Write-Verbose "Getting all Pending Exports for Connected System $ConnectedSystemId"
                $pageRequest = {
                    param($p)
                    $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/pending-exports?page=$p&pageSize=$PageSize"
                    if ($Search) {
                        $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
                    }
                    Invoke-JIMApi -Endpoint $endpoint
                }

                Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMPendingExport' -PageSize $PageSize -Force:$Force `
                    -ItemNoun 'pending exports' -NarrowHint 'filter with -Search'
            }

            'AttributeChanges' {
                Write-Verbose "Getting attribute changes for '$AttributeName' on Pending Export $Id (Page: $Page, PageSize: $PageSize)"
                $encodedAttrName = [System.Uri]::EscapeDataString($AttributeName)
                $endpoint = "/api/v1/synchronisation/pending-exports/$Id/attribute-changes/$encodedAttrName/values?page=$Page&pageSize=$PageSize"
                if ($Search) {
                    $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
                }

                $response = Invoke-JIMApi -Endpoint $endpoint
                foreach ($item in $response.items) {
                    $item
                }
            }

            'AttributeChangesAll' {
                Write-Verbose "Getting all attribute changes for '$AttributeName' on Pending Export $Id"
                $encodedAttrName = [System.Uri]::EscapeDataString($AttributeName)
                $pageRequest = {
                    param($p)
                    $endpoint = "/api/v1/synchronisation/pending-exports/$Id/attribute-changes/$encodedAttrName/values?page=$p&pageSize=$PageSize"
                    if ($Search) {
                        $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
                    }
                    Invoke-JIMApi -Endpoint $endpoint
                }

                Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMPendingExport' -PageSize $PageSize -Force:$Force `
                    -ItemNoun 'attribute changes' -NarrowHint 'filter with -Search'
            }
        }
    }
}
