# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMConfigurationChangePreview {
    <#
    .SYNOPSIS
        Finds out what a proposed configuration change would do, without making it.

    .DESCRIPTION
        Starts a Configuration Change Preview: JIM evaluates a proposed change against the objects
        already in the metaverse and reports what would happen to them, changing nothing.

        The field semantics match Set-JIMMetaverseObjectType exactly, so an omitted parameter previews
        the stored value. Pass the same parameters to this cmdlet and then to Set-JIMMetaverseObjectType
        and the preview describes precisely what the change will do.

        Evaluation is asynchronous. Without -Wait this returns as soon as the proposal itself has been
        validated, carrying the ActivityId to poll with Get-JIMConfigurationChangePreview. With -Wait it
        polls until the preview reaches a terminal state and returns the finished preview.

        A proposal that cannot be applied comes back with a blocking validation finding and is never
        evaluated: check IsBlocked and ValidationFindings before reading anything else.

        Pass the returned ActivityId to Set-JIMMetaverseObjectType -PreviewActivityId when you make the
        change, so the audit records which preview informed it.

    .PARAMETER MetaverseObjectTypeId
        The Metaverse Object Type whose deletion settings are being proposed. Selects the deletion
        settings surface; other surfaces gain their own parameters as their adapters ship.

    .PARAMETER DeletionRule
        The proposed deletion rule. Omitted previews the stored rule.
        - Manual: objects are never automatically deleted
        - WhenLastConnectorDisconnected: objects are deleted when all connectors are removed
        - WhenAuthoritativeSourceDisconnected: objects are deleted when an authoritative source disconnects

    .PARAMETER DeletionGracePeriod
        The proposed grace period, as a TimeSpan. Omitted previews the stored grace period;
        [TimeSpan]::Zero previews no grace period, matching how Set-JIMMetaverseObjectType stores it.

    .PARAMETER DeletionTriggerConnectedSystemIds
        The proposed authoritative sources. Omitted previews the stored list.

        Worth knowing before reading the result: this list is consulted at the moment a Connected System
        Object disconnects, not by the housekeeping pass that acts on objects already marked, so changing
        it alone moves no object's deletion date and the preview will honestly report no impact from it.
        What it can do is make the proposal invalid, which comes back as a blocking finding.

    .PARAMETER DeletionTriggerMode
        The proposed trigger mode. Omitted previews the stored mode. Read at the same moment as the
        trigger list, and so with the same standing impact: none.

    .PARAMETER FullDataSet
        Keep every object-level detail row rather than the per-group cap's worth. Summary counts are
        exact either way; this decides only how much of the detail behind them can be read back with
        Get-JIMConfigurationChangePreviewDelta. Use it when you intend to export the full list.

    .PARAMETER Wait
        Poll until the preview finishes, and return the finished preview rather than the start result.

    .PARAMETER TimeoutSeconds
        How long -Wait polls before giving up and writing an error. The preview itself keeps running;
        read it later with Get-JIMConfigurationChangePreview. Defaults to 300 seconds.

    .OUTPUTS
        Without -Wait: PSCustomObject with ActivityId, ValidationFindings, IsBlocked, Failed,
        EstimatedAffectedObjects and EstimatedDeltaRows.
        With -Wait: the preview, as returned by Get-JIMConfigurationChangePreview.

    .EXAMPLE
        New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionRule WhenLastConnectorDisconnected -Wait

        Reports what switching the User type to automatic deletion would do to the Metaverse Objects
        whose connectors have already gone, and waits for the answer.

    .EXAMPLE
        $preview = New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionGracePeriod ([TimeSpan]::FromDays(7)) -Wait
        $preview.ImpactCounts

        Shortens the grace period to seven days and reads how many objects each transition affects.

    .EXAMPLE
        $preview = New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionRule WhenLastConnectorDisconnected -Wait
        if ($preview.ImpactCounts.Count -eq 0) {
            Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenLastConnectorDisconnected -PreviewActivityId $preview.ActivityId
        }

        Applies the change only when the preview found nothing would happen to existing objects, and
        records the preview against the change.

    .LINK
        Get-JIMConfigurationChangePreview
        Get-JIMConfigurationChangePreviewDelta
        Stop-JIMConfigurationChangePreview
        Set-JIMMetaverseObjectType
    #>
    [CmdletBinding(DefaultParameterSetName = 'MetaverseObjectTypeDeletionSettings')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'MetaverseObjectTypeDeletionSettings', ValueFromPipelineByPropertyName)]
        [int]$MetaverseObjectTypeId,

        [Parameter(ParameterSetName = 'MetaverseObjectTypeDeletionSettings')]
        [ValidateSet('Manual', 'WhenLastConnectorDisconnected', 'WhenAuthoritativeSourceDisconnected')]
        [string]$DeletionRule,

        [Parameter(ParameterSetName = 'MetaverseObjectTypeDeletionSettings')]
        [TimeSpan]$DeletionGracePeriod,

        [Parameter(ParameterSetName = 'MetaverseObjectTypeDeletionSettings')]
        [int[]]$DeletionTriggerConnectedSystemIds,

        [Parameter(ParameterSetName = 'MetaverseObjectTypeDeletionSettings')]
        [ValidateSet('AllSourcesDisconnect', 'SpecificSourcesDisconnect')]
        [string]$DeletionTriggerMode,

        [Parameter()]
        [switch]$FullDataSet,

        [Parameter()]
        [switch]$Wait,

        [Parameter()]
        [ValidateRange(1, 86400)]
        [int]$TimeoutSeconds = 300
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{}

        if ($DeletionRule) {
            # Enum sent as its string name; the API rejects numeric ordinals
            # (JsonStringEnumConverter allowIntegerValues:false).
            $body.deletionRule = $DeletionRule
        }

        if ($PSBoundParameters.ContainsKey('DeletionGracePeriod')) {
            $body.deletionGracePeriod = $DeletionGracePeriod.ToString()
        }

        if ($PSBoundParameters.ContainsKey('DeletionTriggerConnectedSystemIds')) {
            $body.deletionTriggerConnectedSystemIds = $DeletionTriggerConnectedSystemIds
        }

        if ($PSBoundParameters.ContainsKey('DeletionTriggerMode')) {
            $body.deletionTriggerMode = $DeletionTriggerMode
        }

        if ($FullDataSet) {
            $body.deltaPersistence = 'Full'
        }

        try {
            $start = Invoke-JIMApi -Endpoint "/api/v1/metaverse/object-types/$MetaverseObjectTypeId/deletion-settings/preview" -Method 'POST' -Body $body
        }
        catch {
            Write-Error "Failed to start a preview for Metaverse Object Type ${MetaverseObjectTypeId}: $_"
            return
        }

        if (-not $start) {
            return
        }

        foreach ($finding in $start.ValidationFindings) {
            # Surfaced as they are produced rather than left for the caller to find in the object: a
            # blocking finding means nothing was evaluated, and a script that read straight past it
            # would treat "this change is impossible" as "this change affects nothing".
            $message = "Preview validation ($($finding.Severity)): $($finding.Message)"
            if ($finding.Severity -eq 'Blocking') { Write-Warning $message } else { Write-Verbose $message }
        }

        if (-not $Wait -or $start.IsBlocked -or $start.Failed) {
            return $start
        }

        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ($true) {
            $preview = Get-JIMConfigurationChangePreview -ActivityId $start.ActivityId
            if ($preview.IsComplete -or $preview.HasFailed -or $preview.ActivityStatus -eq 'Cancelled') {
                return $preview
            }

            if ((Get-Date) -ge $deadline) {
                Write-Error "The preview did not finish within $TimeoutSeconds seconds. It is still running; read it with Get-JIMConfigurationChangePreview -ActivityId $($start.ActivityId), or abandon it with Stop-JIMConfigurationChangePreview."
                return $preview
            }

            Start-Sleep -Seconds 2
        }
    }
}
