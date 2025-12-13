<#
.SYNOPSIS
    Configure JIM for Scenario 1: HR to Enterprise Directory

.DESCRIPTION
    Sets up Connected Systems and Sync Rules for HR to Active Directory provisioning.
    This script creates:
    - CSV Connected System (HR source)
    - LDAP Connected System (Samba AD target)
    - Sync Rules for attribute mappings
    - Run Profiles for synchronisation

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://jim.web:80 for internal Docker network)

.PARAMETER ApiKey
    API key for authentication (if not provided, will attempt to create one)

.PARAMETER Template
    Data scale template (Micro, Small, Medium, Large, XLarge, XXLarge)

.EXAMPLE
    ./Setup-Scenario1.ps1 -JIMUrl "http://jim.web:80" -ApiKey "jim_abc123..."

.EXAMPLE
    ./Setup-Scenario1.ps1 -Template Small

.NOTES
    This script requires the JIM PowerShell module and assumes:
    - JIM is running and accessible
    - Samba AD container is running on jim-network
    - CSV files exist at /connector-files/hr-users.csv
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://jim.web:80",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [ValidateSet("Micro", "Small", "Medium", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Scenario 1 Setup: HR to Enterprise Directory"

# Step 1: Import JIM PowerShell module
Write-TestStep "Step 1" "Importing JIM PowerShell module"

$modulePath = "$PSScriptRoot/../../JIM.PowerShell/JIM/JIM.psd1"
if (-not (Test-Path $modulePath)) {
    throw "JIM PowerShell module not found at: $modulePath"
}

Import-Module $modulePath -Force -ErrorAction Stop
Write-Host "  ✓ JIM PowerShell module imported" -ForegroundColor Green

# Step 2: Connect to JIM
Write-TestStep "Step 2" "Connecting to JIM at $JIMUrl"

if (-not $ApiKey) {
    Write-Host "  No API key provided - this scenario requires an existing API key" -ForegroundColor Yellow
    Write-Host "  Create an API key via JIM web UI: Admin > API Keys" -ForegroundColor Yellow
    throw "API key required for authentication"
}

try {
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
    Write-Host "  ✓ Connected to JIM" -ForegroundColor Green
}
catch {
    Write-Host "  ✗ Failed to connect to JIM: $_" -ForegroundColor Red
    Write-Host "  Ensure JIM is running and accessible at $JIMUrl" -ForegroundColor Yellow
    throw
}

# Step 3: Get connector definitions
Write-TestStep "Step 3" "Getting connector definitions"

try {
    # Get all connector definitions
    $connectorDefs = Get-JIMConnectorDefinition

    $csvConnector = $connectorDefs | Where-Object { $_.name -eq "CSV File" }
    $ldapConnector = $connectorDefs | Where-Object { $_.name -eq "LDAP" }

    if (-not $csvConnector) {
        throw "CSV File connector definition not found"
    }

    if (-not $ldapConnector) {
        throw "LDAP connector definition not found"
    }

    Write-Host "  ✓ Found CSV connector (ID: $($csvConnector.id))" -ForegroundColor Green
    Write-Host "  ✓ Found LDAP connector (ID: $($ldapConnector.id))" -ForegroundColor Green
}
catch {
    Write-Host "  ✗ Failed to get connector definitions: $_" -ForegroundColor Red
    throw
}

# Step 4: Create CSV Connected System (HR source)
Write-TestStep "Step 4" "Creating CSV Connected System"

