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

# Build the completion payload
$payload = @{
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

# Include wall-clock timings from result file if available
if ($ResultFile -and (Test-Path $ResultFile)) {
    try {
        $resultData = Get-Content $ResultFile -Raw | ConvertFrom-Json
        if ($resultData.Timings) {
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

    # Build and display Grafana dashboard URL
    $grafanaUrl = $ApiUrl -replace '/api$', '' -replace ':5000', ':3000'  # Convention: Grafana on port 3000
    if ($response.grafanaUrl) {
        $grafanaUrl = $response.grafanaUrl
    }
    $dashboardUrl = "$($grafanaUrl.TrimEnd('/'))/d/run-detail?var-runId=$RunId"

    Write-Host ""
    Write-Host "  Metrics submitted successfully" -ForegroundColor Green
    Write-Host "  Dashboard: $dashboardUrl" -ForegroundColor Cyan
    Write-Host ""
}
catch {
    Write-Warning "MetricsSubmission: Failed to submit results: $($_.Exception.Message)"
    Write-Warning "MetricsSubmission: Results were streamed during the run; only the completion signal was lost."
}
