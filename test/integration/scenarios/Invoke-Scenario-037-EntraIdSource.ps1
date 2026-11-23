# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 037: Entra ID as a source (the Microsoft Entra ID Connector imports from a real tenant)

.DESCRIPTION
    The harness seeds users and groups in the JIM test tenant in Microsoft Entra ID through its own Microsoft Graph
    requests, never through JIM, and JIM imports them through the Microsoft Entra ID Connector (#361) and projects them
    into the Metaverse. Writing the data independently of JIM means a mistake mirrored in JIM's import and export cannot
    cancel itself out. The plan is engineering/plans/doing/ENTRA_ID_CONNECTOR_INTEGRATION_SCENARIOS.md.

    Before the steps, as the CI app (Get-EntraIdLabGraphToken), the scenario runs Register-JIMEntraIdConnector
    -ResetCertificate against the permanent "JIM Entra ID Connector (CI)" app, adds the harness's own two-day certificate
    and signs in with it, deletes what an earlier failed run left more than a day ago, and snapshots every account it did
    not make. It then seeds the tenant (utils/Scenario-037-Data.ps1 lists what, and why).

      1  Full Import      A Full Import and Full Synchronisation project every seeded user and group: attributes, the
                          sourceOfAuthority each reads as (cloud, and externalEntraDirectory for the guest), memberships,
                          the nested group, the hidden-membership group's members, and a group whose other member is one
                          of the tenant's own users, out of scope.
      2  Scoping          The tenant's own accounts are imported and never projected.
      3  Delta Import     After a job title change, a member added and one removed, a user deleted and a group deleted, a
                          Delta Import and Delta Synchronisation bring the Metaverse in line, deleting the two.
      4  Delta integrity  A Delta Import with nothing changed reads nothing, and does not fall back to reading everything.

    Everything the run made carries its prefix and is deleted at the end, whatever happened; the snapshot is then
    compared, and a difference fails the run. The harness's certificate is removed as the CI app.

    It is reported with Scenario 036 as the commit status jim-entra-connector, by ad-lab.yml, which runs it apart from
    the LDAP scenarios (Test-ScenarioRunsOnItsOwn). It needs no directory, so it runs on any directory type given the
    tenant's settings: in GitHub Actions the CI app's federated credential, elsewhere JIM_SSO_ENTRA_GRAPH_TOKEN holding a
    Graph token that may manage the Connector app's credentials.

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
    ./Run-IntegrationTests.ps1 -Scenario 37 -DirectoryType ActiveDirectory
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
. "$PSScriptRoot/../utils/Scenario-037-Data.ps1"

if (-not $ApiKey) { throw "API key required for authentication." }
foreach ($name in 'JIM_SSO_ENTRA_TENANT_ID', 'JIM_SSO_ENTRA_CI_CLIENT_ID') {
    if (-not [Environment]::GetEnvironmentVariable($name)) {
        throw "Scenario 037 needs $name, which the jim-ad-lab environment sets: the JIM test tenant, and the CI app that owns the Connector's app registration."
    }
}
if (-not $Domain) { $Domain = $script:Scenario037DefaultDomain }
$tenantId = $env:JIM_SSO_ENTRA_TENANT_ID
$ciClientId = $env:JIM_SSO_ENTRA_CI_CLIENT_ID
$prefix = Get-EntraIdConnectorLabRunPrefix

$testResults = @{
    Scenario = "Entra ID as a source"
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

function New-SeededUser {
    param([Parameter(Mandatory)][object]$Person)
    $body = @{
        accountEnabled    = $true
        displayName       = $Person.DisplayName
        mailNickname      = $Person.AccountName
        userPrincipalName = $Person.Upn
        givenName         = $Person.GivenName
        surname           = $Person.Surname
        department        = $Person.Department
        jobTitle          = $Person.JobTitle
        passwordProfile   = @{ forceChangePasswordNextSignIn = $true; password = (New-Scenario037Password) }
    }
    # Created as a guest rather than invited: an invitation needs User.Invite.All, which the Connector app does not hold,
    # and the Connector reads either as externalEntraDirectory.
    if ($Person.IsGuest) { $body.userType = 'Guest' }
    return Invoke-EntraIdConnectorLabGraph -Token $script:HarnessToken -Method Post -Path "/users" -Body $body
}

function New-SeededGroup {
    param([Parameter(Mandatory)][object]$Group, [Parameter(Mandatory)][string[]]$MemberIds)
    $body = @{
        displayName     = $Group.DisplayName
        mailNickname    = $Group.AccountName
        mailEnabled     = $Group.Unified
        securityEnabled = -not $Group.Unified
        groupTypes      = @(if ($Group.Unified) { 'Unified' })
    }
    if ($Group.Hidden) { $body.visibility = 'HiddenMembership' }
    if ($MemberIds.Count -gt 0) { $body.'members@odata.bind' = @($MemberIds | ForEach-Object { "https://graph.microsoft.com/v1.0/directoryObjects/$_" }) }
    return Invoke-EntraIdConnectorLabGraph -Token $script:HarnessToken -Method Post -Path "/groups" -Body $body
}

# The ids of the run's objects of a kind that Graph lists, keyed by mail nickname.
function Get-ListedRunObjects {
    param([Parameter(Mandatory)][ValidateSet('users', 'groups')][string]$Kind)
    $listed = @(Get-EntraIdConnectorLabGraphCollection -Token $script:HarnessToken -Path "/$Kind`?`$filter=startswith(displayName,'$prefix')&`$select=id,mailNickname")
    $byName = @{}
    foreach ($object in $listed) { $byName[[string]$object.mailNickname] = [string]$object.id }
    return $byName
}

# --- JIM, as the operator ----------------------------------------------------------------------------------------------

# The Metaverse Object with this Account Name, in full, or $null.
function Get-ProjectedObject {
    param([Parameter(Mandatory)][string]$AccountName)
    $found = @(Get-JIMMetaverseObject -AttributeName "Account Name" -AttributeValue $AccountName -ErrorAction SilentlyContinue)
    if ($found.Count -eq 0) { return $null }
    if ($found.Count -gt 1) { throw "$($found.Count) Metaverse Objects have the Account Name '$AccountName'." }
    return Get-JIMMetaverseObject -Id $found[0].id
}

function Get-ProjectedValues {
    param([Parameter(Mandatory)][object]$MetaverseObject, [Parameter(Mandatory)][string]$AttributeName)
    $values = @($MetaverseObject.attributeValues | Where-Object { $_.attributeName -eq $AttributeName })
    return @($values | ForEach-Object { if ($null -ne $_.referenceValueId) { [string]$_.referenceValueDisplayName } else { [string]$_.stringValue } })
}

function Get-PersonMismatch {
    param([Parameter(Mandatory)][object]$Person)
    $projected = Get-ProjectedObject -AccountName $Person.AccountName
    if (-not $projected) { return "$($Person.AccountName) is not in the Metaverse" }
    $mismatches = @()
    foreach ($pair in @(
        @('Display Name', $Person.DisplayName), @('User Principal Name', $Person.Upn), @('First Name', $Person.GivenName),
        @('Last Name', $Person.Surname), @('Department', $Person.Department), @('Job Title', $Person.JobTitle))) {
        $actual = @(Get-ProjectedValues -MetaverseObject $projected -AttributeName $pair[0]) -join ', '
        if ($actual -cne $pair[1]) { $mismatches += "$($Person.AccountName) $($pair[0]) is '$actual', not '$($pair[1])'" }
    }
    return $mismatches
}

function Get-GroupMismatch {
    param([Parameter(Mandatory)][object]$Group)
    $projected = Get-ProjectedObject -AccountName $Group.AccountName
    if (-not $projected) { return "group $($Group.AccountName) is not in the Metaverse" }
    $expected = @(@($Group.Members | ForEach-Object { ($people | Where-Object AccountName -eq $_).DisplayName }) +
        @($Group.GroupMembers | ForEach-Object { "$prefix$_" }) | Sort-Object)
    $actual = @(Get-ProjectedValues -MetaverseObject $projected -AttributeName 'Static Members' | Sort-Object)
    if (($actual -join '|') -cne ($expected -join '|')) {
        return "group $($Group.AccountName) has members '$($actual -join ', ')', not '$($expected -join ', ')'"
    }
    return @()
}

function Get-SourceOfAuthority {
    param([Parameter(Mandatory)][string]$EntraId)
    $object = $script:EntraObjects | Where-Object { $_.externalIdValue -eq $EntraId } | Select-Object -First 1
    if (-not $object) { return "(not imported)" }
    $values = @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $entraSystem.id -CsoId $object.id -AttributeName 'sourceOfAuthority' -All)
    return (@($values | ForEach-Object { $_.stringValue }) -join ', ')
}

# Runs an import and a synchronisation of the given kind, then again a few times while $Check still finds differences:
# Graph's lists and its delta rounds lag its writes, so one run straight after a change can miss part of it.
function Invoke-EntraImportUntil {
    param(
        [Parameter(Mandatory)][ValidateSet('Full', 'Delta')][string]$Kind,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][scriptblock]$Check,
        [int]$MaximumRounds = 4
    )
    $synchronisation = if ($Kind -eq 'Full') { 'Full Synchronisation' } else { 'Delta Synchronisation' }
    for ($round = 1; $round -le $MaximumRounds; $round++) {
        $result = Start-JIMRunProfile -ConnectedSystemId $entraSystem.id -RunProfileName "$Kind Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Entra ID $Kind Import ($Label, round $round)"
        $result = Start-JIMRunProfile -ConnectedSystemId $entraSystem.id -RunProfileName $synchronisation -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Entra ID $synchronisation ($Label, round $round)"

        $script:EntraObjects = @(Get-JIMConnectedSystemObject -ConnectedSystemId $entraSystem.id -All)
        $mismatches = @(& $Check)
        if ($mismatches.Count -eq 0) { return $round }
        Write-Host "    After round ${round}: $(($mismatches | Select-Object -First 5) -join '; ')" -ForegroundColor DarkGray
        if ($round -lt $MaximumRounds) { Start-Sleep -Seconds 30 }
    }
    throw "The Metaverse still differs from Entra ID after $MaximumRounds $Kind Import round(s) ($Label): $(($mismatches | Select-Object -First 10) -join '; ')"
}

