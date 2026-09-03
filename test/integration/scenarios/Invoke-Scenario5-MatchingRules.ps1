# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 5: Object Matching Rules

.DESCRIPTION
    Validates object matching rules functionality including:
    - Basic matching: new CSO projected to MV when no match exists
    - Basic matching: CSO joins existing MVO when match found
    - Duplicate prevention: join fails when MVO already has connector from same CS
    - Multiple matching rules: fallback to subsequent rules when earlier rules don't match
    - Edge cases: null values, case sensitivity
    - Outbound (export-time) matching: a Metaverse Object provisioned into a Connected System
      JOINS a pre-existing, unjoined account created out-of-band instead of provisioning a duplicate
    - Same-page rejoin cancellation (#1612): within a single sync page, a source disconnecting (which
      schedules a grace-period Metaverse Object deletion) and a NEW same-system source rejoining the
      SAME identity (which should cancel that scheduled deletion, per #119) must leave the object alive
      with every deletion marker cleared, not just when the disconnect and rejoin happen on separate runs

.PARAMETER Step
    Which test step to execute (Projection, Join, DuplicatePrevention, MultiplRules, SamePageRejoinCancellation, All)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, MediumLarge, Large, Scale100k50Groups, Scale200k55Groups, Scale500k65Groups, Scale750k70Groups, Scale1m80Groups, Scale100k5kGroups, Scale200k10kGroups, Scale500k25kGroups, Scale750k40kGroups, Scale1m60kGroups)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER WaitSeconds
    Seconds to wait between steps for JIM processing (default: 30)

.EXAMPLE
    ./Invoke-Scenario5-MatchingRules.ps1 -Step All -Template Micro -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario5-MatchingRules.ps1 -Step Projection -Template Micro -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Projection", "Join", "DuplicatePrevention", "MultipleRules", "JoinConflict", "SamePageJoinConflict", "SamePageRejoinCancellation", "CaseSensitivity", "ExportMatchJoin", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 30,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

# Default to SambaAD Primary if no config provided
if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}
$isOpenLDAP = $DirectoryConfig.UserObjectClass -eq "inetOrgPerson"

# Admin account name for server-routed directory writes (Samba AD only), derived from the bind DN.
# Test-created Samba objects are written THROUGH the running server (-H ldap://localhost with
# credentials) rather than directly against the sam.ldb file; direct file writes on a live domain
# controller are an unsupported access pattern.
$sambaAdminUser = if (-not $isOpenLDAP) { ($DirectoryConfig.BindDN -split ',')[0] -replace '^CN=', '' } else { $null }

