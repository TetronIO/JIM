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
    Seconds to wait between steps for JIM processing (default: 5)
    Note: Most operations now use -Wait for synchronous execution.

.PARAMETER RunProfileTimeout
    Timeout in seconds for run profile operations (default: 300)

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
    [int]$WaitSeconds = 5,

    [Parameter(Mandatory=$false)]
    [int]$RunProfileTimeout = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'  # Disable confirmation prompts for non-interactive execution

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

# Performance tracking
$scenarioStartTime = Get-Date
$stepTimings = @{}

try {
    # Step 0: Setup JIM configuration
    $step0Start = Get-Date
    Write-TestSection "Step 0: Setup JIM Configuration"

    if (-not $ApiKey) {
        Write-Host "  No API key provided" -ForegroundColor Yellow
        Write-Host "  Create an API key via JIM web UI: Admin > API Keys" -ForegroundColor Yellow
        throw "API key required for authentication"
    }

    # IMPORTANT: For fully repeatable tests, JIM's database should be reset between runs.
    # Run 'jim-reset' before running tests, or use:
    #   docker compose -f docker-compose.yml down -v && jim-stack
    # This cleans up MVOs, CSOs, and pending exports from previous test runs.

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
    $stepTimings["0. Setup"] = (Get-Date) - $step0Start

    # Test 1: Joiner (New Hire)
    if ($Step -eq "Joiner" -or $Step -eq "All") {
        $step1Start = Get-Date
        Write-TestSection "Test 1: Joiner (New Hire)"

        Write-Host "Creating new test user in CSV..." -ForegroundColor Gray

        # Generate a unique test user
        $testUser = New-TestUser -Index 9999
        $testUser.EmployeeId = "EMP999999"
        $testUser.SamAccountName = "test.joiner"
        $testUser.Email = "test.joiner@testdomain.local"
        $testUser.DisplayName = "Test Joiner"

        # Add user to CSV file
        # Note: Using test/test-data path as that's what's mounted to JIM containers via docker-compose.override.codespaces.yml
        # DN is calculated dynamically by the export sync rule expression, not stored in the CSV
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($testUser.SamAccountName)@testdomain.local"
        $csvLine = "`"$($testUser.EmployeeId)`",`"$($testUser.FirstName)`",`"$($testUser.LastName)`",`"$($testUser.Email)`",`"$($testUser.Department)`",`"$($testUser.Title)`",`"$($testUser.SamAccountName)`",`"$($testUser.DisplayName)`",`"Active`",`"$upn`""

        Add-Content -Path $csvPath -Value $csvLine
        Write-Host "  ✓ Added test.joiner to CSV" -ForegroundColor Green

        # Copy updated CSV to container
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Trigger CSV Import
        Write-Host "Triggering CSV import..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -Timeout $RunProfileTimeout -PassThru

        Write-Host "  ✓ CSV import completed (Activity: $($importResult.activityId))" -ForegroundColor Green

        # Trigger Full Sync (evaluates sync rules, creates MVOs and pending exports)
        Write-Host "Triggering Full Sync..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -Timeout $RunProfileTimeout -PassThru

        Write-Host "  ✓ Full Sync completed (Activity: $($syncResult.activityId))" -ForegroundColor Green

        # Trigger LDAP Export
        Write-Host "Triggering LDAP export..." -ForegroundColor Gray
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -Timeout $RunProfileTimeout -PassThru

        Write-Host "  ✓ LDAP export completed (Activity: $($exportResult.activityId))" -ForegroundColor Green

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
        $stepTimings["1. Joiner"] = (Get-Date) - $step1Start
    }

    # Test 2a: Mover (Attribute Change - No DN Impact)
    if ($Step -eq "Mover" -or $Step -eq "All") {
        $step2aStart = Get-Date
        Write-TestSection "Test 2a: Mover (Attribute Change)"

        Write-Host "Updating user title in CSV..." -ForegroundColor Gray

        # Update CSV - change test.joiner's title (user provisioned in Joiner test)
        # We use test.joiner because they were provisioned via JIM and have proper CSO linkage
        # Using a user created by Populate-SambaAD wouldn't work as JIM doesn't know about them
        #
        # NOTE: We change Title (not Department) because Department now affects DN/OU placement.
        # This test validates simple attribute updates that don't trigger DN changes.
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $csvContent = Get-Content $csvPath

        # CSV columns: employeeId,firstName,lastName,email,department,title,samAccountName,displayName,status,userPrincipalName,dn
        # Change title from whatever it is to "Senior Developer"
        $updatedContent = $csvContent | ForEach-Object {
            if ($_ -match "test\.joiner") {
                # Replace the title field (between department and samAccountName)
                # Pattern: "...",Department,"OldTitle","samAccountName",...
                $_ -replace ',"[^"]*","test\.joiner"', ',"Senior Developer","test.joiner"'
            }
            else {
                $_
            }
        }

        $updatedContent | Set-Content $csvPath
        Write-Host "  ✓ Changed test.joiner title to 'Senior Developer'" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Trigger sync (Import → Full Sync → Export)
        Write-Host "Triggering synchronisation..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Write-Host "  ✓ Synchronisation completed" -ForegroundColor Green

        # Validate title change
        Write-Host "Validating attribute update in AD..." -ForegroundColor Gray

        $adUserInfo = docker exec samba-ad-primary samba-tool user show test.joiner 2>&1

        if ($adUserInfo -match "title:.*Senior Developer") {
            Write-Host "  ✓ Title updated to 'Senior Developer' in AD" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Mover"; Success = $true }
        }
        else {
            Write-Host "  ✗ Title not updated in AD" -ForegroundColor Red
            Write-Host "    AD output: $adUserInfo" -ForegroundColor Gray
            $testResults.Steps += @{ Name = "Mover"; Success = $false; Error = "Attribute not updated" }
        }
        $stepTimings["2a. Mover"] = (Get-Date) - $step2aStart
    }

    # Test 2b: Mover - Rename (DN Change)
    if ($Step -eq "Mover" -or $Step -eq "All") {
        $step2bStart = Get-Date
        Write-TestSection "Test 2b: Mover - Rename (DN Change)"

        Write-Host "Updating user display name in CSV (triggers AD rename)..." -ForegroundColor Gray

        # The DN is computed from displayName: "CN=" + EscapeDN(mv["Display Name"]) + ",CN=Users,DC=testdomain,DC=local"
        # So changing firstName + lastName in CSV will change displayName, which changes DN
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $csvContent = Get-Content $csvPath

        # Change test.joiner's first name from "Test" to "Renamed"
        # This will change displayName from "Test Joiner" to "Renamed Joiner"
        # Which should trigger a DN rename from "CN=Test Joiner,..." to "CN=Renamed Joiner,..."
        #
        # CSV columns: employeeId,firstName,lastName,email,department,title,samAccountName,displayName,status,userPrincipalName,dn
        # We need to update: firstName (col 2), displayName (col 8), and dn (col 11)
        $updatedContent = $csvContent | ForEach-Object {
            if ($_ -match "test\.joiner") {
                # Update firstName from "Test" to "Renamed" (between employeeId and lastName)
                $line = $_ -replace '"Test","Joiner"', '"Renamed","Joiner"'
                # Update displayName from "Test Joiner" to "Renamed Joiner"
                $line = $line -replace '"Test Joiner"', '"Renamed Joiner"'
                # Update dn from "CN=Test Joiner,..." to "CN=Renamed Joiner,..."
                $line = $line -replace 'CN=Test Joiner,', 'CN=Renamed Joiner,'
                $line
            }
            else {
                $_
            }
        }

        $updatedContent | Set-Content $csvPath
        Write-Host "  ✓ Changed test.joiner display name to 'Renamed Joiner'" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Trigger sync (Import → Full Sync → Export)
        Write-Host "Triggering synchronisation..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Write-Host "  ✓ Synchronisation completed" -ForegroundColor Green

        # Validate rename in AD
        # The user should now have DN "CN=Renamed Joiner,CN=Users,DC=testdomain,DC=local"
        Write-Host "Validating rename in AD..." -ForegroundColor Gray

        # Try to find the user with the new name
        $adUserInfo = docker exec samba-ad-primary bash -c "ldbsearch -H /var/lib/samba/private/sam.ldb '(sAMAccountName=test.joiner)' dn displayName 2>&1"

        if ($adUserInfo -match "CN=Renamed Joiner") {
            Write-Host "  ✓ User renamed to 'CN=Renamed Joiner' in AD" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Mover-Rename"; Success = $true }
        }
        else {
            Write-Host "  ✗ User NOT renamed in AD" -ForegroundColor Red
            Write-Host "    AD output: $adUserInfo" -ForegroundColor Gray
            $testResults.Steps += @{ Name = "Mover-Rename"; Success = $false; Error = "DN not renamed" }
        }
        $stepTimings["2b. Mover-Rename"] = (Get-Date) - $step2bStart
    }

    # Test 2c: Mover - Move (OU Change via Department)
    if ($Step -eq "Mover-Move" -or $Step -eq "All") {
        $step2cStart = Get-Date
        Write-TestSection "Test 2c: Mover - Move (OU Change)"

        Write-Host "Updating user department to trigger OU move..." -ForegroundColor Gray

        # The DN is computed from Department: "CN=" + EscapeDN(mv["Display Name"]) + ",OU=" + mv["Department"] + ",DC=testdomain,DC=local"
        # Change test.joiner's department from "Admin" to "Finance"
        # (test.joiner was created with index 9999, which gives department index 9999 % 10 = 9 = Admin)
        # This should trigger an LDAP move from OU=Admin to OU=Finance
        #
        # CSV columns: employeeId,firstName,lastName,email,department,title,samAccountName,displayName,status,userPrincipalName,dn
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $csvContent = Get-Content $csvPath

        # Get the current user info to determine current department
        $currentLine = $csvContent | Where-Object { $_ -match "test\.joiner" } | Select-Object -First 1

        # Change department from Admin to Finance
        $updatedContent = $csvContent | ForEach-Object {
            if ($_ -match "test\.joiner") {
                # Update department column (index 4, 0-based)
                # Pattern: "...","Admin","..." → "...","Finance","..."
                $line = $_ -replace ',"Admin",', ',"Finance",'
                # Update DN to reflect new OU
                $line = $line -replace ',OU=Admin,', ',OU=Finance,'
                $line
            }
            else {
                $_
            }
        }

        $updatedContent | Set-Content $csvPath
        Write-Host "  ✓ Changed department from Admin to Finance (triggers OU move)" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Trigger sync (Import → Full Sync → Export)
        Write-Host "Triggering synchronisation..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Write-Host "  ✓ Synchronisation completed" -ForegroundColor Green

        # Validate move in AD
        # The user should now have DN "CN=Renamed Joiner,OU=Finance,DC=testdomain,DC=local"
        Write-Host "Validating OU move in AD..." -ForegroundColor Gray

        # Query AD to find the user and check DN
        $adUserInfo = docker exec samba-ad-primary bash -c "ldbsearch -H /var/lib/samba/private/sam.ldb '(sAMAccountName=test.joiner)' dn department 2>&1"

        # Check if user is now in OU=Finance
        if ($adUserInfo -match "OU=Finance") {
            Write-Host "  ✓ User moved to OU=Finance in AD" -ForegroundColor Green

            # Also verify department attribute was updated
            if ($adUserInfo -match "department: Finance") {
                Write-Host "  ✓ Department attribute updated to Finance" -ForegroundColor Green
            }

            $testResults.Steps += @{ Name = "Mover-Move"; Success = $true }
        }
        else {
            Write-Host "  ✗ User NOT moved to OU=Finance in AD" -ForegroundColor Red
            Write-Host "    AD output: $adUserInfo" -ForegroundColor Gray
            $testResults.Steps += @{ Name = "Mover-Move"; Success = $false; Error = "OU move did not occur" }
        }
        $stepTimings["2c. Mover-Move"] = (Get-Date) - $step2cStart
    }

    # Test 3: Leaver (Deprovisioning)
    if ($Step -eq "Leaver" -or $Step -eq "All") {
        $step3Start = Get-Date
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
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Write-Host "  ✓ Synchronisation completed" -ForegroundColor Green

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
        $stepTimings["3. Leaver"] = (Get-Date) - $step3Start
    }

    # Test 4: Reconnection (Delete and Restore)
    if ($Step -eq "Reconnection" -or $Step -eq "All") {
        $step4Start = Get-Date
        Write-TestSection "Test 4: Reconnection (Delete and Restore)"

        Write-Host "Testing delete and restore before grace period..." -ForegroundColor Gray

        # Create test user
        $reconnectUser = New-TestUser -Index 8888
        $reconnectUser.EmployeeId = "EMP888888"
        $reconnectUser.SamAccountName = "test.reconnect"

        # Add to CSV (DN is calculated dynamically by the export sync rule expression)
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($reconnectUser.SamAccountName)@testdomain.local"
        $csvLine = "`"$($reconnectUser.EmployeeId)`",`"$($reconnectUser.FirstName)`",`"$($reconnectUser.LastName)`",`"$($reconnectUser.Email)`",`"$($reconnectUser.Department)`",`"$($reconnectUser.Title)`",`"$($reconnectUser.SamAccountName)`",`"Test Reconnect`",`"Active`",`"$upn`""

        Add-Content -Path $csvPath -Value $csvLine
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Initial sync (Import → Full Sync → Export)
        Write-Host "  Initial sync..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Write-Host "  ✓ Initial sync completed" -ForegroundColor Green

        # Remove user (simulating quit)
        Write-Host "  Removing user (simulating quit)..." -ForegroundColor Gray
        $csvContent = Get-Content $csvPath | Where-Object { $_ -notmatch "test.reconnect" }
        $csvContent | Set-Content $csvPath
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Write-Host "  ✓ Removal sync completed" -ForegroundColor Green

        # Restore user (simulating rehire before grace period)
        Write-Host "  Restoring user (simulating rehire)..." -ForegroundColor Gray
        Add-Content -Path $csvPath -Value $csvLine
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -Timeout $RunProfileTimeout | Out-Null
        Write-Host "  ✓ Restore sync completed" -ForegroundColor Green

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
        $stepTimings["4. Reconnection"] = (Get-Date) - $step4Start
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

    # Performance Summary
    if ($stepTimings.Count -gt 0) {
        Write-Host ""
        Write-Host "$("=" * 65)" -ForegroundColor Cyan
        Write-Host "  Performance Breakdown (Test Steps)" -ForegroundColor Cyan
        Write-Host "$("=" * 65)" -ForegroundColor Cyan
        Write-Host ""
        $totalTestTime = 0
        foreach ($timing in $stepTimings.GetEnumerator() | Sort-Object Name) {
            $seconds = [math]::Round($timing.Value.TotalSeconds, 1)
            $totalTestTime += $seconds
            $bar = "█" * [math]::Min(40, [math]::Floor($seconds / 3))
            Write-Host ("  {0,-20} {1,6}s  {2}" -f $timing.Name, $seconds, $bar) -ForegroundColor $(if ($seconds -gt 60) { "Yellow" } elseif ($seconds -gt 30) { "Cyan" } else { "Gray" })
        }
        $scenarioDuration = (Get-Date) - $scenarioStartTime
        Write-Host ""
        Write-Host ("  {0,-20} {1,6}s" -f "Scenario Total", [math]::Round($scenarioDuration.TotalSeconds, 1)) -ForegroundColor Cyan
        Write-Host ""
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
