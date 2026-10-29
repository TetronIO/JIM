# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 033: initial administrator provisioning

.DESCRIPTION
    Configures two File Connector Connected Systems and the Synchronisation Rules between them and the Metaverse, the
    shape of the "Your First Synchronisation" tutorial with no people in it:

      - Scenario 033 HR: import only, from a CSV holding a header and no rows. Its import rule projects any person it
        held (Employee ID and Display Name), and an Object Matching Rule joins on Employee ID.
      - Scenario 033 Directory: export only, to a CSV holding a header and no rows. Its export rule provisions every
        User in the Metaverse to it, with no scoping criteria, unless -OmitExportRule asks for it to be left out so the
        scenario can create it after the initial administrator has signed in.

    Run Profiles: HR has Full Import, Full Synchronisation and Delta Synchronisation; the directory has Export.

    Anything an earlier call left (a Connected System of either name) is removed first, so the call can be repeated.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER OmitExportRule
    Leave out the unscoped export rule; the scenario creates it with Add-Scenario033ExportRule.

.PARAMETER Template
    Accepted for runner compatibility. This scenario's data is its own two files.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. This scenario has no directory.

.EXAMPLE
    ./Setup-Scenario-033.ps1 -JIMUrl "http://localhost:5200" -ApiKey "jim_ak_..."
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [switch]$OmitExportRule,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

# Accepted for runner compatibility; this scenario has no directory, no template and no export tuning.
$null = $DirectoryConfig
$null = $ExportConcurrency
$null = $MaxExportParallelism
$null = $Template

. "$PSScriptRoot/utils/Test-Helpers.ps1"
. "$PSScriptRoot/utils/Scenario-033-Data.ps1"

Write-TestSection "Scenario 033 Setup: initial administrator provisioning"
Write-Host "Unscoped export rule: $(if ($OmitExportRule) { 'left out (the scenario creates it)' } else { 'in place' })" -ForegroundColor Gray
Write-Host ""

# Step 1: Connect
Write-TestStep "Step 1" "Connecting to JIM"
if (-not $ApiKey) { throw "API key required for authentication" }
$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  OK Connected" -ForegroundColor Green

# Step 2: Clear what an earlier call left
Write-TestStep "Step 2" "Removing Connected Systems an earlier call left"
foreach ($systemName in @($script:Scenario033HrSystemName, $script:Scenario033DirectorySystemName)) {
    foreach ($existing in @(Get-JIMConnectedSystem | Where-Object { $_.name -eq $systemName })) {
        Remove-JIMConnectedSystem -Id $existing.id -DeleteImmediately -Force | Out-Null
        Write-Host "  Removed $systemName" -ForegroundColor Gray
    }
}
Write-Host "  OK Nothing of an earlier call remains" -ForegroundColor Green

# Step 3: The two files
Write-TestStep "Step 3" "Writing the HR and directory files (a header each, no rows)"
Write-Scenario033Csvs
Write-Host "  OK $($script:Scenario033HrCsvPath) and $($script:Scenario033DirectoryCsvPath)" -ForegroundColor Green

# Step 4: Connector definition
Write-TestStep "Step 4" "Resolving the File Connector"
$fileConnector = Get-JIMConnectorDefinition | Where-Object { $_.name -eq "JIM File Connector" }
if (-not $fileConnector) { throw "JIM File Connector definition not found." }
$settingId = @{}
(Get-JIMConnectorDefinition -Id $fileConnector.id).settings | ForEach-Object { $settingId[$_.name] = $_.id }
foreach ($name in "File Path", "Object Type", "Mode") {
    if (-not $settingId.ContainsKey($name)) { throw "The File Connector has no '$name' setting." }
}
Write-Host "  OK File Connector ($($fileConnector.id))" -ForegroundColor Green

function Select-AllAttributes {
    param([int]$ConnectedSystemId, $ObjectType, [string]$ExternalId)
    $selection = @{}
    foreach ($attribute in $ObjectType.attributes) {
        $selection[$attribute.id] = @{ selected = $true; isExternalId = ($attribute.name -eq $ExternalId) }
    }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $ConnectedSystemId -ObjectTypeId $ObjectType.id -AttributeUpdates $selection | Out-Null
}

# Step 5: HR
Write-TestStep "Step 5" "Creating the HR Connected System (import only)"
$hrSystem = New-JIMConnectedSystem -Name $script:Scenario033HrSystemName `
    -Description "Holds no people: its Delta Synchronisation has nothing to import, so a review of the Metaverse is all it can do" `
    -ConnectorDefinitionId $fileConnector.id -PassThru
Set-JIMConnectedSystem -Id $hrSystem.id -SettingValues @{
    $settingId["File Path"]   = @{ stringValue = $script:Scenario033HrCsvPath }
    $settingId["Object Type"] = @{ stringValue = "employee" }
    $settingId["Mode"]        = @{ stringValue = "Import Only" }
} | Out-Null
Import-JIMConnectedSystemSchema -Id $hrSystem.id | Out-Null
$hrType = Get-JIMConnectedSystem -Id $hrSystem.id -ObjectTypes | Where-Object { $_.name -eq "employee" }
if (-not $hrType) { throw "The HR file's schema has no 'employee' Object Type." }
Set-JIMConnectedSystemObjectType -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrType.id -Selected $true | Out-Null
Select-AllAttributes -ConnectedSystemId $hrSystem.id -ObjectType $hrType -ExternalId "employeeId"
Write-Host "  OK $($script:Scenario033HrSystemName) (ID: $($hrSystem.id))" -ForegroundColor Green

