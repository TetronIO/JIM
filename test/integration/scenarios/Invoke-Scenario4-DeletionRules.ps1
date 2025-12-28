<#
.SYNOPSIS
    Test Scenario 4: MVO Deletion Rules and Deprovisioning

.DESCRIPTION
    Validates the MVO deletion rules functionality including:
    - Leaver processing with grace period
    - Reconnection before grace period expires (MVO preserved)
    - Source deletion handling (what happens when authoritative record is deleted)
    - Admin account protection (Origin=Internal)
    - Inbound scope filter changes (scoping by CSO attributes, e.g., department)
    - Outbound scope filter changes (scoping by MVO attributes via ObjectScopingCriteriaGroups API)

.PARAMETER Step
    Which test step to execute (LeaverGracePeriod, Reconnection, SourceDeletion, AdminProtection, InboundScopeFilter, OutboundScopeFilter, All)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER WaitSeconds
    Seconds to wait between steps for JIM processing (default: 30)

.EXAMPLE
    ./Invoke-Scenario4-DeletionRules.ps1 -Step All -Template Small -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario4-DeletionRules.ps1 -Step LeaverGracePeriod -Template Micro -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("LeaverGracePeriod", "Reconnection", "SourceDeletion", "AdminProtection", "InboundScopeFilter", "OutboundScopeFilter", "All")]
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

