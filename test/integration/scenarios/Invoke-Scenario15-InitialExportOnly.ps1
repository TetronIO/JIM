# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Initial Export Only attribute flows: set once at provisioning, unmanaged thereafter (#223)

.DESCRIPTION
    Exercises the Initial Export Only option on an export Synchronisation Rule Attribute Flow
    mapping end-to-end against a directory target. The scenario reuses Scenario 1's proven
    HR CSV to directory configuration and adds one extra export mapping on top:

        Employee Type (Metaverse) -> employeeType (directory), Initial Export Only

    The sibling Job Title -> title mapping stays normally managed, so a single test user gives
    both the positive control (title keeps flowing and is drift-corrected) and the behaviour
    under test (employeeType flows once, then the directory owns it).

      InitialProvision:
        Provisions the test user into the directory and confirms the export. Both title and
        employeeType must carry the HR values: the Initial Export Only mapping participates in
        the initial provisioning (Create) export exactly like any other mapping.

      SourceChange:
        Changes the user's title AND employeeType in the HR CSV, then imports and synchronises.
        The staged Pending Export must carry the title change and must NOT carry employeeType
        (the attribute is unmanaged once the object is past provisioning). After the export and
        confirming import, the directory's title is updated and employeeType still holds the
        value set at provisioning.

      ExternalChange:
        Modifies the user's title AND employeeType directly in the directory, then runs the
        delta import and synchronisation so Drift Detection evaluates the diverged values
        (the export rule has enforce state on). The corrective Pending Export must carry the
        title revert and must NOT carry employeeType. After the corrective export, the
        directory's title is back to the Metaverse value and employeeType retains the
        externally-set value: the external system owns it.

.PARAMETER Step
    Which test step to execute. "All" runs every step in order. Later steps depend on the
    state built by earlier ones, so individual steps are for re-runs against an existing
    environment only.

.PARAMETER Template
    Data scale template. The scenario reads its test user from the generated HR CSV, so any
    template works; Nano/Micro are recommended for speed.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication.

.PARAMETER WaitSeconds
    Seconds to wait between run profile executions for JIM processing (default: 0).

.PARAMETER DirectoryConfig
    Directory configuration hashtable from Get-DirectoryConfig. Defaults to Samba AD primary.

.EXAMPLE
    ./Invoke-Scenario15-InitialExportOnly.ps1 -Step All -Template Nano -ApiKey "jim_..."
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("InitialProvision", "SourceChange", "ExternalChange", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [switch]$ContinueOnError,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

# Accepted for runner compatibility; this scenario always runs the full flow per step.
$null = $SkipPopulate
$null = $ContinueOnError
$null = $ExportConcurrency
$null = $MaxExportParallelism

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}
$isOpenLDAP = $DirectoryConfig.UserObjectClass -eq "inetOrgPerson"

$csvSystemName = "HR CSV Source"
$ldapSystemName = $DirectoryConfig.ConnectedSystemName
$exportRuleName = "$ldapSystemName Export Users"
$unmanagedLdapAttribute = "employeeType"
$managedLdapAttribute = "title"

# The values the test writes at each stage. Distinctive strings so a stale value can never
# satisfy a later assertion by accident.
$sourceChangedTitle = "S15 Managed Title"
$sourceChangedEmployeeType = "S15 Should Not Flow"
$externalTitle = "S15 External Drift Title"
$externalEmployeeType = "S15 Externally Owned"

Write-TestSection "Scenario 15: Initial Export Only Attribute Flows (#223)"
Write-Host "Step:      $Step" -ForegroundColor Gray
Write-Host "Template:  $Template" -ForegroundColor Gray
Write-Host "Directory: $ldapSystemName" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Initial Export Only Attribute Flows"
    Steps = @()
    Success = $false
}

