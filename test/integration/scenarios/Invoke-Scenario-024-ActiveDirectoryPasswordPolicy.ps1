# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 024: Active Directory Password Policy (discovery, enforcement, generated passwords satisfy it)

.DESCRIPTION
    Proves that JIM reads the password policy a real Active Directory domain controller enforces, and that the
    Initial Passwords it generates from that policy are ones the domain controller accepts (PRD functional
    requirement 17). It is Scenario 022 for Active Directory: the domain has complexity on, a minimum length of 12
    and a history of 24, a Fine-Grained Password Policy applies a minimum length of 16 to one group, and a
    generated password that only a domain controller can find unacceptable is refused and recovered from.

    The chain, in order, and why each link is needed:

      Policy      1. The domain policy is set to complexity on, minimum length 12, history 24 and no minimum age.
                     The minimum age is set because Windows' default of one day would make an account holder's
                     change refuse a compliant password too, and the negative control below would then prove
                     nothing about length.
                  2. Negative control: an ordinary account holder changes their own password to 5 characters and is
                     refused (constraint violation, Active Directory's 0000052D); the same account then changes it to
                     a compliant one and is accepted. Without this, "nothing parked" further down proves nothing:
                     the domain controller might not be enforcing at all. This is not subject to -ContinueOnError.
                  3. A Fine-Grained Password Policy (a msDS-PasswordSettings object in the Password Settings
                     Container) with a minimum length of 16 applies to a group holding one account. That account
                     is refused a 12 character password the domain policy would accept and takes a longer one, so
                     the object is known to be in force, not merely present.

      Discovery   4. JIM is configured as Scenario 022 configures it (bound as svc-jim, Initial Password source
                     Discovered), and what it discovered matches the fixture: 12, 24, complexity on and three of five
                     character classes, the ages the domain reports, outcome Read. About the Fine-Grained Password
                     Policy JIM reports one field, policyOverrideSignal. Read as svc-jim, which has no right over the
                     Password Settings Container (Domain Admins only, unless delegated: docs/connectors/
                     jim-ldap-connector.md), the search comes back empty, which the connector reports as
                     CouldNotDetermine and never as Absent, and that is asserted. Read by a second Connected System
                     bound as the domain administrator, who can see the container, the same discovery reports
                     Present; that is asserted too, and is the connector's Fine-Grained Password Policy detection
                     proven on a real domain controller. PRD requirement 17 says "reports the FGPP as present"; that is
                     only true for an account that can see the container.

      Provision   5. Scenario 001's import, synchronisation and export at Micro provision ten accounts, each given
                     a generated Initial Password through the password channel.
                  6. Every provisioned account is enabled and carries a non-zero pwdLastSet (Active Directory will
                     not enable an account that does not hold a password it accepted, and stamps pwdLastSet when
                     it takes one), and JIM reports nothing parked and nothing expired.

      Override    7. The export rule is switched to a static Initial Password that JIM's own save-time check accepts
                     (long enough, four character classes) and that a domain controller refuses: it contains a
                     part of the new account's display name, which Active Directory's complexity rule forbids and
                     which no length or category check can see. One more user is provisioned, the account is created
                     but left disabled with no password, and it is PARKED with the directory's own words (a policy
                     rejection carrying 0000052D). The rule is then corrected to a compliant static password and the
                     account is released: enabled, with a password, nothing parked.

    Steps are cumulative: a named step runs everything before it. The domain policy changes are not undone here;
    every run starts from a checkpoint revert, which undoes them (the values replaced are printed).

    Active Directory lab only. Scenario 022 asserts the same chain against an OpenLDAP ppolicy overlay.

    Microsoft Learn, ms-DS-Password-Settings class, lists the attributes a Password Settings Object must contain
    or its creation fails: https://learn.microsoft.com/windows/win32/adschema/c-msds-passwordsettings

.PARAMETER Step
    Which part to execute. Steps are cumulative: a named step runs everything up to and including itself
    (Policy, Discovery, Provision, Override, All).

.PARAMETER Template
    Accepted for runner compatibility and deliberately not used for sizing. This scenario asserts against the
    accounts of one Micro export; a larger template would only lengthen it.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER ContinueOnError
    Continue executing remaining tests even if a test fails. The negative control is exempt: if the domain
    controller does not enforce, nothing after it can prove anything, so it stops the scenario regardless.

.PARAMETER SkipPopulate
    Accepted for runner compatibility. The scenario creates the fixture it needs on the lab's baseline.

.PARAMETER DirectoryConfig
    Directory configuration hashtable from Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary.
    The domain administrator's credentials are used for the scenario's own reads and writes of the directory; JIM
    binds as svc-jim.

.EXAMPLE
    ./Invoke-Scenario-024-ActiveDirectoryPasswordPolicy.ps1 -ApiKey "jim_..."
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Template, WaitSeconds and SkipPopulate are accepted for the runner, which passes the same parameters to every scenario.')]
param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Policy", "Discovery", "Provision", "Override", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Micro",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

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

# Helpers. Directory-Helpers reads and writes the domain policy, creates the accounts and the group and reads the
# directory back as its administrator; LDAP-Helpers supplies the unicodePwd change an account holder makes.
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"
. "$PSScriptRoot/../utils/Directory-Helpers.ps1"
. "$PSScriptRoot/../utils/Invoke-LabControl.ps1"
. "$PSScriptRoot/../utils/ActiveDirectoryLab-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary
}

if (-not $ApiKey) {
    throw "API key required for authentication. Create one via the JIM portal: Admin > API Keys."
}

if ($DirectoryConfig.DirectoryType -ne "ActiveDirectory") {
    throw "Scenario 024 requires the Active Directory lab. It asserts a Windows domain controller's domain password " +
          "policy and a Fine-Grained Password Policy, and $($DirectoryConfig.ConnectedSystemName) is a " +
          "'$($DirectoryConfig.DirectoryType)' directory with neither. Run-IntegrationTests.ps1 should have rejected " +
          "this combination before this script was invoked."
}

