# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Removes a Connected System from JIM.

    .DESCRIPTION
        Deletes a Connected System and all its related data from JIM.

        By default the deletion runs as "Deprovision through synchronisation" (recommended): the system is
        fenced and a background run processes every Connected System Object through the synchronisation
        engine's obsoletion semantics; attribute recall with surviving-contributor re-election, Metaverse
        Object Deletion Rule evaluation, and Pending Export staging all happen exactly as a normal
        synchronisation disconnect would, before the system itself is deleted. The cmdlet then returns a
        tracking object carrying the Activity id; monitor progress with Get-JIMActivity.

        Use -DeleteImmediately for "Delete immediately and keep contributed data" (today's fast path):
        - Small systems (< 1000 objects): deleted immediately
        - Large systems: queued as a background job (a tracking object is returned)
        - Systems with a running sync: queued to run after the sync completes
        WARNING: the attribute values this system contributed are kept but lose their provenance, so
        nothing can ever recall them; surviving contributors are not re-elected and downstream systems are
        not corrected. Deletion Rules are still evaluated in bulk for orphaned Metaverse Objects, but no
        per-object synchronisation processing occurs.

        If a deprovisioning run fails partway, the system stays fenced (its Status remains Deleting) and
        never returns to service. Re-running this cmdlet retries the run: it resumes from its checkpoint
        rather than starting again. Running it with -DeleteImmediately instead finishes the deletion
        immediately, abandoning the remaining deprovisioning work (the Activity records the abandonment).

        Unless -Force is used, the cmdlet retrieves the deletion preview first so the confirmation states
        the impact of the chosen mode. Use Get-JIMConnectedSystem -Id <id> -DeletionPreview to review the
        impact yourself before deleting.

    .PARAMETER Id
        The unique identifier of the Connected System to delete.

    .PARAMETER InputObject
        A Connected System object to delete. Accepts pipeline input.

    .PARAMETER DeleteImmediately
        Deletes the Connected System immediately instead of deprovisioning through synchronisation.
        WARNING: contributed attribute values are KEPT with no provenance, so nothing can ever recall
        them; surviving contributors are not re-elected and downstream systems are not corrected. On a
        system whose deprovisioning run failed partway, this finishes the deletion immediately,
        abandoning the remaining deprovisioning work.

    .PARAMETER PassThru
        If specified, returns the deletion result object for deletions that complete immediately.
        (Queued deletions always return a tracking object, with or without -PassThru.)

    .PARAMETER Force
        Suppresses confirmation prompts (and skips the deletion-preview lookup the confirmation uses).

    .PARAMETER ChangeReason
        Optional reason for the deletion, recorded on the audit Activity and the configuration change history tombstone.

    .OUTPUTS
        When the deletion queues (always the case for the default deprovisioning mode), a PSCustomObject
        tracking the queued work:
        - ActivityId: the deletion Activity's id (a GUID); monitor it with Get-JIMActivity
        - WorkerTaskId: the queued Worker Task's id (a GUID)
        - Outcome: QueuedAsBackgroundJob, or QueuedAfterSync when a running synchronisation delays it
        - ConnectedSystemObjectCount, ContributedValueCount, ContributedValueObjectCount: headline counts
          from the deletion preview, or null when it was not retrieved (-Force skips the lookup)

        When an immediate deletion completes synchronously, nothing is returned unless -PassThru is
        specified, in which case the deletion result (Outcome, ActivityId) is returned.

    .EXAMPLE
        Remove-JIMConnectedSystem -Id 1

        Deprovisions the Connected System with ID 1 through synchronisation (prompts for confirmation,
        stating the impact from the deletion preview) and returns a tracking object for the queued run.

    .EXAMPLE
        $tracking = Remove-JIMConnectedSystem -Id 1 -Force
        Get-JIMActivity -Id $tracking.ActivityId

        Deprovisions the Connected System without confirmation, capturing the tracking object, then
        retrieves the deletion Activity to monitor the run's progress.

    .EXAMPLE
        Remove-JIMConnectedSystem -Id 1 -DeleteImmediately -Force

        Deletes the Connected System immediately, KEEPING the attribute values it contributed. The kept
        values lose their provenance, so nothing can ever recall them, and downstream systems are not
        corrected. Only choose this when the data should outlive the system, or for disposable test data.

    .EXAMPLE
        Remove-JIMConnectedSystem -Id 1 -Force

        Run again after a failed deprovisioning run, this RETRIES the run: the system is still fenced and
        the run resumes from its checkpoint. Use -DeleteImmediately instead to finish the deletion
        immediately, abandoning the remaining deprovisioning work.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "Test*" | Remove-JIMConnectedSystem -DeleteImmediately -Force

        Force-deletes every Connected System whose name starts with "Test", immediately and keeping
        contributed data; the usual choice for disposable test systems. Review the matches first by
        running the Get-JIMConnectedSystem filter on its own, or run the pipeline without -Force to
        confirm each deletion individually.

    .EXAMPLE
        Remove-JIMConnectedSystem -Id 1 -Force -ChangeReason "Decommissioned (CHG0123)"

        Deprovisions the Connected System, recording the reason on the deletion's change history tombstone.

    .LINK
        Get-JIMConnectedSystem
        New-JIMConnectedSystem
        Get-JIMActivity
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [switch]$DeleteImmediately,

        [switch]$PassThru,

        [switch]$Force,

        [string]$ChangeReason
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Get the ID from InputObject if provided
        $systemId = if ($PSCmdlet.ParameterSetName -eq 'ByInputObject') {
            $InputObject.id
        } else {
            $Id
        }

        # Get system name for confirmation message
        $systemName = if ($PSCmdlet.ParameterSetName -eq 'ByInputObject') {
            $InputObject.name
        } else {
            try {
                $system = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId"
                $system.name
            } catch {
                "ID $systemId"
            }
        }

        # Retrieve the deletion preview so the confirmation states the impact of the chosen mode
        # (the #1537 precedent). -Force suppresses the confirmation, so the lookup would be wasted there
        # (the documented bulk-pipeline path); the tracking object's counts are then null.
        $preview = $null
        $confirmAction = if ($DeleteImmediately) {
            'Delete Connected System immediately, keeping contributed data'
        } else {
            'Deprovision Connected System through synchronisation'
        }
        if (-not $Force) {
            try {
                $preview = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId/deletion-preview"
            }
            catch {
                # An unavailable preview must not block the deletion; the server still applies the chosen
                # mode regardless of what the confirmation could state.
                Write-Verbose "Could not retrieve the deletion preview for Connected System ${systemId}: $_"
            }

            $impactText = Get-JIMConnectedSystemDeletionImpactText -Preview $preview -DeleteImmediately:$DeleteImmediately
            if ($impactText) {
                $confirmAction = "$confirmAction ($impactText)"
            }
        }

        if ($Force -or $PSCmdlet.ShouldProcess($systemName, $confirmAction)) {
            Write-Verbose "Deleting Connected System: $systemName (ID: $systemId)"

            try {
                # The mode and the reason are supplied as query parameters because HTTP DELETE bodies are
                # awkward for clients. The deprovisioning default is the server's own, so it is only the
                # immediate mode that needs saying.
                $deleteEndpoint = "/api/v1/synchronisation/connected-systems/$systemId"
                $queryParts = @()
                if ($DeleteImmediately) {
                    $queryParts += 'synchronisedDeprovisioning=false'
                }
                if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                    $queryParts += "changeReason=$([System.Uri]::EscapeDataString($ChangeReason))"
                }
                if ($queryParts.Count -gt 0) {
                    $deleteEndpoint += '?' + ($queryParts -join '&')
                }

                $result = Invoke-JIMApi -Endpoint $deleteEndpoint -Method 'DELETE'

                Write-Verbose "Deletion result: $($result.outcome)"

                if ($result -and $result.WorkerTaskId) {
                    # 202 Accepted: the deletion queued (always the case for deprovisioning). Surface the
                    # tracking object so scripts can monitor the Activity; headline counts come from the
                    # preview when it was retrieved.
                    [PSCustomObject]@{
                        ActivityId                  = $result.ActivityId
                        WorkerTaskId                = $result.WorkerTaskId
                        Outcome                     = $result.Outcome
                        ConnectedSystemObjectCount  = if ($preview) { $preview.ConnectedSystemObjectCount } else { $null }
                        ContributedValueCount       = if ($preview) { $preview.ContributedValueCount } else { $null }
                        ContributedValueObjectCount = if ($preview) { $preview.ContributedValueObjectCount } else { $null }
                    }
                }
                elseif ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to delete Connected System '$systemName': $_"
            }
        }
    }
}
