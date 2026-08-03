# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConfigurationChangePreview {
    <#
    .SYNOPSIS
        Reads a Configuration Change Preview.

    .DESCRIPTION
        Returns a preview's stage statuses, validation findings, impact counts and summary groups.
        Safe to call while the preview is still running: each stage's results appear as it completes,
        so there is something to read long before the whole preview finishes.

        Two fields decide how the rest should be read. HasFailed means the evaluation stopped part-way
        through the population, so its counts are real numbers over an arbitrary subset and are not an
        answer to the question that was asked. IsComplete means every stage that was going to run has
        finished; an empty summary is only "nothing would change" once it is true.

    .PARAMETER ActivityId
        The preview's Activity id, as returned by New-JIMConfigurationChangePreview.

    .OUTPUTS
        PSCustomObject with ActivityId, Surface, ActivityStatus, Message, ErrorMessage,
        ObjectsProcessed, ObjectsToProcess, the four stage statuses (ValidationStatus,
        ImpactCountsStatus, SummaryStatus, DeltasStatus), IsComplete, HasFailed, ValidationFindings,
        ImpactCounts, Groups, EstimatedAffectedObjects, EstimatedDeltaRows, DeltaPersistence,
        DispatchedToWorker and StalenessBaseline.

    .EXAMPLE
        Get-JIMConfigurationChangePreview -ActivityId 019fc824-f8c6-7588-8d9a-24a295e7621d

        Reads a preview by its Activity id.

    .EXAMPLE
        $preview = Get-JIMConfigurationChangePreview -ActivityId $activityId
        $preview.Groups | Format-Table TransitionType, MetaverseObjectTypeName, AttributeName, ObjectCount

        Shows the summary groups behind the counts.

    .LINK
        New-JIMConfigurationChangePreview
        Get-JIMConfigurationChangePreviewDelta
        Stop-JIMConfigurationChangePreview
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$ActivityId
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        try {
            Invoke-JIMApi -Endpoint "/api/v1/previews/$ActivityId"
        }
        catch {
            Write-Error "Failed to retrieve preview ${ActivityId}: $_"
        }
    }
}
