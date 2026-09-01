# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Wait-JIMActivityCompletion {
    <#
    .SYNOPSIS
        Blocks until a JIM Activity reaches a terminal status, rendering live progress.

    .DESCRIPTION
        Polls /api/v1/activities/{id}/progress (the lightweight endpoint: status, counts, phase
        message, throughput and ETA, without the cost of the full detail read) until the Activity
        reaches one of the terminal statuses, then returns that status so the caller can decide what
        a failure means in its own terms.

        A transient polling failure is not fatal: it is written to the verbose stream and the wait
        continues, because an Activity that is still running outlives a dropped request.

    .PARAMETER ActivityId
        The Activity to wait for.

    .PARAMETER ActivityLabel
        The Write-Progress -Activity label, e.g. "Recalling contributed values".

    .PARAMETER Timeout
        Maximum seconds to wait. Omit to wait indefinitely.

    .PARAMETER PollIntervalSeconds
        Seconds between polls. Defaults to 2.

    .OUTPUTS
        The Activity's terminal status as a string, or $null if the timeout elapsed first.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]$ActivityId,

        [Parameter(Mandatory)]
        [string]$ActivityLabel,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$Timeout,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$PollIntervalSeconds = 2
    )

    # Matches the ActivityStatus enum names the progress endpoint returns.
    $terminalStatuses = @('Complete', 'CompleteWithWarning', 'CompleteWithError', 'FailedWithError', 'Cancelled')

    $hasTimeout = $PSBoundParameters.ContainsKey('Timeout')
    $startTime = Get-Date
    $lastStatus = ''

    while ($true) {
        try {
            $activityProgress = Invoke-JIMApi -Endpoint "/api/v1/activities/$ActivityId/progress"
            $status = "$($activityProgress.status ?? 'Running')"

            if ($status -ne $lastStatus) {
                Write-Verbose "${ActivityLabel}: $status"
                $lastStatus = $status
            }

            $elapsed = [int]((Get-Date) - $startTime).TotalSeconds
            $progressParams = Get-JIMActivityProgressDisplay -Progress $activityProgress -ActivityLabel $ActivityLabel -ElapsedSeconds $elapsed
            Write-Progress @progressParams

            if ($status -in $terminalStatuses) {
                Write-Progress -Activity $ActivityLabel -Completed
                return $status
            }
        }
        catch {
            Write-Verbose "Could not read progress for Activity ${ActivityId}: $_"
        }

        if ($hasTimeout -and ((Get-Date) - $startTime).TotalSeconds -ge $Timeout) {
            Write-Progress -Activity $ActivityLabel -Completed
            return $null
        }

        Start-Sleep -Seconds $PollIntervalSeconds
    }
}