# Step 6: Directory
Write-TestStep "Step 6" "Creating the directory Connected System (export only)"
$directorySystem = New-JIMConnectedSystem -Name $script:Scenario033DirectorySystemName `
    -Description "Receives every User the export rule provisions; a row can only have come from a review or a synchronisation" `
    -ConnectorDefinitionId $fileConnector.id -PassThru
Set-JIMConnectedSystem -Id $directorySystem.id -SettingValues @{
    $settingId["File Path"]   = @{ stringValue = $script:Scenario033DirectoryCsvPath }
    $settingId["Object Type"] = @{ stringValue = "user" }
    $settingId["Mode"]        = @{ stringValue = "Export Only" }
} | Out-Null
Import-JIMConnectedSystemSchema -Id $directorySystem.id | Out-Null
$directoryType = Get-JIMConnectedSystem -Id $directorySystem.id -ObjectTypes | Where-Object { $_.name -eq "user" }
if (-not $directoryType) { throw "The directory file's schema has no 'user' Object Type." }
Set-JIMConnectedSystemObjectType -ConnectedSystemId $directorySystem.id -ObjectTypeId $directoryType.id -Selected $true | Out-Null
Select-AllAttributes -ConnectedSystemId $directorySystem.id -ObjectType $directoryType -ExternalId "subjectIdentifier"
Write-Host "  OK $($script:Scenario033DirectorySystemName) (ID: $($directorySystem.id))" -ForegroundColor Green

# Step 7: The Metaverse and the HR import rule
Write-TestStep "Step 7" "Creating the HR Object Matching Rule and import Synchronisation Rule"
$metaverseUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
if (-not $metaverseUserType) { throw "The Metaverse has no 'User' Object Type." }
$metaverseAttributes = @(Get-JIMMetaverseAttribute)
function Get-MetaverseAttributeId {
    param([string]$Name)
    $attribute = $metaverseAttributes | Where-Object { $_.name -eq $Name }
    if (-not $attribute) { throw "The Metaverse has no '$Name' attribute." }
    return $attribute.id
}
$hrAttribute = @{}
$hrType.attributes | ForEach-Object { $hrAttribute[$_.name] = $_.id }

New-JIMMatchingRule -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrType.id -MetaverseObjectTypeId $metaverseUserType.id `
    -SourceAttributeId $hrAttribute["employeeId"] -TargetMetaverseAttributeId (Get-MetaverseAttributeId "Employee ID") | Out-Null
$importRule = New-JIMSyncRule -Name $script:Scenario033HrRuleName -ConnectedSystemId $hrSystem.id `
    -ConnectedSystemObjectTypeId $hrType.id -MetaverseObjectTypeId $metaverseUserType.id -Direction Import -ProjectToMetaverse -PassThru
$hrFlows = [ordered]@{ employeeId = "Employee ID"; displayName = "Display Name" }
foreach ($column in $hrFlows.Keys) {
    New-JIMSyncRuleMapping -SyncRuleId $importRule.id -SourceConnectedSystemAttributeId $hrAttribute[$column] `
        -TargetMetaverseAttributeId (Get-MetaverseAttributeId $hrFlows[$column]) | Out-Null
}
Write-Host "  OK $($script:Scenario033HrRuleName)" -ForegroundColor Green

# Step 8: The unscoped export rule
Write-TestStep "Step 8" "The unscoped provisioning export rule"
if ($OmitExportRule) {
    Write-Host "  Left out: the scenario creates it once the initial administrator has signed in" -ForegroundColor Gray
}
else {
    $exportRule = Add-Scenario033ExportRule -DirectorySystemId $directorySystem.id
    Write-Host "  OK $($script:Scenario033ExportRuleName) (ID: $($exportRule.id)), no scoping criteria" -ForegroundColor Green
}

# Step 9: Run Profiles
Write-TestStep "Step 9" "Creating Run Profiles"
New-JIMRunProfile -ConnectedSystemId $hrSystem.id -Name "Full Import" -RunType FullImport | Out-Null
New-JIMRunProfile -ConnectedSystemId $hrSystem.id -Name "Full Synchronisation" -RunType FullSynchronisation | Out-Null
New-JIMRunProfile -ConnectedSystemId $hrSystem.id -Name "Delta Synchronisation" -RunType DeltaSynchronisation | Out-Null
New-JIMRunProfile -ConnectedSystemId $directorySystem.id -Name "Export" -RunType Export | Out-Null
Write-Host "  OK HR: Full Import, Full Synchronisation, Delta Synchronisation; directory: Export" -ForegroundColor Green

Write-Host ""
Write-Host "Scenario 033 setup complete." -ForegroundColor Green

return @{
    HrSystemId        = $hrSystem.id
    DirectorySystemId = $directorySystem.id
    ExportRuleCreated = (-not $OmitExportRule)
}
