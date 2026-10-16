# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMSyncRule {
    <#
    .SYNOPSIS
        Deletes a Synchronisation Rule.

    .DESCRIPTION
        Permanently deletes a Synchronisation Rule.

        When the rule still contributes Metaverse attribute values, deleting it withdraws them by default:
        the rule is disabled immediately and the recall runs as a queued Worker task (surviving lower-priority
        contributors are re-elected and resulting exports staged), with the rule deleted as the task's final
        step. In that case the cmdlet returns a tracking object carrying the recall Activity id; monitor
        progress with Get-JIMActivity.

        Use -KeepContributedValues to delete the rule immediately and leave the values in place instead. The
        kept values lose their provenance: nothing records that this rule contributed them, so no future
        recall can ever withdraw them.

        A rule contributing nothing deletes immediately either way. Before prompting for confirmation, the
        cmdlet quantifies the contributed values so the confirmation states the impact of the choice
        (-Force skips both the lookup and the prompt).

        Use -Wait to block until a queued recall has finished, so the rule really has gone by the time the
        cmdlet returns; without it, the rule can still be read back (disabled) until the recall lands. A rule
        whose recall is queued drops to the bottom of every attribute priority order it contributes to, so
        reordering the surviving contributors need not wait.

    .PARAMETER Id
        The ID of the Synchronisation Rule to delete.

    .PARAMETER InputObject
        A Synchronisation Rule from Get-JIMSyncRule, through the pipeline.

    .PARAMETER ChangeReason
        An optional reason for the deletion, recorded against the change history.

    .PARAMETER KeepContributedValues
        Keeps the Metaverse attribute values the rule contributed, instead of recalling them, and deletes the rule
        at once. The kept values have no provenance, so nothing can ever recall them, and the other contributors to
        those attributes are not re-elected.

    .PARAMETER Wait
        Waits for a queued recall to finish, so the rule has gone when the command returns. Without it, the command
        returns once the recall is queued, and the rule can still be read, disabled, until the recall finishes. Has
        no effect when the rule is deleted at once.

    .PARAMETER Timeout
        How many seconds -Wait waits before reporting an error; the recall carries on regardless. Without it, -Wait
        waits until the recall finishes.

    .PARAMETER Force
        Suppresses the confirmation prompt, and the count of contributed values it states.

    .PARAMETER PassThru
        Returns the rule as it stood before the deletion.

    .OUTPUTS
        When the deletion recalls the values the rule contributed, what is being recalled and the Activity doing it.
        Nothing when the rule is deleted at once. With -PassThru, the rule as it stood before the deletion as well.

        JIM.SyncRuleDeletionQueuedResponse: When the deletion recalls the values the rule contributed

        JIM.SyncRuleHeader: With -PassThru

    .EXAMPLE
        Remove-JIMSyncRule -Id 1

        Removes the Synchronisation Rule with ID 1 (prompts for confirmation). When the rule still contributes
        Metaverse attribute values, the confirmation states how many attributes and Metaverse Objects the
        recall will affect.

    .EXAMPLE
        Remove-JIMSyncRule -Id 1 -Force -ChangeReason "Decommissioned (CHG0123)"

        Removes the Synchronisation Rule without confirmation and records a reason against the change history.

    .EXAMPLE
        $recall = Remove-JIMSyncRule -Id 1 -Force
        Get-JIMActivity -Id $recall.RecallActivityId

        Removes a Synchronisation Rule that still contributes Metaverse attribute values, capturing the
        tracking object the queued recall returns, then retrieves the recall Activity to monitor its progress.
        The rule is deleted as the recall task's final step.

    .EXAMPLE
        Remove-JIMSyncRule -Id 1 -KeepContributedValues -Force

        Removes the Synchronisation Rule immediately, KEEPING the Metaverse attribute values it contributed.
        The kept values lose their provenance: nothing records that this rule contributed them, so no future
        recall can ever withdraw them. Only choose this when the values should outlive the rule.

    .EXAMPLE
        Remove-JIMSyncRule -Id 1 -Force -Wait

        Removes a contributing Synchronisation Rule and returns only once its recall has finished and the
        rule has been deleted.

    .EXAMPLE
        Remove-JIMSyncRule -Id 1 -Force
        Set-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 3 -MappingId @(7, 9)

        Removes a contributing Synchronisation Rule and reorders the attribute's surviving contributors
        straight away. The rule drops to the bottom of the order as soon as its recall is queued, so the
        new order may leave its mapping out.

    .EXAMPLE
        Get-JIMSyncRule | Where-Object { $_.name -like "Test*" } | Remove-JIMSyncRule -Force

        Force-deletes every Synchronisation Rule whose name starts with "Test". Review the matches first by
        running the Get-JIMSyncRule filter on its own, or run the pipeline without -Force to confirm each
        deletion individually.

    .LINK
        Get-JIMSyncRule
        New-JIMSyncRule
        Set-JIMSyncRule
        Get-JIMActivity
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.SyncRuleDeletionQueuedResponse')]
    [OutputType('JIM.SyncRuleHeader')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$KeepContributedValues,

        [switch]$Force,

        [switch]$Wait,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$Timeout,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $ruleId = if ($InputObject) { $InputObject.id } else { $Id }

        # Get the rule first for confirmation message and PassThru
        $existing = $null
        try {
            $existing = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$ruleId"
        }
        catch {
            Write-Error "Synchronisation Rule not found: $ruleId"
            return
        }

        # Quantify the contributed values so the confirmation states the impact of the recall-or-keep
        # choice (#1537). -Force suppresses the confirmation, so the lookup would be wasted there (the
        # documented bulk-pipeline path).
        $confirmAction = 'Delete Synchronisation Rule'
        if (-not $Force) {
            $contributedSummary = $null
            try {
                $contributedSummary = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$ruleId/contributed-values-summary"
            }
            catch {
                # An unavailable summary must not block the deletion; the server still applies the chosen
                # recall/keep behaviour regardless of what the confirmation could state.
                Write-Verbose "Could not retrieve the contributed-values summary for Synchronisation Rule ${ruleId}: $_"
            }

            $impactText = Get-JIMContributedValuesImpactText -Summary $contributedSummary -KeepContributedValues:$KeepContributedValues
            if ($impactText) {
                $confirmAction = "Delete Synchronisation Rule ($impactText)"
            }
        }

        if (Confirm-JIMAction -Cmdlet $PSCmdlet -Target $existing.name -Action $confirmAction -Force:$Force) {
            Write-Verbose "Deleting Synchronisation Rule: $ruleId"

            # The reason and the keep choice are supplied as query parameters because HTTP DELETE bodies are
            # awkward for clients.
            $deleteEndpoint = "/api/v1/synchronisation/sync-rules/$ruleId"
            $queryParts = @()
            if ($KeepContributedValues) {
                $queryParts += 'keepContributedValues=true'
            }
            if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                $queryParts += "changeReason=$([System.Uri]::EscapeDataString($ChangeReason))"
            }
            if ($queryParts.Count -gt 0) {
                $deleteEndpoint += '?' + ($queryParts -join '&')
            }

            try {
                $result = Invoke-JIMApi -Endpoint $deleteEndpoint -Method 'DELETE'

                # Derived flows the deletion left with a missing input (#1750, FR 3), reported by both the
                # immediate and the queued deletion: reported, never blocking.
                Write-JIMDependentDerivedFlowWarning -DependentDerivedFlows $result.DependentDerivedFlows

                if ($result -and $result.RecallActivityId) {
                    # 202 Accepted: a contributed-values recall was queued. The rule is disabled now and
                    # deleted as the task's final step; surface the tracking object so scripts can monitor
                    # the recall Activity.
                    Write-Verbose "Queued a contributed-values recall for Synchronisation Rule ${ruleId}; Activity: $($result.RecallActivityId)"
                    if ($Wait) {
                        $waitParams = @{
                            ActivityId    = "$($result.RecallActivityId)"
                            ActivityLabel = "Recalling values contributed by '$($existing.name)'"
                        }
                        if ($PSBoundParameters.ContainsKey('Timeout')) { $waitParams.Timeout = $Timeout }
                        $recallStatus = Wait-JIMActivityCompletion @waitParams

                        # The rule is deleted as the recall's final step, so anything short of a clean
                        # completion leaves it in place: say so rather than let the caller assume it has gone.
                        if (-not $recallStatus) {
                            Write-Error ("The contributed-values recall for '$($existing.name)' had not finished after ${Timeout}s. " +
                                "The Synchronisation Rule is deleted as the recall's final step, so it may still exist. " +
                                "Activity: $($result.RecallActivityId).")
                        }
                        elseif ($recallStatus -notin @('Complete', 'CompleteWithWarning')) {
                            Write-Error ("The contributed-values recall for '$($existing.name)' ended with status '$recallStatus'. " +
                                "The Synchronisation Rule is deleted as the recall's final step, so it may still exist. " +
                                "Activity: $($result.RecallActivityId).")
                        }
                        else {
                            # Confirm the deletion rather than infer it from the Activity's status.
                            $survivor = $null
                            try {
                                $survivor = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$ruleId"
                            }
                            catch {
                                Write-Verbose "Synchronisation Rule $ruleId is gone, as expected."
                            }
                            if ($survivor) {
                                Write-Error ("The contributed-values recall for '$($existing.name)' completed, but the " +
                                    "Synchronisation Rule is still present (id $ruleId). Activity: $($result.RecallActivityId).")
                            }
                        }
                    }
                    $result
                }
                else {
                    Write-Verbose "Deleted Synchronisation Rule: $ruleId"
                }

                if ($PassThru) {
                    $existing
                }
            }
            catch {
                Write-Error "Failed to delete Synchronisation Rule: $_"
            }
        }
    }
}
