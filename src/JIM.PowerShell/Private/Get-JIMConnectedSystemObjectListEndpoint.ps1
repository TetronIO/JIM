# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemObjectListEndpoint {
    <#
    .SYNOPSIS
        Builds the API endpoint for a paginated Connected System Object list request.

    .DESCRIPTION
        Internal helper shared by Get-JIMConnectedSystemObject's List and ListAll
        parameter sets, so the query string is built identically for both.

    .OUTPUTS
        The relative API endpoint, including query string.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory)]
        [int]$Page,

        [Parameter(Mandatory)]
        [int]$PageSize,

        [string]$Search,
        [string]$Status,
        [int]$ObjectTypeId,
        [string]$JoinType,
        [string]$SortBy,
        [switch]$Ascending
    )

    $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space?page=$Page&pageSize=$PageSize"

    if ($Search) {
        $endpoint += "&search=$([System.Uri]::EscapeDataString($Search))"
    }

    if ($Status) {
        $endpoint += "&status=$Status"
    }

    if ($ObjectTypeId -gt 0) {
        $endpoint += "&objectTypeId=$ObjectTypeId"
    }

    if ($JoinType) {
        $endpoint += "&joinType=$JoinType"
    }

    if ($SortBy) {
        $endpoint += "&sortBy=$([System.Uri]::EscapeDataString($SortBy))"
    }

    if ($Ascending) {
        $endpoint += "&sortDescending=false"
    }

    $endpoint
}
