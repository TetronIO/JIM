# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMActivityProgressDisplay {
    <#
    .SYNOPSIS
        Builds Write-Progress parameters from an Activity progress snapshot.

    .DESCRIPTION
        Shared by Get-JIMActivity -Follow and Start-JIMRunProfile -Wait so both render live
        progress identically. Maps the /activities/{id}/progress response (status, object counts,
        percentage, throughput, ETA, phase message) onto a hashtable ready for splatting into
        Write-Progress.

    .PARAMETER Progress
        The progress snapshot returned by the /api/v1/activities/{id}/progress endpoint.

    .PARAMETER ActivityLabel
        The Write-Progress -Activity label to display.

    .PARAMETER ElapsedSeconds
        Optional elapsed seconds to display when object counts are unavailable. Pass -1 (default)
        to omit.

    .OUTPUTS
        Hashtable of Write-Progress parameters.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        $Progress,

        [Parameter(Mandatory)]
        [string]$ActivityLabel,

        [int]$ElapsedSeconds = -1
    )

    $status = "$($Progress.status ?? 'Running')"
    $objectsToProcess = [int]($Progress.objectsToProcess ?? 0)
    $objectsProcessed = [int]($Progress.objectsProcessed ?? 0)
    $message = "$($Progress.message ?? '')"

    # The step the run is on, where the server records them (#454). Shown as "Step 3 of 7: Saving
    # changes" so the counter restarting between steps reads as progress rather than lost work.
    # Composed by the shared helper so this sentence matches the portal and the other cmdlets (#1162).
    $stepText = ''
    $currentPhaseName = "$($Progress.currentPhase.name ?? '')"
    if ($currentPhaseName) {
        $stepText = Get-JIMStepPositionDisplay `
            -StepNumber $Progress.currentPhaseNumber `
            -TotalSteps ([int]($Progress.totalPhases ?? 0)) `
            -StepName $currentPhaseName
    }

    $statusText = $status
    $percent = -1  # Indeterminate

    if ($objectsToProcess -gt 0) {
        $percentValue = $Progress.percentComplete
        if ($null -ne $percentValue) {
            $percent = [Math]::Max(0, [Math]::Min(100, [int]$percentValue))
        } else {
            $percent = [Math]::Max(0, [Math]::Min(100, [int](($objectsProcessed / $objectsToProcess) * 100)))
        }
        $statusText += " - $objectsProcessed of $objectsToProcess objects"
    } elseif ($objectsProcessed -gt 0) {
        # A paged import never learns a total, so there is no percentage to show; what has arrived
        # so far is still worth reporting, and the progress message no longer carries it.
        $statusText += " - $objectsProcessed objects processed"
    } elseif ($ElapsedSeconds -ge 0) {
        $statusText += " - Elapsed: ${ElapsedSeconds}s"
    }

    if ($stepText) {
        $statusText += " - $stepText"
    }

    # The message says what is happening inside the step, so it only adds something when it is not
    # simply repeating the step's own name.
    if ($message -and $message -ne $currentPhaseName) {
        $statusText += " - $message"
    }

    $progressParams = @{
        Activity = $ActivityLabel
        Status = $statusText
        PercentComplete = $percent
    }

    # Server-calculated ETA; Write-Progress renders SecondsRemaining as "hh:mm:ss remaining".
    $secondsRemaining = $Progress.estimatedSecondsRemaining
    if ($null -ne $secondsRemaining -and [double]$secondsRemaining -ge 0) {
        $progressParams.SecondsRemaining = [int][double]$secondsRemaining
    }

    $objectsPerSecond = $Progress.objectsPerSecond
    if ($null -ne $objectsPerSecond -and [double]$objectsPerSecond -gt 0) {
        $progressParams.CurrentOperation = ('{0:N1} objects/second' -f [double]$objectsPerSecond)
    }

    return $progressParams
}
