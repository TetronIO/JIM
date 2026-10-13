# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemObject {
    <#
    .SYNOPSIS
        Gets the Connected System Objects in a Connected System's Connector Space.

    .DESCRIPTION
        Gets a Connected System's Connected System Objects: a page of them, every one with -All, one by ID, or how
        many there are with -Count. Filters narrow the list.

        An object read by ID carries at most 10 values of each multi-valued attribute. -AttributeName pages through
        the rest of one attribute's values.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER Id
        The ID of the Connected System Object to get.

    .PARAMETER AttributeName
        With -Id, gets the values of this attribute a page at a time, for a multi-valued attribute such as member
        whose values the object read by ID cuts short.

    .PARAMETER Search
        Returns only the objects, or with -AttributeName the values, that match this text.

    .PARAMETER Page
        The page to get.

    .PARAMETER PageSize
        How many objects or values a page holds.

    .PARAMETER All
        Fetches every page instead of one. Stops after 1000 pages with a warning; -Force fetches the rest.

    .PARAMETER Force
        With -All, fetches every page, past the 1000-page limit.

    .PARAMETER Count
        Returns how many Connected System Objects there are, rather than the objects.

    .PARAMETER ObjectTypeId
        Returns or counts only the objects of the Object Type with this ID.

    .PARAMETER PartitionId
        With -Count, counts only the objects in the partition with this ID.

    .PARAMETER Status
        Returns only the objects with this status.

    .PARAMETER JoinType
        Returns only the objects joined this way.

    .PARAMETER SortBy
        Sorts the list by ExternalId, SecondaryExternalId, DisplayName, Type, DateJoined, Status or JoinType. Newest
        first when omitted.

    .PARAMETER Ascending
        Sorts in ascending order rather than descending.

    .PARAMETER HasUnresolvedReference
        Returns only the objects holding a reference value JIM could not resolve to another Connected System Object.
        Get-JIMConnectedSystemObjectUnresolvedReference says why, for one object.

    .OUTPUTS
        The objects, the one object, its values of -AttributeName, or with -Count how many objects there are.

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
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -HasUnresolvedReference -All |
            Sort-Object UnresolvedReferenceCount -Descending |
            Select-Object DisplayName, TypeName, UnresolvedReferenceCount -First 10

        Lists the ten objects in Connected System 1 holding the most unresolved references.

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -All

        Gets every Connected System Object header for Connected System 1 (auto-paginates).

    .EXAMPLE
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -All -Force

        Gets every Connected System Object header for Connected System 1, overriding the
        1000-page safety cap for very large connector spaces (over about 50,000 objects at the default page size of 50).

    .LINK
        Get-JIMConnectedSystem
        Get-JIMPendingExport
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.ConnectedSystemObjectHeader', ParameterSetName = 'List')]
    [OutputType('JIM.ConnectedSystemObjectHeader', ParameterSetName = 'ListAll')]
    [OutputType('JIM.ConnectedSystemObjectDetailDto', ParameterSetName = 'ById')]
    [OutputType('JIM.ConnectedSystemObjectAttributeValueDto', ParameterSetName = 'AttributeValues')]
    [OutputType('JIM.ConnectedSystemObjectAttributeValueDto', ParameterSetName = 'AttributeValuesAll')]
    [OutputType([int], ParameterSetName = 'Count')]
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
        [switch]$Ascending,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'ListAll')]
        [switch]$HasUnresolvedReference
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
                $encodedAttrName = [System.Uri]::EscapeDataString($AttributeName)
                $pageRequest = {
                    param($p)
                    $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$Id/attributes/$encodedAttrName/values?page=$p&pageSize=$PageSize"
                    if ($Search) {
                        $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
                    }
                    Invoke-JIMApi -Endpoint $endpoint
                }

                Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMConnectedSystemObject' -PageSize $PageSize -Force:$Force `
                    -ItemNoun 'attribute values' -NarrowHint 'filter with -Search'
            }

            'List' {
                Write-Verbose "Getting Connected System Objects for Connected System $ConnectedSystemId (Page: $Page, PageSize: $PageSize)"
                $endpoint = Get-JIMConnectedSystemObjectListEndpoint -ConnectedSystemId $ConnectedSystemId -Page $Page -PageSize $PageSize `
                    -Search $Search -Status $Status -ObjectTypeId $ObjectTypeId -JoinType $JoinType -SortBy $SortBy -Ascending:$Ascending -HasUnresolvedReference:$HasUnresolvedReference

                $response = Invoke-JIMApi -Endpoint $endpoint
                foreach ($item in $response.items) {
                    $item
                }
            }

            'ListAll' {
                Write-Verbose "Getting all Connected System Objects for Connected System $ConnectedSystemId"
                $pageRequest = {
                    param($p)
                    $endpoint = Get-JIMConnectedSystemObjectListEndpoint -ConnectedSystemId $ConnectedSystemId -Page $p -PageSize $PageSize `
                        -Search $Search -Status $Status -ObjectTypeId $ObjectTypeId -JoinType $JoinType -SortBy $SortBy -Ascending:$Ascending -HasUnresolvedReference:$HasUnresolvedReference
                    Invoke-JIMApi -Endpoint $endpoint
                }

                Invoke-JIMPagedFetch -PageRequest $pageRequest -CmdletName 'Get-JIMConnectedSystemObject' -PageSize $PageSize -Force:$Force `
                    -ItemNoun 'objects' -NarrowHint 'narrow the query with -Search, -Status or -ObjectTypeId'
            }
        }
    }
}
