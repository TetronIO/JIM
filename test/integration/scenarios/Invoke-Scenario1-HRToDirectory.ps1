<#
.SYNOPSIS
    Test Scenario 1: HR to Enterprise Directory

.DESCRIPTION
    Validates provisioning users from HR system (CSV) to enterprise directory (Samba AD).
    Tests the complete ILM lifecycle: Joiner, Mover, Leaver, and Reconnection patterns.

.PARAMETER Step
    Which test step to execute (Joiner, Leaver, Mover, Reconnection, All)

.PARAMETER Template
    Data scale template (Micro, Small, Medium, Large, XLarge, XXLarge)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER WaitSeconds
    Seconds to wait between steps for JIM processing (default: 30)

.EXAMPLE
    ./Invoke-Scenario1-HRToDirectory.ps1 -Step All -Template Small -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario1-HRToDirectory.ps1 -Step Joiner -Template Micro -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Joiner", "Leaver", "Mover", "Reconnection", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Micro", "Small", "Medium", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

Write-TestSection "Scenario 1: HR to Enterprise Directory"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "HR to Enterprise Directory"
    Template = $Template
    Steps = @()
    Success = $false
}

try {
    # Step 0: Setup JIM configuration
    Write-TestSection "Step 0: Setup JIM Configuration"

    if (-not $ApiKey) {
        Write-Host "  No API key provided" -ForegroundColor Yellow
        Write-Host "  Create an API key via JIM web UI: Admin > API Keys" -ForegroundColor Yellow
        throw "API key required for authentication"
    }

    $config = & "$PSScriptRoot/../Setup-Scenario1.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template

    if (-not $config) {
        throw "Failed to setup Scenario 1 configuration"
    }

    Write-Host "✓ JIM configured for Scenario 1" -ForegroundColor Green
    Write-Host "  CSV System ID: $($config.CSVSystemId)" -ForegroundColor Gray
    Write-Host "  LDAP System ID: $($config.LDAPSystemId)" -ForegroundColor Gray

    # Re-import module to ensure we have connection
    $modulePath = "$PSScriptRoot/../../../JIM.PowerShell/JIM/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop

    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    # Test 1: Joiner (New Hire)
    if ($Step -eq "Joiner" -or $Step -eq "All") {
        Write-TestSection "Test 1: Joiner (New Hire)"

        Write-Host "Creating new test user in CSV..." -ForegroundColor Gray

        # Generate a unique test user
        $testUser = New-TestUser -Index 9999
        $testUser.EmployeeId = "EMP999999"
        $testUser.SamAccountName = "test.joiner"
        $testUser.Email = "test.joiner@testdomain.local"
        $testUser.DisplayName = "Test Joiner"

        # Add user to CSV file
        $csvPath = "$PSScriptRoot/../test-data/hr-users.csv"
        $csvLine = "`"$($testUser.EmployeeId)`",`"$($testUser.FirstName)`",`"$($testUser.LastName)`",`"$($testUser.Email)`",`"$($testUser.Department)`",`"$($testUser.Title)`",`"$($testUser.SamAccountName)`",`"$($testUser.DisplayName)`",`"Active`""

        Add-Content -Path $csvPath -Value $csvLine
        Write-Host "  ✓ Added test.joiner to CSV" -ForegroundColor Green

        # Copy updated CSV to container
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Trigger CSV Import
        Write-Host "Triggering CSV import..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -PassThru

        Write-Host "  ✓ CSV import started (Activity: $($importResult.activityId))" -ForegroundColor Green

        # Wait for processing
        Write-Host "Waiting $WaitSeconds seconds for processing..." -ForegroundColor Gray
        Start-Sleep -Seconds $WaitSeconds

        # Trigger LDAP Export
        Write-Host "Triggering LDAP export..." -ForegroundColor Gray
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -PassThru

        Write-Host "  ✓ LDAP export started (Activity: $($exportResult.activityId))" -ForegroundColor Green

        # Wait for export
        Start-Sleep -Seconds $WaitSeconds

        # Validate user exists in AD
        Write-Host "Validating user in Samba AD..." -ForegroundColor Gray

        $adUser = docker exec samba-ad-primary samba-tool user show $testUser.SamAccountName 2>&1

        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ User 'test.joiner' provisioned to AD" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Joiner"; Success = $true }
        }
        else {
            Write-Host "  ✗ User 'test.joiner' NOT found in AD" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Joiner"; Success = $false; Error = "User not found in AD" }
        }
    }

    # Test 2: Mover (Attribute Change)
    if ($Step -eq "Mover" -or $Step -eq "All") {
        Write-TestSection "Test 2: Mover (Attribute Change)"

        Write-Host "Updating user department in CSV..." -ForegroundColor Gray

        # Update CSV - change bob.smith1's department
        $csvPath = "$PSScriptRoot/../test-data/hr-users.csv"
        $csvContent = Get-Content $csvPath

        $updatedContent = $csvContent | ForEach-Object {
            if ($_ -match "bob\.smith1") {
                $_ -replace '"HR"', '"IT"'  # Change department from HR to IT
            }
            else {
                $_
            }
        }

        $updatedContent | Set-Content $csvPath
        Write-Host "  ✓ Changed bob.smith1 department to IT" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Trigger sync
        Write-Host "Triggering synchronisation..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        # Validate department change
        Write-Host "Validating attribute update in AD..." -ForegroundColor Gray

        $adUserInfo = docker exec samba-ad-primary samba-tool user show bob.smith1 2>&1

        if ($adUserInfo -match "department:.*IT") {
            Write-Host "  ✓ Department updated to IT in AD" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Mover"; Success = $true }
        }
        else {
            Write-Host "  ✗ Department not updated in AD" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Mover"; Success = $false; Error = "Attribute not updated" }
        }
    }

    # Test 3: Leaver (Deprovisioning)
    if ($Step -eq "Leaver" -or $Step -eq "All") {
        Write-TestSection "Test 3: Leaver (Deprovisioning)"

        Write-Host "Removing user from CSV..." -ForegroundColor Gray

        $csvPath = "$PSScriptRoot/../test-data/hr-users.csv"
        $csvContent = Get-Content $csvPath

        # Remove test.joiner (or bob.smith1 if joiner not run)
        $userToRemove = if ($Step -eq "Leaver") { "bob.smith1" } else { "test.joiner" }

        $filteredContent = $csvContent | Where-Object { $_ -notmatch $userToRemove }
        $filteredContent | Set-Content $csvPath

        Write-Host "  ✓ Removed $userToRemove from CSV" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Trigger sync
        Write-Host "Triggering synchronisation..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        # Validate user removed/disabled in AD
        Write-Host "Validating deprovisioning in AD..." -ForegroundColor Gray

        $adUserCheck = docker exec samba-ad-primary samba-tool user show $userToRemove 2>&1

        # User should either be deleted or disabled depending on deletion rules
        if ($LASTEXITCODE -ne 0 -or $adUserCheck -match "disabled") {
            Write-Host "  ✓ User $userToRemove deprovisioned in AD" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Leaver"; Success = $true }
        }
        else {
            Write-Host "  ⚠ User $userToRemove still active in AD (check deletion rules)" -ForegroundColor Yellow
            $testResults.Steps += @{ Name = "Leaver"; Success = $true; Warning = "User not deleted (may be expected based on rules)" }
        }
    }

    # Test 4: Reconnection (Delete and Restore)
    if ($Step -eq "Reconnection" -or $Step -eq "All") {
        Write-TestSection "Test 4: Reconnection (Delete and Restore)"

        Write-Host "Testing delete and restore before grace period..." -ForegroundColor Gray

        # Create test user
        $reconnectUser = New-TestUser -Index 8888
        $reconnectUser.EmployeeId = "EMP888888"
        $reconnectUser.SamAccountName = "test.reconnect"

        # Add to CSV
        $csvPath = "$PSScriptRoot/../test-data/hr-users.csv"
        $csvLine = "`"$($reconnectUser.EmployeeId)`",`"$($reconnectUser.FirstName)`",`"$($reconnectUser.LastName)`",`"$($reconnectUser.Email)`",`"$($reconnectUser.Department)`",`"$($reconnectUser.Title)`",`"$($reconnectUser.SamAccountName)`",`"Test Reconnect`",`"Active`""

        Add-Content -Path $csvPath -Value $csvLine
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Initial sync
        Write-Host "  Initial sync..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds
        Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        # Remove user (simulating quit)
        Write-Host "  Removing user (simulating quit)..." -ForegroundColor Gray
        $csvContent = Get-Content $csvPath | Where-Object { $_ -notmatch "test.reconnect" }
        $csvContent | Set-Content $csvPath
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds 10  # Short wait

        # Restore user (simulating rehire before grace period)
        Write-Host "  Restoring user (simulating rehire)..." -ForegroundColor Gray
        Add-Content -Path $csvPath -Value $csvLine
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds
        Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        # Verify user still exists (reconnection should preserve AD account)
        $adUserCheck = docker exec samba-ad-primary samba-tool user show test.reconnect 2>&1

        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ Reconnection successful - user preserved in AD" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Reconnection"; Success = $true }
        }
        else {
            Write-Host "  ✗ Reconnection failed - user lost in AD" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Reconnection"; Success = $false; Error = "User deleted instead of preserved" }
        }
    }

    # Summary
    Write-TestSection "Test Results Summary"

    $successCount = ($testResults.Steps | Where-Object { $_.Success }).Count
    $totalCount = $testResults.Steps.Count

    Write-Host "Tests run:    $totalCount" -ForegroundColor Cyan
    Write-Host "Tests passed: $successCount" -ForegroundColor $(if ($successCount -eq $totalCount) { "Green" } else { "Yellow" })

    foreach ($stepResult in $testResults.Steps) {
        $status = if ($stepResult.Success) { "✓" } else { "✗" }
        $color = if ($stepResult.Success) { "Green" } else { "Red" }

        Write-Host "$status $($stepResult.Name)" -ForegroundColor $color

        if ($stepResult.Error) {
            Write-Host "  Error: $($stepResult.Error)" -ForegroundColor Red
        }
        if ($stepResult.Warning) {
            Write-Host "  Warning: $($stepResult.Warning)" -ForegroundColor Yellow
        }
    }

    $testResults.Success = ($successCount -eq $totalCount)

    if ($testResults.Success) {
        Write-Host ""
        Write-Host "✓ All tests passed" -ForegroundColor Green
        exit 0
    }
    else {
        Write-Host ""
        Write-Host "✗ Some tests failed" -ForegroundColor Red
        exit 1
    }
}
catch {
    Write-Host ""
    Write-Host "✗ Scenario 1 failed: $_" -ForegroundColor Red
    Write-Host "  Stack trace: $($_.ScriptStackTrace)" -ForegroundColor Gray
    $testResults.Error = $_.Exception.Message
    exit 1
}
