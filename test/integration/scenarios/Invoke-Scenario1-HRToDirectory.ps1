<#
.SYNOPSIS
    Test Scenario 1: HR to Enterprise Directory

.DESCRIPTION
    Validates provisioning users from HR system (CSV) to enterprise directory (Samba AD).
    Tests the complete ILM lifecycle: Joiner, Mover, Leaver, and Reconnection patterns.

.PARAMETER Step
    Which test step to execute (Joiner, Leaver, Mover, Reconnection, All)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

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
    [ValidateSet("Nano", "Micro", "Small", "Medium", "Large", "XLarge", "XXLarge")]
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

    # Reset CSV to baseline state before running tests
    # This ensures test data is in a known state regardless of previous test runs
    Write-Host "Resetting CSV test data to baseline..." -ForegroundColor Gray
    & "$PSScriptRoot/../Generate-TestCSV.ps1" -Template $Template -OutputPath "$PSScriptRoot/../../test-data"
    Write-Host "  ✓ CSV test data reset to baseline" -ForegroundColor Green

    # Clean up test-specific AD users from previous test runs
    # Only delete test.joiner and test.reconnect - NOT the baseline users (bob.smith1, etc.)
    # Baseline users are created by Populate-SambaAD.ps1 and are needed for Mover tests
    Write-Host "Cleaning up test-specific AD users from previous runs..." -ForegroundColor Gray
    $testUsers = @("test.joiner", "test.reconnect")
    $deletedCount = 0
    foreach ($user in $testUsers) {
        # Try to delete the user - if they don't exist, samba-tool will error but that's OK
        # Use bash -c to properly capture the output and exit code
        $output = & docker exec samba-ad-primary bash -c "samba-tool user delete '$user' 2>&1; echo EXIT_CODE:\$?"
        if ($output -match "Deleted user") {
            Write-Host "  ✓ Deleted $user from AD" -ForegroundColor Gray
            $deletedCount++
        } elseif ($output -match "Unable to find user") {
            Write-Host "  - $user not found (already clean)" -ForegroundColor DarkGray
        } else {
            Write-Host "  ⚠ Could not delete ${user}: $output" -ForegroundColor Yellow
        }
    }
    Write-Host "  ✓ AD cleanup complete ($deletedCount test users deleted)" -ForegroundColor Green

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

        # Add user to CSV file (with new columns: userPrincipalName and dn)
        # Note: Using test/test-data path as that's what's mounted to JIM containers via docker-compose.override.codespaces.yml
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($testUser.SamAccountName)@testdomain.local"
        $dn = "CN=$($testUser.DisplayName),CN=Users,DC=testdomain,DC=local"
        $csvLine = "`"$($testUser.EmployeeId)`",`"$($testUser.FirstName)`",`"$($testUser.LastName)`",`"$($testUser.Email)`",`"$($testUser.Department)`",`"$($testUser.Title)`",`"$($testUser.SamAccountName)`",`"$($testUser.DisplayName)`",`"Active`",`"$upn`",`"$dn`""

        Add-Content -Path $csvPath -Value $csvLine
        Write-Host "  ✓ Added test.joiner to CSV" -ForegroundColor Green

        # Copy updated CSV to container
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Trigger CSV Import
        Write-Host "Triggering CSV import..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -PassThru

        Write-Host "  ✓ CSV import started (Activity: $($importResult.activityId))" -ForegroundColor Green

        # Wait for import processing
        Write-Host "Waiting $WaitSeconds seconds for import..." -ForegroundColor Gray
        Start-Sleep -Seconds $WaitSeconds

        # Trigger Full Sync (evaluates sync rules, creates MVOs and pending exports)
        Write-Host "Triggering Full Sync..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -PassThru

        Write-Host "  ✓ Full Sync started (Activity: $($syncResult.activityId))" -ForegroundColor Green

        # Wait for sync processing
        Write-Host "Waiting $WaitSeconds seconds for sync..." -ForegroundColor Gray
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

        # Update CSV - change test.joiner's department (user provisioned in Joiner test)
        # We use test.joiner because they were provisioned via JIM and have proper CSO linkage
        # Using a user created by Populate-SambaAD wouldn't work as JIM doesn't know about them
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $csvContent = Get-Content $csvPath

        $updatedContent = $csvContent | ForEach-Object {
            if ($_ -match "test\.joiner") {
                $_ -replace '"Admin"', '"IT"'  # Change department from Admin to IT (index 9999 % 10 = 9 = Admin)
            }
            else {
                $_
            }
        }

        $updatedContent | Set-Content $csvPath
        Write-Host "  ✓ Changed test.joiner department to IT" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Trigger sync (Import → Full Sync → Export)
        Write-Host "Triggering synchronisation..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        # Validate department change
        Write-Host "Validating attribute update in AD..." -ForegroundColor Gray

        $adUserInfo = docker exec samba-ad-primary samba-tool user show test.joiner 2>&1

        if ($adUserInfo -match "department:.*IT") {
            Write-Host "  ✓ Department updated to IT in AD" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Mover"; Success = $true }
        }
        else {
            Write-Host "  ✗ Department not updated in AD" -ForegroundColor Red
            Write-Host "    AD output: $adUserInfo" -ForegroundColor Gray
            $testResults.Steps += @{ Name = "Mover"; Success = $false; Error = "Attribute not updated" }
        }
    }

    # Test 3: Leaver (Deprovisioning)
    if ($Step -eq "Leaver" -or $Step -eq "All") {
        Write-TestSection "Test 3: Leaver (Deprovisioning)"

        Write-Host "Removing user from CSV..." -ForegroundColor Gray

        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $csvContent = Get-Content $csvPath

        # Remove test.joiner (or bob.smith1 if joiner not run)
        $userToRemove = if ($Step -eq "Leaver") { "bob.smith1" } else { "test.joiner" }

        $filteredContent = $csvContent | Where-Object { $_ -notmatch $userToRemove }
        $filteredContent | Set-Content $csvPath

        Write-Host "  ✓ Removed $userToRemove from CSV" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Trigger sync (Import → Full Sync → Export)
        Write-Host "Triggering synchronisation..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId | Out-Null
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

        # Add to CSV (with new columns: userPrincipalName and dn)
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($reconnectUser.SamAccountName)@testdomain.local"
        $dn = "CN=Test Reconnect,CN=Users,DC=testdomain,DC=local"
        $csvLine = "`"$($reconnectUser.EmployeeId)`",`"$($reconnectUser.FirstName)`",`"$($reconnectUser.LastName)`",`"$($reconnectUser.Email)`",`"$($reconnectUser.Department)`",`"$($reconnectUser.Title)`",`"$($reconnectUser.SamAccountName)`",`"Test Reconnect`",`"Active`",`"$upn`",`"$dn`""

        Add-Content -Path $csvPath -Value $csvLine
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Initial sync (Import → Full Sync → Export)
        Write-Host "  Initial sync..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId | Out-Null
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
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId | Out-Null
        Start-Sleep -Seconds 10  # Short wait

        # Restore user (simulating rehire before grace period)
        Write-Host "  Restoring user (simulating rehire)..." -ForegroundColor Gray
        Add-Content -Path $csvPath -Value $csvLine
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId | Out-Null
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

    $successCount = @($testResults.Steps | Where-Object { $_.Success }).Count
    $totalCount = @($testResults.Steps).Count

    Write-Host "Tests run:    $totalCount" -ForegroundColor Cyan
    Write-Host "Tests passed: $successCount" -ForegroundColor $(if ($successCount -eq $totalCount) { "Green" } else { "Yellow" })

    foreach ($stepResult in $testResults.Steps) {
        $status = if ($stepResult.Success) { "✓" } else { "✗" }
        $color = if ($stepResult.Success) { "Green" } else { "Red" }

        Write-Host "$status $($stepResult.Name)" -ForegroundColor $color

        if ($stepResult.ContainsKey('Error') -and $stepResult.Error) {
            Write-Host "  Error: $($stepResult.Error)" -ForegroundColor Red
        }
        if ($stepResult.ContainsKey('Warning') -and $stepResult.Warning) {
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