try {
    # Check if already exists
    $existingSystems = Get-JIMConnectedSystem
    $csvSystem = $existingSystems | Where-Object { $_.name -eq "HR CSV Source" }

    if ($csvSystem) {
        Write-Host "  Connected System 'HR CSV Source' already exists (ID: $($csvSystem.id))" -ForegroundColor Yellow
    }
    else {
        $csvSystem = New-JIMConnectedSystem `
            -Name "HR CSV Source" `
            -Description "HR system CSV export for integration testing" `
            -ConnectorDefinitionId $csvConnector.id `
            -PassThru

        Write-Host "  ✓ Created CSV Connected System (ID: $($csvSystem.id))" -ForegroundColor Green
    }

    # Configure CSV settings
    # Get settings from the connector definition to find setting IDs
    $csvConnectorFull = Get-JIMConnectorDefinition -Id $csvConnector.id
    $filePathSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "FilePath" }
    $delimiterSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "Delimiter" }
    $hasHeadersSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "HasHeaders" }

    $settingValues = @{}
    if ($filePathSetting) {
        $settingValues[$filePathSetting.id] = @{ stringValue = "/connector-files/hr-users.csv" }
    }
    if ($delimiterSetting) {
        $settingValues[$delimiterSetting.id] = @{ stringValue = "," }
    }
    if ($hasHeadersSetting) {
        $settingValues[$hasHeadersSetting.id] = @{ checkboxValue = $true }
    }

    if ($settingValues.Count -gt 0) {
        Set-JIMConnectedSystem -Id $csvSystem.id -SettingValues $settingValues | Out-Null
        Write-Host "  ✓ Configured CSV settings" -ForegroundColor Green
    }
}
catch {
    Write-Host "  ✗ Failed to create/configure CSV Connected System: $_" -ForegroundColor Red
    throw
}

# Step 5: Create LDAP Connected System (Samba AD target)
Write-TestStep "Step 5" "Creating LDAP Connected System"

try {
    $ldapSystem = $existingSystems | Where-Object { $_.name -eq "Samba AD Primary" }

    if ($ldapSystem) {
        Write-Host "  Connected System 'Samba AD Primary' already exists (ID: $($ldapSystem.id))" -ForegroundColor Yellow
    }
    else {
        $ldapSystem = New-JIMConnectedSystem `
            -Name "Samba AD Primary" `
            -Description "Samba Active Directory for integration testing" `
            -ConnectorDefinitionId $ldapConnector.id `
            -PassThru

        Write-Host "  ✓ Created LDAP Connected System (ID: $($ldapSystem.id))" -ForegroundColor Green
    }

    # Configure LDAP settings
    $ldapConnectorFull = Get-JIMConnectorDefinition -Id $ldapConnector.id

    # Find setting IDs by name
    $serverSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Server" }
    $portSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Port" }
    $baseDnSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "BaseDN" }
    $bindDnSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "BindDN" }
    $passwordSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Password" }
    $useSSLSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "UseSSL" }

    $ldapSettings = @{}
    if ($serverSetting) {
        $ldapSettings[$serverSetting.id] = @{ stringValue = "samba-ad-primary" }
    }
    if ($portSetting) {
        $ldapSettings[$portSetting.id] = @{ intValue = 389 }
    }
    if ($baseDnSetting) {
        $ldapSettings[$baseDnSetting.id] = @{ stringValue = "dc=testdomain,dc=local" }
    }
    if ($bindDnSetting) {
        $ldapSettings[$bindDnSetting.id] = @{ stringValue = "cn=Administrator,cn=Users,dc=testdomain,dc=local" }
    }
    if ($passwordSetting) {
        $ldapSettings[$passwordSetting.id] = @{ stringValue = "Test@123!" }
    }
    if ($useSSLSetting) {
        $ldapSettings[$useSSLSetting.id] = @{ checkboxValue = $false }
    }

    if ($ldapSettings.Count -gt 0) {
        Set-JIMConnectedSystem -Id $ldapSystem.id -SettingValues $ldapSettings | Out-Null
        Write-Host "  ✓ Configured LDAP settings" -ForegroundColor Green
    }
}
catch {
    Write-Host "  ✗ Failed to create/configure LDAP Connected System: $_" -ForegroundColor Red
    throw
}

# Step 6: Create Sync Rules
Write-TestStep "Step 6" "Creating Sync Rules"