function Add-StepResult {
    param([string]$Name, [bool]$Success = $true, [string]$Note = "", [string]$ErrorMsg = "")
    $entry = @{ Name = $Name; Success = $Success }
    if ($Note) { $entry.Note = $Note }
    if ($ErrorMsg) { $entry.Error = $ErrorMsg }
    $testResults.Steps += $entry
}

function Test-StepEnabled {
    param([string]$StepName)
    return ($Step -eq $StepName -or $Step -eq "All")
}

function Get-NamedRunProfile {
    param([int]$SystemId, [string]$Name)
    $runProfile = (Get-JIMRunProfile -ConnectedSystemId $SystemId) | Where-Object { $_.name -eq $Name }
    if (-not $runProfile) { throw "Run profile '$Name' not found for Connected System $SystemId" }
    return $runProfile
}

function Invoke-NamedRunProfile {
    param([int]$SystemId, [string]$ProfileName, [string]$Label)
    $runProfile = Get-NamedRunProfile -SystemId $SystemId -Name $ProfileName
    $execution = Start-JIMRunProfile -ConnectedSystemId $SystemId -RunProfileId $runProfile.id -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $execution.activityId -Name $Label
    if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }
    return $execution
}

# Runs the outbound half of the flow: export to the directory, wait for it to settle, then the
# confirming delta import and delta synchronisation so Pending Exports confirm and the
# provisioned object transitions out of PendingProvisioning.
function Invoke-ExportAndConfirm {
    param([int]$LdapSystemId, [string]$Label)
    Invoke-NamedRunProfile -SystemId $LdapSystemId -ProfileName "Export" -Label "$Label Export" | Out-Null
    Start-Sleep -Seconds 5
    Invoke-NamedRunProfile -SystemId $LdapSystemId -ProfileName "Delta Import" -Label "$Label Delta Import (confirming)" | Out-Null
    Invoke-NamedRunProfile -SystemId $LdapSystemId -ProfileName "Delta Synchronisation" -Label "$Label Delta Synchronisation" | Out-Null
}

# Collects the attribute names carried by every Pending Export currently staged for the system.
function Get-PendingExportAttributeNames {
    param([int]$SystemId)
    $names = @()
    $pendingExports = @(Get-JIMPendingExport -ConnectedSystemId $SystemId -All)
    foreach ($pendingExport in $pendingExports) {
        $detail = Get-JIMPendingExport -Id $pendingExport.Id
        if ($detail -and $detail.AttributeChanges) {
            $names += @($detail.AttributeChanges | ForEach-Object { $_.AttributeName })
        }
    }
    return $names
}

# Replaces attribute values on a directory user via ldapmodify, branching on directory type
# (mirrors the Scenario 2 external-modification patterns).
function Set-DirectoryUserAttributes {
    param([string]$UserDn, [hashtable]$Values, [string]$Label)

    $ldifLines = @("dn: $UserDn", "changetype: modify")
    $first = $true
    foreach ($attributeName in $Values.Keys) {
        if (-not $first) { $ldifLines += "-" }
        $ldifLines += "replace: $attributeName"
        $ldifLines += "${attributeName}: $($Values[$attributeName])"
        $first = $false
    }
    $ldif = $ldifLines -join "`n"

    if ($isOpenLDAP) {
        $result = $ldif | docker exec -i $DirectoryConfig.ContainerName ldapmodify -x -H "ldap://localhost:$($DirectoryConfig.Port)" -D "$($DirectoryConfig.BindDN)" -w "$($DirectoryConfig.BindPassword)" 2>&1
    }
    else {
        $result = docker exec $DirectoryConfig.ContainerName bash -c "cat > /tmp/scenario15-modify.ldif << 'LDIFEOF'
$ldif
LDIFEOF
ldapmodify -x -H ldap://localhost -D '$($DirectoryConfig.BindDN)' -w '$($DirectoryConfig.BindPassword)' -f /tmp/scenario15-modify.ldif" 2>&1
    }
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed to modify directory user ${UserDn}: $result"
    }
}

