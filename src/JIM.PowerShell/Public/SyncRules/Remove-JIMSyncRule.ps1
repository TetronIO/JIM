# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMSyncRule {
    <#
    .SYNOPSIS
        Removes a Synchronisation Rule from JIM.

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
        cmdlet returns; without it, anything the caller does next races the recall task.

    .PARAMETER Id
        The unique identifier of the Synchronisation Rule to delete.

    .PARAMETER InputObject
        Synchronisation Rule object to delete (from pipeline).

    .PARAMETER ChangeReason
        An optional reason for the deletion, recorded against the change history.

    .PARAMETER KeepContributedValues
        Keeps the Metaverse attribute values the rule contributed instead of recalling them. WARNING: the kept
        values remain in place with no provenance, so nothing can ever recall them; surviving lower-priority
        contributors are not re-elected. Omit this switch to recall the values (the default), which withdraws
        them via a queued Worker task before the rule is deleted.

    .PARAMETER Wait
        Waits for a queued contributed-values recall to finish before returning, so the rule really has
        gone when the cmdlet does. Without it the cmdlet returns as soon as the recall is queued, and a
        caller that immediately reads the rule back, or reorders the attribute's contributors, races the
        recall task. Has no effect when the deletion completes immediately.

    .PARAMETER Timeout
        Maximum seconds to wait when -Wait is supplied. Omit to wait indefinitely. A recall that has not
        finished by the timeout is reported as an error; it continues on the server regardless.

    .PARAMETER Force
        Suppresses confirmation prompts.

    .PARAMETER PassThru
        If specified, returns the deleted Synchronisation Rule object.

    .OUTPUTS
        When the deletion queues a contributed-values recall, a PSCustomObject tracking the queued work:
        - RecallActivityId: the recall Activity's id (a GUID); monitor it with Get-JIMActivity
        - AffectedValueCount: how many Metaverse attribute values the rule contributed at decision time
        - AffectedObjectCount: how many distinct Metaverse Objects held at least one of those values

        When the deletion completes immediately (keep chosen, or nothing contributed), nothing is returned.
        If -PassThru is specified, the Synchronisation Rule object as it stood before deletion is also
        returned.

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
        Set-JIMMetaverseAttributePriority -AttributeId 12 -ObjectTypeId 3 -MappingId @(7, 9)

        Removes a contributing Synchronisation Rule and waits for its recall to finish before reordering
        the attribute's surviving contributors. Without -Wait the reorder races the recall, and is
        refused while the deleted rule still counts as a contributor.

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
    [OutputType([PSCustomObject])]
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

        if ($Force -or $PSCmdlet.ShouldProcess($existing.name, $confirmAction)) {
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
