# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Setup for Scenario 034: Exchange Recipient Provisioning

.DESCRIPTION
    Configures JIM to make the accounts it provisions into PANOPLY Exchange recipients through the Active Directory
    lab's Exchange server, exchange-primary (#2021), as a customer would: tick Manage Exchange recipients on the LDAP
    Connected System, name the Exchange server, refresh the schema so the connector-defined Exchange attributes appear,
    and flow them on the export Synchronisation Rule.

    The provisioning substrate is Scenario 001's (HR CSV source, Active Directory target, an export Synchronisation
    Rule that provisions Users), composed rather than rebuilt, as Scenario 017 does. Only the people Scenario 034 adds
    to the HR feed become recipients: their Employee Type is "ExchangeLab", their Job Title is the recipient type
    (Mailbox, SharedMailbox, RoomMailbox, EquipmentMailbox or MailUser), and their Company chooses a mailbox database.
    Everyone else in the template declares nothing, so the export of the whole population (which crosses a page
    boundary at Medium) carries a handful of Exchange changes among a thousand accounts that have none.

    The Attribute Flows, all expressions on the export rule:
      - Exchange Recipient Type: the Job Title, for the ExchangeLab people only.
      - Exchange Database: MDB02 (the database the lab excludes from automatic distribution, so a mailbox only lands
        there when it is named) for Company "ExchangeLab Placed"; MDB99, which does not exist, for Company
        "ExchangeLab Missing"; for everyone else nothing, so Exchange chooses.
      - Exchange Alias: first and last name, lower case, so a surname change is an alias change.
      - Exchange External Address: first.last@partner.example, for a mail user only.
      - mail: a mail user's external address, which is what Exchange writes there; everyone else's HR address.
      - userAccountControl: Scenario 001's flow, extended so shared, room and equipment mailboxes are written
        disabled, which Exchange requires and the LDAP Connector enforces before creating the account.

    Saving the Exchange settings signs in to Exchange (the save-time check), so the Exchange server has to be ready
    when this runs; the runner's revert waits for it.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Data scale template, passed through to Setup-Scenario-001.ps1

.PARAMETER DirectoryConfig
    Directory configuration hashtable from Get-DirectoryConfig (the Active Directory lab's Primary)

.PARAMETER ExchangeConfig
    The Exchange server's configuration. Defaults to Get-ActiveDirectoryLabExchangeConfig -Instance ExchangePrimary.

.PARAMETER ExportConcurrency
    LDAP Connector export concurrency, passed through to Setup-Scenario-001.ps1

.PARAMETER MaxExportParallelism
    Connected System export parallelism, passed through to Setup-Scenario-001.ps1

.EXAMPLE
    ./Setup-Scenario-034.ps1 -ApiKey "jim_..." -Template Medium -DirectoryConfig $dc -ExchangeConfig $exchange
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$true)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Micro",

    [Parameter(Mandatory=$true)]
    [hashtable]$DirectoryConfig,

    [Parameter(Mandatory=$false)]
    [hashtable]$ExchangeConfig,

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

. "$PSScriptRoot/utils/Test-Helpers.ps1"

# The runner's -SetupOnly passes the standard parameters only, so the Exchange server defaults to the lab's own.
if (-not $ExchangeConfig) {
    $ExchangeConfig = Get-ActiveDirectoryLabExchangeConfig -Instance ExchangePrimary
}

if ($DirectoryConfig.UserObjectClass -ne "user") {
    throw "Scenario 034 needs Active Directory: Exchange recipients exist only there."
}

Write-TestSection "Scenario 034 Setup: Exchange Recipient Provisioning"

# Step 1: The provisioning substrate (HR CSV -> Metaverse -> Active Directory)
Write-TestStep "Step 1" "Running Setup-Scenario-001.ps1 for the provisioning substrate"

$setupParams = @{
    JIMUrl          = $JIMUrl
    ApiKey          = $ApiKey
    Template        = $Template
    DirectoryConfig = $DirectoryConfig
}
if ($PSBoundParameters.ContainsKey('ExportConcurrency')) { $setupParams.ExportConcurrency = $ExportConcurrency }
if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) { $setupParams.MaxExportParallelism = $MaxExportParallelism }

$config = & "$PSScriptRoot/Setup-Scenario-001.ps1" @setupParams
if (-not $config) {
    throw "Setup-Scenario-001.ps1 returned no configuration"
}
Write-Host "  ✓ Provisioning substrate configured" -ForegroundColor Green

