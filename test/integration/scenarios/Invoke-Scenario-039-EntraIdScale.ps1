# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 039: the Microsoft Entra ID Connector at scale, against the JIM test tenant

.DESCRIPTION
    Provisions the run's template of people and groups from an HR CSV to the JIM test tenant in Microsoft Entra ID, and
    times each step (#3053; Phase 5 of engineering/plans/doing/ENTRA_ID_CONNECTOR_INTEGRATION_SCENARIOS.md). It uses
    Scenario 036's configuration without the Initial Password, writing at Graph's per-application rate (20 a second).

    Before anything is written, the run's users and groups must fit in the tenant (its directorySizeQuota) with a tenth
    of the limit left free; otherwise it stops and says why. Purged users still count for a quarter for 30 days.

      1  Provision    Every person and group exported (managers, and an All Staff group holding everyone), confirmed by
                      Full Import, and counted in Graph; a sample read back attribute by attribute.
      2  Changes      A tenth of the people get a new job title and a tenth move between groups, confirmed by Delta Import.
      3  Leavers      A hundredth leave scope and are deleted (Deprovisioning Action Delete), confirmed by Delta Import.
      4  Full Import  A Full Import and Full Synchronisation of the whole tenant find nothing to export.

    Cleanup deletes everything the run made in batches, and the CI app purges its users from deleted items (groups
    cannot be purged by an application, so the template's groups stay there for 30 days). The steps' timings are
    written to results/scenario-039-<template>-<time>.json and printed.

    It runs on demand only, never nightly: at the largest size the tenant takes, a run takes hours. Locally,
    JIM_SSO_ENTRA_GRAPH_TOKEN expires within the hour, so purge what a long local run leaves with a fresh one.

.PARAMETER Template
    The size: Get-TemplateScale's users, groups and average memberships.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario 39 -Template Large -DirectoryType OpenLDAP
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Step, WaitSeconds, SkipPopulate and DirectoryConfig are accepted for the runner, which passes the same parameters to every scenario.')]
param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Domain,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

$null = $DirectoryConfig
$null = $SkipPopulate
$null = $WaitSeconds

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/EntraIdLab-Helpers.ps1"
. "$PSScriptRoot/../utils/EntraIdConnectorLab-Helpers.ps1"
. "$PSScriptRoot/../utils/Scenario-036-Data.ps1"
. "$PSScriptRoot/../utils/Scenario-039-Data.ps1"

if (-not $ApiKey) { throw "API key required for authentication." }
foreach ($name in 'JIM_SSO_ENTRA_TENANT_ID', 'JIM_SSO_ENTRA_CI_CLIENT_ID') {
    if (-not [Environment]::GetEnvironmentVariable($name)) {
        throw "Scenario 039 needs ${name}: the JIM test tenant, and the CI app that owns the Connector's app registration."
    }
}
if (-not $Domain) { $Domain = $script:Scenario036DefaultDomain }
$tenantId = $env:JIM_SSO_ENTRA_TENANT_ID
$ciClientId = $env:JIM_SSO_ENTRA_CI_CLIENT_ID
$prefix = Get-EntraIdConnectorLabRunPrefix
$scale = Get-TemplateScale -Template $Template

$testResults = @{
    Scenario = "Entra ID at scale"
    Steps = @()
    Success = $false
}
$timings = [System.Collections.Generic.List[object]]::new()

function Add-StepResult {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $testResults.Steps += @{ Name = $Name; Passed = $Passed; Detail = $Detail }
}

# --- Tokens: a run outlasts them ---------------------------------------------------------------------------------------

$script:HarnessCertificate = $null
$script:HarnessClientId = $null
$script:HarnessToken = $null
$script:HarnessTokenAt = [DateTime]::MinValue

# The Connector app's token, as the harness: renewed after 40 minutes, inside an access token's hour.
function Get-HarnessToken {
    if (-not $script:HarnessToken -or ([DateTime]::UtcNow - $script:HarnessTokenAt).TotalMinutes -gt 40) {
        $script:HarnessToken = Get-EntraIdConnectorLabToken -TenantId $tenantId -ClientId $script:HarnessClientId -Certificate $script:HarnessCertificate
        $script:HarnessTokenAt = [DateTime]::UtcNow
    }
    return $script:HarnessToken
}

function Get-TenantUsage {
    $ciToken = Get-EntraIdLabGraphToken -TenantId $tenantId -ClientId $ciClientId
    $organization = @((Invoke-EntraIdConnectorLabGraph -Token $ciToken -Path "/organization?`$select=directorySizeQuota").value)[0]
    return $organization.directorySizeQuota
}

# --- Graph, as the harness ---------------------------------------------------------------------------------------------

# How many of the run's users or groups Graph lists (its count is eventually consistent, as a list is).
function Get-RunCount {
    param([Parameter(Mandatory)][ValidateSet('users', 'groups')][string]$Kind)
    $page = Invoke-EntraIdConnectorLabGraph -Token (Get-HarnessToken) -Headers @{ ConsistencyLevel = 'eventual' } `
        -Path "/$Kind`?`$filter=startswith(displayName,'$prefix')&`$count=true&`$top=1&`$select=id"
    return [int](Get-EntraIdConnectorLabValue -Object $page -Name '@odata.count')
}

function Get-MemberCount {
    param([Parameter(Mandatory)][string]$GroupId)
    $page = Invoke-EntraIdConnectorLabGraph -Token (Get-HarnessToken) -Headers @{ ConsistencyLevel = 'eventual' } `
        -Path "/groups/$GroupId/members?`$count=true&`$top=1&`$select=id"
    return [int](Get-EntraIdConnectorLabValue -Object $page -Name '@odata.count')
}

function Get-GraphObject {
    param([Parameter(Mandatory)][string]$Path)
    try { return Invoke-EntraIdConnectorLabGraph -Token (Get-HarnessToken) -Path $Path }
    catch {
        if ("$($_.Exception.Message)" -match '\(404\)') { return $null }
        throw
    }
}

$userSelect = 'id,userPrincipalName,displayName,mailNickname,accountEnabled,givenName,surname,department,jobTitle,employeeId'

function Get-PersonMismatch {
    param([Parameter(Mandatory)][object]$Person)
    $user = Get-GraphObject -Path "/users/$($Person.Upn)?`$select=$userSelect"
    if (-not $user) { return "$($Person.AccountName) is not in Entra ID" }
    $expected = [ordered]@{
        displayName = $Person.DisplayName; mailNickname = $Person.AccountName; givenName = $Person.FirstName
        surname = $Person.LastName; department = $Person.Department; jobTitle = $Person.JobTitle; employeeId = $Person.EmployeeId
    }
    $mismatches = @(foreach ($name in $expected.Keys) {
        if ("$($user.$name)" -ne "$($expected[$name])") { "$($Person.AccountName).$name is '$($user.$name)', expected '$($expected[$name])'" }
    })
    if ($Person.Manager) {
        $manager = Get-GraphObject -Path "/users/$($user.id)/manager?`$select=mailNickname"
        $managerName = if ($manager) { "$($manager.mailNickname)" } else { "" }
        if ($managerName -ne $Person.Manager) { $mismatches += "$($Person.AccountName).manager is '$managerName', expected '$($Person.Manager)'" }
    }
    return $mismatches
}

# Polls until $Check returns nothing, since Graph is eventually consistent; throws with what still differs.
function Wait-EntraState {
    param([Parameter(Mandatory)][string]$Description, [Parameter(Mandatory)][scriptblock]$Check, [int]$TimeoutSeconds = 900)
    $script:LastMismatches = @()
    try {
        $null = Wait-EntraIdConnectorLabCondition -Description $Description -TimeoutSeconds $TimeoutSeconds -IntervalSeconds 20 -Condition {
            $script:LastMismatches = @(& $Check)
            $script:LastMismatches.Count -eq 0
        }
    }
    catch { throw "$($_.Exception.Message) Still different: $(($script:LastMismatches | Select-Object -First 10) -join '; ')" }
}

# --- JIM, timed ---------------------------------------------------------------------------------------------------------

# Runs a Run Profile to completion and records how long it took, and the rate over the given number of objects.
function Invoke-TimedRun {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][object]$System,
        [Parameter(Mandatory)][string]$RunProfile,
        [int]$Objects = 0,
        [switch]$Export,
        [string[]]$AllowedWarningTypes = @()
    )
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $result = Start-JIMRunProfile -ConnectedSystemId $System.id -RunProfileName $RunProfile -Wait -PassThru
    $watch.Stop()
    if ($Export) { Assert-ExportSuccess -ActivityId $result.activityId -Name $Label }
    elseif ($AllowedWarningTypes.Count -gt 0) { Assert-ActivitySuccess -ActivityId $result.activityId -Name $Label -AllowWarnings -AllowedWarningTypes $AllowedWarningTypes }
    else { Assert-ActivitySuccess -ActivityId $result.activityId -Name $Label }
    $seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
    $rate = if ($Objects -gt 0 -and $seconds -gt 0) { [Math]::Round($Objects / $seconds, 1) } else { $null }
    $timings.Add([PSCustomObject]@{ Step = $Label; Seconds = $seconds; Objects = $Objects; PerSecond = $rate })
    Write-Host ("    {0}: {1}s{2}" -f $Label, $seconds, $(if ($rate) { ", $rate objects a second" } else { '' })) -ForegroundColor DarkGray
}

