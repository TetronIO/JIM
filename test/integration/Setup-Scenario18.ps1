# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Configure JIM for Scenario 18: writeback into the Connected System being synchronised (#1284)

.DESCRIPTION
    Proves whether an outbound Synchronisation Rule whose target is the SAME Connected System the
    import ran against is evaluated at all. This is the "attribute writeback" shape: a system is
    authoritative for identity, JIM derives a value from it, and that value is written back into the
    same system. Nothing about it is database-specific; this scenario deliberately uses the JIM File
    Connector so the question is answered away from the JIM SQL Connector, where it was first noticed.

    Topology (two File Connected Systems, so the two cases differ ONLY in whose run is executing):

      - HR: the source of identity, seeded with three people. Carries BOTH
          * an inbound Synchronisation Rule projecting people into the Metaverse, and
          * an outbound Synchronisation Rule targeting ITSELF, writing the Metaverse's Account Name
            into a column the seed file leaves empty. This is the rule under test.

      - Control: a header-only file carrying an outbound Synchronisation Rule of the same shape,
        scoped identically, fed by the same Metaverse Objects. This is the control: if it stages
        Pending Exports while HR's rule does not, the rule shape and the scope are demonstrably fine
        and the only difference left is which system the synchronisation run is reading from.

    Both outbound rules are scoped on Job Title so neither depends on an unscoped rule's behaviour,
    which is a separate question and not the one being asked here.

    The scenario that drives this setup then runs the two synchronisations in turn, which is what
    separates "the rule never works" from "the rule works except during its own system's run".

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Accepted for runner compatibility. This scenario seeds its own three users and ignores it.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. This scenario has no directory target and ignores it.
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

$null = $Template
$null = $DirectoryConfig

. "$PSScriptRoot/utils/Test-Helpers.ps1"

$hrSystemName      = "Scenario 18 HR"
$controlSystemName = "Scenario 18 Control"
$hrCsvFilePath      = "/connector-files/test-data/scenario18-hr.csv"
$controlCsvFilePath = "/connector-files/test-data/scenario18-control.csv"

# The scoped population. Every seeded person carries this Job Title, so both outbound rules select
# all three; a rule that stages nothing has not simply scoped itself out.
$scopedJobTitle = "Engineer"

Write-TestSection "Scenario 18 Setup: writeback into the source Connected System"

# ─── Step 1: JIM module and session ────────────────────────────────────────────

Write-TestStep "Step 1" "Importing the JIM PowerShell module"
$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop

Write-TestStep "Step 2" "Connecting to JIM"
if (-not $ApiKey) { throw "API key required for authentication" }
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

# ─── Step 3: Clean up any prior run ────────────────────────────────────────────

Write-TestStep "Step 3" "Removing any Connected System left by a previous run"
# Control first: it holds Connected System Objects provisioned from HR's Metaverse Objects, and
# removing the source of identity first would deprovision them on the way past.
foreach ($staleName in @($controlSystemName, $hrSystemName)) {
    foreach ($stale in @(Get-JIMConnectedSystem -Name $staleName -ErrorAction SilentlyContinue | Where-Object { $_ })) {
        Remove-JIMConnectedSystem -Id $stale.id -DeleteImmediately -Force | Out-Null
        Write-Host "  Removed '$($stale.name)'" -ForegroundColor Gray
    }
}

# ─── Step 4: Connector definition ──────────────────────────────────────────────

Write-TestStep "Step 4" "Resolving the JIM File Connector definition"
$connectorSummary = Get-JIMConnectorDefinition | Where-Object { $_.name -eq "JIM File Connector" }
if (-not $connectorSummary) {
    throw "The 'JIM File Connector' Connector Definition was not found."
}
$connector = Get-JIMConnectorDefinition -Id $connectorSummary.id

function Get-SettingId {
    param([string]$Name)
    $setting = $connector.settings | Where-Object { $_.name -eq $Name }
    if (-not $setting) {
        throw "JIM File Connector setting '$Name' not found; the connector definition may be stale."
    }
    return $setting.id
}

$filePathSettingId   = Get-SettingId "File Path"
$delimiterSettingId  = Get-SettingId "Delimiter"
$objectTypeSettingId = Get-SettingId "Object Type"

# ─── Step 5: The two Connected Systems ─────────────────────────────────────────

