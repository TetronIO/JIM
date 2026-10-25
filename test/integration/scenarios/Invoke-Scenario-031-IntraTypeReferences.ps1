# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 031: Intra-type References

.DESCRIPTION
    Validates the Full Synchronisation's cross-page reference step end to end (#2492). Users have managers and groups
    contain groups, references within one Object Type, which no ordering of types can resolve inside the page loop:
    about half of each type is deferred to "Resolving cross-page references", which re-flows their references and
    replaces the Pending Exports the page loop already staged. The scenario then exports everything to an empty
    target and asserts that every manager and every nested membership arrived.

    It is also the measurement scenario for that step: it reports the step's duration and reference-value count, so a
    change to the step can be timed against the same data.

    OpenLDAP only. Self-populating (Populate-OpenLDAP-Scenario-031.ps1); configured by Setup-Scenario-008.ps1 with
    -IntraTypeReferences, which adds the manager mappings to Scenario 008's user and group rules.

.PARAMETER Step
    Which test step to execute (InitialSync, All).

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large).

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER SkipPopulate
    Accepted for the runner's interface; this scenario always populates its own data.

.PARAMETER DirectoryConfig
    The runner's directory configuration; must be OpenLDAP.

.EXAMPLE
    ./Invoke-Scenario-031-IntraTypeReferences.ps1 -Step All -Template Medium -ApiKey "jim_..."
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("InitialSync", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "Large")]
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
. "$PSScriptRoot/../utils/Scenario31-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source
}
if ($DirectoryConfig.DirectoryType -ne "OpenLDAP") {
    throw "Scenario 031 runs on OpenLDAP only (got $($DirectoryConfig.DirectoryType))"
}

$sourceConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source
$targetConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Target
$scale = Get-Scenario31Scale -Template $Template
$targetPeopleOU = "ou=People,dc=glitterband,dc=local"
$targetGroupsOU = "ou=Groups,dc=glitterband,dc=local"

Write-TestSection "Scenario 031: Intra-type References"
Write-Host "Step:     $Step" -ForegroundColor Gray
Write-Host "Template: $Template ($($scale.Users) users, $($scale.Groups) groups)" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Intra-type References"
    Template = $Template
    Steps    = @()
    Success  = $false
}

function Get-Scenario31DirectoryEntries {
    <#
    .SYNOPSIS
        Searches the target suffix and returns each entry as DN => @{ attribute => values }, unfolding LDIF lines.
    #>
    param(
        [Parameter(Mandatory)][string]$BaseDN,
        [Parameter(Mandatory)][string]$Filter,
        [Parameter(Mandatory)][string[]]$Attributes
    )

    $lines = Invoke-LDAPSearch `
        -ContainerName $targetConfig.ContainerName `
        -DirectoryConfig $targetConfig `
        -Server "localhost" `
        -Port $targetConfig.LdapSearchPort `
        -Scheme $targetConfig.LdapSearchScheme `
        -BaseDN $BaseDN `
        -BindDN $targetConfig.BindDN `
        -BindPassword $targetConfig.BindPassword `
        -Filter $Filter `
        -Attributes $Attributes
    if ($null -eq $lines) {
        throw "LDAP search of $BaseDN failed"
    }

    # LDIF folds long lines: a line starting with one space continues the previous one.
    $unfolded = [System.Collections.Generic.List[string]]::new()
    foreach ($line in (($lines -join "`n") -split "`n")) {
        $text = $line.TrimEnd("`r")
        if ($text.StartsWith(" ") -and $unfolded.Count -gt 0) {
            $unfolded[$unfolded.Count - 1] += $text.Substring(1)
        }
        else {
            $unfolded.Add($text)
        }
    }

    $entries = @{}
    $current = $null
    foreach ($text in $unfolded) {
        if ($text -match '^dn:\s*(.+)$') {
            $current = @{}
            $entries[$matches[1].Trim().ToLowerInvariant()] = $current
        }
        elseif ($null -ne $current -and $text -match '^([A-Za-z0-9-]+):\s*(.*)$') {
            $name = $matches[1].ToLowerInvariant()
            if (-not $current.ContainsKey($name)) { $current[$name] = [System.Collections.Generic.List[string]]::new() }
            $current[$name].Add($matches[2].Trim().ToLowerInvariant())
        }
    }
    return $entries
}

