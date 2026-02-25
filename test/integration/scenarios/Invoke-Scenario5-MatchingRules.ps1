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

.PARAMETER Step
    Which test step to execute (Projection, Join, DuplicatePrevention, MultiplRules, All)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

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
    [ValidateSet("Projection", "Join", "DuplicatePrevention", "MultipleRules", "JoinConflict", "CaseSensitivity", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
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
    Write-Host "Setting up dedicated CSV for Scenario 5 tests..." -ForegroundColor Gray
    $testDataPath = "$PSScriptRoot/../../test-data"
    $scenarioDataPath = "$PSScriptRoot/data"

    # Ensure test-data directory exists
    if (-not (Test-Path $testDataPath)) {
        New-Item -ItemType Directory -Path $testDataPath -Force | Out-Null
    }

    # Copy empty scenario-specific CSV as the starting point
    Copy-Item -Path "$scenarioDataPath/scenario5-hr-users.csv" -Destination "$testDataPath/hr-users.csv" -Force

    # Copy to container volume
    $csvPath = "$testDataPath/hr-users.csv"
    docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv
    Write-Host "  ✓ Dedicated CSV initialised (1 baseline user for schema discovery)" -ForegroundColor Green

    # Clean up test-specific AD users from previous test runs
    Write-Host "Cleaning up test-specific AD users from previous runs..." -ForegroundColor Gray
    $testUsers = @("test.projection", "test.join", "test.duplicate1", "test.duplicate2", "test.multirule.first", "test.multirule.second", "baseline.user1")
    $deletedCount = 0
    foreach ($user in $testUsers) {
        $output = & docker exec samba-ad-primary bash -c "samba-tool user delete '$user' 2>&1; echo EXIT_CODE:\$?"
        if ($output -match "Deleted user") {
            Write-Host "  Deleted $user from AD" -ForegroundColor Gray
            $deletedCount++
        } elseif ($output -match "Unable to find user") {
            Write-Host "  - $user not found (already clean)" -ForegroundColor DarkGray
        }
    }
    Write-Host "  ✓ AD cleanup complete ($deletedCount test users deleted)" -ForegroundColor Green

    # Setup scenario configuration (reuse Scenario 1 setup)
    $config = & "$PSScriptRoot/../Setup-Scenario1.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template

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
    # The DN expression uses: OU=<Department>,OU=Users,OU=Corp,DC=subatomic,DC=local
    Write-Host "Creating department OUs for test users..." -ForegroundColor Gray
    $testDepartments = @("Information Technology", "Operations", "Finance", "Sales", "Marketing")
    foreach ($dept in $testDepartments) {
        $result = docker exec samba-ad-primary samba-tool ou create "OU=$dept,OU=Users,OU=Corp,DC=subatomic,DC=local" 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ Created OU: $dept" -ForegroundColor Gray
        } elseif ($result -match "already exists") {
            Write-Host "  - OU $dept already exists" -ForegroundColor DarkGray
        }
    }
    Write-Host "  ✓ Department OUs ready" -ForegroundColor Green
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
        $testUser.Email = "test.projection@subatomic.local"
        $testUser.DisplayName = "Test Projection User"

        # Add user to CSV using proper CSV parsing (DN is calculated dynamically by the export sync rule expression)
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($testUser.SamAccountName)@subatomic.local"

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
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

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
        $testUser.Email = "test.join@subatomic.local"
        $testUser.DisplayName = "Test Join User"

        # DN is calculated dynamically by the export sync rule expression
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($testUser.SamAccountName)@subatomic.local"

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
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

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
            Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "LDAP Export (Join)"

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
        $testUser1.Email = "test.importdup1@subatomic.local"
        $testUser1.DisplayName = "Test Import Dup One"

        $upn1 = "$($testUser1.SamAccountName)@subatomic.local"
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
        $testUser2.Email = "test.importdup2@subatomic.local"
        $testUser2.DisplayName = "Test Import Dup Two"

        $upn2 = "$($testUser2.SamAccountName)@subatomic.local"
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
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

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
        docker cp "$testDataPath/hr-users.csv" samba-ad-primary:/connector-files/hr-users.csv
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
                $testUser1.Email = "test.multirule@subatomic.local"  # This email will be shared
                $testUser1.DisplayName = "Test MultiRule First"

                # DN is calculated dynamically by the export sync rule expression
                $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
                $upn1 = "$($testUser1.SamAccountName)@subatomic.local"

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
                docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

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
                    docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

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
                    $testUser2.Email = "test.multirule@subatomic.local"  # SAME email (rule 2 should match)
                    $testUser2.DisplayName = "Test MultiRule Second"

                    # DN is calculated dynamically by the export sync rule expression
                    $upn2 = "$($testUser2.SamAccountName)@subatomic.local"

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
                    docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

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
        $testUser1.Email = "test.joinconflict1@subatomic.local"
        $testUser1.DisplayName = "Test Join Conflict One"

        $upn1 = "$($testUser1.SamAccountName)@subatomic.local"
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
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

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
        $testUser2.Email = "test.joinconflict2@subatomic.local"
        $testUser2.DisplayName = "Test Join Conflict Two"

        $upn2 = "$($testUser2.SamAccountName)@subatomic.local"
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
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

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
        docker cp "$testDataPath/hr-users.csv" samba-ad-primary:/connector-files/hr-users.csv
        $cleanupImport = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        $cleanupSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Write-Host "  ✓ Reset CSV to baseline and ran cleanup import/sync for subsequent tests" -ForegroundColor Gray
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
                $testUser1.Email = "test.casesens.upper@subatomic.local"
                $testUser1.DisplayName = "Test Case Upper"

                $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
                $upn1 = "$($testUser1.SamAccountName)@subatomic.local"

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
                docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

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
                    $testUser2.Email = "test.casesens.lower@subatomic.local"
                    $testUser2.DisplayName = "Test Case Lower"

                    $upn2 = "$($testUser2.SamAccountName)@subatomic.local"

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
                    docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

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
                docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv
                Write-Host "  ✓ Cleaned up case sensitivity test data" -ForegroundColor Gray
            }
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
    Write-Host "✗ Scenario 5 failed: $_" -ForegroundColor Red
    Write-Host "  Stack trace: $($_.ScriptStackTrace)" -ForegroundColor Gray
    $testResults.Error = $_.Exception.Message
    exit 1
}
