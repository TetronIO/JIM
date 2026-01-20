#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
$BaseUrl = 'http://localhost:5200/api/v1'
$ApiKey = 'jim_ak_omL3W2TZCSkiDvbc4YscV7k81sCT79fH4npHJfrsOA'
$Headers = @{ 'X-API-Key' = $ApiKey; 'Accept' = 'application/json' }

function Invoke-Api($Uri, $Method = 'Get') {
    try {
        return Invoke-RestMethod -Uri $Uri -Headers $Headers -Method $Method -ContentType 'application/json'
    } catch {
        Write-Host "API Error: $($_.Exception.Message)" -ForegroundColor Red
        throw
    }
}

Write-Host "Testing API health..." -ForegroundColor Cyan
$health = Invoke-Api "$BaseUrl/health/ready"
Write-Host "  Health: $($health.status)" -ForegroundColor Green

Write-Host "`nListing connected systems..." -ForegroundColor Cyan
$systems = Invoke-Api "$BaseUrl/synchronisation/connected-systems"
Write-Host "  Found $($systems.items.Count) systems"
foreach ($s in $systems.items) {
    Write-Host "  - $($s.name) (ID: $($s.id))"
}

# Find Source system
$sourceSystem = $systems.items | Where-Object { $_.name -like '*APAC*' }
if (-not $sourceSystem) {
    throw "Could not find APAC Source system"
}
Write-Host "`nSource System: $($sourceSystem.name) (ID: $($sourceSystem.id))" -ForegroundColor Green

# Get run profiles
$profiles = Invoke-Api "$BaseUrl/synchronisation/connected-systems/$($sourceSystem.id)/run-profiles"
$fullSyncProfile = $profiles | Where-Object { $_.name -eq 'Full Sync' }
if (-not $fullSyncProfile) {
    throw "Could not find Full Sync profile"
}
Write-Host "Full Sync Profile ID: $($fullSyncProfile.id)"

# Execute Full Sync
Write-Host "`nExecuting Full Sync on Source..." -ForegroundColor Cyan
$result = Invoke-Api "$BaseUrl/synchronisation/connected-systems/$($sourceSystem.id)/run-profiles/$($fullSyncProfile.id)/execute" -Method Post
$activityId = $result.activityId
Write-Host "  Activity ID: $activityId"

# Wait for completion
Write-Host "  Waiting for completion..." -NoNewline
$maxWait = 60
$waited = 0
while ($waited -lt $maxWait) {
    Start-Sleep -Seconds 2
    $waited += 2
    $activity = Invoke-Api "$BaseUrl/activities/$activityId"
    if ($activity.status -in @('Completed', 'Failed', 'Error')) {
        Write-Host " Done!" -ForegroundColor Green
        Write-Host "  Status: $($activity.status)"
        break
    }
    Write-Host "." -NoNewline
}

# Now run on Target
$targetSystem = $systems.items | Where-Object { $_.name -like '*EMEA*' }
if (-not $targetSystem) {
    throw "Could not find EMEA Target system"
}
Write-Host "`nTarget System: $($targetSystem.name) (ID: $($targetSystem.id))" -ForegroundColor Green

# Get Target run profiles
$profiles = Invoke-Api "$BaseUrl/synchronisation/connected-systems/$($targetSystem.id)/run-profiles"
$fullSyncProfile = $profiles | Where-Object { $_.name -eq 'Full Sync' }
$exportProfile = $profiles | Where-Object { $_.name -eq 'Export' }

if (-not $fullSyncProfile) {
    throw "Could not find Full Sync profile for target"
}

# Execute Full Sync on Target
Write-Host "`nExecuting Full Sync on Target..." -ForegroundColor Cyan
$result = Invoke-Api "$BaseUrl/synchronisation/connected-systems/$($targetSystem.id)/run-profiles/$($fullSyncProfile.id)/execute" -Method Post
$activityId = $result.activityId
Write-Host "  Activity ID: $activityId"

# Wait for completion
Write-Host "  Waiting for completion..." -NoNewline
$waited = 0
while ($waited -lt $maxWait) {
    Start-Sleep -Seconds 2
    $waited += 2
    $activity = Invoke-Api "$BaseUrl/activities/$activityId"
    if ($activity.status -in @('Completed', 'Failed', 'Error')) {
        Write-Host " Done!" -ForegroundColor Green
        Write-Host "  Status: $($activity.status)"
        break
    }
    Write-Host "." -NoNewline
}

# Execute Export on Target
if ($exportProfile) {
    Write-Host "`nExecuting Export on Target..." -ForegroundColor Cyan
    $result = Invoke-Api "$BaseUrl/synchronisation/connected-systems/$($targetSystem.id)/run-profiles/$($exportProfile.id)/execute" -Method Post
    $activityId = $result.activityId
    Write-Host "  Activity ID: $activityId"

    # Wait for completion
    Write-Host "  Waiting for completion..." -NoNewline
    $waited = 0
    while ($waited -lt $maxWait) {
        Start-Sleep -Seconds 2
        $waited += 2
        $activity = Invoke-Api "$BaseUrl/activities/$activityId"
        if ($activity.status -in @('Completed', 'Failed', 'Error')) {
            Write-Host " Done!" -ForegroundColor Green
            Write-Host "  Status: $($activity.status)"
            break
        }
        Write-Host "." -NoNewline
    }
}

Write-Host "`nForward Sync complete!" -ForegroundColor Green
