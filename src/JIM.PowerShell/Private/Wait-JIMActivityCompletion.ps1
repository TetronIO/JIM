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

        A transient polling failure is not fatal: it is warned about and the wait continues, because an
        Activity that is still running outlives a dropped request. Repeated authentication failures are,
        because every further poll would fail the same way; the caller is told the operation is still
        running on the server and how to check on it once re-authenticated.

    .PARAMETER ActivityId
        The Activity to wait for.

    .PARAMETER ActivityLabel
        The Write-Progress -Activity label, e.g. "Recalling contributed values".

    .PARAMETER Timeout
        Maximum seconds to wait. Omit to wait indefinitely.

    .PARAMETER PollIntervalSeconds
        Seconds between polls. Defaults to 2.

    .PARAMETER AbortSentinelPath
        Optional path to a cooperative abort sentinel. When the file exists and is non-empty, a test
        harness has decided the run should fail (typically because an error watcher saw an [ERR] line in
        JIM's logs), and the wait throws rather than polling to its natural end.

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
        [int]$PollIntervalSeconds = 2,

        [string]$AbortSentinelPath
    )

    # Matches the ActivityStatus enum names the progress endpoint returns.
    $terminalStatuses = @('Complete', 'CompleteWithWarning', 'CompleteWithError', 'FailedWithError', 'Cancelled')

    $hasTimeout = $PSBoundParameters.ContainsKey('Timeout')
    $startTime = Get-Date
    $lastStatus = ''

    # Invoke-JIMApi may have refreshed the token transparently, so a single authentication failure is
    # worth another attempt; three in a row is not.
    $consecutiveAuthFailures = 0
    $maxAuthFailures = 3

    while ($true) {
        if ($AbortSentinelPath -and (Test-Path $AbortSentinelPath)) {
            $sentinelInfo = Get-Item $AbortSentinelPath -ErrorAction SilentlyContinue
            if ($sentinelInfo -and $sentinelInfo.Length -gt 0) {
                Write-Progress -Activity $ActivityLabel -Completed
                throw "Wait aborted for '$ActivityLabel': JIM error watcher reported errors (see $AbortSentinelPath). Activity ID: $ActivityId."
            }
        }

        try {
            $activityProgress = Invoke-JIMApi -Endpoint "/api/v1/activities/$ActivityId/progress"
            $consecutiveAuthFailures = 0
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
            $errorMsg = "$_"

            if ($errorMsg -match 'Authentication failed|session may have expired|API key may be invalid') {
                $consecutiveAuthFailures++

                if ($consecutiveAuthFailures -ge $maxAuthFailures) {
                    Write-Progress -Activity $ActivityLabel -Completed
                    throw "Authentication failed while monitoring activity $ActivityId. The operation was submitted successfully and may still be running on the server. Use Get-JIMActivity -Id $ActivityId to check its status after re-authenticating with Connect-JIM."
                }

                Write-Warning "Authentication error while checking activity status (attempt $consecutiveAuthFailures of $maxAuthFailures). Retrying..."
            }
            else {
                Write-Warning "Error checking activity status: $errorMsg"
            }
        }

        if ($hasTimeout -and ((Get-Date) - $startTime).TotalSeconds -ge $Timeout) {
            Write-Progress -Activity $ActivityLabel -Completed
            return $null
        }

        Start-Sleep -Seconds $PollIntervalSeconds
    }
}
