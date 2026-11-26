# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 038: hybrid and Source of Authority (the Microsoft Entra ID Connector against objects synchronised
    from Active Directory)

.DESCRIPTION
    The harness creates users and groups in the lab's hybrid forest (dc-hybrid), in an organisational unit of the run's
    own under the one Entra Cloud Sync synchronises. Cloud Sync copies them into the hybrid test tenant, and JIM imports
    them from there through the Microsoft Entra ID Connector (#3029). Only the Source of Authority switches are written
    to Entra ID directly, since Entra ID alone holds them. The plan is
    engineering/plans/doing/ENTRA_ID_CONNECTOR_INTEGRATION_SCENARIOS.md, Scenario 038.

    Before the steps, as the hybrid tenant's CI app (Get-EntraIdLabGraphToken), the scenario runs
    Register-JIMEntraIdConnector -TrackSourceOfAuthority -ResetCertificate against the tenant's permanent Connector app,
    adds the harness's own two-day certificate and signs in with it, deletes what an earlier failed run left, and
    snapshots every account it did not make. It then creates the run's objects in Active Directory
    (utils/Scenario-038-Data.ps1 lists what, and why) and waits for Cloud Sync to copy them.

      1  Synchronised     A Full Import and Full Synchronisation project every user and group, each reading
                          sourceOfAuthority onPremisesDirectory.
      2  ExportRefused    An export of a job title to a person Active Directory owns is refused by Entra ID, and
                          reported on the person, in Entra ID's words and the Connector's.
      3  SwitchToCloud    A user and a group switched to cloud management read as cloud after a Delta Import, which
                          reads every object to see it; the others still read onPremisesDirectory.
      4  SwitchBack       Switched back, they read as onPremisesDirectory again. What Entra ID reports for
                          onPremisesSyncEnabled at each stage is printed, which answers the plan's open question.

    Everything the run made is deleted at the end, whatever happened: its organisational unit in Active Directory, which
    Cloud Sync then deletes from Entra ID, and anything still managed in Entra ID, which the harness deletes itself. The
    snapshot is then compared, and a difference fails the run. The harness's certificate is removed as the CI app.

    It is reported as the commit status jim-entra-hybrid by ad-lab.yml, which runs it apart from the LDAP scenarios
    (Test-ScenarioRunsOnItsOwn), and only where the hybrid tenant is configured. It needs the Active Directory lab's
    hybrid forest (Get-DirectoryConfig -Instance Hybrid) and the hybrid tenant's settings: JIM_ENTRA_HYBRID_TENANT_ID and
    JIM_ENTRA_HYBRID_CI_CLIENT_ID, with the CI app's federated credential in GitHub Actions, and elsewhere
    JIM_SSO_ENTRA_GRAPH_TOKEN holding a Graph token for that app. test/integration/ad-lab/README.md, The hybrid forest,
    describes the one-time setup.

.PARAMETER Step
    Accepted for runner compatibility; the steps depend on each other, so only All is meaningful.

.PARAMETER Template
    Accepted for runner compatibility. The scenario's data is its own.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200).

.PARAMETER ApiKey
    The infrastructure API key.

.PARAMETER SyncTimeoutMinutes
    How long to wait for Entra Cloud Sync to copy a change from Active Directory (default 20; it runs every two minutes).

.PARAMETER SkipPopulate
    Accepted for runner compatibility. The scenario creates its own data.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. The run's directory is dc-primary; this scenario uses the hybrid forest.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario 38 -DirectoryType ActiveDirectory
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
    [ValidateRange(2, 120)]
    [int]$SyncTimeoutMinutes = 20,

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
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"
. "$PSScriptRoot/../utils/Directory-Helpers.ps1"
. "$PSScriptRoot/../utils/ActiveDirectoryLab-Helpers.ps1"
. "$PSScriptRoot/../utils/EntraIdLab-Helpers.ps1"
. "$PSScriptRoot/../utils/EntraIdConnectorLab-Helpers.ps1"
. "$PSScriptRoot/../utils/Scenario-038-Data.ps1"

if (-not $ApiKey) { throw "API key required for authentication." }
foreach ($name in 'JIM_ENTRA_HYBRID_TENANT_ID', 'JIM_ENTRA_HYBRID_CI_CLIENT_ID') {
    if (-not [Environment]::GetEnvironmentVariable($name)) {
        throw "Scenario 038 needs $name, which the jim-ad-lab environment sets: the hybrid test tenant, and the CI app that owns its Connector app registration. See test/integration/ad-lab/README.md, The hybrid forest."
    }
}
$tenantId = $env:JIM_ENTRA_HYBRID_TENANT_ID
$ciClientId = $env:JIM_ENTRA_HYBRID_CI_CLIENT_ID
$prefix = Get-EntraIdConnectorLabRunPrefix
$hybrid = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Hybrid
$runOuDn = Get-Scenario038RunOuDn -Prefix $prefix -SyncOuDn $hybrid.SyncOuDn
$syncTimeoutSeconds = $SyncTimeoutMinutes * 60

$testResults = @{
    Scenario = "Hybrid and Source of Authority"
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

# Polls until $Check returns no mismatches; throws with the last mismatches.
function Wait-EntraState {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Check,
        [int]$TimeoutSeconds = 180,
        [int]$IntervalSeconds = 10
    )
    $script:LastMismatches = @()
    try {
        $null = Wait-EntraIdConnectorLabCondition -Description $Description -TimeoutSeconds $TimeoutSeconds -IntervalSeconds $IntervalSeconds -Condition {
            $script:LastMismatches = @(& $Check)
            $script:LastMismatches.Count -eq 0
        }
    }
    catch {
        throw "$($_.Exception.Message) Still different: $(($script:LastMismatches | Select-Object -First 10) -join '; ')"
    }
}

# The run's users or groups Graph lists, keyed by display name, with whether each is synchronised from Active Directory.
function Get-ListedRunObjects {
    param([Parameter(Mandatory)][ValidateSet('users', 'groups')][string]$Kind)
    $listed = @(Get-EntraIdConnectorLabGraphCollection -Token $script:HarnessToken -Path "/$Kind`?`$filter=startswith(displayName,'$prefix')&`$select=id,displayName,onPremisesSyncEnabled")
    $byName = @{}
    foreach ($object in $listed) { $byName[[string]$object.displayName] = $object }
    return $byName
}

function Get-CloudManaged {
    param([Parameter(Mandatory)][ValidateSet('users', 'groups')][string]$Kind, [Parameter(Mandatory)][string]$Id)
    $behaviour = Get-GraphObject -Path "/$Kind/$Id/onPremisesSyncBehavior"
    return [bool](Get-EntraIdConnectorLabValue -Object $behaviour -Name 'isCloudManaged')
}

function Get-OnPremisesSyncEnabled {
    param([Parameter(Mandatory)][ValidateSet('users', 'groups')][string]$Kind, [Parameter(Mandatory)][string]$Id)
    $object = Get-GraphObject -Path "/$Kind/$Id`?`$select=onPremisesSyncEnabled"
    $value = Get-EntraIdConnectorLabValue -Object $object -Name 'onPremisesSyncEnabled'
    return $(if ($null -eq $value) { 'null' } else { "$value".ToLowerInvariant() })
}

function Set-CloudManaged {
    param(
        [Parameter(Mandatory)][ValidateSet('users', 'groups')][string]$Kind,
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][bool]$Value,
        [Parameter(Mandatory)][string]$Name
    )
    try {
        $null = Invoke-EntraIdConnectorLabGraph -Token $script:HarnessToken -Method Patch -Path "/$Kind/$Id/onPremisesSyncBehavior" -Body @{ isCloudManaged = $Value }
    }
    catch {
        $hint = if (-not $Value -and $Kind -eq 'users') {
            " Switching a user back to Active Directory is refused while the tenant's blockCloudObjectTakeoverThroughHardMatchEnabled is on; the one-time setup turns it off (test/integration/ad-lab/README.md, The hybrid forest)."
        } else { '' }
        throw "Switching $Name's Source of Authority to $(if ($Value) { 'Entra ID' } else { 'Active Directory' }) failed: $($_.Exception.Message)$hint"
    }
}

