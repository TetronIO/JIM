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
        If specified, automatically retrieves all pages of attribute values.

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

    .LINK
        Get-JIMConnectedSystem
        Get-JIMPendingExport
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'AttributeValues', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'AttributeValuesAll', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'Count', ValueFromPipelineByPropertyName)]
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
        [string]$Search,

        [Parameter(ParameterSetName = 'AttributeValues')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'AttributeValues')]
        [Parameter(ParameterSetName = 'AttributeValuesAll')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 50,

        [Parameter(Mandatory, ParameterSetName = 'AttributeValuesAll')]
        [switch]$All,

        [Parameter(Mandatory, ParameterSetName = 'Count')]
        [switch]$Count,

        [Parameter(ParameterSetName = 'Count')]
        [int]$ObjectTypeId,

        [Parameter(ParameterSetName = 'Count')]
        [int]$PartitionId
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
                $hasMore = $true
                $encodedAttrName = [System.Uri]::EscapeDataString($AttributeName)

                while ($hasMore) {
                    $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$Id/attributes/$encodedAttrName/values?page=$currentPage&pageSize=$PageSize"
                    if ($Search) {
                        $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
                    }

                    $response = Invoke-JIMApi -Endpoint $endpoint
                    foreach ($item in $response.items) {
                        $item
                    }

                    $hasMore = $response.hasNextPage
                    $currentPage++
                }
            }
        }
    }
}
