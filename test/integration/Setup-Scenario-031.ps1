# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 031: AD FS sign-in

.DESCRIPTION
    Synchronises the lab's Active Directory into JIM the way a customer's first Active Directory Connected System does,
    so that an ordinary employee has a Metaverse Object before they sign in (#2441). Without one, JIM signs nobody in
    but its initial administrator. Creates:

    - the LDAP Connected System for dc-primary ($DirectoryConfig.ConnectedSystemName), bound as the delegated service
      account, importing users from OU=Users,OU=Corp only (the sign-in administrator lives in CN=Users and is not
      imported: JIM makes their Metaverse Object when they first sign in);
    - an import Synchronisation Rule projecting users to the Metaverse User type, flowing objectGUID to Object GUID
      (the attribute AD FS's oid claim is matched to, which the runner created before turning sign-in on),
      sAMAccountName to Account Name, userPrincipalName to User Principal Name and displayName to Display Name;
    - Full Import and Full Synchronisation run profiles.

    Returns a hashtable with LDAPSystemId, FullImportProfileId and FullSyncProfileId.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER DirectoryConfig
    dc-primary's configuration (Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary)

.EXAMPLE
    ./Setup-Scenario-031.ps1 -ApiKey "jim_..." -DirectoryConfig (Get-DirectoryConfig -DirectoryType ActiveDirectory)
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Template, ExportConcurrency and MaxExportParallelism are accepted for runner compatibility: the setup exports nothing.')]
param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1,

    [Parameter(Mandatory=$true)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

. "$PSScriptRoot/utils/Test-Helpers.ps1"

if (-not $ApiKey) { throw "API key required for authentication" }
if ("$($DirectoryConfig.DirectoryType)" -ne 'ActiveDirectory') {
    throw "Scenario 031 runs against the Active Directory lab only, not '$($DirectoryConfig.DirectoryType)'."
}

$systemName = $DirectoryConfig.ConnectedSystemName
$userContainerDn = $DirectoryConfig.UserContainer
Write-TestSection "Scenario 031 Setup: AD FS sign-in ($systemName, $userContainerDn)"

Write-TestStep "Step 1" "Importing the JIM PowerShell module and connecting"
$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  OK Connected to JIM" -ForegroundColor Green

Write-TestStep "Step 2" "Creating the LDAP Connected System ($systemName)"
$existing = @(Get-JIMConnectedSystem) | Where-Object { $_.name -eq $systemName }
if ($existing) {
    Remove-JIMConnectedSystem -Id $existing.id -DeleteImmediately | Out-Null
}
$ldapConnector = Get-JIMConnectorDefinition | Where-Object { $_.name -eq "JIM LDAP Connector" }
if (-not $ldapConnector) { throw "LDAP connector not found" }
$ldapSystem = New-JIMConnectedSystem -Name $systemName -Description "dc-primary, for the AD FS sign-in checks" -ConnectorDefinitionId $ldapConnector.id -PassThru

$settingsByName = @{}
foreach ($setting in (Get-JIMConnectorDefinition -Id $ldapConnector.id).settings) { $settingsByName[$setting.name] = $setting.id }
$values = @{
    $settingsByName["Host"]                           = @{ stringValue = $DirectoryConfig.Host }
    $settingsByName["Port"]                           = @{ intValue = $DirectoryConfig.Port }
    $settingsByName["Username"]                       = @{ stringValue = $DirectoryConfig.JimBindDN }
    $settingsByName["Password"]                       = @{ stringValue = $DirectoryConfig.JimBindPassword }
    $settingsByName["Use Secure Connection (LDAPS)?"] = @{ checkboxValue = $DirectoryConfig.UseSSL }
    $settingsByName["Connection Timeout"]             = @{ intValue = 30 }
    $settingsByName["Authentication Type"]            = @{ stringValue = $DirectoryConfig.AuthType }
}
Set-JIMConnectedSystem -Id $ldapSystem.id -SettingValues $values | Out-Null
Write-Host "  OK Created and configured (ID: $($ldapSystem.id))" -ForegroundColor Green

Write-TestStep "Step 3" "Importing the schema and selecting the user attributes"
Import-JIMConnectedSystemSchema -Id $ldapSystem.id | Out-Null
$userObjectType = @(Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes) | Where-Object { $_.name -eq $DirectoryConfig.UserObjectClass }
if (-not $userObjectType) { throw "No '$($DirectoryConfig.UserObjectClass)' object type in the schema" }
Set-JIMConnectedSystemObjectType -ConnectedSystemId $ldapSystem.id -ObjectTypeId $userObjectType.id -Selected $true | Out-Null
$wanted = @('objectGUID', 'sAMAccountName', 'userPrincipalName', 'displayName', 'givenName', 'sn', 'distinguishedName')
$updates = @{}
foreach ($attribute in $userObjectType.attributes) {
    if ($attribute.name -in $wanted) { $updates[$attribute.id] = @{ selected = $true } }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $ldapSystem.id -ObjectTypeId $userObjectType.id -AttributeUpdates $updates -ErrorAction Stop | Out-Null
Write-Host "  OK Selected $($updates.Count) attributes" -ForegroundColor Green

Write-TestStep "Step 4" "Selecting $userContainerDn"
Import-JIMConnectedSystemHierarchy -Id $ldapSystem.id | Out-Null
$partition = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id) |
    Where-Object { $_.name -eq $DirectoryConfig.BaseDN -or $_.externalId -eq $DirectoryConfig.BaseDN } | Select-Object -First 1
if (-not $partition) { throw "No partition $($DirectoryConfig.BaseDN)" }
Set-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id -PartitionId $partition.id -Selected $true | Out-Null

# The container path below the partition, outermost first: OU=Users,OU=Corp,DC=... is Corp, then Users.
$path = @(($userContainerDn.Substring(0, $userContainerDn.Length - $DirectoryConfig.BaseDN.Length).TrimEnd(',') -split ',') |
    ForEach-Object { ($_ -replace '^(?i)(OU|CN)=', '') })
[array]::Reverse($path)
$level = @($partition.containers)
$container = $null
foreach ($name in $path) {
    $container = $level | Where-Object { $_.name -eq $name -or $_.name -match "^(?i)(OU|CN)=$([regex]::Escape($name))(,|$)" } | Select-Object -First 1
    if (-not $container) { throw "No container '$name' on the way to $userContainerDn" }
    $level = @(if ($container.PSObject.Properties['childContainers']) { $container.childContainers } else { @() })
}
Set-JIMConnectedSystemContainer -ConnectedSystemId $ldapSystem.id -ContainerId $container.id -Selected $true | Out-Null
Write-Host "  OK Selected $userContainerDn (container ID: $($container.id))" -ForegroundColor Green

Write-TestStep "Step 5" "Creating the import Synchronisation Rule"
$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
$mvAttributes = @(Get-JIMMetaverseAttribute)
$importRule = New-JIMSyncRule -Name "$systemName Import Users (sign-in)" -ConnectedSystemId $ldapSystem.id `
    -ConnectedSystemObjectTypeId $userObjectType.id -MetaverseObjectTypeId $mvUserType.id -Direction Import -ProjectToMetaverse -PassThru
$userObjectType = @(Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes) | Where-Object { $_.name -eq $DirectoryConfig.UserObjectClass }
foreach ($mapping in @(
        @{ CS = 'objectGUID'; MV = 'Object GUID' },
        @{ CS = 'sAMAccountName'; MV = 'Account Name' },
        @{ CS = 'userPrincipalName'; MV = 'User Principal Name' },
        @{ CS = 'displayName'; MV = 'Display Name' }
    )) {
    $csAttribute = $userObjectType.attributes | Where-Object { $_.name -eq $mapping.CS }
    $mvAttribute = $mvAttributes | Where-Object { $_.name -eq $mapping.MV }
    if (-not $csAttribute -or -not $mvAttribute) { throw "Cannot map $($mapping.CS) to $($mapping.MV): one of them does not exist" }
    New-JIMSyncRuleMapping -SyncRuleId $importRule.id -TargetMetaverseAttributeId $mvAttribute.id -SourceConnectedSystemAttributeId $csAttribute.id | Out-Null
}
Write-Host "  OK Created '$($importRule.name)' with 4 Attribute Flows" -ForegroundColor Green

Write-TestStep "Step 6" "Creating run profiles"
$fullImport = New-JIMRunProfile -Name "Full Import" -ConnectedSystemId $ldapSystem.id -RunType "FullImport" -PassThru
$fullSync = New-JIMRunProfile -Name "Full Synchronisation" -ConnectedSystemId $ldapSystem.id -RunType "FullSynchronisation" -PassThru
Write-Host "  OK Created Full Import and Full Synchronisation" -ForegroundColor Green

return @{
    LDAPSystemId        = $ldapSystem.id
    FullImportProfileId = $fullImport.id
    FullSyncProfileId   = $fullSync.id
}
