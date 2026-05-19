<#
.SYNOPSIS
    Submits end-of-run test results summary to the Metrics API.

.DESCRIPTION
    Called at the end of an integration test run to submit the final summary including
    pass/fail status, wall-clock duration, host fingerprint, and scenario metadata.
    Signals run completion to the API so dashboards can mark the run as finished.

    On success, prints a Grafana dashboard URL for the run.

    Graceful failure: if submission fails, logs a warning but never fails the test run.

.PARAMETER RunId
    Unique identifier for this test run (same ID used by Stream-WorkerLogs.ps1).

.PARAMETER ResultFile
    Path to the performance result JSON file (existing runner output format).

.PARAMETER HostFingerprint
    Host fingerprint object from Get-HostFingerprint.ps1.

.PARAMETER Scenario
    Scenario name.

.PARAMETER Template
    Template size.

.PARAMETER Step
    Test step (e.g. "All", "Joiner", "Mover").

.PARAMETER DirectoryType
    Directory type used (e.g. "SambaAD", "OpenLDAP").

.PARAMETER Success
    Whether the test run passed.

.PARAMETER ExitCode
    Exit code from the scenario script.

.PARAMETER TestDurationMs
    Total test duration in milliseconds.

.PARAMETER ApiUrl
    Base URL of the Metrics API.

.PARAMETER ApiKey
    API key for authentication.
#>

param(
    [Parameter(Mandatory)][string]$RunId,
    [Parameter(Mandatory)][string]$Scenario,
    [Parameter(Mandatory)][string]$Template,
    [Parameter(Mandatory)][string]$Step,
    [Parameter(Mandatory)][string]$DirectoryType,
    [Parameter(Mandatory)][bool]$Success,
    [Parameter(Mandatory)][int]$ExitCode,
    [Parameter(Mandatory)][double]$TestDurationMs,
    [Parameter(Mandatory)]$HostFingerprint,
    [Parameter(Mandatory)][string]$ApiUrl,
    [Parameter(Mandatory)][string]$ApiKey,
    [string]$ResultFile
)

$ErrorActionPreference = "Continue"

$apiEndpoint = "$($ApiUrl.TrimEnd('/'))/api/v1/runs/$RunId/complete"

# Build the completion payload. schemaVersion is explicit so future server-side
# contract bumps can reject or adapt based on the value rather than relying on
# the "absent = 1 with deprecation warning" compatibility path.
$payload = @{
    schemaVersion   = 1
    runId           = $RunId
    scenario        = $Scenario
    template        = $Template
    step            = $Step
    directoryType   = $DirectoryType
    hostFingerprint = $HostFingerprint
    success         = $Success
    exitCode        = $ExitCode
    testDurationMs  = $TestDurationMs
    completedAt     = (Get-Date).ToUniversalTime().ToString("o")
}

# Include wall-clock timings from result file if available.
# Use PSObject.Properties rather than direct property access so this stays
# silent under Set-StrictMode when the result file is the wall-clock-only
# shape (no Timings sub-object); a missing property is expected, not an error.
if ($ResultFile -and (Test-Path $ResultFile)) {
    try {
        $resultData = Get-Content $ResultFile -Raw | ConvertFrom-Json
        if ($resultData.PSObject.Properties.Name -contains 'Timings' -and $resultData.Timings) {
            $payload.wallClockTimings = $resultData.Timings
        }
    }
    catch {
        Write-Warning "MetricsSubmission: Failed to parse result file: $($_.Exception.Message)"
    }
}

try {
    $headers = @{
        "X-API-Key"    = $ApiKey
        "Content-Type" = "application/json"
    }
    $body = $payload | ConvertTo-Json -Depth 5 -Compress

    $response = Invoke-RestMethod -Uri $apiEndpoint -Method POST -Headers $headers -Body $body -TimeoutSec 15

    Write-Host ""
    Write-Host "  Metrics submitted successfully" -ForegroundColor Green
    if ($response.grafanaUrl) {
        Write-Host "  Dashboard: $($response.grafanaUrl)" -ForegroundColor Cyan
    }
    Write-Host ""
}
catch {
    Write-Warning "MetricsSubmission: Failed to submit results: $($_.Exception.Message)"
    Write-Warning "MetricsSubmission: Results were streamed during the run; only the completion signal was lost."
}