try {
    # Get existing sync rules
    $existingRules = Get-JIMSyncRule

    # Define sync rules for attribute mappings
    $ruleMappings = @(
        @{ SourceAttr = "employeeId"; TargetAttr = "employeeNumber"; Description = "Employee ID mapping" }
        @{ SourceAttr = "firstName"; TargetAttr = "givenName"; Description = "First name mapping" }
        @{ SourceAttr = "lastName"; TargetAttr = "sn"; Description = "Last name mapping" }
        @{ SourceAttr = "email"; TargetAttr = "mail"; Description = "Email mapping" }
        @{ SourceAttr = "department"; TargetAttr = "department"; Description = "Department mapping" }
        @{ SourceAttr = "samAccountName"; TargetAttr = "sAMAccountName"; Description = "Sam account name mapping" }
        @{ SourceAttr = "displayName"; TargetAttr = "displayName"; Description = "Display name mapping" }
    )

    $createdRules = 0
    foreach ($mapping in $ruleMappings) {
        $ruleName = "HR-AD: $($mapping.SourceAttr) → $($mapping.TargetAttr)"

        $existingRule = $existingRules | Where-Object { $_.name -eq $ruleName }

        if ($existingRule) {
            Write-Host "  Sync Rule '$ruleName' already exists" -ForegroundColor Gray
        }
        else {
            New-JIMSyncRule `
                -Name $ruleName `
                -Description $mapping.Description `
                -SourceSystemId $csvSystem.id `
                -TargetSystemId $ldapSystem.id `
                -SourceAttribute $mapping.SourceAttr `
                -TargetAttribute $mapping.TargetAttr | Out-Null

            $createdRules++
        }
    }

    if ($createdRules -gt 0) {
        Write-Host "  ✓ Created $createdRules sync rule(s)" -ForegroundColor Green
    }
    else {
        Write-Host "  All sync rules already exist" -ForegroundColor Gray
    }
}
catch {
    Write-Host "  ✗ Failed to create sync rules: $_" -ForegroundColor Red
    Write-Host "  Error details: $($_.Exception.Message)" -ForegroundColor Red
    # Continue - sync rules might need manual configuration
}

# Step 7: Create Run Profiles
Write-TestStep "Step 7" "Creating Run Profiles"

try {
    $existingProfiles = Get-JIMRunProfile

    # Full Import from CSV
    $csvImportProfile = $existingProfiles | Where-Object { $_.name -eq "HR CSV - Full Import" -and $_.connectedSystemId -eq $csvSystem.id }
    if (-not $csvImportProfile) {
        $csvImportProfile = New-JIMRunProfile `
            -Name "HR CSV - Full Import" `
            -ConnectedSystemId $csvSystem.id `
            -ProfileType "FullImport" `
            -PassThru
        Write-Host "  ✓ Created 'HR CSV - Full Import' run profile" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'HR CSV - Full Import' already exists" -ForegroundColor Gray
    }

    # Full Export to LDAP
    $ldapExportProfile = $existingProfiles | Where-Object { $_.name -eq "Samba AD - Full Export" -and $_.connectedSystemId -eq $ldapSystem.id }
    if (-not $ldapExportProfile) {
        $ldapExportProfile = New-JIMRunProfile `
            -Name "Samba AD - Full Export" `
            -ConnectedSystemId $ldapSystem.id `
            -ProfileType "FullExport" `
            -PassThru
        Write-Host "  ✓ Created 'Samba AD - Full Export' run profile" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Samba AD - Full Export' already exists" -ForegroundColor Gray
    }
}
catch {
    Write-Host "  ✗ Failed to create run profiles: $_" -ForegroundColor Red
    Write-Host "  Error details: $($_.Exception.Message)" -ForegroundColor Red
    # Continue - run profiles might need manual configuration
}

# Summary
Write-TestSection "Setup Complete"
Write-Host "Template:        $Template" -ForegroundColor Cyan
Write-Host "CSV System ID:   $($csvSystem.id)" -ForegroundColor Cyan
Write-Host "LDAP System ID:  $($ldapSystem.id)" -ForegroundColor Cyan
Write-Host ""
Write-Host "✓ Scenario 1 setup complete" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Run: ./scenarios/Invoke-Scenario1-HRToDirectory.ps1 -Template $Template" -ForegroundColor Gray
Write-Host "  2. Or manually trigger run profiles via JIM UI" -ForegroundColor Gray
Write-Host ""

# Return configuration for use by scenario scripts
return @{
    CSVSystemId = $csvSystem.id
    LDAPSystemId = $ldapSystem.id
    CSVImportProfileId = $csvImportProfile.id
    LDAPExportProfileId = $ldapExportProfile.id
}
