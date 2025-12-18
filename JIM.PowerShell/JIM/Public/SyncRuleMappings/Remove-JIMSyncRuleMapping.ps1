function Remove-JIMSyncRuleMapping {
    <#
    .SYNOPSIS
        Removes a Sync Rule Mapping (attribute flow rule) from JIM.

    .DESCRIPTION
        Deletes an attribute flow mapping from a Sync Rule.

    .PARAMETER SyncRuleId
        The unique identifier of the Sync Rule.

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

        Removes the mapping with ID 5 from Sync Rule 1 after confirmation.

    .EXAMPLE
        Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Force

        Removes the mapping without confirmation.

    .EXAMPLE
        Get-JIMSyncRuleMapping -SyncRuleId 1 | Remove-JIMSyncRuleMapping -SyncRuleId 1

        Removes all mappings from Sync Rule 1 (with confirmation for each).

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
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $mapId = if ($InputObject) { $InputObject.id } else { $MappingId }
        $displayName = "Mapping $mapId in Sync Rule $SyncRuleId"

        if ($Force -and -not $Confirm) {
            $ConfirmPreference = 'None'
        }

        if ($PSCmdlet.ShouldProcess($displayName, "Remove Sync Rule Mapping")) {
            Write-Verbose "Removing Sync Rule Mapping: $mapId from Sync Rule: $SyncRuleId"

            try {
                $null = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings/$mapId" -Method 'DELETE'

                Write-Verbose "Removed Sync Rule Mapping: $mapId"
            }
            catch {
                Write-Error "Failed to remove Sync Rule Mapping: $_"
            }
        }
    }
}