# Deletes the run's objects (by prefix) that Entra ID itself manages, which Cloud Sync never deletes; returns how many.
# A synchronised object is left for Cloud Sync, which deletes it once its Active Directory object is gone. With
# -OlderThanHours, any run's (jimit-) created before then, left by a run that failed.
function Remove-CloudManagedRunObjects {
    param([Parameter(Mandatory)][string]$Match, [DateTime]$Cutoff = [DateTime]::MaxValue)
    $deleted = 0
    foreach ($kind in 'users', 'groups') {
        $found = @(Get-EntraIdConnectorLabGraphCollection -Token $script:HarnessToken -Path "/$kind`?`$filter=startswith(displayName,'$Match')&`$select=id,displayName,createdDateTime,onPremisesSyncEnabled&`$top=999")
        foreach ($object in $found) {
            if (-not ([string]$object.displayName).StartsWith($Match, [StringComparison]::Ordinal)) { continue }
            $created = Get-EntraIdConnectorLabValue -Object $object -Name 'createdDateTime'
            if ($created -and ([DateTime]$created).ToUniversalTime() -ge $Cutoff) { continue }
            $synchronised = [bool](Get-EntraIdConnectorLabValue -Object $object -Name 'onPremisesSyncEnabled')
            if ($synchronised -and -not (Get-CloudManaged -Kind $kind -Id ([string]$object.id))) { continue }
            try {
                $null = Invoke-EntraIdConnectorLabGraph -Token $script:HarnessToken -Method Delete -Path "/$kind/$($object.id)"
                $deleted++
            }
            catch { if ("$($_.Exception.Message)" -notmatch '\(404\)') { throw } }
        }
    }
    return $deleted
}

