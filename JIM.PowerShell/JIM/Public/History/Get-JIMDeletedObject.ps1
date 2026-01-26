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
        The type of deleted objects to retrieve: 'MVO' (default) or 'CSO'.

    .PARAMETER ConnectedSystemId
        Filter deleted CSOs by Connected System ID. Only valid when ObjectType is 'CSO'.

    .PARAMETER MetaverseObjectTypeId
        Filter deleted MVOs by Metaverse Object Type ID. Only valid when ObjectType is 'MVO'.

    .PARAMETER Search
        Search term for filtering results. For CSOs, searches by External ID.
        For MVOs, searches by Display Name.

    .PARAMETER FromDate
        Filter for deletions on or after this date (UTC).

    .PARAMETER ToDate
        Filter for deletions on or before this date (UTC).

    .PARAMETER Page
        Page number for paginated results (default: 1).

    .PARAMETER PageSize
        Number of items per page (default: 50, max: 1000).

    .OUTPUTS
        PSCustomObject containing:
        - items: Array of deleted object records
        - totalCount: Total number of matching records
        - page: Current page number
        - pageSize: Items per page

        Each deleted CSO item contains: id, externalId, displayName, objectTypeName,
        connectedSystemId, connectedSystemName, changeTime, initiatedByType, initiatedByName.

        Each deleted MVO item contains: id, displayName, objectTypeName, objectTypeId,
        changeTime, initiatedByType, initiatedByName.

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
    [OutputType([PSCustomObject])]
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