function New-Scenario18System {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][string]$Description,
        [Parameter(Mandatory=$true)][string]$FilePath
    )

    $created = New-JIMConnectedSystem -Name $Name -Description $Description `
        -ConnectorDefinitionId $connector.id -PassThru

    $settings = @{
        $filePathSettingId   = @{ stringValue = $FilePath }
        $delimiterSettingId  = @{ stringValue = "," }
        $objectTypeSettingId = @{ stringValue = "person" }
    }
    Set-JIMConnectedSystem -Id $created.id -SettingValues $settings -ErrorAction Stop | Out-Null
    Import-JIMConnectedSystemSchema -Id $created.id -ErrorAction Stop | Out-Null

    $objectType = Get-JIMConnectedSystem -Id $created.id -ObjectTypes |
        Where-Object { $_.name -eq "person" } | Select-Object -First 1
    if (-not $objectType) {
        throw "Schema discovery on '$Name' did not return the 'person' Object Type; check the seeded CSV."
    }

    Set-JIMConnectedSystemObjectType -ConnectedSystemId $created.id -ObjectTypeId $objectType.id -Selected $true | Out-Null
    $attributeUpdates = @{}
    foreach ($attribute in $objectType.attributes) {
        $attributeUpdates[$attribute.id] = @{
            selected     = $true
            isExternalId = ($attribute.name -eq "employeeId")
        }
    }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $created.id -ObjectTypeId $objectType.id -AttributeUpdates $attributeUpdates | Out-Null

    Write-Host "  OK $Name (ID $($created.id), $($objectType.attributes.Count) attributes, anchor employeeId)" -ForegroundColor Green
    return @{ System = $created; ObjectType = $objectType }
}

Write-TestStep "Step 5" "Creating the HR and Control Connected Systems"
$hr      = New-Scenario18System -Name $hrSystemName      -Description "Scenario 18: source of identity, and the target of its own writeback rule" -FilePath $hrCsvFilePath
$control = New-Scenario18System -Name $controlSystemName -Description "Scenario 18: control target, fed by the same Metaverse Objects"            -FilePath $controlCsvFilePath

$hrSystem      = $hr.System
$hrType        = $hr.ObjectType
$controlSystem = $control.System
$controlType   = $control.ObjectType

function Get-CsAttributeId {
    param($ObjectType, [string]$Name)
    $attribute = $ObjectType.attributes | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $attribute) {
        throw "Column '$Name' was not discovered on Object Type '$($ObjectType.name)'. Discovered: $(($ObjectType.attributes | ForEach-Object { $_.name }) -join ', ')"
    }
    return $attribute.id
}

# ─── Step 6: Metaverse plumbing ────────────────────────────────────────────────

Write-TestStep "Step 6" "Resolving the Metaverse schema"
$mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
if (-not $mvUserType) { throw "Metaverse Object Type 'User' not found." }

function Get-MvAttributeId {
    param([string]$Name)
    $attribute = Get-JIMMetaverseAttribute | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $attribute) { throw "Metaverse Attribute '$Name' not found." }
    return $attribute.id
}

$mvEmployeeIdId  = Get-MvAttributeId "Employee ID"
$mvAccountNameId = Get-MvAttributeId "Account Name"
$mvJobTitleId    = Get-MvAttributeId "Job Title"

Write-TestStep "Step 7" "Creating the Object Matching Rule"
New-JIMMatchingRule -ConnectedSystemId $hrSystem.id -ObjectTypeId $hrType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId (Get-CsAttributeId $hrType "employeeId") `
    -TargetMetaverseAttributeId $mvEmployeeIdId | Out-Null
Write-Host "  OK HR employeeId matches on Metaverse 'Employee ID'" -ForegroundColor Green

# The Control system joins on the same key, so the rows it receives are recognised as the same
# people when it is imported back. Without this the Control import would project duplicates and the
# second synchronisation would be measuring the wrong objects.
New-JIMMatchingRule -ConnectedSystemId $controlSystem.id -ObjectTypeId $controlType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -SourceAttributeId (Get-CsAttributeId $controlType "employeeId") `
    -TargetMetaverseAttributeId $mvEmployeeIdId | Out-Null
Write-Host "  OK Control employeeId matches on Metaverse 'Employee ID'" -ForegroundColor Green

# ─── Step 8: Inbound Synchronisation Rule ──────────────────────────────────────

Write-TestStep "Step 8" "Creating the inbound Synchronisation Rule (HR to Metaverse)"
$importRule = New-JIMSyncRule `
    -Name "Scenario 18 HR Import" `
    -Description "Projects the seeded people into the Metaverse so both outbound rules have something to act on." `
    -ConnectedSystemId $hrSystem.id `
    -ConnectedSystemObjectTypeId $hrType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Import `
    -ProjectToMetaverse `
    -PassThru

foreach ($mapping in @(
    @{ Cs = "employeeId";  Mv = $mvEmployeeIdId  }
    @{ Cs = "accountName"; Mv = $mvAccountNameId }
    @{ Cs = "jobTitle";    Mv = $mvJobTitleId    }
)) {
    New-JIMSyncRuleMapping -SyncRuleId $importRule.id `
        -SourceConnectedSystemAttributeId (Get-CsAttributeId $hrType $mapping.Cs) `
        -TargetMetaverseAttributeId $mapping.Mv | Out-Null
}
Write-Host "  OK Inbound rule created (3 Attribute Flows)" -ForegroundColor Green

# ─── Step 9: The outbound rule under test, targeting HR itself ─────────────────

Write-TestStep "Step 9" "Creating the writeback Synchronisation Rule (Metaverse back into HR)"

# The 'writeback' column is present in the seeded file but empty for every row, so the Metaverse's
# Account Name is genuinely a change rather than a value that already agrees.
$writebackRule = New-JIMSyncRule `
    -Name "Scenario 18 HR Writeback" `
    -Description "Writes the Metaverse Account Name back into the HR system's own writeback column. This is the rule #1284 is about." `
    -ConnectedSystemId $hrSystem.id `
    -ConnectedSystemObjectTypeId $hrType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Export `
    -ProvisionToConnectedSystem `
    -OutboundDeprovisionAction Disconnect `
    -PassThru

