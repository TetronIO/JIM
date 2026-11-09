# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Setup for Scenario 003: GAL synchronisation between PANOPLY and RESURGAM

.DESCRIPTION
    Configures JIM to keep two Exchange organisations' address books in step, as a customer with two forests would
    (#2021, PRD Scenario 4 and requirements 21 to 24): each forest's mailboxes, and PANOPLY's mail-enabled group, appear
    in the other forest as mail contacts in OU=GALSync,OU=Corp, carrying an X500: proxy of their Legacy Exchange DN, and
    none is projected back as a person.

    There is no Contact Metaverse Object Type (PRD decision 17): a person or group in the Metaverse is one identity,
    provisioned to each forest in the form it needs, an account in its own forest and a contact in the other. "From
    PANOPLY, not from RESURGAM" is a connection criterion (decision 18), and loop safety comes from Synchronisation Rule
    isolation, not from leaving GALSync out of scope (decision 19).

    PANOPLY is composed from Setup-Scenario-034.ps1 (Scenario 001's HR source and Active Directory target, managing
    Exchange recipients through exchange-primary): its people with Employee Type "ExchangeLab" get mailboxes there. This
    script adds:

      PANOPLY
        - the export rule leaves people with Employee Type "ResurgamLab" to RESURGAM;
        - "Panoply AD Import Users" brings back each mailbox's Legacy Exchange DN and Proxy Addresses;
        - the group Object Type, with "Panoply AD Import Groups" projecting the test group (Account Name gal-*) and
          "Panoply AD Mail-enable Groups" making it a mail-enabled group through Exchange;
        - the contact Object Type and OU=GALSync, with "Panoply AD Export GAL Contacts" (people from RESURGAM) and
          "Panoply AD Import Contacts" for PANOPLY's own contacts, scoped away from GALSync;

      RESURGAM (a new LDAP Connected System, managing Exchange recipients through exchange-source)
        - "Resurgam AD Export Users" provisioning Employee Type "ResurgamLab" people with mailboxes into OU=Corp;
        - "Resurgam AD Import Users" bringing back their Legacy Exchange DN and Proxy Addresses;
        - "Resurgam AD Export GAL Contacts" (people from PANOPLY) and "Resurgam AD Export GAL Group Contacts" (groups from
          PANOPLY) into OU=GALSync;
        - "Resurgam AD Import Contacts" for RESURGAM's own contacts, projecting, scoped away from GALSync.

    Every contact rule declares Exchange Recipient Type "MailContact" with the source's address as its external address,
    and the X500 proxy Phase 7b proved:
        Split(Join(mv["Proxy Addresses"], "|") + IIF(HasValue(mv["Legacy Exchange DN"]), "|X500:" + mv["Legacy Exchange DN"], ""), "|")
    A contact rule is scoped to identities with a Legacy Exchange DN, so a contact appears only once its source is a
    recipient whose address JIM has imported.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER DirectoryConfig
    The Active Directory lab's Primary (PANOPLY)

.PARAMETER SourceConfig
    The Active Directory lab's Source (RESURGAM). Defaults to Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Source.

.PARAMETER PrimaryExchangeConfig
    exchange-primary. Defaults to Get-ActiveDirectoryLabExchangeConfig -Instance ExchangePrimary.

.PARAMETER SourceExchangeConfig
    exchange-source. Defaults to Get-ActiveDirectoryLabExchangeConfig -Instance ExchangeSource.

.PARAMETER Template
    Ignored: GAL synchronisation is about the handful of recipients Scenario 003 adds, so PANOPLY is set up at Nano.
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$true)]
    [string]$ApiKey,

    [Parameter(Mandatory=$true)]
    [hashtable]$DirectoryConfig,

    [Parameter(Mandatory=$false)]
    [hashtable]$SourceConfig,

    [Parameter(Mandatory=$false)]
    [hashtable]$PrimaryExchangeConfig,

    [Parameter(Mandatory=$false)]
    [hashtable]$SourceExchangeConfig,

    # Accepted for the runner's -SetupOnly, which passes it to every setup script; GAL synchronisation runs at Nano.
    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

. "$PSScriptRoot/utils/Test-Helpers.ps1"

