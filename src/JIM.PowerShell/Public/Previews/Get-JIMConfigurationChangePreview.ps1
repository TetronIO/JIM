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
        The preview, with each stage's results as far as it has got; it is safe to read while the preview runs.
        IsComplete means every stage that was going to run has finished: an empty summary means nothing would change
        only once it is $true.

    .EXAMPLE
        Get-JIMConfigurationChangePreview -ActivityId 019fc824-f8c6-7588-8d9a-24a295e7621d

        Reads a preview by its Activity id.

    .EXAMPLE
        $preview = Get-JIMConfigurationChangePreview -ActivityId $activityId
        $preview.Groups | Format-Table TransitionType, MetaverseObjectTypeName, AttributeName, PatternKey, ObjectCount

        Shows the summary groups behind the counts. PatternKey names what kind of change a group describes
        (EmailDomainChanged, ContainerChanged, CasingChanged, PrefixAdded, PrefixRemoved, SuffixAdded,
        SuffixRemoved), and is empty where no pattern was recognised or where the group's objects did not all
        share one.

    .LINK
        New-JIMConfigurationChangePreview
        Get-JIMConfigurationChangePreviewDelta
        Stop-JIMConfigurationChangePreview
    #>
    [CmdletBinding()]
    [OutputType('JIM.ConfigurationChangePreviewResponse')]
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
