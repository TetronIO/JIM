# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMDeletedObject {
    <#
    .SYNOPSIS
        Gets deleted objects (CSOs or MVOs) from the deleted objects view.

    .DESCRIPTION
        Retrieves deleted Connected System Objects (CSOs) or Metaverse Objects (MVOs)
        from the JIM deleted objects audit trail. When objects are deleted, their identity
        and change history are preserved for audit and compliance purposes.

        By default, retrieves deleted MVOs. Use -ObjectType CSO to retrieve deleted CSOs.

    .PARAMETER ObjectType
        Which deleted objects to return: Metaverse Objects or Connected System Objects.

    .PARAMETER ConnectedSystemId
        Only Connected System Objects deleted from this Connected System. Applies when returning Connected System
        Objects.

    .PARAMETER MetaverseObjectTypeId
        Only Metaverse Objects of this type. Applies when returning Metaverse Objects.

    .PARAMETER Search
        Only objects matching this text: by External ID for Connected System Objects, by display name for Metaverse
        Objects.

    .PARAMETER FromDate
        Only objects deleted on or after this date (UTC).

    .PARAMETER ToDate
        Only objects deleted on or before this date (UTC).

    .PARAMETER Page
        The page of results to return.

    .PARAMETER PageSize
        How many objects to return per page.

    .OUTPUTS
        One page of results, whose Items are the deleted objects. A deleted Metaverse Object carries Id,
        DisplayName, ObjectTypeName, ObjectTypeId, ChangeTime, InitiatedByType and InitiatedByName; a deleted
        Connected System Object carries Id, ExternalId, DisplayName, ObjectTypeName, ConnectedSystemId,
        ConnectedSystemName, ChangeTime, InitiatedByType and InitiatedByName.

        JIM.DeletedObjectsPagedResponseOfDeletedMvoResponse: By default, or with `-ObjectType MVO`

        JIM.DeletedObjectsPagedResponseOfDeletedCsoResponse: With `-ObjectType CSO`

    .EXAMPLE
        Get-JIMDeletedObject

        Gets all deleted MVOs (default).

    .EXAMPLE
        Get-JIMDeletedObject -ObjectType MVO -Search "John"

        Gets deleted MVOs with display name containing "John".

    .EXAMPLE
        Get-JIMDeletedObject -ObjectType CSO -ConnectedSystemId 1

        Gets deleted CSOs from Connected System ID 1.

    .EXAMPLE
        Get-JIMDeletedObject -ObjectType CSO -Search "EMP001"

        Gets deleted CSOs with external ID containing "EMP001".

    .EXAMPLE
        Get-JIMDeletedObject -ObjectType MVO -MetaverseObjectTypeId 1 -PageSize 100

        Gets deleted MVOs of a specific object type, 100 per page.

    .LINK
        Get-JIMMetaverseObject
        Get-JIMConnectedSystem
        Invoke-JIMHistoryCleanup
    #>
    [CmdletBinding()]
    [OutputType('JIM.DeletedObjectsPagedResponseOfDeletedMvoResponse', 'JIM.DeletedObjectsPagedResponseOfDeletedCsoResponse')]
    param(
        [Parameter()]
        [ValidateSet('MVO', 'CSO')]
        [string]$ObjectType = 'MVO',

        [Parameter()]
        [int]$ConnectedSystemId,

        [Parameter()]
        [int]$MetaverseObjectTypeId,

        [Parameter()]
        [string]$Search,

        [Parameter()]
        [DateTime]$FromDate,

        [Parameter()]
        [DateTime]$ToDate,

        [Parameter()]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter()]
        [ValidateRange(1, 1000)]
        [int]$PageSize = 50
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Build query string parameters
        $queryParams = @()
        $queryParams += "page=$Page"
        $queryParams += "pageSize=$PageSize"

        if ($ObjectType -eq 'CSO') {
            $endpoint = "/api/v1/history/deleted-objects/cso"

            if ($PSBoundParameters.ContainsKey('ConnectedSystemId')) {
                $queryParams += "connectedSystemId=$ConnectedSystemId"
            }
            if ($PSBoundParameters.ContainsKey('Search')) {
                $queryParams += "externalIdSearch=$([System.Uri]::EscapeDataString($Search))"
            }
        }
        else {
            $endpoint = "/api/v1/history/deleted-objects/mvo"

            if ($PSBoundParameters.ContainsKey('MetaverseObjectTypeId')) {
                $queryParams += "objectTypeId=$MetaverseObjectTypeId"
            }
            if ($PSBoundParameters.ContainsKey('Search')) {
                $queryParams += "displayNameSearch=$([System.Uri]::EscapeDataString($Search))"
            }
        }

        if ($PSBoundParameters.ContainsKey('FromDate')) {
            $queryParams += "fromDate=$($FromDate.ToUniversalTime().ToString('o'))"
        }
        if ($PSBoundParameters.ContainsKey('ToDate')) {
            $queryParams += "toDate=$($ToDate.ToUniversalTime().ToString('o'))"
        }

        $queryString = $queryParams -join '&'
        $fullEndpoint = "${endpoint}?${queryString}"

        Write-Verbose "Getting deleted ${ObjectType}s from: $fullEndpoint"

        try {
            $result = Invoke-JIMApi -Endpoint $fullEndpoint
            $result
        }
        catch {
            Write-Error "Failed to get deleted ${ObjectType}s: $_"
            throw
        }
    }
}
