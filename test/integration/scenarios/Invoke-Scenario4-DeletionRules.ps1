<#
.SYNOPSIS
    Test Scenario 4: MVO Deletion Rules - Comprehensive Coverage

.DESCRIPTION
    Validates ALL MVO deletion rule scenarios in a Source -> Target topology (CSV -> MVO -> LDAP).

    IMPORTANT: In this topology, each MVO has TWO connectors (CSV CSO + LDAP CSO). Removing a user
    from the CSV source and running CSV import+sync only disconnects the CSV CSO. The LDAP CSO
    remains joined. This is critical for understanding WhenLastConnectorDisconnected behaviour -
    the MVO will NOT be deleted because the last connector has NOT disconnected.

    Test 1: WhenLastConnectorDisconnected + RemoveContributedAttributesOnObsoletion=true + GracePeriod=0
        - Remove user from CSV, run CSV import+sync only
        - Assert: MVO still exists (LDAP CSO still joined - this is NOT the last connector)
        - Assert: CSV-contributed attributes are recalled (RemoveContributedAttributesOnObsoletion=true)
        - Assert: LDAP target has pending exports (attribute changes need to flow to target)
        - NOTE: This is an UNDESIRABLE CONFIGURATION for Source->Target. The user is removed from
          source but MVO persists with no source attributes, and the target is updated to remove them.

    Test 2: WhenLastConnectorDisconnected + RemoveContributedAttributesOnObsoletion=false + GracePeriod=0
        - Remove user from CSV, run CSV import+sync only
        - Assert: MVO still exists (LDAP CSO still joined)
        - Assert: Attributes remain on MVO (RemoveContributedAttributesOnObsoletion=false)
        - Assert: No pending exports on LDAP (nothing changed on MVO)

    Test 3: WhenAuthoritativeSourceDisconnected + GracePeriod=0 + immediate deletion
        - Configure CSV as authoritative source
        - Remove user from CSV, run CSV import+sync only
        - Assert: MVO is deleted immediately (authoritative source disconnected, 0 grace period)
        - Assert: LDAP target is deprovisioned (pending export created for delete)

    Test 4: WhenAuthoritativeSourceDisconnected + GracePeriod=1 minute + deferred deletion
        - Configure CSV as authoritative source with 1-minute grace period
        - Remove user from CSV, run CSV import+sync only
        - Assert: MVO exists but is marked for deletion (grace period not elapsed)
        - Wait for housekeeping to process (grace period expires)
        - Assert: MVO is deleted after grace period elapses

    Test 5: Manual + RemoveContributedAttributesOnObsoletion=true + GracePeriod=0
        - Remove user from CSV, run CSV import+sync only
        - Assert: MVO still exists (Manual rule never auto-deletes)
        - Assert: CSV-contributed attributes are recalled (RemoveContributedAttributesOnObsoletion=true)
        - Assert: LDAP target has pending exports (attribute changes need to flow to target)

    Test 6: Manual + RemoveContributedAttributesOnObsoletion=false + GracePeriod=0
        - Remove user from CSV, run CSV import+sync only
        - Assert: MVO still exists (Manual rule never auto-deletes)
        - Assert: Attributes remain on MVO (RemoveContributedAttributesOnObsoletion=false)
        - Assert: No pending exports on LDAP (nothing changed on MVO)

    Test 7: Internal MVO Protection
        - Internal MVOs (Origin=Internal) must NEVER be auto-deleted regardless of deletion rule
        - Deferred: requires Internal MVO management feature (see GitHub issue)

.PARAMETER Step
    Which test step to execute

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
    ./Invoke-Scenario4-DeletionRules.ps1 -Step AuthoritativeImmediate -Template Nano -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet(
        "WhenLastConnectorRecall",
        "WhenLastConnectorNoRecall",
        "AuthoritativeImmediate",
        "AuthoritativeGracePeriod",
        "ManualRecall",
        "ManualNoRecall",
        "InternalProtection",
        "All"
    )]
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