Write-TestStep "Step 2" "Connecting to JIM"
$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  ✓ Connected to $JIMUrl" -ForegroundColor Green

try {
    # Step 3: Manage Exchange recipients, through ex1. Saving signs in to Exchange as the account JIM binds with, so a
    # failure here (Kerberos, the server's name, svc-jim's Recipient Management role) is the first thing to read.
    Write-TestStep "Step 3" "Managing Exchange recipients through $($ExchangeConfig.Host)"
    $ldapSystem = Get-JIMConnectedSystem -Id $config.LDAPSystemId
    $ldapConnector = Get-JIMConnectorDefinition | Where-Object { $_.name -eq "JIM LDAP Connector" }
    if (-not $ldapConnector) { throw "LDAP connector not found" }
    $settingsByName = @{}
    foreach ($setting in (Get-JIMConnectorDefinition -Id $ldapConnector.id).settings) { $settingsByName[$setting.name] = $setting.id }
    foreach ($required in "Manage Exchange recipients", "Exchange server") {
        if (-not $settingsByName.ContainsKey($required)) {
            throw "The LDAP Connector lists no '$required' setting"
        }
    }
    $saved = Set-JIMConnectedSystem -Id $ldapSystem.id -PassThru -SettingValues @{
        $settingsByName["Manage Exchange recipients"] = @{ checkboxValue = $true }
        $settingsByName["Exchange server"]            = @{ stringValue = $ExchangeConfig.Host }
    }
    $settingWarnings = @(if ($saved -and $saved.PSObject.Properties['settingWarnings']) { $saved.settingWarnings })
    if ($settingWarnings.Count -gt 0) {
        throw "Saving the Exchange settings raised warnings, so JIM could not confirm it can manage recipients: " +
              (@($settingWarnings | ForEach-Object { "$($_.setting): $($_.message)" }) -join '; ')
    }
    Write-Host "  ✓ Exchange server $($ExchangeConfig.Host) saved, and JIM signed in to it" -ForegroundColor Green

    # Step 4: Refresh the schema so the connector-defined Exchange attributes appear on user, and select them. A refresh
    # adds new attributes unselected and keeps every existing selection.
    Write-TestStep "Step 4" "Refreshing the schema for the Exchange attributes"
    Import-JIMConnectedSystemSchema -Id $ldapSystem.id -Confirm:$false | Out-Null
    $userType = @(Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes) | Where-Object { $_.name -eq 'user' } | Select-Object -First 1
    if (-not $userType) { throw "The LDAP Connected System has no 'user' Object Type after the schema refresh." }

    $exchangeAttributeNames = @("Exchange Recipient Type", "Exchange Alias", "Exchange Database", "Exchange Primary SMTP Address", "Exchange External Address")
    $exchangeAttributes = @{}
    foreach ($name in $exchangeAttributeNames) {
        $attribute = @($userType.attributes) | Where-Object { $_.name -eq $name } | Select-Object -First 1
        if (-not $attribute) {
            throw "The schema refresh did not add '$name' to 'user'. The Connector adds the Exchange attributes only while the Connected System manages Exchange recipients."
        }
        $exchangeAttributes[$name] = $attribute
    }
    $updates = @{}
    foreach ($attribute in $exchangeAttributes.Values) { $updates[[string]$attribute.id] = @{ selected = $true } }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $ldapSystem.id -ObjectTypeId $userType.id -AttributeUpdates $updates | Out-Null
    Write-Host "  ✓ $($exchangeAttributes.Count) Exchange attributes selected on 'user'" -ForegroundColor Green

    # Step 5: The Attribute Flows
    Write-TestStep "Step 5" "Flowing the Exchange attributes on the export Synchronisation Rule"
    $exportRuleName = "$($DirectoryConfig.ConnectedSystemName) Export Users"
    $exportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq $exportRuleName } | Select-Object -First 1
    if (-not $exportRule) { throw "Could not find the export Synchronisation Rule '$exportRuleName' that Setup-Scenario-001.ps1 creates." }

    $flows = [ordered]@{
        "Exchange Recipient Type"   = 'IIF(Eq(mv["Employee Type"], "ExchangeLab"), mv["Job Title"], null)'
        "Exchange Database"         = 'IIF(Eq(mv["Company"], "ExchangeLab Placed"), "' + $ExchangeConfig.ExcludedDatabase + '", IIF(Eq(mv["Company"], "ExchangeLab Missing"), "MDB99", null))'
        "Exchange Alias"            = 'IIF(Eq(mv["Employee Type"], "ExchangeLab"), Lower(mv["First Name"]) + Lower(mv["Last Name"]), null)'
        "Exchange External Address" = 'IIF(Eq(mv["Job Title"], "MailUser"), Lower(mv["First Name"]) + "." + Lower(mv["Last Name"]) + "@partner.example", null)'
    }
    $flowMappingIds = @{}
    foreach ($name in $flows.Keys) {
        $mapping = New-JIMSyncRuleMapping -SyncRuleId $exportRule.id -TargetConnectedSystemAttributeId $exchangeAttributes[$name].id -Expression $flows[$name]
        $flowMappingIds[$name] = $mapping.id
        Write-Host "  ✓ $name" -ForegroundColor Green
    }

    # A mail user's mail is its external address: Exchange writes that there when it mail-enables the account, so a
    # mail flow carrying the HR address would fight Exchange (the confirming import reads back the external address).
    # Setup-Scenario-001.ps1 flows Email to mail as an attribute mapping, which cannot become an expression in place,
    # so it is replaced. Everyone else keeps the HR address, which is what Exchange's address policy gives a mailbox.
    $mailAttribute = @($userType.attributes) | Where-Object { $_.name -eq 'mail' } | Select-Object -First 1
    $mailMapping = @(Get-JIMSyncRuleMapping -SyncRuleId $exportRule.id) |
        Where-Object { $_.targetConnectedSystemAttributeId -eq $mailAttribute.id } | Select-Object -First 1
    if (-not $mailMapping) { throw "Could not find the mail Attribute Flow Setup-Scenario-001.ps1 creates." }
    Remove-JIMSyncRuleMapping -SyncRuleId $exportRule.id -MappingId $mailMapping.id -Force | Out-Null
    $mailExpression = 'IIF(Eq(mv["Job Title"], "MailUser"), ' + $flows["Exchange External Address"] + ', mv["Email"])'
    New-JIMSyncRuleMapping -SyncRuleId $exportRule.id -TargetConnectedSystemAttributeId $mailAttribute.id -Expression $mailExpression | Out-Null
    Write-Host "  ✓ mail carries a mail user's external address, and everyone else's HR address" -ForegroundColor Green

    # Resource mailboxes live on disabled accounts. Scenario 001 flows userAccountControl enabled for everyone not
    # Archived, so for those three types the flow writes it disabled instead; the Connector refuses to create one
    # enabled.
    $uacAttribute = @($userType.attributes) | Where-Object { $_.name -eq 'userAccountControl' } | Select-Object -First 1
    $uacMapping = @(Get-JIMSyncRuleMapping -SyncRuleId $exportRule.id) |
        Where-Object { $_.targetConnectedSystemAttributeId -eq $uacAttribute.id } | Select-Object -First 1
    if (-not $uacMapping) { throw "Could not find the userAccountControl Attribute Flow Setup-Scenario-001.ps1 creates." }
    $resourceDisabled = 'IIF(Eq(mv["Status"], "Archived"), DisableUser(512), ' +
        'IIF(Eq(mv["Job Title"], "SharedMailbox"), DisableUser(512), ' +
        'IIF(Eq(mv["Job Title"], "RoomMailbox"), DisableUser(512), ' +
        'IIF(Eq(mv["Job Title"], "EquipmentMailbox"), DisableUser(512), EnableUser(512)))))'
    Set-JIMSyncRuleMapping -SyncRuleId $exportRule.id -MappingId $uacMapping.id -Expression $resourceDisabled | Out-Null
    Write-Host "  ✓ userAccountControl writes shared, room and equipment mailboxes disabled" -ForegroundColor Green
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

Write-TestSection "Scenario 034 Setup Complete"
Write-Host "Exchange server:       $($ExchangeConfig.Host)" -ForegroundColor Cyan
Write-Host "Export rule:           $exportRuleName (ID: $($exportRule.id))" -ForegroundColor Cyan
Write-Host ""

$config.ExportSyncRuleId = $exportRule.id
$config.ExchangeHost = $ExchangeConfig.Host
$config.ExchangeSettingIds = @{ Manage = $settingsByName["Manage Exchange recipients"]; Server = $settingsByName["Exchange server"] }
return $config
