# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 041: Several Export Rules per Account

.DESCRIPTION
    Validates several export Synchronisation Rules covering one directory account, each writing its own attributes
    (#2826), end to end against a real directory. Scenario 001's export rule provisions every person and writes their
    core attributes (the base rule, Enforce State on). This scenario adds an "Operations Attributes" rule beside it:
    no provisioning, Enforce State off, scoped to the Operations department, writing employeeType.

    Three people, from Scenario 041's own HR file over Scenario 001's configuration:
        Ana Base   (Information Technology) - covered by the base rule only
        Ben Lead   (Operations)             - covered by both rules from the start
        Cat Mover  (Information Technology) - moves into Operations

    Steps (the PRD's scenarios):
        Joiner     (PRD 2) - Ben's one Create carries the base rule's attributes and the add-on's employeeType
        Promotion  (PRD 1) - Cat moves into Operations: the add-on starts covering her account and sends employeeType,
                             which did not change
        BothCover  (PRD 3) - a change to an attribute only the base rule writes stages only that attribute
        Clash      (PRD 5) - both rules write title: neither exports it, the rest goes, Export Rules Overlap is recorded,
                             and saving the second title mapping warns
        Drift      (PRD 9) - hand edits to mail and employeeType: the base rule (Enforce State on) puts mail back, the
                             add-on (Enforce State off) leaves employeeType as edited

    OpenLDAP only: the behaviour is directory-independent, and one directory covers it. Always Nano-sized data.

.PARAMETER Step
    Which test step to execute (Joiner, Promotion, BothCover, Clash, Drift, All). Each step builds on the ones before it.

.PARAMETER Template
    Accepted for the runner's interface and passed to Setup-Scenario-001.ps1; the scenario's own data is three people.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER SkipPopulate
    Accepted for the runner's interface; the scenario always seeds its own data.

.PARAMETER DirectoryConfig
    The runner's directory configuration; must be OpenLDAP.

.EXAMPLE
    ./Invoke-Scenario-041-SeveralExportRules.ps1 -Step All -ApiKey "jim_..."
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Joiner", "Promotion", "BothCover", "Clash", "Drift", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"
. "$PSScriptRoot/../utils/Directory-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Primary
}
if ($DirectoryConfig.DirectoryType -ne "OpenLDAP") {
    throw "Scenario 041 runs on OpenLDAP only (got $($DirectoryConfig.DirectoryType))"
}

$people = @{
    Ana = @{ AccountName = "ana.base";  DisplayName = "Ana Base" }
    Ben = @{ AccountName = "ben.lead";  DisplayName = "Ben Lead" }
    Cat = @{ AccountName = "cat.mover"; DisplayName = "Cat Mover" }
}
$addOnRuleName = "$($DirectoryConfig.ConnectedSystemName) Operations Attributes"

function Invoke-HrCycle {
    param([hashtable]$Config, [string]$Label, [switch]$AllowOverlap)
    $import = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $import.activityId -Name "$Label CSV Import"
    $sync = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVSyncProfileId -Wait -PassThru
    if ($AllowOverlap) {
        Assert-ActivitySuccess -ActivityId $sync.activityId -Name "$Label Full Sync" -AllowWarnings -AllowedWarningTypes @('ExportRuleOverlap')
    }
    else {
        Assert-ActivitySuccess -ActivityId $sync.activityId -Name "$Label Full Sync"
    }
    return $sync.activityId
}

function Invoke-ExportAndConfirm {
    param([hashtable]$Config, [string]$Label)
    $export = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
    Assert-ExportSuccess -ActivityId $export.activityId -Name "$Label LDAP Export"
    Start-Sleep -Seconds 2
    $import = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPDeltaImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $import.activityId -Name "$Label LDAP Delta Import (confirming)"
    $sync = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPDeltaSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $sync.activityId -Name "$Label LDAP Delta Sync"
}

# The Pending Exports staged for one person's account, each with the attribute names it carries.
function Get-StagedExports {
    param([hashtable]$Config, [string]$DisplayName)
    $staged = @(Get-JIMPendingExport -ConnectedSystemId $Config.LDAPSystemId -Search $DisplayName -All -ErrorAction SilentlyContinue)
    return @($staged | ForEach-Object {
        $detail = Get-JIMPendingExport -Id $_.Id
        [PSCustomObject]@{
            ChangeType = "$($detail.ChangeType)"
            Attributes = @($detail.AttributeChanges | ForEach-Object { $_.AttributeName } | Select-Object -Unique)
        }
    })
}

function Assert-OneExport {
    param([object[]]$Staged, [string]$ChangeType, [string[]]$Includes = @(), [string[]]$Excludes = @(), [string]$Label)
    Assert-Equal -Expected 1 -Actual $Staged.Count -Message "$Label stages one Pending Export for the account"
    Assert-Equal -Expected $ChangeType -Actual $Staged[0].ChangeType -Message "$Label stages a $ChangeType"
    foreach ($name in $Includes) {
        Assert-Condition -Condition ($Staged[0].Attributes -contains $name) -Message "$Label carries '$name' (carries: $($Staged[0].Attributes -join ', '))"
    }
    foreach ($name in $Excludes) {
        Assert-Condition -Condition ($Staged[0].Attributes -notcontains $name) -Message "$Label does not carry '$name' (carries: $($Staged[0].Attributes -join ', '))"
    }
}

function Get-DirectoryValue {
    param([string]$AccountName, [string]$AttributeName)
    $user = Get-LDAPUser -UserIdentifier $AccountName -DirectoryConfig $DirectoryConfig
    if (-not $user) { throw "Expected '$AccountName' in $($DirectoryConfig.ConnectedSystemName), but it was not found" }
    if ($user.ContainsKey($AttributeName)) { return "$($user[$AttributeName])" }
    return $null
}

function Set-HrValue {
    param([string]$AccountName, [hashtable]$Values)
    $csvPath = "$(Get-IntegrationTestDataPath)/hr-users.csv"
    $rows = @(Import-Csv $csvPath)
    $row = $rows | Where-Object { $_.samAccountName -eq $AccountName }
    if (-not $row) { throw "No HR row for $AccountName" }
    foreach ($key in $Values.Keys) { $row.$key = $Values[$key] }
    $rows | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    Copy-CsvToConnectorFiles -SourcePath $csvPath
}

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
    $result = Invoke-DirectoryLdif -DirectoryConfig $DirectoryConfig -Ldif ($ldifLines -join "`n") -Operation modify
    if (-not $result.Success) { throw "$Label failed to modify ${UserDn}: $($result.Output)" }
}

