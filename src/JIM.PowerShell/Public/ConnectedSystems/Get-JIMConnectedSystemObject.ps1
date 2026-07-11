# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemObject {
    <#
    .SYNOPSIS
        Gets a Connected System Object from JIM.

    .DESCRIPTION
        Retrieves a Connected System Object (CSO) by ID, with capped multi-valued attribute
        values. For attributes with more than 10 values, use the -AttributeName parameter
        to page through all values.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System that owns the object.

    .PARAMETER Id
        The unique identifier (GUID) of the Connected System Object to retrieve.

    .PARAMETER AttributeName
        When specified with -Id, retrieves paginated attribute values for the named
        attribute. Use this to page through large multi-valued attributes (e.g. member)
        that are capped in the detail response.

    .PARAMETER Search
        Optional search text to filter attribute values.

    .PARAMETER Page
        Page number for pagination (attribute values only). Defaults to 1.

    .PARAMETER PageSize
        Number of items per page (attribute values only). Defaults to 50.

    .PARAMETER All
        If specified, automatically retrieves all pages (of attribute values, or of
        Connected System Object headers). Fetches at most 1000 pages before stopping
        with a warning; use -Force to fetch beyond the cap. When used on a large result
        set, a warning is emitted so a long-running sequential fetch is not a surprise.

    .PARAMETER Force
        Override the -All page ceiling (1000 pages) and fetch every page regardless of
        how large the result set is. Only valid with -All.

    .OUTPUTS
        PSCustomObject representing the Connected System Object or attribute values.

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Id "3934ff12-4996-42c0-a396-41e17ac47af7"

        Gets the detail of a specific Connected System Object with capped attribute values.

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Id "3934ff12-4996-42c0-a396-41e17ac47af7" -AttributeName "member"

        Gets the first page of "member" attribute values for the object.

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Id "3934ff12-4996-42c0-a396-41e17ac47af7" -AttributeName "member" -All

        Gets all "member" attribute values (auto-paginates).

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Id "3934ff12-4996-42c0-a396-41e17ac47af7" -AttributeName "member" -Search "admin"

        Searches "member" attribute values containing "admin".

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Count

        Gets the total count of objects in the connector space for Connected System 1.

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Count -ObjectTypeId 2

        Gets the count of objects of type 2 in Connected System 1.

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Count -PartitionId 5

        Gets the count of objects in partition 5 of Connected System 1.

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1

        Gets the first page of Connected System Object headers for Connected System 1.

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Search "smith" -Status Obsolete

        Gets Obsolete objects matching "smith" in Connected System 1.

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -All

        Gets every Connected System Object header for Connected System 1 (auto-paginates).

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -All -Force

        Gets every Connected System Object header for Connected System 1, overriding the
        1000-page safety cap for very large connector spaces (over ~100,000 objects).

    .LINK
        Get-JIMConnectedSystem
        Get-JIMPendingExport
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'AttributeValues', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'AttributeValuesAll', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'Count', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'List', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'ListAll', ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'AttributeValues', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'AttributeValuesAll', ValueFromPipelineByPropertyName)]
        [guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'AttributeValues')]
        [Parameter(Mandatory, ParameterSetName = 'AttributeValuesAll')]
        [ValidateNotNullOrEmpty()]
        [string]$AttributeName,

        [Parameter(ParameterSetName = 'AttributeValues')]
        [Parameter(ParameterSetName = 'AttributeValuesAll')]
        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [string]$Search,

        [Parameter(ParameterSetName = 'AttributeValues')]
        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'AttributeValues')]
        [Parameter(ParameterSetName = 'AttributeValuesAll')]
        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 50,

        [Parameter(Mandatory, ParameterSetName = 'AttributeValuesAll')]
        [Parameter(Mandatory, ParameterSetName = 'ListAll')]
        [switch]$All,

        [Parameter(ParameterSetName = 'AttributeValuesAll')]
        [Parameter(ParameterSetName = 'ListAll')]
        [switch]$Force,

        [Parameter(Mandatory, ParameterSetName = 'Count')]
        [switch]$Count,

        [Parameter(ParameterSetName = 'Count')]
        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [int]$ObjectTypeId,

        [Parameter(ParameterSetName = 'Count')]
        [int]$PartitionId,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateSet('Normal', 'Obsolete', 'PendingProvisioning')]
        [string]$Status,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [ValidateSet('NotJoined', 'Projected', 'Provisioned', 'Joined')]
        [string]$JoinType,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [string]$SortBy,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [switch]$Ascending
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        switch ($PSCmdlet.ParameterSetName) {
            'Count' {
                Write-Verbose "Getting connector space count for Connected System $ConnectedSystemId"

                $queryParams = @()

                if ($PSBoundParameters.ContainsKey('ObjectTypeId')) {
                    $queryParams += "objectTypeId=$ObjectTypeId"
                }

                if ($PSBoundParameters.ContainsKey('PartitionId')) {
                    $queryParams += "partitionId=$PartitionId"
                }

                $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/count"
                if ($queryParams.Count -gt 0) {
                    $endpoint += "?" + ($queryParams -join '&')
                }

                $result = Invoke-JIMApi -Endpoint $endpoint
                $result
            }

            'ById' {
                Write-Verbose "Getting Connected System Object $Id from Connected System $ConnectedSystemId"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$Id"
                $result
            }

            'AttributeValues' {
                Write-Verbose "Getting attribute values for '$AttributeName' on CSO $Id (Page: $Page, PageSize: $PageSize)"
                $encodedAttrName = [System.Uri]::EscapeDataString($AttributeName)
                $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$Id/attributes/$encodedAttrName/values?page=$Page&pageSize=$PageSize"
                if ($Search) {
                    $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
                }

                $response = Invoke-JIMApi -Endpoint $endpoint
                foreach ($item in $response.items) {
                    $item
                }
            }

            'AttributeValuesAll' {
                Write-Verbose "Getting all attribute values for '$AttributeName' on CSO $Id"
                $currentPage = 1
                $pagesFetched = 0
                $hasMore = $true
                $encodedAttrName = [System.Uri]::EscapeDataString($AttributeName)

                while ($hasMore) {
                    $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$Id/attributes/$encodedAttrName/values?page=$currentPage&pageSize=$PageSize"
                    if ($Search) {
                        $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
                    }

                    $response = Invoke-JIMApi -Endpoint $endpoint
                    $pagesFetched++
                    foreach ($item in $response.items) {
                        $item
                    }

                    $hasMore = $response.hasNextPage

                    # Enforce the -All page ceiling unless -Force is supplied; stop clearly rather than truncating silently.
                    if ($hasMore -and -not $Force -and $pagesFetched -ge $script:JIMMaxAllPages) {
                        Write-Warning "Get-JIMConnectedSystemObject -All stopped after $script:JIMMaxAllPages pages (~$($script:JIMMaxAllPages * $PageSize) attribute values); more values remain (total pages: $($response.totalPages)). Re-run with -Force to fetch everything, or filter with -Search."
                        break
                    }

                    $currentPage++
                }
            }

            'List' {
                Write-Verbose "Getting Connected System Objects for Connected System $ConnectedSystemId (Page: $Page, PageSize: $PageSize)"
                $endpoint = Get-JIMConnectedSystemObjectListEndpoint -ConnectedSystemId $ConnectedSystemId -Page $Page -PageSize $PageSize `
                    -Search $Search -Status $Status -ObjectTypeId $ObjectTypeId -JoinType $JoinType -SortBy $SortBy -Ascending:$Ascending

                $response = Invoke-JIMApi -Endpoint $endpoint
                foreach ($item in $response.items) {
                    $item
                }
            }

            'ListAll' {
                Write-Verbose "Getting all Connected System Objects for Connected System $ConnectedSystemId"
                $currentPage = 1
                $pagesFetched = 0
                $warnedLargeSet = $false
                $hasMore = $true

                while ($hasMore) {
                    $endpoint = Get-JIMConnectedSystemObjectListEndpoint -ConnectedSystemId $ConnectedSystemId -Page $currentPage -PageSize $PageSize `
                        -Search $Search -Status $Status -ObjectTypeId $ObjectTypeId -JoinType $JoinType -SortBy $SortBy -Ascending:$Ascending

                    $response = Invoke-JIMApi -Endpoint $endpoint
                    $pagesFetched++

                    # Warn up front (once) when -All is auto-paginating a large result set.
                    if (-not $warnedLargeSet -and $response.totalCount -ge $script:JIMAllWarningThreshold) {
                        Write-Warning "Get-JIMConnectedSystemObject -All is fetching a large result set ($($response.totalCount) objects across $($response.totalPages) pages); this may take a while."
                        $warnedLargeSet = $true
                    }

                    foreach ($item in $response.items) {
                        $item
                    }

                    $hasMore = $response.hasNextPage

                    # Enforce the -All page ceiling unless -Force is supplied; stop clearly rather than truncating silently.
                    if ($hasMore -and -not $Force -and $pagesFetched -ge $script:JIMMaxAllPages) {
                        Write-Warning "Get-JIMConnectedSystemObject -All stopped after $script:JIMMaxAllPages pages (~$($script:JIMMaxAllPages * $PageSize) objects); more results remain (total pages: $($response.totalPages)). Re-run with -Force to fetch everything, or narrow the query with -Search, -Status or -ObjectTypeId."
                        break
                    }

                    $currentPage++
                }
            }
        }
    }
}