function Get-DirectoryTestUser {
    param([string]$SamAccountName, [string]$Label)
    $directoryUser = Get-LDAPUser -UserIdentifier $SamAccountName -DirectoryConfig $DirectoryConfig
    if (-not $directoryUser) { throw "$Label expected user '$SamAccountName' to exist in $ldapSystemName, but it was not found" }
    return $directoryUser
}

function Assert-DirectoryAttribute {
    param([hashtable]$DirectoryUser, [string]$AttributeName, [string]$ExpectedValue, [string]$Label)
    $actual = if ($DirectoryUser.ContainsKey($AttributeName)) { $DirectoryUser[$AttributeName] } else { $null }
    if ("$actual" -ne $ExpectedValue) {
        throw "$Label expected directory attribute '$AttributeName' to be '$ExpectedValue' but it was '$actual'"
    }
    Write-Host "  OK $AttributeName = '$ExpectedValue'" -ForegroundColor Green
}

try {
    # ─────────────────────────────────────────────────────────────────────────
    # Step 0: Setup. Reuse Scenario 1's HR CSV to directory configuration, then layer the
    # Initial Export Only mapping on top: select the spare directory attribute, ensure the
    # export rule enforces state (so ExternalChange genuinely exercises Drift Detection), and
    # add the Employee Type -> employeeType mapping with Initial Export Only enabled.
    # ─────────────────────────────────────────────────────────────────────────
    Write-TestSection "Step 0: Setup"

    if (-not $ApiKey) { throw "API key required for authentication" }

    Write-Host "Running Scenario 1 setup (shared configuration)..." -ForegroundColor Gray
    & "$PSScriptRoot/../Setup-Scenario1.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template -DirectoryConfig $DirectoryConfig

    # Setup removes and re-imports the module, so reconnect before issuing cmdlets here.
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $csvSystem = (Get-JIMConnectedSystem) | Where-Object { $_.name -eq $csvSystemName }
    if (-not $csvSystem) { throw "Connected system '$csvSystemName' not found after setup" }
    $ldapSystem = (Get-JIMConnectedSystem) | Where-Object { $_.name -eq $ldapSystemName }
    if (-not $ldapSystem) { throw "Connected system '$ldapSystemName' not found after setup" }
    $exportRule = (Get-JIMSyncRule) | Where-Object { $_.name -eq $exportRuleName }
    if (-not $exportRule) { throw "Synchronisation Rule '$exportRuleName' not found after setup" }

    # Enforce state must be on for the ExternalChange step to exercise Drift Detection.
    Set-JIMSyncRule -Id $exportRule.id -EnforceState $true | Out-Null

    # Select the spare directory attribute the Initial Export Only mapping targets. Both the AD
    # and inetOrgPerson schemas define employeeType; Scenario 1's minimal selection excludes it.
    $ldapObjectTypes = Get-JIMConnectedSystemObjectType -ConnectedSystemId $ldapSystem.id
    $ldapUserType = $ldapObjectTypes | Where-Object { $_.name -eq $DirectoryConfig.UserObjectClass } | Select-Object -First 1
    if (-not $ldapUserType) { throw "Directory user object type '$($DirectoryConfig.UserObjectClass)' not found" }
    $employeeTypeAttr = $ldapUserType.attributes | Where-Object { $_.name -eq $unmanagedLdapAttribute }
    if (-not $employeeTypeAttr) {
        throw "Directory attribute '$unmanagedLdapAttribute' not found in the $($DirectoryConfig.UserObjectClass) schema. This usually means the directory image is outdated and needs rebuilding."
    }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $ldapSystem.id -ObjectTypeId $ldapUserType.id -AttributeUpdates @{ $employeeTypeAttr.id = @{ selected = $true } } | Out-Null
    Write-Host "  OK Selected directory attribute '$unmanagedLdapAttribute'" -ForegroundColor Green

    # Add the Initial Export Only mapping: Employee Type (MV) -> employeeType (directory).
    $mvEmployeeTypeAttr = Get-JIMMetaverseAttribute | Where-Object { $_.name -eq "Employee Type" }
    if (-not $mvEmployeeTypeAttr) { throw "Metaverse attribute 'Employee Type' not found" }

    $existingMappings = @(Get-JIMSyncRuleMapping -SyncRuleId $exportRule.id)
    $initialExportOnlyMapping = $existingMappings | Where-Object { $_.TargetConnectedSystemAttributeId -eq $employeeTypeAttr.id }
    if (-not $initialExportOnlyMapping) {
        $initialExportOnlyMapping = New-JIMSyncRuleMapping -SyncRuleId $exportRule.id `
            -TargetConnectedSystemAttributeId $employeeTypeAttr.id `
            -SourceMetaverseAttributeId $mvEmployeeTypeAttr.id `
            -InitialExportOnly
        Write-Host "  OK Created Initial Export Only mapping (Employee Type -> $unmanagedLdapAttribute)" -ForegroundColor Green
    }
    else {
        Write-Host "  Initial Export Only mapping already exists" -ForegroundColor Gray
    }

    # Prove the flag round-tripped through the API.
    $persistedMapping = Get-JIMSyncRuleMapping -SyncRuleId $exportRule.id | Where-Object { $_.TargetConnectedSystemAttributeId -eq $employeeTypeAttr.id }
    if (-not $persistedMapping -or -not $persistedMapping.InitialExportOnly) {
        throw "Setup expected the Employee Type -> $unmanagedLdapAttribute mapping to persist InitialExportOnly=true, but it did not"
    }
    Write-Host "  OK Mapping persisted with InitialExportOnly=true" -ForegroundColor Green

    # The test user: first user of the generated HR CSV (deterministic per template).
    $testUser = New-TestUser -Index 1
    $testUserSam = $testUser.SamAccountName
    $originalTitle = $testUser.Title
    $originalEmployeeType = $testUser.EmployeeType
    $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
    Write-Host "  Test user: $testUserSam (title='$originalTitle', employeeType='$originalEmployeeType')" -ForegroundColor Gray

    # ─────────────────────────────────────────────────────────────────────────
    # T1 (InitialProvision): the Initial Export Only mapping participates in the provisioning
    # (Create) export, so the directory receives BOTH the managed and the one-time values.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "InitialProvision") {
        Write-TestSection "Test 1: Initial Provision (one-time value flows on Create)"
        try {
            Invoke-NamedRunProfile -SystemId $csvSystem.id -ProfileName "Full Import" -Label "T1 CSV Full Import" | Out-Null
            Invoke-NamedRunProfile -SystemId $csvSystem.id -ProfileName "Full Synchronisation" -Label "T1 CSV Full Synchronisation" | Out-Null
            Invoke-ExportAndConfirm -LdapSystemId $ldapSystem.id -Label "T1"

            $directoryUser = Get-DirectoryTestUser -SamAccountName $testUserSam -Label "T1"
            Assert-DirectoryAttribute -DirectoryUser $directoryUser -AttributeName $managedLdapAttribute -ExpectedValue $originalTitle -Label "T1"
            Assert-DirectoryAttribute -DirectoryUser $directoryUser -AttributeName $unmanagedLdapAttribute -ExpectedValue $originalEmployeeType -Label "T1"

            Add-StepResult -Name "InitialProvision" -Success $true -Note "Provisioning exported both the managed title and the Initial Export Only employeeType"
        }
        catch {
            Add-StepResult -Name "InitialProvision" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T2 (SourceChange): once the object is past provisioning, a Metaverse change to the
    # Initial Export Only attribute must not stage or export; the sibling managed mapping
    # keeps flowing.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "SourceChange") {
        Write-TestSection "Test 2: Source Change (unmanaged attribute is not re-exported)"
        try {
            Write-Host "Updating title and employeeType in the HR CSV..." -ForegroundColor Gray
            $csv = Import-Csv $csvPath
            $csvRow = $csv | Where-Object { $_.samAccountName -eq $testUserSam }
            if (-not $csvRow) { throw "T2 could not find $testUserSam in $csvPath" }
            $csvRow.title = $sourceChangedTitle
            $csvRow.employeeType = $sourceChangedEmployeeType
            $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
            Copy-CsvToConnectorFiles -SourcePath $csvPath
            Write-Host "  OK Changed $testUserSam title to '$sourceChangedTitle' and employeeType to '$sourceChangedEmployeeType'" -ForegroundColor Green

            Invoke-NamedRunProfile -SystemId $csvSystem.id -ProfileName "Full Import" -Label "T2 CSV Full Import" | Out-Null
            Invoke-NamedRunProfile -SystemId $csvSystem.id -ProfileName "Delta Synchronisation" -Label "T2 CSV Delta Synchronisation" | Out-Null

            # The staged Pending Export must carry the managed attribute and not the unmanaged one.
            $stagedAttributeNames = @(Get-PendingExportAttributeNames -SystemId $ldapSystem.id)
            if ($stagedAttributeNames -notcontains $managedLdapAttribute) {
                throw "T2 expected a Pending Export carrying '$managedLdapAttribute' (managed sibling must keep flowing); staged attributes: [$($stagedAttributeNames -join ', ')]"
            }
            if ($stagedAttributeNames -contains $unmanagedLdapAttribute) {
                throw "T2 staged a Pending Export for '$unmanagedLdapAttribute': the Initial Export Only attribute must be unmanaged after provisioning"
            }
            Write-Host "  OK Pending Export carries '$managedLdapAttribute' only (staged: [$($stagedAttributeNames -join ', ')])" -ForegroundColor Green

            Invoke-ExportAndConfirm -LdapSystemId $ldapSystem.id -Label "T2"

            $directoryUser = Get-DirectoryTestUser -SamAccountName $testUserSam -Label "T2"
            Assert-DirectoryAttribute -DirectoryUser $directoryUser -AttributeName $managedLdapAttribute -ExpectedValue $sourceChangedTitle -Label "T2"
            Assert-DirectoryAttribute -DirectoryUser $directoryUser -AttributeName $unmanagedLdapAttribute -ExpectedValue $originalEmployeeType -Label "T2"

            Add-StepResult -Name "SourceChange" -Success $true -Note "Metaverse change flowed for the managed title only; the Initial Export Only employeeType kept its provisioning value"
        }
        catch {
            Add-StepResult -Name "SourceChange" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    # ─────────────────────────────────────────────────────────────────────────
    # T3 (ExternalChange): an external edit to the Initial Export Only attribute must survive
    # Drift Detection; the managed sibling's external edit is corrected back, proving enforce
    # state is genuinely active in the same run.
    # ─────────────────────────────────────────────────────────────────────────
    if (Test-StepEnabled "ExternalChange") {
        Write-TestSection "Test 3: External Change (Drift Correction leaves the unmanaged attribute alone)"
        try {
            $directoryUser = Get-DirectoryTestUser -SamAccountName $testUserSam -Label "T3"
            $userDn = $directoryUser["dn"]

            Write-Host "Modifying title and employeeType directly in $ldapSystemName..." -ForegroundColor Gray
            Set-DirectoryUserAttributes -UserDn $userDn -Label "T3" -Values @{
                $managedLdapAttribute = $externalTitle
                $unmanagedLdapAttribute = $externalEmployeeType
            }
            Write-Host "  OK Set title to '$externalTitle' and employeeType to '$externalEmployeeType' externally" -ForegroundColor Green

            # Import the external changes and synchronise: Drift Detection evaluates the diverged
            # values and stages corrective exports for managed attributes only.
            Invoke-NamedRunProfile -SystemId $ldapSystem.id -ProfileName "Delta Import" -Label "T3 Delta Import (external changes)" | Out-Null
            Invoke-NamedRunProfile -SystemId $ldapSystem.id -ProfileName "Delta Synchronisation" -Label "T3 Delta Synchronisation (drift evaluation)" | Out-Null

            $stagedAttributeNames = @(Get-PendingExportAttributeNames -SystemId $ldapSystem.id)
            if ($stagedAttributeNames -notcontains $managedLdapAttribute) {
                throw "T3 expected a corrective Pending Export for '$managedLdapAttribute' (enforce state must revert managed drift); staged attributes: [$($stagedAttributeNames -join ', ')]"
            }
            if ($stagedAttributeNames -contains $unmanagedLdapAttribute) {
                throw "T3 staged a corrective Pending Export for '$unmanagedLdapAttribute': Drift Correction must not touch an Initial Export Only attribute"
            }
            Write-Host "  OK Corrective Pending Export carries '$managedLdapAttribute' only (staged: [$($stagedAttributeNames -join ', ')])" -ForegroundColor Green

            Invoke-ExportAndConfirm -LdapSystemId $ldapSystem.id -Label "T3"

            $directoryUser = Get-DirectoryTestUser -SamAccountName $testUserSam -Label "T3"
            Assert-DirectoryAttribute -DirectoryUser $directoryUser -AttributeName $managedLdapAttribute -ExpectedValue $sourceChangedTitle -Label "T3"
            Assert-DirectoryAttribute -DirectoryUser $directoryUser -AttributeName $unmanagedLdapAttribute -ExpectedValue $externalEmployeeType -Label "T3"

            Add-StepResult -Name "ExternalChange" -Success $true -Note "Drift Correction reverted the managed title and left the externally-owned employeeType untouched"
        }
        catch {
            Add-StepResult -Name "ExternalChange" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $_" -ForegroundColor Red
        }
    }

    $failed = @($testResults.Steps | Where-Object { -not $_.Success })
    $testResults.Success = ($failed.Count -eq 0 -and $testResults.Steps.Count -gt 0)
}
catch {
    Write-Host ""
    Write-Host "FAIL Test scenario failed with error:" -ForegroundColor Red
    Write-Host "  $_" -ForegroundColor Red
    Add-StepResult -Name "Setup" -Success $false -ErrorMsg $_.Exception.Message
}

# ─────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────
Write-TestSection "Test Results Summary"

$passed = @($testResults.Steps | Where-Object { $_.Success }).Count
$failedCount = @($testResults.Steps | Where-Object { -not $_.Success }).Count
$total = @($testResults.Steps).Count

Write-Host "Scenario: $($testResults.Scenario)" -ForegroundColor Cyan
Write-Host ""

foreach ($testStep in $testResults.Steps) {
    $icon = if ($testStep.Success) { "OK" } else { "FAIL" }
    $colour = if ($testStep.Success) { "Green" } else { "Red" }
    Write-Host "  $icon $($testStep.Name)" -ForegroundColor $colour
    if ($testStep.ContainsKey('Note') -and $testStep.Note) {
        Write-Host "    $($testStep.Note)" -ForegroundColor Gray
    }
    if (-not $testStep.Success -and $testStep.ContainsKey('Error') -and $testStep.Error) {
        Write-Host "    Error: $($testStep.Error)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Results: $passed passed, $failedCount failed (of $total tests)" -ForegroundColor $(if ($failedCount -eq 0 -and $total -gt 0) { "Green" } else { "Red" })

if ($testResults.Success) {
    Write-Host ""
    Write-Host "OK All Scenario 15 tests passed!" -ForegroundColor Green
    exit 0
}
else {
    Write-Host ""
    Write-Host "FAIL Some tests failed" -ForegroundColor Red
    exit 1
}
