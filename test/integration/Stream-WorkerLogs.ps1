<#
.SYNOPSIS
    Streams worker diagnostic log lines to the Metrics API during an integration test run.

.DESCRIPTION
    Tails the worker log file using Get-Content -Wait and filters for DiagnosticListener
    and MetricsCheckpoint lines. Filtered lines are buffered and POSTed to the Metrics API
    in batches (every 200 lines or 5 seconds, whichever comes first).

    Designed to run as a background job started by Run-IntegrationTests.ps1. Exits cleanly
    when the log file handle is released (container stopped) or when the job is stopped.

    Graceful failure: if the API is unreachable, lines are buffered up to a cap and retried.
    Streaming failures never affect the test run.

.PARAMETER LogFilePath
    Path to the worker log file (from bind mount).

.PARAMETER ApiUrl
    Base URL of the Metrics API (e.g. https://metrics.tetron.io).

.PARAMETER ApiKey
    API key for authentication (sent as X-API-Key header).

.PARAMETER RunId
    Unique identifier for this test run.

.PARAMETER Scenario
    Scenario name (e.g. "Scenario1-HRToIdentityDirectory").

.PARAMETER Template
    Template size (e.g. "Medium", "Scale100k50Groups").

.PARAMETER HostClass
    Host class label from Get-HostFingerprint.ps1 (e.g. "4c-8g-ssd").
#>

param(
    [Parameter(Mandatory)][string]$LogFilePath,
    [Parameter(Mandatory)][string]$ApiUrl,
    [Parameter(Mandatory)][string]$ApiKey,
    [Parameter(Mandatory)][string]$RunId,
    [Parameter(Mandatory)][string]$Scenario,
    [Parameter(Mandatory)][string]$Template,
    [Parameter(Mandatory)][string]$HostClass
)

$ErrorActionPreference = "Continue"

$batchSize = 200
$flushIntervalSeconds = 5
$maxBufferSize = 5000  # Cap to prevent unbounded memory growth if API is down
$buffer = [System.Collections.Generic.List[string]]::new()
$lastFlush = [DateTime]::UtcNow
$apiEndpoint = "$($ApiUrl.TrimEnd('/'))/api/v1/runs/$RunId/logs"

function Send-Batch {
    param([System.Collections.Generic.List[string]]$Lines)

    if ($Lines.Count -eq 0) { return $true }

    $payload = @{
        runId    = $RunId
        scenario = $Scenario
        template = $Template
        hostClass = $HostClass
        lines    = @($Lines)
    } | ConvertTo-Json -Depth 3 -Compress

    try {
        $headers = @{
            "X-API-Key"    = $ApiKey
            "Content-Type" = "application/json"
        }
        Invoke-RestMethod -Uri $apiEndpoint -Method POST -Headers $headers -Body $payload -TimeoutSec 10 | Out-Null
        return $true
    }
    catch {
        Write-Warning "MetricsStreaming: Failed to send batch ($($Lines.Count) lines): $($_.Exception.Message)"
        return $false
    }
}

# Wait for the log file to appear (the container may not have started writing yet)
$waitStart = [DateTime]::UtcNow
while (-not (Test-Path $LogFilePath)) {
    if (([DateTime]::UtcNow - $waitStart).TotalSeconds -gt 120) {
        Write-Warning "MetricsStreaming: Log file not found after 120s, exiting: $LogFilePath"
        exit 0
    }
    Start-Sleep -Seconds 1
}

# Tail the log file, filtering for metrics-relevant lines
try {
    Get-Content -Path $LogFilePath -Wait -Tail 0 -ErrorAction Stop | ForEach-Object {
        $line = $_

        # Only capture DiagnosticListener spans and MetricsCheckpoint lines
        if ($line -match "DiagnosticListener:" -or $line -match "MetricsCheckpoint:") {
            $buffer.Add($line)
        }

        # Flush when buffer is full or flush interval has elapsed
        $elapsed = ([DateTime]::UtcNow - $lastFlush).TotalSeconds
        if ($buffer.Count -ge $batchSize -or ($buffer.Count -gt 0 -and $elapsed -ge $flushIntervalSeconds)) {
            $success = Send-Batch -Lines $buffer
            if ($success) {
                $buffer.Clear()
            }
            elseif ($buffer.Count -gt $maxBufferSize) {
                # Drop oldest lines to prevent unbounded growth
                $toDrop = $buffer.Count - $maxBufferSize
                $buffer.RemoveRange(0, $toDrop)
                Write-Warning "MetricsStreaming: Buffer exceeded $maxBufferSize lines, dropped $toDrop oldest"
            }
            $lastFlush = [DateTime]::UtcNow
        }
    }
}
catch {
    # Get-Content -Wait throws when the pipeline is stopped (job cancellation) - this is expected
    if ($_.Exception -isnot [System.Management.Automation.PipelineStoppedException]) {
        Write-Warning "MetricsStreaming: Unexpected error: $($_.Exception.Message)"
    }
}
finally {
    # Final flush of remaining buffer
    if ($buffer.Count -gt 0) {
        Send-Batch -Lines $buffer | Out-Null
    }
}
