# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMPendingDeletion {
    <#
    .SYNOPSIS
        Gets metaverse objects pending deletion in JIM.

    .DESCRIPTION
        Gets the Metaverse Objects pending deletion: as a page of them, as a count, or as a summary of where their
        deletions stand. A Metaverse Object becomes pending deletion when its Object Type's deletion rule is met.

    .PARAMETER ObjectTypeId
        Returns only the Metaverse Objects of the Object Type with this ID.

    .PARAMETER Page
        The page of results to return, counting from 1.

    .PARAMETER PageSize
        How many objects each page holds.

    .PARAMETER Count
        Returns only the number of Metaverse Objects pending deletion.

    .PARAMETER Summary
        Returns how many are pending deletion, and where their deletions stand, across every Object Type.

    .OUTPUTS
        By default, one page of Metaverse Objects pending deletion. With -Count, how many there are. With -Summary,
        the counts by where each deletion stands.

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
    [OutputType('JIM.PendingDeletionDto', ParameterSetName = 'List')]
    [OutputType([int], ParameterSetName = 'Count')]
    [OutputType('JIM.PendingDeletionSummary', ParameterSetName = 'Summary')]
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
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
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
