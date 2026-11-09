# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 034: Exchange Recipient Provisioning against a real Exchange SE server (Active Directory lab only)

.DESCRIPTION
    Proves #2021's end-to-end path in the Active Directory lab, against exchange-primary (ex1.panoply.local) in PANOPLY:
    an export Synchronisation Rule declares an account's Exchange recipient state through the connector-defined
    Exchange attributes, the export creates the account, JIM queues an Exchange change, the delivery lane runs the
    Exchange cmdlets over Kerberos-encrypted Remote PowerShell, and the next import brings back what Exchange stamped,
    after which JIM has nothing more to do.

    The lab write suite (LdapExchangeRecipientDeliveryLabTests) already proves the Connector's Exchange step for every
    recipient type and group against the same server. This scenario proves what only the whole of JIM can: staging
    within a real export run (at Medium, among a thousand accounts that declare nothing, across export pages), the
    queue and the lane, the confirming import, follow-ups, and the failure handling an administrator meets.

    Tests, each building on the one before:
      Provision      PRD Scenarios 1 and 2: a mailbox placed by Exchange, one placed in MDB02 (excluded from automatic
                     distribution, so only naming it puts it there), shared, room and equipment mailboxes on disabled
                     accounts, and a mail user with its external address. A mailbox naming a database that does not
                     exist parks at once (Refused).
      NoOp           PRD Scenario 7: a further full cycle stages no Exchange change, which also proves the import derived
                     the Exchange attributes Exchange stamped exactly as the rule declares them.
      Retry          A parked change retried from PowerShell is attempted again (and parks again, its cause unchanged).
      Replace        Correcting the declared database replaces the parked change, and the mailbox is made.
      Update         Alias and external address changes reach Exchange.
      Unreachable    PRD Scenario 5: with IIS stopped on ex1, a new joiner's change retries, naming Exchange as
                     unreachable; with it started again, the lane delivers it without another export.
      Leaver         PRD Scenario 3: a mailbox user's account is deleted with no Exchange step.
      TypeChange     PRD Scenario 8: a mailbox declared a mail user is reported as not made, and Exchange is untouched.

    Only the people this scenario adds to the HR feed are recipients (Employee Type "ExchangeLab"); see
    Setup-Scenario-034.ps1 for the Attribute Flows.

.PARAMETER Step
    All (the tests depend on each other, so they run together).

.PARAMETER Template
    Data scale template for the population around the recipients. Medium is the sign-off template.

.PARAMETER ApiKey
    API key for authentication

.PARAMETER DirectoryConfig
    The Active Directory lab's Primary (Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary)

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario 34 -Template Medium -DirectoryType ActiveDirectory
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Medium",

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
. "$PSScriptRoot/../utils/Invoke-LabControl.ps1"
. "$PSScriptRoot/../utils/ActiveDirectoryLab-Helpers.ps1"

if (-not $ApiKey) { throw "API key required for authentication." }
if (-not $DirectoryConfig -or $DirectoryConfig.DirectoryType -ne 'ActiveDirectory') {
    throw "Scenario 034 runs on the Active Directory lab only (-DirectoryType ActiveDirectory): it needs a real Exchange server."
}

$exchange = Get-ActiveDirectoryLabExchangeConfig -Instance ExchangePrimary
$mailDomain = $DirectoryConfig.Domain.ToLowerInvariant()
$script:TestResults = @()
$startTime = Get-Date

# Exchange's msExchRecipientTypeDetails for each type this scenario declares.
$recipientTypeDetails = @{ Mailbox = '1'; SharedMailbox = '4'; RoomMailbox = '16'; EquipmentMailbox = '32'; MailUser = '128' }

