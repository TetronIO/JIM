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

.PARAMETER ContinueOnError
    Continue executing remaining tests even if a test fails. By default, tests stop on first failure.

.EXAMPLE
    ./Invoke-Scenario1-HRToDirectory.ps1 -Step All -Template Small -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario1-HRToDirectory.ps1 -Step Joiner -Template Micro -ApiKey $env:JIM_API_KEY

.EXAMPLE
    ./Invoke-Scenario1-HRToDirectory.ps1 -Step All -Template Large -ApiKey "jim_..." -ContinueOnError
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Joiner", "Leaver", "Mover", "Mover-Rename", "Mover-Move", "Reconnection", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 5,

    [Parameter(Mandatory=$false)]
    [switch]$ContinueOnError
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'  # Disable confirmation prompts for non-interactive execution

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

# Helper function to run the standard delta sync sequence with detailed output
# This sequence is used after CSV changes to sync them through to LDAP:
# 1. CSV Full Import - detect changes in CSV file
# 2. CSV Delta Sync - process only changed CSOs, evaluate export rules
# 3. LDAP Export - apply pending exports to AD
# 4. LDAP Delta Import - confirm the exports succeeded
# 5. LDAP Delta Sync - process confirmed imports
function Invoke-SyncSequence {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,
        [switch]$ShowProgress
    )

    $results = @{
        Success = $true
        Steps = @()
    }

    # Step 1: CSV Full Import
    if ($ShowProgress) { Write-Host "  [1/5] CSV Full Import..." -ForegroundColor DarkGray }
    $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
    $results.Steps += @{ Name = "CSV Full Import"; ActivityId = $importResult.activityId }

    # Step 2: CSV Delta Sync
    if ($ShowProgress) { Write-Host "  [2/5] CSV Delta Sync..." -ForegroundColor DarkGray }
    $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVDeltaSyncProfileId -Wait -PassThru
    $results.Steps += @{ Name = "CSV Delta Sync"; ActivityId = $syncResult.activityId }

    # Step 3: LDAP Export
    if ($ShowProgress) { Write-Host "  [3/5] LDAP Export..." -ForegroundColor DarkGray }
    $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
    $results.Steps += @{ Name = "LDAP Export"; ActivityId = $exportResult.activityId }

    # Wait for AD replication
    if ($ShowProgress) { Write-Host "  Waiting 5s for AD replication..." -ForegroundColor DarkGray }
    Start-Sleep -Seconds 5

    # Step 4: LDAP Delta Import (confirming export)
    if ($ShowProgress) { Write-Host "  [4/5] LDAP Delta Import (confirming)..." -ForegroundColor DarkGray }
    $confirmImportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPDeltaImportProfileId -Wait -PassThru
    $results.Steps += @{ Name = "LDAP Delta Import"; ActivityId = $confirmImportResult.activityId }

    # Step 5: LDAP Delta Sync
    if ($ShowProgress) { Write-Host "  [5/5] LDAP Delta Sync..." -ForegroundColor DarkGray }
    $confirmSyncResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPDeltaSyncProfileId -Wait -PassThru
    $results.Steps += @{ Name = "LDAP Delta Sync"; ActivityId = $confirmSyncResult.activityId }

    return $results
}

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

    # Establish baseline state: Import existing AD structure (OUs, users, groups)
    # This is critical so JIM knows what already exists in AD before applying business rules
    Write-Host ""
    Write-Host "Establishing baseline state from Active Directory..." -ForegroundColor Gray
    Write-Host "  Importing existing OUs, users, and groups from AD..." -ForegroundColor DarkGray
    $baselineImportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaImportProfileId -Wait -PassThru
    Write-Host "  ✓ LDAP baseline import completed (Activity: $($baselineImportResult.activityId))" -ForegroundColor Green

    # Run Delta Sync to process baseline imports and establish MVOs for existing AD objects
    Write-Host "  Processing baseline imports..." -ForegroundColor DarkGray
    $baselineSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaSyncProfileId -Wait -PassThru
    Write-Host "  ✓ LDAP baseline sync completed (Activity: $($baselineSyncResult.activityId))" -ForegroundColor Green
    Write-Host "✓ Baseline state established" -ForegroundColor Green

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
        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru

        Write-Host "  ✓ CSV import completed (Activity: $($importResult.activityId))" -ForegroundColor Green

        # Trigger Full Sync (evaluates sync rules, creates MVOs and pending exports)
        Write-Host "Triggering Full Sync..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru

        Write-Host "  ✓ Full Sync completed (Activity: $($syncResult.activityId))" -ForegroundColor Green

        # Trigger LDAP Export
        Write-Host "Triggering LDAP export..." -ForegroundColor Gray
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru

        Write-Host "  ✓ LDAP export completed (Activity: $($exportResult.activityId))" -ForegroundColor Green

        # Wait for AD replication (local AD should be fast, but needs time to process)
        Write-Host "Waiting 5 seconds for AD replication..." -ForegroundColor Gray
        Start-Sleep -Seconds 5

        # Confirming Import - import the changes we just exported to LDAP
        Write-Host "Triggering LDAP delta import (confirming export)..." -ForegroundColor Gray
        $confirmImportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaImportProfileId -Wait -PassThru

        Write-Host "  ✓ LDAP delta import completed (Activity: $($confirmImportResult.activityId))" -ForegroundColor Green

        # Delta Sync - synchronise the confirmed imports
        Write-Host "Triggering LDAP delta sync..." -ForegroundColor Gray
        $confirmSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaSyncProfileId -Wait -PassThru

        Write-Host "  ✓ LDAP delta sync completed (Activity: $($confirmSyncResult.activityId))" -ForegroundColor Green

        # Validate user exists in AD
        Write-Host "Validating user in Samba AD..." -ForegroundColor Gray

        docker exec samba-ad-primary samba-tool user show $testUser.SamAccountName 2>&1 | Out-Null

        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ User 'test.joiner' provisioned to AD" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Joiner"; Success = $true }
        }
        else {
            Write-Host "  ✗ User 'test.joiner' NOT found in AD" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Joiner"; Success = $false; Error = "User not found in AD" }
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
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
                # Pattern: ,"Department","OldTitle","test.joiner" → ,"Department","Senior Developer","test.joiner"
                $_ -replace ',"[^"]+","test\.joiner"', ',"Senior Developer","test.joiner"'
            }
            else {
                $_
            }
        }

        $updatedContent | Set-Content $csvPath
        Write-Host "  ✓ Changed test.joiner title to 'Senior Developer'" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Trigger sync sequence with progress output
        Write-Host "Triggering sync sequence:" -ForegroundColor Gray
        Invoke-SyncSequence -Config $config -ShowProgress | Out-Null
        Write-Host "  ✓ Sync sequence completed" -ForegroundColor Green

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
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
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

        # Trigger sync sequence with progress output
        Write-Host "Triggering sync sequence:" -ForegroundColor Gray
        Invoke-SyncSequence -Config $config -ShowProgress | Out-Null
        Write-Host "  ✓ Sync sequence completed" -ForegroundColor Green

        # Validate rename in AD
        # The user should now have DN "CN=Renamed Joiner,CN=Users,DC=testdomain,DC=local"
        Write-Host "Validating rename in AD..." -ForegroundColor Gray

        # Try to find the user with the new name
        $adUserInfo = docker exec samba-ad-primary bash -c "ldbsearch -H /usr/local/samba/private/sam.ldb '(sAMAccountName=test.joiner)' dn displayName 2>&1"

        if ($adUserInfo -match "CN=Renamed Joiner") {
            Write-Host "  ✓ User renamed to 'CN=Renamed Joiner' in AD" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Mover-Rename"; Success = $true }
        }
        else {
            Write-Host "  ✗ User NOT renamed in AD" -ForegroundColor Red
            Write-Host "    AD output: $adUserInfo" -ForegroundColor Gray
            $testResults.Steps += @{ Name = "Mover-Rename"; Success = $false; Error = "DN not renamed" }
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
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

        # Debug: Show current line before change
        Write-Host "  Current CSV line: $currentLine" -ForegroundColor DarkGray

        # Change department from Sales to Finance
        # Note: Test user at index 9999 is assigned to Sales department (9999 % 12 = 3)
        # CSV columns: employeeId,firstName,lastName,email,department,title,samAccountName,displayName,status,userPrincipalName
        # We need to update the department field (5th field, index 4)
        $updatedContent = $csvContent | ForEach-Object {
            if ($_ -match "test\.joiner") {
                # Parse CSV line and update department field directly
                # This is more robust than regex replacement
                $fields = $_ -split '","'
                if ($fields.Count -ge 5) {
                    # Field indices (after split on ","):
                    # 0: "employeeId  (has leading quote)
                    # 1: firstName
                    # 2: lastName
                    # 3: email
                    # 4: department  <-- this is what we want to change
                    # 5: title
                    # etc.
                    $oldDept = $fields[4]
                    $fields[4] = "Finance"
                    $newLine = $fields -join '","'
                    Write-Host "  Changed department from '$oldDept' to 'Finance'" -ForegroundColor DarkGray
                    $newLine
                }
                else {
                    Write-Host "  Warning: Could not parse CSV line for test.joiner" -ForegroundColor Yellow
                    $_
                }
            }
            else {
                $_
            }
        }

        $updatedContent | Set-Content $csvPath
        Write-Host "  ✓ Changed department from Sales to Finance (triggers OU move)" -ForegroundColor Green

        # Copy updated CSV
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Trigger sync sequence with progress output
        Write-Host "Triggering sync sequence:" -ForegroundColor Gray
        Invoke-SyncSequence -Config $config -ShowProgress | Out-Null
        Write-Host "  ✓ Sync sequence completed" -ForegroundColor Green

        # Validate move in AD
        # The user should now be in OU=Finance (moved from OU=Sales)
        Write-Host "Validating OU move in AD..." -ForegroundColor Gray

        # Query AD to find the user and check DN
        $adUserInfo = docker exec samba-ad-primary bash -c "ldbsearch -H /usr/local/samba/private/sam.ldb '(sAMAccountName=test.joiner)' dn department 2>&1"

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
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
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

        # Trigger sync sequence with progress output
        Write-Host "Triggering sync sequence:" -ForegroundColor Gray
        Invoke-SyncSequence -Config $config -ShowProgress | Out-Null
        Write-Host "  ✓ Sync sequence completed" -ForegroundColor Green

        # Validate user state in AD
        # With a 7-day grace period configured, the MVO won't be deleted immediately,
        # so the user should still exist in AD but the CSO should be disconnected
        Write-Host "Validating leaver state in AD..." -ForegroundColor Gray

        $adUserCheck = docker exec samba-ad-primary samba-tool user show $userToRemove 2>&1

        if ($LASTEXITCODE -eq 0) {
            # User still exists in AD - expected with grace period
            Write-Host "  ✓ User $userToRemove still exists in AD (within grace period)" -ForegroundColor Green
            Write-Host "    Note: User will be deleted after 7-day grace period expires" -ForegroundColor DarkGray
            $testResults.Steps += @{ Name = "Leaver"; Success = $true }
        }
        elseif ($adUserCheck -match "Unable to find user") {
            # User was deleted - unexpected with grace period, but not a failure
            Write-Host "  ✓ User $userToRemove deleted from AD" -ForegroundColor Green
            $testResults.Steps += @{ Name = "Leaver"; Success = $true }
        }
        else {
            Write-Host "  ✗ Unexpected state for $userToRemove in AD" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Leaver"; Success = $false; Error = "Unexpected AD state: $adUserCheck" }
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
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
        $reconnectUser.Email = "test.reconnect@testdomain.local"
        $reconnectUser.FirstName = "Test"
        $reconnectUser.LastName = "Reconnect"
        $reconnectUser.Department = "IT"
        $reconnectUser.Title = "Developer"

        # Add to CSV (DN is calculated dynamically by the export sync rule expression)
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($reconnectUser.SamAccountName)@testdomain.local"
        $csvLine = "`"$($reconnectUser.EmployeeId)`",`"$($reconnectUser.FirstName)`",`"$($reconnectUser.LastName)`",`"$($reconnectUser.Email)`",`"$($reconnectUser.Department)`",`"$($reconnectUser.Title)`",`"$($reconnectUser.SamAccountName)`",`"Test Reconnect`",`"Active`",`"$upn`""

        Add-Content -Path $csvPath -Value $csvLine
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Initial sync - uses Delta Sync for efficiency (baseline already established)
        Write-Host "  Initial sync (provisioning new user):" -ForegroundColor Gray
        Invoke-SyncSequence -Config $config -ShowProgress | Out-Null
        Write-Host "  ✓ Initial sync completed" -ForegroundColor Green

        # Verify user was created in AD
        docker exec samba-ad-primary samba-tool user show test.reconnect 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ✗ User was not created in AD during initial sync" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Reconnection"; Success = $false; Error = "User not provisioned during initial sync" }
            if (-not $ContinueOnError) {
                Write-Host ""
                Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                exit 1
            }
            $stepTimings["4. Reconnection"] = (Get-Date) - $step4Start
        }
        else {
            Write-Host "  ✓ User exists in AD after initial sync" -ForegroundColor Green

            # Remove user (simulating quit)
            Write-Host "  Removing user (simulating quit)..." -ForegroundColor Gray
            $csvContent = Get-Content $csvPath | Where-Object { $_ -notmatch "test.reconnect" }
            $csvContent | Set-Content $csvPath
            docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

            # Only need CSV import/sync for removal - no LDAP export needed
            Write-Host "    [1/2] CSV Full Import..." -ForegroundColor DarkGray
            Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait | Out-Null
            Write-Host "    [2/2] CSV Delta Sync (marks CSO obsolete)..." -ForegroundColor DarkGray
            Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait | Out-Null
            Write-Host "  ✓ Removal sync completed" -ForegroundColor Green

            # Verify user still exists in AD (grace period should prevent deletion)
            docker exec samba-ad-primary samba-tool user show test.reconnect 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✓ User still in AD after removal (grace period active)" -ForegroundColor Green
            }
            else {
                Write-Host "  ⚠ User missing from AD after removal sync" -ForegroundColor Yellow
            }

            # Restore user (simulating rehire before grace period)
            Write-Host "  Restoring user (simulating rehire)..." -ForegroundColor Gray
            Add-Content -Path $csvPath -Value $csvLine
            docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

            Invoke-SyncSequence -Config $config -ShowProgress | Out-Null
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
                if (-not $ContinueOnError) {
                    Write-Host ""
                    Write-Host "Test failed. Stopping execution. Use -ContinueOnError to continue despite failures." -ForegroundColor Red
                    exit 1
                }
            }
            $stepTimings["4. Reconnection"] = (Get-Date) - $step4Start
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

    # Performance Summary
    if ($stepTimings.Count -gt 0) {
        Write-Host ""
        Write-Host "$("=" * 65)" -ForegroundColor Cyan
        Write-Host "  Performance Breakdown (Test Steps)" -ForegroundColor Cyan
        Write-Host "$("=" * 65)" -ForegroundColor Cyan
        Write-Host ""
        $totalTestTime = 0
        $maxSeconds = ($stepTimings.Values | Measure-Object -Property TotalSeconds -Maximum).Maximum
        foreach ($timing in $stepTimings.GetEnumerator() | Sort-Object Name) {
            $seconds = $timing.Value.TotalSeconds
            $totalTestTime += $seconds

            # Format time appropriately based on magnitude
            $timeDisplay = if ($seconds -lt 1) {
                "{0,6}ms" -f [math]::Round($seconds * 1000)
            } elseif ($seconds -lt 60) {
                "{0,6}s" -f [math]::Round($seconds, 1)
            } elseif ($seconds -lt 3600) {
                $mins = [math]::Floor($seconds / 60)
                $secs = [math]::Round($seconds % 60)
                "{0}m {1}s" -f $mins, $secs
            } else {
                $hours = [math]::Floor($seconds / 3600)
                $mins = [math]::Floor(($seconds % 3600) / 60)
                "{0}h {1}m" -f $hours, $mins
            }

            # Scale bar relative to max time, with reasonable max width
            $barWidth = if ($maxSeconds -gt 0) {
                [math]::Min(50, [math]::Floor(($seconds / $maxSeconds) * 50))
            } else { 0 }
            $bar = "█" * $barWidth

            Write-Host ("  {0,-20} {1,8}  {2}" -f $timing.Name, $timeDisplay, $bar) -ForegroundColor $(if ($seconds -gt 60) { "Yellow" } elseif ($seconds -gt 30) { "Cyan" } else { "Gray" })
        }
        $scenarioDuration = (Get-Date) - $scenarioStartTime
        $totalSeconds = $scenarioDuration.TotalSeconds
        $totalDisplay = if ($totalSeconds -lt 60) {
            "{0}s" -f [math]::Round($totalSeconds, 1)
        } elseif ($totalSeconds -lt 3600) {
            $mins = [math]::Floor($totalSeconds / 60)
            $secs = [math]::Round($totalSeconds % 60)
            "{0}m {1}s" -f $mins, $secs
        } else {
            $hours = [math]::Floor($totalSeconds / 3600)
            $mins = [math]::Floor(($totalSeconds % 3600) / 60)
            "{0}h {1}m" -f $hours, $mins
        }
        Write-Host ""
        Write-Host ("  {0,-20} {1}" -f "Scenario Total", $totalDisplay) -ForegroundColor Cyan
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