$testResults = @{ Scenario = "Several Export Rules per Account"; Steps = @(); Success = $false }

try {
    Write-TestSection "Scenario 041: Several Export Rules per Account"

    if (-not $ApiKey) { throw "API key required for authentication" }

    # --- Setup: Scenario 001's configuration over three people of our own ---
    Write-TestSection "Step 0: Setup"
    $testDataPath = "$(Get-IntegrationTestDataPath)"
    if (-not (Test-Path $testDataPath)) { New-Item -ItemType Directory -Path $testDataPath -Force | Out-Null }
    & "$PSScriptRoot/../Generate-TestCSV.ps1" -Template "Nano" -OutputPath $testDataPath
    Copy-Item -Path "$PSScriptRoot/data/scenario-041-hr-users.csv" -Destination "$testDataPath/hr-users.csv" -Force
    Write-FilesToConnectorVolume -SourceDir $testDataPath -Files @(
        @{ SourceFile = 'hr-users.csv'; DestinationPath = '/connector-files/test-data/hr-users.csv' }
    )

    foreach ($person in $people.Values) {
        Remove-DirectoryEntry -DirectoryConfig $DirectoryConfig -Dn "$($DirectoryConfig.UserRdnAttr)=$($person.AccountName),$($DirectoryConfig.UserContainer)" | Out-Null
    }

    $config = & "$PSScriptRoot/../Setup-Scenario-001.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template -DirectoryConfig $DirectoryConfig
    if (-not $config) { throw "Failed to set up Scenario 001's configuration" }

    Import-Module "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1" -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $ldapUserType = @(Get-JIMConnectedSystem -Id $config.LDAPSystemId -ObjectTypes) | Where-Object { $_.name -eq $DirectoryConfig.UserObjectClass } | Select-Object -First 1
    if (-not $ldapUserType) { throw "No '$($DirectoryConfig.UserObjectClass)' Object Type in $($DirectoryConfig.ConnectedSystemName)" }
    $employeeTypeAttribute = $ldapUserType.attributes | Where-Object { $_.name -eq 'employeeType' }
    $titleAttribute = $ldapUserType.attributes | Where-Object { $_.name -eq 'title' }
    if (-not $employeeTypeAttribute -or -not $titleAttribute) { throw "The directory schema lacks employeeType or title" }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $config.LDAPSystemId -ObjectTypeId $ldapUserType.id `
        -AttributeUpdates @{ $employeeTypeAttribute.id = @{ selected = $true } } | Out-Null

    $mvUserType = Get-JIMMetaverseObjectType -Name "User"
    $mvAttributes = @(Get-JIMMetaverseAttribute)
    $mvEmployeeType = $mvAttributes | Where-Object { $_.name -eq 'Employee Type' }
    $mvJobTitle = $mvAttributes | Where-Object { $_.name -eq 'Job Title' }

    $addOnRule = New-JIMSyncRule -Name $addOnRuleName -ConnectedSystemId $config.LDAPSystemId `
        -ConnectedSystemObjectTypeId $ldapUserType.id -MetaverseObjectTypeId $mvUserType.id -Direction Export -PassThru
    Set-JIMSyncRule -Id $addOnRule.id -EnforceState $false | Out-Null
    $scope = New-JIMScopingCriteriaGroup -SyncRuleId $addOnRule.id -Type All -PassThru
    New-JIMScopingCriterion -SyncRuleId $addOnRule.id -GroupId $scope.id -MetaverseAttributeName 'Department' `
        -ComparisonType Equals -StringValue 'Operations' | Out-Null
    New-JIMSyncRuleMapping -SyncRuleId $addOnRule.id -TargetConnectedSystemAttributeId $employeeTypeAttribute.id `
        -SourceMetaverseAttributeId $mvEmployeeType.id | Out-Null
    Write-Host "  ✓ '$addOnRuleName': no provisioning, Enforce State off, Operations only, writes employeeType" -ForegroundColor Green

    # --- Joiner (PRD Scenario 2) ---
    if ($Step -in "Joiner", "All", "Promotion", "BothCover", "Clash", "Drift") {
        Write-TestSection "Joiner: one Create carries both rules' attributes"
        Invoke-HrCycle -Config $config -Label "Joiner" | Out-Null

        Assert-OneExport -Staged (Get-StagedExports -Config $config -DisplayName $people.Ben.DisplayName) -ChangeType 'Create' `
            -Includes @('uid', 'mail', 'employeeType') -Label "Ben's Create"
        Assert-OneExport -Staged (Get-StagedExports -Config $config -DisplayName $people.Ana.DisplayName) -ChangeType 'Create' `
            -Includes @('uid', 'mail') -Excludes @('employeeType') -Label "Ana's Create"

        Invoke-ExportAndConfirm -Config $config -Label "Joiner"
        Assert-Equal -Expected 'Contractor' -Actual (Get-DirectoryValue -AccountName $people.Ben.AccountName -AttributeName 'employeeType') `
            -Message "Ben's account holds the add-on rule's employeeType"
        Assert-Equal -Expected $null -Actual (Get-DirectoryValue -AccountName $people.Ana.AccountName -AttributeName 'employeeType') `
            -Message "Ana's account has no employeeType: the add-on rule does not cover her"
        $testResults.Steps += @{ Name = "Joiner"; Success = $true }
    }

    # --- Promotion (PRD Scenario 1) ---
    if ($Step -in "Promotion", "All", "BothCover", "Clash", "Drift") {
        Write-TestSection "Promotion: an add-on rule starting to cover an account sends everything it writes"
        Set-HrValue -AccountName $people.Cat.AccountName -Values @{ department = 'Operations' }
        Invoke-HrCycle -Config $config -Label "Promotion" | Out-Null

        Assert-OneExport -Staged (Get-StagedExports -Config $config -DisplayName $people.Cat.DisplayName) -ChangeType 'Update' `
            -Includes @('departmentNumber', 'employeeType') -Label "Cat's Update"

        Invoke-ExportAndConfirm -Config $config -Label "Promotion"
        Assert-Equal -Expected 'Employee' -Actual (Get-DirectoryValue -AccountName $people.Cat.AccountName -AttributeName 'employeeType') `
            -Message "Cat's account holds employeeType, though Employee Type did not change"
        $testResults.Steps += @{ Name = "Promotion"; Success = $true }
    }

    # --- BothCover (PRD Scenario 3) ---
    if ($Step -in "BothCover", "All", "Clash", "Drift") {
        Write-TestSection "Both cover: a change to the base rule's attribute stages only that"
        Set-HrValue -AccountName $people.Ben.AccountName -Values @{ title = 'Operations Director' }
        Invoke-HrCycle -Config $config -Label "BothCover" | Out-Null

        Assert-OneExport -Staged (Get-StagedExports -Config $config -DisplayName $people.Ben.DisplayName) -ChangeType 'Update' `
            -Includes @('title') -Excludes @('employeeType') -Label "Ben's Update"

        Invoke-ExportAndConfirm -Config $config -Label "BothCover"
        Assert-Equal -Expected 'Operations Director' -Actual (Get-DirectoryValue -AccountName $people.Ben.AccountName -AttributeName 'title') `
            -Message "Ben's title was exported"
        $testResults.Steps += @{ Name = "BothCover"; Success = $true }
    }

    # --- Clash (PRD Scenario 5) ---
    if ($Step -in "Clash", "All", "Drift") {
        Write-TestSection "Clash: an attribute both rules write is exported by neither"
        $saveWarnings = @()
        New-JIMSyncRuleMapping -SyncRuleId $addOnRule.id -TargetConnectedSystemAttributeId $titleAttribute.id `
            -SourceMetaverseAttributeId $mvJobTitle.id -WarningVariable saveWarnings -WarningAction SilentlyContinue | Out-Null
        Assert-Condition -Condition (@($saveWarnings | Where-Object { "$_" -match "'title'" }).Count -ge 1) `
            -Message "Saving the second title mapping warns naming the attribute (warnings: $($saveWarnings -join ' | '))"

        Set-HrValue -AccountName $people.Ben.AccountName -Values @{ title = 'Head of Operations'; lastName = 'Leader'; displayName = 'Ben Leader' }
        $syncActivityId = Invoke-HrCycle -Config $config -Label "Clash" -AllowOverlap

        Assert-OneExport -Staged (Get-StagedExports -Config $config -DisplayName 'Ben Leader') -ChangeType 'Update' `
            -Includes @('sn') -Excludes @('title') -Label "Ben's Update with a clash"
        $overlaps = @(Get-JIMActivity -Id $syncActivityId -ExecutionItems | Where-Object { $_.errorType -eq 'ExportRuleOverlap' })
        Assert-Equal -Expected 1 -Actual $overlaps.Count -Message "One Export Rules Overlap item"

        Invoke-ExportAndConfirm -Config $config -Label "Clash"
        Assert-Equal -Expected 'Operations Director' -Actual (Get-DirectoryValue -AccountName $people.Ben.AccountName -AttributeName 'title') `
            -Message "Ben's title keeps the value the directory held"
        Assert-Equal -Expected 'Leader' -Actual (Get-DirectoryValue -AccountName $people.Ben.AccountName -AttributeName 'sn') `
            -Message "Ben's other changes were exported"

        # Give title back to the base rule alone, keeping what it exported.
        $clashMapping = @(Get-JIMSyncRule -Id $addOnRule.id -Mappings) | Where-Object { $_.targetConnectedSystemAttributeId -eq $titleAttribute.id }
        Remove-JIMSyncRuleMapping -SyncRuleId $addOnRule.id -MappingId $clashMapping.id -KeepContributedValues -Force | Out-Null
        $testResults.Steps += @{ Name = "Clash"; Success = $true }
    }

    # --- Drift (PRD Scenario 9) ---
    if ($Step -in "Drift", "All") {
        Write-TestSection "Drift: each rule enforces only what it writes, by its own Enforce State"
        $benDn = (Get-LDAPUser -UserIdentifier $people.Ben.AccountName -DirectoryConfig $DirectoryConfig).dn
        $expectedMail = Get-DirectoryValue -AccountName $people.Ben.AccountName -AttributeName 'mail'
        Set-DirectoryUserAttributes -UserDn $benDn -Label "Drift" -Values @{ mail = 'edited.by.hand@panoply.local'; employeeType = 'Edited by hand' }

        $import = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $import.activityId -Name "Drift LDAP Delta Import"
        $sync = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $sync.activityId -Name "Drift LDAP Delta Sync"

        Assert-OneExport -Staged (Get-StagedExports -Config $config -DisplayName 'Ben Leader') -ChangeType 'Update' `
            -Includes @('mail') -Excludes @('employeeType') -Label "Ben's drift correction"

        Invoke-ExportAndConfirm -Config $config -Label "Drift"
        Assert-Equal -Expected $expectedMail -Actual (Get-DirectoryValue -AccountName $people.Ben.AccountName -AttributeName 'mail') `
            -Message "The base rule put mail back"
        Assert-Equal -Expected 'Edited by hand' -Actual (Get-DirectoryValue -AccountName $people.Ben.AccountName -AttributeName 'employeeType') `
            -Message "The add-on rule, with Enforce State off, left employeeType as edited"
        $testResults.Steps += @{ Name = "Drift"; Success = $true }
    }

    $testResults.Success = $true
    Write-Host ""
    Write-Host "✓ Scenario 041 passed" -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "✗ Scenario 041 failed: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    $testResults.Error = "$_"
    $testResults.Success = $false
}

return $testResults