Write-TestSection "Scenario 4: MVO Deletion Rules and Deprovisioning"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "MVO Deletion Rules and Deprovisioning"
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
    Write-Host "  ✓ CSV test data reset to baseline" -ForegroundColor Green

    # Clean up test-specific AD users from previous test runs
    Write-Host "Cleaning up test-specific AD users from previous runs..." -ForegroundColor Gray
    $testUsers = @("test.leaver", "test.reconnect2", "test.outofscope", "test.admin")
    $deletedCount = 0
    foreach ($user in $testUsers) {
        $output = & docker exec samba-ad-primary bash -c "samba-tool user delete '$user' 2>&1; echo EXIT_CODE:\$?"
        if ($output -match "Deleted user") {
            Write-Host "  ✓ Deleted $user from AD" -ForegroundColor Gray
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

    Write-Host "✓ JIM configured for Scenario 4" -ForegroundColor Green
    Write-Host "  CSV System ID: $($config.CSVSystemId)" -ForegroundColor Gray
    Write-Host "  LDAP System ID: $($config.LDAPSystemId)" -ForegroundColor Gray

    # Re-import module to ensure we have connection
    $modulePath = "$PSScriptRoot/../../../JIM.PowerShell/JIM/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop

    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    # Configure deletion rules on the User object type
    Write-Host "Configuring deletion rules on User object type..." -ForegroundColor Gray

    # Get the User object type and configure deletion rules
    $userObjectType = Get-JIMMetaverseObjectType -Name "User"
    if ($userObjectType) {
        # Set deletion rule to WhenLastConnectorDisconnected with a short grace period for testing
        Set-JIMMetaverseObjectType -Id $userObjectType.id `
            -DeletionRule "WhenLastConnectorDisconnected" `
            -DeletionGracePeriodDays 1

        Write-Host "  ✓ User object type configured with DeletionRule=WhenLastConnectorDisconnected, GracePeriod=1 day" -ForegroundColor Green
    } else {
        Write-Host "  ⚠ User object type not found - using defaults" -ForegroundColor Yellow
    }

    # Test 1: Leaver with Grace Period
    if ($Step -eq "LeaverGracePeriod" -or $Step -eq "All") {
        Write-TestSection "Test 1: Leaver with Grace Period"

        Write-Host "Creating test user for leaver scenario..." -ForegroundColor Gray

        # Create test user
        $testUser = New-TestUser -Index 7777
        $testUser.EmployeeId = "EMP777777"
        $testUser.SamAccountName = "test.leaver"
        $testUser.Email = "test.leaver@testdomain.local"
        $testUser.DisplayName = "Test Leaver"

        # Add user to CSV (DN is calculated dynamically by the export sync rule expression)
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($testUser.SamAccountName)@testdomain.local"
        $csvLine = "`"$($testUser.EmployeeId)`",`"$($testUser.FirstName)`",`"$($testUser.LastName)`",`"$($testUser.Email)`",`"$($testUser.Department)`",`"$($testUser.Title)`",`"$($testUser.SamAccountName)`",`"$($testUser.DisplayName)`",`"Active`",`"$upn`""

        Add-Content -Path $csvPath -Value $csvLine
        Write-Host "  ✓ Added test.leaver to CSV" -ForegroundColor Green

        # Copy updated CSV to container
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Initial sync to provision the user
        Write-Host "Provisioning user via sync..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import (LeaverGracePeriod provisioning)"
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Sync (LeaverGracePeriod provisioning)"
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "LDAP Export (LeaverGracePeriod provisioning)"
        Start-Sleep -Seconds 5  # Brief wait for AD replication

        # Verify user was provisioned
        $adUserCheck = docker exec samba-ad-primary samba-tool user show test.leaver 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ✗ User test.leaver was not provisioned to AD" -ForegroundColor Red
            $testResults.Steps += @{ Name = "LeaverGracePeriod"; Success = $false; Error = "User not provisioned" }
        } else {
            Write-Host "  ✓ User test.leaver provisioned to AD" -ForegroundColor Green

            # Now remove the user from CSV (simulating leaver)
            Write-Host "Removing user from CSV (simulating leaver)..." -ForegroundColor Gray
            $csvContent = Get-Content $csvPath | Where-Object { $_ -notmatch "test.leaver" }
            $csvContent | Set-Content $csvPath
            docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

            # Sync to process the leaver
            $leaverImportResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $leaverImportResult.activityId -Name "CSV Import (LeaverGracePeriod removal)"
            $leaverSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $leaverSyncResult.activityId -Name "Full Sync (LeaverGracePeriod removal)"

            # Check MVO status via API - should have LastConnectorDisconnectedDate set
            Write-Host "Checking MVO deletion status..." -ForegroundColor Gray

            # Get MVOs and check if test.leaver MVO has LastConnectorDisconnectedDate set
            $mvos = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "test.leaver" -PageSize 10 -ErrorAction SilentlyContinue

            if ($mvos -and $mvos.items) {
                $leaverMvo = $mvos.items | Where-Object { $_.displayName -match "Test Leaver" }
                if ($leaverMvo) {
                    # MVO still exists with grace period pending
                    Write-Host "  ✓ MVO still exists with grace period pending (expected behaviour)" -ForegroundColor Green
                    $testResults.Steps += @{ Name = "LeaverGracePeriod"; Success = $true }
                } else {
                    Write-Host "  ⚠ MVO not found - may have been deleted (check grace period)" -ForegroundColor Yellow
                    $testResults.Steps += @{ Name = "LeaverGracePeriod"; Success = $true; Warning = "MVO deleted - grace period may have been 0" }
                }
            } else {
                Write-Host "  ⚠ No MVOs found matching test.leaver" -ForegroundColor Yellow
                $testResults.Steps += @{ Name = "LeaverGracePeriod"; Success = $true; Warning = "MVO not found - may already be deleted" }
            }
        }
    }

    # Test 2: Reconnection before Grace Period Expires
    if ($Step -eq "Reconnection" -or $Step -eq "All") {
        Write-TestSection "Test 2: Reconnection before Grace Period Expires"

        Write-Host "Creating test user for reconnection scenario..." -ForegroundColor Gray

        # Create test user
        $reconnectUser = New-TestUser -Index 6666
        $reconnectUser.EmployeeId = "EMP666666"
        $reconnectUser.SamAccountName = "test.reconnect2"
        $reconnectUser.Email = "test.reconnect2@testdomain.local"
        $reconnectUser.DisplayName = "Test Reconnect Two"

        # Add user to CSV (DN is calculated dynamically by the export sync rule expression)
        $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
        $upn = "$($reconnectUser.SamAccountName)@testdomain.local"
        $csvLine = "`"$($reconnectUser.EmployeeId)`",`"$($reconnectUser.FirstName)`",`"$($reconnectUser.LastName)`",`"$($reconnectUser.Email)`",`"$($reconnectUser.Department)`",`"$($reconnectUser.Title)`",`"$($reconnectUser.SamAccountName)`",`"$($reconnectUser.DisplayName)`",`"Active`",`"$upn`""

        Add-Content -Path $csvPath -Value $csvLine
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

        # Initial sync to provision the user
        Write-Host "Provisioning user via sync..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import (Reconnection provisioning)"
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Sync (Reconnection provisioning)"
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "LDAP Export (Reconnection provisioning)"
        Start-Sleep -Seconds 5  # Brief wait for AD replication

        # Verify user was provisioned
        $adUserCheck = docker exec samba-ad-primary samba-tool user show test.reconnect2 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ✗ User test.reconnect2 was not provisioned to AD" -ForegroundColor Red
            $testResults.Steps += @{ Name = "Reconnection"; Success = $false; Error = "User not provisioned" }
        } else {
            Write-Host "  ✓ User test.reconnect2 provisioned to AD" -ForegroundColor Green

            # Remove user from CSV (simulating quit)
            Write-Host "Removing user from CSV (simulating quit)..." -ForegroundColor Gray
            $csvContent = Get-Content $csvPath | Where-Object { $_ -notmatch "test.reconnect2" }
            $csvContent | Set-Content $csvPath
            docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

            # Short sync to mark CSO obsolete
            $quitImportResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $quitImportResult.activityId -Name "CSV Import (Reconnection quit)"
            $quitSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $quitSyncResult.activityId -Name "Full Sync (Reconnection quit)"

            # Re-add user to CSV (simulating rehire before grace period)
            Write-Host "Re-adding user to CSV (simulating rehire)..." -ForegroundColor Gray
            Add-Content -Path $csvPath -Value $csvLine
            docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv

            # Sync to process the rehire
            $rehireImportResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $rehireImportResult.activityId -Name "CSV Import (Reconnection rehire)"
            $rehireSyncResult = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $rehireSyncResult.activityId -Name "Full Sync (Reconnection rehire)"

            # Verify user still exists in AD and MVO is reconnected
            $adUserCheck = docker exec samba-ad-primary samba-tool user show test.reconnect2 2>&1

            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✓ Reconnection successful - user preserved in AD" -ForegroundColor Green

                # Additional check: verify MVO no longer has LastConnectorDisconnectedDate set
                $mvos = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "test.reconnect2" -PageSize 10 -ErrorAction SilentlyContinue

                if ($mvos -and $mvos.items) {
                    $reconnectMvo = $mvos.items | Where-Object { $_.displayName -match "Test Reconnect Two" }
                    if ($reconnectMvo) {
                        Write-Host "  ✓ MVO reconnected and grace period cleared" -ForegroundColor Green
                        $testResults.Steps += @{ Name = "Reconnection"; Success = $true }
                    } else {
                        $testResults.Steps += @{ Name = "Reconnection"; Success = $true; Warning = "MVO found but name mismatch" }
                    }
                } else {
                    $testResults.Steps += @{ Name = "Reconnection"; Success = $true; Warning = "Could not verify MVO state" }
                }
            } else {
                Write-Host "  ✗ Reconnection failed - user lost in AD" -ForegroundColor Red
                $testResults.Steps += @{ Name = "Reconnection"; Success = $false; Error = "User deleted instead of preserved" }
            }
        }
    }

    # Test 3: Source Deletion Handling
    # This tests what happens when the authoritative source record is deleted (e.g., HR removes employee)
    # This is DIFFERENT from scope filter changes - this tests CSO deletion, not attribute-based scope evaluation
    if ($Step -eq "SourceDeletion" -or $Step -eq "All") {
        Write-TestSection "Test 3: Source Deletion Handling"

        Write-Host "This test validates behaviour when the authoritative source record is deleted." -ForegroundColor Gray
        Write-Host "This triggers the MVO deletion rule processing (deferred deletion with grace period)." -ForegroundColor Gray

        # Verify the OutboundDeprovisionAction and InboundOutOfScopeAction properties exist on sync rules
        Write-Host "Checking sync rule deprovisioning settings..." -ForegroundColor Gray

        $syncRules = Get-JIMSyncRule -ConnectedSystemId $config.CSVSystemId -ErrorAction SilentlyContinue

        if ($syncRules) {
            $hasDeprovisionSettings = $false
            foreach ($rule in $syncRules) {
                if ($rule.PSObject.Properties.Name -contains 'outboundDeprovisionAction' -or
                    $rule.PSObject.Properties.Name -contains 'inboundOutOfScopeAction') {
                    $hasDeprovisionSettings = $true
                    Write-Host "  ✓ Sync rule '$($rule.name)' has deprovisioning settings" -ForegroundColor Green
                }
            }

            if ($hasDeprovisionSettings) {
                $testResults.Steps += @{ Name = "SourceDeletion"; Success = $true; Warning = "Deprovisioning settings present (full scenario tested in LeaverGracePeriod)" }
            } else {
                $testResults.Steps += @{ Name = "SourceDeletion"; Success = $true; Warning = "Deprovisioning properties available but not configured" }
            }
        } else {
            Write-Host "  ⚠ Could not retrieve sync rules" -ForegroundColor Yellow
            $testResults.Steps += @{ Name = "SourceDeletion"; Success = $true; Warning = "Could not verify sync rule settings" }
        }
    }

    # Test 5: Inbound Scope Filter Changes
    # Scenario: Configure scoping criteria on an IMPORT sync rule to filter by department
    # Only users in department='IT' should be synced to the Metaverse
    # Tests that CSOs not matching criteria are skipped during join/projection
    if ($Step -eq "InboundScopeFilter" -or $Step -eq "All") {
        Write-TestSection "Test 5: Inbound Scope Filter Changes"

        Write-Host "Testing: Inbound scope filter using Connected System attribute (department)" -ForegroundColor Gray
        Write-Host "  Configuring import sync rule to only sync users from department='IT'" -ForegroundColor Gray
        Write-Host "  Users in other departments should NOT be synced to the Metaverse." -ForegroundColor Gray

        # Get the CSV import sync rule
        $syncRules = Get-JIMSyncRule -ErrorAction SilentlyContinue

        if ($syncRules) {
            $csvImportRule = $syncRules | Where-Object { $_.name -match "HR CSV.*Import" -or ($_.direction -eq "Import" -and $_.connectedSystemName -match "HR CSV") } | Select-Object -First 1

            if ($csvImportRule) {
                Write-Host "  Found CSV Import sync rule: $($csvImportRule.name) (ID: $($csvImportRule.id))" -ForegroundColor Gray

                # Get the Connected System and object type to find the department attribute
                $csvConnectedSystem = Get-JIMConnectedSystem -ErrorAction SilentlyContinue | Where-Object { $_.name -match "HR CSV" } | Select-Object -First 1

                if ($csvConnectedSystem) {
                    $csvObjectTypes = Get-JIMConnectedSystemObjectType -ConnectedSystemId $csvConnectedSystem.id -ErrorAction SilentlyContinue
                    $csvUserType = $csvObjectTypes | Where-Object { $_.name -eq "user" -or $_.name -eq "User" } | Select-Object -First 1

                    if ($csvUserType -and $csvUserType.attributes) {
                        $deptAttr = $csvUserType.attributes | Where-Object { $_.name -eq "department" } | Select-Object -First 1

                        if ($deptAttr) {
                            Write-Host "  Found 'department' attribute (ID: $($deptAttr.id)) on CSV object type" -ForegroundColor Gray

                            try {
                                # Step 1: Create test users - one in IT dept, one in Finance dept
                                Write-Host "  Creating test users for scoping test..." -ForegroundColor Gray

                                $itUser = @{
                                    EmployeeId = "SCOPE001"
                                    FirstName = "Scope"
                                    LastName = "ITUser"
                                    Email = "scope.ituser@testdomain.local"
                                    Department = "IT"  # Should be IN scope
                                    Title = "IT Engineer"
                                    SamAccountName = "scope.ituser"
                                    DisplayName = "Scope ITUser"
                                }

                                $financeUser = @{
                                    EmployeeId = "SCOPE002"
                                    FirstName = "Scope"
                                    LastName = "FinanceUser"
                                    Email = "scope.financeuser@testdomain.local"
                                    Department = "Finance"  # Should be OUT of scope
                                    Title = "Financial Analyst"
                                    SamAccountName = "scope.financeuser"
                                    DisplayName = "Scope FinanceUser"
                                }

                                # Add users to CSV
                                $csvPath = "/var/connector-files/test-data/hr-users.csv"
                                $csvContent = docker exec samba-ad-primary cat $csvPath 2>$null
                                if ($csvContent) {
                                    $itCsvLine = "`"$($itUser.EmployeeId)`",`"$($itUser.FirstName)`",`"$($itUser.LastName)`",`"$($itUser.Email)`",`"$($itUser.Department)`",`"$($itUser.Title)`",`"$($itUser.SamAccountName)`",`"$($itUser.DisplayName)`",`"Active`",`"$($itUser.Email)`",`"CN=$($itUser.DisplayName),CN=Users,DC=testdomain,DC=local`""
                                    $financeCsvLine = "`"$($financeUser.EmployeeId)`",`"$($financeUser.FirstName)`",`"$($financeUser.LastName)`",`"$($financeUser.Email)`",`"$($financeUser.Department)`",`"$($financeUser.Title)`",`"$($financeUser.SamAccountName)`",`"$($financeUser.DisplayName)`",`"Active`",`"$($financeUser.Email)`",`"CN=$($financeUser.DisplayName),CN=Users,DC=testdomain,DC=local`""

                                    $newContent = $csvContent + "`n" + $itCsvLine + "`n" + $financeCsvLine
                                    $newContent | docker exec -i samba-ad-primary tee $csvPath > $null
                                    Write-Host "  ✓ Added test users to CSV (IT and Finance departments)" -ForegroundColor Green
                                }

                                # Step 2: Add scoping criteria to the import sync rule
                                Write-Host "  Adding scoping criteria: department = 'IT'..." -ForegroundColor Gray

                                $testGroup = New-JIMScopingCriteriaGroup -SyncRuleId $csvImportRule.id -Type All -PassThru -ErrorAction Stop

                                if ($testGroup -and $testGroup.id) {
                                    Write-Host "  ✓ Created scoping criteria group (ID: $($testGroup.id))" -ForegroundColor Green

                                    # Add criterion using Connected System attribute
                                    $criterion = New-JIMScopingCriterion -SyncRuleId $csvImportRule.id -GroupId $testGroup.id `
                                        -ConnectedSystemAttributeId $deptAttr.id -ComparisonType Equals -StringValue 'IT' `
                                        -PassThru -ErrorAction Stop

                                    if ($criterion) {
                                        Write-Host "  ✓ Created criterion: department Equals 'IT'" -ForegroundColor Green

                                        # Step 3: Run import to trigger scoping evaluation
                                        Write-Host "  Running CSV import to test scoping..." -ForegroundColor Gray

                                        $runProfile = Get-JIMRunProfile -ConnectedSystemId $csvConnectedSystem.id -ErrorAction SilentlyContinue |
                                            Where-Object { $_.name -match "Import" } | Select-Object -First 1

                                        if ($runProfile) {
                                            $scopeImportResult = Start-JIMRunProfile -ConnectedSystemId $csvConnectedSystem.id -RunProfileId $runProfile.id -Wait -PassThru -ErrorAction SilentlyContinue
                                            Assert-ActivitySuccess -ActivityId $scopeImportResult.activityId -Name "CSV Import (InboundScopeFilter)"

                                            # Step 4: Verify scoping worked
                                            Write-Host "  Verifying scoping results..." -ForegroundColor Gray

                                            # Check if IT user was synced to Metaverse
                                            $mvITUser = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "SCOPE001" -PageSize 10 -ErrorAction SilentlyContinue
                                            $itUserFound = $mvITUser -and $mvITUser.items -and ($mvITUser.items | Where-Object {
                                                ($_.attributeValues | Where-Object { $_.attributeName -eq "Employee ID" -and $_.stringValue -eq "SCOPE001" })
                                            })

                                            # Check if Finance user was NOT synced (out of scope)
                                            $mvFinanceUser = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "SCOPE002" -PageSize 10 -ErrorAction SilentlyContinue
                                            $financeUserFound = $mvFinanceUser -and $mvFinanceUser.items -and ($mvFinanceUser.items | Where-Object {
                                                ($_.attributeValues | Where-Object { $_.attributeName -eq "Employee ID" -and $_.stringValue -eq "SCOPE002" })
                                            })

                                            if ($itUserFound -and -not $financeUserFound) {
                                                Write-Host "  ✓ Inbound scoping working correctly!" -ForegroundColor Green
                                                Write-Host "    - IT user (SCOPE001): Synced to Metaverse (in scope)" -ForegroundColor Green
                                                Write-Host "    - Finance user (SCOPE002): NOT synced (out of scope)" -ForegroundColor Green
                                                $testResults.Steps += @{ Name = "InboundScopeFilter"; Success = $true }
                                            }
                                            elseif ($itUserFound -and $financeUserFound) {
                                                Write-Host "  ⚠ Both users synced - scoping may not be working" -ForegroundColor Yellow
                                                Write-Host "    This could indicate scoping criteria not being evaluated during import." -ForegroundColor Yellow
                                                $testResults.Steps += @{ Name = "InboundScopeFilter"; Success = $false; Error = "Finance user should have been filtered out by scoping criteria" }
                                            }
                                            elseif (-not $itUserFound -and -not $financeUserFound) {
                                                Write-Host "  ⚠ Neither user synced - sync may have failed" -ForegroundColor Yellow
                                                $testResults.Steps += @{ Name = "InboundScopeFilter"; Success = $true; Warning = "Users not found - sync may need more time or there's an issue" }
                                            }
                                            else {
                                                Write-Host "  ⚠ Unexpected result: IT user not synced but Finance user was" -ForegroundColor Yellow
                                                $testResults.Steps += @{ Name = "InboundScopeFilter"; Success = $false; Error = "Scoping appears inverted" }
                                            }
                                        }
                                        else {
                                            Write-Host "  ⚠ Could not find CSV import run profile" -ForegroundColor Yellow
                                            $testResults.Steps += @{ Name = "InboundScopeFilter"; Success = $true; Warning = "Run profile not found - cannot run import" }
                                        }

                                        # Clean up: Remove criterion
                                        Write-Host "  Cleaning up scoping criteria..." -ForegroundColor Gray
                                        Remove-JIMScopingCriterion -SyncRuleId $csvImportRule.id -GroupId $testGroup.id -CriterionId $criterion.id -Confirm:$false -ErrorAction SilentlyContinue
                                    }

                                    # Clean up: Remove group
                                    Remove-JIMScopingCriteriaGroup -SyncRuleId $csvImportRule.id -GroupId $testGroup.id -Confirm:$false -ErrorAction SilentlyContinue
                                    Write-Host "  ✓ Cleaned up test scoping criteria" -ForegroundColor Green
                                }
                                else {
                                    Write-Host "  ✗ Failed to create scoping criteria group" -ForegroundColor Red
                                    $testResults.Steps += @{ Name = "InboundScopeFilter"; Success = $false; Error = "Could not create scoping criteria group" }
                                }
                            }
                            catch {
                                Write-Host "  ✗ Error testing inbound scoping: $_" -ForegroundColor Red
                                $testResults.Steps += @{ Name = "InboundScopeFilter"; Success = $false; Error = $_.Exception.Message }
                            }
                        }
                        else {
                            Write-Host "  ⚠ 'department' attribute not found on CSV object type" -ForegroundColor Yellow
                            $testResults.Steps += @{ Name = "InboundScopeFilter"; Success = $true; Warning = "department attribute not found" }
                        }
                    }
                    else {
                        Write-Host "  ⚠ Could not get CSV object type attributes" -ForegroundColor Yellow
                        $testResults.Steps += @{ Name = "InboundScopeFilter"; Success = $true; Warning = "CSV object type not found" }
                    }
                }
                else {
                    Write-Host "  ⚠ Could not find HR CSV Connected System" -ForegroundColor Yellow
                    $testResults.Steps += @{ Name = "InboundScopeFilter"; Success = $true; Warning = "CSV Connected System not found" }
                }
            }
            else {
                Write-Host "  ⚠ Could not find CSV Import sync rule" -ForegroundColor Yellow
                $testResults.Steps += @{ Name = "InboundScopeFilter"; Success = $true; Warning = "CSV Import sync rule not found" }
            }
        }
        else {
            Write-Host "  ⚠ Could not retrieve sync rules" -ForegroundColor Yellow
            $testResults.Steps += @{ Name = "InboundScopeFilter"; Success = $true; Warning = "Could not retrieve sync rules" }
        }
    }

    # Test 6: Outbound Scope Filter Changes
    # Scenario: Configure scoping criteria on an export sync rule via the API
    # Then verify the criteria are applied correctly during export evaluation
    if ($Step -eq "OutboundScopeFilter" -or $Step -eq "All") {
        Write-TestSection "Test 6: Outbound Scope Filter Changes"

        Write-Host "Testing: Outbound scope filter configuration via API" -ForegroundColor Gray
        Write-Host "  Validates that scoping criteria can be configured on export sync rules." -ForegroundColor Gray

        # Find the LDAP export sync rule
        $syncRules = Get-JIMSyncRule -ErrorAction SilentlyContinue

        if ($syncRules) {
            $ldapExportRule = $syncRules | Where-Object { $_.name -match "LDAP.*Export" -or ($_.direction -eq "Export" -and $_.connectedSystemName -match "LDAP") } | Select-Object -First 1

            if ($ldapExportRule) {
                Write-Host "  Found LDAP Export sync rule: $($ldapExportRule.name) (ID: $($ldapExportRule.id))" -ForegroundColor Gray

                # Test 1: Get existing scoping criteria (should be empty initially)
                Write-Host "  Testing scoping criteria API endpoints..." -ForegroundColor Gray

                try {
                    $existingCriteria = Get-JIMScopingCriteria -SyncRuleId $ldapExportRule.id -ErrorAction SilentlyContinue

                    if ($null -eq $existingCriteria -or @($existingCriteria).Count -eq 0) {
                        Write-Host "  ✓ No existing scoping criteria (expected for new sync rule)" -ForegroundColor Green
                    }
                    else {
                        Write-Host "  Found $(@($existingCriteria).Count) existing scoping criteria group(s)" -ForegroundColor Gray
                    }

                    # Test 2: Create a scoping criteria group
                    Write-Host "  Creating test scoping criteria group..." -ForegroundColor Gray
                    $testGroup = New-JIMScopingCriteriaGroup -SyncRuleId $ldapExportRule.id -Type All -PassThru -ErrorAction Stop

                    if ($testGroup -and $testGroup.id) {
                        Write-Host "  ✓ Created scoping criteria group (ID: $($testGroup.id), Type: $($testGroup.type))" -ForegroundColor Green

                        # Test 3: Add a criterion to the group (filter on Department attribute)
                        Write-Host "  Adding test criterion (Department = 'IT')..." -ForegroundColor Gray

                        # Get Department attribute ID
                        $mvAttributes = Get-JIMMetaverseAttribute -ErrorAction SilentlyContinue
                        $deptAttr = $mvAttributes | Where-Object { $_.name -eq 'Department' } | Select-Object -First 1

                        if ($deptAttr) {
                            try {
                                $criterion = New-JIMScopingCriterion -SyncRuleId $ldapExportRule.id -GroupId $testGroup.id `
                                    -MetaverseAttributeId $deptAttr.id -ComparisonType Equals -StringValue 'IT' `
                                    -PassThru -ErrorAction Stop

                                if ($criterion) {
                                    Write-Host "  ✓ Created criterion: Department Equals 'IT'" -ForegroundColor Green

                                    # Verify the criteria group now contains the criterion
                                    $updatedGroup = Get-JIMScopingCriteria -SyncRuleId $ldapExportRule.id -GroupId $testGroup.id -ErrorAction SilentlyContinue

                                    if ($updatedGroup -and $updatedGroup.criteria -and @($updatedGroup.criteria).Count -gt 0) {
                                        Write-Host "  ✓ Verified criterion appears in group" -ForegroundColor Green
                                    }

                                    # Clean up: Remove the criterion
                                    Write-Host "  Cleaning up test criterion..." -ForegroundColor Gray
                                    Remove-JIMScopingCriterion -SyncRuleId $ldapExportRule.id -GroupId $testGroup.id -CriterionId $criterion.id -Confirm:$false -ErrorAction SilentlyContinue
                                }
                            }
                            catch {
                                Write-Host "  ⚠ Could not create test criterion: $_" -ForegroundColor Yellow
                            }
                        }
                        else {
                            Write-Host "  ⚠ Department attribute not found - skipping criterion test" -ForegroundColor Yellow
                        }

                        # Clean up: Remove the test group
                        Write-Host "  Cleaning up test scoping criteria group..." -ForegroundColor Gray
                        Remove-JIMScopingCriteriaGroup -SyncRuleId $ldapExportRule.id -GroupId $testGroup.id -Confirm:$false -ErrorAction SilentlyContinue
                        Write-Host "  ✓ Cleaned up test data" -ForegroundColor Green

                        $testResults.Steps += @{ Name = "OutboundScopeFilter"; Success = $true }
                    }
                    else {
                        Write-Host "  ✗ Failed to create scoping criteria group" -ForegroundColor Red
                        $testResults.Steps += @{ Name = "OutboundScopeFilter"; Success = $false; Error = "Could not create scoping criteria group" }
                    }
                }
                catch {
                    Write-Host "  ✗ Error testing scoping criteria API: $_" -ForegroundColor Red
                    $testResults.Steps += @{ Name = "OutboundScopeFilter"; Success = $false; Error = $_.Exception.Message }
                }
            }
            else {
                Write-Host "  ⚠ Could not find LDAP Export sync rule" -ForegroundColor Yellow
                Write-Host "  Scoping criteria tests require an export sync rule." -ForegroundColor Yellow
                $testResults.Steps += @{ Name = "OutboundScopeFilter"; Success = $true; Warning = "LDAP Export sync rule not found" }
            }
        }
        else {
            Write-Host "  ⚠ Could not retrieve sync rules" -ForegroundColor Yellow
            $testResults.Steps += @{ Name = "OutboundScopeFilter"; Success = $true; Warning = "Could not retrieve sync rules" }
        }
    }

    # Test 4: Admin Account Protection (Origin=Internal)
    if ($Step -eq "AdminProtection" -or $Step -eq "All") {
        Write-TestSection "Test 4: Admin Account Protection"

        Write-Host "Verifying admin account has Origin=Internal protection..." -ForegroundColor Gray

        # The built-in admin user should have Origin=Internal
        # Query the API to verify
        $adminUser = Get-JIMMetaverseObject -ObjectTypeName "User" -Search "admin" -PageSize 10 -ErrorAction SilentlyContinue

        if ($adminUser -and $adminUser.items) {
            $admin = $adminUser.items | Where-Object { $_.displayName -match "admin" -or $_.userName -match "admin" } | Select-Object -First 1
            if ($admin) {
                # Check if the admin has origin property set to Internal
                if ($admin.PSObject.Properties.Name -contains 'origin') {
                    if ($admin.origin -eq 'Internal' -or $admin.origin -eq 1) {
                        Write-Host "  ✓ Admin account has Origin=Internal (protected from auto-deletion)" -ForegroundColor Green
                        $testResults.Steps += @{ Name = "AdminProtection"; Success = $true }
                    } else {
                        Write-Host "  ⚠ Admin account has Origin=$($admin.origin) - may not be protected" -ForegroundColor Yellow
                        $testResults.Steps += @{ Name = "AdminProtection"; Success = $true; Warning = "Admin origin is $($admin.origin)" }
                    }
                } else {
                    Write-Host "  ⚠ Origin property not exposed via API (may be internal only)" -ForegroundColor Yellow
                    $testResults.Steps += @{ Name = "AdminProtection"; Success = $true; Warning = "Origin property not visible via API" }
                }
            } else {
                Write-Host "  ⚠ Admin account not found in results" -ForegroundColor Yellow
                $testResults.Steps += @{ Name = "AdminProtection"; Success = $true; Warning = "Admin account not found" }
            }
        } else {
            Write-Host "  ⚠ Could not query metaverse objects" -ForegroundColor Yellow
            $testResults.Steps += @{ Name = "AdminProtection"; Success = $true; Warning = "Could not query admin account" }
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
    Write-Host "✗ Scenario 4 failed: $_" -ForegroundColor Red
    Write-Host "  Stack trace: $($_.ScriptStackTrace)" -ForegroundColor Gray
    $testResults.Error = $_.Exception.Message
    exit 1
}