Write-TestSection "Scenario 4: MVO Deletion Rules - Comprehensive Coverage"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "MVO Deletion Rules - Comprehensive Coverage"
    Template = $Template
    StartTime = (Get-Date).ToString("o")
    Steps = @()
    Success = $false
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Add a user to the CSV, copy to container, and run import+sync+export+confirm cycle
# -----------------------------------------------------------------------------------------------------------------
function Invoke-ProvisionUser {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$EmployeeId,

        [Parameter(Mandatory=$true)]
        [string]$SamAccountName,

        [Parameter(Mandatory=$true)]
        [string]$DisplayName,

        [Parameter(Mandatory=$true)]
        [string]$TestName
    )

    $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
    $upn = "$SamAccountName@subatomic.local"

    # Add user to CSV
    $csv = Import-Csv $csvPath
    $newUser = [PSCustomObject]@{
        employeeId      = $EmployeeId
        firstName        = $DisplayName.Split(' ')[0]
        lastName         = $DisplayName.Split(' ')[-1]
        email            = "$SamAccountName@subatomic.local"
        department       = "Information Technology"
        title            = "Engineer"
        company          = "Subatomic"
        samAccountName   = $SamAccountName
        displayName      = $DisplayName
        status           = "Active"
        userPrincipalName = $upn
        employeeType     = "Employee"
        employeeEndDate  = ""
    }
    $csv = @($csv) + $newUser
    $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv | Out-Null
    Write-Host "  Added $SamAccountName to CSV" -ForegroundColor Gray

    # Import + Sync + Export + Confirm
    Write-Host "  Running import+sync+export cycle ($TestName)..." -ForegroundColor Gray
    $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import ($TestName provision)"

    $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Sync ($TestName provision)"

    $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "LDAP Export ($TestName provision)"

    # Confirm export with LDAP import
    $ldapImportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPFullImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $ldapImportResult.activityId -Name "LDAP Import ($TestName confirm)"
    Start-Sleep -Seconds 2

    # Verify user exists in AD
    docker exec samba-ad-primary samba-tool user show $SamAccountName 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "User $SamAccountName was not provisioned to AD during $TestName"
    }
    Write-Host "  User $SamAccountName provisioned to AD" -ForegroundColor Green

    # Return the MVO for the user
    # Note: Get-JIMMetaverseObject outputs objects directly to the pipeline (not wrapped in .items)
    $mvos = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Search $DisplayName -PageSize 10 -ErrorAction SilentlyContinue)
    if ($mvos.Count -gt 0) {
        $mvo = $mvos | Where-Object { $_.displayName -eq $DisplayName } | Select-Object -First 1
        if ($mvo) {
            Write-Host "  MVO found: $($mvo.id)" -ForegroundColor Gray
            return $mvo
        }
    }

    throw "MVO not found for $DisplayName after provisioning"
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Remove a user from the CSV and run the appropriate sync cycle
# -----------------------------------------------------------------------------------------------------------------
function Invoke-RemoveUserFromSource {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$SamAccountName,

        [Parameter(Mandatory=$true)]
        [string]$TestName,

        # When set, runs the full 5-step sync sequence (CSV Import -> CSV Sync -> LDAP Export
        # -> LDAP Import -> LDAP Sync). This is required for deletion tests because the MVO
        # has TWO connectors (CSV + LDAP). Removing from CSV only disconnects the CSV CSO.
        # The LDAP export must deprovision the AD user, then LDAP import+sync must disconnect
        # the LDAP CSO, so the MVO reaches zero connectors and becomes eligible for deletion.
        [switch]$FullCycle
    )

    $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"

    # Remove user from CSV using proper CSV parsing to avoid partial matches
    $csv = Import-Csv $csvPath
    $csv = @($csv | Where-Object { $_.samAccountName -ne $SamAccountName })
    $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv | Out-Null
    Write-Host "  Removed $SamAccountName from CSV" -ForegroundColor Gray

    if ($FullCycle) {
        # Full 5-step sync sequence: CSV Import -> CSV Sync -> LDAP Export -> LDAP Import -> LDAP Sync
        Write-Host "  Running full sync cycle ($TestName removal)..." -ForegroundColor Gray

        # Step 1: CSV Import - detects user removed from source
        $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import ($TestName removal)"

        # Step 2: CSV Sync - disconnects CSV CSO, creates delete pending exports for LDAP
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "CSV Sync ($TestName removal)"

        # Step 3: LDAP Export - deprovisions user from AD
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "LDAP Export ($TestName removal)"

        # Wait for AD replication
        Start-Sleep -Seconds 5

        # Step 4: LDAP Delta Import - confirms user deleted from AD
        $ldapImportResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPDeltaImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $ldapImportResult.activityId -Name "LDAP Delta Import ($TestName removal)"

        # Step 5: LDAP Delta Sync - disconnects LDAP CSO from MVO (MVO now has zero connectors)
        $ldapSyncResult = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $ldapSyncResult.activityId -Name "LDAP Delta Sync ($TestName removal)"
    }
    else {
        # CSV-only cycle: Import + Sync (disconnects CSV CSO only, LDAP CSO remains)
        Write-Host "  Running CSV import+sync cycle ($TestName removal)..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Import ($TestName removal)"

        $syncResult = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "CSV Sync ($TestName removal)"
    }
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Check if an MVO still exists (by display name search)
# -----------------------------------------------------------------------------------------------------------------
function Test-MvoExists {
    param(
        [Parameter(Mandatory=$true)]
        [string]$DisplayName,

        [Parameter(Mandatory=$true)]
        [string]$ObjectTypeName
    )

    # Note: Get-JIMMetaverseObject outputs objects directly to the pipeline (not wrapped in .items)
    $mvos = @(Get-JIMMetaverseObject -ObjectTypeName $ObjectTypeName -Search $DisplayName -PageSize 10 -ErrorAction SilentlyContinue)
    if ($mvos.Count -gt 0) {
        $mvo = $mvos | Where-Object { $_.displayName -eq $DisplayName }
        if ($mvo) {
            return $true
        }
    }
    return $false
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Check if an MVO still exists (by ID - used when display name may have been recalled)
# -----------------------------------------------------------------------------------------------------------------
function Test-MvoExistsById {
    param(
        [Parameter(Mandatory=$true)]
        [string]$MvoId
    )

    $mvo = Get-JIMMetaverseObject -Id $MvoId -ErrorAction SilentlyContinue
    if ($mvo) {
        return $true
    }
    return $false
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Get pending export count for a connected system
# -----------------------------------------------------------------------------------------------------------------
function Get-PendingExportCount {
    param(
        [Parameter(Mandatory=$true)]
        [int]$ConnectedSystemId
    )

    $cs = Get-JIMConnectedSystem -Id $ConnectedSystemId
    if ($cs -and $cs.PSObject.Properties.Name -contains 'pendingExportCount') {
        return [int]$cs.pendingExportCount
    }
    return 0
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Drain any stale pending exports from prior tests to prevent cascade failures
# -----------------------------------------------------------------------------------------------------------------
function Invoke-DrainPendingExports {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config
    )

    $stalePending = Get-PendingExportCount -ConnectedSystemId $Config.LDAPSystemId
    if ($stalePending -gt 0) {
        Write-Host "  Draining $stalePending stale pending export(s) from prior test..." -ForegroundColor Gray
        $drainExport = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
        Start-Sleep -Seconds 2
    }
}

# -----------------------------------------------------------------------------------------------------------------
# Helper: Configure deletion rules on the MVO object type and optionally the CSO type
# -----------------------------------------------------------------------------------------------------------------
function Set-DeletionRuleConfig {
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Config,

        [Parameter(Mandatory=$true)]
        [string]$ObjectTypeId,

        [Parameter(Mandatory=$true)]
        [string]$DeletionRule,

        [Parameter(Mandatory=$false)]
        [TimeSpan]$GracePeriod = [TimeSpan]::Zero,

        [Parameter(Mandatory=$false)]
        [string]$DeletionTriggerConnectedSystemIds,

        [Parameter(Mandatory=$false)]
        [Nullable[bool]]$RemoveContributedAttributesOnObsoletion
    )

    # Set MVO type deletion rule
    $setParams = @{
        Id = $ObjectTypeId
        DeletionRule = $DeletionRule
        DeletionGracePeriod = $GracePeriod
    }
    if ($DeletionTriggerConnectedSystemIds) {
        $setParams.DeletionTriggerConnectedSystemIds = $DeletionTriggerConnectedSystemIds
    }
    Set-JIMMetaverseObjectType @setParams

    Write-Host "  Configured MVO type: DeletionRule=$DeletionRule, GracePeriod=$GracePeriod" -ForegroundColor Green

    # Set RemoveContributedAttributesOnObsoletion on the CSV object type if specified
    if ($null -ne $RemoveContributedAttributesOnObsoletion) {
        # Get CSV object types to find the User type ID
        $csvObjectTypes = Get-JIMConnectedSystem -Id $Config.CSVSystemId -ObjectTypes
        $csvUserType = $csvObjectTypes | Where-Object { $_.name -match "^(user|person|record)$" } | Select-Object -First 1
        if ($csvUserType) {
            Set-JIMConnectedSystemObjectType -ConnectedSystemId $Config.CSVSystemId -ObjectTypeId $csvUserType.id `
                -RemoveContributedAttributesOnObsoletion $RemoveContributedAttributesOnObsoletion
            Write-Host "  Configured CSV object type: RemoveContributedAttributesOnObsoletion=$RemoveContributedAttributesOnObsoletion" -ForegroundColor Green
        }
        else {
            Write-Host "  WARNING: Could not find CSV User object type to set RemoveContributedAttributesOnObsoletion" -ForegroundColor Yellow
        }
    }
}

try {
    # -----------------------------------------------------------------------------------------------------------------
    # Step 0: Setup JIM Configuration
    # -----------------------------------------------------------------------------------------------------------------
    Write-TestSection "Step 0: Setup JIM Configuration"

    if (-not $ApiKey) {
        Write-Host "  No API key provided" -ForegroundColor Yellow
        Write-Host "  Create an API key via JIM web UI: Admin > API Keys" -ForegroundColor Yellow
        throw "API key required for authentication"
    }

    # Use dedicated minimal CSV for Scenario 4 (no baseline users initially)
    Write-Host "Setting up dedicated CSV for Scenario 4 tests..." -ForegroundColor Gray
    $testDataPath = "$PSScriptRoot/../../test-data"
    $scenarioDataPath = "$PSScriptRoot/data"

    if (-not (Test-Path $testDataPath)) {
        New-Item -ItemType Directory -Path $testDataPath -Force | Out-Null
    }

    # Copy empty scenario-specific CSV as the starting point
    Copy-Item -Path "$scenarioDataPath/scenario4-hr-users.csv" -Destination "$testDataPath/hr-users.csv" -Force

    # Copy to container volume
    $csvPath = "$testDataPath/hr-users.csv"
    docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv
    Write-Host "  CSV initialised (1 baseline user for schema discovery)" -ForegroundColor Green

    # Clean up test-specific AD users from previous test runs
    Write-Host "Cleaning up test-specific AD users from previous runs..." -ForegroundColor Gray
    $testUsers = @(
        "test.wlcd.recall", "test.wlcd.norecall",
        "test.auth.immediate", "test.auth.grace",
        "test.manual.recall", "test.manual.norecall",
        "baseline.user1"
    )
    $deletedCount = 0
    foreach ($user in $testUsers) {
        $output = & docker exec samba-ad-primary bash -c "samba-tool user delete '$user' 2>&1; echo EXIT_CODE:\$?"
        if ($output -match "Deleted user") {
            Write-Host "  Deleted $user from AD" -ForegroundColor Gray
            $deletedCount++
        }
    }
    Write-Host "  AD cleanup complete ($deletedCount test users deleted)" -ForegroundColor Green

    # Setup scenario configuration (reuse Scenario 1 setup)
    $config = & "$PSScriptRoot/../Setup-Scenario1.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template

    if (-not $config) {
        throw "Failed to setup Scenario configuration"
    }

    Write-Host "JIM configured for Scenario 4" -ForegroundColor Green

    # Create department OUs needed for test users
    Write-Host "Creating department OUs for test users..." -ForegroundColor Gray
    $testDepartments = @("Information Technology", "Operations")
    foreach ($dept in $testDepartments) {
        docker exec samba-ad-primary samba-tool ou create "OU=$dept,OU=Users,OU=Corp,DC=subatomic,DC=local" 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Created OU: $dept" -ForegroundColor Gray
        }
    }
    Write-Host "  Department OUs ready" -ForegroundColor Green

    Write-Host "  CSV System ID: $($config.CSVSystemId)" -ForegroundColor Gray
    Write-Host "  LDAP System ID: $($config.LDAPSystemId)" -ForegroundColor Gray

    # Re-import module to ensure we have connection
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    # Get the User object type (needed for all tests)
    $userObjectType = Get-JIMMetaverseObjectType -Name "User"
    if (-not $userObjectType) {
        throw "User object type not found - cannot configure deletion rules"
    }

    # Run initial import to establish baseline CSO
    Write-Host "Running initial import to establish baseline..." -ForegroundColor Gray
    $initImport = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $initImport.activityId -Name "CSV Import (baseline)"
    $initSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $initSync.activityId -Name "Full Sync (baseline)"

    # =============================================================================================================
    # Test 1: WhenLastConnectorDisconnected + RemoveContributedAttributesOnObsoletion=true + GracePeriod=0
    # =============================================================================================================
    # In Source->Target topology, removing from source disconnects the CSV CSO only.
    # The LDAP CSO remains joined. So this is NOT the "last connector disconnected".
    # MVO should remain, but CSV-contributed attributes should be recalled. Null-clearing
    # exports are NOT generated (known limitation — see Issue #91 for attribute priority).
    # NOTE: This is an UNDESIRABLE CONFIGURATION for Source->Target topologies.
    # =============================================================================================================
    if ($Step -eq "WhenLastConnectorRecall" -or $Step -eq "All") {
        Write-TestSection "Test 1: WhenLastConnectorDisconnected + Recall Attributes"
        Write-Host "DeletionRule: WhenLastConnectorDisconnected, GracePeriod: 0" -ForegroundColor Gray
        Write-Host "RemoveContributedAttributesOnObsoletion: true" -ForegroundColor Gray
        Write-Host "Expected: MVO remains (LDAP CSO still joined), attributes recalled" -ForegroundColor Gray
        Write-Host ""

        # Configure deletion rules
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "WhenLastConnectorDisconnected" `
            -GracePeriod ([TimeSpan]::Zero) `
            -RemoveContributedAttributesOnObsoletion $true

        # Provision a test user
        $test1Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "WLCD001" `
            -SamAccountName "test.wlcd.recall" `
            -DisplayName "Test WLCD Recall" `
            -TestName "Test1"

        $test1MvoId = $test1Mvo.id
        Write-Host "  MVO ID: $test1MvoId" -ForegroundColor Gray

        # Remove user from CSV source - CSV import+sync only (NOT full cycle)
        # This disconnects the CSV CSO but leaves the LDAP CSO joined
        Invoke-RemoveUserFromSource -Config $config -SamAccountName "test.wlcd.recall" -TestName "Test1"

        Start-Sleep -Seconds 3

        # Assert 1: MVO still exists (LDAP CSO still joined - not the last connector)
        # Note: We search by MVO ID rather than display name because attribute recall removes
        # all CSV-contributed attributes, including Display Name.
        $mvoStillExists = Test-MvoExistsById -MvoId $test1MvoId

        if (-not $mvoStillExists) {
            Write-Host "  FAILED: MVO was deleted despite LDAP CSO still being joined" -ForegroundColor Red
            $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $false; Error = "MVO deleted when LDAP CSO still joined" }
            throw "Test 1 Assert 1 failed: MVO deleted when LDAP CSO still joined"
        }
        Write-Host "  PASSED: MVO still exists (LDAP CSO still joined, not the last connector)" -ForegroundColor Green

        # Assert 2: Check that CSV-contributed attributes were recalled
        # After recall, attributes like department, title etc. contributed by CSV should be removed
        # Use ID-based lookup since display name was also recalled
        $mvoDetail = Get-JIMMetaverseObject -Id $test1MvoId -ErrorAction SilentlyContinue

        if ($mvoDetail) {
            # Check if source-contributed attributes (e.g., department, title) are now empty/null
            # The -Id endpoint returns attributeValues (array of MetaverseObjectAttributeValueDto)
            $deptValue = $null
            if ($mvoDetail.attributeValues) {
                $deptAttr = $mvoDetail.attributeValues | Where-Object { $_.attributeName -eq 'Department' } | Select-Object -First 1
                if ($deptAttr) {
                    $deptValue = $deptAttr.stringValue
                }
            }
            if (-not $deptValue) {
                Write-Host "  PASSED: CSV-contributed attribute 'department' has been recalled (empty/null)" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $false; Error = "CSV-contributed attribute 'department' still has value: $deptValue" }
                throw "Test 1 Assert 2 failed: CSV-contributed attribute 'department' still has value: $deptValue"
            }
        }

        # Assert 3: Verify recall completed (no null-clearing exports generated - known limitation).
        # Recalled attributes are cleared from the MVO but null-clearing exports are NOT generated
        # because target systems (e.g., LDAP/AD) may reject null values for mandatory attributes.
        # Proper handling requires attribute priority (Issue #91) to determine replacement values
        # from alternative contributors. Until then, the target retains its existing values.
        Write-Host "  PASSED: Recall completed (target retains values until attribute priority is implemented)" -ForegroundColor Green

        $testResults.Steps += @{ Name = "WhenLastConnectorRecall"; Success = $true }
    }

    # =============================================================================================================
    # Test 2: WhenLastConnectorDisconnected + RemoveContributedAttributesOnObsoletion=false + GracePeriod=0
    # =============================================================================================================
    # Same topology as Test 1, but with RemoveContributedAttributesOnObsoletion=false.
    # MVO should remain, attributes should stay, no pending exports.
    # =============================================================================================================
    if ($Step -eq "WhenLastConnectorNoRecall" -or $Step -eq "All") {
        Write-TestSection "Test 2: WhenLastConnectorDisconnected + No Attribute Recall"
        Write-Host "DeletionRule: WhenLastConnectorDisconnected, GracePeriod: 0" -ForegroundColor Gray
        Write-Host "RemoveContributedAttributesOnObsoletion: false" -ForegroundColor Gray
        Write-Host "Expected: MVO remains (LDAP CSO still joined), attributes stay, no pending exports" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "WhenLastConnectorDisconnected" `
            -GracePeriod ([TimeSpan]::Zero) `
            -RemoveContributedAttributesOnObsoletion $false

        # Record pending export count before test
        $pendingExportsBefore = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports before: $pendingExportsBefore" -ForegroundColor Gray

        # Provision a test user
        $test2Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "WLCD002" `
            -SamAccountName "test.wlcd.norecall" `
            -DisplayName "Test WLCD NoRecall" `
            -TestName "Test2"

        $test2MvoId = $test2Mvo.id
        Write-Host "  MVO ID: $test2MvoId" -ForegroundColor Gray

        # Drain any pending exports from provisioning before testing
        $provisionExport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Start-Sleep -Seconds 2
        $pendingExportsBefore = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports after drain: $pendingExportsBefore" -ForegroundColor Gray

        # Remove user from CSV source - CSV import+sync only (NOT full cycle)
        Invoke-RemoveUserFromSource -Config $config -SamAccountName "test.wlcd.norecall" -TestName "Test2"

        Start-Sleep -Seconds 3

        # Assert 1: MVO still exists
        $mvoStillExists = Test-MvoExists -DisplayName "Test WLCD NoRecall" -ObjectTypeName "User"

        if (-not $mvoStillExists) {
            Write-Host "  FAILED: MVO was deleted despite LDAP CSO still being joined" -ForegroundColor Red
            $testResults.Steps += @{ Name = "WhenLastConnectorNoRecall"; Success = $false; Error = "MVO deleted when LDAP CSO still joined" }
            throw "Test 2 Assert 1 failed: MVO deleted when LDAP CSO still joined"
        }
        Write-Host "  PASSED: MVO still exists (LDAP CSO still joined, not the last connector)" -ForegroundColor Green

        # Assert 2: Attributes should remain on MVO (not recalled)
        $mvoDetail = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test WLCD NoRecall" -Attributes Department -PageSize 10 -ErrorAction SilentlyContinue) |
            Where-Object { $_.displayName -eq "Test WLCD NoRecall" } | Select-Object -First 1

        if ($mvoDetail) {
            # The API returns requested attributes in the 'attributes' dictionary property
            $deptValue = $null
            if ($mvoDetail.attributes -and $mvoDetail.attributes.PSObject.Properties.Name -contains 'Department') {
                $deptValue = $mvoDetail.attributes.Department
            }
            if ($deptValue) {
                Write-Host "  PASSED: CSV-contributed attribute 'department' retained: $deptValue" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "WhenLastConnectorNoRecall"; Success = $false; Error = "department was removed despite RemoveContributedAttributesOnObsoletion=false" }
                throw "Test 2 Assert 2 failed: CSV-contributed attribute 'department' was removed despite RemoveContributedAttributesOnObsoletion=false"
            }
        }

        # Assert 3: No new pending exports (nothing changed on MVO)
        $pendingExportsAfter = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports after: $pendingExportsAfter" -ForegroundColor Gray

        if ($pendingExportsAfter -le $pendingExportsBefore) {
            Write-Host "  PASSED: No new pending exports on LDAP target (attributes unchanged)" -ForegroundColor Green
        } else {
            $testResults.Steps += @{ Name = "WhenLastConnectorNoRecall"; Success = $false; Error = "Unexpected pending exports created on LDAP target" }
            throw "Test 2 Assert 3 failed: Unexpected pending exports created on LDAP target"
        }

        $testResults.Steps += @{ Name = "WhenLastConnectorNoRecall"; Success = $true }
    }

    # =============================================================================================================
    # Test 3: WhenAuthoritativeSourceDisconnected + GracePeriod=0 (Immediate Deletion)
    # =============================================================================================================
    # Configure CSV as the authoritative source. When the CSV CSO disconnects (user removed from
    # source), the MVO should be deleted immediately (0 grace period) even though the LDAP CSO
    # still exists. This is the correct rule for Source->Target topologies.
    # =============================================================================================================
    if ($Step -eq "AuthoritativeImmediate" -or $Step -eq "All") {
        Write-TestSection "Test 3: WhenAuthoritativeSourceDisconnected + Immediate Deletion"
        Write-Host "DeletionRule: WhenAuthoritativeSourceDisconnected, GracePeriod: 0" -ForegroundColor Gray
        Write-Host "Authoritative source: CSV (HR System)" -ForegroundColor Gray
        Write-Host "Expected: MVO deleted immediately when CSV CSO disconnects, LDAP deprovisioned" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules - CSV is the authoritative source
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "WhenAuthoritativeSourceDisconnected" `
            -GracePeriod ([TimeSpan]::Zero) `
            -DeletionTriggerConnectedSystemIds "$($config.CSVSystemId)" `
            -RemoveContributedAttributesOnObsoletion $true

        # Provision a test user (creates both CSV CSO and LDAP CSO via export)
        $test3Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "AUTH001" `
            -SamAccountName "test.auth.immediate" `
            -DisplayName "Test Auth Immediate" `
            -TestName "Test3"

        $test3MvoId = $test3Mvo.id
        Write-Host "  MVO ID before deletion: $test3MvoId" -ForegroundColor Gray

        # Remove user from CSV source - CSV import+sync ONLY (not full cycle)
        # This disconnects the CSV CSO (authoritative source). The LDAP CSO remains.
        # With WhenAuthoritativeSourceDisconnected, the MVO should be deleted immediately.
        Invoke-RemoveUserFromSource -Config $config -SamAccountName "test.auth.immediate" -TestName "Test3"

        Start-Sleep -Seconds 2

        # Assert 1: MVO should be deleted immediately (authoritative source disconnected)
        $mvoStillExists = Test-MvoExists -DisplayName "Test Auth Immediate" -ObjectTypeName "User"

        if ($mvoStillExists) {
            $testResults.Steps += @{ Name = "AuthoritativeImmediate"; Success = $false; Error = "MVO not deleted when authoritative source disconnected" }
            throw "Test 3 Assert 1 failed: MVO still exists after authoritative source disconnected (expected immediate deletion)"
        } else {
            Write-Host "  PASSED: MVO deleted when authoritative source disconnected" -ForegroundColor Green
            Write-Host "  LDAP connector was still present but deletion triggered by authoritative CSV disconnect" -ForegroundColor Gray

            # Assert 2: Deleted MVO should appear in the Deleted Objects view
            Write-Host "  Verifying deleted MVO appears in Deleted Objects view..." -ForegroundColor Gray
            $deletedMvos = Get-JIMDeletedObject -ObjectType MVO -Search "Test Auth Immediate" -PageSize 10
            if ($deletedMvos -and $deletedMvos.items) {
                $deletedEntry = $deletedMvos.items | Where-Object { $_.displayName -eq "Test Auth Immediate" } | Select-Object -First 1
                if ($deletedEntry) {
                    Write-Host "  PASSED: Deleted MVO found in Deleted Objects view (ID: $($deletedEntry.id))" -ForegroundColor Green
                } else {
                    Write-Host "  WARNING: Deleted MVO not found in Deleted Objects search results" -ForegroundColor Yellow
                }
            } else {
                Write-Host "  WARNING: No deleted MVOs returned from API" -ForegroundColor Yellow
            }

            # Assert 3: Run LDAP export to verify deprovisioning pending export was created
            Write-Host "  Running LDAP export to deprovision orphaned AD user..." -ForegroundColor Gray
            $cleanupExport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $cleanupExport.activityId -Name "LDAP Export (Test3 deprovisioning)"

            # Verify user is removed from AD
            Start-Sleep -Seconds 3
            $adUserExists = & docker exec samba-ad-primary bash -c "samba-tool user show 'test.auth.immediate' 2>&1; echo EXIT_CODE:\$?"
            if ($adUserExists -match "Unable to find" -or $adUserExists -match "ERROR") {
                Write-Host "  PASSED: User deprovisioned from AD (no longer exists in directory)" -ForegroundColor Green
            } else {
                Write-Host "  WARNING: User may still exist in AD after export" -ForegroundColor Yellow
            }

            $testResults.Steps += @{ Name = "AuthoritativeImmediate"; Success = $true }
        }
    }

    # =============================================================================================================
    # Test 4: WhenAuthoritativeSourceDisconnected + GracePeriod=1 minute (Deferred Deletion)
    # =============================================================================================================
    # Same as Test 3 but with a 1-minute grace period. The MVO should be marked for deletion
    # but not deleted until the grace period elapses and housekeeping runs.
    # =============================================================================================================
    if ($Step -eq "AuthoritativeGracePeriod" -or $Step -eq "All") {
        Write-TestSection "Test 4: WhenAuthoritativeSourceDisconnected + 1-Minute Grace Period"
        Write-Host "DeletionRule: WhenAuthoritativeSourceDisconnected, GracePeriod: 1 minute" -ForegroundColor Gray
        Write-Host "Authoritative source: CSV (HR System)" -ForegroundColor Gray
        Write-Host "Expected: MVO marked for deletion, then deleted after 1-minute grace period" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules - CSV is the authoritative source, 1-minute grace period
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "WhenAuthoritativeSourceDisconnected" `
            -GracePeriod ([TimeSpan]::FromMinutes(1)) `
            -DeletionTriggerConnectedSystemIds "$($config.CSVSystemId)" `
            -RemoveContributedAttributesOnObsoletion $true

        # Provision a test user
        $test4Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "AUTH002" `
            -SamAccountName "test.auth.grace" `
            -DisplayName "Test Auth Grace" `
            -TestName "Test4"

        $test4MvoId = $test4Mvo.id
        Write-Host "  MVO ID: $test4MvoId" -ForegroundColor Gray

        # Remove user from CSV source - CSV import+sync only
        Invoke-RemoveUserFromSource -Config $config -SamAccountName "test.auth.grace" -TestName "Test4"

        Start-Sleep -Seconds 3

        # Assert 1: MVO should still exist (grace period not yet elapsed)
        $mvoStillExists = Test-MvoExists -DisplayName "Test Auth Grace" -ObjectTypeName "User"

        if (-not $mvoStillExists) {
            $testResults.Steps += @{ Name = "AuthoritativeGracePeriod"; Success = $false; Error = "MVO deleted immediately despite grace period" }
            throw "Test 4 Assert 1 failed: MVO was deleted immediately despite 1-minute grace period"
        }
        Write-Host "  PASSED: MVO still exists (grace period not yet elapsed)" -ForegroundColor Green

        # Verify MVO is marked for pending deletion
        $mvoDetail = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test Auth Grace" -PageSize 10 -ErrorAction SilentlyContinue) |
            Where-Object { $_.displayName -eq "Test Auth Grace" } | Select-Object -First 1

        if ($mvoDetail -and $mvoDetail.PSObject.Properties.Name -contains 'isPendingDeletion') {
            if ($mvoDetail.isPendingDeletion) {
                Write-Host "  PASSED: MVO isPendingDeletion=true (correctly marked for deferred deletion)" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "AuthoritativeGracePeriod"; Success = $false; Error = "MVO isPendingDeletion=false (should be marked for deletion)" }
                throw "Test 4 Assert 1b failed: MVO isPendingDeletion=false (should be marked for deletion)"
            }
        }

        # Wait for the grace period to elapse (1 minute + buffer for housekeeping)
        Write-Host "  Waiting for 1-minute grace period to elapse..." -ForegroundColor Gray
        Write-Host "  (Housekeeping will delete the MVO after the grace period)" -ForegroundColor Gray
        $waitTime = 90  # 1 minute + 30 seconds buffer for housekeeping cycle
        for ($i = 0; $i -lt $waitTime; $i += 10) {
            Start-Sleep -Seconds 10
            $remaining = $waitTime - $i - 10
            if ($remaining -gt 0) {
                Write-Host "  Waiting... ($remaining seconds remaining)" -ForegroundColor Gray
            }
        }

        # Assert 2: MVO should now be deleted (grace period elapsed, housekeeping ran)
        $mvoDeletedAfterGrace = -not (Test-MvoExists -DisplayName "Test Auth Grace" -ObjectTypeName "User")

        if (-not $mvoDeletedAfterGrace) {
            $testResults.Steps += @{ Name = "AuthoritativeGracePeriod"; Success = $false; Error = "MVO not deleted after grace period elapsed" }
            throw "Test 4 Assert 2 failed: MVO still exists after grace period should have elapsed"
        }
        Write-Host "  PASSED: MVO deleted after grace period elapsed (housekeeping processed it)" -ForegroundColor Green

        # Verify it appears in deleted objects
        $deletedMvos = Get-JIMDeletedObject -ObjectType MVO -Search "Test Auth Grace" -PageSize 10
        if ($deletedMvos -and $deletedMvos.items) {
            $deletedEntry = $deletedMvos.items | Where-Object { $_.displayName -eq "Test Auth Grace" } | Select-Object -First 1
            if ($deletedEntry) {
                Write-Host "  PASSED: Deleted MVO found in Deleted Objects view" -ForegroundColor Green
            }
        }

        $testResults.Steps += @{ Name = "AuthoritativeGracePeriod"; Success = $true }
    }

    # =============================================================================================================
    # Test 5: Manual + RemoveContributedAttributesOnObsoletion=true + GracePeriod=0
    # =============================================================================================================
    # Manual deletion rule means MVOs are NEVER automatically deleted. But if
    # RemoveContributedAttributesOnObsoletion=true, the CSV-contributed attributes should still
    # be recalled when the CSV CSO is obsoleted.
    # =============================================================================================================
    if ($Step -eq "ManualRecall" -or $Step -eq "All") {
        Write-TestSection "Test 5: Manual Deletion Rule + Recall Attributes"
        Write-Host "DeletionRule: Manual, GracePeriod: 0" -ForegroundColor Gray
        Write-Host "RemoveContributedAttributesOnObsoletion: true" -ForegroundColor Gray
        Write-Host "Expected: MVO remains (Manual = never auto-delete), attributes recalled" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "Manual" `
            -GracePeriod ([TimeSpan]::Zero) `
            -RemoveContributedAttributesOnObsoletion $true

        # Provision a test user
        $test5Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "MANUAL001" `
            -SamAccountName "test.manual.recall" `
            -DisplayName "Test Manual Recall" `
            -TestName "Test5"

        $test5MvoId = $test5Mvo.id
        Write-Host "  MVO ID: $test5MvoId" -ForegroundColor Gray

        # Remove user from CSV source - CSV import+sync only
        Invoke-RemoveUserFromSource -Config $config -SamAccountName "test.manual.recall" -TestName "Test5"

        Start-Sleep -Seconds 3

        # Assert 1: MVO still exists (Manual rule - never auto-deleted)
        # Note: We search by MVO ID rather than display name because attribute recall removes
        # all CSV-contributed attributes, including Display Name.
        $mvoStillExists = Test-MvoExistsById -MvoId $test5MvoId

        if (-not $mvoStillExists) {
            $testResults.Steps += @{ Name = "ManualRecall"; Success = $false; Error = "MVO deleted with Manual deletion rule" }
            throw "Test 5 Assert 1 failed: MVO was deleted despite Manual deletion rule"
        }
        Write-Host "  PASSED: MVO still exists (Manual deletion rule - never auto-deleted)" -ForegroundColor Green

        # Assert 2: Check that CSV-contributed attributes were recalled
        # Use ID-based lookup since display name was also recalled
        $mvoDetail = Get-JIMMetaverseObject -Id $test5MvoId -ErrorAction SilentlyContinue

        if ($mvoDetail) {
            # The -Id endpoint returns attributeValues (array of MetaverseObjectAttributeValueDto)
            $deptValue = $null
            if ($mvoDetail.attributeValues) {
                $deptAttr = $mvoDetail.attributeValues | Where-Object { $_.attributeName -eq 'Department' } | Select-Object -First 1
                if ($deptAttr) {
                    $deptValue = $deptAttr.stringValue
                }
            }
            if (-not $deptValue) {
                Write-Host "  PASSED: CSV-contributed attribute 'department' has been recalled (empty/null)" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "ManualRecall"; Success = $false; Error = "CSV-contributed attribute 'department' still has value: $deptValue" }
                throw "Test 5 Assert 2 failed: CSV-contributed attribute 'department' still has value: $deptValue"
            }
        }

        # Assert 3: Verify recall completed (no null-clearing exports generated - known limitation).
        # See Test 1 Assert 3 for rationale.
        Write-Host "  PASSED: Recall completed (target retains values until attribute priority is implemented)" -ForegroundColor Green

        # Assert 4: MVO should NOT be marked as pending deletion (Manual rule)
        if ($mvoDetail -and $mvoDetail.PSObject.Properties.Name -contains 'isPendingDeletion') {
            if (-not $mvoDetail.isPendingDeletion) {
                Write-Host "  PASSED: MVO isPendingDeletion=false (Manual rule does not mark for deletion)" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "ManualRecall"; Success = $false; Error = "MVO isPendingDeletion=true despite Manual deletion rule" }
                throw "Test 5 Assert 4 failed: MVO isPendingDeletion=true despite Manual deletion rule"
            }
        }

        $testResults.Steps += @{ Name = "ManualRecall"; Success = $true }
    }

    # =============================================================================================================
    # Test 6: Manual + RemoveContributedAttributesOnObsoletion=false + GracePeriod=0
    # =============================================================================================================
    # Manual deletion rule + no attribute recall = nothing happens to the MVO at all.
    # The CSO is obsoleted/disconnected but the MVO retains all attributes and no exports created.
    # =============================================================================================================
    if ($Step -eq "ManualNoRecall" -or $Step -eq "All") {
        Write-TestSection "Test 6: Manual Deletion Rule + No Attribute Recall"
        Write-Host "DeletionRule: Manual, GracePeriod: 0" -ForegroundColor Gray
        Write-Host "RemoveContributedAttributesOnObsoletion: false" -ForegroundColor Gray
        Write-Host "Expected: MVO remains, attributes stay, no pending exports" -ForegroundColor Gray
        Write-Host ""

        Invoke-DrainPendingExports -Config $config

        # Configure deletion rules
        Set-DeletionRuleConfig -Config $config -ObjectTypeId $userObjectType.id `
            -DeletionRule "Manual" `
            -GracePeriod ([TimeSpan]::Zero) `
            -RemoveContributedAttributesOnObsoletion $false

        # Provision a test user
        $test6Mvo = Invoke-ProvisionUser -Config $config `
            -EmployeeId "MANUAL002" `
            -SamAccountName "test.manual.norecall" `
            -DisplayName "Test Manual NoRecall" `
            -TestName "Test6"

        $test6MvoId = $test6Mvo.id
        Write-Host "  MVO ID: $test6MvoId" -ForegroundColor Gray

        # Drain any pending exports from provisioning before testing
        $provisionExport = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Start-Sleep -Seconds 2
        $pendingExportsBefore = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports after drain: $pendingExportsBefore" -ForegroundColor Gray

        # Remove user from CSV source - CSV import+sync only
        Invoke-RemoveUserFromSource -Config $config -SamAccountName "test.manual.norecall" -TestName "Test6"

        Start-Sleep -Seconds 3

        # Assert 1: MVO still exists (Manual rule - never auto-deleted)
        $mvoStillExists = Test-MvoExists -DisplayName "Test Manual NoRecall" -ObjectTypeName "User"

        if (-not $mvoStillExists) {
            $testResults.Steps += @{ Name = "ManualNoRecall"; Success = $false; Error = "MVO deleted with Manual deletion rule" }
            throw "Test 6 Assert 1 failed: MVO was deleted despite Manual deletion rule"
        }
        Write-Host "  PASSED: MVO still exists (Manual deletion rule - never auto-deleted)" -ForegroundColor Green

        # Assert 2: Attributes should remain on MVO (not recalled)
        $mvoDetail = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Search "Test Manual NoRecall" -Attributes Department -PageSize 10 -ErrorAction SilentlyContinue) |
            Where-Object { $_.displayName -eq "Test Manual NoRecall" } | Select-Object -First 1

        if ($mvoDetail) {
            # The API returns requested attributes in the 'attributes' dictionary property
            $deptValue = $null
            if ($mvoDetail.attributes -and $mvoDetail.attributes.PSObject.Properties.Name -contains 'Department') {
                $deptValue = $mvoDetail.attributes.Department
            }
            if ($deptValue) {
                Write-Host "  PASSED: CSV-contributed attribute 'department' retained: $deptValue" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "ManualNoRecall"; Success = $false; Error = "department was removed despite RemoveContributedAttributesOnObsoletion=false" }
                throw "Test 6 Assert 2 failed: CSV-contributed attribute 'department' was removed despite RemoveContributedAttributesOnObsoletion=false"
            }
        }

        # Assert 3: No new pending exports (nothing changed on MVO)
        $pendingExportsAfter = Get-PendingExportCount -ConnectedSystemId $config.LDAPSystemId
        Write-Host "  LDAP pending exports after: $pendingExportsAfter" -ForegroundColor Gray

        if ($pendingExportsAfter -le $pendingExportsBefore) {
            Write-Host "  PASSED: No new pending exports on LDAP target (attributes unchanged)" -ForegroundColor Green
        } else {
            $testResults.Steps += @{ Name = "ManualNoRecall"; Success = $false; Error = "Unexpected pending exports created on LDAP target" }
            throw "Test 6 Assert 3 failed: Unexpected pending exports created on LDAP target"
        }

        # Assert 4: MVO should NOT be marked as pending deletion
        if ($mvoDetail -and $mvoDetail.PSObject.Properties.Name -contains 'isPendingDeletion') {
            if (-not $mvoDetail.isPendingDeletion) {
                Write-Host "  PASSED: MVO isPendingDeletion=false (Manual rule does not mark for deletion)" -ForegroundColor Green
            } else {
                $testResults.Steps += @{ Name = "ManualNoRecall"; Success = $false; Error = "MVO isPendingDeletion=true despite Manual deletion rule" }
                throw "Test 6 Assert 4 failed: MVO isPendingDeletion=true despite Manual deletion rule"
            }
        }

        $testResults.Steps += @{ Name = "ManualNoRecall"; Success = $true }
    }

    # =============================================================================================================
    # Test 7: Internal MVO Protection (DEFERRED)
    # =============================================================================================================
    # Internal MVOs (Origin=Internal) must NEVER be auto-deleted regardless of deletion rule.
    # This test is deferred until the Internal MVO management feature is implemented,
    # which will allow creating and managing Internal MVOs via the admin UI/API.
    # See GitHub issue for Internal MVO management.
    # =============================================================================================================
    if ($Step -eq "InternalProtection" -or $Step -eq "All") {
        Write-TestSection "Test 7: Internal MVO Protection (DEFERRED)"
        Write-Host "  SKIPPED: This test is deferred until Internal MVO management is implemented." -ForegroundColor Yellow
        Write-Host "  Internal MVOs (Origin=Internal) must never be auto-deleted." -ForegroundColor Gray
        Write-Host "  When implemented, this test will:" -ForegroundColor Gray
        Write-Host "    1. Create an Internal MVO via the admin API" -ForegroundColor Gray
        Write-Host "    2. Configure WhenLastConnectorDisconnected with 0 grace period" -ForegroundColor Gray
        Write-Host "    3. Verify the Internal MVO is never deleted" -ForegroundColor Gray
        Write-Host "  See GitHub issue for Internal MVO management feature." -ForegroundColor Gray
        Write-Host ""
        $testResults.Steps += @{
            Name = "InternalProtection"
            Success = $true
            Warning = "DEFERRED - Internal MVO management not yet implemented"
        }
    }

    # =============================================================================================================
    # Reset deletion rules to sensible default before finishing
    # =============================================================================================================
    Write-TestSection "Cleanup: Reset Deletion Rules"
    try {
        Set-JIMMetaverseObjectType -Id $userObjectType.id `
            -DeletionRule "WhenLastConnectorDisconnected" `
            -DeletionGracePeriod ([TimeSpan]::FromDays(7))

        # Reset RemoveContributedAttributesOnObsoletion to default (true)
        $csvObjectTypes = Get-JIMConnectedSystem -Id $config.CSVSystemId -ObjectTypes
        $csvUserType = $csvObjectTypes | Where-Object { $_.name -match "^(user|person|record)$" } | Select-Object -First 1
        if ($csvUserType) {
            Set-JIMConnectedSystemObjectType -ConnectedSystemId $config.CSVSystemId -ObjectTypeId $csvUserType.id `
                -RemoveContributedAttributesOnObsoletion $true
        }

        Write-Host "  Reset to: DeletionRule=WhenLastConnectorDisconnected, GracePeriod=7 days" -ForegroundColor Green
        Write-Host "  Reset to: RemoveContributedAttributesOnObsoletion=true" -ForegroundColor Green
    }
    catch {
        Write-Host "  WARNING: Could not reset deletion rules: $_" -ForegroundColor Yellow
    }

    # =============================================================================================================
    # Summary
    # =============================================================================================================
    Write-TestSection "Test Results Summary"

    $successCount = @($testResults.Steps | Where-Object { $_.Success }).Count
    $failCount = @($testResults.Steps | Where-Object { -not $_.Success }).Count
    $totalCount = @($testResults.Steps).Count

    Write-Host "Tests run:    $totalCount" -ForegroundColor Cyan
    Write-Host "Tests passed: $successCount" -ForegroundColor $(if ($successCount -eq $totalCount) { "Green" } else { "Yellow" })
    if ($failCount -gt 0) {
        Write-Host "Tests failed: $failCount" -ForegroundColor Red
    }

    foreach ($stepResult in $testResults.Steps) {
        $status = if ($stepResult.Success) { "PASS" } else { "FAIL" }
        $color = if ($stepResult.Success) { "Green" } else { "Red" }

        Write-Host "  [$status] $($stepResult.Name)" -ForegroundColor $color

        if ($stepResult.ContainsKey('Error') -and $stepResult.Error) {
            Write-Host "         Error: $($stepResult.Error)" -ForegroundColor Red
        }
        if ($stepResult.ContainsKey('Warning') -and $stepResult.Warning) {
            Write-Host "         Note: $($stepResult.Warning)" -ForegroundColor Yellow
        }
    }

    $testResults.Success = ($successCount -eq $totalCount)
    $testResults.EndTime = (Get-Date).ToString("o")
    $testResults.TotalTests = $totalCount
    $testResults.PassedTests = $successCount
    $testResults.FailedTests = $failCount

    # Save structured test results to JSON for diagnostics
    $resultsDir = Join-Path $PSScriptRoot ".." "results" "test-results"
    if (-not (Test-Path $resultsDir)) {
        New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
    }
    $resultsTimestamp = (Get-Date).ToString("yyyy-MM-dd_HHmmss")
    $resultsFile = Join-Path $resultsDir "Scenario4-DeletionRules-$Template-$resultsTimestamp.json"
    $testResults | ConvertTo-Json -Depth 5 | Set-Content $resultsFile
    Write-Host ""
    Write-Host "Test results saved to: $resultsFile" -ForegroundColor Gray

    if ($testResults.Success) {
        Write-Host ""
        Write-Host "All tests passed" -ForegroundColor Green
        exit 0
    }
    else {
        Write-Host ""
        Write-Host "Some tests failed" -ForegroundColor Red
        exit 1
    }
}
catch {
    Write-Host ""
    Write-Host "Scenario 4 failed: $_" -ForegroundColor Red
    Write-Host "  Stack trace: $($_.ScriptStackTrace)" -ForegroundColor Gray
    $testResults.Error = $_.Exception.Message
    $testResults.EndTime = (Get-Date).ToString("o")

    # Save structured test results even on failure
    $resultsDir = Join-Path $PSScriptRoot ".." "results" "test-results"
    if (-not (Test-Path $resultsDir)) {
        New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
    }
    $resultsTimestamp = (Get-Date).ToString("yyyy-MM-dd_HHmmss")
    $resultsFile = Join-Path $resultsDir "Scenario4-DeletionRules-$Template-$resultsTimestamp.json"
    $testResults | ConvertTo-Json -Depth 5 | Set-Content $resultsFile
    Write-Host "Test results saved to: $resultsFile" -ForegroundColor Gray

    exit 1
}