# The fixture. Where each of these is created is a container the lab's baseline already holds (see
# test/integration/ad-lab/README.md): TestUsers for the two accounts the scenario changes passwords on, and
# Groups under Corp for the group the Fine-Grained Password Policy applies to.
$baseDn = $DirectoryConfig.BaseDN
$probeSam = "s24probe"
$probeDn = "CN=$probeSam,OU=TestUsers,$baseDn"
$fgppMemberSam = "s24fgpp"
$fgppMemberDn = "CN=$fgppMemberSam,OU=TestUsers,$baseDn"
$fgppGroupDn = "CN=PSO-Scenario024-Members,OU=Groups,OU=Corp,$baseDn"
$fgppDn = "CN=PSO-Scenario024-Strict,CN=Password Settings Container,CN=System,$baseDn"

# What the domain policy is set to, and therefore what JIM must report; and what the Fine-Grained Password Policy
# demands of its member (longer than the domain, so that a password the domain accepts is one it refuses).
$expectedMinimumLength = 12
$expectedHistoryLength = 24
$expectedCharacterClassCount = 3
$fgppMinimumLength = 16

# A value the policy must refuse, from an account holder changing their own password: five characters, against a
# minimum of twelve. Cut from a generated value like every other password here, so none is written into the script.
$shortPassword = (Get-ActiveDirectoryFixturePassword -Length 12).Substring(0, 5)

# One Micro export is what this scenario asserts against, so it always provisions at Micro no matter what the
# runner passed. A larger template would lengthen the export and prove nothing further.
$effectiveTemplate = "Micro"

$script:TestResults = @()
$startTime = Get-Date

function Add-TestResult {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][bool]$Passed,
        [Parameter(Mandatory=$false)][string]$Detail = ""
    )
    $script:TestResults += @{ Name = $Name; Passed = $Passed; Detail = $Detail }
    if ($Passed) {
        Write-Host "  OK PASSED: $Name" -ForegroundColor Green
    }
    else {
        Write-Host "  FAILED: $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "      $Detail" -ForegroundColor Yellow }
        if (-not $ContinueOnError) {
            throw "Assertion failed: $Name. $Detail"
        }
    }
}

function Get-PolicyValue {
    <#
    .SYNOPSIS
        Reads a property off the discovered policy response, naming its absence rather than throwing.

    .DESCRIPTION
        Set-StrictMode turns a missing property into a terminating error, which would hide which assertion
        failed, and the API leaves out a property whose value is null. A null reads as '<null>' and a property the
        response does not carry at all as '<absent>'.
    #>
    param(
        [Parameter(Mandatory=$true)]$Policy,
        [Parameter(Mandatory=$true)][string]$Name
    )
    $property = $Policy.PSObject.Properties[$Name]
    if ($null -eq $property) { return '<absent>' }
    if ($null -eq $property.Value) { return '<null>' }
    return $property.Value
}

function Test-PolicyValue {
    <#
    .SYNOPSIS
        Whether the discovered policy carries the expected value; an expected null is met by a null or an absent property.

    .DESCRIPTION
        JIM reports a figure it could not read, or that the directory does not set (a minimum age of zero), as
        null, and whether the API writes that as a property with no value or leaves the property out is not
        something an assertion about the directory should depend on.
    #>
    param(
        [Parameter(Mandatory=$true)]$Policy,
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)]$Expected
    )

    $actual = Get-PolicyValue -Policy $Policy -Name $Name
    if ($Expected -eq '<null>') { return ($actual -eq '<null>' -or $actual -eq '<absent>') }
    return ($actual -eq $Expected)
}

function Get-PropertyText {
    <#
    .SYNOPSIS
        A property of an API object as text, or an empty string when the API left it out.

    .DESCRIPTION
        The API omits a property whose value is null, and Set-StrictMode turns reading a missing property into a
        terminating error, which would hide the assertion that was about to say what was wrong.
    #>
    param(
        [Parameter(Mandatory=$true)]$Object,
        [Parameter(Mandatory=$true)][string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return "" }
    return "$($property.Value)"
}

function Get-ParkedReasonText {
    param([Parameter(Mandatory=$true)]$RuleConfiguration)

    $reasons = $RuleConfiguration.PSObject.Properties['parkedReasons']
    if ($null -eq $reasons -or $null -eq $reasons.Value) { return "none" }
    return (@($reasons.Value | ForEach-Object {
        "$(Get-PropertyText -Object $_ -Name 'accountCount') x [$(Get-PropertyText -Object $_ -Name 'failureReason')] $(Get-PropertyText -Object $_ -Name 'targetMessage')"
    }) -join ' | ')
}

function ConvertTo-SecurePasswordValue {
    # A SecureString for a cmdlet parameter that takes one, built without ConvertTo-SecureString -AsPlainText.
    param([Parameter(Mandatory=$true)][string]$Text)
    return [System.Net.NetworkCredential]::new('', $Text).SecurePassword
}

function Invoke-AccountHolderChange {
    <#
    .SYNOPSIS
        An account holder changes their own password: bound as the account, one modify that deletes the old value
        and adds the new one. What Windows does when a person changes their password.
    #>
    param(
        [Parameter(Mandatory=$true)][string]$AccountDn,
        [Parameter(Mandatory=$true)][string]$CurrentText,
        [Parameter(Mandatory=$true)][string]$ReplacementText
    )

    return Invoke-LdapUnicodePwdModify -DirectoryConfig $DirectoryConfig `
        -BindDN $AccountDn -BindPassword $CurrentText -TargetDN $AccountDn -NewPassword $ReplacementText -OldPassword $CurrentText
}

function Add-FixtureAccount {
    <#
    .SYNOPSIS
        Creates an enabled account with the given password as the domain administrator, or throws.
    #>
    param(
        [Parameter(Mandatory=$true)][string]$Dn,
        [Parameter(Mandatory=$true)][string]$SamAccountName,
        [Parameter(Mandatory=$true)][string]$Text
    )

    $created = New-DirectoryUser -DirectoryConfig $DirectoryConfig -Dn $Dn -SamAccountName $SamAccountName -Password $Text
    if ($created.Outcome -ne "Created") {
        throw "Could not create $Dn on $($DirectoryConfig.VmName): $($created.Outcome). An 'AlreadyExists' means the domain " +
              "controller was not reverted to its baseline checkpoint before this run. Output: $($created.Output)"
    }
}

function Get-AccountState {
    <#
    .SYNOPSIS
        An account's enabled state and password stamp, read from the directory as its administrator.
    #>
    param([Parameter(Mandatory=$true)][string]$SamAccountName)

    $entry = @(Find-DirectoryEntry -DirectoryConfig $DirectoryConfig -Filter "(sAMAccountName=$SamAccountName)" `
        -BaseDn $DirectoryConfig.UserContainer -Attributes @("userAccountControl", "pwdLastSet")) | Select-Object -First 1
    if ($null -eq $entry) { return $null }

    return @{
        Enabled            = (([int]$entry["userAccountControl"][0] -band 2) -eq 0)
        PasswordWasStamped = ($entry.ContainsKey("pwdLastSet") -and "$($entry["pwdLastSet"][0])" -ne "0")
    }
}