try {
    Write-TestSection "Step 0: Setup and Verification"

    if (-not $ApiKey) {
        throw "API key required for authentication"
    }

    $status = docker inspect --format='{{.State.Health.Status}}' $sourceConfig.ContainerName 2>&1
    if ($status -ne "healthy") {
        throw "$($sourceConfig.ContainerName) container is not healthy (status: $status)"
    }

    & "$PSScriptRoot/../Populate-OpenLDAP-Scenario-031.ps1" -Template $Template -Container $sourceConfig.ContainerName

    & "$PSScriptRoot/../Setup-Scenario-008.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template -DirectoryConfig $DirectoryConfig -IntraTypeReferences
    Write-Host "JIM configured for Scenario 031" -ForegroundColor Green

    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $connectedSystems = Get-JIMConnectedSystem
    $sourceSystem = $connectedSystems | Where-Object { $_.name -eq $sourceConfig.ConnectedSystemName }
    $targetSystem = $connectedSystems | Where-Object { $_.name -eq $targetConfig.ConnectedSystemName }
    if (-not $sourceSystem -or -not $targetSystem) {
        throw "Connected Systems not found. Ensure Setup-Scenario-008.ps1 completed successfully."
    }

    $sourceProfiles = Get-JIMRunProfile -ConnectedSystemId $sourceSystem.id
    $targetProfiles = Get-JIMRunProfile -ConnectedSystemId $targetSystem.id
    $sourceFullImport = $sourceProfiles | Where-Object { $_.name -eq "Full Import" }
    $sourceFullSync = $sourceProfiles | Where-Object { $_.name -eq "Full Sync" }
    $targetFullImport = $targetProfiles | Where-Object { $_.name -eq "Full Import" }
    $targetFullSync = $targetProfiles | Where-Object { $_.name -eq "Full Sync" }
    $targetExport = $targetProfiles | Where-Object { $_.name -eq "Export" }

    if ($Step -eq "InitialSync" -or $Step -eq "All") {
        Write-TestSection "Test 1: InitialSync (Source -> Metaverse -> empty Target)"

        $result = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullImport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Source Full Import"
        Assert-NoUnresolvedReferences -ConnectedSystemId $sourceSystem.id -Name "Source" -Context "after Full Import"

        $result = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullImport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Target Full Import"

        $result = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Source Full Sync"

        # The scenario exists to exercise the cross-page step: prove it ran, and report what it cost.
        $syncActivity = Get-JIMActivity -Id $result.activityId
        $crossPageStep = @($syncActivity.phases | Where-Object { $_.key -eq "sync.crosspagereferences" }) | Select-Object -First 1
        if ($null -eq $crossPageStep -or $crossPageStep.status -ne "Completed") {
            $stepStatus = if ($crossPageStep) { $crossPageStep.status } else { "not recorded" }
            throw "The cross-page reference step did not run (status: $stepStatus); Scenario 031 no longer exercises it"
        }
        $stepSeconds = [double]($crossPageStep.durationSeconds ?? 0)
        Write-Host ("  Cross-page reference step: {0:N1} s for {1:N0} reference values" -f $stepSeconds, $syncActivity.objectsToProcess) -ForegroundColor Cyan
        $testResults.CrossPageStepSeconds = $stepSeconds
        $testResults.CrossPageReferenceValues = $syncActivity.objectsToProcess

        $result = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Target Full Sync" -AllowWarnings

        $result = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetExport.id -Wait -PassThru
        Assert-ExportSuccess -ActivityId $result.activityId -Name "Target Export"

        $result = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullImport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Target Confirming Import"
        Assert-NoUnresolvedReferences -ConnectedSystemId $targetSystem.id -Name "Target" -Context "after Confirming Import"

        Write-Host "Validating managers in Target..." -ForegroundColor Gray
        $users = Get-Scenario31DirectoryEntries -BaseDN $targetPeopleOU -Filter "(uid=s31user*)" -Attributes @("uid", "manager")
        $managerFailures = [System.Collections.Generic.List[string]]::new()
        for ($i = 0; $i -lt $scale.Users; $i++) {
            $dn = "uid=$(Get-Scenario31UserUid -Index $i),$targetPeopleOU".ToLowerInvariant()
            if (-not $users.ContainsKey($dn)) {
                $managerFailures.Add("$dn was not provisioned")
                continue
            }
            $managerIndex = Get-Scenario31ManagerIndex -Index $i
            $actual = if ($users[$dn].ContainsKey("manager")) { @($users[$dn]["manager"]) } else { @() }
            $expected = if ($managerIndex -lt 0) { @() } else { @("uid=$(Get-Scenario31UserUid -Index $managerIndex),$targetPeopleOU".ToLowerInvariant()) }
            if (($actual -join ";") -ne ($expected -join ";")) {
                $managerFailures.Add("$dn has manager '$($actual -join ';')', expected '$($expected -join ';')'")
            }
        }
        if ($managerFailures.Count -gt 0) {
            $managerFailures | Select-Object -First 10 | ForEach-Object { Write-Host "  ✗ $_" -ForegroundColor Red }
            throw "$($managerFailures.Count) of $($scale.Users) users in Target have the wrong manager"
        }
        Write-Host "  ✓ All $($scale.Users) users carry the expected manager" -ForegroundColor Green

        Write-Host "Validating nested group membership in Target..." -ForegroundColor Gray
        $groups = Get-Scenario31DirectoryEntries -BaseDN $targetGroupsOU -Filter "(cn=S31-Group-*)" -Attributes @("cn", "member")
        $membershipFailures = [System.Collections.Generic.List[string]]::new()
        for ($g = 0; $g -lt $scale.Groups; $g++) {
            $dn = "cn=$(Get-Scenario31GroupName -Index $g),$targetGroupsOU".ToLowerInvariant()
            if (-not $groups.ContainsKey($dn)) {
                $membershipFailures.Add("$dn was not provisioned")
                continue
            }
            $expected = @(Get-Scenario31GroupUserMemberIndexes -Index $g -UserCount $scale.Users -MembersPerGroup $scale.UsersPerGroup |
                ForEach-Object { "uid=$(Get-Scenario31UserUid -Index $_),$targetPeopleOU".ToLowerInvariant() })
            $expected += @(Get-Scenario31NestedGroupIndexes -Index $g -GroupCount $scale.Groups |
                ForEach-Object { "cn=$(Get-Scenario31GroupName -Index $_),$targetGroupsOU".ToLowerInvariant() })
            $actual = if ($groups[$dn].ContainsKey("member")) { @($groups[$dn]["member"] | Where-Object { $_ -ne "cn=placeholder" }) } else { @() }
            $missing = @($expected | Where-Object { $_ -notin $actual })
            $unexpected = @($actual | Where-Object { $_ -notin $expected })
            if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
                $membershipFailures.Add("$dn is missing [$($missing -join '; ')] and has unexpected [$($unexpected -join '; ')]")
            }
        }
        if ($membershipFailures.Count -gt 0) {
            $membershipFailures | Select-Object -First 10 | ForEach-Object { Write-Host "  ✗ $_" -ForegroundColor Red }
            throw "$($membershipFailures.Count) of $($scale.Groups) groups in Target have the wrong members"
        }
        Write-Host "  ✓ All $($scale.Groups) groups carry the expected users and nested groups" -ForegroundColor Green

        # A second Full Synchronisation must find nothing left to do: every reference was resolved and exported once.
        $result = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Source Full Sync (repeat)"
        $pendingExportCount = [int](Get-JIMPendingExport -ConnectedSystemId $targetSystem.id -Count)
        if ($pendingExportCount -gt 0) {
            throw "A repeat Full Synchronisation staged $pendingExportCount Pending Export(s); expected none"
        }
        Write-Host "  ✓ A repeat Full Synchronisation stages nothing" -ForegroundColor Green

        $testResults.Steps += "InitialSync"
    }

    $testResults.Success = $true
}
catch {
    Write-Host ""
    Write-Host "Scenario 031 failed: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    $testResults.Error = "$_"
}

Write-TestSection "Scenario 031 Results"
Write-Host "Steps passed: $($testResults.Steps -join ', ')" -ForegroundColor Cyan
if ($testResults.ContainsKey('CrossPageStepSeconds')) {
    Write-Host ("Cross-page reference step: {0:N1} s, {1:N0} reference values" -f $testResults.CrossPageStepSeconds, $testResults.CrossPageReferenceValues) -ForegroundColor Cyan
}

if ($testResults.Success) {
    Write-Host "OK All Scenario 031 tests passed!" -ForegroundColor Green
    exit 0
}

Write-Host "FAIL Scenario 031 failed" -ForegroundColor Red
exit 1
