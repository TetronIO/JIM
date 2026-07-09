# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMSyncRuleMapping {
    <#
    .SYNOPSIS
        Removes a Synchronisation Rule Mapping (attribute flow rule) from JIM.

    .DESCRIPTION
        Deletes an attribute flow mapping from a Synchronisation Rule.

    .PARAMETER SyncRuleId
        The unique identifier of the Synchronisation Rule.

    .PARAMETER MappingId
        The unique identifier of the Mapping to delete.

    .PARAMETER InputObject
        Mapping object to delete (from pipeline).

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5

        Removes the mapping with ID 5 from Synchronisation Rule 1 after confirmation.

    .EXAMPLE
        Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Force

        Removes the mapping without confirmation.

    .EXAMPLE
        Get-JIMSyncRuleMapping -SyncRuleId 1 | Remove-JIMSyncRuleMapping -SyncRuleId 1

        Removes all mappings from Synchronisation Rule 1 (with confirmation for each).

    .LINK
        Get-JIMSyncRuleMapping
        New-JIMSyncRuleMapping
        Get-JIMSyncRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    param(
        [Parameter(Mandatory)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$MappingId,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [switch]$Force
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $mapId = if ($InputObject) { $InputObject.id } else { $MappingId }
        $displayName = "Mapping $mapId in Synchronisation Rule $SyncRuleId"

        if ($Force -and -not $Confirm) {
            $ConfirmPreference = 'None'
        }

        if ($PSCmdlet.ShouldProcess($displayName, "Remove Synchronisation Rule Mapping")) {
            Write-Verbose "Removing Synchronisation Rule Mapping: $mapId from Synchronisation Rule: $SyncRuleId"

            try {
                $null = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings/$mapId" -Method 'DELETE'

                Write-Verbose "Removed Synchronisation Rule Mapping: $mapId"
            }
            catch {
                Write-Error "Failed to remove Synchronisation Rule Mapping: $_"
            }
        }
    }
}
