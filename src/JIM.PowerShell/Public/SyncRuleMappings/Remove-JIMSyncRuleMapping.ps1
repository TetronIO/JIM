# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMSyncRuleMapping {
    <#
    .SYNOPSIS
        Removes a Synchronisation Rule Mapping (attribute flow rule) from JIM.

    .DESCRIPTION
        Deletes an attribute flow mapping from a Synchronisation Rule.

        When an import mapping still contributes Metaverse attribute values, deleting it recalls them by
        default: the values are withdrawn at the next Full Synchronisation of the contributing system, with
        surviving lower-priority contributors re-elected. Use -KeepContributedValues to leave the values in
        place instead; their provenance is severed before the mapping is deleted, so nothing can ever recall
        them. Export mappings contribute nothing to the Metaverse, so the choice does not apply to them.

        Before prompting for confirmation, the cmdlet quantifies the mapping's contributed values so the
        confirmation states the impact of the choice (-Force skips both the lookup and the prompt).

    .PARAMETER SyncRuleId
        The unique identifier of the Synchronisation Rule.

    .PARAMETER MappingId
        The unique identifier of the Mapping to delete.

    .PARAMETER InputObject
        Mapping object to delete (from pipeline).

    .PARAMETER KeepContributedValues
        Keeps the Metaverse attribute values the mapping contributed instead of leaving them to be recalled
        at the next Full Synchronisation of the contributing system. WARNING: the kept values remain in place
        with no provenance, so nothing can ever recall them; surviving lower-priority contributors are not
        re-elected. Omit this switch to let the recall happen (the default).

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5

        Removes the mapping with ID 5 from Synchronisation Rule 1 after confirmation. When the mapping still
        contributes Metaverse attribute values, the confirmation states how many attributes and Metaverse
        Objects the recall at the next Full Synchronisation will affect.

    .EXAMPLE
        Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Force

        Removes the mapping without confirmation. Any Metaverse attribute values it contributed are recalled
        at the next Full Synchronisation of the contributing system.

    .EXAMPLE
        Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -KeepContributedValues -Force

        Removes the mapping, KEEPING the Metaverse attribute values it contributed. The kept values lose
        their provenance: nothing records that this mapping contributed them, so no future recall (including
        the next Full Synchronisation) can ever withdraw them. Only choose this when the values should
        outlive the mapping.

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

        [switch]$KeepContributedValues,

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

        # Quantify the contributed values so the confirmation states the impact of the recall-or-keep
        # choice (#1537). A mapping's recall is deferred: it happens at the next Full Synchronisation of the
        # contributing system. -Force suppresses the confirmation, so the lookup would be wasted there.
        $confirmAction = 'Remove Synchronisation Rule Mapping'
        if (-not $Force) {
            $contributedSummary = $null
            try {
                $contributedSummary = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings/$mapId/contributed-values-summary"
            }
            catch {
                # An unavailable summary must not block the deletion; the server still applies the chosen
                # recall/keep behaviour regardless of what the confirmation could state.
                Write-Verbose "Could not retrieve the contributed-values summary for Mapping ${mapId}: $_"
            }

            $impactText = Get-JIMContributedValuesImpactText -Summary $contributedSummary -KeepContributedValues:$KeepContributedValues -DeferredRecall
            if ($impactText) {
                $confirmAction = "Remove Synchronisation Rule Mapping ($impactText)"
            }
        }

        if ($PSCmdlet.ShouldProcess($displayName, $confirmAction)) {
            Write-Verbose "Removing Synchronisation Rule Mapping: $mapId from Synchronisation Rule: $SyncRuleId"

            # The keep choice is supplied as a query parameter because HTTP DELETE bodies are awkward for
            # clients.
            $deleteEndpoint = "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings/$mapId"
            if ($KeepContributedValues) {
                $deleteEndpoint += '?keepContributedValues=true'
            }

            try {
                $null = Invoke-JIMApi -Endpoint $deleteEndpoint -Method 'DELETE'

                Write-Verbose "Removed Synchronisation Rule Mapping: $mapId"
            }
            catch {
                Write-Error "Failed to remove Synchronisation Rule Mapping: $_"
            }
        }
    }
}