# --- JIM, as the operator ----------------------------------------------------------------------------------------------

# The Metaverse Object with this Display Name, in full, or $null.
function Get-ProjectedObject {
    param([Parameter(Mandatory)][string]$DisplayName)
    $found = @(Get-JIMMetaverseObject -AttributeName "Display Name" -AttributeValue $DisplayName -ErrorAction SilentlyContinue)
    if ($found.Count -eq 0) { return $null }
    if ($found.Count -gt 1) { throw "$($found.Count) Metaverse Objects have the Display Name '$DisplayName'." }
    return Get-JIMMetaverseObject -Id $found[0].id
}

function Get-ProjectedValues {
    param([Parameter(Mandatory)][object]$MetaverseObject, [Parameter(Mandatory)][string]$AttributeName)
    $values = @($MetaverseObject.attributeValues | Where-Object { $_.attributeName -eq $AttributeName })
    return @($values | ForEach-Object { if ($null -ne $_.referenceValueId) { [string]$_.referenceValueDisplayName } else { [string]$_.stringValue } })
}

function Get-ProjectionMismatch {
    $mismatches = @()
    foreach ($person in $people) {
        if (-not (Get-ProjectedObject -DisplayName $person.DisplayName)) { $mismatches += "$($person.DisplayName) is not in the Metaverse" }
    }
    foreach ($group in $groups) {
        $projected = Get-ProjectedObject -DisplayName $group.DisplayName
        if (-not $projected) { $mismatches += "$($group.DisplayName) is not in the Metaverse"; continue }
        $expected = @($group.Members | ForEach-Object { $key = $_; ($people | Where-Object Key -eq $key).DisplayName } | Sort-Object)
        $actual = @(Get-ProjectedValues -MetaverseObject $projected -AttributeName 'Static Members' | Sort-Object)
        if (($actual -join '|') -cne ($expected -join '|')) {
            $mismatches += "$($group.DisplayName) has members '$($actual -join ', ')', not '$($expected -join ', ')'"
        }
    }
    return $mismatches
}