function Invoke-HrImportAndSync {
    param([string]$Label)
    Invoke-TimedRun -Label "HR Full Import ($Label)" -System $hrSystem -RunProfile "Full Import" -Objects ($people.Count + $groups.Count)
    Invoke-TimedRun -Label "HR Full Synchronisation ($Label)" -System $hrSystem -RunProfile "Full Synchronisation" -Objects ($people.Count + $groups.Count)
}

# Exports, then confirms by importing, until no Pending Export is left (Graph's lists lag its writes: an import that
# misses a create has JIM send it again, and the Connector then finds the object rather than making a second).
function Invoke-EntraExportAndConfirm {
    param([string]$Label, [string]$ConfirmWith, [int]$Objects, [scriptblock]$Expected, [int]$MaximumRounds = 4)

    for ($round = 1; $round -le $MaximumRounds; $round++) {
        Invoke-TimedRun -Label "Entra ID Export ($Label, round $round)" -System $entraSystem -RunProfile "Export" -Objects $(if ($round -eq 1) { $Objects } else { 0 }) -Export
        if ($Expected) {
            try { Wait-EntraState -Description "Graph to show the export ($Label, round $round)" -Check $Expected -TimeoutSeconds 600 }
            catch { Write-Host "    $($_.Exception.Message)" -ForegroundColor DarkGray }
        }
        $importLabel = "Entra ID $ConfirmWith ($Label, round $round)"
        if ($round -lt $MaximumRounds) {
            # Both are Graph catching up: an object the import has yet to see (sent again next round), and a group just
            # created whose members Graph cannot yet read (404; the Connector keeps what it held, and the next round
            # reads them). The last round allows neither.
            Invoke-TimedRun -Label $importLabel -System $entraSystem -RunProfile $ConfirmWith -AllowedWarningTypes @('ExportNotConfirmed', 'ImportAttributeValueError')
        }
        else { Invoke-TimedRun -Label $importLabel -System $entraSystem -RunProfile $ConfirmWith }
        $synchronisation = if ($ConfirmWith -eq 'Delta Import') { 'Delta Synchronisation' } else { 'Full Synchronisation' }
        Invoke-TimedRun -Label "Entra ID $synchronisation ($Label, round $round)" -System $entraSystem -RunProfile $synchronisation

        $pending = @(Get-JIMPendingExport -ConnectedSystemId $entraSystem.id -All)
        if ($pending.Count -eq 0) { return }
        $summary = ($pending | Select-Object -First 5 | ForEach-Object { "$($_.changeType) $($_.status)" }) -join "; "
        Write-Host "    $($pending.Count) Pending Export(s) left after round ${round}: $summary" -ForegroundColor DarkGray
    }
    $pending = @(Get-JIMPendingExport -ConnectedSystemId $entraSystem.id -All)
    throw "Entra ID export ($Label) still had $($pending.Count) Pending Export(s) after $MaximumRounds rounds."
}