# The runner's -SetupOnly passes the standard parameters only, so RESURGAM and both Exchange servers default to the
# lab's own.
if (-not $SourceConfig) { $SourceConfig = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Source }
if (-not $PrimaryExchangeConfig) { $PrimaryExchangeConfig = Get-ActiveDirectoryLabExchangeConfig -Instance ExchangePrimary }
if (-not $SourceExchangeConfig) { $SourceExchangeConfig = Get-ActiveDirectoryLabExchangeConfig -Instance ExchangeSource }

$x500Proxy = 'Split(Join(mv["Proxy Addresses"], "|") + IIF(HasValue(mv["Legacy Exchange DN"]), "|X500:" + mv["Legacy Exchange DN"], ""), "|")'
$galOuName = 'GALSync'

Write-TestSection "Scenario 003 Setup: GAL synchronisation between PANOPLY and RESURGAM"

# Step 1: PANOPLY, its HR source and its Exchange (Scenario 034's setup, at Nano: GAL sync is about the handful of
# recipients the scenario adds, not the population around them).
Write-TestStep "Step 1" "Running Setup-Scenario-034.ps1 for HR, PANOPLY and exchange-primary"
$config = & "$PSScriptRoot/Setup-Scenario-034.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template Nano `
    -DirectoryConfig $DirectoryConfig -ExchangeConfig $PrimaryExchangeConfig
if (-not $config) { throw "Setup-Scenario-034.ps1 returned no configuration" }

Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1" -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

function Get-ObjectType {
    param([int]$ConnectedSystemId, [string]$Name)
    $type = @(Get-JIMConnectedSystem -Id $ConnectedSystemId -ObjectTypes) | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $type) { throw "Connected System $ConnectedSystemId has no '$Name' Object Type." }
    return $type
}

function Select-ObjectTypeAttribute {
    # Selects an Object Type and the named attributes on it, and returns it as it then stands. Every name must exist.
    param([int]$ConnectedSystemId, [string]$ObjectTypeName, [string[]]$AttributeNames)
    $type = Get-ObjectType -ConnectedSystemId $ConnectedSystemId -Name $ObjectTypeName
    if (-not $type.selected) {
        Set-JIMConnectedSystemObjectType -ConnectedSystemId $ConnectedSystemId -ObjectTypeId $type.id -Selected $true | Out-Null
    }
    $updates = @{}
    foreach ($name in $AttributeNames) {
        $attribute = @($type.attributes) | Where-Object { $_.name -eq $name } | Select-Object -First 1
        if (-not $attribute) { throw "'$ObjectTypeName' on Connected System $ConnectedSystemId has no attribute '$name'." }
        $updates[[string]$attribute.id] = @{ selected = $true }
    }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $ConnectedSystemId -ObjectTypeId $type.id -AttributeUpdates $updates | Out-Null
    return (Get-ObjectType -ConnectedSystemId $ConnectedSystemId -Name $ObjectTypeName)
}

function Get-AttributeId {
    param($ObjectType, [string]$Name)
    $attribute = @($ObjectType.attributes) | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $attribute) { throw "'$($ObjectType.name)' has no attribute '$Name'." }
    return $attribute.id
}

function Select-Container {
    # Selects the container at $ContainerDn (and so its subtree) under the Connected System's selected partition.
    param([int]$ConnectedSystemId, [string]$BaseDn, [string]$ContainerDn)
    $partition = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ConnectedSystemId) |
        Where-Object { $_.name -eq $BaseDn -or $_.externalId -eq $BaseDn } | Select-Object -First 1
    if (-not $partition) { throw "Connected System $ConnectedSystemId has no partition $BaseDn." }
    if (-not $partition.selected) {
        Set-JIMConnectedSystemPartition -ConnectedSystemId $ConnectedSystemId -PartitionId $partition.id -Selected $true | Out-Null
        Import-JIMConnectedSystemHierarchy -Id $ConnectedSystemId | Out-Null
        $partition = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ConnectedSystemId) | Where-Object { $_.id -eq $partition.id }
    }
    # The container path below the partition, outermost first: OU=GALSync,OU=Corp,DC=... is Corp, then GALSync.
    $path = @(($ContainerDn.Substring(0, $ContainerDn.Length - $BaseDn.Length).TrimEnd(',') -split ',') | ForEach-Object { ($_ -replace '^(?i)(OU|CN)=', '') })
    [array]::Reverse($path)
    $level = @($partition.containers)
    $container = $null
    foreach ($name in $path) {
        $container = $level | Where-Object { $_.name -eq $name -or $_.name -match "^(?i)(OU|CN)=$([regex]::Escape($name))(,|$)" } | Select-Object -First 1
        if (-not $container) { throw "No container '$name' on the way to $ContainerDn on Connected System $ConnectedSystemId." }
        $level = @(if ($container.PSObject.Properties['childContainers']) { $container.childContainers } else { @() })
    }
    if (-not $container.selected) {
        Set-JIMConnectedSystemContainer -ConnectedSystemId $ConnectedSystemId -ContainerId $container.id -Selected $true | Out-Null
    }
}