function Wait-ParkedAndPendingChange {
    <#
    .SYNOPSIS
        Polls the password queue until the parked count reaches the expected value and nothing is left Pending.
    #>
    param(
        [Parameter(Mandatory=$true)][int]$ConnectedSystemId,
        [Parameter(Mandatory=$true)][int]$ExpectedParked,
        [Parameter(Mandatory=$false)][int]$TimeoutSeconds = 60
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $parked = @(Get-JIMPendingPasswordChange -ConnectedSystemId $ConnectedSystemId -Status Parked)
        $pending = @(Get-JIMPendingPasswordChange -ConnectedSystemId $ConnectedSystemId -Status Pending)
        if ($parked.Count -eq $ExpectedParked -and $pending.Count -eq 0) { break }
        if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { break }
        Start-Sleep -Milliseconds 500
    }
    return @{ Parked = $parked; Pending = $pending; Seconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1) }
}

Write-TestSection "Scenario 024: Active Directory Password Policy"
Write-Host "Directory:    $($DirectoryConfig.ConnectedSystemName) ($($DirectoryConfig.VmName))" -ForegroundColor Gray
Write-Host "JIM binds as: $($DirectoryConfig.JimBindDN) (delegated, not a domain administrator)" -ForegroundColor Gray
Write-Host "Template:     $effectiveTemplate (the -Template value is not used for sizing)" -ForegroundColor Gray
Write-Host "Step:         $Step (steps are cumulative)" -ForegroundColor Gray
Write-Host ""

# Cumulative dispatch: each step builds on the previous one's state.
$stepOrder = @("Policy", "Discovery", "Provision", "Override")
$lastStepIndex = if ($Step -eq "All") { $stepOrder.Count - 1 } else { $stepOrder.IndexOf($Step) }

# ---------------------------------------------------------------------------------------------------------------
# Policy: the fixture, and the proof that it is enforced
# ---------------------------------------------------------------------------------------------------------------
Write-TestSection "Policy: the domain policy, the negative control and the Fine-Grained Password Policy"

$domainBefore = Get-DirectoryPasswordPolicy -DirectoryConfig $DirectoryConfig
$complexityBefore = if ($domainBefore.Complexity) { "on" } else { "off" }
Write-Host "  Domain policy before: complexity $complexityBefore, minimum length $($domainBefore.MinLength), history $($domainBefore.HistoryLength), minimum age $($domainBefore.MinAgeDays) day(s), maximum age $($domainBefore.MaxAgeDays) day(s). The checkpoint revert at the start of the next run restores it." -ForegroundColor Gray

$policySet = Set-DirectoryPasswordPolicy -DirectoryConfig $DirectoryConfig -Complexity on -MinLength $expectedMinimumLength `
    -HistoryLength $expectedHistoryLength -MinAgeDays 0
if (-not $policySet.Success) {
    throw "Could not set the domain password policy on $($DirectoryConfig.VmName). Output: $($policySet.Output)"
}

$domain = Get-DirectoryPasswordPolicy -DirectoryConfig $DirectoryConfig
Add-TestResult -Name "The domain policy is complexity on, minimum length $expectedMinimumLength, history $expectedHistoryLength, no minimum age" `
    -Passed ($domain.Complexity -and $domain.MinLength -eq $expectedMinimumLength -and $domain.HistoryLength -eq $expectedHistoryLength -and $domain.MinAgeDays -eq 0) `
    -Detail "Read back: complexity $($domain.Complexity), minimum length $($domain.MinLength), history $($domain.HistoryLength), minimum age $($domain.MinAgeDays) day(s)."

# Test 1: the negative control that gives every later "nothing parked" its meaning
Write-TestSection "Test 1: The domain refuses a 5-character password from an ordinary account holder"

$probePassword = Get-ActiveDirectoryFixturePassword -Avoid @($probeSam)
Add-FixtureAccount -Dn $probeDn -SamAccountName $probeSam -Text $probePassword

$shortChange = Invoke-AccountHolderChange -AccountDn $probeDn -CurrentText $probePassword -ReplacementText $shortPassword
$enforced = ($shortChange.Outcome -eq 'ConstraintViolation')
Add-TestResult -Name "A 5-character password is refused with a constraint violation (policy enforced)" `
    -Passed $enforced `
    -Detail "Expected ConstraintViolation (Active Directory's 0000052D), got '$($shortChange.Outcome)' (exit code $($shortChange.ExitCode)). Directory said: $($shortChange.Output)"

if (-not $enforced) {
    # Deliberately not subject to -ContinueOnError: every assertion from here on reads "the domain accepted it",
    # which means nothing if the domain accepts everything.
    throw "ENFORCEMENT NOT PROVEN: $($DirectoryConfig.VmName) did not refuse a 5-character password from $probeDn " +
          "(outcome '$($shortChange.Outcome)': $($shortChange.Output)). Nothing this scenario asserts afterwards can " +
          "distinguish a satisfied policy from an absent one, so it stops here. Any other refusal (access, bind) is a " +
          "fixture fault and stops it just the same."
}

# Test 2: the channel itself works, so a later acceptance is real
Write-TestSection "Test 2: A compliant password is accepted from the same account over the same channel"

$compliantPassword = Get-ActiveDirectoryFixturePassword -Avoid @($probeSam)
$compliantChange = Invoke-AccountHolderChange -AccountDn $probeDn -CurrentText $probePassword -ReplacementText $compliantPassword
Add-TestResult -Name "A compliant password is accepted when an account holder changes their own password over LDAPS" `
    -Passed ($compliantChange.Outcome -eq 'Success') `
    -Detail "Expected Success, got '$($compliantChange.Outcome)' (exit code $($compliantChange.ExitCode)). Directory said: $($compliantChange.Output)"

# Test 3: a Fine-Grained Password Policy on a group with one member
Write-TestSection "Test 3: A Fine-Grained Password Policy applies a minimum length of $fgppMinimumLength to one account"

# The object's mandatory attributes are those of the msDS-PasswordSettings class (Microsoft Learn,
# https://learn.microsoft.com/windows/win32/adschema/c-msds-passwordsettings, "Windows Server 2012 Attributes",
# Mandatory True): msDS-PasswordSettingsPrecedence, msDS-PasswordReversibleEncryptionEnabled,
# msDS-PasswordHistoryLength, msDS-PasswordComplexityEnabled, msDS-MinimumPasswordLength, msDS-MinimumPasswordAge,
# msDS-MaximumPasswordAge, msDS-LockoutThreshold, msDS-LockoutObservationWindow and msDS-LockoutDuration. That is
# five age and lockout attributes plus the reversible encryption flag, and its creation fails without any of them.
# Ages are Interval syntax: the negative of a count of 100 nanosecond intervals (42 days is 36288000000000, 30
# minutes is 18000000000). It applies to the group named by msDS-PSOAppliesTo, which must be a global security
# group; New-DirectoryGroup makes one.
$groupResult = New-DirectoryGroup -DirectoryConfig $DirectoryConfig -Dn $fgppGroupDn `
    -Description "Scenario 024: accounts held to the Fine-Grained Password Policy"
