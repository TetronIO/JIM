# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 003: GALSYNC (Global Address List synchronisation between PANOPLY and RESURGAM, Active Directory lab only)

.DESCRIPTION
    Proves GAL synchronisation (#2021, PRD Scenario 4, requirements 21 to 24) between two Exchange organisations in the
    Active Directory lab: PANOPLY (dc-primary, exchange-primary) and RESURGAM (dc-source, exchange-source). Each
    forest's mailboxes, and PANOPLY's mail-enabled group, appear in the other forest's address book as mail contacts in
    OU=GALSync,OU=Corp, made by JIM's LDAP export and mail-enabled through that forest's Exchange.

    Tests, each building on the one before:
      Provision       Carol and Leo get mailboxes in PANOPLY, Rita one in RESURGAM, and PANOPLY's gal-test-group is
                      made a mail-enabled group. Once JIM has imported their addresses, each appears in the other forest
                      as a mail contact whose mail goes to the source address and which carries an X500: proxy of the
                      source's Legacy Exchange DN, so replies to old messages still reach them.
      NoProjection    No GAL contact is projected back as a person or group, while each forest's own contact (outside
                      GALSync) is projected exactly once, so the scoping that keeps them apart is proven to select.
      NoOp            A further cycle exports nothing in either forest and stages no Exchange change: what Exchange
                      stamped on the contacts imports as the rules declare it.
      Update          Carol's change of name reaches her contact in RESURGAM.
      Leaver          Leo's account is deleted in PANOPLY, and his contact in RESURGAM with it.

    The -Template value is not used: GAL synchronisation is about the recipients the scenario adds, and every one of
    them costs Exchange commands in two forests. Setup runs at Nano.

.PARAMETER Step
    All (the tests depend on each other, so they run together).

.PARAMETER ApiKey
    API key for authentication

.PARAMETER DirectoryConfig
    The Active Directory lab's Primary (Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary)

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario 3 -DirectoryType ActiveDirectory
#>

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
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [switch]$ContinueOnError,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"
. "$PSScriptRoot/../utils/Directory-Helpers.ps1"

if (-not $ApiKey) { throw "API key required for authentication." }
if (-not $DirectoryConfig -or $DirectoryConfig.DirectoryType -ne 'ActiveDirectory') {
    throw "Scenario 003 runs on the Active Directory lab only (-DirectoryType ActiveDirectory): it needs two Exchange organisations."
}

$panoply = $DirectoryConfig
$resurgam = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Source
$panoplyExchange = Get-ActiveDirectoryLabExchangeConfig -Instance ExchangePrimary
$resurgamExchange = Get-ActiveDirectoryLabExchangeConfig -Instance ExchangeSource
$script:TestResults = @()
$startTime = Get-Date

# Exchange's msExchRecipientDisplayType for a mail contact. Exchange stamps no msExchRecipientTypeDetails on a contact
# it mail-enables (Scenario 003 run 42780578451 read display type 6 and no details), which is why JIM reads the type
# from the display type.
$mailContactDisplayType = '6'

$people = [ordered]@{
    Carol = @{ EmployeeId = 'GAL000001'; FirstName = 'Carol'; LastName = 'Panoplygal'; Forest = 'PANOPLY'; Domain = $panoply.Domain }
    Leo   = @{ EmployeeId = 'GAL000002'; FirstName = 'Leo'; LastName = 'Panoplygal'; Forest = 'PANOPLY'; Domain = $panoply.Domain }
    Rita  = @{ EmployeeId = 'GAL000003'; FirstName = 'Rita'; LastName = 'Resurgamgal'; Forest = 'RESURGAM'; Domain = $resurgam.Domain }
}
foreach ($person in $people.Values) { $person.Sam = "gal.$($person.FirstName.ToLowerInvariant())" }
$testGroup = @{ Sam = 'gal-test-group'; Cn = 'GAL Test Group'; Dn = "CN=GAL Test Group,$($panoply.UserContainer)" }
$nativeContacts = @{
    PANOPLY  = @{ Name = 'Partner Panoply'; Dn = "CN=Partner Panoply,$($panoply.UserContainer)"; Config = $panoply }
    RESURGAM = @{ Name = 'Partner Resurgam'; Dn = "CN=Partner Resurgam,OU=Corp,$($resurgam.BaseDN)"; Config = $resurgam }
}