function Get-SourceOfAuthority {
    param([Parameter(Mandatory)][string]$EntraId)
    $object = $script:EntraObjects | Where-Object { $_.externalIdValue -eq $EntraId } | Select-Object -First 1
    if (-not $object) { return "(not imported)" }
    $values = @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $entraSystem.id -CsoId $object.id -AttributeName 'sourceOfAuthority' -All)
    return (@($values | ForEach-Object { $_.stringValue }) -join ', ')
}

# What each of the run's objects reads as, against what it should: one line per difference.
function Get-AuthorityMismatch {
    param([Parameter(Mandatory)][hashtable]$Expected)
    $mismatches = @()
    foreach ($name in @($Expected.Keys | Sort-Object)) {
        $actual = Get-SourceOfAuthority -EntraId $entraIds[$name]
        if ($actual -cne $Expected[$name]) { $mismatches += "$name reads as '$actual', not '$($Expected[$name])'" }
    }
    return $mismatches
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
    $script:ImportWarnings = @()
    for ($round = 1; $round -le $MaximumRounds; $round++) {
        $result = Start-JIMRunProfile -ConnectedSystemId $entraSystem.id -RunProfileName "$Kind Import" -Wait -PassThru
        # A Delta Import after a switch reads every object, since Graph's changes do not report a switch, and says why.
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Entra ID $Kind Import ($Label, round $round)" `
            -AllowWarnings -AllowedWarningTypes @('DeltaImportFallbackToFullImport')
        $activity = Get-JIMActivity -Id $result.activityId
        $warning = Get-EntraIdConnectorLabValue -Object $activity -Name 'warningMessage'
        if ($warning) { $script:ImportWarnings += [string]$warning }
        $fallbacks = @(Get-JIMActivity -Id $result.activityId -ExecutionItems | Where-Object { $_.errorType -eq 'DeltaImportFallbackToFullImport' })
        foreach ($fallback in $fallbacks) {
            $detail = Get-JIMActivityExecutionItem -Id $fallback.id
            $script:ImportWarnings += [string](Get-EntraIdConnectorLabValue -Object $detail -Name 'errorMessage')
        }
        $result = Start-JIMRunProfile -ConnectedSystemId $entraSystem.id -RunProfileName $synchronisation -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Entra ID $synchronisation ($Label, round $round)"

        $script:EntraObjects = @(Get-JIMConnectedSystemObject -ConnectedSystemId $entraSystem.id -All)
        $mismatches = @(& $Check)
        if ($mismatches.Count -eq 0) { return $round }
        Write-Host "    After round ${round}: $(($mismatches | Select-Object -First 5) -join '; ')" -ForegroundColor DarkGray
        if ($round -lt $MaximumRounds) { Start-Sleep -Seconds 30 }
    }
    throw "JIM still differs from Entra ID after $MaximumRounds $Kind Import round(s) ($Label): $(($mismatches | Select-Object -First 10) -join '; ')"
}

# --- The run -----------------------------------------------------------------------------------------------------------

Write-TestSection "Scenario 038: hybrid and Source of Authority"
Write-Host "Run prefix:      $prefix" -ForegroundColor Gray
Write-Host "Hybrid forest:   $($hybrid.Domain) ($($hybrid.VmName))" -ForegroundColor Gray
Write-Host "Run's unit:      $runOuDn" -ForegroundColor Gray
Write-Host ""

$script:HarnessToken = $null
$script:EntraObjects = @()
$script:ImportWarnings = @()
$applicationObjectId = $null
$harnessKeyId = $null
$snapshotBefore = $null
$runOuCreated = $false
$cleanupProblems = [System.Collections.Generic.List[string]]::new()
$observations = [System.Collections.Generic.List[string]]::new()