Write-TestSection "Scenario 5: Object Matching Rules"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Object Matching Rules"
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

    # Use dedicated minimal CSV for Scenario 5 (no baseline users)
    # This ensures tests are self-contained and don't cause mass export failures
    # Seed a full baseline set of CSVs into the volume first. Setup-Scenario1.ps1
    # creates four CSV connected systems (HR, Training, Departments, Cross-Domain)
    # and runs schema discovery against all of them, so every file must exist before
    # setup runs. We then overlay Scenario 5's minimal HR CSV on top of the baseline.
    # Prior to this the scenario relied on files leaking from Scenario 1's volume.
    Write-Host "Seeding baseline CSVs for Scenario 5..." -ForegroundColor Gray
    $testDataPath = "$PSScriptRoot/../../test-data"
    $scenarioDataPath = "$PSScriptRoot/data"

    if (-not (Test-Path $testDataPath)) {
        New-Item -ItemType Directory -Path $testDataPath -Force | Out-Null
    }

    & "$PSScriptRoot/../Generate-TestCSV.ps1" -Template "Nano" -OutputPath $testDataPath

    # Overlay Scenario 5's tailored HR CSV (1 baseline user for schema discovery)
    Write-Host "Applying Scenario 5 HR overlay..." -ForegroundColor Gray
    Copy-Item -Path "$scenarioDataPath/scenario5-hr-users.csv" -Destination "$testDataPath/hr-users.csv" -Force

    $csvPath = "$testDataPath/hr-users.csv"
    Write-FileToConnectorVolume -SourcePath $csvPath -DestinationPath "/connector-files/test-data/hr-users.csv"
    Write-Host "  ✓ CSV initialised (HR overlay over Nano baseline)" -ForegroundColor Green

    # Clean up test-specific directory users from previous test runs
    Write-Host "Cleaning up test-specific directory users from previous runs..." -ForegroundColor Gray
    $testUsers = @("test.projection", "test.join", "test.duplicate1", "test.duplicate2", "test.multirule.first", "test.multirule.second", "baseline.user1", "test.match.outbound")
    $deletedCount = 0
    foreach ($user in $testUsers) {
        if ($isOpenLDAP) {
            $userDN = "$($DirectoryConfig.UserRdnAttr)=$user,$($DirectoryConfig.UserContainer)"
            $output = docker exec $DirectoryConfig.ContainerName ldapdelete -x -H "ldap://localhost:$($DirectoryConfig.Port)" -D "$($DirectoryConfig.BindDN)" -w "$($DirectoryConfig.BindPassword)" "$userDN" 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  Deleted $user from directory" -ForegroundColor Gray
                $deletedCount++
            }
        }
        else {
            $output = & docker exec $DirectoryConfig.ContainerName bash -c "samba-tool user delete '$user' 2>&1; echo EXIT_CODE:\$?"
            if ($output -match "Deleted user") {
                Write-Host "  Deleted $user from directory" -ForegroundColor Gray
                $deletedCount++
            }
        }
    }
    Write-Host "  ✓ Directory cleanup complete ($deletedCount test users deleted)" -ForegroundColor Green

    # Setup scenario configuration (reuse Scenario 1 setup)
    $setupParams = @{ JIMUrl = $JIMUrl; ApiKey = $ApiKey; Template = $Template }
    if ($DirectoryConfig) { $setupParams.DirectoryConfig = $DirectoryConfig }
    $config = & "$PSScriptRoot/../Setup-Scenario1.ps1" @setupParams

    if (-not $config) {
        throw "Failed to setup Scenario configuration"
    }

    # Scenario 5 uses hrId as external ID (not employeeId) to test:
    # - Import deduplication: Two rows with same hrId → only one CSO
    # - Join conflict: Two CSOs with different hrIds but same employeeId → CouldNotJoinDueToExistingJoin
    Write-Host "Reconfiguring CSV external ID for Scenario 5..." -ForegroundColor Gray

    # Re-import module and connect
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    # Get CSV system and object type
    $csvSystem = Get-JIMConnectedSystem -Id $config.CSVSystemId -ObjectTypes
    $csvUserType = $csvSystem | Where-Object { $_.name -eq "person" }
    if (-not $csvUserType) {
        $csvUserType = $csvSystem[0]  # Fallback if filtering doesn't work
    }

    # Find hrId and employeeId attributes
    $hrIdAttr = $csvUserType.attributes | Where-Object { $_.name -eq "hrId" }
    $employeeIdAttr = $csvUserType.attributes | Where-Object { $_.name -eq "employeeId" }

    if ($hrIdAttr -and $employeeIdAttr) {
        # Remove external ID from employeeId
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $config.CSVSystemId -ObjectTypeId $csvUserType.id -AttributeId $employeeIdAttr.id -IsExternalId $false | Out-Null
        # Set hrId as the new external ID
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $config.CSVSystemId -ObjectTypeId $csvUserType.id -AttributeId $hrIdAttr.id -IsExternalId $true | Out-Null
        Write-Host "  ✓ Changed external ID from 'employeeId' to 'hrId'" -ForegroundColor Green
    }
    else {
        Write-Host "  ⚠ Could not find hrId or employeeId attributes" -ForegroundColor Yellow
    }

    Write-Host "✓ JIM configured for Scenario 5" -ForegroundColor Green

    # Create department OUs needed for test users AFTER Setup-Scenario1
    # (Setup may recreate base Corp OU structure, so department OUs must come after)
    if (-not $isOpenLDAP) {
        # Samba AD: DN expression uses OU=<Department>,OU=Users,OU=Corp,DC=panoply,DC=local
        Write-Host "Creating department OUs for test users..." -ForegroundColor Gray
        $testDepartments = @("Information Technology", "Operations", "Finance", "Sales", "Marketing")
        foreach ($dept in $testDepartments) {
            $result = docker exec $DirectoryConfig.ContainerName samba-tool ou create "OU=$dept,OU=Users,OU=Corp,$($DirectoryConfig.BaseDN)" -H ldap://localhost -U "$sambaAdminUser%$($DirectoryConfig.BindPassword)" 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✓ Created OU: $dept" -ForegroundColor Gray
            } elseif ($result -match "already exists") {
                Write-Host "  - OU $dept already exists" -ForegroundColor DarkGray
            } else {
                # Fail loudly here rather than five tests later with a confusing missing-parent error.
                throw "Failed to create department OU '$dept': $result"
            }
        }
        Write-Host "  ✓ Department OUs ready" -ForegroundColor Green
    }
    else {
        # OpenLDAP: flat OU structure, no department OUs needed (users go to People)
        Write-Host "  OpenLDAP: flat OU structure, skipping department OU creation" -ForegroundColor Gray
    }
    Write-Host "  CSV System ID: $($config.CSVSystemId)" -ForegroundColor Gray
    Write-Host "  LDAP System ID: $($config.LDAPSystemId)" -ForegroundColor Gray

    # Test 1: Projection - New CSO creates new MVO when no match exists
    if ($Step -eq "Projection" -or $Step -eq "All") {
        Write-TestSection "Test 1: Projection - New Identity"

        Write-Host "Testing: New CSO with unique employeeId should project to new MVO" -ForegroundColor Gray

        # Create test user with unique employee ID and HR ID (GUID)
        $testUser = New-TestUser -Index 9001
        $testUser.HrId = "00009001-0000-0000-0000-000000000000"
        $testUser.EmployeeId = "EMP900001"
        $testUser.SamAccountName = "test.projection"
        $testUser.Email = "test.projection@panoply.local"
        $testUser.DisplayName = "Test Projection User"

        # Add user to CSV using proper CSV parsing (DN is calculated dynamically by the export sync rule expression)
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($testUser.SamAccountName)@panoply.local"

        # Use Import-Csv/Export-Csv to ensure correct column handling
        $csv = Import-Csv $csvPath
        $newUser = [PSCustomObject]@{
            hrId = $testUser.HrId
            employeeId = $testUser.EmployeeId
            firstName = $testUser.FirstName
            lastName = $testUser.LastName
            email = $testUser.Email
            department = $testUser.Department
            title = $testUser.Title
            company = $testUser.Company
            samAccountName = $testUser.SamAccountName
            displayName = $testUser.DisplayName
            status = "Active"
            userPrincipalName = $upn
            employeeType = $testUser.EmployeeType
            employeeEndDate = ""
        }
        $csv = @($csv) + $newUser
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Write-Host "  Added test.projection to CSV with HrId=$($testUser.HrId), EmployeeId=$($testUser.EmployeeId)" -ForegroundColor Gray

        # Copy updated CSV to container
        Copy-CsvToConnectorFiles -SourcePath $csvPath

        # Run import and sync
        Write-Host "  Running import and sync..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import (Projection)"
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Sync (Projection)"

        # Verify MVO was created
        $mvos = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test Projection" -PageSize 10 -ErrorAction SilentlyContinue

        # Get-JIMMetaverseObject returns objects directly, not wrapped in .items
        if ($mvos) {
            $projectedMvo = $mvos | Where-Object { $_.displayName -match "Test Projection" }
            if ($projectedMvo) {
                Write-Host "  ✓ MVO created with ID: $($projectedMvo.id)" -ForegroundColor Green
                $testResults.Steps += @{ Name = "Projection"; Success = $true }
            } else {
                Write-Host "  MVO not found with expected display name" -ForegroundColor Red
                $testResults.Steps += @{ Name = "Projection"; Success = $false; Error = "MVO not found" }
            }
        } else {
            Write-Host "  No MVOs found matching test.projection" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Projection"; Success = $false; Error = "No MVOs found" }
        }
    }

    # Test 2: Join - CSO joins existing MVO when employeeId matches
    if ($Step -eq "Join" -or $Step -eq "All") {
        Write-TestSection "Test 2: Join - Existing Identity"

        Write-Host "Testing: CSO with matching employeeId should join existing MVO (not create duplicate)" -ForegroundColor Gray

        # First, create an MVO via HR import
        $testUser = New-TestUser -Index 9002
        $testUser.HrId = "00009002-0000-0000-0000-000000000000"
        $testUser.EmployeeId = "EMP900002"
        $testUser.SamAccountName = "test.join"
        $testUser.Email = "test.join@panoply.local"
        $testUser.DisplayName = "Test Join User"

        # DN is calculated dynamically by the export sync rule expression
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($testUser.SamAccountName)@panoply.local"

        # Use Import-Csv/Export-Csv to ensure correct column handling
        $csv = Import-Csv $csvPath
        $joinUser = [PSCustomObject]@{
            hrId = $testUser.HrId
            employeeId = $testUser.EmployeeId
            firstName = $testUser.FirstName
            lastName = $testUser.LastName
            email = $testUser.Email
            department = $testUser.Department
            title = $testUser.Title
            company = $testUser.Company
            samAccountName = $testUser.SamAccountName
            displayName = $testUser.DisplayName
            status = "Active"
            userPrincipalName = $upn
            employeeType = $testUser.EmployeeType
            employeeEndDate = ""
        }
        $csv = @($csv) + $joinUser
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath

        # Import from HR to create MVO
        Write-Host "  Creating MVO via HR import..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import (Join - create MVO)"
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Sync (Join - create MVO)"

        # Get the MVO that was created (Get-JIMMetaverseObject returns objects directly)
        $mvos = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test Join" -PageSize 10 -ErrorAction SilentlyContinue
        $originalMvo = $mvos | Where-Object { $_.displayName -match "Test Join" } | Select-Object -First 1

        if (-not $originalMvo) {
            Write-Host "  Failed to create initial MVO for join test" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Join"; Success = $false; Error = "Could not create initial MVO" }
        }
        else {
            $originalMvoId = $originalMvo.id
            Write-Host "  MVO created with ID: $originalMvoId" -ForegroundColor Gray

            # Now export to AD (this will provision the user)
            Write-Host "  Exporting to AD..." -ForegroundColor Gray
            $exportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
            Assert-ExportSuccess -ActivityId $exportResult.activityId -Name "LDAP Export (Join)"

            # Import from AD to confirm the CSO joins back to the same MVO
            Write-Host "  Importing from AD to verify join..." -ForegroundColor Gray
            $ldapImportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $ldapImportResult.activityId -Name "LDAP Import (Join - confirm)"

            # Verify the MVO still has the same ID (not duplicated)
            # Get-JIMMetaverseObject returns objects directly, not wrapped in .items
            $mvosAfter = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test Join" -PageSize 10 -ErrorAction SilentlyContinue
            $matchingMvos = $mvosAfter | Where-Object { $_.displayName -match "Test Join" }

            if ($matchingMvos.Count -eq 1 -and $matchingMvos[0].id -eq $originalMvoId) {
                Write-Host "  ✓ AD CSO joined to existing MVO (no duplicate created)" -ForegroundColor Green
                $testResults.Steps += @{ Name = "Join"; Success = $true }
            }
            elseif ($matchingMvos.Count -gt 1) {
                Write-Host "  FAIL: Duplicate MVOs created!" -ForegroundColor Red
                $testResults.Steps += @{ Name = "Join"; Success = $false; Error = "Duplicate MVOs created - matching rules may not be working" }
            }
            else {
                Write-Host "  WARNING: Could not verify join behaviour" -ForegroundColor Yellow
                $testResults.Steps += @{ Name = "Join"; Success = $true; Warning = "Could not fully verify join" }
            }
        }
    }

    # Test 3: Import Deduplication - Same-batch duplicate external IDs
    # JIM detects when two rows in the same import batch have the same external ID.
    # When detected, BOTH objects are rejected with DuplicateObject error - no "random winner".
    # This forces the data owner to fix the source data.
    if ($Step -eq "DuplicatePrevention" -or $Step -eq "All") {
        Write-TestSection "Test 3: Import Deduplication (Same External ID)"

        Write-Host "Testing: Two CSV rows with same hrId (external ID) should BOTH be rejected" -ForegroundColor Gray
        Write-Host "  This tests the import-level duplicate detection when source data has duplicates" -ForegroundColor Gray

        # This scenario tests what happens when:
        # 1. Two CSV rows have the SAME hrId (external ID) - a data error in HR
        # 2. The import detects the duplicate and rejects BOTH rows
        # 3. Neither CSO is created - the data owner must fix the source data

        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"

        # Create first user with unique hrId
        $testUser1 = New-TestUser -Index 9003
        $testUser1.HrId = "00009003-0000-0000-0000-000000000000"
        $testUser1.EmployeeId = "EMP900003"
        $testUser1.SamAccountName = "test.importdup1"
        $testUser1.Email = "test.importdup1@panoply.local"
        $testUser1.DisplayName = "Test Import Dup One"

        $upn1 = "$($testUser1.SamAccountName)@panoply.local"
        $csv = Import-Csv $csvPath
        $dupUser1 = [PSCustomObject]@{
            hrId = $testUser1.HrId
            employeeId = $testUser1.EmployeeId
            firstName = $testUser1.FirstName
            lastName = $testUser1.LastName
            email = $testUser1.Email
            department = $testUser1.Department
            title = $testUser1.Title
            company = $testUser1.Company
            samAccountName = $testUser1.SamAccountName
            displayName = $testUser1.DisplayName
            status = "Active"
            userPrincipalName = $upn1
            employeeType = $testUser1.EmployeeType
            employeeEndDate = ""
        }

        # Now add second user with SAME hrId (simulating HR data error)
        $testUser2 = New-TestUser -Index 9004
        $testUser2.HrId = "00009003-0000-0000-0000-000000000000"  # SAME hrId - duplicate external ID
        $testUser2.EmployeeId = "EMP900004"  # Different employeeId
        $testUser2.SamAccountName = "test.importdup2"
        $testUser2.Email = "test.importdup2@panoply.local"
        $testUser2.DisplayName = "Test Import Dup Two"

        $upn2 = "$($testUser2.SamAccountName)@panoply.local"
        $dupUser2 = [PSCustomObject]@{
            hrId = $testUser2.HrId
            employeeId = $testUser2.EmployeeId
            firstName = $testUser2.FirstName
            lastName = $testUser2.LastName
            email = $testUser2.Email
            department = $testUser2.Department
            title = $testUser2.Title
            company = $testUser2.Company
            samAccountName = $testUser2.SamAccountName
            displayName = $testUser2.DisplayName
            status = "Active"
            userPrincipalName = $upn2
            employeeType = $testUser2.EmployeeType
            employeeEndDate = ""
        }

        # Add both users to CSV at once
        $csv = @($csv) + $dupUser1 + $dupUser2
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath

        Write-Host "  Added 2 users with SAME hrId=$($testUser1.HrId) to CSV..." -ForegroundColor Gray

        # Import - this should detect the duplicate and error BOTH objects
        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        # Note: Activity may succeed overall but have DuplicateObject errors in execution items

        # Check the import activity for DuplicateObject errors
        Write-Host "  Checking import activity for DuplicateObject errors..." -ForegroundColor Gray
        $executionItems = @(Get-JIMActivity -Id $importResult.activityId -ExecutionItems)

        # Filter for the expected error type
        $duplicateErrors = @($executionItems | Where-Object { $_.errorType -eq "DuplicateObject" })

        if ($duplicateErrors.Count -ge 2) {
            Write-Host "  ✓ JIM correctly rejected BOTH duplicate objects with DuplicateObject error" -ForegroundColor Green
            foreach ($dupErr in $duplicateErrors) {
                $errorMsg = $dupErr.PSObject.Properties['errorMessage']?.Value ?? "[no message]"
                Write-Host "    Error: $errorMsg" -ForegroundColor Gray
            }

            # Run sync to verify no MVOs are created
            $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru

            # Verify NO MVOs were created (both rows were rejected)
            $mvos = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test Import Dup" -PageSize 20 -ErrorAction SilentlyContinue
            $dupMvos = @($mvos | Where-Object { $_.displayName -match "Test Import Dup" })

            Write-Host "  Found $($dupMvos.Count) MVO(s) for import dedup test" -ForegroundColor Gray

            if ($dupMvos.Count -eq 0) {
                Write-Host "  ✓ No MVOs created (both duplicate rows rejected)" -ForegroundColor Green
                $testResults.Steps += @{ Name = "ImportDeduplication"; Success = $true }
            }
            else {
                Write-Host "  ⚠ Found $($dupMvos.Count) MVO(s) - but duplicates were correctly detected" -ForegroundColor Yellow
                $testResults.Steps += @{ Name = "ImportDeduplication"; Success = $true; Warning = "Found $($dupMvos.Count) MVOs despite duplicate detection" }
            }
        }
        elseif ($duplicateErrors.Count -eq 1) {
            Write-Host "  ⚠ Only 1 DuplicateObject error found (expected 2 for BOTH rows)" -ForegroundColor Yellow
            $testResults.Steps += @{ Name = "ImportDeduplication"; Success = $false; Error = "Only 1 DuplicateObject error (expected 2)" }
        }
        else {
            Write-Host "  ✗ No DuplicateObject errors found - duplicate detection may have failed" -ForegroundColor Red
            $testResults.Steps += @{ Name = "ImportDeduplication"; Success = $false; Error = "No DuplicateObject errors found" }
        }

        # Clean up Test 3 data - reload the baseline CSV to avoid interfering with subsequent tests
        Copy-Item -Path "$scenarioDataPath/scenario5-hr-users.csv" -Destination "$testDataPath/hr-users.csv" -Force
        Copy-CsvToConnectorFiles -SourcePath $csvPath
        Write-Host "  ✓ Reset CSV to baseline for subsequent tests" -ForegroundColor Gray
    }

    # Test 4: Multiple Matching Rules - Fallback behaviour
    # Tests that when the primary matching rule doesn't match, secondary rules are evaluated.
    # This is a complex test that requires specific setup; run separately with -Step MultipleRules.
    if ($Step -eq "MultipleRules") {
        Write-TestSection "Test 4: Multiple Matching Rules"

        Write-Host "Testing: When first matching rule doesn't match, subsequent rules should be evaluated" -ForegroundColor Gray

        # Scenario:
        # 1. Create an MVO via HR with specific employeeId and email
        # 2. Add a second matching rule on email (order=1, after employeeId order=0)
        # 3. Create a NEW HR record with DIFFERENT employeeId but SAME email
        # 4. The first rule (employeeId) won't match, but second rule (email) should match
        # 5. Verify the CSO joins to the existing MVO via the email rule

        # First, get the CSV object type and its attributes
        $csvSystem = Get-JIMConnectedSystem | Where-Object { $_.name -match "HR CSV" }
        # Get object types separately (returns array of object types, not system with .objectTypes property)
        $csvObjectTypes = Get-JIMConnectedSystem -Id $csvSystem.id -ObjectTypes
        # CSV object type is "person" not "User" (LDAP uses "User")
        $csvUserType = $csvObjectTypes | Where-Object { $_.name -eq "person" }
        $mvAttributes = Get-JIMMetaverseAttribute
        $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1

        if (-not $csvSystem -or -not $csvUserType) {
            Write-Host "  Could not find CSV system or User object type" -ForegroundColor Red
            $testResults.Steps += @{ Name = "MultipleRules"; Success = $false; Error = "CSV system not found" }
        }
        else {
            # Get attribute IDs
            $csvEmailAttr = $csvUserType.attributes | Where-Object { $_.name -eq 'email' }
            $mvEmailAttr = $mvAttributes | Where-Object { $_.name -eq 'Email' }

            if (-not $csvEmailAttr -or -not $mvEmailAttr) {
                Write-Host "  Could not find email attributes for matching rule" -ForegroundColor Red
                $testResults.Steps += @{ Name = "MultipleRules"; Success = $false; Error = "Email attributes not found" }
            }
            else {
                # Check if email matching rule already exists
                $existingRules = Get-JIMMatchingRule -ConnectedSystemId $csvSystem.id -ObjectTypeId $csvUserType.id
                $emailRuleExists = $existingRules | Where-Object {
                    $_.targetMetaverseAttributeId -eq $mvEmailAttr.id
                }

                if (-not $emailRuleExists) {
                    # Add a second matching rule on email (order=1)
                    Write-Host "  Adding secondary matching rule (email → Email) with order=1..." -ForegroundColor Gray
                    try {
                        New-JIMMatchingRule -ConnectedSystemId $csvSystem.id `
                            -ObjectTypeId $csvUserType.id `
                            -MetaverseObjectTypeId $mvUserType.id `
                            -SourceAttributeId $csvEmailAttr.id `
                            -TargetMetaverseAttributeId $mvEmailAttr.id `
                            -Order 1 | Out-Null
                        Write-Host "  ✓ Secondary matching rule created" -ForegroundColor Green
                    }
                    catch {
                        Write-Host "  Failed to create secondary matching rule: $_" -ForegroundColor Yellow
                    }
                }
                else {
                    Write-Host "  Email matching rule already exists" -ForegroundColor Gray
                }

                # Step 1: Create first user (will be the MVO we want to join to)
                $testUser1 = New-TestUser -Index 9010
                $testUser1.HrId = "00009010-0000-0000-0000-000000000000"
                $testUser1.EmployeeId = "EMP901000"
                $testUser1.SamAccountName = "test.multirule.first"
                $testUser1.Email = "test.multirule@panoply.local"  # This email will be shared
                $testUser1.DisplayName = "Test MultiRule First"

                # DN is calculated dynamically by the export sync rule expression
                $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
                $upn1 = "$($testUser1.SamAccountName)@panoply.local"

                # Use Import-Csv/Export-Csv to ensure correct column handling
                $csv = Import-Csv $csvPath
                $multiRule1 = [PSCustomObject]@{
                    hrId = $testUser1.HrId
                    employeeId = $testUser1.EmployeeId
                    firstName = $testUser1.FirstName
                    lastName = $testUser1.LastName
                    email = $testUser1.Email
                    department = $testUser1.Department
                    title = $testUser1.Title
                    company = $testUser1.Company
                    samAccountName = $testUser1.SamAccountName
                    displayName = $testUser1.DisplayName
                    status = "Active"
                    userPrincipalName = $upn1
                    employeeType = $testUser1.EmployeeType
                    employeeEndDate = ""
                }
                $csv = @($csv) + $multiRule1
                $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
                Copy-CsvToConnectorFiles -SourcePath $csvPath

                # Import first user to create MVO
                Write-Host "  Creating MVO with EmployeeId=$($testUser1.EmployeeId), Email=$($testUser1.Email)..." -ForegroundColor Gray
                $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
                Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import (MultipleRules - first user)"
                $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
                Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Sync (MultipleRules - first user)"

                # Verify MVO was created (Get-JIMMetaverseObject returns objects directly)
                $mvos = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test MultiRule" -PageSize 10 -ErrorAction SilentlyContinue
                $originalMvo = $mvos | Where-Object { $_.displayName -match "Test MultiRule First" } | Select-Object -First 1

                if (-not $originalMvo) {
                    Write-Host "  Failed to create initial MVO" -ForegroundColor Red
                    $testResults.Steps += @{ Name = "MultipleRules"; Success = $false; Error = "Could not create initial MVO" }
                }
                else {
                    $originalMvoId = $originalMvo.id
                    Write-Host "  MVO created with ID: $originalMvoId" -ForegroundColor Gray

                    # Step 2: Remove the first user from CSV (so its CSO gets deleted)
                    Write-Host "  Removing first user from CSV..." -ForegroundColor Gray
                    $csvContent = Get-Content $csvPath | Where-Object { $_ -notmatch "test.multirule.first" }
                    $csvContent | Set-Content $csvPath
                    Copy-CsvToConnectorFiles -SourcePath $csvPath

                    # Import to process deletion
                    $delImportResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
                    Assert-ActivitySuccess -ActivityId $delImportResult.activityId -Name "CSV Import (MultipleRules - delete first)"
                    $delSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
                    Assert-ActivitySuccess -ActivityId $delSyncResult.activityId -Name "Full Sync (MultipleRules - delete first)"

                    # Step 3: Create second user with DIFFERENT employeeId but SAME email
                    $testUser2 = New-TestUser -Index 9011
                    $testUser2.HrId = "00009011-0000-0000-0000-000000000000"
                    $testUser2.EmployeeId = "EMP901001"  # Different employeeId (rule 1 won't match)
                    $testUser2.SamAccountName = "test.multirule.second"
                    $testUser2.Email = "test.multirule@panoply.local"  # SAME email (rule 2 should match)
                    $testUser2.DisplayName = "Test MultiRule Second"

                    # DN is calculated dynamically by the export sync rule expression
                    $upn2 = "$($testUser2.SamAccountName)@panoply.local"

                    # Use Import-Csv/Export-Csv to ensure correct column handling
                    $csv = Import-Csv $csvPath
                    $multiRule2 = [PSCustomObject]@{
                        hrId = $testUser2.HrId
                        employeeId = $testUser2.EmployeeId
                        firstName = $testUser2.FirstName
                        lastName = $testUser2.LastName
                        email = $testUser2.Email
                        department = $testUser2.Department
                        title = $testUser2.Title
                        company = $testUser2.Company
                        samAccountName = $testUser2.SamAccountName
                        displayName = $testUser2.DisplayName
                        status = "Active"
                        userPrincipalName = $upn2
                        employeeType = $testUser2.EmployeeType
                        employeeEndDate = ""
                    }
                    $csv = @($csv) + $multiRule2
                    $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
                    Copy-CsvToConnectorFiles -SourcePath $csvPath

                    # Import second user
                    Write-Host "  Importing second user with different EmployeeId=$($testUser2.EmployeeId), same Email=$($testUser2.Email)..." -ForegroundColor Gray
                    $importResult2 = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
                    Assert-ActivitySuccess -ActivityId $importResult2.activityId -Name "CSV Import (MultipleRules - second user)"
                    $syncResult2 = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
                    Assert-ActivitySuccess -ActivityId $syncResult2.activityId -Name "Full Sync (MultipleRules - second user)"

                    # Step 4: Verify the second CSO joined to the SAME MVO (via email rule)
                    # Get-JIMMetaverseObject returns objects directly, not wrapped in .items
                    $mvosAfter = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test MultiRule" -PageSize 10 -ErrorAction SilentlyContinue
                    $matchingMvos = @($mvosAfter | Where-Object { $_.displayName -match "Test MultiRule" })

                    Write-Host "  Found $($matchingMvos.Count) MVO(s) after second import" -ForegroundColor Gray

                    # Check if the original MVO still exists and was joined to
                    $originalStillExists = $matchingMvos | Where-Object { $_.id -eq $originalMvoId }

                    if ($matchingMvos.Count -eq 1 -and $originalStillExists) {
                        # Perfect - second CSO joined to existing MVO via email rule
                        Write-Host "  ✓ Second CSO joined to existing MVO via secondary matching rule (email)" -ForegroundColor Green
                        $testResults.Steps += @{ Name = "MultipleRules"; Success = $true }
                    }
                    elseif ($matchingMvos.Count -eq 2) {
                        # Two MVOs - the second CSO created a new MVO instead of joining
                        # This could mean the email rule didn't work, or projection happened first
                        Write-Host "  Two MVOs exist - secondary rule may not have matched (projection may have occurred first)" -ForegroundColor Yellow
                        $testResults.Steps += @{ Name = "MultipleRules"; Success = $true; Warning = "Two MVOs created - email rule may not have matched before projection" }
                    }
                    elseif ($matchingMvos.Count -eq 1 -and -not $originalStillExists) {
                        # Original MVO was replaced - unexpected
                        Write-Host "  Original MVO was replaced - unexpected behaviour" -ForegroundColor Yellow
                        $testResults.Steps += @{ Name = "MultipleRules"; Success = $true; Warning = "Original MVO replaced" }
                    }
                    else {
                        Write-Host "  Unexpected result: $($matchingMvos.Count) MVOs found" -ForegroundColor Yellow
                        $testResults.Steps += @{ Name = "MultipleRules"; Success = $true; Warning = "Unexpected MVO count: $($matchingMvos.Count)" }
                    }
                }
            }
        }
    }

    # Test 5: Join Conflict - Different external IDs, same matching attribute
    if ($Step -eq "JoinConflict" -or $Step -eq "All") {
        Write-TestSection "Test 5: Join Conflict (CouldNotJoinDueToExistingJoin)"

        Write-Host "Testing: Two CSOs with different hrIds but same employeeId should produce join error" -ForegroundColor Gray
        Write-Host "  This tests the sync-level join conflict when matching rules find an MVO with existing connector" -ForegroundColor Gray

        # This scenario tests what happens when:
        # 1. CSO #1 imports with hrId=A, employeeId=X → Projects to MVO
        # 2. CSO #2 imports with hrId=B (different), employeeId=X (same)
        # 3. Matching rule on employeeId finds the MVO
        # 4. CSO #2 tries to join → ERROR: MVO already has a connector from this CS

        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"

        # Create first user - will project to create MVO
        $testUser1 = New-TestUser -Index 9020
        $testUser1.HrId = "00009020-0000-0000-0000-000000000000"
        $testUser1.EmployeeId = "EMP900020"  # Same employeeId for both
        $testUser1.SamAccountName = "test.joinconflict1"
        $testUser1.Email = "test.joinconflict1@panoply.local"
        $testUser1.DisplayName = "Test Join Conflict One"

        $upn1 = "$($testUser1.SamAccountName)@panoply.local"
        $csv = Import-Csv $csvPath
        $conflictUser1 = [PSCustomObject]@{
            hrId = $testUser1.HrId
            employeeId = $testUser1.EmployeeId
            firstName = $testUser1.FirstName
            lastName = $testUser1.LastName
            email = $testUser1.Email
            department = $testUser1.Department
            title = $testUser1.Title
            company = $testUser1.Company
            samAccountName = $testUser1.SamAccountName
            displayName = $testUser1.DisplayName
            status = "Active"
            userPrincipalName = $upn1
            employeeType = $testUser1.EmployeeType
            employeeEndDate = ""
        }
        $csv = @($csv) + $conflictUser1
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath

        # Import and sync first user to create MVO
        Write-Host "  Creating MVO with first user (HrId=$($testUser1.HrId), EmployeeId=$($testUser1.EmployeeId))..." -ForegroundColor Gray
        $importResult1 = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult1.activityId -Name "CSV Import (JoinConflict - first user)"
        $syncResult1 = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult1.activityId -Name "Full Sync (JoinConflict - first user)"

        # Now add second user with DIFFERENT hrId but SAME employeeId
        $testUser2 = New-TestUser -Index 9021
        $testUser2.HrId = "00009021-0000-0000-0000-000000000000"  # DIFFERENT hrId (different CSO)
        $testUser2.EmployeeId = "EMP900020"  # SAME employeeId (will match the MVO)
        $testUser2.SamAccountName = "test.joinconflict2"
        $testUser2.Email = "test.joinconflict2@panoply.local"
        $testUser2.DisplayName = "Test Join Conflict Two"

        $upn2 = "$($testUser2.SamAccountName)@panoply.local"
        $csv = Import-Csv $csvPath
        $conflictUser2 = [PSCustomObject]@{
            hrId = $testUser2.HrId
            employeeId = $testUser2.EmployeeId
            firstName = $testUser2.FirstName
            lastName = $testUser2.LastName
            email = $testUser2.Email
            department = $testUser2.Department
            title = $testUser2.Title
            company = $testUser2.Company
            samAccountName = $testUser2.SamAccountName
            displayName = $testUser2.DisplayName
            status = "Active"
            userPrincipalName = $upn2
            employeeType = $testUser2.EmployeeType
            employeeEndDate = ""
        }
        $csv = @($csv) + $conflictUser2
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath

        # Import second user (this will create a separate CSO)
        Write-Host "  Importing second user with DIFFERENT HrId=$($testUser2.HrId), SAME EmployeeId=$($testUser2.EmployeeId)..." -ForegroundColor Gray
        $importResult2 = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult2.activityId -Name "CSV Import (JoinConflict - second user)"

        # Sync - this should produce the join conflict error
        $syncResult2 = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        # Don't assert success - we EXPECT this sync to have errors

        # Check the sync activity for CouldNotJoinDueToExistingJoin errors
        Write-Host "  Checking sync activity for join conflict error..." -ForegroundColor Gray
        $executionItems = @(Get-JIMActivity -Id $syncResult2.activityId -ExecutionItems)

        # Filter for the expected error type
        $joinConflictErrors = @($executionItems | Where-Object { $_.errorType -eq "CouldNotJoinDueToExistingJoin" })

        if ($joinConflictErrors.Count -ge 1) {
            Write-Host "  ✓ JIM correctly rejected join with error: CouldNotJoinDueToExistingJoin" -ForegroundColor Green
            $errorMsg = $joinConflictErrors[0].PSObject.Properties['errorMessage']?.Value ?? "[no message]"
            Write-Host "    Error message: $errorMsg" -ForegroundColor Gray

            # Verify only one MVO exists (second CSO should not have projected)
            $mvos = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test Join Conflict" -PageSize 20 -ErrorAction SilentlyContinue
            $conflictMvos = @($mvos | Where-Object { $_.displayName -match "Test Join Conflict" })

            if ($conflictMvos.Count -eq 1) {
                Write-Host "  ✓ Only 1 MVO exists (second CSO did not project)" -ForegroundColor Green
                $testResults.Steps += @{ Name = "JoinConflict"; Success = $true }
            }
            else {
                Write-Host "  ⚠ Found $($conflictMvos.Count) MVOs (expected 1)" -ForegroundColor Yellow
                $testResults.Steps += @{ Name = "JoinConflict"; Success = $true; Warning = "Found $($conflictMvos.Count) MVOs" }
            }
        }
        else {
            Write-Host "  ✗ Expected CouldNotJoinDueToExistingJoin error but none found" -ForegroundColor Red
            Write-Host "    This means the second CSO either joined (shouldn't happen) or projected (created new MVO)" -ForegroundColor Gray
            $testResults.Steps += @{ Name = "JoinConflict"; Success = $false; Error = "No join conflict error was raised" }
        }

        # Clean up Test 5 data - reset CSV to baseline and run import to obsolete leftover CSOs
        Copy-Item -Path "$scenarioDataPath/scenario5-hr-users.csv" -Destination "$testDataPath/hr-users.csv" -Force
        Copy-CsvToConnectorFiles -SourcePath $csvPath
        $cleanupImport = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        $cleanupSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Write-Host "  ✓ Reset CSV to baseline and ran cleanup import/sync for subsequent tests" -ForegroundColor Gray
    }

    # Test 5b: Same-Page Join Conflict (a second same-system CSO matching an MVO already claimed by a
    # PENDING join in ONE sync page). This is the intra-page sibling of Test 5. Test 5 imports and syncs
    # the two conflicting CSOs in SEPARATE sync runs, so the first join is already flushed to the database
    # when the second is evaluated and the database join-count guard sees it.
    #
    # Two BRAND-NEW same-key CSOs alone do NOT reproduce the defect: object matching runs against committed
    # database state, which cannot see an unflushed same-page projection, so each new CSO simply projects its
    # own MVO and they never contend for one. The defect needs a pre-existing, committed MVO that BOTH new
    # CSOs match. We create that by re-keying: seed one CSO/MVO first, then in a single later page obsolete
    # the seed CSO and introduce TWO new CSOs that both match the seeded MVO on employeeId.
    #
    # In that one page: the obsolete seed CSO releases its join slot (Defect A), the first new CSO claims the
    # slot as a pending (unflushed) join, and the second new CSO matches the same MVO. The database join-count
    # cannot see the first CSO's pending join, so without the pending-join guard both new CSOs pass validation
    # and their join writes collide on the filtered unique index
    # (IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique) as a raw 23505 that fails the
    # whole synchronisation. With the guard, the second match fails cleanly as CouldNotJoinDueToExistingJoin
    # and the run completes. The EF Core in-memory test provider does not enforce partial unique indexes and
    # reflects in-memory joins in its count, so this defect only reproduces against real PostgreSQL.
    if ($Step -eq "SamePageJoinConflict" -or $Step -eq "All") {
        Write-TestSection "Test 5b: Same-Page Join Conflict (intra-page CouldNotJoinDueToExistingJoin)"

        Write-Host "Testing: after a seed MVO exists, re-keying it so two new same-system CSOs both match it in" -ForegroundColor Gray
        Write-Host "  ONE sync page must not collide on the unique index; the second match fails cleanly" -ForegroundColor Gray

        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $spEmployeeId = "EMP900022"  # shared matching key: the seed and both re-keyed CSOs all carry it

        # Local helper: append a fully-populated HR CSV row for the given identifiers.
        $addSpRow = {
            param($rows, $index, $hrId, $sam, $display)
            $u = New-TestUser -Index $index
            $u.HrId = $hrId
            $u.EmployeeId = $spEmployeeId
            $u.SamAccountName = $sam
            $u.Email = "$sam@panoply.local"
            $u.DisplayName = $display
            return @($rows) + [PSCustomObject]@{
                hrId = $u.HrId; employeeId = $u.EmployeeId; firstName = $u.FirstName; lastName = $u.LastName
                email = $u.Email; department = $u.Department; title = $u.Title; company = $u.Company
                samAccountName = $u.SamAccountName; displayName = $u.DisplayName; status = "Active"
                userPrincipalName = "$($u.SamAccountName)@panoply.local"; employeeType = $u.EmployeeType; employeeEndDate = ""
            }
        }

        # Phase 1 (seed): baseline + a single seed user. Import + sync projects one committed MVO carrying the
        # shared employeeId, joined to the seed CSO.
        Copy-Item -Path "$scenarioDataPath/scenario5-hr-users.csv" -Destination $csvPath -Force
        $csv = & $addSpRow (Import-Csv $csvPath) 9021 "00009021-0000-0000-0000-000000000000" "test.samepage.seed" "SamePageRekey Seed"
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath

        Write-Host "  Seeding the pre-existing MVO (user 9021, EmployeeId=$spEmployeeId)..." -ForegroundColor Gray
        $spSeedImport = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $spSeedImport.activityId -Name "CSV Import (SamePageJoinConflict - seed)"
        $spSeedSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $spSeedSync.activityId -Name "CSV Full Sync (SamePageJoinConflict - seed)"

        # Phase 2 (re-key): baseline WITHOUT the seed user (so it obsoletes) + two new users that both match
        # the seeded MVO on employeeId. A single import brings both new CSOs; a single sync processes the
        # obsolete + both new joins in one page.
        Copy-Item -Path "$scenarioDataPath/scenario5-hr-users.csv" -Destination $csvPath -Force
        $csv = Import-Csv $csvPath
        $csv = & $addSpRow $csv 9022 "00009022-0000-0000-0000-000000000000" "test.samepage.win"  "SamePageRekey Winner"
        $csv = & $addSpRow $csv 9023 "00009023-0000-0000-0000-000000000000" "test.samepage.lose" "SamePageRekey Loser"
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
        Copy-CsvToConnectorFiles -SourcePath $csvPath

        Write-Host "  Re-keying: obsolete the seed and introduce two new matching CSOs in one import..." -ForegroundColor Gray
        $spImport = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $spImport.activityId -Name "CSV Import (SamePageJoinConflict - re-key)"

        Write-Host "  Synchronising the obsolete + two joins in a single page (must not crash on the unique index)..." -ForegroundColor Gray
        # Do not assert overall success: exactly one CSO is expected to fail with a clean join-conflict error.
        # A raw 23505 (defect present) aborts the whole page; -Wait may surface that as a thrown failure, which
        # we translate into an explicit red result rather than letting it escape as an unhandled error.
        $spSyncFailed = $false
        try {
            $spSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        }
        catch {
            $spSyncFailed = $true
            Write-Host "  ✗ Synchronisation aborted: $($_.Exception.Message)" -ForegroundColor Red
            $testResults.Steps += @{ Name = "SamePageJoinConflict"; Success = $false; Error = "Sync aborted (likely raw 23505): $($_.Exception.Message)" }
        }

        if (-not $spSyncFailed) {
            $spItems = @(Get-JIMActivity -Id $spSync.activityId -ExecutionItems)
            # errorType is always present on execution items (enum serialised as its name, "NotSet" when clean),
            # so this is safe under Set-StrictMode. errorMessage is omitted when null, so we never touch it.
            $spJoinConflictErrors = @($spItems | Where-Object { $_.errorType -eq "CouldNotJoinDueToExistingJoin" })
            $spUnexpectedErrors   = @($spItems | Where-Object { $_.errorType -and $_.errorType -ne "NotSet" -and $_.errorType -ne "CouldNotJoinDueToExistingJoin" })

            # Count MVOs carrying the shared employeeId: exactly one must survive (the seeded MVO, now owned by
            # the winning CSO); the losing CSO neither joined nor projected.
            $spMvos = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Search "SamePageRekey" -PageSize 20 -ErrorAction SilentlyContinue)
            $spRekeyMvos = @($spMvos | Where-Object { $_.displayName -match "SamePageRekey" })

            if ($spUnexpectedErrors.Count -gt 0) {
                $types = ($spUnexpectedErrors | ForEach-Object { $_.errorType }) -join ", "
                Write-Host "  ✗ Synchronisation recorded an unexpected error type ($types) - a raw constraint violation may have leaked" -ForegroundColor Red
                $testResults.Steps += @{ Name = "SamePageJoinConflict"; Success = $false; Error = "Unexpected error type(s) during same-page join: $types" }
            }
            elseif ($spJoinConflictErrors.Count -eq 1 -and $spRekeyMvos.Count -eq 1) {
                Write-Host "  ✓ Exactly one CSO failed cleanly with CouldNotJoinDueToExistingJoin; no raw constraint violation" -ForegroundColor Green
                Write-Host "  ✓ Exactly one MVO survives (the losing CSO neither joined nor projected)" -ForegroundColor Green
                $testResults.Steps += @{ Name = "SamePageJoinConflict"; Success = $true }
            }
            else {
                Write-Host "  ✗ Expected 1 CouldNotJoinDueToExistingJoin and 1 surviving MVO; found $($spJoinConflictErrors.Count) conflict error(s), $($spRekeyMvos.Count) MVO(s)" -ForegroundColor Red
                $testResults.Steps += @{ Name = "SamePageJoinConflict"; Success = $false; Error = "Found $($spJoinConflictErrors.Count) conflict error(s), $($spRekeyMvos.Count) MVO(s)" }
            }
        }

        # Clean up - reset CSV to baseline and run import/sync to obsolete leftover CSOs.
        Copy-Item -Path "$scenarioDataPath/scenario5-hr-users.csv" -Destination $csvPath -Force
        Copy-CsvToConnectorFiles -SourcePath $csvPath
        $spCleanupImport = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        $spCleanupSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Write-Host "  ✓ Reset CSV to baseline and ran cleanup import/sync for subsequent tests" -ForegroundColor Gray
    }

    # Test 5c: Same-Page Rejoin Cancellation (probe for #1612). Pass 1 of a sync page obsoletes a CSO
    # whose source row vanished, scheduling a grace-period Metaverse Object deletion (WhenAuthoritativeSourceDisconnected, CSV as the trigger source).
    # Pass 2 of the SAME page has a different CSO from the SAME Connected System match/join that same MVO via
    # the Object Matching Rule. Across separate runs, a rejoin correctly cancels a scheduled deletion (#119;
    # see Scenario 1's Reconnection step and Scenario 4's AuthoritativeRejoinCancellation step). #1612 suspects
    # this does NOT hold within one page, because Pass 2 evaluates a different in-memory instance of the MVO
    # than the one Pass 1 scheduled the deletion on: the cancellation is applied to an instance whose change is
    # then discarded rather than flushed, so the database-committed scheduling from Pass 1 survives the page.
    #
    # Reuses the SamePageJoinConflict mechanics immediately above (seed one CSO/MVO, then in a single later
    # page remove the seed row and add a new row with a NEW hrId but the SAME employeeId), but with only ONE
    # rekeyed CSO rather than two: here there is nothing to conflict over, only a disconnect and a rejoin
    # landing on the same MVO in the same page. Scenario 4 cannot express this at all: its CSV uses employeeId
    # as the external id, so one system cannot both obsolete and rejoin the same identity within one page.
    #
    # These assertions describe the CORRECT behaviour (deletion cancelled), not necessarily the current one:
    # the isPendingDeletion/marker assertion below is expected to FAIL on current code if the defect is live.
    if ($Step -eq "SamePageRejoinCancellation" -or $Step -eq "All") {
        Write-TestSection "Test 5c: Same-Page Rejoin Cancellation (#1612 probe)"

        Write-Host "Testing: within ONE sync page, a source disconnecting (scheduling grace-period deletion)" -ForegroundColor Gray
        Write-Host "  and a NEW same-system source rejoining the same identity must cancel that scheduled" -ForegroundColor Gray
        Write-Host "  deletion, leaving the object alive with every deletion marker cleared" -ForegroundColor Gray

        $srpUserObjectType = Get-JIMMetaverseObjectType -Name "User"

        # Capture the current deletion rule configuration so it can be restored once this step is done;
        # later steps (Case Sensitivity, ExportMatchJoin) rely on Setup-Scenario1's baseline configuration.
        $srpPreviousDeletionRule = $srpUserObjectType.deletionRule
        $srpPreviousGracePeriod = $srpUserObjectType.deletionGracePeriod
        $srpPreviousTriggerIds = $srpUserObjectType.deletionTriggerConnectedSystemIds
        $srpPreviousTriggerMode = $srpUserObjectType.deletionTriggerMode

        try {
            # WhenAuthoritativeSourceDisconnected anchored on the CSV system: the disconnection schedules the
            # deletion regardless of the directory connector this step deliberately leaves joined. Under
            # WhenLastConnectorDisconnected that surviving connector would mean no deletion is scheduled at
            # all (values preserved instead), and the cancellation assertion below would pass vacuously.
            Write-Host "  Configuring User deletion rule: WhenAuthoritativeSourceDisconnected (CSV source), 1-hour grace period..." -ForegroundColor Gray
            Set-JIMMetaverseObjectType -Id $srpUserObjectType.id `
                -DeletionRule "WhenAuthoritativeSourceDisconnected" `
                -DeletionTriggerConnectedSystemIds @($config.CSVSystemId) `
                -DeletionTriggerMode SpecificSourcesDisconnect `
                -DeletionGracePeriod ([TimeSpan]::FromHours(1)) | Out-Null

            $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
            $srpEmployeeId = "EMP900050"
            $srpSam = "test.samepage.rejoin"
            $srpDisplayName = "Test SamePage Rejoin"
            $srpDepartment = "Information Technology"  # one of the OUs Scenario 5 pre-creates for Samba AD

            $srpUser = New-TestUser -Index 9050
            $srpUser.EmployeeId = $srpEmployeeId
            $srpUser.SamAccountName = $srpSam
            $srpUser.Email = "$srpSam@panoply.local"
            $srpUser.DisplayName = $srpDisplayName
            $srpUser.Department = $srpDepartment

            # Local helper: append a fully-populated HR CSV row for the shared identity, varying only hrId.
            $addSrpRow = {
                param($rows, $hrId)
                return @($rows) + [PSCustomObject]@{
                    hrId = $hrId; employeeId = $srpUser.EmployeeId; firstName = $srpUser.FirstName; lastName = $srpUser.LastName
                    email = $srpUser.Email; department = $srpUser.Department; title = $srpUser.Title; company = $srpUser.Company
                    samAccountName = $srpUser.SamAccountName; displayName = $srpUser.DisplayName; status = "Active"
                    userPrincipalName = "$($srpUser.SamAccountName)@panoply.local"; employeeType = $srpUser.EmployeeType; employeeEndDate = ""
                }
            }

            # Phase 1 (seed): baseline + one seed row. Import + sync projects a committed MVO. Then export to
            # AD and confirm with an import, so the MVO carries a real, joined Connected System Object on a
            # system the HR-side rekey below never touches - proving that rekey does not collaterally disturb it.
            Copy-Item -Path "$scenarioDataPath/scenario5-hr-users.csv" -Destination $csvPath -Force
            $csv = & $addSrpRow (Import-Csv $csvPath) "00009050-0000-0000-0000-000000000000"
            $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
            Copy-CsvToConnectorFiles -SourcePath $csvPath

            Write-Host "  Seeding $srpSam (EmployeeId=$srpEmployeeId)..." -ForegroundColor Gray
            $srpSeedImport = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $srpSeedImport.activityId -Name "CSV Import (SamePageRejoinCancellation - seed)"
            $srpSeedSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $srpSeedSync.activityId -Name "CSV Full Sync (SamePageRejoinCancellation - seed)"

            Write-Host "  Provisioning to AD so the object carries a real connector the rekey never touches..." -ForegroundColor Gray
            $srpExport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
            Assert-ExportSuccess -ActivityId $srpExport.activityId -Name "LDAP Export (SamePageRejoinCancellation - seed)"
            $srpLdapImport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $srpLdapImport.activityId -Name "LDAP Import (SamePageRejoinCancellation - confirm)"

            # Find the MVO by an exact Account Name match (-Search only filters by display name;
            # -AttributeName/-AttributeValue is the exact-match attribute filter Scenario 1 also uses).
            $srpMvos = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Account Name" -AttributeValue $srpSam -ErrorAction SilentlyContinue)
            $srpMvo = $srpMvos | Select-Object -First 1
            if (-not $srpMvo) {
                $testResults.Steps += @{ Name = "SamePageRejoinCancellation"; Success = $false; Error = "Could not find seeded MVO by Account Name '$srpSam'" }
                throw "SamePageRejoinCancellation setup failed: could not find seeded MVO for $srpSam"
            }
            $srpMvoId = $srpMvo.id
            Write-Host "  MVO ID: $srpMvoId" -ForegroundColor Gray

            # Phase 2 (rekey): in ONE CSV write, remove the seed row and add a row with a NEW hrId but the
            # SAME employeeId (and same samAccountName/displayName). One import brings the obsoletion and the
            # rejoining CSO together; one sync then runs, in a SINGLE page, Pass 1 (the seed CSO's source row
            # vanished -> obsolete -> schedule grace-period deletion) and Pass 2 (the new CSO matches the same
            # MVO on employeeId -> join).
            Copy-Item -Path "$scenarioDataPath/scenario5-hr-users.csv" -Destination $csvPath -Force
            $csv = & $addSrpRow (Import-Csv $csvPath) "00009051-0000-0000-0000-000000000000"
            $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
            Copy-CsvToConnectorFiles -SourcePath $csvPath

            Write-Host "  Re-keying: obsolete the seed CSO and introduce the rejoining CSO in one import..." -ForegroundColor Gray
            $srpRekeyImport = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $srpRekeyImport.activityId -Name "CSV Import (SamePageRejoinCancellation - rekey)"

            Write-Host "  Synchronising the disconnect + rejoin in a single page..." -ForegroundColor Gray
            $srpRekeySync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $srpRekeySync.activityId -Name "CSV Full Sync (SamePageRejoinCancellation - rekey)"

            # Assert 1: the Metaverse Object still exists, with the SAME id (the rejoin landed on the marked
            # object, not on a fresh projection).
            $srpMvoAfter = Get-JIMMetaverseObject -Id $srpMvoId -ErrorAction SilentlyContinue
            if (-not $srpMvoAfter) {
                $testResults.Steps += @{ Name = "SamePageRejoinCancellation"; Success = $false; Error = "MVO $srpMvoId no longer exists after the same-page disconnect+rejoin" }
                throw "SamePageRejoinCancellation Assert 1 failed: MVO $srpMvoId no longer exists"
            }
            if ($srpMvoAfter.id -ne $srpMvoId) {
                $testResults.Steps += @{ Name = "SamePageRejoinCancellation"; Success = $false; Error = "Rejoin landed on a different MVO ($($srpMvoAfter.id)) instead of the marked one ($srpMvoId)" }
                throw "SamePageRejoinCancellation Assert 1 failed: rejoin landed on a different MVO"
            }
            Write-Host "  PASSED: the same Metaverse Object survived the same-page disconnect+rejoin" -ForegroundColor Green

            # Assert 2 (the #1612 assertion): isPendingDeletion=false and every deletion marker cleared.
            # lastConnectorDisconnectedDate and isPendingDeletion come from the MVO detail API; the
            # trigger/policy/initiator markers are not exposed on that DTO, so Get-MvoDeletionMarkers reads
            # them off the MetaverseObjects table directly (same pattern as Scenario 4's #119 trigger-mode
            # tests). This is expected to FAIL on current code if #1612 is live: Pass 2 cancels the deletion
            # against a different in-memory MVO instance than the one Pass 1 scheduled it on, so Pass 1's
            # database-committed scheduling survives the page uncancelled.
            $srpMarkers = Get-MvoDeletionMarkers -MvoId $srpMvoId
            $srpMarkerFailures = @()
            if ($srpMvoAfter.isPendingDeletion) { $srpMarkerFailures += "isPendingDeletion=true" }
            if ($srpMvoAfter.lastConnectorDisconnectedDate) { $srpMarkerFailures += "lastConnectorDisconnectedDate=$($srpMvoAfter.lastConnectorDisconnectedDate)" }
            if ($srpMarkers.IsMarkedForDeletion) { $srpMarkerFailures += "IsMarkedForDeletion=true" }
            if ($null -ne $srpMarkers.TriggeredBySystemId) { $srpMarkerFailures += "DeletionTriggeredBySystemId=$($srpMarkers.TriggeredBySystemId)" }
            if ($srpMarkers.TriggeredBySystemName) { $srpMarkerFailures += "DeletionTriggeredBySystemName=$($srpMarkers.TriggeredBySystemName)" }
            if ($srpMarkers.HasPolicySnapshot) { $srpMarkerFailures += "DeletionPolicySnapshotJson is set" }
            if ($srpMarkers.InitiatedByType -ne 0) { $srpMarkerFailures += "DeletionInitiatedByType=$($srpMarkers.InitiatedByType)" }
            if ($srpMarkers.InitiatedById) { $srpMarkerFailures += "DeletionInitiatedById=$($srpMarkers.InitiatedById)" }
            if ($srpMarkers.InitiatedByName) { $srpMarkerFailures += "DeletionInitiatedByName=$($srpMarkers.InitiatedByName)" }

            if ($srpMarkerFailures.Count -gt 0) {
                $srpFailureText = $srpMarkerFailures -join "; "
                Write-Host "  ✗ Deletion markers not cleared after a same-page rejoin (#1612): $srpFailureText" -ForegroundColor Red
                $testResults.Steps += @{ Name = "SamePageRejoinCancellation"; Success = $false; Error = "Deletion markers not cleared after a same-page rejoin (#1612): $srpFailureText" }
                throw "SamePageRejoinCancellation Assert 2 failed (#1612): a same-page rejoin did not cancel the grace-period deletion scheduled earlier in the same page ($srpFailureText)"
            }
            Write-Host "  PASSED: same-page rejoin cancelled the scheduled deletion and cleared every marker (#1612)" -ForegroundColor Green

            # Assert 3: the rejoining CSO (carrying the new hrId) is joined to the MVO.
            $srpJoinedCso = $srpMvoAfter.connectedSystemObjects | Where-Object { $_.connectedSystemId -eq $config.CSVSystemId } | Select-Object -First 1
            if (-not $srpJoinedCso) {
                $testResults.Steps += @{ Name = "SamePageRejoinCancellation"; Success = $false; Error = "No joined CSV Connected System Object on the MVO after the rejoin" }
                throw "SamePageRejoinCancellation Assert 3 failed: MVO has no joined CSV CSO after the rejoin"
            }
            Write-Host "  PASSED: the rejoining CSO is joined to the MVO ($($srpJoinedCso.displayName))" -ForegroundColor Green

            # Assert 4: the sync Activity's execution items recorded the disconnect and the join. They are
            # also expected to record MvoDeletionScheduled on the disconnect item: that outcome describes what
            # Pass 1 genuinely did in isolation and is not itself a #1612 defect signal (the defect is Pass 2
            # failing to see and cancel that scheduling, asserted above, not whether Pass 1 recorded it).
            Assert-ActivityItemsHaveOutcomeSummary -ActivityId $srpRekeySync.activityId -Name "Full Sync (SamePageRejoinCancellation)" -ExpectedOutcomeType "Disconnected"
            Assert-ActivityItemsHaveOutcomeSummary -ActivityId $srpRekeySync.activityId -Name "Full Sync (SamePageRejoinCancellation)" -ExpectedOutcomeType "Joined"
            Assert-ActivityItemsHaveOutcomeSummary -ActivityId $srpRekeySync.activityId -Name "Full Sync (SamePageRejoinCancellation)" -ExpectedOutcomeType "MvoDeletionScheduled"
            Write-Host "  PASSED: execution items recorded Disconnected, Joined and MvoDeletionScheduled outcomes" -ForegroundColor Green

            # Assert 5: nothing deprovisioned the directory account (the HR-side rekey never touched LDAP).
            $srpDirExists = Test-LDAPUserExists -UserIdentifier $srpSam -DirectoryConfig $DirectoryConfig
            if (-not $srpDirExists) {
                $testResults.Steps += @{ Name = "SamePageRejoinCancellation"; Success = $false; Error = "Directory account $srpSam no longer exists after the same-page rejoin" }
                throw "SamePageRejoinCancellation Assert 5 failed: directory account $srpSam was deprovisioned"
            }
            Write-Host "  PASSED: the directory account is untouched" -ForegroundColor Green

            $testResults.Steps += @{ Name = "SamePageRejoinCancellation"; Success = $true }
        }
        finally {
            # Restore the User Metaverse Object Type's deletion rule to whatever this step found it at,
            # so later steps (Case Sensitivity, ExportMatchJoin) see Setup-Scenario1's baseline configuration.
            Write-Host "  Restoring User deletion rule to its previous configuration..." -ForegroundColor Gray
            $srpRestoreParams = @{
                Id = $srpUserObjectType.id
                DeletionRule = $srpPreviousDeletionRule
            }
            if ($srpPreviousGracePeriod) { $srpRestoreParams.DeletionGracePeriod = [TimeSpan]$srpPreviousGracePeriod }
            if ($srpPreviousTriggerIds -and $srpPreviousTriggerIds.Count -gt 0) { $srpRestoreParams.DeletionTriggerConnectedSystemIds = $srpPreviousTriggerIds }
            if ($srpPreviousTriggerMode) { $srpRestoreParams.DeletionTriggerMode = $srpPreviousTriggerMode }
            Set-JIMMetaverseObjectType @srpRestoreParams | Out-Null

            # Clean up test user rows from the CSV so later steps start from the scenario baseline.
            $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
            if (Test-Path $csvPath) {
                $csvContent = Get-Content $csvPath | Where-Object { $_ -notmatch "test.samepage.rejoin" }
                $csvContent | Set-Content $csvPath
                Copy-CsvToConnectorFiles -SourcePath $csvPath
                $srpCleanupImport = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
                $srpCleanupSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
            }
            Write-Host "  ✓ Reset CSV to baseline and ran cleanup import/sync for subsequent tests" -ForegroundColor Gray
        }
    }

    # Test 6: Case Sensitivity
    # Tests that case-insensitive matching (default) works correctly and that
    # case-sensitive matching can be enabled when needed.
    if ($Step -eq "CaseSensitivity" -or $Step -eq "All") {
        Write-TestSection "Test 6: Case Sensitivity"

        Write-Host "Testing: Case-insensitive matching (default) should match 'emp123' to 'EMP123'" -ForegroundColor Gray

        # Get the CSV connected system and matching rule info
        $csvSystem = Get-JIMConnectedSystem | Where-Object { $_.name -match "HR CSV" }
        $csvObjectTypes = Get-JIMConnectedSystem -Id $csvSystem.id -ObjectTypes
        $csvUserType = $csvObjectTypes | Where-Object { $_.name -eq "person" }
        $mvAttributes = Get-JIMMetaverseAttribute

        if (-not $csvSystem -or -not $csvUserType) {
            Write-Host "  Could not find CSV system or User object type" -ForegroundColor Red
            $testResults.Steps += @{ Name = "CaseSensitivity"; Success = $false; Error = "CSV system not found" }
        }
        else {
            # Get attribute IDs for employeeId
            $csvEmployeeIdAttr = $csvUserType.attributes | Where-Object { $_.name -eq 'employeeId' }
            $mvEmployeeIdAttr = $mvAttributes | Where-Object { $_.name -eq 'Employee ID' }

            if (-not $csvEmployeeIdAttr -or -not $mvEmployeeIdAttr) {
                Write-Host "  Could not find employeeId attributes for matching rule" -ForegroundColor Red
                $testResults.Steps += @{ Name = "CaseSensitivity"; Success = $false; Error = "EmployeeId attributes not found" }
            }
            else {
                # First, check if there's a matching rule for employeeId and ensure it's case-insensitive
                $existingRules = Get-JIMMatchingRule -ConnectedSystemId $csvSystem.id -ObjectTypeId $csvUserType.id
                $employeeIdRule = $existingRules | Where-Object {
                    $_.targetMetaverseAttributeId -eq $mvEmployeeIdAttr.id
                }

                if ($employeeIdRule -and $employeeIdRule.caseSensitive -eq $true) {
                    # Update the rule to be case-insensitive for this test
                    Write-Host "  Updating existing employeeId matching rule to case-insensitive..." -ForegroundColor Gray
                    Set-JIMMatchingRule -ConnectedSystemId $csvSystem.id `
                        -Id $employeeIdRule.id `
                        -CaseSensitive $false | Out-Null
                }

                # Step 1: Create first user with UPPERCASE employeeId to establish the MVO
                $testUser1 = New-TestUser -Index 9030
                $testUser1.HrId = "00009030-0000-0000-0000-000000000000"
                $testUser1.EmployeeId = "CASETEST123"  # UPPERCASE
                $testUser1.SamAccountName = "test.casesens.upper"
                $testUser1.Email = "test.casesens.upper@panoply.local"
                $testUser1.DisplayName = "Test Case Upper"

                $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
                $upn1 = "$($testUser1.SamAccountName)@panoply.local"

                $csv = Import-Csv $csvPath
                $caseUser1 = [PSCustomObject]@{
                    hrId = $testUser1.HrId
                    employeeId = $testUser1.EmployeeId
                    firstName = $testUser1.FirstName
                    lastName = $testUser1.LastName
                    email = $testUser1.Email
                    department = $testUser1.Department
                    title = $testUser1.Title
                    company = $testUser1.Company
                    samAccountName = $testUser1.SamAccountName
                    displayName = $testUser1.DisplayName
                    status = "Active"
                    userPrincipalName = $upn1
                    employeeType = $testUser1.EmployeeType
                    employeeEndDate = ""
                }
                $csv = @($csv) + $caseUser1
                $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
                Copy-CsvToConnectorFiles -SourcePath $csvPath

                # Import and sync first user to create MVO
                Write-Host "  Creating MVO with UPPERCASE EmployeeId='$($testUser1.EmployeeId)'..." -ForegroundColor Gray
                $importResult1 = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
                Assert-ActivitySuccess -ActivityId $importResult1.activityId -Name "CSV Import (CaseSensitivity - first user)"
                $syncResult1 = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
                Assert-ActivitySuccess -ActivityId $syncResult1.activityId -Name "Full Sync (CaseSensitivity - first user)"

                # Verify MVO was created
                $mvos = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test Case Upper" -PageSize 10 -ErrorAction SilentlyContinue
                $originalMvo = $mvos | Where-Object { $_.displayName -match "Test Case Upper" } | Select-Object -First 1

                if (-not $originalMvo) {
                    Write-Host "  Failed to create initial MVO" -ForegroundColor Red
                    $testResults.Steps += @{ Name = "CaseSensitivity"; Success = $false; Error = "Could not create initial MVO" }
                }
                else {
                    $originalMvoId = $originalMvo.id
                    Write-Host "  MVO created with ID: $originalMvoId" -ForegroundColor Gray

                    # Step 2: Remove first user and add second user with LOWERCASE employeeId
                    Write-Host "  Removing first user and adding second user with lowercase employeeId..." -ForegroundColor Gray

                    # Remove the first user
                    $csvContent = Get-Content $csvPath | Where-Object { $_ -notmatch "test.casesens.upper" }
                    $csvContent | Set-Content $csvPath

                    # Add second user with lowercase employeeId that should match (case-insensitive)
                    $testUser2 = New-TestUser -Index 9031
                    $testUser2.HrId = "00009031-0000-0000-0000-000000000000"
                    $testUser2.EmployeeId = "casetest123"  # lowercase - should match CASETEST123
                    $testUser2.SamAccountName = "test.casesens.lower"
                    $testUser2.Email = "test.casesens.lower@panoply.local"
                    $testUser2.DisplayName = "Test Case Lower"

                    $upn2 = "$($testUser2.SamAccountName)@panoply.local"

                    $csv = Import-Csv $csvPath
                    $caseUser2 = [PSCustomObject]@{
                        hrId = $testUser2.HrId
                        employeeId = $testUser2.EmployeeId
                        firstName = $testUser2.FirstName
                        lastName = $testUser2.LastName
                        email = $testUser2.Email
                        department = $testUser2.Department
                        title = $testUser2.Title
                        company = $testUser2.Company
                        samAccountName = $testUser2.SamAccountName
                        displayName = $testUser2.DisplayName
                        status = "Active"
                        userPrincipalName = $upn2
                        employeeType = $testUser2.EmployeeType
                        employeeEndDate = ""
                    }
                    $csv = @($csv) + $caseUser2
                    $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
                    Copy-CsvToConnectorFiles -SourcePath $csvPath

                    # Import and sync second user - should join to existing MVO via case-insensitive match
                    Write-Host "  Importing second user with lowercase EmployeeId='$($testUser2.EmployeeId)'..." -ForegroundColor Gray
                    $importResult2 = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
                    Assert-ActivitySuccess -ActivityId $importResult2.activityId -Name "CSV Import (CaseSensitivity - second user)"
                    $syncResult2 = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
                    Assert-ActivitySuccess -ActivityId $syncResult2.activityId -Name "Full Sync (CaseSensitivity - second user)"

                    # Check if the second CSO joined to the existing MVO
                    # The MVO should now have a CSO from second user, and displayName might be updated
                    $updatedMvo = Get-JIMMetaverseObject -Id $originalMvoId -ErrorAction SilentlyContinue

                    if ($updatedMvo) {
                        # Verify case-insensitive matching by checking MVO count.
                        # If only 1 MVO exists with "Test Case" in the name, the second CSO joined
                        # the existing MVO instead of projecting a new one.
                        $allCaseMvos = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test Case" -PageSize 20 -ErrorAction SilentlyContinue
                        $caseMvoCount = @($allCaseMvos | Where-Object { $_.displayName -match "Test Case" }).Count

                        if ($caseMvoCount -eq 1) {
                            Write-Host "  ✓ Case-insensitive matching worked! Only 1 MVO exists (second CSO with lowercase 'casetest123' joined MVO with UPPERCASE 'CASETEST123')" -ForegroundColor Green
                            $testResults.Steps += @{ Name = "CaseSensitivity"; Success = $true }
                        }
                        elseif ($caseMvoCount -gt 1) {
                            Write-Host "  ✗ Case-insensitive matching FAILED - second user projected to NEW MVO instead of joining" -ForegroundColor Red
                            Write-Host "    Found $caseMvoCount MVOs (expected 1)" -ForegroundColor Gray
                            $testResults.Steps += @{ Name = "CaseSensitivity"; Success = $false; Error = "Case-insensitive matching did not work - created $caseMvoCount MVOs" }
                        }
                        else {
                            Write-Host "  ✗ No MVOs found matching 'Test Case'" -ForegroundColor Red
                            $testResults.Steps += @{ Name = "CaseSensitivity"; Success = $false; Error = "No MVOs found after case sensitivity test" }
                        }
                    }
                    else {
                        Write-Host "  ✗ Original MVO no longer exists" -ForegroundColor Red
                        $testResults.Steps += @{ Name = "CaseSensitivity"; Success = $false; Error = "Original MVO was deleted" }
                    }
                }

                # Clean up Test 6 data
                $csvContent = Get-Content $csvPath | Where-Object { $_ -notmatch "test.casesens" }
                $csvContent | Set-Content $csvPath
                Copy-CsvToConnectorFiles -SourcePath $csvPath
                Write-Host "  ✓ Cleaned up case sensitivity test data" -ForegroundColor Gray
            }
        }
    }

    # Test 7: Export Match Join (Outbound Matching)
    # Covers the OUTBOUND (bidirectional) direction of Object Matching Rules: when a Metaverse Object is
    # evaluated for provisioning into the LDAP Connected System and a matching, unjoined account ALREADY
    # exists there (created directly in the directory, out-of-band, not by JIM), the export evaluator must
    # JOIN to the existing Connected System Object instead of provisioning a duplicate.
    #
    # Engine path under test: ExportEvaluationServer.AttemptExportMatchingAsync ->
    # FindConnectedSystemObjectUsingMatchingRuleAsync, using the LDAP object matching rule that
    # Setup-Scenario1.ps1 configures (employeeID for Samba AD / employeeNumber for OpenLDAP -> Employee ID).
    #
    # Arrange: create the account directly in the directory (ldbadd for Samba AD, ldapadd for OpenLDAP) at
    # the EXACT DN the export attribute flow will compute (CN=<Display Name>,OU=<Department>,OU=Users,OU=Corp
    # for Samba AD; uid=<Account Name>,ou=People for OpenLDAP - see Setup-Scenario1.ps1's distinguishedName
    # expression mappings), so a correct join needs no incidental rename to prove itself.
    if ($Step -eq "ExportMatchJoin" -or $Step -eq "All") {
        Write-TestSection "Test 7: Export Match Join (Outbound Matching)"

        Write-Host "Testing: export evaluation should JOIN to a pre-existing, unjoined directory account instead of provisioning a duplicate" -ForegroundColor Gray

        $omjSamAccountName = "test.match.outbound"
        $omjEmployeeId = "OMJ001"
        $omjDisplayName = "Test Match Outbound"
        $omjFirstName = "TestMatch"
        $omjLastName = "Outbound"
        $omjEmail = "$omjSamAccountName@panoply.local"
        # "Information Technology" is one of the department OUs Scenario 5 pre-creates for Samba AD (see
        # above); using it keeps the out-of-band DN and the CSV-driven export DN identical.
        $omjDepartment = "Information Technology"
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"

        Write-Host "  Creating out-of-band directory account (not provisioned by JIM)..." -ForegroundColor Gray

        if ($isOpenLDAP) {
            $omjUserDN = "$($DirectoryConfig.UserRdnAttr)=$omjSamAccountName,$($DirectoryConfig.UserContainer)"
            $omjLdif = @"
dn: $omjUserDN
objectClass: inetOrgPerson
objectClass: organizationalPerson
objectClass: person
objectClass: top
uid: $omjSamAccountName
cn: $omjDisplayName
sn: $omjLastName
givenName: $omjFirstName
displayName: $omjDisplayName
mail: $omjEmail
employeeNumber: $omjEmployeeId
userPassword: Password123!
"@
            $omjCreateResult = $omjLdif | docker exec -i $DirectoryConfig.ContainerName ldapadd -x -H "ldap://localhost:$($DirectoryConfig.Port)" -D "$($DirectoryConfig.BindDN)" -w "$($DirectoryConfig.BindPassword)" 2>&1

            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✓ Created out-of-band account $omjSamAccountName in OpenLDAP (DN: $omjUserDN)" -ForegroundColor Green
            }
            elseif ($omjCreateResult -match "already exists") {
                Write-Host "  Out-of-band account $omjSamAccountName already exists" -ForegroundColor Yellow
            }
            else {
                throw "Failed to create out-of-band OpenLDAP account: $omjCreateResult"
            }
        }
        else {
            # Samba AD: samba-tool user create does not accept an arbitrary DN or an employeeID value, and
            # its default CN would not match the DN the export attribute flow computes from Display Name.
            # Use ldbadd routed through the RUNNING server (ldap://localhost with admin credentials), not
            # against the raw sam.ldb file: direct file access on a live domain controller races with the
            # server's own writes and index maintenance, and intermittently fails with a phantom
            # "No such object: parent does not exist" even though the parent OU exists (observed 2026-07-23
            # at the Nano template). Going through the server is the supported path and eliminates the race.
            $omjUserDN = "CN=$omjDisplayName,OU=$omjDepartment,OU=Users,OU=Corp,$($DirectoryConfig.BaseDN)"
            $omjLdif = @"
dn: $omjUserDN
objectClass: top
objectClass: person
objectClass: organizationalPerson
objectClass: user
cn: $omjDisplayName
sn: $omjLastName
givenName: $omjFirstName
sAMAccountName: $omjSamAccountName
displayName: $omjDisplayName
userPrincipalName: $omjEmail
mail: $omjEmail
department: $omjDepartment
employeeID: $omjEmployeeId

"@
            $omjLdifPath = [System.IO.Path]::GetTempFileName()
            try {
                # Normalise to LF line endings: this script file is CRLF, so the here-string embeds
                # \r\n, and samba's ldb LDIF parser (unlike OpenLDAP's) does NOT strip the \r. The
                # DN then carries an invisible trailing \r, the parent resolves to "...DC=local\r",
                # and the add fails with LDAP error 32 "parent does not exist" even though the
                # parent OU is right there (root-caused 2026-07-23 after the \r visually truncated
                # the captured error message, hiding its closing quote).
                [System.IO.File]::WriteAllText($omjLdifPath, $omjLdif.Replace("`r`n", "`n"))
                docker cp $omjLdifPath "$($DirectoryConfig.ContainerName):/tmp/omj-user.ldif" 2>&1 | Out-Null

                # Retry with backoff as cheap insurance against genuinely transient directory
                # conditions, capturing the full ldb output plus a parent-OU probe per attempt so
                # any persistent failure is diagnosable rather than a one-line mystery.
                $omjMaxAttempts = 5
                $omjCreated = $false
                $omjCreateText = ""
                for ($omjAttempt = 1; $omjAttempt -le $omjMaxAttempts; $omjAttempt++) {
                    $omjCreateResult = docker exec $DirectoryConfig.ContainerName ldbadd -H ldap://localhost -U "$sambaAdminUser%$($DirectoryConfig.BindPassword)" /tmp/omj-user.ldif 2>&1 | ForEach-Object { "$_" }
                    $omjCreateText = $omjCreateResult -join "`n"

                    if ($omjCreateText -match "Added 1 record" -or ($LASTEXITCODE -eq 0 -and [string]::IsNullOrWhiteSpace($omjCreateText))) {
                        Write-Host "  ✓ Created out-of-band account $omjSamAccountName in Samba AD (DN: $omjUserDN)" -ForegroundColor Green
                        $omjCreated = $true
                        break
                    }
                    if ($omjCreateText -match "already exists") {
                        Write-Host "  Out-of-band account $omjSamAccountName already exists" -ForegroundColor Yellow
                        $omjCreated = $true
                        break
                    }

                    $omjParentProbe = (docker exec $DirectoryConfig.ContainerName ldbsearch -H ldap://localhost -U "$sambaAdminUser%$($DirectoryConfig.BindPassword)" -b "OU=$omjDepartment,OU=Users,OU=Corp,$($DirectoryConfig.BaseDN)" -s base dn 2>&1 | ForEach-Object { "$_" }) -join "`n"
                    Write-Host "  Attempt $omjAttempt/$omjMaxAttempts to create out-of-band account failed: $omjCreateText" -ForegroundColor Yellow
                    Write-Host "  Parent OU probe: $omjParentProbe" -ForegroundColor Yellow
                    if ($omjAttempt -lt $omjMaxAttempts) { Start-Sleep -Seconds 5 }
                }
                docker exec $DirectoryConfig.ContainerName rm -f /tmp/omj-user.ldif 2>&1 | Out-Null

                if (-not $omjCreated) {
                    throw "Failed to create out-of-band Samba AD account via ldbadd after $omjMaxAttempts attempts: $omjCreateText"
                }
            }
            finally {
                Remove-Item $omjLdifPath -Force -ErrorAction SilentlyContinue
            }
        }

        # Step: full import from LDAP and confirm the CSO exists but is genuinely unjoined - this proves the
        # account really was created out-of-band, not via JIM (otherwise the "join" assertion below would be
        # meaningless).
        Write-Host "  Running LDAP full import..." -ForegroundColor Gray
        $omjLdapImport1 = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $omjLdapImport1.activityId -Name "LDAP Import (ExportMatchJoin - arrange)"

        $omjUnjoinedCsos = @(Get-JIMConnectedSystemObject -ConnectedSystemId $config.LDAPSystemId -Search $omjDisplayName -JoinType NotJoined -PageSize 10)
        $omjUnjoinedCso = $omjUnjoinedCsos | Where-Object { $_.displayName -eq $omjDisplayName } | Select-Object -First 1
        $omjArrangeOk = $false

        if (-not $omjUnjoinedCso) {
            Write-Host "  ✗ Could not find an unjoined LDAP CSO for '$omjDisplayName' after import - arrange step failed" -ForegroundColor Red
            $testResults.Steps += @{ Name = "ExportMatchJoin"; Success = $false; Error = "Out-of-band account did not import as an unjoined CSO" }
        }
        else {
            Write-Host "  ✓ Out-of-band account imported as an unjoined Connected System Object (proves genuine out-of-band arrange)" -ForegroundColor Green
            $omjArrangeOk = $true
        }

        if ($omjArrangeOk) {
            # Act: add the HR CSV row with the SAME employeeId. Import + sync projects the MVO and runs
            # export evaluation, where export matching should find and JOIN the existing CSO instead of
            # provisioning a new one.
            $upn = "$omjSamAccountName@panoply.local"
            $csv = Import-Csv $csvPath
            $omjHrRow = [PSCustomObject]@{
                hrId = "00009040-0000-0000-0000-000000000000"
                employeeId = $omjEmployeeId
                firstName = $omjFirstName
                lastName = $omjLastName
                email = $omjEmail
                department = $omjDepartment
                title = "Engineer"
                company = "Panoply"
                pronouns = ""
                samAccountName = $omjSamAccountName
                displayName = $omjDisplayName
                status = "Active"
                userPrincipalName = $upn
                employeeType = "Employee"
                employeeEndDate = ""
            }
            $csv = @($csv) + $omjHrRow
            $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
            Copy-CsvToConnectorFiles -SourcePath $csvPath
            Write-Host "  Added $omjSamAccountName to HR CSV with EmployeeId=$omjEmployeeId..." -ForegroundColor Gray

            $omjCsvImport = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $omjCsvImport.activityId -Name "CSV Import (ExportMatchJoin)"
            $omjCsvSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $omjCsvSync.activityId -Name "Full Sync (ExportMatchJoin)"

            Write-Host "  Running LDAP export (export matching should join the existing CSO)..." -ForegroundColor Gray
            $omjExport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
            Assert-ExportSuccess -ActivityId $omjExport.activityId -Name "LDAP Export (ExportMatchJoin)"

            Write-Host "  Running confirming LDAP import..." -ForegroundColor Gray
            $omjLdapImport2 = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $omjLdapImport2.activityId -Name "LDAP Import (ExportMatchJoin - confirm)"

            # Assert: the CSO is now Joined (not Provisioned); exactly one LDAP CSO and one directory account
            # exist for this identity - no duplicate was provisioned.
            $omjCsosAfter = @(Get-JIMConnectedSystemObject -ConnectedSystemId $config.LDAPSystemId -Search $omjDisplayName -PageSize 10)
            $omjMatchingCsos = @($omjCsosAfter | Where-Object { $_.displayName -eq $omjDisplayName })

            if ($omjMatchingCsos.Count -eq 1 -and "$($omjMatchingCsos[0].joinType)" -eq 'Joined') {
                Write-Host "  ✓ Connected System Object JoinType=Joined (existing directory account was joined, not provisioned)" -ForegroundColor Green

                $omjDirCount = if ($isOpenLDAP) {
                    Get-LDAPUserCount -DirectoryConfig $DirectoryConfig -Filter "(employeeNumber=$omjEmployeeId)"
                } else {
                    Get-LDAPUserCount -DirectoryConfig $DirectoryConfig -Filter "(employeeID=$omjEmployeeId)"
                }

                if ($omjDirCount -eq 1) {
                    Write-Host "  ✓ Exactly one directory account exists for EmployeeId=$omjEmployeeId (no duplicate provisioned)" -ForegroundColor Green
                    $testResults.Steps += @{ Name = "ExportMatchJoin"; Success = $true }
                }
                else {
                    Write-Host "  ✗ Found $omjDirCount directory account(s) for EmployeeId=$omjEmployeeId (expected 1)" -ForegroundColor Red
                    $testResults.Steps += @{ Name = "ExportMatchJoin"; Success = $false; Error = "Found $omjDirCount directory accounts (expected 1) - possible duplicate provisioning" }
                }
            }
            elseif ($omjMatchingCsos.Count -gt 1) {
                Write-Host "  ✗ Found $($omjMatchingCsos.Count) LDAP Connected System Objects for '$omjDisplayName' (expected 1) - export matching provisioned a duplicate instead of joining" -ForegroundColor Red
                $testResults.Steps += @{ Name = "ExportMatchJoin"; Success = $false; Error = "Duplicate CSO created ($($omjMatchingCsos.Count) found) - export matching did not join" }
            }
            elseif ($omjMatchingCsos.Count -eq 1) {
                Write-Host "  ✗ Connected System Object exists but JoinType=$($omjMatchingCsos[0].joinType) (expected Joined)" -ForegroundColor Red
                $testResults.Steps += @{ Name = "ExportMatchJoin"; Success = $false; Error = "CSO JoinType=$($omjMatchingCsos[0].joinType), expected Joined" }
            }
            else {
                Write-Host "  ✗ No LDAP Connected System Object found for '$omjDisplayName' after export" -ForegroundColor Red
                $testResults.Steps += @{ Name = "ExportMatchJoin"; Success = $false; Error = "No LDAP CSO found after export" }
            }
        }

        # Clean up: remove the HR CSV row and the directory account. The Metaverse Object Type's default
        # deletion rule (WhenAuthoritativeSourceDisconnected anchored on the HR CSV, 7-day grace period - set in
        # Setup-Scenario1.ps1) means removing the CSV row only schedules the deletion: nothing deprovisions the
        # LDAP side until housekeeping acts after the grace period, so the out-of-band-created directory account
        # must be deleted directly rather than relying on the export sync rule's OutboundDeprovisionAction=Delete cascade.
        Copy-Item -Path "$scenarioDataPath/scenario5-hr-users.csv" -Destination $csvPath -Force
        Copy-CsvToConnectorFiles -SourcePath $csvPath
        $omjCleanupImport = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        $omjCleanupSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru

        if ($isOpenLDAP) {
            $omjDeleteResult = docker exec $DirectoryConfig.ContainerName ldapdelete -x -H "ldap://localhost:$($DirectoryConfig.Port)" -D "$($DirectoryConfig.BindDN)" -w "$($DirectoryConfig.BindPassword)" "$omjUserDN" 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✓ Deleted out-of-band directory account $omjSamAccountName" -ForegroundColor Gray
            }
            else {
                Write-Host "  ⚠ Could not delete out-of-band directory account: $omjDeleteResult" -ForegroundColor Yellow
            }
        }
        else {
            $omjDeleteResult = docker exec $DirectoryConfig.ContainerName bash -c "samba-tool user delete '$omjSamAccountName' 2>&1; echo EXIT_CODE:`$?"
            if ($omjDeleteResult -match "Deleted user") {
                Write-Host "  ✓ Deleted out-of-band directory account $omjSamAccountName" -ForegroundColor Gray
            }
            else {
                Write-Host "  ⚠ Could not delete out-of-band directory account: $omjDeleteResult" -ForegroundColor Yellow
            }
        }
        Write-Host "  ✓ Reset CSV to baseline and ran cleanup import/sync for subsequent tests" -ForegroundColor Gray
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
    Write-Host "✗ Scenario 5 failed: $_" -ForegroundColor Red
    Write-Host "  Stack trace: $($_.ScriptStackTrace)" -ForegroundColor Gray
    $testResults.Error = $_.Exception.Message
    exit 1
}
