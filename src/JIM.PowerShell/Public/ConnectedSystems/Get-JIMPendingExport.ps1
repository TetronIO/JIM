# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMPendingExport {
    <#
    .SYNOPSIS
        Gets Pending Exports from JIM.

    .DESCRIPTION
        Retrieves Pending Exports for a Connected System. Pending Exports represent changes
        that need to be applied to a connected system, created when metaverse objects change
        and need to be synchronised to target systems.

        Can list all pending exports for a connected system, retrieve a specific pending export
        by ID, or get paginated attribute changes for a specific attribute.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System to retrieve pending exports for.

    .PARAMETER Id
        The unique identifier (GUID) of a specific Pending Export to retrieve.

    .PARAMETER AttributeName
        When specified with -Id, retrieves paginated attribute value changes for the named
        attribute. Use this to page through large multi-valued attribute changes (e.g. member
        additions) that are capped in the detail response.

    .PARAMETER Search
        Optional search text to filter results. For listing, filters by target object,
        source MVO, or error message. For attribute changes, filters by value.

    .PARAMETER Page
        Page number for pagination. Defaults to 1.

    .PARAMETER PageSize
        Number of items per page. Defaults to 50.

    .PARAMETER All
        If specified, automatically retrieves all pages of results. Fetches at most 1000
        pages before stopping with a warning; use -Force to fetch beyond the cap.

    .PARAMETER Force
        Override the -All page ceiling (1000 pages) and fetch every page regardless of how
        large the result set is. Only valid with -All.

    .OUTPUTS
        PSCustomObject representing Pending Export(s) or attribute value changes.

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
    [OutputType([PSCustomObject])]
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