New-JIMSyncRuleMapping -SyncRuleId $writebackRule.id `
    -TargetConnectedSystemAttributeId (Get-CsAttributeId $hrType "writeback") `
    -SourceMetaverseAttributeId $mvAccountNameId | Out-Null

$writebackScope = New-JIMScopingCriteriaGroup -SyncRuleId $writebackRule.id -Type All -PassThru
New-JIMScopingCriterion -SyncRuleId $writebackRule.id -GroupId $writebackScope.id `
    -MetaverseAttributeId $mvJobTitleId -ComparisonType Equals -StringValue $scopedJobTitle | Out-Null
Write-Host "  OK Writeback rule created, scoped to Job Title = $scopedJobTitle" -ForegroundColor Green

# ─── Step 10: The control outbound rule ────────────────────────────────────────

Write-TestStep "Step 10" "Creating the control Synchronisation Rule (Metaverse to the Control system)"
$controlRule = New-JIMSyncRule `
    -Name "Scenario 18 Control Export" `
    -Description "Identical shape and scope to the writeback rule, but targeting a different Connected System." `
    -ConnectedSystemId $controlSystem.id `
    -ConnectedSystemObjectTypeId $controlType.id `
    -MetaverseObjectTypeId $mvUserType.id `
    -Direction Export `
    -ProvisionToConnectedSystem `
    -OutboundDeprovisionAction Disconnect `
    -PassThru

New-JIMSyncRuleMapping -SyncRuleId $controlRule.id `
    -TargetConnectedSystemAttributeId (Get-CsAttributeId $controlType "employeeId") `
    -SourceMetaverseAttributeId $mvEmployeeIdId | Out-Null
New-JIMSyncRuleMapping -SyncRuleId $controlRule.id `
    -TargetConnectedSystemAttributeId (Get-CsAttributeId $controlType "writeback") `
    -SourceMetaverseAttributeId $mvAccountNameId | Out-Null

$controlScope = New-JIMScopingCriteriaGroup -SyncRuleId $controlRule.id -Type All -PassThru
New-JIMScopingCriterion -SyncRuleId $controlRule.id -GroupId $controlScope.id `
    -MetaverseAttributeId $mvJobTitleId -ComparisonType Equals -StringValue $scopedJobTitle | Out-Null
Write-Host "  OK Control rule created, scoped to Job Title = $scopedJobTitle" -ForegroundColor Green

# ─── Step 11: Run Profiles ─────────────────────────────────────────────────────

Write-TestStep "Step 11" "Creating Run Profiles"

# The File connector takes its path from the Run Profile, not only from the Connected System's
# settings; a Full Import created without -FilePath imports nothing at all and reports success,
# which is a quiet way to spend an afternoon.
New-JIMRunProfile -Name "Full Import"          -ConnectedSystemId $hrSystem.id      -RunType "FullImport"          -FilePath $hrCsvFilePath      | Out-Null
New-JIMRunProfile -Name "Full Synchronisation" -ConnectedSystemId $hrSystem.id      -RunType "FullSynchronisation"                               | Out-Null
New-JIMRunProfile -Name "Export"               -ConnectedSystemId $hrSystem.id      -RunType "Export"                                            | Out-Null
New-JIMRunProfile -Name "Full Import"          -ConnectedSystemId $controlSystem.id -RunType "FullImport"          -FilePath $controlCsvFilePath | Out-Null
New-JIMRunProfile -Name "Full Synchronisation" -ConnectedSystemId $controlSystem.id -RunType "FullSynchronisation"                               | Out-Null
New-JIMRunProfile -Name "Export"               -ConnectedSystemId $controlSystem.id -RunType "Export"                                            | Out-Null
Write-Host "  OK Six Run Profiles created (Full Import, Full Synchronisation, Export on each system)" -ForegroundColor Green

Write-TestSection "Scenario 18 Setup Complete"
Write-Host "  HR system:      $hrSystemName (ID: $($hrSystem.id))" -ForegroundColor Cyan
Write-Host "  Control system: $controlSystemName (ID: $($controlSystem.id))" -ForegroundColor Cyan
Write-Host "  Scoped Job Title: $scopedJobTitle" -ForegroundColor Cyan

return @{
    HrConnectedSystemId      = $hrSystem.id
    ControlConnectedSystemId = $controlSystem.id
    HrSystemName             = $hrSystemName
    ControlSystemName        = $controlSystemName
    ImportRuleId             = $importRule.id
    WritebackRuleId          = $writebackRule.id
    ControlRuleId            = $controlRule.id
    ScopedJobTitle           = $scopedJobTitle
    HrCsvFilePath            = $hrCsvFilePath
    ControlCsvFilePath       = $controlCsvFilePath
}