function Add-TestResult {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][bool]$Passed,
        [Parameter(Mandatory=$false)][string]$Detail = ""
    )
    $script:TestResults += @{ Name = $Name; Passed = $Passed; Detail = $Detail }
    if ($Passed) {
        Write-Host "  ✓ PASSED: $Name" -ForegroundColor Green
        if ($Detail) { Write-Host "      $Detail" -ForegroundColor DarkGray }
    }
    else {
        Write-Host "  ✗ FAILED: $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "      $Detail" -ForegroundColor Yellow }
        if (-not $ContinueOnError) { throw "Assertion failed: $Name. $Detail" }
    }
}

function Get-DisplayName {
    param([hashtable]$Person)
    return "$($Person.FirstName) $($Person.LastName)"
}

function Get-Alias {
    param([hashtable]$Person)
    return ($Person.FirstName + $Person.LastName).ToLowerInvariant()
}

function Set-HrPerson {
    # Adds or changes a person in the HR CSV (every other column copied from the first row) and seeds it. PANOPLY's
    # people are Scenario 034's "ExchangeLab" mailboxes; RESURGAM's are "ResurgamLab", which only RESURGAM provisions.
    param([Parameter(Mandatory=$true)][hashtable]$Person)
    $csvPath = "$(Get-IntegrationTestDataPath)/hr-users.csv"
    $csv = @(Import-Csv $csvPath)
    $row = $csv | Where-Object { $_.employeeId -eq $Person.EmployeeId } | Select-Object -First 1
    if (-not $row) {
        $row = $csv[0].PSObject.Copy()
        $csv += $row
    }
    $alias = Get-Alias -Person $Person
    $row.employeeId = $Person.EmployeeId
    $row.firstName = $Person.FirstName
    $row.lastName = $Person.LastName
    $row.displayName = Get-DisplayName -Person $Person
    $row.samAccountName = $Person.Sam
    # The address each forest's default Email Address Policy gives the alias, so JIM's mail flow and Exchange agree.
    $row.email = "$alias@$($Person.Domain.ToLowerInvariant())"
    $row.userPrincipalName = "$($Person.Sam)@$($Person.Domain.ToLowerInvariant())"
    $row.title = 'Mailbox'
    $row.company = if ($Person.Forest -eq 'PANOPLY') { 'ExchangeLab' } else { 'Resurgam' }
    $row.employeeType = if ($Person.Forest -eq 'PANOPLY') { 'ExchangeLab' } else { 'ResurgamLab' }
    $row.status = 'Active'
    $row.employeeEndDate = ''
    $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    Copy-CsvToConnectorFiles -SourcePath $csvPath
}

function Remove-HrPerson {
    param([Parameter(Mandatory=$true)][hashtable]$Person)
    $csvPath = "$(Get-IntegrationTestDataPath)/hr-users.csv"
    @(Import-Csv $csvPath) | Where-Object { $_.employeeId -ne $Person.EmployeeId } | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    Copy-CsvToConnectorFiles -SourcePath $csvPath
}

function Start-Profile {
    param([int]$SystemId, [int]$ProfileId, [string]$Name, [switch]$Export)
    $r = Start-JIMRunProfile -ConnectedSystemId $SystemId -RunProfileId $ProfileId -Wait -PassThru
    if ($Export) { Assert-ExportSuccess -ActivityId $r.activityId -Name $Name } else { Assert-ActivitySuccess -ActivityId $r.activityId -Name $Name }
    return [string]$r.activityId
}

function Wait-ForExchangeQueuesToSettle {
    # Both forests' Exchange changes delivered (gone from the queue) or stopped (parked, refused, not made). The queue
    # summary counts every Connected System's changes, which here are exactly the two forests'.
    param([int]$TimeoutSeconds = 900)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $summary = Get-JIMPendingExchangeChange -Summary
        $moving = [int]$summary.queuedCount + [int]$summary.deliveringCount + [int]$summary.retryingCount
        if ($moving -eq 0) { return }
        if ((Get-Date) -gt $deadline) { throw "$moving Exchange change(s) were still moving after $TimeoutSeconds seconds." }
        Start-Sleep -Seconds 5
    }
}