# --- The run -----------------------------------------------------------------------------------------------------------

Write-TestSection "Scenario 037: Entra ID as a source"
Write-Host "Run prefix: $prefix" -ForegroundColor Gray
Write-Host "Domain:     $Domain" -ForegroundColor Gray
Write-Host ""

$script:HarnessToken = $null
$script:EntraObjects = @()
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
    $registration = Register-JIMEntraIdConnector -AccessToken $ciToken -Name $script:Scenario037ApplicationName `
        -ConnectedSystemName $script:Scenario037EntraSystemName -ResetCertificate -Confirm:$false
    if (-not $registration -or -not $registration.ConnectedSystemId) { throw "Register-JIMEntraIdConnector created no Connected System." }
    Write-Host "  OK '$($script:Scenario037ApplicationName)' set up: Connected System $($registration.ConnectedSystemId)" -ForegroundColor Green

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
    $ownUserIds = @($snapshotBefore.Keys | Where-Object { $_ -like 'user:*' } | ForEach-Object { $_.Substring(5) } | Sort-Object)
    if ($ownUserIds.Count -eq 0) { throw "The tenant holds no user of its own to put out of scope." }

    # --- Seed ---
    Write-TestStep "Seed" "Seeding the tenant through Microsoft Graph"

    $people = @(New-Scenario037People -Prefix $prefix -Domain $Domain)
    $groups = @(New-Scenario037Groups -Prefix $prefix)
    $entraIds = @{}
    foreach ($person in $people) { $entraIds[$person.AccountName] = [string](New-SeededUser -Person $person).id }

    # A user just created can be refused as a member for a few seconds (404 or 400), so each group waits that out.
    $hiddenSkipped = $null
    foreach ($group in $groups) {
        $memberIds = @(@($group.Members | ForEach-Object { $entraIds[$_] }) + @($group.GroupMembers | ForEach-Object { $entraIds["$prefix$($_.ToLowerInvariant())"] }))
        if ($group.Key -eq 'Mixed') { $memberIds += $ownUserIds[0] }
        $created = $null
        for ($attempt = 1; -not $created; $attempt++) {
            try { $created = New-SeededGroup -Group $group -MemberIds $memberIds }
            catch {
                $message = "$($_.Exception.Message)"
                if ($group.Unified -and $message -notmatch '\((400|404)\).*(does not exist|Resource|reference)') {
                    # A tenant that cannot hold Microsoft 365 groups refuses one outright; say so rather than fail on it.
                    $hiddenSkipped = $message
                    break
                }
                if ($attempt -ge 6 -or $message -notmatch '\((400|404)\)') { throw }
                Start-Sleep -Seconds (5 * $attempt)
            }
        }
        if ($created) { $entraIds[$group.AccountName] = [string]$created.id }
    }
    if ($hiddenSkipped) {
        $groups = @($groups | Where-Object { -not $_.Hidden })
        Write-Host "  WARNING The tenant refused the Microsoft 365 group with hidden membership, so that check is skipped: $hiddenSkipped" -ForegroundColor Yellow
    }

    # Graph's lists lag its writes, and the Full Import reads lists.
    Wait-EntraState -Description "Graph to list the seeded users and groups" -TimeoutSeconds 300 -Check {
        $listedUsers = Get-ListedRunObjects -Kind users
        $listedGroups = Get-ListedRunObjects -Kind groups
        if ($listedUsers.Count -ne $people.Count) { "Graph lists $($listedUsers.Count) of the run's $($people.Count) users" }
        if ($listedGroups.Count -ne $groups.Count) { "Graph lists $($listedGroups.Count) of the run's $($groups.Count) groups" }
    }
    Write-Host "  OK $($people.Count) users (one a guest) and $($groups.Count) groups seeded and listed" -ForegroundColor Green

    & "$PSScriptRoot/../Setup-Scenario-037.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -ConnectedSystemId $registration.ConnectedSystemId -Prefix $prefix
    $entraSystem = Get-JIMConnectedSystem -Id $registration.ConnectedSystemId

    # --- 1: Full Import and projection ---
    Write-TestStep "FullImport" "A Full Import and Full Synchronisation project every seeded user and group"

    $projected = {
        foreach ($person in $people) { Get-PersonMismatch -Person $person }
        foreach ($group in $groups) { Get-GroupMismatch -Group $group }
    }
    $rounds = Invoke-EntraImportUntil -Kind Full -Label "projection" -Check $projected

    $authorityMismatches = @()
    foreach ($person in $people) {
        $expected = if ($person.IsGuest) { 'externalEntraDirectory' } else { 'cloud' }
        $actual = Get-SourceOfAuthority -EntraId $entraIds[$person.AccountName]
        if ($actual -cne $expected) { $authorityMismatches += "$($person.AccountName) reads as '$actual', not '$expected'" }
    }
    foreach ($group in $groups) {
        $actual = Get-SourceOfAuthority -EntraId $entraIds[$group.AccountName]
        if ($actual -cne 'cloud') { $authorityMismatches += "group $($group.AccountName) reads as '$actual', not 'cloud'" }
    }
    Assert-Equal -Actual $authorityMismatches.Count -Expected 0 -Message "sourceOfAuthority ($($authorityMismatches -join '; '))"

    $detail = "$($people.Count) users and $($groups.Count) groups projected in $rounds round(s), sourceOfAuthority cloud and externalEntraDirectory; nested and out-of-scope members as expected"
    if ($hiddenSkipped) { $detail += "; the hidden-membership group was skipped" }
    Add-StepResult -Name "FullImport" -Passed $true -Detail $detail
    Write-Host "  OK $detail" -ForegroundColor Green

    # --- 2: Scoping ---
    Write-TestStep "Scoping" "The tenant's own accounts are imported, and never projected"

    $ownObjects = @($script:EntraObjects | Where-Object { $_.externalIdValue -in $ownUserIds })
    Assert-Condition -Condition ($ownObjects.Count -gt 0) -Message "The tenant's own users were imported ($($ownObjects.Count) of $($ownUserIds.Count))"
    $joined = @($ownObjects | Where-Object { "$($_.joinType)" -ne 'NotJoined' })
    Assert-Equal -Actual $joined.Count -Expected 0 -Message "The tenant's own users projected or joined ($(($joined | ForEach-Object { $_.displayName }) -join ', '))"

    Add-StepResult -Name "Scoping" -Passed $true -Detail "$($ownObjects.Count) of the tenant's own users imported, none projected"
    Write-Host "  OK The tenant's own accounts imported and left out of every rule" -ForegroundColor Green

    # --- 3: Delta Import ---
    Write-TestStep "DeltaImport" "A job title, a member added and one removed, a user and a group deleted"

    $changed = $people[0]
    $changed.JobTitle = 'Principal Engineer'
    $null = Invoke-EntraIdConnectorLabGraph -Token $script:HarnessToken -Method Patch -Path "/users/$($entraIds[$changed.AccountName])" -Body @{ jobTitle = $changed.JobTitle }

    $engineering = $groups | Where-Object Key -eq 'Engineering'
    $added = $people[4]
    $removed = $people[1]
    $null = Invoke-EntraIdConnectorLabGraph -Token $script:HarnessToken -Method Post -Path "/groups/$($entraIds[$engineering.AccountName])/members/`$ref" `
        -Body @{ '@odata.id' = "https://graph.microsoft.com/v1.0/directoryObjects/$($entraIds[$added.AccountName])" }
    $null = Invoke-EntraIdConnectorLabGraph -Token $script:HarnessToken -Method Delete -Path "/groups/$($entraIds[$engineering.AccountName])/members/$($entraIds[$removed.AccountName])/`$ref"
    $engineering.Members.Add($added.AccountName)
    $null = $engineering.Members.Remove($removed.AccountName)

    $leaver = $people[5]
    $null = Invoke-EntraIdConnectorLabGraph -Token $script:HarnessToken -Method Delete -Path "/users/$($entraIds[$leaver.AccountName])"
    $temporary = $groups | Where-Object Key -eq 'Temporary'
    $null = Invoke-EntraIdConnectorLabGraph -Token $script:HarnessToken -Method Delete -Path "/groups/$($entraIds[$temporary.AccountName])"

    # Before a Delta Import, a direct read must show every change; the Delta Import then gets more rounds if its own
    # delta round still lags.
    Wait-EntraState -Description "Graph to show the changes" -Check {
        $user = Get-GraphObject -Path "/users/$($entraIds[$changed.AccountName])?`$select=jobTitle"
        if (-not $user -or $user.jobTitle -ne $changed.JobTitle) { "$($changed.AccountName)'s job title is not yet '$($changed.JobTitle)'" }
        $members = @(Get-EntraIdConnectorLabGraphCollection -Token $script:HarnessToken -Path "/groups/$($entraIds[$engineering.AccountName])/members?`$select=mailNickname" |
            ForEach-Object { [string]$_.mailNickname } | Sort-Object)
        if (($members -join '|') -cne (@($engineering.Members | Sort-Object) -join '|')) { "Engineering lists '$($members -join ', ')'" }
        if (Get-GraphObject -Path "/users/$($entraIds[$leaver.AccountName])?`$select=id") { "$($leaver.AccountName) is still readable" }
        if (Get-GraphObject -Path "/groups/$($entraIds[$temporary.AccountName])?`$select=id") { "$($temporary.AccountName) is still readable" }
    }

    $deltaState = {
        Get-PersonMismatch -Person $changed
        Get-GroupMismatch -Group $engineering
        if (Get-ProjectedObject -AccountName $leaver.AccountName) { "$($leaver.AccountName) is still in the Metaverse" }
        if (Get-ProjectedObject -AccountName $temporary.AccountName) { "group $($temporary.AccountName) is still in the Metaverse" }
    }
    $rounds = Invoke-EntraImportUntil -Kind Delta -Label "changes" -Check $deltaState

    Add-StepResult -Name "DeltaImport" -Passed $true -Detail "Job title, membership and two deletions reached the Metaverse in $rounds Delta Import round(s)"
    Write-Host "  OK The Delta Import brought the Metaverse in line, deleting the user and the group" -ForegroundColor Green

    # --- 4: Delta integrity ---
    Write-TestStep "DeltaIntegrity" "A Delta Import with nothing changed reads nothing"

    # Graph can still report a late write from the step before; three tries, each strict about falling back.
    $quiet = $false
    for ($attempt = 1; $attempt -le 3 -and -not $quiet; $attempt++) {
        $result = Start-JIMRunProfile -ConnectedSystemId $entraSystem.id -RunProfileName "Delta Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Entra ID Delta Import (nothing changed, attempt $attempt)"
        $stats = Get-JIMActivityStats -ActivityId $result.activityId
        $read = [int]$stats.totalCsoAdds + [int]$stats.totalCsoUpdates + [int]$stats.totalCsoDeletes
        if ($read -eq 0) { $quiet = $true }
        else {
            Write-Host "    Attempt ${attempt}: $read change(s) still read; trying again in 30 seconds" -ForegroundColor DarkGray
            Start-Sleep -Seconds 30
        }
    }
    Assert-Condition -Condition $quiet -Message "A Delta Import with nothing changed read nothing"

    Add-StepResult -Name "DeltaIntegrity" -Passed $true -Detail "Nothing read, and no fall back to reading everything"
    Write-Host "  OK Nothing to read when nothing changed" -ForegroundColor Green

    $testResults.Success = $true
}
catch {
    $testResults.Success = $false
    Write-Failure "Scenario 037 failed: $_"
}
finally {
    Write-TestStep "Cleanup" "Deleting what the run made, comparing the snapshot, removing the harness's certificate"

    if ($script:HarnessToken) {
        try {
            $removedCount = Remove-EntraIdConnectorLabObject -Prefix $prefix -Token $script:HarnessToken
            Write-Host "  Deleted $removedCount object(s) the run made" -ForegroundColor Gray
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
            $ciToken = Get-EntraIdLabGraphToken -TenantId $tenantId -ClientId $ciClientId
            Remove-EntraIdConnectorLabCredential -AccessToken $ciToken -ApplicationObjectId $applicationObjectId -KeyId $harnessKeyId
            Write-Host "  Removed the harness's certificate" -ForegroundColor Gray
        }
        catch {
            Write-Host "  Could not remove the harness's certificate (the next run will): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    if ($cleanupProblems.Count -gt 0) {
        $testResults.Success = $false
        foreach ($problem in $cleanupProblems) { Write-Failure $problem }
    }
    if (Get-Module JIM) { Disconnect-JIM -ErrorAction SilentlyContinue }
}

Write-TestSection "Scenario 037 $(if ($testResults.Success) { 'Complete' } else { 'Failed' })"
foreach ($stepResult in $testResults.Steps) {
    Write-Host "  [PASS] $($stepResult.Name): $($stepResult.Detail)" -ForegroundColor Green
}

return $testResults