# The people who become recipients. The alias is first and last name, lower case (the Exchange Alias flow), and each
# Email is the address Exchange's default Email Address Policy gives that alias, so Scenario 001's mail flow and
# Exchange agree on it rather than overwriting each other's value.
$people = [ordered]@{
    Ada = @{ EmployeeId = 'EXO000001'; FirstName = 'Ada'; LastName = 'Exmailbox'; Type = 'Mailbox'; Company = 'ExchangeLab' }
    Bea = @{ EmployeeId = 'EXO000002'; FirstName = 'Bea'; LastName = 'Explaced'; Type = 'Mailbox'; Company = 'ExchangeLab Placed' }
    Cy  = @{ EmployeeId = 'EXO000003'; FirstName = 'Cy'; LastName = 'Exshared'; Type = 'SharedMailbox'; Company = 'ExchangeLab' }
    Di  = @{ EmployeeId = 'EXO000004'; FirstName = 'Di'; LastName = 'Exroom'; Type = 'RoomMailbox'; Company = 'ExchangeLab' }
    Ed  = @{ EmployeeId = 'EXO000005'; FirstName = 'Ed'; LastName = 'Exequipment'; Type = 'EquipmentMailbox'; Company = 'ExchangeLab' }
    Flo = @{ EmployeeId = 'EXO000006'; FirstName = 'Flo'; LastName = 'Exmailuser'; Type = 'MailUser'; Company = 'ExchangeLab' }
    Gus = @{ EmployeeId = 'EXO000007'; FirstName = 'Gus'; LastName = 'Exmissing'; Type = 'Mailbox'; Company = 'ExchangeLab Missing' }
    Ivy = @{ EmployeeId = 'EXO000008'; FirstName = 'Ivy'; LastName = 'Exleaver'; Type = 'Mailbox'; Company = 'ExchangeLab' }
}
$latecomer = @{ EmployeeId = 'EXO000009'; FirstName = 'Hal'; LastName = 'Exunreach'; Type = 'Mailbox'; Company = 'ExchangeLab' }
foreach ($person in @($people.Values) + $latecomer) { $person.Sam = "exo.$($person.FirstName.ToLowerInvariant())" }

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

function Get-Alias {
    param([hashtable]$Person)
    return ($Person.FirstName + $Person.LastName).ToLowerInvariant()
}

function Set-HrPerson {
    <#
        Adds a person to the HR CSV, or changes one already there, copying every unspecified column from the first
        row, and seeds the file to the connector volume.
    #>
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
    $row.displayName = "$($Person.FirstName) $($Person.LastName)"
    $row.samAccountName = $Person.Sam
    $row.email = "$alias@$mailDomain"
    $row.userPrincipalName = "$($Person.Sam)@$mailDomain"
    $row.title = $Person.Type
    $row.company = $Person.Company
    $row.employeeType = 'ExchangeLab'
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

function Invoke-ProvisioningCycle {
    <#
        HR in, out to Active Directory, and the confirming import: CSV Full Import, CSV Delta Synchronisation, LDAP
        Export, LDAP Delta Import, LDAP Delta Synchronisation. Returns the export's Activity id, whose follow-ups are the
        Exchange changes it staged.
    #>
    param([Parameter(Mandatory=$true)][string]$Label)

    $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $r.activityId -Name "CSV Full Import ($Label)"
    $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $r.activityId -Name "CSV Delta Synchronisation ($Label)"
    $export = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
    Assert-ExportSuccess -ActivityId $export.activityId -Name "LDAP Export ($Label)"
    Invoke-ConfirmingImport -Label $Label
    return [string]$export.activityId
}

function Invoke-ConfirmingImport {
    # Brings back what the directory (and Exchange, once it has acted) holds: LDAP Delta Import and Delta Synchronisation.
    param([Parameter(Mandatory=$true)][string]$Label)
    $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $r.activityId -Name "LDAP Delta Import ($Label)"
    $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $r.activityId -Name "LDAP Delta Synchronisation ($Label)"
}

function Get-ExchangeQueueSummary {
    return (Get-JIMPendingExchangeChange -ConnectedSystemId $config.LDAPSystemId -Summary)
}

function Wait-ForExchangeQueueToSettle {
    <#
        Waits until no Exchange change is queued, being delivered or retrying: every change has been delivered (and
        left the queue) or is parked, refused or not made. The lane picks a change up within seconds of the export (at
        worst one 30-second safety poll), and each Exchange command takes a few seconds.
    #>
    param([int]$TimeoutSeconds = 600)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $summary = Get-ExchangeQueueSummary
        $moving = [int]$summary.queuedCount + [int]$summary.deliveringCount + [int]$summary.retryingCount
        if ($moving -eq 0) { return $summary }
        if ((Get-Date) -gt $deadline) {
            throw "Exchange changes were still moving after $TimeoutSeconds seconds: $($summary.queuedCount) queued, $($summary.deliveringCount) delivering, $($summary.retryingCount) retrying."
        }
        Start-Sleep -Seconds 5
    }
}

