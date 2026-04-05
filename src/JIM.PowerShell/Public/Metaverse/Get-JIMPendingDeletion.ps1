function Get-JIMPendingDeletion {
    <#
    .SYNOPSIS
        Gets metaverse objects pending deletion in JIM.

    .DESCRIPTION
        Retrieves metaverse objects that are pending deletion, either as a paginated list,
        a count, or a summary of deletion states. Objects enter pending deletion when all
        their connector space objects are disconnected and their object type has an automatic
        deletion rule configured.

    .PARAMETER ObjectTypeId
        Filter results by Metaverse Object Type ID. Applicable to the List and Count parameter sets.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1. Only applicable to the List parameter set.

    .PARAMETER PageSize
        Number of items per page. Defaults to 25. Maximum is 100. Only applicable to the List parameter set.

    .PARAMETER Count
        Returns only the total count of pending deletions, optionally filtered by ObjectTypeId.

    .PARAMETER Summary
        Returns a summary breakdown of pending deletion states across all object types.

    .OUTPUTS
        PSCustomObject representing pending deletion item(s), a count, or a summary.

    .EXAMPLE
        Get-JIMPendingDeletion

        Gets the first page of metaverse objects pending deletion.

    .EXAMPLE
        Get-JIMPendingDeletion -ObjectTypeId 1

        Gets pending deletions filtered to object type ID 1.

    .EXAMPLE
        Get-JIMPendingDeletion -Count

        Gets the total count of pending deletions.

    .EXAMPLE
        Get-JIMPendingDeletion -Summary

        Gets a summary breakdown of pending deletion states.

    .LINK
        Get-JIMMetaverseObject

    .LINK
        Get-JIMMetaverseObjectType
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'Count')]
        [int]$ObjectTypeId,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 25,

        [Parameter(Mandatory, ParameterSetName = 'Count')]
        [switch]$Count,

        [Parameter(Mandatory, ParameterSetName = 'Summary')]
        [switch]$Summary
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Run Connect-JIM first."
            return
        }

        switch ($PSCmdlet.ParameterSetName) {
            'List' {
                Write-Verbose "Getting metaverse objects pending deletion (Page: $Page, PageSize: $PageSize)"
                $endpoint = "/api/v1/metaverse/pending-deletions?page=$Page&pageSize=$PageSize"
                if ($PSBoundParameters.ContainsKey('ObjectTypeId')) {
                    $endpoint += "&objectTypeId=$ObjectTypeId"
                }

                $response = Invoke-JIMApi -Endpoint $endpoint
                foreach ($item in $response.items) {
                    $item
                }
            }

            'Count' {
                Write-Verbose "Getting count of metaverse objects pending deletion"
                $endpoint = "/api/v1/metaverse/pending-deletions/count"
                if ($PSBoundParameters.ContainsKey('ObjectTypeId')) {
                    $endpoint += "?objectTypeId=$ObjectTypeId"
                }

                $result = Invoke-JIMApi -Endpoint $endpoint
                $result
            }

            'Summary' {
                Write-Verbose "Getting summary of metaverse pending deletion states"
                $result = Invoke-JIMApi -Endpoint "/api/v1/metaverse/pending-deletions/summary"
                $result
            }
        }
    }
}
