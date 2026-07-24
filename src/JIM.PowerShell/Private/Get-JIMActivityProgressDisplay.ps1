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
    } elseif ($ElapsedSeconds -ge 0) {
        $statusText += " - Elapsed: ${ElapsedSeconds}s"
    }

    if ($message) {
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