function Add-ExpressionFlow {
    param([int]$SyncRuleId, $ObjectType, [string]$Target, [string]$Expression)
    New-JIMSyncRuleMapping -SyncRuleId $SyncRuleId -TargetConnectedSystemAttributeId (Get-AttributeId -ObjectType $ObjectType -Name $Target) -Expression $Expression | Out-Null
}

function Add-ImportFlow {
    param([int]$SyncRuleId, $ObjectType, [string]$Source, [string]$Target)
    $mvAttribute = $script:MvAttributes | Where-Object { $_.name -eq $Target } | Select-Object -First 1
    if (-not $mvAttribute) { throw "No Metaverse attribute '$Target'." }
    New-JIMSyncRuleMapping -SyncRuleId $SyncRuleId -TargetMetaverseAttributeId $mvAttribute.id -SourceConnectedSystemAttributeId (Get-AttributeId -ObjectType $ObjectType -Name $Source) | Out-Null
}

function New-ScopeGroup {
    param([int]$SyncRuleId)
    return (New-JIMScopingCriteriaGroup -SyncRuleId $SyncRuleId -Type All -PassThru)
}

function New-ContactExportRule {
    # A mail contact in $ContactOuDn for each identity of $MetaverseTypeName that has an object of $SourceSystemName's
    # $SourceObjectTypeName (the connection criterion) and a Legacy Exchange DN, that is, a recipient there whose
    # address JIM has imported.
    param([string]$Name, [int]$ConnectedSystemId, $ContactType, [string]$MetaverseTypeName, [string]$SourceSystemName,
        [string]$SourceObjectTypeName, [string]$ContactOuDn, [string]$AliasExpression)
    $mvType = Get-JIMMetaverseObjectType -Name $MetaverseTypeName
    $rule = New-JIMSyncRule -Name $Name -ConnectedSystemId $ConnectedSystemId -ConnectedSystemObjectTypeId $ContactType.id `
        -MetaverseObjectTypeId $mvType.id -Direction Export -ProvisionToConnectedSystem -OutboundDeprovisionAction Delete -PassThru
    $group = New-ScopeGroup -SyncRuleId $rule.id
    New-JIMScopingCriterion -SyncRuleId $rule.id -GroupId $group.id -ConnectedSystemName $SourceSystemName -ConnectedObjectTypeName $SourceObjectTypeName -ComparisonType HasValue | Out-Null
    New-JIMScopingCriterion -SyncRuleId $rule.id -GroupId $group.id -MetaverseAttributeName 'Legacy Exchange DN' -ComparisonType HasValue | Out-Null
    Add-ExpressionFlow -SyncRuleId $rule.id -ObjectType $ContactType -Target 'distinguishedName' -Expression ('"CN=" + EscapeDN(mv["Display Name"]) + ",' + $ContactOuDn + '"')
    Add-ExpressionFlow -SyncRuleId $rule.id -ObjectType $ContactType -Target 'displayName' -Expression 'mv["Display Name"]'
    Add-ExpressionFlow -SyncRuleId $rule.id -ObjectType $ContactType -Target 'proxyAddresses' -Expression $x500Proxy
    Add-ExpressionFlow -SyncRuleId $rule.id -ObjectType $ContactType -Target 'Exchange Recipient Type' -Expression '"MailContact"'
    Add-ExpressionFlow -SyncRuleId $rule.id -ObjectType $ContactType -Target 'Exchange External Address' -Expression 'mv["Email"]'
    Add-ExpressionFlow -SyncRuleId $rule.id -ObjectType $ContactType -Target 'Exchange Alias' -Expression $AliasExpression
    return $rule
}

function New-ContactImportRule {
    # A forest's own contacts, projected, scoped away from GALSync: JIM's GAL contacts there are joined by the contact
    # export rule and must never be projected back as people (Synchronisation Rule isolation keeps those joins the
    # export rule's own).
    param([string]$Name, [int]$ConnectedSystemId, $ContactType)
    $mvType = Get-JIMMetaverseObjectType -Name 'User'
    $rule = New-JIMSyncRule -Name $Name -ConnectedSystemId $ConnectedSystemId -ConnectedSystemObjectTypeId $ContactType.id `
        -MetaverseObjectTypeId $mvType.id -Direction Import -ProjectToMetaverse -PassThru
    $group = New-ScopeGroup -SyncRuleId $rule.id
    New-JIMScopingCriterion -SyncRuleId $rule.id -GroupId $group.id -ConnectedSystemAttributeName 'distinguishedName' -ComparisonType NotContains -StringValue ",OU=$galOuName," | Out-Null
    Add-ImportFlow -SyncRuleId $rule.id -ObjectType $ContactType -Source 'displayName' -Target 'Display Name'
    return $rule
}

