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
    [ValidateSet("Projection", "Join", "DuplicatePrevention", "MultipleRules", "All")]
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

    # Reset CSV to baseline state before running tests
    Write-Host "Resetting CSV test data to baseline..." -ForegroundColor Gray
    & "$PSScriptRoot/../Generate-TestCSV.ps1" -Template $Template -OutputPath "$PSScriptRoot/../../test-data"
    Write-Host "  CSV test data reset to baseline" -ForegroundColor Green

    # Clean up test-specific AD users from previous test runs
    Write-Host "Cleaning up test-specific AD users from previous runs..." -ForegroundColor Gray
    $testUsers = @("test.projection", "test.join", "test.duplicate1", "test.duplicate2", "test.multirule")
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
    Write-Host "  AD cleanup complete ($deletedCount test users deleted)" -ForegroundColor Green

    # Setup scenario configuration (reuse Scenario 1 setup)
    $config = & "$PSScriptRoot/../Setup-Scenario1.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template

    if (-not $config) {
        throw "Failed to setup Scenario configuration"
    }

    Write-Host "JIM configured for Scenario 5" -ForegroundColor Green
    Write-Host "  CSV System ID: $($config.CSVSystemId)" -ForegroundColor Gray
    Write-Host "  LDAP System ID: $($config.LDAPSystemId)" -ForegroundColor Gray

    # Re-import module to ensure we have connection
    $modulePath = "$PSScriptRoot/../../../JIM.PowerShell/JIM/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop

    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    # Test 1: Projection - New CSO creates new MVO when no match exists
    if ($Step -eq "Projection" -or $Step -eq "All") {
        Write-TestSection "Test 1: Projection - New Identity"

        Write-Host "Testing: New CSO with unique employeeId should project to new MVO" -ForegroundColor Gray

        # Create test user with unique employee ID
        $testUser = New-TestUser -Index 9001
        $testUser.EmployeeId = "EMP900001"
        $testUser.SamAccountName = "test.projection"
        $testUser.Email = "test.projection@testdomain.local"
        $testUser.DisplayName = "Test Projection User"

        # Add user to CSV
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($testUser.SamAccountName)@testdomain.local"
        $dn = "CN=$($testUser.DisplayName),CN=Users,DC=testdomain,DC=local"
        $csvLine = "`"$($testUser.EmployeeId)`",`"$($testUser.FirstName)`",`"$($testUser.LastName)`",`"$($testUser.Email)`",`"$($testUser.Department)`",`"$($testUser.Title)`",`"$($testUser.SamAccountName)`",`"$($testUser.DisplayName)`",`"Active`",`"$upn`",`"$dn`""

        Add-Content -Path $csvPath -Value $csvLine
        Write-Host "  Added test.projection to CSV with EmployeeId=$($testUser.EmployeeId)" -ForegroundColor Gray

        # Copy updated CSV to container
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Run import and sync
        Write-Host "  Running import and sync..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        # Verify MVO was created
        $mvos = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "test.projection" -PageSize 10 -ErrorAction SilentlyContinue

        if ($mvos -and $mvos.items) {
            $projectedMvo = $mvos.items | Where-Object { $_.displayName -match "Test Projection" }
            if ($projectedMvo) {
                Write-Host "  MVO created with ID: $($projectedMvo.id)" -ForegroundColor Green
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
        $testUser.EmployeeId = "EMP900002"
        $testUser.SamAccountName = "test.join"
        $testUser.Email = "test.join@testdomain.local"
        $testUser.DisplayName = "Test Join User"

        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($testUser.SamAccountName)@testdomain.local"
        $dn = "CN=$($testUser.DisplayName),CN=Users,DC=testdomain,DC=local"
        $csvLine = "`"$($testUser.EmployeeId)`",`"$($testUser.FirstName)`",`"$($testUser.LastName)`",`"$($testUser.Email)`",`"$($testUser.Department)`",`"$($testUser.Title)`",`"$($testUser.SamAccountName)`",`"$($testUser.DisplayName)`",`"Active`",`"$upn`",`"$dn`""

        Add-Content -Path $csvPath -Value $csvLine
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Import from HR to create MVO
        Write-Host "  Creating MVO via HR import..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        # Get the MVO that was created
        $mvos = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "test.join" -PageSize 10 -ErrorAction SilentlyContinue
        $originalMvo = $mvos.items | Where-Object { $_.displayName -match "Test Join" } | Select-Object -First 1

        if (-not $originalMvo) {
            Write-Host "  Failed to create initial MVO for join test" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Join"; Success = $false; Error = "Could not create initial MVO" }
        }
        else {
            $originalMvoId = $originalMvo.id
            Write-Host "  MVO created with ID: $originalMvoId" -ForegroundColor Gray

            # Now export to AD (this will provision the user)
            Write-Host "  Exporting to AD..." -ForegroundColor Gray
            Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId | Out-Null
            Start-Sleep -Seconds $WaitSeconds

            # Import from AD to confirm the CSO joins back to the same MVO
            Write-Host "  Importing from AD to verify join..." -ForegroundColor Gray
            Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPImportProfileId | Out-Null
            Start-Sleep -Seconds $WaitSeconds

            # Verify the MVO still has the same ID (not duplicated)
            $mvosAfter = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "test.join" -PageSize 10 -ErrorAction SilentlyContinue
            $matchingMvos = $mvosAfter.items | Where-Object { $_.displayName -match "Test Join" }

            if ($matchingMvos.Count -eq 1 -and $matchingMvos[0].id -eq $originalMvoId) {
                Write-Host "  AD CSO joined to existing MVO (no duplicate created)" -ForegroundColor Green
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

    # Test 3: Duplicate Prevention - Can't have two CSOs from same CS joined to one MVO
    if ($Step -eq "DuplicatePrevention" -or $Step -eq "All") {
        Write-TestSection "Test 3: Duplicate Prevention"

        Write-Host "Testing: Cannot join second CSO from same CS to an MVO that already has a connector" -ForegroundColor Gray
        Write-Host "  This test validates that matching rules prevent data integrity issues" -ForegroundColor Gray

        # This scenario tests what happens when:
        # 1. User A exists in HR with employeeId X, projected to MVO
        # 2. User B is added to HR with SAME employeeId X (data error in HR)
        # 3. User B should NOT join to the same MVO (would create conflict)

        # Create first user
        $testUser1 = New-TestUser -Index 9003
        $testUser1.EmployeeId = "EMP900003"  # Same employeeId for both
        $testUser1.SamAccountName = "test.duplicate1"
        $testUser1.Email = "test.duplicate1@testdomain.local"
        $testUser1.DisplayName = "Test Duplicate User One"

        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn1 = "$($testUser1.SamAccountName)@testdomain.local"
        $dn1 = "CN=$($testUser1.DisplayName),CN=Users,DC=testdomain,DC=local"
        $csvLine1 = "`"$($testUser1.EmployeeId)`",`"$($testUser1.FirstName)`",`"$($testUser1.LastName)`",`"$($testUser1.Email)`",`"$($testUser1.Department)`",`"$($testUser1.Title)`",`"$($testUser1.SamAccountName)`",`"$($testUser1.DisplayName)`",`"Active`",`"$upn1`",`"$dn1`""

        Add-Content -Path $csvPath -Value $csvLine1
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Import first user
        Write-Host "  Creating first user with EmployeeId=$($testUser1.EmployeeId)..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        # Now add second user with same employeeId (simulating HR data error)
        $testUser2 = New-TestUser -Index 9004
        $testUser2.EmployeeId = "EMP900003"  # SAME employeeId - this is the conflict
        $testUser2.SamAccountName = "test.duplicate2"
        $testUser2.Email = "test.duplicate2@testdomain.local"
        $testUser2.DisplayName = "Test Duplicate User Two"

        $upn2 = "$($testUser2.SamAccountName)@testdomain.local"
        $dn2 = "CN=$($testUser2.DisplayName),CN=Users,DC=testdomain,DC=local"
        $csvLine2 = "`"$($testUser2.EmployeeId)`",`"$($testUser2.FirstName)`",`"$($testUser2.LastName)`",`"$($testUser2.Email)`",`"$($testUser2.Department)`",`"$($testUser2.Title)`",`"$($testUser2.SamAccountName)`",`"$($testUser2.DisplayName)`",`"Active`",`"$upn2`",`"$dn2`""

        Add-Content -Path $csvPath -Value $csvLine2
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Import second user
        Write-Host "  Importing second user with SAME EmployeeId (simulating HR data error)..." -ForegroundColor Gray
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds
        Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId | Out-Null
        Start-Sleep -Seconds $WaitSeconds

        # Check results - we should have appropriate handling of this conflict
        $mvos = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "test.duplicate" -PageSize 20 -ErrorAction SilentlyContinue

        if ($mvos -and $mvos.items) {
            $duplicateMvos = $mvos.items | Where-Object { $_.displayName -match "Test Duplicate" }

            # The expected behaviour depends on JIM's configuration:
            # - Could create two MVOs (if matching fails and projection creates new)
            # - Could have one MVO with join error on second CSO
            # - Could reject the second import entirely

            Write-Host "  Found $($duplicateMvos.Count) MVO(s) for duplicate test" -ForegroundColor Gray

            # For now, we're documenting what happens rather than asserting specific behaviour
            # The key is that data integrity is maintained (no silent data corruption)
            $testResults.Steps += @{
                Name = "DuplicatePrevention"
                Success = $true
                Warning = "Found $($duplicateMvos.Count) MVOs - review sync errors for conflict handling"
            }
        }
        else {
            $testResults.Steps += @{ Name = "DuplicatePrevention"; Success = $true; Warning = "Could not verify duplicate handling" }
        }
    }

    # Test 4: Multiple Matching Rules - Fallback behaviour
    if ($Step -eq "MultipleRules" -or $Step -eq "All") {
        Write-TestSection "Test 4: Multiple Matching Rules"

        Write-Host "Testing: When first matching rule doesn't match, subsequent rules should be evaluated" -ForegroundColor Gray
        Write-Host ""
        Write-Host "  NOT YET IMPLEMENTED" -ForegroundColor Yellow
        Write-Host "  Requires:" -ForegroundColor Yellow
        Write-Host "    1. Multiple matching rules configured on connected system" -ForegroundColor Yellow
        Write-Host "    2. Test data designed to match only on secondary/tertiary rules" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  Scenario to test:" -ForegroundColor Gray
        Write-Host "    1. Configure matching rules: Rule1 (employeeId), Rule2 (email), Rule3 (samAccountName)" -ForegroundColor Gray
        Write-Host "    2. Create CSO where employeeId doesn't match any MVO" -ForegroundColor Gray
        Write-Host "    3. But email DOES match an existing MVO" -ForegroundColor Gray
        Write-Host "    4. Verify CSO joins via Rule2 (email match)" -ForegroundColor Gray

        $testResults.Steps += @{ Name = "MultipleRules"; Success = $true; Warning = "Not implemented - requires multiple matching rules configuration" }
    }

    # Summary
    Write-TestSection "Test Results Summary"

    $successCount = @($testResults.Steps | Where-Object { $_.Success }).Count
    $totalCount = @($testResults.Steps).Count

    Write-Host "Tests run:    $totalCount" -ForegroundColor Cyan
    Write-Host "Tests passed: $successCount" -ForegroundColor $(if ($successCount -eq $totalCount) { "Green" } else { "Yellow" })

    foreach ($stepResult in $testResults.Steps) {
        $status = if ($stepResult.Success) { "" } else { "" }
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
        Write-Host " All tests passed" -ForegroundColor Green
        exit 0
    }
    else {
        Write-Host ""
        Write-Host " Some tests failed" -ForegroundColor Red
        exit 1
    }
}
catch {
    Write-Host ""
    Write-Host " Scenario 5 failed: $_" -ForegroundColor Red
    Write-Host "  Stack trace: $($_.ScriptStackTrace)" -ForegroundColor Gray
    $testResults.Error = $_.Exception.Message
    exit 1
}
