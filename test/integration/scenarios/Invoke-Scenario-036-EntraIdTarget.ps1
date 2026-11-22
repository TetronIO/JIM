# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 036: Entra ID as a target (the Microsoft Entra ID Connector provisions to a real tenant)

.DESCRIPTION
    JIM provisions, updates, disables and deletes users and groups in the JIM test tenant in Microsoft Entra ID through
    the Microsoft Entra ID Connector (#361), from an HR CSV, and the harness checks each result through its own Microsoft
    Graph requests, never through JIM, so a mistake mirrored in JIM's import and export cannot cancel itself out. The
    plan is engineering/plans/doing/ENTRA_ID_CONNECTOR_INTEGRATION_SCENARIOS.md.

    Before the steps, as the CI app (Get-EntraIdLabGraphToken), the scenario:
      - runs Register-JIMEntraIdConnector -ResetCertificate against the permanent "JIM Entra ID Connector (CI)" app,
        which gives it a new certificate, hands the private key to JIM in a new Connected System, and removes the
        previous certificate. No credential for the app is stored anywhere;
      - adds the harness's own two-day certificate to the same app and signs in with it;
      - deletes anything an earlier, failed run left more than a day ago, and snapshots every account it did not make.

      1  Provision         24 people and four groups (one with more than the 20 members Graph takes on a create, one
                           whose members are groups) are created, with every exported attribute, manager and member
                           read back from Graph.
      2  Initial Password  Every user's Initial Password, generated from the policy JIM discovered, is delivered through
                           the password channel; none is parked or expires.
      3  Update            A job title, a user principal name and a manager change.
      4  Membership        A member added to and one removed from a department group.
      5  Leaver (disable)  With Delete Behaviour Disable, a person leaving scope is disabled and kept.
      6  Leaver (delete)   With Delete Behaviour Delete, a person leaving scope is deleted, and is in deleted items.
      7  Idempotence       Synchronising again with nothing changed queues no Pending Exports.
      8  Untouchable       The tenant's own accounts are imported, stay disconnected, and are unchanged afterwards.

    Everything the run made carries its prefix and is deleted at the end, whatever happened; the snapshot is then
    compared, and a difference fails the run. The harness's certificate is removed as the CI app.

    It is reported as its own commit status, jim-entra-connector, by ad-lab.yml, which runs it apart from the LDAP
    scenarios (Test-ScenarioRunsOnItsOwn). It needs no directory, so it runs on any directory type given the tenant's
    settings: in GitHub Actions the CI app's federated credential, elsewhere JIM_SSO_ENTRA_GRAPH_TOKEN holding a Graph
    token that may manage the Connector app's credentials.

.PARAMETER Step
    Accepted for runner compatibility; the steps depend on each other, so only All is meaningful.

.PARAMETER Template
    Accepted for runner compatibility. The scenario's data is its own.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200).

.PARAMETER ApiKey
    The infrastructure API key.

.PARAMETER Domain
    The tenant domain every user principal name is in (default: the JIM test tenant's).

.PARAMETER SkipPopulate
    Accepted for runner compatibility. The scenario seeds its own data.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. The scenario uses no directory.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario 36 -DirectoryType ActiveDirectory
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Step, Template, WaitSeconds, SkipPopulate and DirectoryConfig are accepted for the runner, which passes the same parameters to every scenario.')]
param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

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
$null = $Template
$null = $WaitSeconds

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/EntraIdLab-Helpers.ps1"
. "$PSScriptRoot/../utils/EntraIdConnectorLab-Helpers.ps1"
. "$PSScriptRoot/../utils/Scenario-036-Data.ps1"

if (-not $ApiKey) { throw "API key required for authentication." }
foreach ($name in 'JIM_SSO_ENTRA_TENANT_ID', 'JIM_SSO_ENTRA_CI_CLIENT_ID') {
    if (-not [Environment]::GetEnvironmentVariable($name)) {
        throw "Scenario 036 needs $name, which the jim-ad-lab environment sets: the JIM test tenant, and the CI app that owns the Connector's app registration."
    }
}
if (-not $Domain) { $Domain = $script:Scenario036DefaultDomain }
$tenantId = $env:JIM_SSO_ENTRA_TENANT_ID
$ciClientId = $env:JIM_SSO_ENTRA_CI_CLIENT_ID
$prefix = Get-EntraIdConnectorLabRunPrefix

$testResults = @{
    Scenario = "Entra ID as a target"
    Steps = @()
    Success = $false
}

function Add-StepResult {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $testResults.Steps += @{ Name = $Name; Passed = $Passed; Detail = $Detail }
}

# --- Microsoft Graph, as the harness ---------------------------------------------------------------------------------

# The object, or $null when Graph answers 404.
function Get-GraphObject {
    param([Parameter(Mandatory)][string]$Path)
    try { return Invoke-EntraIdConnectorLabGraph -Token $script:HarnessToken -Path $Path }
    catch {
        if ("$($_.Exception.Message)" -match '\(404\)') { return $null }
        throw
    }
}

# Polls until $Check returns no mismatches, since Graph is eventually consistent; throws with the last mismatches.
function Wait-EntraState {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Check,
        [int]$TimeoutSeconds = 180
    )
    $script:LastMismatches = @()
    try {
        $null = Wait-EntraIdConnectorLabCondition -Description $Description -TimeoutSeconds $TimeoutSeconds -IntervalSeconds 10 -Condition {
            $script:LastMismatches = @(& $Check)
            $script:LastMismatches.Count -eq 0
        }
    }
    catch {
        throw "$($_.Exception.Message) Still different: $(($script:LastMismatches | Select-Object -First 10) -join '; ')"
    }
}

$userSelect = 'id,userPrincipalName,displayName,mailNickname,accountEnabled,givenName,surname,department,jobTitle,employeeId'

# What Graph holds for a person that differs from the HR data; their manager is compared by mail nickname.
function Get-PersonMismatch {
    param([Parameter(Mandatory)][object]$Person, [Parameter(Mandatory)][string]$Key)
    $user = Get-GraphObject -Path "/users/$Key`?`$select=$userSelect"
    if (-not $user) { return "$($Person.AccountName) is not in Entra ID" }
    $expected = [ordered]@{
        userPrincipalName = $Person.Upn; displayName = $Person.DisplayName; mailNickname = $Person.AccountName
        accountEnabled = $Person.Active; givenName = $Person.FirstName; surname = $Person.LastName
        department = $Person.Department; jobTitle = $Person.JobTitle; employeeId = $Person.EmployeeId
    }
    $mismatches = @(foreach ($name in $expected.Keys) {
        if ("$($user.$name)" -ne "$($expected[$name])") { "$($Person.AccountName).$name is '$($user.$name)', expected '$($expected[$name])'" }
    })
    $manager = Get-GraphObject -Path "/users/$($user.id)/manager?`$select=mailNickname"
    $managerName = if ($manager) { "$($manager.mailNickname)" } else { "" }
    if ($managerName -ne "$($Person.Manager)") { $mismatches += "$($Person.AccountName).manager is '$managerName', expected '$($Person.Manager)'" }
    return $mismatches
}

function Get-EntraGroup {
    param([Parameter(Mandatory)][string]$MailNickname)
    return @((Invoke-EntraIdConnectorLabGraph -Token $script:HarnessToken -Path "/groups?`$filter=mailNickname eq '$MailNickname'&`$select=id,displayName,mailNickname,mailEnabled,securityEnabled").value) | Select-Object -First 1
}

function Get-GroupMismatch {
    param([Parameter(Mandatory)][object]$Group)
    $entraGroup = Get-EntraGroup -MailNickname $Group.AccountName
    if (-not $entraGroup) { return "group $($Group.AccountName) is not in Entra ID" }
    $mismatches = @()
    if ($entraGroup.displayName -ne $Group.DisplayName) { $mismatches += "$($Group.AccountName).displayName is '$($entraGroup.displayName)'" }
    if ($entraGroup.mailEnabled -ne $false -or $entraGroup.securityEnabled -ne $true) { $mismatches += "$($Group.AccountName) is not a security group" }
    $members = @(Get-EntraIdConnectorLabGraphCollection -Token $script:HarnessToken -Path "/groups/$($entraGroup.id)/members?`$select=mailNickname&`$top=999" |
        ForEach-Object { "$($_.mailNickname)" } | Sort-Object)
    $expected = @($Group.Members | Sort-Object)
    $missing = @($expected | Where-Object { $_ -notin $members })
    $extra = @($members | Where-Object { $_ -notin $expected })
    if ($missing.Count -gt 0) { $mismatches += "$($Group.AccountName) lacks $($missing -join ', ')" }
    if ($extra.Count -gt 0) { $mismatches += "$($Group.AccountName) also holds $($extra -join ', ')" }
    return $mismatches
}

# --- JIM ---------------------------------------------------------------------------------------------------------------

function Invoke-HrImportAndSync {
    param([string]$Label)
    $result = Start-JIMRunProfile -ConnectedSystemId $hrSystem.id -RunProfileName "Full Import" -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $result.activityId -Name "HR Full Import ($Label)"
    $result = Start-JIMRunProfile -ConnectedSystemId $hrSystem.id -RunProfileName "Full Synchronisation" -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $result.activityId -Name "HR Full Synchronisation ($Label)"
}

# Exports to Entra ID, then reads it back, until no Pending Export is left. Graph's lists lag its writes, and a confirming
# import that misses a create has JIM send it again, which for a group (no unique name) makes a second one. So before
# each confirming import the harness waits until Graph itself shows what the step expects ($Expected, returning what
# still differs), as an administrator's schedule would leave time for. A change referring to something not yet
# exported goes out in a later round.
function Invoke-EntraExportAndConfirm {
    param([string]$Label, [scriptblock]$Expected, [int]$MaximumRounds = 4)

    for ($round = 1; $round -le $MaximumRounds; $round++) {
        $result = Start-JIMRunProfile -ConnectedSystemId $entraSystem.id -RunProfileName "Export" -Wait -PassThru
        Assert-ExportSuccess -ActivityId $result.activityId -Name "Entra ID Export ($Label, round $round)"

        if ($Expected) {
            # Not fatal here: what a later round still has to export cannot show yet. The import's check is what fails.
            try { Wait-EntraState -Description "Graph to show the export ($Label, round $round)" -Check $Expected -TimeoutSeconds 120 }
            catch { Write-Host "    $($_.Exception.Message)" -ForegroundColor DarkGray }
        }

        $result = Start-JIMRunProfile -ConnectedSystemId $entraSystem.id -RunProfileName "Full Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Entra ID Confirming Full Import ($Label, round $round)"
        $result = Start-JIMRunProfile -ConnectedSystemId $entraSystem.id -RunProfileName "Full Synchronisation" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Entra ID Full Synchronisation ($Label, round $round)"

        $pending = @(Get-JIMPendingExport -ConnectedSystemId $entraSystem.id -All)
        if ($pending.Count -eq 0) { return }
        $summary = ($pending | Select-Object -First 5 | ForEach-Object { "$($_.changeType) $($_.status)" }) -join "; "
        Write-Host "    $($pending.Count) Pending Export(s) left after round ${round}: $summary" -ForegroundColor DarkGray
        Start-Sleep -Seconds 15
    }

    $pending = @(Get-JIMPendingExport -ConnectedSystemId $entraSystem.id -All)
    $summary = ($pending | Select-Object -First 5 | ForEach-Object { "$($_.changeType) $($_.status)" }) -join "; "
    throw "Entra ID export ($Label) still had $($pending.Count) Pending Export(s) after $MaximumRounds rounds: $summary"
}

function Write-HrCsv { Write-Scenario036HrCsv -People $people -Groups $groups }

function Remove-FromGroups {
    param([Parameter(Mandatory)][string]$AccountName)
    foreach ($group in $groups) { $null = $group.Members.Remove($AccountName) }
}

# --- The run -----------------------------------------------------------------------------------------------------------

Write-TestSection "Scenario 036: Entra ID as a target"
Write-Host "Run prefix: $prefix" -ForegroundColor Gray
Write-Host "Domain:     $Domain" -ForegroundColor Gray
Write-Host ""

$script:HarnessToken = $null
$applicationObjectId = $null
$harnessKeyId = $null
$snapshotBefore = $null
$cleanupProblems = [System.Collections.Generic.List[string]]::new()

try {
    Write-TestStep "Tenant" "Registering the Connector and signing the harness in"

    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $ciToken = Get-EntraIdLabGraphToken -TenantId $tenantId -ClientId $ciClientId
    # A new certificate every run: JIM's database is new, so it holds none, and the previous run's is removed.
    $registration = Register-JIMEntraIdConnector -AccessToken $ciToken -Name $script:Scenario036ApplicationName `
        -ConnectedSystemName $script:Scenario036EntraSystemName -ResetCertificate -Confirm:$false
    if (-not $registration -or -not $registration.ConnectedSystemId) { throw "Register-JIMEntraIdConnector created no Connected System." }
    Write-Host "  OK '$($script:Scenario036ApplicationName)' set up: $(@($registration.Changes).Count) change(s), Connected System $($registration.ConnectedSystemId)" -ForegroundColor Green
    foreach ($change in @($registration.Changes)) { Write-Host "      $change" -ForegroundColor DarkGray }

    $application = @((Invoke-EntraIdConnectorLabGraph -Token $ciToken -Path "/applications?`$filter=appId eq '$($registration.ClientId)'&`$select=id").value) | Select-Object -First 1
    if (-not $application) { throw "The CI app cannot see the application $($registration.ClientId); is it still an owner?" }
    $applicationObjectId = $application.id

    $harnessCertificate = New-EntraIdConnectorLabCertificate
    $harnessKeyId = Add-EntraIdConnectorLabCredential -AccessToken $ciToken -ApplicationObjectId $applicationObjectId -Certificate $harnessCertificate
    $script:HarnessToken = Get-EntraIdConnectorLabToken -TenantId $tenantId -ClientId $registration.ClientId -Certificate $harnessCertificate
    Write-Host "  OK The harness signed in as the Connector app with its own certificate" -ForegroundColor Green

    $leftovers = Remove-EntraIdConnectorLabObject -OlderThanHours 24 -Token $script:HarnessToken
    if ($leftovers -gt 0) { Write-Host "  Deleted $leftovers object(s) earlier runs left behind" -ForegroundColor Yellow }
    $snapshotBefore = Get-EntraIdConnectorLabSnapshot -Token $script:HarnessToken
    Write-Host "  OK Snapshot of $($snapshotBefore.Count) user(s) and group(s) the run must not change" -ForegroundColor Green

    & "$PSScriptRoot/../Setup-Scenario-036.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -ConnectedSystemId $registration.ConnectedSystemId `
        -Prefix $prefix -Domain $Domain

    $systems = @(Get-JIMConnectedSystem)
    $hrSystem = $systems | Where-Object { $_.name -eq $script:Scenario036HrSystemName }
    $entraSystem = $systems | Where-Object { $_.id -eq $registration.ConnectedSystemId }
    if (-not $hrSystem -or -not $entraSystem) { throw "The scenario's Connected Systems were not found after setup." }
    $exportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq $script:Scenario036UserExportRuleName }

    $people = @(New-Scenario036People -Prefix $prefix -Domain $Domain)
    $groups = @(New-Scenario036Groups -Prefix $prefix -People $people)
    $entraIds = @{}

    # --- 1: Provision ---
    Write-TestStep "Provision" "Provisioning $($people.Count) people and $($groups.Count) groups to Entra ID"

    Write-HrCsv
    Invoke-HrImportAndSync -Label "baseline"
    $provisioned = {
        foreach ($person in $people) { Get-PersonMismatch -Person $person -Key $person.Upn }
        foreach ($group in $groups) { Get-GroupMismatch -Group $group }
        # The confirming import lists every user and group, and a list lags a direct read.
        $listed = @(Get-EntraIdConnectorLabGraphCollection -Token $script:HarnessToken -Path "/groups?`$filter=startswith(displayName,'$prefix')&`$select=id")
        if ($listed.Count -ne $groups.Count) { "Graph lists $($listed.Count) of the run's $($groups.Count) groups" }
        $listed = @(Get-EntraIdConnectorLabGraphCollection -Token $script:HarnessToken -Path "/users?`$filter=startswith(displayName,'$prefix')&`$select=id")
        if ($listed.Count -ne $people.Count) { "Graph lists $($listed.Count) of the run's $($people.Count) users" }
    }
    Invoke-EntraExportAndConfirm -Label "provision" -Expected $provisioned

    Wait-EntraState -Description "the people in Entra ID" -Check { foreach ($person in $people) { Get-PersonMismatch -Person $person -Key $person.Upn } }
    foreach ($person in $people) { $entraIds[$person.AccountName] = (Get-GraphObject -Path "/users/$($person.Upn)?`$select=id").id }
    Wait-EntraState -Description "the groups in Entra ID" -Check { foreach ($group in $groups) { Get-GroupMismatch -Group $group } }

    $staff = $groups | Where-Object { $_.Key -eq "Staff" }
    Add-StepResult -Name "Provision" -Passed $true -Detail "$($people.Count) users with attributes and managers; $($groups.Count) groups, Staff with $($staff.Members.Count) members, one nested"
    Write-Host "  OK Users, managers, groups and members read back from Graph" -ForegroundColor Green

    # --- 2: Initial Password ---
    Write-TestStep "InitialPassword" "Every new user's Initial Password is delivered"

    $null = Wait-EntraIdConnectorLabCondition -Description "the Initial Passwords to be delivered" -TimeoutSeconds 300 -IntervalSeconds 5 -Condition {
        @(Get-JIMPendingPasswordChange -ConnectedSystemId $entraSystem.id -Status Pending -All).Count -eq 0
    }
    $parked = @(Get-JIMPendingPasswordChange -ConnectedSystemId $entraSystem.id -Status Parked -All)
    $expired = @(Get-JIMPendingPasswordChange -ConnectedSystemId $entraSystem.id -Status Expired -All)
    Assert-Equal -Actual $parked.Count -Expected 0 -Message "Initial Passwords Entra ID refused (reasons: $(($parked | ForEach-Object { $_.targetMessage }) -join '; '))"
    Assert-Equal -Actual $expired.Count -Expected 0 -Message "Initial Passwords that expired undelivered"
    $initialPassword = Get-JIMSyncRuleInitialPassword -Id $exportRule.id
    Assert-Equal -Actual $initialPassword.parkedAccountCount -Expected 0 -Message "Accounts parked on the user export rule"
    # Graph shows no sign of which password a user holds (lastPasswordChangeDateTime is set by the create as well), so
    # the delivery is judged by JIM's queue: every Initial Password left it, none refused, none expired.

    Add-StepResult -Name "InitialPassword" -Passed $true -Detail "$($people.Count) delivered; none parked or expired"
    Write-Host "  OK Initial Passwords delivered through the password channel" -ForegroundColor Green

    # --- 3: Update ---
    Write-TestStep "Update" "A job title, a user principal name and a manager change"

    $retitled = $people[1]; $retitled.JobTitle = "Senior Analyst"
    $renamed = $people[2]; $renamed.Upn = "${prefix}renamed.03@$Domain"
    $moved = $people[3]; $moved.Manager = $people[[int]($people.Count / 2)].AccountName
    Write-HrCsv
    Invoke-HrImportAndSync -Label "update"
    $updated = { foreach ($person in @($retitled, $renamed, $moved)) { Get-PersonMismatch -Person $person -Key $entraIds[$person.AccountName] } }
    Invoke-EntraExportAndConfirm -Label "update" -Expected $updated
    Wait-EntraState -Description "the updates in Entra ID" -Check $updated

    Add-StepResult -Name "Update" -Passed $true -Detail "jobTitle, userPrincipalName and manager changed"
    Write-Host "  OK Attribute, user principal name and manager changes read back from Graph" -ForegroundColor Green

    # --- 4: Membership ---
    $engineering = $groups | Where-Object { $_.Key -eq "Engineering" }
    $leavingGroup = $people[4].AccountName
    $joiningGroup = $people[$people.Count - 1].AccountName
    Write-TestStep "Membership" "Removing $leavingGroup from, and adding $joiningGroup to, '$($engineering.DisplayName)'"

    $null = $engineering.Members.Remove($leavingGroup)
    $engineering.Members.Add($joiningGroup)
    Write-HrCsv
    Invoke-HrImportAndSync -Label "membership"
    Invoke-EntraExportAndConfirm -Label "membership" -Expected { Get-GroupMismatch -Group $engineering }
    Wait-EntraState -Description "the membership change in Entra ID" -Check { Get-GroupMismatch -Group $engineering }

    Add-StepResult -Name "Membership" -Passed $true -Detail "One member added and one removed"
    Write-Host "  OK Membership change read back from Graph" -ForegroundColor Green

    # --- 5: Leaver, Delete Behaviour Disable ---
    $disabled = $people[5]
    Write-TestStep "LeaverDisable" "$($disabled.AccountName) leaves scope with Delete Behaviour Disable"

    $disabled.Department = $script:Scenario036LeaverDepartment
    Remove-FromGroups -AccountName $disabled.AccountName
    Write-HrCsv
    Invoke-HrImportAndSync -Label "leaver disable"
    $disabledCheck = {
        $user = Get-GraphObject -Path "/users/$($entraIds[$disabled.AccountName])?`$select=accountEnabled"
        if (-not $user) { "$($disabled.AccountName) was deleted, not disabled" }
        elseif ($user.accountEnabled -ne $false) { "$($disabled.AccountName) is still enabled" }
        foreach ($group in $groups) { Get-GroupMismatch -Group $group }
    }
    Invoke-EntraExportAndConfirm -Label "leaver disable" -Expected $disabledCheck
    Wait-EntraState -Description "$($disabled.AccountName) disabled" -Check $disabledCheck

    Add-StepResult -Name "LeaverDisable" -Passed $true -Detail "$($disabled.AccountName) disabled and kept"
    Write-Host "  OK $($disabled.AccountName) disabled in Entra ID, not deleted" -ForegroundColor Green

    # --- 6: Leaver, Delete Behaviour Delete ---
    $deleted = $people[6]
    Write-TestStep "LeaverDelete" "$($deleted.AccountName) leaves scope with Delete Behaviour Delete"

    $graphConnector = Get-JIMConnectorDefinition | Where-Object { $_.name -eq "JIM Microsoft Entra ID Connector" }
    $deleteBehaviour = (Get-JIMConnectorDefinition -Id $graphConnector.id).settings | Where-Object { $_.name -eq "Delete Behaviour" }
    Set-JIMConnectedSystem -Id $entraSystem.id -SettingValues @{ $deleteBehaviour.id = @{ stringValue = "Delete" } } | Out-Null

    $deleted.Department = $script:Scenario036LeaverDepartment
    Remove-FromGroups -AccountName $deleted.AccountName
    Write-HrCsv
    Invoke-HrImportAndSync -Label "leaver delete"
    $deletedId = $entraIds[$deleted.AccountName]
    $deletedCheck = {
        if (Get-GraphObject -Path "/users/$deletedId`?`$select=id") { "$($deleted.AccountName) is still in Entra ID" }
        elseif (-not (Get-GraphObject -Path "/directory/deletedItems/$deletedId")) { "$($deleted.AccountName) is not in deleted items" }
        foreach ($group in $groups) { Get-GroupMismatch -Group $group }
    }
    Invoke-EntraExportAndConfirm -Label "leaver delete" -Expected $deletedCheck
    Wait-EntraState -Description "$($deleted.AccountName) deleted" -Check $deletedCheck

    Add-StepResult -Name "LeaverDelete" -Passed $true -Detail "$($deleted.AccountName) deleted, and in deleted items"
    Write-Host "  OK $($deleted.AccountName) deleted, and restorable from deleted items" -ForegroundColor Green

    # --- 7: Idempotence ---
    Write-TestStep "Idempotence" "Synchronising again with nothing changed"

    Invoke-HrImportAndSync -Label "idempotence"
    $result = Start-JIMRunProfile -ConnectedSystemId $entraSystem.id -RunProfileName "Full Import" -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $result.activityId -Name "Entra ID Full Import (idempotence)"
    $result = Start-JIMRunProfile -ConnectedSystemId $entraSystem.id -RunProfileName "Full Synchronisation" -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $result.activityId -Name "Entra ID Full Synchronisation (idempotence)"
    $pending = @(Get-JIMPendingExport -ConnectedSystemId $entraSystem.id -All)
    Assert-Equal -Actual $pending.Count -Expected 0 -Message "Pending Exports after synchronising with nothing changed ($(($pending | Select-Object -First 5 | ForEach-Object { "$($_.changeType) $($_.status)" }) -join '; '))"

    Add-StepResult -Name "Idempotence" -Passed $true -Detail "No Pending Exports"
    Write-Host "  OK Nothing to export when nothing changed" -ForegroundColor Green

    # --- 8: Untouchable accounts ---
    Write-TestStep "Untouchable" "The tenant's own accounts are imported, and stay out of every rule"

    $ownIds = @($snapshotBefore.Keys | Where-Object { $_ -like 'user:*' } | ForEach-Object { $_.Substring(5) })
    $entraObjects = @(Get-JIMConnectedSystemObject -ConnectedSystemId $entraSystem.id -All)
    $ownObjects = @($entraObjects | Where-Object { $_.externalIdValue -in $ownIds })
    Assert-Condition -Condition ($ownObjects.Count -gt 0) -Message "The tenant's own users were imported ($($ownObjects.Count) of $($ownIds.Count))"
    $touched = @($ownObjects | Where-Object { "$($_.joinType)" -ne 'NotJoined' -or $_.hasPendingExport })
    Assert-Equal -Actual $touched.Count -Expected 0 -Message "The tenant's own users joined or with a Pending Export ($(($touched | ForEach-Object { $_.displayName }) -join ', '))"

    Add-StepResult -Name "Untouchable" -Passed $true -Detail "$($ownObjects.Count) of the tenant's own users imported, none joined"
    Write-Host "  OK The tenant's own accounts imported and left alone (the snapshot is compared after cleanup)" -ForegroundColor Green

    $testResults.Success = $true
}
catch {
    $testResults.Success = $false
    Write-Failure "Scenario 036 failed: $_"
}
finally {
    Write-TestStep "Cleanup" "Deleting what the run made, comparing the snapshot, removing the harness's certificate"

    if ($script:HarnessToken) {
        try {
            $removed = Remove-EntraIdConnectorLabObject -Prefix $prefix -Token $script:HarnessToken
            Write-Host "  Deleted $removed object(s) the run made" -ForegroundColor Gray
        }
        catch { $cleanupProblems.Add("Deleting the run's objects failed: $($_.Exception.Message)") }

        if ($snapshotBefore) {
            try {
                $differences = Compare-EntraIdConnectorLabSnapshot -Before $snapshotBefore -After (Get-EntraIdConnectorLabSnapshot -Token $script:HarnessToken)
                if ($differences.Count -gt 0) {
                    $cleanupProblems.Add("The run changed $($differences.Count) object(s) it did not make: $(($differences | Select-Object -First 5) -join ' | ')")
                }
                else {
                    Add-StepResult -Name "Snapshot" -Passed $true -Detail "$($snapshotBefore.Count) users and groups the run did not make are unchanged"
                    Write-Host "  OK The $($snapshotBefore.Count) users and groups the run did not make are unchanged" -ForegroundColor Green
                }
            }
            catch { $cleanupProblems.Add("Comparing the snapshot failed: $($_.Exception.Message)") }
        }
    }

    if ($applicationObjectId -and $harnessKeyId) {
        try {
            # A fresh token: the first may have expired during the run.
            $ciToken = Get-EntraIdLabGraphToken -TenantId $tenantId -ClientId $ciClientId
            Remove-EntraIdConnectorLabCredential -AccessToken $ciToken -ApplicationObjectId $applicationObjectId -KeyId $harnessKeyId
            Write-Host "  Removed the harness's certificate" -ForegroundColor Gray
        }
        catch {
            # It expires within two days, and the next run removes it; not a reason to fail the run.
            Write-Host "  Could not remove the harness's certificate (the next run will): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    if ($cleanupProblems.Count -gt 0) {
        $testResults.Success = $false
        foreach ($problem in $cleanupProblems) { Write-Failure $problem }
    }
    if (Get-Module JIM) { Disconnect-JIM -ErrorAction SilentlyContinue }
}

Write-TestSection "Scenario 036 $(if ($testResults.Success) { 'Complete' } else { 'Failed' })"
foreach ($stepResult in $testResults.Steps) {
    Write-Host "  [PASS] $($stepResult.Name): $($stepResult.Detail)" -ForegroundColor Green
}

return $testResults