try {
    $script:MvAttributes = @(Get-JIMMetaverseAttribute)
    $panoplyId = $config.LDAPSystemId
    $panoplyName = $DirectoryConfig.ConnectedSystemName
    $sourceName = $SourceConfig.ConnectedSystemName

    # ─────────────────────────────────────────────────────────────────────────────────────────────
    # Step 2: PANOPLY's additions
    # ─────────────────────────────────────────────────────────────────────────────────────────────
    Write-TestStep "Step 2" "PANOPLY: leaving RESURGAM's people to RESURGAM, and importing mailbox addresses"
    $panoplyExport = @(Get-JIMSyncRule) | Where-Object { $_.name -eq "$panoplyName Export Users" } | Select-Object -First 1
    $exportScope = @(Get-JIMScopingCriteria -SyncRuleId $panoplyExport.id) | Select-Object -First 1
    if (-not $exportScope) { throw "'$panoplyName Export Users' has no scoping criteria group to add to." }
    New-JIMScopingCriterion -SyncRuleId $panoplyExport.id -GroupId $exportScope.id -MetaverseAttributeName 'Employee Type' -ComparisonType NotEquals -StringValue 'ResurgamLab' | Out-Null

    $panoplyUser = Select-ObjectTypeAttribute -ConnectedSystemId $panoplyId -ObjectTypeName 'user' -AttributeNames @('legacyExchangeDN', 'proxyAddresses')
    $mvUserType = Get-JIMMetaverseObjectType -Name 'User'
    $panoplyUserImport = New-JIMSyncRule -Name "$panoplyName Import Users" -ConnectedSystemId $panoplyId -ConnectedSystemObjectTypeId $panoplyUser.id `
        -MetaverseObjectTypeId $mvUserType.id -Direction Import -PassThru
    Add-ImportFlow -SyncRuleId $panoplyUserImport.id -ObjectType $panoplyUser -Source 'legacyExchangeDN' -Target 'Legacy Exchange DN'
    Add-ImportFlow -SyncRuleId $panoplyUserImport.id -ObjectType $panoplyUser -Source 'proxyAddresses' -Target 'Proxy Addresses'
    Write-Host "  ✓ Export Users leaves ResurgamLab people out; Import Users brings back Legacy Exchange DN and Proxy Addresses" -ForegroundColor Green

    Write-TestStep "Step 3" "PANOPLY: the test group, imported and mail-enabled through Exchange"
    $panoplyGroup = Select-ObjectTypeAttribute -ConnectedSystemId $panoplyId -ObjectTypeName 'group' -AttributeNames @(
        'cn', 'sAMAccountName', 'mail', 'legacyExchangeDN', 'proxyAddresses', 'distinguishedName',
        'Exchange Recipient Type', 'Exchange Alias', 'Exchange Primary SMTP Address')
    $mvGroupType = Get-JIMMetaverseObjectType -Name 'Group'
    $groupImport = New-JIMSyncRule -Name "$panoplyName Import Groups" -ConnectedSystemId $panoplyId -ConnectedSystemObjectTypeId $panoplyGroup.id `
        -MetaverseObjectTypeId $mvGroupType.id -Direction Import -ProjectToMetaverse -PassThru
    $scope = New-ScopeGroup -SyncRuleId $groupImport.id
    New-JIMScopingCriterion -SyncRuleId $groupImport.id -GroupId $scope.id -ConnectedSystemAttributeName 'sAMAccountName' -ComparisonType StartsWith -StringValue 'gal-' | Out-Null
    Add-ImportFlow -SyncRuleId $groupImport.id -ObjectType $panoplyGroup -Source 'sAMAccountName' -Target 'Account Name'
    Add-ImportFlow -SyncRuleId $groupImport.id -ObjectType $panoplyGroup -Source 'cn' -Target 'Display Name'
    Add-ImportFlow -SyncRuleId $groupImport.id -ObjectType $panoplyGroup -Source 'mail' -Target 'Email'
    Add-ImportFlow -SyncRuleId $groupImport.id -ObjectType $panoplyGroup -Source 'legacyExchangeDN' -Target 'Legacy Exchange DN'
    Add-ImportFlow -SyncRuleId $groupImport.id -ObjectType $panoplyGroup -Source 'proxyAddresses' -Target 'Proxy Addresses'
    $groupMailEnable = New-JIMSyncRule -Name "$panoplyName Mail-enable Groups" -ConnectedSystemId $panoplyId -ConnectedSystemObjectTypeId $panoplyGroup.id `
        -MetaverseObjectTypeId $mvGroupType.id -Direction Export -PassThru
    $scope = New-ScopeGroup -SyncRuleId $groupMailEnable.id
    New-JIMScopingCriterion -SyncRuleId $groupMailEnable.id -GroupId $scope.id -MetaverseAttributeName 'Account Name' -ComparisonType StartsWith -StringValue 'gal-' | Out-Null
    Add-ExpressionFlow -SyncRuleId $groupMailEnable.id -ObjectType $panoplyGroup -Target 'Exchange Recipient Type' -Expression '"MailEnabledGroup"'
    Add-ExpressionFlow -SyncRuleId $groupMailEnable.id -ObjectType $panoplyGroup -Target 'Exchange Alias' -Expression 'mv["Account Name"]'
    Write-Host "  ✓ gal-* groups are projected and made mail-enabled groups" -ForegroundColor Green

    Write-TestStep "Step 4" "PANOPLY: contacts for RESURGAM's people in $galOuName, and PANOPLY's own contacts"
    Select-Container -ConnectedSystemId $panoplyId -BaseDn $DirectoryConfig.BaseDN -ContainerDn $PrimaryExchangeConfig.ContactOuDn
    $contactAttributes = @('cn', 'displayName', 'proxyAddresses', 'distinguishedName',
        'Exchange Recipient Type', 'Exchange Alias', 'Exchange Primary SMTP Address', 'Exchange External Address')
    $panoplyContact = Select-ObjectTypeAttribute -ConnectedSystemId $panoplyId -ObjectTypeName 'contact' -AttributeNames $contactAttributes
    New-ContactExportRule -Name "$panoplyName Export GAL Contacts" -ConnectedSystemId $panoplyId -ContactType $panoplyContact `
        -MetaverseTypeName 'User' -SourceSystemName $sourceName -SourceObjectTypeName 'user' -ContactOuDn $PrimaryExchangeConfig.ContactOuDn `
        -AliasExpression 'Lower(mv["First Name"] + mv["Last Name"])' | Out-Null
    New-ContactImportRule -Name "$panoplyName Import Contacts" -ConnectedSystemId $panoplyId -ContactType $panoplyContact | Out-Null
    Write-Host "  ✓ $galOuName selected; GAL contact export and own-contact import rules created" -ForegroundColor Green

    # ─────────────────────────────────────────────────────────────────────────────────────────────
    # Step 5: RESURGAM
    # ─────────────────────────────────────────────────────────────────────────────────────────────
    Write-TestStep "Step 5" "RESURGAM: the Connected System, managing Exchange recipients through $($SourceExchangeConfig.Host)"
    $existing = @(Get-JIMConnectedSystem) | Where-Object { $_.name -eq $sourceName }
    if ($existing) { Remove-JIMConnectedSystem -Id $existing[0].id -DeleteImmediately -Force | Out-Null }
    $ldapConnector = Get-JIMConnectorDefinition | Where-Object { $_.name -eq "JIM LDAP Connector" }
    $resurgam = New-JIMConnectedSystem -Name $sourceName -Description "dc-source, for GAL synchronisation (Scenario 003)" -ConnectorDefinitionId $ldapConnector.id -PassThru
    $settingsByName = @{}
    foreach ($setting in (Get-JIMConnectorDefinition -Id $ldapConnector.id).settings) { $settingsByName[$setting.name] = $setting.id }
    # One save: the Exchange sign-in check runs with the directory settings it derives Kerberos from.
    $saved = Set-JIMConnectedSystem -Id $resurgam.id -PassThru -SettingValues @{
        $settingsByName["Host"]                           = @{ stringValue = $SourceConfig.Host }
        $settingsByName["Port"]                           = @{ intValue = $SourceConfig.Port }
        $settingsByName["Username"]                       = @{ stringValue = $SourceConfig.JimBindDN }
        $settingsByName["Password"]                       = @{ stringValue = $SourceConfig.JimBindPassword }
        $settingsByName["Use Secure Connection (LDAPS)?"] = @{ checkboxValue = $SourceConfig.UseSSL }
        $settingsByName["Connection Timeout"]             = @{ intValue = 30 }
        $settingsByName["Authentication Type"]            = @{ stringValue = $SourceConfig.AuthType }
        $settingsByName["Manage Exchange recipients"]     = @{ checkboxValue = $true }
        $settingsByName["Exchange server"]                = @{ stringValue = $SourceExchangeConfig.Host }
    }
    $settingWarnings = @(if ($saved -and $saved.PSObject.Properties['settingWarnings']) { $saved.settingWarnings })
    if ($settingWarnings.Count -gt 0) {
        throw "Saving RESURGAM's settings raised warnings, so JIM could not confirm it can manage recipients there: " +
              (@($settingWarnings | ForEach-Object { "$($_.setting): $($_.message)" }) -join '; ')
    }
    Import-JIMConnectedSystemSchema -Id $resurgam.id -Confirm:$false | Out-Null
    Import-JIMConnectedSystemHierarchy -Id $resurgam.id | Out-Null
    Select-Container -ConnectedSystemId $resurgam.id -BaseDn $SourceConfig.BaseDN -ContainerDn "OU=Corp,$($SourceConfig.BaseDN)"
    Write-Host "  ✓ '$sourceName' created (ID: $($resurgam.id)); OU=Corp (with $galOuName) selected" -ForegroundColor Green

    Write-TestStep "Step 6" "RESURGAM: its people, with mailboxes, and their addresses imported"
    $resurgamUser = Select-ObjectTypeAttribute -ConnectedSystemId $resurgam.id -ObjectTypeName 'user' -AttributeNames @(
        'sAMAccountName', 'givenName', 'sn', 'displayName', 'mail', 'userPrincipalName', 'employeeID', 'distinguishedName',
        'legacyExchangeDN', 'proxyAddresses', 'Exchange Recipient Type', 'Exchange Alias', 'Exchange Database',
        'Exchange Primary SMTP Address', 'Exchange External Address')
    $resurgamExport = New-JIMSyncRule -Name "$sourceName Export Users" -ConnectedSystemId $resurgam.id -ConnectedSystemObjectTypeId $resurgamUser.id `
        -MetaverseObjectTypeId $mvUserType.id -Direction Export -ProvisionToConnectedSystem -OutboundDeprovisionAction Delete -PassThru
    $scope = New-ScopeGroup -SyncRuleId $resurgamExport.id
    New-JIMScopingCriterion -SyncRuleId $resurgamExport.id -GroupId $scope.id -MetaverseAttributeName 'Employee Type' -ComparisonType Equals -StringValue 'ResurgamLab' | Out-Null
    # No userAccountControl and no password: Active Directory creates the account disabled, which a mailbox does not
    # mind, and these people never sign in.
    $userFlows = [ordered]@{
        'distinguishedName'       = '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Corp,' + $SourceConfig.BaseDN + '"'
        'sAMAccountName'          = 'mv["Account Name"]'
        'givenName'               = 'mv["First Name"]'
        'sn'                      = 'mv["Last Name"]'
        'displayName'             = 'mv["Display Name"]'
        'mail'                    = 'mv["Email"]'
        'userPrincipalName'       = 'mv["Email"]'
        'employeeID'              = 'mv["Employee ID"]'
        'Exchange Recipient Type' = 'mv["Job Title"]'
        'Exchange Alias'          = 'Lower(mv["First Name"] + mv["Last Name"])'
    }
    foreach ($target in $userFlows.Keys) { Add-ExpressionFlow -SyncRuleId $resurgamExport.id -ObjectType $resurgamUser -Target $target -Expression $userFlows[$target] }
    $resurgamUserImport = New-JIMSyncRule -Name "$sourceName Import Users" -ConnectedSystemId $resurgam.id -ConnectedSystemObjectTypeId $resurgamUser.id `
        -MetaverseObjectTypeId $mvUserType.id -Direction Import -PassThru
    Add-ImportFlow -SyncRuleId $resurgamUserImport.id -ObjectType $resurgamUser -Source 'legacyExchangeDN' -Target 'Legacy Exchange DN'
    Add-ImportFlow -SyncRuleId $resurgamUserImport.id -ObjectType $resurgamUser -Source 'proxyAddresses' -Target 'Proxy Addresses'
    $employeeIdAttribute = $script:MvAttributes | Where-Object { $_.name -eq 'Employee ID' } | Select-Object -First 1
    New-JIMMatchingRule -ConnectedSystemId $resurgam.id -ObjectTypeId $resurgamUser.id -MetaverseObjectTypeId $mvUserType.id `
        -SourceAttributeId (Get-AttributeId -ObjectType $resurgamUser -Name 'employeeID') -TargetMetaverseAttributeId $employeeIdAttribute.id | Out-Null
    Write-Host "  ✓ ResurgamLab people provisioned into OU=Corp with mailboxes; their addresses imported" -ForegroundColor Green

    Write-TestStep "Step 7" "RESURGAM: contacts for PANOPLY's people and group in $galOuName, and RESURGAM's own contacts"
    $resurgamContact = Select-ObjectTypeAttribute -ConnectedSystemId $resurgam.id -ObjectTypeName 'contact' -AttributeNames $contactAttributes
    New-ContactExportRule -Name "$sourceName Export GAL Contacts" -ConnectedSystemId $resurgam.id -ContactType $resurgamContact `
        -MetaverseTypeName 'User' -SourceSystemName $panoplyName -SourceObjectTypeName 'user' -ContactOuDn $SourceExchangeConfig.ContactOuDn `
        -AliasExpression 'Lower(mv["First Name"] + mv["Last Name"])' | Out-Null
    New-ContactExportRule -Name "$sourceName Export GAL Group Contacts" -ConnectedSystemId $resurgam.id -ContactType $resurgamContact `
        -MetaverseTypeName 'Group' -SourceSystemName $panoplyName -SourceObjectTypeName 'group' -ContactOuDn $SourceExchangeConfig.ContactOuDn `
        -AliasExpression 'mv["Account Name"]' | Out-Null
    New-ContactImportRule -Name "$sourceName Import Contacts" -ConnectedSystemId $resurgam.id -ContactType $resurgamContact | Out-Null
    Write-Host "  ✓ GAL contact export rules (people and groups) and own-contact import rule created" -ForegroundColor Green

    Write-TestStep "Step 8" "RESURGAM: run profiles"
    $profiles = @{}
    foreach ($runProfile in @(
            @{ Key = 'FullImport'; Name = 'Full Import'; RunType = 'FullImport' },
            @{ Key = 'DeltaImport'; Name = 'Delta Import'; RunType = 'DeltaImport' },
            @{ Key = 'FullSync'; Name = 'Full Synchronisation'; RunType = 'FullSynchronisation' },
            @{ Key = 'DeltaSync'; Name = 'Delta Synchronisation'; RunType = 'DeltaSynchronisation' },
            @{ Key = 'Export'; Name = 'Export'; RunType = 'Export' })) {
        $profiles[$runProfile.Key] = (New-JIMRunProfile -Name $runProfile.Name -ConnectedSystemId $resurgam.id -RunType $runProfile.RunType -PassThru).id
    }
    Write-Host "  ✓ Full Import, Delta Import, Full Synchronisation, Delta Synchronisation and Export" -ForegroundColor Green
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

Write-TestSection "Scenario 003 Setup Complete"
$config.SourceLDAPSystemId = $resurgam.id
$config.SourceLDAPFullImportProfileId = $profiles.FullImport
$config.SourceLDAPDeltaImportProfileId = $profiles.DeltaImport
$config.SourceLDAPFullSyncProfileId = $profiles.FullSync
$config.SourceLDAPDeltaSyncProfileId = $profiles.DeltaSync
$config.SourceLDAPExportProfileId = $profiles.Export
return $config