if ($groupResult.Outcome -eq "Failed") {
    throw "Could not create the group $fgppGroupDn. Output: $($groupResult.Output)"
}

$fgppMemberPassword = Get-ActiveDirectoryFixturePassword -Avoid @($fgppMemberSam)
Add-FixtureAccount -Dn $fgppMemberDn -SamAccountName $fgppMemberSam -Text $fgppMemberPassword

$memberResult = Add-DirectoryGroupMember -DirectoryConfig $DirectoryConfig -GroupDn $fgppGroupDn -MemberDn $fgppMemberDn
if (-not $memberResult.Success) {
    throw "Could not add $fgppMemberDn to $fgppGroupDn. Output: $($memberResult.Output)"
}

$fgppLdif = @"
dn: $fgppDn
objectClass: msDS-PasswordSettings
cn: PSO-Scenario024-Strict
msDS-PasswordSettingsPrecedence: 10
msDS-PasswordReversibleEncryptionEnabled: FALSE
msDS-PasswordHistoryLength: $expectedHistoryLength
msDS-PasswordComplexityEnabled: TRUE
msDS-MinimumPasswordLength: $fgppMinimumLength
msDS-MinimumPasswordAge: 0
msDS-MaximumPasswordAge: -36288000000000
msDS-LockoutThreshold: 0
msDS-LockoutObservationWindow: -18000000000
msDS-LockoutDuration: -18000000000
msDS-PSOAppliesTo: $fgppGroupDn
"@
$fgppResult = Invoke-DirectoryLdif -DirectoryConfig $DirectoryConfig -Ldif $fgppLdif -Operation add
if ($fgppResult.Outcome -eq "Failed") {
    throw "Could not create the Fine-Grained Password Policy $fgppDn. Output: $($fgppResult.Output)"
}
Write-Host "  OK Fine-Grained Password Policy $fgppDn ($($fgppResult.Outcome)), applied to $fgppGroupDn with one member" -ForegroundColor Green

$member = Get-DirectoryEntry -DirectoryConfig $DirectoryConfig -Dn $fgppMemberDn -Attributes "msDS-ResultantPSO"
$resultantPso = if ($null -ne $member -and $member.ContainsKey("msDS-ResultantPSO")) { "$($member["msDS-ResultantPSO"][0])" } else { "" }
Add-TestResult -Name "The Fine-Grained Password Policy is the resultant policy of its group's member" `
    -Passed ($resultantPso -eq $fgppDn) `
    -Detail "msDS-ResultantPSO of $fgppMemberDn was '$resultantPso', expected '$fgppDn'. The object exists but is not applying."

$twelveCharacterPassword = Get-ActiveDirectoryFixturePassword -Length $expectedMinimumLength -Avoid @($fgppMemberSam)
$memberShortChange = Invoke-AccountHolderChange -AccountDn $fgppMemberDn -CurrentText $fgppMemberPassword -ReplacementText $twelveCharacterPassword
Add-TestResult -Name "The Fine-Grained Password Policy refuses a $expectedMinimumLength-character password the domain policy would accept" `
    -Passed ($memberShortChange.Outcome -eq 'ConstraintViolation') `
    -Detail "Expected ConstraintViolation, got '$($memberShortChange.Outcome)'. Directory said: $($memberShortChange.Output)"

$longPassword = Get-ActiveDirectoryFixturePassword -Length ($fgppMinimumLength + 2) -Avoid @($fgppMemberSam)
$memberLongChange = Invoke-AccountHolderChange -AccountDn $fgppMemberDn -CurrentText $fgppMemberPassword -ReplacementText $longPassword
Add-TestResult -Name "The Fine-Grained Password Policy accepts a $($fgppMinimumLength + 2)-character password" `
    -Passed ($memberLongChange.Outcome -eq 'Success') `
    -Detail "Expected Success, got '$($memberLongChange.Outcome)'. Directory said: $($memberLongChange.Output)"