try {
    Write-TestStep "Tenant" "Registering the Connector and signing the harness in"

    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $ciToken = Get-EntraIdLabGraphToken -TenantId $tenantId -ClientId $ciClientId
    $registration = Register-JIMEntraIdConnector -AccessToken $ciToken -Name $script:Scenario038ApplicationName `
        -ConnectedSystemName $script:Scenario038EntraSystemName -TrackSourceOfAuthority -ResetCertificate -Confirm:$false
    if (-not $registration -or -not $registration.ConnectedSystemId) { throw "Register-JIMEntraIdConnector created no Connected System." }
    Write-Host "  OK '$($script:Scenario038ApplicationName)' set up, tracking Source of Authority: Connected System $($registration.ConnectedSystemId)" -ForegroundColor Green

    $application = @((Invoke-EntraIdConnectorLabGraph -Token $ciToken -Path "/applications?`$filter=appId eq '$($registration.ClientId)'&`$select=id").value) | Select-Object -First 1
    if (-not $application) { throw "The CI app cannot see the application $($registration.ClientId); is it still an owner?" }
    $applicationObjectId = $application.id

    $harnessCertificate = New-EntraIdConnectorLabCertificate
    $harnessKeyId = Add-EntraIdConnectorLabCredential -AccessToken $ciToken -ApplicationObjectId $applicationObjectId -Certificate $harnessCertificate
    $script:HarnessToken = Get-EntraIdConnectorLabToken -TenantId $tenantId -ClientId $registration.ClientId -Certificate $harnessCertificate
    Write-Host "  OK The harness signed in as the Connector app with its own certificate" -ForegroundColor Green

    $snapshotBefore = Get-EntraIdConnectorLabSnapshot -Token $script:HarnessToken
    Write-Host "  OK Snapshot of $($snapshotBefore.Count) user(s) and group(s) the run must not change" -ForegroundColor Green

    # --- Active Directory ---
    Write-TestStep "Directory" "Creating the run's users and groups in the hybrid forest"

    Save-ActiveDirectoryCertificate -DirectoryConfig $hybrid | Out-Null

    # The hybrid forest is this scenario's alone, and runs never overlap, so every run unit under the synchronised one
    # is a failed run's: deleting it lets Cloud Sync delete its objects from Entra ID too.
    $leftoverUnits = @(Find-DirectoryEntry -DirectoryConfig $hybrid -BaseDn $hybrid.SyncOuDn -Scope one -Filter '(&(objectClass=organizationalUnit)(ou=jimit-*))' -Attributes 'ou' |
        ForEach-Object { [string]$_['dn'] } | Where-Object { $_ })
    foreach ($unit in $leftoverUnits) {
        Remove-DirectoryOu -DirectoryConfig $hybrid -Dn $unit -Recurse | Out-Null
    }
    $leftoverCloud = Remove-CloudManagedRunObjects -Match $script:EntraIdConnectorLabPrefixRoot -Cutoff ([DateTime]::UtcNow.AddHours(-24))
    if ($leftoverUnits.Count -gt 0 -or $leftoverCloud -gt 0) {
        Write-Host "  Deleted $($leftoverUnits.Count) unit(s) and $leftoverCloud cloud-managed object(s) earlier runs left behind" -ForegroundColor Yellow
    }

    $people = @(New-Scenario038People -Prefix $prefix -RunOuDn $runOuDn)
    $groups = @(New-Scenario038Groups -Prefix $prefix -RunOuDn $runOuDn)

    if ((Invoke-ActiveDirectoryOrganisationalUnitAdd -DirectoryConfig $hybrid -Dn $runOuDn) -ne 'Created') {
        throw "The run's unit $runOuDn already exists; another run is using this prefix."
    }
    $runOuCreated = $true
    foreach ($person in $people) {
        $created = New-DirectoryUser -DirectoryConfig $hybrid -Dn $person.Dn -SamAccountName $person.SamAccountName `
            -Password (Get-ActiveDirectoryFixturePassword -Avoid @($person.GivenName, $person.Surname, $person.SamAccountName)) `
            -Attributes @{ givenName = $person.GivenName; sn = $person.Surname; displayName = $person.DisplayName }
        if ($created.Outcome -ne 'Created') { throw "Could not create $($person.DisplayName) in the hybrid forest: $($created.Outcome). $($created.Output)" }
    }
    foreach ($group in $groups) {
        $created = New-DirectoryGroup -DirectoryConfig $hybrid -Dn $group.Dn -SamAccountName $group.SamAccountName
        if ($created.Outcome -ne 'Created') { throw "Could not create $($group.DisplayName) in the hybrid forest: $($created.Outcome). $($created.Output)" }
        foreach ($key in $group.Members) {
            $member = $people | Where-Object Key -eq $key
            $added = Add-DirectoryGroupMember -DirectoryConfig $hybrid -GroupDn $group.Dn -MemberDn $member.Dn
            if (-not $added.Success) { throw "Could not add $($member.DisplayName) to $($group.DisplayName): $($added.Output)" }
        }
    }
    Write-Host "  OK $($people.Count) users and $($groups.Count) groups created in $runOuDn" -ForegroundColor Green

    # --- Cloud Sync ---
    Write-TestStep "CloudSync" "Waiting for Entra Cloud Sync to copy them into the hybrid tenant"

    $entraIds = @{}
    $started = [DateTime]::UtcNow
    Wait-EntraState -Description "Cloud Sync to copy the run's users and groups" -TimeoutSeconds $syncTimeoutSeconds -IntervalSeconds 30 -Check {
        $listedUsers = Get-ListedRunObjects -Kind users
        $listedGroups = Get-ListedRunObjects -Kind groups
        foreach ($object in @($people) + @($groups)) {
            $listed = if ($object.PSObject.Properties['Members']) { $listedGroups[$object.DisplayName] } else { $listedUsers[$object.DisplayName] }
            if (-not $listed) { "$($object.DisplayName) is not in Entra ID"; continue }
            if (-not [bool](Get-EntraIdConnectorLabValue -Object $listed -Name 'onPremisesSyncEnabled')) { "$($object.DisplayName) is in Entra ID but not synchronised" }
            $entraIds[$object.DisplayName] = [string]$listed.id
        }
        foreach ($group in $groups) {
            if (-not $entraIds.ContainsKey($group.DisplayName)) { continue }
            $members = @(Get-EntraIdConnectorLabGraphCollection -Token $script:HarnessToken -Path "/groups/$($entraIds[$group.DisplayName])/members?`$select=displayName")
            if ($members.Count -ne $group.Members.Count) { "$($group.DisplayName) has $($members.Count) of its $($group.Members.Count) members in Entra ID" }
        }
    }
    $copiedIn = [int]([DateTime]::UtcNow - $started).TotalSeconds
    Write-Host "  OK Cloud Sync copied every user, group and membership in ${copiedIn}s" -ForegroundColor Green

    $exported = $people | Where-Object Key -eq 'p01'
    $switchedUser = $people | Where-Object Key -eq 'p02'
    $switchedGroup = $groups | Where-Object Key -eq 'switched'
    & "$PSScriptRoot/../Setup-Scenario-038.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -ConnectedSystemId $registration.ConnectedSystemId `
        -Prefix $prefix -ExportedDisplayName $exported.DisplayName
    $entraSystem = Get-JIMConnectedSystem -Id $registration.ConnectedSystemId

    $allOnPremises = @{}
    foreach ($object in @($people) + @($groups)) { $allOnPremises[$object.DisplayName] = 'onPremisesDirectory' }

    # --- 1: Synchronised ---
    Write-TestStep "Synchronised" "A Full Import and Full Synchronisation project every object, each reading onPremisesDirectory"

    $rounds = Invoke-EntraImportUntil -Kind Full -Label "projection" -Check {
        Get-ProjectionMismatch
        Get-AuthorityMismatch -Expected $allOnPremises
    }
    $observations.Add("Synchronised: onPremisesSyncEnabled reads $(Get-OnPremisesSyncEnabled -Kind users -Id $entraIds[$switchedUser.DisplayName]) on a user, $(Get-OnPremisesSyncEnabled -Kind groups -Id $entraIds[$switchedGroup.DisplayName]) on a group")

    Add-StepResult -Name "Synchronised" -Passed $true -Detail "$($people.Count) users and $($groups.Count) groups projected in $rounds round(s), all onPremisesDirectory, memberships as in Active Directory"
    Write-Host "  OK Every object projected, reading onPremisesDirectory" -ForegroundColor Green

    # --- 2: ExportRefused ---
    Write-TestStep "ExportRefused" "An export to a person Active Directory owns is refused, and reported"

    $result = Start-JIMRunProfile -ConnectedSystemId $entraSystem.id -RunProfileName "Export" -Wait -PassThru
    $items = @(Get-JIMActivity -Id $result.activityId -ExecutionItems)
    $errored = @($items | Where-Object { $_.errorType -and $_.errorType -ne 'NotSet' })
    Assert-Equal -Actual $errored.Count -Expected 1 -Message "Export items in error (expected the one for $($exported.DisplayName))"
    $item = Get-JIMActivityExecutionItem -Id $errored[0].id
    Assert-Equal -Actual ([string]$item.errorType) -Expected 'RejectedByConnectedSystem' -Message "The refused export's error type"
    $message = [string](Get-EntraIdConnectorLabValue -Object $item -Name 'errorMessage')
    Assert-Condition -Condition ($message -match 'on-premises mastered') -Message "The error carries Entra ID's own words ('on-premises mastered'): $message"
    Assert-Condition -Condition ($message -match 'synchronised from on-premises Active Directory') -Message "The error carries the Connector's explanation: $message"
    $jobTitle = Get-EntraIdConnectorLabValue -Object (Get-GraphObject -Path "/users/$($entraIds[$exported.DisplayName])?`$select=jobTitle") -Name 'jobTitle'
    Assert-Condition -Condition ($jobTitle -ne $script:Scenario038ExportedJobTitle) -Message "Entra ID did not take the job title (it reads '$jobTitle')"

    Add-StepResult -Name "ExportRefused" -Passed $true -Detail "Refused by Entra ID and reported on $($exported.DisplayName) as RejectedByConnectedSystem, in Entra ID's words and the Connector's"
    Write-Host "  OK The export was refused and reported: $message" -ForegroundColor Green

    # --- 3: SwitchToCloud ---
    Write-TestStep "SwitchToCloud" "A user and a group switched to cloud management read as cloud"

    Set-CloudManaged -Kind users -Id $entraIds[$switchedUser.DisplayName] -Value $true -Name $switchedUser.DisplayName
    Set-CloudManaged -Kind groups -Id $entraIds[$switchedGroup.DisplayName] -Value $true -Name $switchedGroup.DisplayName
    Wait-EntraState -Description "Entra ID to report both switched" -Check {
        if (-not (Get-CloudManaged -Kind users -Id $entraIds[$switchedUser.DisplayName])) { "$($switchedUser.DisplayName) is not yet cloud-managed" }
        if (-not (Get-CloudManaged -Kind groups -Id $entraIds[$switchedGroup.DisplayName])) { "$($switchedGroup.DisplayName) is not yet cloud-managed" }
    }
    $observations.Add("Switched to Entra ID: onPremisesSyncEnabled reads $(Get-OnPremisesSyncEnabled -Kind users -Id $entraIds[$switchedUser.DisplayName]) on the user, $(Get-OnPremisesSyncEnabled -Kind groups -Id $entraIds[$switchedGroup.DisplayName]) on the group")

    $cloudExpected = $allOnPremises.Clone()
    $cloudExpected[$switchedUser.DisplayName] = 'cloud'
    $cloudExpected[$switchedGroup.DisplayName] = 'cloud'
    $rounds = Invoke-EntraImportUntil -Kind Delta -Label "switched to Entra ID" -Check { Get-AuthorityMismatch -Expected $cloudExpected }
    $readEverything = @($script:ImportWarnings | Where-Object { $_ -match 'Source of Authority of some' }).Count -gt 0
    Assert-Condition -Condition $readEverything -Message "A Delta Import read every object to see the switch, and said why"

    Add-StepResult -Name "SwitchToCloud" -Passed $true -Detail "The user and the group read as cloud after $rounds Delta Import round(s), which read every object to see the switch; the rest still onPremisesDirectory"
    Write-Host "  OK The switched user and group read as cloud; the rest still onPremisesDirectory" -ForegroundColor Green

    # --- 4: SwitchBack ---
    Write-TestStep "SwitchBack" "Switched back, they read as onPremisesDirectory again"

    Set-CloudManaged -Kind users -Id $entraIds[$switchedUser.DisplayName] -Value $false -Name $switchedUser.DisplayName
    Set-CloudManaged -Kind groups -Id $entraIds[$switchedGroup.DisplayName] -Value $false -Name $switchedGroup.DisplayName
    Wait-EntraState -Description "Entra ID to report both switched back" -Check {
        if (Get-CloudManaged -Kind users -Id $entraIds[$switchedUser.DisplayName]) { "$($switchedUser.DisplayName) is still cloud-managed" }
        if (Get-CloudManaged -Kind groups -Id $entraIds[$switchedGroup.DisplayName]) { "$($switchedGroup.DisplayName) is still cloud-managed" }
    }
    $observations.Add("Switched back: onPremisesSyncEnabled reads $(Get-OnPremisesSyncEnabled -Kind users -Id $entraIds[$switchedUser.DisplayName]) on the user, $(Get-OnPremisesSyncEnabled -Kind groups -Id $entraIds[$switchedGroup.DisplayName]) on the group")

    $rounds = Invoke-EntraImportUntil -Kind Delta -Label "switched back" -Check { Get-AuthorityMismatch -Expected $allOnPremises }

    Add-StepResult -Name "SwitchBack" -Passed $true -Detail "The user and the group read as onPremisesDirectory again after $rounds Delta Import round(s)"
    Write-Host "  OK The user and the group read as onPremisesDirectory again" -ForegroundColor Green

    $testResults.Success = $true
}
catch {
    $testResults.Success = $false
    Write-Failure "Scenario 038 failed: $_"
}
finally {
    Write-TestStep "Cleanup" "Deleting what the run made, comparing the snapshot, removing the harness's certificate"

    if ($runOuCreated) {
        try {
            Remove-DirectoryOu -DirectoryConfig $hybrid -Dn $runOuDn -Recurse | Out-Null
            Write-Host "  Deleted $runOuDn from the hybrid forest" -ForegroundColor Gray
        }
        catch { $cleanupProblems.Add("Deleting $runOuDn failed (the next run deletes it): $($_.Exception.Message)") }
    }

    if ($script:HarnessToken) {
        try {
            $removedCount = Remove-CloudManagedRunObjects -Match $prefix
            if ($removedCount -gt 0) { Write-Host "  Deleted $removedCount object(s) Entra ID still managed itself" -ForegroundColor Gray }
            # Cloud Sync deletes the rest on its next cycle. Not seeing that happen is not the run's failure; the next run
            # finds anything left over, since its Active Directory object is gone.
            try {
                Wait-EntraState -Description "Cloud Sync to delete the run's objects from Entra ID" -TimeoutSeconds $syncTimeoutSeconds -IntervalSeconds 30 -Check {
                    $left = @((Get-ListedRunObjects -Kind users).Keys) + @((Get-ListedRunObjects -Kind groups).Keys)
                    if ($left.Count -gt 0) { "$($left.Count) still listed: $($left -join ', ')" }
                }
                Write-Host "  Cloud Sync deleted the run's objects from Entra ID" -ForegroundColor Gray
            }
            catch { Write-Host "  WARNING $($_.Exception.Message)" -ForegroundColor Yellow }
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

Write-TestSection "Scenario 038 $(if ($testResults.Success) { 'Complete' } else { 'Failed' })"
foreach ($stepResult in $testResults.Steps) {
    Write-Host "  [PASS] $($stepResult.Name): $($stepResult.Detail)" -ForegroundColor Green
}
if ($observations.Count -gt 0) {
    Write-Host ""
    Write-Host "What Entra ID reported for onPremisesSyncEnabled:" -ForegroundColor Cyan
    foreach ($observation in $observations) { Write-Host "  $observation" -ForegroundColor Cyan }
}

return $testResults