function Write-RecipientState {
    # What the directory holds for the test group and for Rita's contact in PANOPLY once Exchange has acted, before the
    # import reads them: what JIM derives each one's Exchange Recipient Type from, printed so a confirming import that
    # disagrees can be explained.
    param([string]$Label)
    $attributes = @('msExchRecipientTypeDetails', 'msExchRecipientDisplayType', 'mailNickname', 'mail', 'targetAddress')
    $subjects = [ordered]@{
        $testGroup.Sam = { Find-DirectoryEntry -DirectoryConfig $panoply -Filter "(&(objectClass=group)(sAMAccountName=$($testGroup.Sam)))" -Attributes $attributes }
        "Rita's contact" = { Find-DirectoryEntry -DirectoryConfig $panoply -BaseDn $panoplyExchange.ContactOuDn `
            -Filter "(&(objectClass=contact)(displayName=$(ConvertTo-LdapFilterValue -Value (Get-DisplayName $people.Rita))))" -Attributes $attributes }
    }
    foreach ($subject in $subjects.GetEnumerator()) {
        $entry = @(& $subject.Value) | Select-Object -First 1
        if (-not $entry) { Write-Host "  [diagnostic] $($subject.Key) ($Label): not in PANOPLY" -ForegroundColor DarkGray; continue }
        $read = foreach ($name in $attributes) { $v = @($entry[$name]); "$name $(if ($v.Count -eq 0) { '(none)' } else { [string]$v[0] })" }
        Write-Host "  [diagnostic] $($subject.Key) ($Label): $($read -join ', ')" -ForegroundColor DarkGray
    }
}

function Invoke-GalCycle {
    <#
        One round of GAL synchronisation: HR in; both forests exported (accounts, groups, contacts) and their Exchange
        changes delivered; both forests imported and synchronised, which brings back what Exchange stamped and stages
        whatever that makes due. Returns the two exports' Activity ids.
    #>
    param([Parameter(Mandatory=$true)][string]$Label)
    Start-Profile -SystemId $config.CSVSystemId -ProfileId $config.CSVImportProfileId -Name "CSV Full Import ($Label)" | Out-Null
    Start-Profile -SystemId $config.CSVSystemId -ProfileId $config.CSVDeltaSyncProfileId -Name "CSV Delta Synchronisation ($Label)" | Out-Null
    $exports = @{
        PANOPLY  = Start-Profile -SystemId $config.LDAPSystemId -ProfileId $config.LDAPExportProfileId -Name "PANOPLY Export ($Label)" -Export
        RESURGAM = Start-Profile -SystemId $config.SourceLDAPSystemId -ProfileId $config.SourceLDAPExportProfileId -Name "RESURGAM Export ($Label)" -Export
    }
    Wait-ForExchangeQueuesToSettle
    Write-RecipientState -Label $Label
    Start-Profile -SystemId $config.LDAPSystemId -ProfileId $config.LDAPDeltaImportProfileId -Name "PANOPLY Delta Import ($Label)" | Out-Null
    Start-Profile -SystemId $config.SourceLDAPSystemId -ProfileId $config.SourceLDAPDeltaImportProfileId -Name "RESURGAM Delta Import ($Label)" | Out-Null
    Start-Profile -SystemId $config.LDAPSystemId -ProfileId $config.LDAPDeltaSyncProfileId -Name "PANOPLY Delta Synchronisation ($Label)" | Out-Null
    Start-Profile -SystemId $config.SourceLDAPSystemId -ProfileId $config.SourceLDAPDeltaSyncProfileId -Name "RESURGAM Delta Synchronisation ($Label)" | Out-Null
    return $exports
}

function Get-ExportedCount {
    param([string]$ActivityId)
    $activity = Get-JIMActivity -Id $ActivityId
    if ($activity.PSObject.Properties['executionStats'] -and $activity.executionStats) { return [int]$activity.executionStats.totalExported }
    return 0
}

function Get-ExchangeFollowUpTotal {
    param([string]$ActivityId)
    # Summed by hand: Measure-Object returns nothing for no input, and under StrictMode .Sum then throws, which is
    # exactly the NoOp case this is asked about (no follow-ups at all).
    $total = 0
    foreach ($row in @((Get-JIMActivityStats -Id $ActivityId).followUps | Where-Object { $_.kind -eq 'ExchangeRecipient' })) {
        $total += [int]$row.count
    }
    return $total
}

function Find-GalContact {
    # The contact named $Name in a forest's GALSync OU, or nothing.
    param([hashtable]$Forest, [string]$ContactOuDn, [string]$Name)
    return @(Find-DirectoryEntry -DirectoryConfig $Forest -BaseDn $ContactOuDn -Filter "(&(objectClass=contact)(displayName=$(ConvertTo-LdapFilterValue -Value $Name)))" `
            -Attributes @('displayName', 'targetAddress', 'proxyAddresses', 'msExchRecipientDisplayType')) | Select-Object -First 1
}

function Get-LegacyExchangeDn {
    # What Exchange stamped on the source recipient.
    param([hashtable]$Forest, [string]$Filter)
    $entry = @(Find-DirectoryEntry -DirectoryConfig $Forest -Filter $Filter -Attributes @('legacyExchangeDN', 'mail')) | Select-Object -First 1
    if (-not $entry) { return $null }
    return @{ LegacyExchangeDn = [string](@($entry['legacyExchangeDN']) | Select-Object -First 1); Mail = [string](@($entry['mail']) | Select-Object -First 1) }
}

function Test-GalContact {
    # A mail contact whose mail goes to the source's address and which carries the source's X500: proxy.
    param([string]$Label, $Contact, $Source)
    if (-not $Contact) { Add-TestResult -Name $Label -Passed $false -Detail "No such contact."; return }
    $displayType = [string](@($Contact['msExchRecipientDisplayType']) | Select-Object -First 1)
    $target = [string](@($Contact['targetAddress']) | Select-Object -First 1)
    $proxies = @($Contact['proxyAddresses'])
    $x500 = "X500:$($Source.LegacyExchangeDn)"
    Add-TestResult -Name $Label `
        -Passed ($displayType -eq $mailContactDisplayType -and $target -ieq "SMTP:$($Source.Mail)" -and ($proxies -ccontains $x500) -and -not [string]::IsNullOrEmpty($Source.LegacyExchangeDn)) `
        -Detail "msExchRecipientDisplayType $displayType (expected $mailContactDisplayType), targetAddress $target (expected SMTP:$($Source.Mail)), X500 proxy $(if ($proxies -ccontains $x500) { 'present' } else { "missing (expected $x500; has $($proxies -join ', '))" })"
}

Write-TestSection "Scenario 003: GAL synchronisation between PANOPLY and RESURGAM"
Write-Host "PANOPLY:   $($panoply.Host), Exchange $($panoplyExchange.Host)" -ForegroundColor Gray
Write-Host "RESURGAM:  $($resurgam.Host), Exchange $($resurgamExchange.Host)" -ForegroundColor Gray
Write-Host "Template:  Nano (the -Template value, $Template, is not used for sizing)" -ForegroundColor Gray
Write-Host ""

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Step 0: The forests' own objects, and JIM
# ─────────────────────────────────────────────────────────────────────────────────────────────
Write-TestSection "Step 0: Configuring JIM"

# The group JIM mail-enables in PANOPLY (Exchange mail-enables universal groups only), and a contact of each forest's
# own outside GALSync, which its contact import rule must project.
$groupResult = New-DirectoryGroup -DirectoryConfig $panoply -Dn $testGroup.Dn -SamAccountName $testGroup.Sam -Scope Universal -Description "GAL synchronisation test group (Scenario 003)"
if ($groupResult.Outcome -eq 'Failed') { throw "Could not create $($testGroup.Dn): $($groupResult.Output)" }
foreach ($native in $nativeContacts.Values) {
    $ldif = "dn: $($native.Dn)`nobjectClass: contact`ncn: $($native.Name)`ndisplayName: $($native.Name)`n"
    $result = Invoke-DirectoryLdif -DirectoryConfig $native.Config -Ldif $ldif -Operation add
    if ($result.Outcome -eq 'Failed') { throw "Could not create $($native.Dn): $($result.Output)" }
}
Write-Host "  ✓ $($testGroup.Sam) (universal) in PANOPLY; a partner contact of each forest's own" -ForegroundColor Green

& "$PSScriptRoot/../Get-OrGenerate-TestCSV.ps1" -Template Nano -OutputPath "$(Get-IntegrationTestDataPath)"
foreach ($person in $people.Values) { Set-HrPerson -Person $person }

$config = & "$PSScriptRoot/../Setup-Scenario-003.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -DirectoryConfig $panoply -SourceConfig $resurgam `
    -PrimaryExchangeConfig $panoplyExchange -SourceExchangeConfig $resurgamExchange
if (-not $config) { throw "Failed to set up Scenario 003" }

Import-Module "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1" -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

try {
    # Both directories' baselines (and the watermarks Delta Import needs).
    Start-Profile -SystemId $config.LDAPSystemId -ProfileId $config.LDAPFullImportProfileId -Name "PANOPLY Full Import (baseline)" | Out-Null
    Start-Profile -SystemId $config.SourceLDAPSystemId -ProfileId $config.SourceLDAPFullImportProfileId -Name "RESURGAM Full Import (baseline)" | Out-Null
    Start-Profile -SystemId $config.LDAPSystemId -ProfileId $config.LDAPFullSyncProfileId -Name "PANOPLY Full Synchronisation (baseline)" | Out-Null
    Start-Profile -SystemId $config.SourceLDAPSystemId -ProfileId $config.SourceLDAPFullSyncProfileId -Name "RESURGAM Full Synchronisation (baseline)" | Out-Null

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: Provision
    # ─────────────────────────────────────────────────────────────────────────────────────────
    # The first cycle makes the mailboxes and the group, and imports their addresses, which puts them in the contact
    # rules' scope; the second makes the contacts and mail-enables them.
    Write-TestSection "Test: Provision: each forest's recipients appear in the other's address book"
    Invoke-GalCycle -Label "Recipients" | Out-Null
    Invoke-GalCycle -Label "Contacts" | Out-Null

    foreach ($key in 'Carol', 'Leo') {
        $person = $people[$key]
        $source = Get-LegacyExchangeDn -Forest $panoply -Filter "(&(objectClass=user)(sAMAccountName=$($person.Sam)))"
        Test-GalContact -Label "$key, a PANOPLY mailbox, is a mail contact in RESURGAM with her or his address and X500 proxy" `
            -Contact (Find-GalContact -Forest $resurgam -ContactOuDn $resurgamExchange.ContactOuDn -Name (Get-DisplayName $person)) -Source $source
    }
    $ritaSource = Get-LegacyExchangeDn -Forest $resurgam -Filter "(&(objectClass=user)(sAMAccountName=$($people.Rita.Sam)))"
    Test-GalContact -Label "Rita, a RESURGAM mailbox, is a mail contact in PANOPLY with her address and X500 proxy" `
        -Contact (Find-GalContact -Forest $panoply -ContactOuDn $panoplyExchange.ContactOuDn -Name (Get-DisplayName $people.Rita)) -Source $ritaSource
    $groupSource = Get-LegacyExchangeDn -Forest $panoply -Filter "(&(objectClass=group)(sAMAccountName=$($testGroup.Sam)))"
    Test-GalContact -Label "PANOPLY's mail-enabled group is a mail contact in RESURGAM with its address and X500 proxy" `
        -Contact (Find-GalContact -Forest $resurgam -ContactOuDn $resurgamExchange.ContactOuDn -Name $testGroup.Cn) -Source $groupSource

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: NoProjection (requirement 23, PRD decision 19)
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test: NoProjection: no GAL contact is projected back, while each forest's own contact is"
    foreach ($key in $people.Keys) {
        $count = [int](Get-JIMMetaverseObject -ObjectTypeName 'User' -AttributeName 'Display Name' -AttributeValue (Get-DisplayName $people[$key]) -Count)
        Add-TestResult -Name "$key is one person in the Metaverse, not one more for each contact" -Passed ($count -eq 1) -Detail "$count Metaverse Object(s)"
    }
    $groupCount = [int](Get-JIMMetaverseObject -ObjectTypeName 'Group' -AttributeName 'Display Name' -AttributeValue $testGroup.Cn -Count)
    $groupAsUser = [int](Get-JIMMetaverseObject -ObjectTypeName 'User' -AttributeName 'Display Name' -AttributeValue $testGroup.Cn -Count)
    Add-TestResult -Name "The group is one group, and its contact in RESURGAM is not projected as a person" `
        -Passed ($groupCount -eq 1 -and $groupAsUser -eq 0) -Detail "$groupCount group(s), $groupAsUser person/people"
    foreach ($native in $nativeContacts.GetEnumerator()) {
        $count = [int](Get-JIMMetaverseObject -ObjectTypeName 'User' -AttributeName 'Display Name' -AttributeValue $native.Value.Name -Count)
        Add-TestResult -Name "$($native.Key)'s own contact, outside GALSync, is projected exactly once" -Passed ($count -eq 1) -Detail "$count Metaverse Object(s)"
    }

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: NoOp
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test: NoOp: a further cycle exports nothing and stages no Exchange change"
    $noOp = Invoke-GalCycle -Label "NoOp"
    $exported = @{ PANOPLY = Get-ExportedCount -ActivityId $noOp.PANOPLY; RESURGAM = Get-ExportedCount -ActivityId $noOp.RESURGAM }
    $followUps = (Get-ExchangeFollowUpTotal -ActivityId $noOp.PANOPLY) + (Get-ExchangeFollowUpTotal -ActivityId $noOp.RESURGAM)
    Add-TestResult -Name "Neither forest exports anything, and no Exchange change is staged: the contacts hold what the rules declare" `
        -Passed ($exported.PANOPLY -eq 0 -and $exported.RESURGAM -eq 0 -and $followUps -eq 0) `
        -Detail "Exported: PANOPLY $($exported.PANOPLY), RESURGAM $($exported.RESURGAM); Exchange follow-ups $followUps"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: Update
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test: Update: a change of name reaches the contact"
    $oldName = Get-DisplayName $people.Carol
    $people.Carol.LastName = 'Panoplymoved'
    Set-HrPerson -Person $people.Carol
    Invoke-GalCycle -Label "Update" | Out-Null
    $renamed = Find-GalContact -Forest $resurgam -ContactOuDn $resurgamExchange.ContactOuDn -Name (Get-DisplayName $people.Carol)
    $stale = Find-GalContact -Forest $resurgam -ContactOuDn $resurgamExchange.ContactOuDn -Name $oldName
    Add-TestResult -Name "Carol's contact in RESURGAM carries her new name, and none is left under the old one" `
        -Passed ($null -ne $renamed -and $null -eq $stale) -Detail "New name: $(if ($renamed) { 'found' } else { 'missing' }); old name: $(if ($stale) { 'still there' } else { 'gone' })"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: Leaver
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test: Leaver: a leaver's contact leaves the other forest's address book"
    $userType = Get-JIMMetaverseObjectType -Name "User"
    try {
        Set-JIMMetaverseObjectType -Id $userType.id -DeletionGracePeriod ([TimeSpan]::Zero) | Out-Null
        Remove-HrPerson -Person $people.Leo
        Start-Profile -SystemId $config.CSVSystemId -ProfileId $config.CSVImportProfileId -Name "CSV Full Import (Leaver)" | Out-Null
        Start-Profile -SystemId $config.CSVSystemId -ProfileId $config.CSVDeltaSyncProfileId -Name "CSV Delta Synchronisation (Leaver)" | Out-Null
        $deadline = (Get-Date).AddSeconds(150)
        while ([int](Get-JIMPendingDeletion -Count) -gt 0) {
            if ((Get-Date) -gt $deadline) { throw "Housekeeping did not delete Leo's Metaverse Object within 150 seconds." }
            Start-Sleep -Seconds 5
        }
        Invoke-GalCycle -Label "Leaver" | Out-Null
        $leoAccount = @(Find-DirectoryEntry -DirectoryConfig $panoply -Filter "(&(objectClass=user)(sAMAccountName=$($people.Leo.Sam)))" -Attributes @('sAMAccountName'))
        $leoContact = Find-GalContact -Forest $resurgam -ContactOuDn $resurgamExchange.ContactOuDn -Name (Get-DisplayName $people.Leo)
        Add-TestResult -Name "Leo's account in PANOPLY is deleted, and his contact in RESURGAM with it" `
            -Passed ($leoAccount.Count -eq 0 -and $null -eq $leoContact) `
            -Detail "Account: $(if ($leoAccount.Count) { 'still there' } else { 'deleted' }); contact: $(if ($leoContact) { 'still there' } else { 'deleted' })"
    }
    finally {
        # Setup-Scenario-001.ps1's grace period, as Scenario 001 restores it.
        Set-JIMMetaverseObjectType -Id $userType.id -DeletionGracePeriod ([TimeSpan]::FromDays(7)) | Out-Null
    }

    Assert-NoWorkerErrors -Since $startTime
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────────────────────
$duration = (Get-Date) - $startTime
$passed = @($script:TestResults | Where-Object { $_.Passed }).Count
$failed = @($script:TestResults | Where-Object { -not $_.Passed }).Count

Write-TestSection "Scenario 003 Summary"
Write-Host "Duration: $([math]::Round($duration.TotalSeconds, 1))s" -ForegroundColor Gray
Write-Host "Passed:   $passed" -ForegroundColor Green
if ($failed -gt 0) {
    Write-Host "Failed:   $failed" -ForegroundColor Red
    foreach ($result in $script:TestResults | Where-Object { -not $_.Passed }) {
        Write-Host "  - $($result.Name)" -ForegroundColor Red
    }
    exit 1
}

Write-Host ""
Write-Host "✓ Each forest's recipients are in the other's address book, with their X500 proxies, and nothing came back as a person." -ForegroundColor Green
exit 0
