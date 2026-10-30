# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMSyncRuleMapping {
    <#
    .SYNOPSIS
        Deletes an Attribute Flow from a Synchronisation Rule.

    .DESCRIPTION
        Deletes an Attribute Flow (mapping) from a Synchronisation Rule.

        When an import mapping still contributes Metaverse attribute values, deleting it recalls them by
        default: the values are withdrawn at the next synchronisation of the contributing system, Delta or Full, with
        surviving lower-priority contributors re-elected. Use -KeepContributedValues to leave the values in
        place instead; their provenance is severed before the mapping is deleted, so nothing can ever recall
        them. Export mappings contribute nothing to the Metaverse, so the choice does not apply to them.

        Before prompting for confirmation, the cmdlet quantifies the mapping's contributed values so the
        confirmation states the impact of the choice (-Force skips both the lookup and the prompt).

        When the mapping was the last enabled contributor of a Metaverse attribute that an Attribute Flow
        deriving another Metaverse attribute reads (directly, or through other derived attributes), the
        deletion still goes ahead, and the cmdlet writes a warning naming each such flow: a summary line,
        then one line per flow. Each flow's Missing Input Behaviour then decides what it contributes. This
        applies only while the Metaverse-Derived Attribute Flows feature is enabled.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule the mapping belongs to.

    .PARAMETER MappingId
        The ID of the mapping to delete.

    .PARAMETER InputObject
        A mapping from Get-JIMSyncRuleMapping, through the pipeline.

    .PARAMETER KeepContributedValues
        Keeps the Metaverse attribute values the mapping contributed, instead of leaving them to be recalled at the
        next synchronisation of the contributing Connected System, Delta or Full. The kept values have no provenance, so
        nothing can ever recall them, and the other contributors to those attributes are not re-elected.

    .PARAMETER Force
        Suppresses the confirmation prompt, and the count of contributed values it states.

    .OUTPUTS
        None. Derived Attribute Flows the deletion left with a missing input are written to the warning stream. When the
        mapping was on an import rule, a warning saying that its Connected System's next synchronisation, Delta or Full,
        processes every object of the rule's Connected System Object Type, with how many objects that is and about how
        long it takes, is written to the warning stream.

    .EXAMPLE
        Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5

        Removes the mapping with ID 5 from Synchronisation Rule 1 after confirmation. When the mapping still
        contributes Metaverse attribute values, the confirmation states how many attributes and Metaverse
        Objects the recall at the next synchronisation will affect.

    .EXAMPLE
        Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Force

        Removes the mapping without confirmation. Any Metaverse attribute values it contributed are recalled
        at the next synchronisation of the contributing system, Delta or Full.

    .EXAMPLE
        Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -KeepContributedValues -Force

        Removes the mapping, KEEPING the Metaverse attribute values it contributed. The kept values lose
        their provenance: nothing records that this mapping contributed them, so no future recall (including
        the next synchronisation) can ever withdraw them. Only choose this when the values should
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
    [OutputType([void])]
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
        # choice (#1537). A mapping's recall is deferred: it happens at the next synchronisation of the
        # contributing system, Delta or Full. -Force suppresses the confirmation, so the lookup would be wasted there.
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
                $result = Invoke-JIMApi -Endpoint $deleteEndpoint -Method 'DELETE'

                Write-Verbose "Removed Synchronisation Rule Mapping: $mapId"

                # Derived flows the deletion left with a missing input (#1750, FR 3): reported, never blocking.
                Write-JIMDependentDerivedFlowWarning -DependentDerivedFlows $result.DependentDerivedFlows

                # The Object Types the save made the next synchronisation process in full (#2615): advised, never blocking.
                Write-JIMAwaitingSynchronisationWarning -AwaitingSynchronisation $result.AwaitingSynchronisation
            }
            catch {
                Write-Error "Failed to remove Synchronisation Rule Mapping: $_"
            }
        }
    }
}