if ($lastStepIndex -ge $stepOrder.IndexOf("Discovery")) {
    # -------------------------------------------------------------------------------------------------------------
    # Discovery: configure JIM, then read what it discovered
    # -------------------------------------------------------------------------------------------------------------
    Write-TestSection "Discovery: Configuring JIM"

    Write-Host "Resetting CSV test data to baseline..." -ForegroundColor Gray
    & "$PSScriptRoot/../Get-OrGenerate-TestCSV.ps1" -Template $effectiveTemplate -OutputPath "$PSScriptRoot/../../test-data"
    Write-Host "  OK CSV test data reset to baseline" -ForegroundColor Green

    $config = & "$PSScriptRoot/../Setup-Scenario-024.ps1" `
        -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $effectiveTemplate -DirectoryConfig $DirectoryConfig
    if (-not $config) {
        throw "Failed to set up Scenario 024 configuration"
    }
    Write-Host "  OK JIM configured; the Export Synchronisation Rule sets a Discovered-policy Initial Password" -ForegroundColor Green

    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    try {
        Write-TestSection "Test 4: The discovered policy matches the fixture"

        # Setup-Scenario-001's schema import is what triggers discovery, and it ran as svc-jim.
        $policy = Get-JIMConnectedSystemPasswordPolicy -Id $config.LDAPSystemId

        # The domain's ages, as the domain reports them: a maximum age of 0 days means passwords never expire and
        # the connector reports neither age as a figure in that case (and no minimum age as none).
        $expectedMaximumAge = if ($domain.MaxAgeDays -gt 0) { $domain.MaxAgeDays } else { '<null>' }
        $expectedMinimumAge = if ($domain.MinAgeDays -gt 0) { $domain.MinAgeDays } else { '<null>' }

        Add-TestResult -Name "JIM discovered at least one constraint" `
            -Passed ((Get-PolicyValue -Policy $policy -Name 'hasAnyDiscoveredConstraint') -eq $true) `
            -Detail "hasAnyDiscoveredConstraint was '$(Get-PolicyValue -Policy $policy -Name 'hasAnyDiscoveredConstraint')'. Nothing read at all means the reader did not recognise the directory or could not read the domain root as $($DirectoryConfig.JimBindDN)."

        Add-TestResult -Name "discoveryOutcome is Read" `
            -Passed ((Get-PolicyValue -Policy $policy -Name 'discoveryOutcome') -eq 'Read') `
            -Detail "discoveryOutcome was '$(Get-PolicyValue -Policy $policy -Name 'discoveryOutcome')'"

        Add-TestResult -Name "minimumLength is $expectedMinimumLength (minPwdLength)" `
            -Passed ((Get-PolicyValue -Policy $policy -Name 'minimumLength') -eq $expectedMinimumLength) `
            -Detail "minimumLength was '$(Get-PolicyValue -Policy $policy -Name 'minimumLength')'"

        Add-TestResult -Name "passwordHistoryLength is $expectedHistoryLength (pwdHistoryLength)" `
            -Passed ((Get-PolicyValue -Policy $policy -Name 'passwordHistoryLength') -eq $expectedHistoryLength) `
            -Detail "passwordHistoryLength was '$(Get-PolicyValue -Policy $policy -Name 'passwordHistoryLength')'"

        Add-TestResult -Name "complexityRequired is true (pwdProperties bit 0)" `
            -Passed ((Get-PolicyValue -Policy $policy -Name 'complexityRequired') -eq $true) `
            -Detail "complexityRequired was '$(Get-PolicyValue -Policy $policy -Name 'complexityRequired')'"

        Add-TestResult -Name "requiredCharacterClassCount is $expectedCharacterClassCount (Active Directory's three of five)" `
            -Passed ((Get-PolicyValue -Policy $policy -Name 'requiredCharacterClassCount') -eq $expectedCharacterClassCount) `
            -Detail "requiredCharacterClassCount was '$(Get-PolicyValue -Policy $policy -Name 'requiredCharacterClassCount')'"

        $recognisedClasses = @(Get-PolicyValue -Policy $policy -Name 'recognisedCharacterClasses')
        $missingClasses = @("Uppercase", "Lowercase", "Digit", "Symbol", "OtherUnicodeLetter" | Where-Object { $_ -notin $recognisedClasses })
        Add-TestResult -Name "recognisedCharacterClasses names all five categories Active Directory counts" `
            -Passed ($missingClasses.Count -eq 0) `
            -Detail "recognisedCharacterClasses was '$($recognisedClasses -join ', ')'; missing: $($missingClasses -join ', ')"

        Add-TestResult -Name "maximumPasswordAgeDays is $expectedMaximumAge (maxPwdAge as the domain reports it)" `
            -Passed (Test-PolicyValue -Policy $policy -Name 'maximumPasswordAgeDays' -Expected $expectedMaximumAge) `
            -Detail "maximumPasswordAgeDays was '$(Get-PolicyValue -Policy $policy -Name 'maximumPasswordAgeDays')'"

        Add-TestResult -Name "minimumPasswordAgeDays is $expectedMinimumAge (minPwdAge, none set by this scenario)" `
            -Passed (Test-PolicyValue -Policy $policy -Name 'minimumPasswordAgeDays' -Expected $expectedMinimumAge) `
            -Detail "minimumPasswordAgeDays was '$(Get-PolicyValue -Policy $policy -Name 'minimumPasswordAgeDays')'"

        # About the Fine-Grained Password Policy, the connector sets ONE field: policyOverrideSignal
        # (LdapConnectorPasswordPolicyActiveDirectory.DetectOverridingPoliciesAsync). svc-jim is delegated over
        # OU=Corp, OU=TestUsers, OU=TestGroups and Deleted Objects only, so its search of the Password Settings
        # Container (Domain Admins only by default) succeeds and returns nothing, which cannot be told apart from
        # a domain with no such policies. The connector's answer to that is CouldNotDetermine, never Absent.
        Add-TestResult -Name "policyOverrideSignal is CouldNotDetermine for svc-jim, which cannot see the Password Settings Container" `
            -Passed ((Get-PolicyValue -Policy $policy -Name 'policyOverrideSignal') -eq 'CouldNotDetermine') `
            -Detail "policyOverrideSignal was '$(Get-PolicyValue -Policy $policy -Name 'policyOverrideSignal')'. Present would mean svc-jim can read the container after all (docs/connectors/jim-ldap-connector.md says it cannot by default); Absent would be the connector calling an empty, unreadable result 'none', which it must never do."

        # The same discovery as an account that CAN see the container, to prove the detection itself: a second
        # Connected System bound as the domain administrator.
        Write-TestSection "Test 4b: The Fine-Grained Password Policy is reported as present to an account that can see it"

        $adminSystemName = "$($DirectoryConfig.ConnectedSystemName) (Administrator)"
        $ldapConnector = @(Get-JIMConnectorDefinition) | Where-Object { $_.name -eq "JIM LDAP Connector" } | Select-Object -First 1
        $adminSystem = New-JIMConnectedSystem -Name $adminSystemName `
            -Description "Scenario 024: the same domain controller, read as the domain administrator" `
            -ConnectorDefinitionId $ldapConnector.id -PassThru
        try {
            $definition = Get-JIMConnectorDefinition -Id $ldapConnector.id
            $settingByName = @{}
            foreach ($setting in $definition.settings) { $settingByName[$setting.name] = $setting }

            $adminSettings = @{}
            $adminSettings[$settingByName["Host"].id] = @{ stringValue = $DirectoryConfig.Host }
            $adminSettings[$settingByName["Port"].id] = @{ intValue = $DirectoryConfig.Port }
            $adminSettings[$settingByName["Username"].id] = @{ stringValue = $DirectoryConfig.BindDN }
            $adminSettings[$settingByName["Password"].id] = @{ stringValue = $DirectoryConfig.BindPassword }
            $adminSettings[$settingByName["Use Secure Connection (LDAPS)?"].id] = @{ checkboxValue = $DirectoryConfig.UseSSL }
            $adminSettings[$settingByName["Connection Timeout"].id] = @{ intValue = 30 }
            $adminSettings[$settingByName["Authentication Type"].id] = @{ stringValue = $DirectoryConfig.AuthType }
            Set-JIMConnectedSystem -Id $adminSystem.id -SettingValues $adminSettings | Out-Null

            Import-JIMConnectedSystemSchema -Id $adminSystem.id -Confirm:$false | Out-Null
            $adminPolicy = Get-JIMConnectedSystemPasswordPolicy -Id $adminSystem.id

            Add-TestResult -Name "policyOverrideSignal is Present when JIM reads the domain as the administrator" `
                -Passed ((Get-PolicyValue -Policy $adminPolicy -Name 'policyOverrideSignal') -eq 'Present') `
                -Detail "policyOverrideSignal was '$(Get-PolicyValue -Policy $adminPolicy -Name 'policyOverrideSignal')' with the Fine-Grained Password Policy $fgppDn in the Password Settings Container."

            Add-TestResult -Name "The administrator's discovery reads the same domain figures (minimumLength $expectedMinimumLength)" `
                -Passed ((Get-PolicyValue -Policy $adminPolicy -Name 'minimumLength') -eq $expectedMinimumLength) `
                -Detail "minimumLength was '$(Get-PolicyValue -Policy $adminPolicy -Name 'minimumLength')'"
        }
        finally {
            Remove-JIMConnectedSystem -Id $adminSystem.id -DeleteImmediately -Force | Out-Null
        }

        if ($lastStepIndex -ge $stepOrder.IndexOf("Provision")) {
            # ---------------------------------------------------------------------------------------------------------
            # Provision: accounts, each with a generated Initial Password
            # ---------------------------------------------------------------------------------------------------------
            Write-TestSection "Test 5: Provisioning accounts into $($DirectoryConfig.ConnectedSystemName) with generated Initial Passwords"

            Write-Host "  [1/5] HR CSV Full Import..." -ForegroundColor DarkGray
            $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Full Import"

            Write-Host "  [2/5] HR CSV Delta Sync..." -ForegroundColor DarkGray
            $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Delta Sync"

            # The Create exports land here, and each one stages a Pending Initial Password that the delivery pass then
            # sets through the Connector's password channel: unicodePwd over LDAPS as svc-jim, into the domain's policy.
            Write-Host "  [3/5] Directory Export (accounts created, Initial Passwords set)..." -ForegroundColor DarkGray
            $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Export"

            # A Full Import, not a Delta Import: this is the Connected System's first import, so there is no persisted
            # baseline for a delta to compare against (Scenario 017's reasoning).
            Write-Host "  [4/5] Directory Full Import (confirms the exports)..." -ForegroundColor DarkGray
            $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Full Import"

            Write-Host "  [5/5] Directory Delta Sync..." -ForegroundColor DarkGray
            $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Delta Sync"

            Write-Host "  OK Provisioning complete" -ForegroundColor Green

            Write-TestSection "Test 6: Every provisioned account holds a policy-checked password and nothing is parked"

            # A password reaches an account only through the domain policy, so what the directory itself says about
            # each account is the evidence: Active Directory will not enable an account that does not hold a password
            # it accepted (the Initial Password enables it once the password lands), and stamps pwdLastSet, non-zero
            # here because the expiry behaviour is not "must change at next sign-in", when it takes one.
            $provisioned = @(Find-DirectoryEntry -DirectoryConfig $DirectoryConfig -Filter "(&(objectClass=user)(objectCategory=person))" `
                -BaseDn $DirectoryConfig.UserContainer -Attributes @("sAMAccountName", "userAccountControl", "pwdLastSet"))
            $expectedUsers = (Get-TemplateScale -Template $effectiveTemplate).Users
            $disabled = @($provisioned | Where-Object { ([int]$_["userAccountControl"][0] -band 2) -ne 0 } | ForEach-Object { "$($_["sAMAccountName"][0])" })
            $unstamped = @($provisioned | Where-Object { -not $_.ContainsKey("pwdLastSet") -or "$($_["pwdLastSet"][0])" -eq "0" } | ForEach-Object { "$($_["sAMAccountName"][0])" })

            Write-Host "  Provisioned accounts: $($provisioned.Count) (expected $expectedUsers); disabled: $($disabled.Count); without a password stamp: $($unstamped.Count)" -ForegroundColor Cyan

            Add-TestResult -Name "The export provisioned $expectedUsers accounts under $($DirectoryConfig.UserContainer)" `
                -Passed ($provisioned.Count -eq $expectedUsers) `
                -Detail "Found $($provisioned.Count). Either the export provisioned fewer or $($DirectoryConfig.JimBindDN) could not write there; check the Directory Export Activity."

            Add-TestResult -Name "Every provisioned account is enabled (the domain accepted its Initial Password)" `
                -Passed ($provisioned.Count -gt 0 -and $disabled.Count -eq 0) `
                -Detail "$($disabled.Count) of $($provisioned.Count) accounts are still disabled: $($disabled -join ', ')"

            Add-TestResult -Name "Every provisioned account carries a non-zero pwdLastSet (the domain processed its Initial Password)" `
                -Passed ($provisioned.Count -gt 0 -and $unstamped.Count -eq 0) `
                -Detail "$($unstamped.Count) of $($provisioned.Count) accounts carry no password stamp: $($unstamped -join ', ')"

            $initialPasswordConfig = Get-JIMSyncRuleInitialPassword -Id $config.ExportSyncRuleId

            # A parked account is one the target refused. With Test 1 proving the domain refuses short passwords and
            # JIM bound as an account the policy applies to, zero parked means every generated password met a
            # 12 character, three-category policy.
            Add-TestResult -Name "No account was parked by the domain refusing a generated Initial Password" `
                -Passed ($initialPasswordConfig.parkedAccountCount -eq 0) `
                -Detail "parkedAccountCount was $($initialPasswordConfig.parkedAccountCount); reasons: $(Get-ParkedReasonText -RuleConfiguration $initialPasswordConfig)"

            Add-TestResult -Name "No account expired waiting for an Initial Password" `
                -Passed ($initialPasswordConfig.expiredAccountCount -eq 0) `
                -Detail "expiredAccountCount was $($initialPasswordConfig.expiredAccountCount)"
        }

        if ($lastStepIndex -ge $stepOrder.IndexOf("Override")) {
            # ---------------------------------------------------------------------------------------------------------
            # Override: a password only a domain controller can refuse, parked with its words, then released
            # ---------------------------------------------------------------------------------------------------------
            Write-TestSection "Test 7: A static Initial Password the domain refuses parks the account with the directory's words"

            $givenName = "Marigold"
            $surname = "Wintergreen"
            $samAccountName = "mwintergreen99"
            $displayName = "$givenName $surname"

            # The static password JIM's save-time check accepts (16 or more characters, four categories, checked
            # against the discovered policy) and the domain refuses: it contains the surname, part of the display name.
            # Active Directory's complexity rule forbids that, and it is the one requirement JIM cannot check
            # because it depends on the account the password is set on. It is set on exactly one account, the new one.
            $refusedPassword = "$surname-" + (Get-ActiveDirectoryFixturePassword -Length 16 -Avoid @($givenName, $samAccountName))
            $correctedPassword = Get-ActiveDirectoryFixturePassword -Length 20 -Avoid @($givenName, $surname, $samAccountName)

            try {
                Set-JIMSyncRuleInitialPassword -Id $config.ExportSyncRuleId -Source Static `
                    -StaticPassword (ConvertTo-SecurePasswordValue -Text $refusedPassword) `
                    -ChangeReason "Scenario 024: a password only the domain can refuse, to prove parking before release" | Out-Null
            }
            catch {
                throw "JIM refused to save the static Initial Password that the scenario needs the DOMAIN to refuse. Its save-time " +
                      "assessment (length and character categories against the discovered policy) was expected to accept it: " +
                      "$($_.Exception.Message)"
            }

            # One more HR user. Cloned from the last row so every column has a valid value, then made unique.
            $csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"
            $rows = @(Import-Csv $csvPath)
            $newRow = $rows[-1].PSObject.Copy()
            $overrides = [ordered]@{
                employeeId        = "EMP000999"
                firstName         = $givenName
                lastName          = $surname
                email             = "$samAccountName@$($DirectoryConfig.Domain)"
                samAccountName    = $samAccountName
                displayName       = $displayName
                status            = "Active"
                userPrincipalName = "$samAccountName@$($DirectoryConfig.Domain)"
                employeeType      = "Employee"
                employeeEndDate   = ""
            }
            foreach ($column in $overrides.Keys) {
                if ($null -eq $newRow.PSObject.Properties[$column]) {
                    throw "hr-users.csv has no '$column' column, so the new user cannot be added to it. The generator (Generate-TestCSV.ps1) has changed; update this scenario."
                }
                $newRow.$column = $overrides[$column]
            }
            @($rows) + $newRow | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
            Copy-CsvToConnectorFiles -SourcePath $csvPath
            Write-Host "  OK Added $displayName ($samAccountName) to the HR CSV; the Initial Password contains '$surname'" -ForegroundColor Green

            Write-Host "  [1/4] HR CSV Full Import..." -ForegroundColor DarkGray
            $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Full Import (one more user)"

            Write-Host "  [2/4] HR CSV Delta Sync..." -ForegroundColor DarkGray
            $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Delta Sync (one more user)"

            Write-Host "  [3/4] Directory Export (the account is created, its Initial Password refused)..." -ForegroundColor DarkGray
            $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Export (one more user)"

            $parkedResult = Wait-ParkedAndPendingChange -ConnectedSystemId $config.LDAPSystemId -ExpectedParked 1
            $parkedRows = @($parkedResult.Parked)
            Write-Host "  Parked after $($parkedResult.Seconds) s: $($parkedRows.Count)" -ForegroundColor Cyan

            Add-TestResult -Name "The new account is parked by the domain's refusal of the Initial Password" `
                -Passed ($parkedRows.Count -eq 1) `
                -Detail "Expected 1 parked row, found $($parkedRows.Count) after $($parkedResult.Seconds) seconds (still pending: $(@($parkedResult.Pending).Count)). If it was not refused, this domain controller accepted a password containing part of the account's display name."

            $wrongOrigin = @($parkedRows | Where-Object { $_.origin -ne 'Provisioned' -or $_.syncRuleId -ne $config.ExportSyncRuleId })
            Add-TestResult -Name "The parked row is Provisioned, staged against this Synchronisation Rule" `
                -Passed ($wrongOrigin.Count -eq 0) `
                -Detail "$($wrongOrigin.Count) row(s) carried an Origin other than Provisioned, or a SyncRuleId other than $($config.ExportSyncRuleId)."

            $parkedConfig = Get-JIMSyncRuleInitialPassword -Id $config.ExportSyncRuleId
            $parkedReason = @($parkedConfig.parkedReasons) | Select-Object -First 1
            $reasonClass = if ($parkedReason) { Get-PropertyText -Object $parkedReason -Name 'failureReason' } else { "" }
            $reasonText = if ($parkedReason) { Get-PropertyText -Object $parkedReason -Name 'targetMessage' } else { "" }
            Write-Host "  Parked reason: [$reasonClass] $reasonText" -ForegroundColor Yellow

            Add-TestResult -Name "The Synchronisation Rule reports one parked account, classed as a policy rejection" `
                -Passed ($parkedConfig.parkedAccountCount -eq 1 -and $reasonClass -eq "PolicyRejection") `
                -Detail "parkedAccountCount was $($parkedConfig.parkedAccountCount) and the reason was classed '$reasonClass'. Reasons: $(Get-ParkedReasonText -RuleConfiguration $parkedConfig)"

            # The directory's own words: Active Directory explains a refusal with a hexadecimal code, 0000052D
            # (ERROR_PASSWORD_RESTRICTION), at the head of its diagnostic message.
            Add-TestResult -Name "The parked reason carries the directory's own words (0000052D)" `
                -Passed ($reasonText -match '0000052D') `
                -Detail "The reason was '$reasonText'. The connector classes a refusal as a policy rejection from the result code alone; the directory's code is what tells an administrator which rule it broke."

            $refusedState = Get-AccountState -SamAccountName $samAccountName
            Add-TestResult -Name "The refused account exists in the directory, disabled and without a password" `
                -Passed ($null -ne $refusedState -and -not $refusedState.Enabled -and -not $refusedState.PasswordWasStamped) `
                -Detail "State read from the directory: $(if ($null -eq $refusedState) { 'the account does not exist' } else { "enabled $($refusedState.Enabled), password stamped $($refusedState.PasswordWasStamped)" }). The export creates the account and only then is the password refused."

            Write-TestSection "Test 8: Correcting the rule to a compliant password releases the parked account"

            Set-JIMSyncRuleInitialPassword -Id $config.ExportSyncRuleId -Source Static `
                -StaticPassword (ConvertTo-SecurePasswordValue -Text $correctedPassword) `
                -ChangeReason "Scenario 024: correct the rule so the parked account releases" | Out-Null

            # Saving the corrected rule is what releases every account parked against it: no re-export, no retry.
            # (Scenario 017 proves it for Samba AD; this is the same behaviour against a domain controller.)
            Write-Host "  [4/4] Waiting for the parked account to be released and its password delivered..." -ForegroundColor DarkGray
            $releasedResult = Wait-ParkedAndPendingChange -ConnectedSystemId $config.LDAPSystemId -ExpectedParked 0
            Write-Host ("  Released and delivered {0} s after the rule was saved" -f $releasedResult.Seconds) -ForegroundColor Cyan

            $stillExpired = @(Get-JIMPendingPasswordChange -ConnectedSystemId $config.LDAPSystemId -Status Expired)
            $releasedConfig = Get-JIMSyncRuleInitialPassword -Id $config.ExportSyncRuleId

            Add-TestResult -Name "Nothing remains Parked or Pending once the Synchronisation Rule is corrected" `
                -Passed (@($releasedResult.Parked).Count -eq 0 -and @($releasedResult.Pending).Count -eq 0) `
                -Detail "Parked: $(@($releasedResult.Parked).Count), pending: $(@($releasedResult.Pending).Count) after $($releasedResult.Seconds) seconds."

            Add-TestResult -Name "Nothing expired while the account waited for the rule to be corrected" `
                -Passed ($stillExpired.Count -eq 0) `
                -Detail "$($stillExpired.Count) row(s) expired."

            Add-TestResult -Name "The Synchronisation Rule's parked count returns to zero once corrected" `
                -Passed ($releasedConfig.parkedAccountCount -eq 0) `
                -Detail "parkedAccountCount was $($releasedConfig.parkedAccountCount)."

            $releasedState = Get-AccountState -SamAccountName $samAccountName
            Add-TestResult -Name "The released account is enabled and holds a password the domain accepted" `
                -Passed ($null -ne $releasedState -and $releasedState.Enabled -and $releasedState.PasswordWasStamped) `
                -Detail "State read from the directory: $(if ($null -eq $releasedState) { 'the account does not exist' } else { "enabled $($releasedState.Enabled), password stamped $($releasedState.PasswordWasStamped)" })."
        }

        # The worker logs everything it does through the password channel; an Error there means a delivery that failed
        # quietly behind a green Activity. A refusal that parks an account is logged as a Warning, not an Error.
        Assert-NoWorkerErrors -Since $startTime
    }
    finally {
        Disconnect-JIM -ErrorAction SilentlyContinue
        Remove-Module JIM -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------------------------------------------
$duration = (Get-Date) - $startTime
$passed = @($script:TestResults | Where-Object { $_.Passed }).Count
$failed = @($script:TestResults | Where-Object { -not $_.Passed }).Count

Write-TestSection "Scenario 024 Summary"
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
Write-Host "OK JIM read the domain policy as the domain enforces it, and every Initial Password it generated from it" -ForegroundColor Green
Write-Host "   was accepted by a domain controller that provably refuses shorter ones." -ForegroundColor Green
exit 0