function Write-HrCsv { Write-Scenario036HrCsv -People $people -Groups $groups }

# --- The run -----------------------------------------------------------------------------------------------------------

Write-TestSection "Scenario 039: Entra ID at scale ($Template)"
Write-Host "Run prefix: $prefix" -ForegroundColor Gray
Write-Host "Size:       $($scale.Users) users, $($scale.Groups) groups, $($scale.AvgMemberships) memberships each on average" -ForegroundColor Gray
Write-Host ""

$applicationObjectId = $null
$harnessKeyId = $null
$snapshotBefore = $null
$runUserIds = [System.Collections.Generic.HashSet[string]]::new()
$cleanupProblems = [System.Collections.Generic.List[string]]::new()
$usageBefore = $null

try {
    Write-TestStep "Tenant" "Checking the run fits, registering the Connector and signing the harness in"

    $usageBefore = Get-TenantUsage
    $fit = Get-Scenario039Fit -Used $usageBefore.used -Total $usageBefore.total -Users $scale.Users -Groups $scale.Groups
    if (-not $fit.Fits) { throw "The $Template template does not fit in the tenant: $($fit.Reason)" }
    Write-Host "  OK $($fit.Reason)" -ForegroundColor Green

    Remove-Module JIM -Force -ErrorAction SilentlyContinue
    Import-Module "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1" -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $ciToken = Get-EntraIdLabGraphToken -TenantId $tenantId -ClientId $ciClientId
    $registration = Register-JIMEntraIdConnector -AccessToken $ciToken -Name $script:Scenario036ApplicationName `
        -ConnectedSystemName $script:Scenario039EntraSystemName -ResetCertificate -Confirm:$false
    if (-not $registration -or -not $registration.ConnectedSystemId) { throw "Register-JIMEntraIdConnector created no Connected System." }
    $application = @((Invoke-EntraIdConnectorLabGraph -Token $ciToken -Path "/applications?`$filter=appId eq '$($registration.ClientId)'&`$select=id").value) | Select-Object -First 1
    if (-not $application) { throw "The CI app cannot see the application $($registration.ClientId); is it still an owner?" }
    $applicationObjectId = $application.id

    $script:HarnessCertificate = New-EntraIdConnectorLabCertificate
    $script:HarnessClientId = $registration.ClientId
    $harnessKeyId = Add-EntraIdConnectorLabCredential -AccessToken $ciToken -ApplicationObjectId $applicationObjectId -Certificate $script:HarnessCertificate
    $null = Get-HarnessToken
    Write-Host "  OK '$($script:Scenario039EntraSystemName)' registered; the harness signed in with its own certificate" -ForegroundColor Green

    $leftovers = Remove-EntraIdConnectorLabObject -OlderThanHours 24 -Token (Get-HarnessToken)
    if ($leftovers -gt 0) { Write-Host "  Deleted $leftovers object(s) earlier runs left behind" -ForegroundColor Yellow }
    $snapshotBefore = Get-EntraIdConnectorLabSnapshot -Token (Get-HarnessToken)

    & "$PSScriptRoot/../Setup-Scenario-036.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -ConnectedSystemId $registration.ConnectedSystemId `
        -Prefix $prefix -Domain $Domain -InitialPassword $false

    $systems = @(Get-JIMConnectedSystem)
    $hrSystem = $systems | Where-Object { $_.name -eq $script:Scenario036HrSystemName }
    $entraSystem = $systems | Where-Object { $_.id -eq $registration.ConnectedSystemId }
    if (-not $hrSystem -or -not $entraSystem) { throw "The scenario's Connected Systems were not found after setup." }
    $graphConnector = Get-JIMConnectorDefinition -Id (@(Get-JIMConnectorDefinition) | Where-Object { $_.name -eq 'JIM Microsoft Entra ID Connector' }).id
    $writesSetting = $graphConnector.settings | Where-Object { $_.name -eq 'Maximum Writes Per Second' }
    # Saving tests the connection, which signs in, and Entra ID can still be applying the certificate made minutes ago.
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while ($true) {
        try { Set-JIMConnectedSystem -Id $entraSystem.id -SettingValues @{ $writesSetting.id = @{ intValue = $script:Scenario039WritesPerSecond } } | Out-Null; break }
        catch {
            if ("$($_.Exception.Message)" -notmatch 'AADSTS700027' -or [DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Seconds 15
        }
    }
    New-JIMRunProfile -ConnectedSystemId $entraSystem.id -Name "Delta Import" -RunType DeltaImport | Out-Null
    New-JIMRunProfile -ConnectedSystemId $entraSystem.id -Name "Delta Synchronisation" -RunType DeltaSynchronisation | Out-Null
    $exportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq $script:Scenario036UserExportRuleName }
    Set-JIMSyncRule -Id $exportRule.id -OutboundDeprovisionAction Delete | Out-Null
    Write-Host "  OK Writing at $($script:Scenario039WritesPerSecond) a second; leavers are deleted" -ForegroundColor Green

    $population = New-Scenario039Population -Prefix $prefix -Domain $Domain -Users $scale.Users -Groups $scale.Groups -AverageMemberships $scale.AvgMemberships
    $people = $population.People
    $groups = $population.Groups
    $memberships = @($groups | ForEach-Object { $_.Members.Count } | Measure-Object -Sum).Sum
    $sample = @(for ($i = 0; $i -lt $people.Count; $i += [Math]::Max(1, [Math]::Floor($people.Count / 20))) { $people[$i] })

    # --- 1: Provision ---
    Write-TestStep "Provision" "Provisioning $($people.Count) people and $($groups.Count) groups ($memberships memberships)"

    Write-HrCsv
    Invoke-HrImportAndSync -Label "baseline"
    $provisioned = {
        $users = Get-RunCount -Kind users
        if ($users -ne $people.Count) { "Graph lists $users of the run's $($people.Count) users" }
        $listedGroups = Get-RunCount -Kind groups
        if ($listedGroups -ne $groups.Count) { "Graph lists $listedGroups of the run's $($groups.Count) groups" }
    }
    Invoke-EntraExportAndConfirm -Label "provision" -ConfirmWith "Full Import" -Objects ($people.Count + $groups.Count) -Expected $provisioned
    Wait-EntraState -Description "the sample of people" -Check { foreach ($person in $sample) { Get-PersonMismatch -Person $person } }
    $allStaff = Get-GraphObject -Path "/groups?`$filter=mailNickname eq '$($groups[0].AccountName)'&`$select=id"
    $allStaffId = @($allStaff.value)[0].id
    Wait-EntraState -Description "All Staff's members" -Check {
        $count = Get-MemberCount -GroupId $allStaffId
        if ($count -ne $people.Count) { "All Staff has $count of $($people.Count) members" }
    }
    foreach ($user in @(Get-EntraIdConnectorLabGraphCollection -Token (Get-HarnessToken) -Path "/users?`$filter=startswith(displayName,'$prefix')&`$select=id&`$top=999")) {
        $null = $runUserIds.Add([string]$user.id)
    }

    Add-StepResult -Name "Provision" -Passed $true -Detail "$($people.Count) users and $($groups.Count) groups in Graph; $($sample.Count) people read back; All Staff holds everyone"
    Write-Host "  OK Everyone provisioned, counted in Graph, and a sample read back" -ForegroundColor Green

    # --- 2: Changes ---
    $retitled = @(for ($i = 7; $i -lt $people.Count; $i += 10) { $people[$i] })
    $moved = @(for ($i = 3; $i -lt $people.Count; $i += 10) { $people[$i] })
    Write-TestStep "Changes" "$($retitled.Count) new job titles and $($moved.Count) moves between groups, confirmed by Delta Import"

    foreach ($person in $retitled) { $person.JobTitle = "Senior Analyst" }
    $movedTo = @{}
    if ($groups.Count -gt 2) {
        foreach ($person in $moved) {
            $from = $groups | Select-Object -Skip 1 | Where-Object { $_.Members.Contains($person.AccountName) } | Select-Object -First 1
            $to = $groups | Select-Object -Skip 1 | Where-Object { -not $_.Members.Contains($person.AccountName) } | Select-Object -First 1
            if ($from -and $to) {
                $null = $from.Members.Remove($person.AccountName)
                $to.Members.Add($person.AccountName)
                $movedTo[$person.AccountName] = $to
            }
        }
    }
    Write-HrCsv
    Invoke-HrImportAndSync -Label "changes"
    $changedSample = @($retitled | Select-Object -First 20)
    Invoke-EntraExportAndConfirm -Label "changes" -ConfirmWith "Delta Import" -Objects ($retitled.Count + 2 * $movedTo.Count) `
        -Expected { foreach ($person in $changedSample) { Get-PersonMismatch -Person $person } }
    Wait-EntraState -Description "the new job titles" -Check { foreach ($person in $changedSample) { Get-PersonMismatch -Person $person } }

    Add-StepResult -Name "Changes" -Passed $true -Detail "$($retitled.Count) job titles and $($movedTo.Count) moves exported, confirmed by Delta Import"
    Write-Host "  OK Changes exported and confirmed" -ForegroundColor Green

    # --- 3: Leavers ---
    $leavers = @(for ($i = 5; $i -lt $people.Count; $i += 100) { $people[$i] })
    Write-TestStep "Leavers" "$($leavers.Count) people leave scope and are deleted"

    foreach ($person in $leavers) {
        $person.Department = $script:Scenario036LeaverDepartment
        foreach ($group in $groups) { $null = $group.Members.Remove($person.AccountName) }
    }
    Write-HrCsv
    Invoke-HrImportAndSync -Label "leavers"
    $leaverSample = @($leavers | Select-Object -First 20)
    $gone = { foreach ($person in $leaverSample) { if (Get-GraphObject -Path "/users/$($person.Upn)?`$select=id") { "$($person.AccountName) is still in Entra ID" } } }
    Invoke-EntraExportAndConfirm -Label "leavers" -ConfirmWith "Delta Import" -Objects $leavers.Count -Expected $gone
    Wait-EntraState -Description "the leavers deleted" -Check $gone

    Add-StepResult -Name "Leavers" -Passed $true -Detail "$($leavers.Count) deleted"
    Write-Host "  OK Leavers deleted" -ForegroundColor Green

    # --- 4: Full Import ---
    Write-TestStep "FullImport" "A Full Import and Full Synchronisation of the whole tenant find nothing to export"

    Invoke-TimedRun -Label "Entra ID Full Import (everything)" -System $entraSystem -RunProfile "Full Import" -Objects ($people.Count + $groups.Count)
    Invoke-TimedRun -Label "Entra ID Full Synchronisation (everything)" -System $entraSystem -RunProfile "Full Synchronisation" -Objects ($people.Count + $groups.Count)
    $pending = @(Get-JIMPendingExport -ConnectedSystemId $entraSystem.id -All)
    Assert-Equal -Actual $pending.Count -Expected 0 -Message "Pending Exports after a Full Import with nothing changed"

    Add-StepResult -Name "FullImport" -Passed $true -Detail "No Pending Exports"
    Write-Host "  OK Nothing to export" -ForegroundColor Green

    $testResults.Success = $true
}
catch {
    $testResults.Success = $false
    Write-Failure "Scenario 039 failed: $_"
}
finally {
    Write-TestStep "Cleanup" "Deleting what the run made, purging its users, comparing the snapshot"

    if ($script:HarnessToken) {
        try {
            foreach ($user in @(Get-EntraIdConnectorLabGraphCollection -Token (Get-HarnessToken) -Path "/users?`$filter=startswith(displayName,'$prefix')&`$select=id&`$top=999")) {
                $null = $runUserIds.Add([string]$user.id)
            }
            $watch = [System.Diagnostics.Stopwatch]::StartNew()
            $removed = Remove-EntraIdConnectorLabObject -Prefix $prefix -Token (Get-HarnessToken)
            $timings.Add([PSCustomObject]@{ Step = "Harness delete"; Seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1); Objects = $removed; PerSecond = $null })
            Write-Host "  Deleted $removed object(s) the run made" -ForegroundColor Gray
        }
        catch { $cleanupProblems.Add("Deleting the run's objects failed: $($_.Exception.Message)") }

        if ($runUserIds.Count -gt 0) {
            try {
                $watch = [System.Diagnostics.Stopwatch]::StartNew()
                $purged = Remove-EntraIdConnectorLabDeletedUser -Token (Get-EntraIdLabGraphToken -TenantId $tenantId -ClientId $ciClientId) -Id @($runUserIds)
                $timings.Add([PSCustomObject]@{ Step = "Purge deleted users"; Seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1); Objects = $purged; PerSecond = $null })
                Write-Host "  Purged $purged user(s) from deleted items" -ForegroundColor Gray
            }
            catch { $cleanupProblems.Add("Purging the run's users failed: $($_.Exception.Message)") }
        }

        if ($snapshotBefore) {
            try {
                $differences = Compare-EntraIdConnectorLabSnapshot -Before $snapshotBefore -After (Get-EntraIdConnectorLabSnapshot -Token (Get-HarnessToken))
                if ($differences.Count -gt 0) {
                    $cleanupProblems.Add("The run changed $($differences.Count) object(s) it did not make: $(($differences | Select-Object -First 5) -join ' | ')")
                }
                else { Add-StepResult -Name "Snapshot" -Passed $true -Detail "$($snapshotBefore.Count) users and groups the run did not make are unchanged" }
            }
            catch { $cleanupProblems.Add("Comparing the snapshot failed: $($_.Exception.Message)") }
        }
    }

    if ($applicationObjectId -and $harnessKeyId) {
        try {
            Remove-EntraIdConnectorLabCredential -AccessToken (Get-EntraIdLabGraphToken -TenantId $tenantId -ClientId $ciClientId) -ApplicationObjectId $applicationObjectId -KeyId $harnessKeyId
        }
        catch { Write-Host "  Could not remove the harness's certificate (the next run will): $($_.Exception.Message)" -ForegroundColor Yellow }
    }

    try {
        $usageAfter = Get-TenantUsage
        Write-Host "  Tenant objects: $(if ($usageBefore) { $usageBefore.used } else { '?' }) before, $($usageAfter.used) after, of $($usageAfter.total)" -ForegroundColor Gray
    }
    catch { $usageAfter = $null }

    $resultsFile = Join-Path $script:Scenario039ResultsFolder "scenario-039-$Template-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')).json"
    [PSCustomObject]@{
        Template = $Template; Users = $scale.Users; Groups = $scale.Groups; WritesPerSecond = $script:Scenario039WritesPerSecond
        Succeeded = $testResults.Success -and $cleanupProblems.Count -eq 0
        TenantObjectsBefore = if ($usageBefore) { $usageBefore.used } else { $null }
        TenantObjectsAfter = if ($usageAfter) { $usageAfter.used } else { $null }
        Steps = $timings
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $resultsFile
    Write-Host ""
    $timings | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host "  Timings written to $resultsFile" -ForegroundColor Gray

    if ($cleanupProblems.Count -gt 0) {
        $testResults.Success = $false
        foreach ($problem in $cleanupProblems) { Write-Failure $problem }
    }
    if (Get-Module JIM) { Disconnect-JIM -ErrorAction SilentlyContinue }
}

Write-TestSection "Scenario 039 $(if ($testResults.Success) { 'Complete' } else { 'Failed' })"
foreach ($stepResult in $testResults.Steps) {
    Write-Host "  [PASS] $($stepResult.Name): $($stepResult.Detail)" -ForegroundColor Green
}

return $testResults