function Get-PersonExchangeChange {
    # The queued Exchange change for a person, found by display name, or nothing once it has been delivered.
    param([Parameter(Mandatory=$true)][hashtable]$Person)
    $name = "$($Person.FirstName) $($Person.LastName)"
    return @(Get-JIMPendingExchangeChange -ConnectedSystemId $config.LDAPSystemId -Search $name -All -Force |
        Where-Object { $_.metaverseObjectDisplayName -eq $name })
}

function Get-ExchangeFollowUpCount {
    # The export run's Exchange follow-ups by state, from the run's own summary: @{ Delivered = 6; Parked = 1 }.
    param([Parameter(Mandatory=$true)][string]$ActivityId)
    $counts = @{}
    foreach ($row in @((Get-JIMActivityStats -Id $ActivityId).followUps | Where-Object { $_.kind -eq 'ExchangeRecipient' })) {
        $counts[[string]$row.state] = [int]$row.count
    }
    return $counts
}

function Format-Count {
    param([hashtable]$Counts)
    if ($Counts.Count -eq 0) { return 'none' }
    return (@($Counts.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Value) $($_.Name)" }) -join ', ')
}

function Get-Recipient {
    # What Active Directory holds for a person: what Exchange stamped (msExchRecipientTypeDetails, homeMDB,
    # mailNickname, targetAddress) and the account's state.
    param([Parameter(Mandatory=$true)][hashtable]$Person)
    return (Get-LDAPUser -UserIdentifier $Person.Sam -DirectoryConfig $DirectoryConfig)
}

function Test-RecipientAttribute {
    param($User, [string]$Name)
    return ($null -ne $User -and $User.ContainsKey($Name))
}

function Get-DatabaseName {
    # homeMDB is the database's DN; its first RDN value is the database's name.
    param($User)
    if (-not (Test-RecipientAttribute -User $User -Name 'homeMDB')) { return $null }
    if ([string]$User.homeMDB -match '^CN=([^,]+),') { return $matches[1] }
    return [string]$User.homeMDB
}

function Test-AccountDisabled {
    param($User)
    return ((Test-RecipientAttribute -User $User -Name 'userAccountControl') -and (([long]$User.userAccountControl -band 2) -ne 0))
}

Write-TestSection "Scenario 034: Exchange Recipient Provisioning"
Write-Host "Directory:  $($DirectoryConfig.ConnectedSystemName) ($($DirectoryConfig.Host))" -ForegroundColor Gray
Write-Host "Exchange:   $($exchange.Host) ($($exchange.VmName))" -ForegroundColor Gray
Write-Host "Template:   $Template" -ForegroundColor Gray
Write-Host ""

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Step 0: Configure JIM
# ─────────────────────────────────────────────────────────────────────────────────────────────
Write-TestSection "Step 0: Configuring JIM"

& "$PSScriptRoot/../Get-OrGenerate-TestCSV.ps1" -Template $Template -OutputPath "$(Get-IntegrationTestDataPath)"
# Setup-Scenario-001.ps1 flows the feed's samAccountName to sAMAccountName as it is, and at Medium some run past
# Active Directory's 20 characters. Set-HrPerson seeds the shortened file with the people it adds.
$shortened = Limit-HrFeedAccountNames -Path "$(Get-IntegrationTestDataPath)/hr-users.csv"
foreach ($person in $people.Values) { Set-HrPerson -Person $person }
if ($shortened -gt 0) { Write-Host "  ✓ $shortened account name(s) shortened to Active Directory's 20 characters" -ForegroundColor Green }
Write-Host "  ✓ HR feed: the $Template population, plus $($people.Count) people who are to be Exchange recipients" -ForegroundColor Green

$config = & "$PSScriptRoot/../Setup-Scenario-034.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template `
    -DirectoryConfig $DirectoryConfig -ExchangeConfig $exchange
if (-not $config) { throw "Failed to set up Scenario 034" }

Import-Module "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1" -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

try {
    # The directory's baseline and the watermark Delta Import needs, as Scenario 001 establishes them.
    $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $r.activityId -Name "LDAP Full Import (baseline)"
    $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $r.activityId -Name "LDAP Full Synchronisation (baseline)"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: Provision (PRD Scenarios 1 and 2)
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test: Provision: accounts become the recipients their rule declares"
    $provisionExport = Invoke-ProvisioningCycle -Label "Provision"
    $summary = Wait-ForExchangeQueueToSettle
    Invoke-ConfirmingImport -Label "Provision, after Exchange"

    foreach ($key in 'Ada', 'Bea', 'Cy', 'Di', 'Ed', 'Flo', 'Ivy') {
        $person = $people[$key]
        $user = Get-Recipient -Person $person
        $details = if (Test-RecipientAttribute -User $user -Name 'msExchRecipientTypeDetails') { [string]$user.msExchRecipientTypeDetails } else { 'none' }
        $nickname = if (Test-RecipientAttribute -User $user -Name 'mailNickname') { [string]$user.mailNickname } else { 'none' }
        Add-TestResult -Name "$key is a $($person.Type) with the alias the rule declares" `
            -Passed ($details -eq $recipientTypeDetails[$person.Type] -and $nickname -eq (Get-Alias -Person $person)) `
            -Detail "msExchRecipientTypeDetails $details (expected $($recipientTypeDetails[$person.Type])), mailNickname $nickname"
    }

    $adaDatabase = Get-DatabaseName -User (Get-Recipient -Person $people.Ada)
    Add-TestResult -Name "With no database declared, Exchange placed Ada's mailbox itself, in the one database open to distribution" `
        -Passed ($adaDatabase -eq $exchange.DistributionDatabase) -Detail "homeMDB $adaDatabase (expected $($exchange.DistributionDatabase))"
    $beaDatabase = Get-DatabaseName -User (Get-Recipient -Person $people.Bea)
    Add-TestResult -Name "Bea's mailbox is in the database her rule names, which Exchange never chooses itself" `
        -Passed ($beaDatabase -eq $exchange.ExcludedDatabase) -Detail "homeMDB $beaDatabase (expected $($exchange.ExcludedDatabase))"

    foreach ($key in 'Cy', 'Di', 'Ed') {
        Add-TestResult -Name "$key's $($people[$key].Type) is on a disabled account" -Passed (Test-AccountDisabled -User (Get-Recipient -Person $people[$key]))
    }

    $flo = Get-Recipient -Person $people.Flo
    $floTarget = if (Test-RecipientAttribute -User $flo -Name 'targetAddress') { [string]$flo.targetAddress } else { 'none' }
    Add-TestResult -Name "Flo is a mail user whose mail goes to the external address the rule declares" `
        -Passed ($floTarget -ieq "SMTP:flo.exmailuser@partner.example") -Detail "targetAddress $floTarget"

    $gusChange = @(Get-PersonExchangeChange -Person $people.Gus)
    $gusDetail = if ($gusChange.Count -gt 0) { "state $($gusChange[0].state), failure $($gusChange[0].failureKind): $($gusChange[0].failureMessage)" } else { 'no change on the queue' }
    Add-TestResult -Name "Gus's mailbox names a database that does not exist, so his change parks at once, naming it" `
        -Passed ($gusChange.Count -eq 1 -and $gusChange[0].state -eq 'Parked' -and $gusChange[0].failureKind -eq 'Refused' -and [string]$gusChange[0].failureMessage -match 'MDB99') `
        -Detail $gusDetail
    $gusAccount = Get-Recipient -Person $people.Gus
    Add-TestResult -Name "Gus's account was created all the same, and is not a recipient" `
        -Passed ($null -ne $gusAccount -and -not (Test-RecipientAttribute -User $gusAccount -Name 'msExchRecipientTypeDetails'))

    $provisionCounts = Get-ExchangeFollowUpCount -ActivityId $provisionExport
    Add-TestResult -Name "The export run's follow-ups count each Exchange change: seven delivered, one parked" `
        -Passed ($provisionCounts['Delivered'] -eq 7 -and $provisionCounts['Parked'] -eq 1 -and $provisionCounts.Count -eq 2) `
        -Detail "Follow-ups: $(Format-Count $provisionCounts)"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: NoOp (PRD Scenario 7)
    # ─────────────────────────────────────────────────────────────────────────────────────────
    # If the import derived the Exchange attributes from what Exchange stamped exactly as the rule declares them, the
    # next export finds nothing to do for any recipient. A derivation that disagreed would stage a change here.
    Write-TestSection "Test: NoOp: recipients already in their declared state stage nothing"
    $noOpExport = Invoke-ProvisioningCycle -Label "NoOp"
    $noOpCounts = Get-ExchangeFollowUpCount -ActivityId $noOpExport
    $noOpSummary = Get-ExchangeQueueSummary
    Add-TestResult -Name "A further cycle stages no Exchange change: what Exchange stamped imported as the rule declares it" `
        -Passed ($noOpCounts.Count -eq 0 -and [int]$noOpSummary.queuedCount -eq 0 -and [int]$noOpSummary.parkedCount -eq 1) `
        -Detail "Follow-ups: $(Format-Count $noOpCounts); queue: $($noOpSummary.queuedCount) queued, $($noOpSummary.parkedCount) parked"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: Retry
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test: Retry: a parked change retried from PowerShell is attempted again"
    $before = @(Get-PersonExchangeChange -Person $people.Gus)[0]
    $resumed = Resume-JIMPendingExchangeChange -Id $before.id -Force
    Wait-ForExchangeQueueToSettle -TimeoutSeconds 300 | Out-Null
    $after = @(Get-PersonExchangeChange -Person $people.Gus)
    $afterDetail = if ($after.Count -gt 0) { "state $($after[0].state), attempts $($after[0].attemptCount), last attempted $($after[0].lastAttemptedAt)" } else { 'gone from the queue' }
    Add-TestResult -Name "Retrying Gus's parked change attempts it again, and it parks again for the same reason" `
        -Passed ([int]$resumed.AffectedCount -eq 1 -and $after.Count -eq 1 -and $after[0].state -eq 'Parked' -and `
                 [datetime]$after[0].lastAttemptedAt -gt [datetime]$before.lastAttemptedAt) `
        -Detail "Retried $($resumed.AffectedCount); $afterDetail"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: Replace
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test: Replace: correcting the declared state replaces a parked change"
    $people.Gus.Company = 'ExchangeLab Placed'
    Set-HrPerson -Person $people.Gus
    Invoke-ProvisioningCycle -Label "Replace" | Out-Null
    Wait-ForExchangeQueueToSettle | Out-Null
    Invoke-ConfirmingImport -Label "Replace, after Exchange"
    $gusDatabase = Get-DatabaseName -User (Get-Recipient -Person $people.Gus)
    Add-TestResult -Name "Gus's corrected database replaced his parked change, and his mailbox is in it" `
        -Passed ($gusDatabase -eq $exchange.ExcludedDatabase -and @(Get-PersonExchangeChange -Person $people.Gus).Count -eq 0) `
        -Detail "homeMDB $gusDatabase (expected $($exchange.ExcludedDatabase))"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: Update
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test: Update: alias and external address changes reach Exchange"
    $people.Bea.LastName = 'Exmoved'
    $people.Flo.LastName = 'Exforward'
    Set-HrPerson -Person $people.Bea
    Set-HrPerson -Person $people.Flo
    Invoke-ProvisioningCycle -Label "Update" | Out-Null
    Wait-ForExchangeQueueToSettle | Out-Null
    Invoke-ConfirmingImport -Label "Update, after Exchange"

    $bea = Get-Recipient -Person $people.Bea
    $beaNickname = if (Test-RecipientAttribute -User $bea -Name 'mailNickname') { [string]$bea.mailNickname } else { 'none' }
    Add-TestResult -Name "Bea's new alias reached Exchange" -Passed ($beaNickname -eq 'beaexmoved') -Detail "mailNickname $beaNickname"
    $flo = Get-Recipient -Person $people.Flo
    $floTarget = if (Test-RecipientAttribute -User $flo -Name 'targetAddress') { [string]$flo.targetAddress } else { 'none' }
    $floNickname = if (Test-RecipientAttribute -User $flo -Name 'mailNickname') { [string]$flo.mailNickname } else { 'none' }
    Add-TestResult -Name "Flo's new external address and alias reached Exchange" `
        -Passed ($floTarget -ieq 'SMTP:flo.exforward@partner.example' -and $floNickname -eq 'floexforward') `
        -Detail "targetAddress $floTarget, mailNickname $floNickname"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: Unreachable (PRD Scenario 5)
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test: Unreachable: a change Exchange could not take is retried, and delivered once it is back"
    $unreachable = $false
    try {
        Set-ActiveDirectoryLabExchangeReachability -VmName $exchange.VmName -State Unreachable | Out-Null
        $unreachable = $true
        Set-HrPerson -Person $latecomer
        Invoke-ProvisioningCycle -Label "Unreachable" | Out-Null

        # The lane attempts it within seconds (at worst one 30-second safety poll) and fails to reach Exchange.
        $deadline = (Get-Date).AddSeconds(180)
        do {
            Start-Sleep -Seconds 5
            $halChange = @(Get-PersonExchangeChange -Person $latecomer)
        } while (($halChange.Count -eq 0 -or $halChange[0].state -ne 'Retrying') -and (Get-Date) -lt $deadline)
        $halDetail = if ($halChange.Count -gt 0) { "state $($halChange[0].state), failure $($halChange[0].failureKind), next attempt $($halChange[0].nextRetryAt): $($halChange[0].failureMessage)" } else { 'no change on the queue' }
        Add-TestResult -Name "Hal's account is created, and his Exchange change is retrying, naming Exchange as unreachable" `
            -Passed ($null -ne (Get-Recipient -Person $latecomer) -and $halChange.Count -eq 1 -and $halChange[0].state -eq 'Retrying' -and $halChange[0].failureKind -eq 'Unreachable') `
            -Detail $halDetail
    }
    finally {
        if ($unreachable) { Set-ActiveDirectoryLabExchangeReachability -VmName $exchange.VmName -State Reachable | Out-Null }
    }

    # Exchange is back. The first retry comes two minutes after the failure; nothing else is run here, so the delivery
    # can only come from the lane itself.
    Wait-ForExchangeQueueToSettle -TimeoutSeconds 420 | Out-Null
    Invoke-ConfirmingImport -Label "Unreachable, after Exchange"
    $hal = Get-Recipient -Person $latecomer
    $halDetails = if (Test-RecipientAttribute -User $hal -Name 'msExchRecipientTypeDetails') { [string]$hal.msExchRecipientTypeDetails } else { 'none' }
    Add-TestResult -Name "Once Exchange is back, the lane makes Hal's mailbox with no further export" `
        -Passed ($halDetails -eq $recipientTypeDetails.Mailbox -and @(Get-PersonExchangeChange -Person $latecomer).Count -eq 0) `
        -Detail "msExchRecipientTypeDetails $halDetails"

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: Leaver (PRD Scenario 3)
    # ─────────────────────────────────────────────────────────────────────────────────────────
    Write-TestSection "Test: Leaver: a mailbox user's account is deleted with no Exchange step"
    $userType = Get-JIMMetaverseObjectType -Name "User"
    if (-not $userType) { throw "Could not find the 'User' Metaverse Object Type" }
    try {
        # Immediate deletion for this test only; the scenario's 7-day grace period would merely schedule it.
        Set-JIMMetaverseObjectType -Id $userType.id -DeletionGracePeriod ([TimeSpan]::Zero) | Out-Null
        Remove-HrPerson -Person $people.Ivy
        $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "CSV Full Import (Leaver)"
        $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "CSV Delta Synchronisation (Leaver)"

        # Housekeeping deletes the Metaverse Object on its next 60-second tick, staging the account's deletion.
        $deadline = (Get-Date).AddSeconds(150)
        while ([int](Get-JIMPendingDeletion -Count) -gt 0) {
            if ((Get-Date) -gt $deadline) { throw "Housekeeping did not delete Ivy's Metaverse Object within 150 seconds." }
            Start-Sleep -Seconds 5
        }
        $leaverExport = Invoke-ProvisioningCycle -Label "Leaver"
        $leaverCounts = Get-ExchangeFollowUpCount -ActivityId $leaverExport
        Add-TestResult -Name "Ivy's account is deleted, and no Exchange step is run for it" `
            -Passed ($null -eq (Get-Recipient -Person $people.Ivy) -and $leaverCounts.Count -eq 0 -and @(Get-PersonExchangeChange -Person $people.Ivy).Count -eq 0) `
            -Detail "Follow-ups: $(Format-Count $leaverCounts)"
    }
    finally {
        # Setup-Scenario-001.ps1's grace period, as Scenario 001 restores it.
        Set-JIMMetaverseObjectType -Id $userType.id -DeletionGracePeriod ([TimeSpan]::FromDays(7)) | Out-Null
    }

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test: TypeChange (PRD Scenario 8)
    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Last, because Ada's declared type then disagrees with Exchange for good, which every later export would meet.
    Write-TestSection "Test: TypeChange: a change of recipient type is reported, and Exchange is untouched"
    $people.Ada.Type = 'MailUser'
    Set-HrPerson -Person $people.Ada
    $typeChangeExport = Invoke-ProvisioningCycle -Label "TypeChange"
    Wait-ForExchangeQueueToSettle | Out-Null
    $typeChangeCounts = Get-ExchangeFollowUpCount -ActivityId $typeChangeExport
    $ada = Get-Recipient -Person $people.Ada
    $adaDetails = if (Test-RecipientAttribute -User $ada -Name 'msExchRecipientTypeDetails') { [string]$ada.msExchRecipientTypeDetails } else { 'none' }
    Add-TestResult -Name "Ada's change from mailbox to mail user is reported as not made, and she is still a mailbox" `
        -Passed ($typeChangeCounts['NotMade'] -eq 1 -and $typeChangeCounts.Count -eq 1 -and $adaDetails -eq $recipientTypeDetails.Mailbox) `
        -Detail "Follow-ups: $(Format-Count $typeChangeCounts); msExchRecipientTypeDetails $adaDetails"

    # The Worker logs every Exchange command it runs; an error there means a delivery that failed behind a green run.
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

Write-TestSection "Scenario 034 Summary"
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
Write-Host "✓ JIM made every recipient its rule declared through a real Exchange server, and handled each failure as designed." -ForegroundColor Green
exit 0
