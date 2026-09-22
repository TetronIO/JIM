# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 22: OpenLDAP Password Policy (discovery, enforcement, generated passwords satisfy it)

.DESCRIPTION
    Proves that JIM reads the password policy an OpenLDAP directory publishes through the ppolicy
    overlay, and that the Initial Passwords it generates from that policy are ones the directory accepts
    (#1702; PRD Scenarios 1 and 2).

    The unit tests in LdapConnectorPasswordPolicyTests assert against a mocked LDAP executor, so they
    prove how JIM maps what a directory says and nothing about what a real overlay says or enforces.
    This scenario closes that gap against a real ppolicy overlay whose minimum length (12) is above
    JIM's default, so a generator that ignored the discovered policy would park accounts here.

    The chain, in order, and why each link is needed:

      1. Populate: a default policy (pwdMinLength 12, pwdInHistory 5, pwdMaxAge 90 days,
         pwdCheckQuality 2) on the Yellowstone suffix, a NON-ROOT provisioner for JIM to bind as, and a
         probe user with a known password.
      2. Negative control: the probe user changes its own password to a 5-character value through the
         RFC 3062 Password Modify operation and is refused with a constraint violation. Without this,
         "nothing parked" in step 5 proves nothing: the overlay might not be enforcing at all. Any
         other refusal (access, bind) is a fixture fault and fails just as loudly.
      3. Setup, then JIM's discovered policy matches the fixture exactly: 12, 5, 90 days, nothing for
         minimum age or character classes, outcome Read, override signal CouldNotDetermine (an empty
         pwdPolicySubentry probe is never "none": the attribute is operational and may be hidden),
         and no further checks (no check module is named).
      4. Scenario 1's import, synchronisation and export at Micro provision ten accounts as the
         provisioner, each given a generated Initial Password through the password channel.
      5. Every provisioned entry carries pwdChangedTime (the overlay processed the write) and JIM
         reports nothing parked. With step 2 and the non-root bind, a password under 12 characters
         would have been parked, so this is the evidence for PRD Scenario 2.
      6. One entry is given a pwdPolicySubentry; a schema refresh then reports the override signal as
         Present.

    OpenLDAP only. JIM binds as cn=jim-provisioner rather than the rootdn because slapo-ppolicy exempts
    the rootdn from every check; a scenario bound as cn=admin would see every password accepted.

.PARAMETER Step
    Which part to execute. Steps are cumulative: a named step runs everything up to and including
    itself (Discovery, Provision, Override, All).

.PARAMETER Template
    Accepted for runner compatibility and deliberately not used for sizing. This scenario asserts
    against the accounts of one Micro export; a larger template would only lengthen it.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER ContinueOnError
    Continue executing remaining tests even if a test fails. The negative control is exempt: if the
    directory does not enforce, nothing after it can prove anything, so it stops the scenario regardless.

.PARAMETER SkipPopulate
    Skips Populate-OpenLDAP-Scenario22.ps1. The negative control still runs; without the fixture it fails.

.PARAMETER DirectoryConfig
    Directory configuration hashtable from Get-DirectoryConfig -DirectoryType OpenLDAP. Its rootdn
    credentials are used for the scenario's own reads of the directory; JIM binds as the provisioner.

.EXAMPLE
    ./Invoke-Scenario22-OpenLdapPasswordPolicy.ps1 -ApiKey "jim_..." -Template Micro
#>

<#
    STEP 0 SPIKE (a manual check for whoever runs this scenario first)

    The plan for #1702 asks for one thing to be established before anything is built on it: that the
    integration OpenLDAP container takes an RFC 3062 Password Modify from a NON-ROOT account over plain
    ldap://. JIM's password channel uses exactly that operation on OpenLDAP, and LdapConnector's
    OpenPasswordConnection only WARNS about an unencrypted channel (it does not refuse), so the
    scenario can run without TLS if the container permits the operation.

    With the stack up (openldap-primary healthy) and Populate-OpenLDAP-Scenario22.ps1 run once:

        docker exec openldap-primary ldappasswd -x -H ldap://localhost:1389 \
            -D "uid=s22probe,dc=yellowstone,dc=local" -w 'Probe-Lantern-88!' -s 'Spike-Harbour-2099!'

    Expected: exit code 0 and no output (the operation succeeded). Test 2 below automates this same
    check, so a green Test 2 is the spike recorded.

    If the directory answers "Confidentiality required (13)" or "Server is unwilling to perform (53)",
    the container refuses a cleartext extended operation, and the scenario needs the TLS fallback
    before it can go further:
      - give the openldap-primary service LDAP_ENABLE_TLS=yes with LDAP_TLS_CERT_FILE, LDAP_TLS_KEY_FILE
        and LDAP_TLS_CA_FILE mounted from a generated self-signed pair (the Bitnami image does not
        generate one), listening on 1636;
      - trust that certificate in JIM with Add-JIMCertificate -CertificateData (Setup-Scenario15.ps1's
        pattern: upload the bytes, never skip validation);
      - hand Setup-Scenario22.ps1 a DirectoryConfig with UseSSL $true, Port 1636, LdapSearchScheme
        "ldaps" and LdapSearchPort 1636, so both JIM and this script's ldappasswd calls use TLS.
    Then rerun the command above over ldaps:// and proceed.
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Discovery", "Provision", "Override", "All")]
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

# Import helpers. LDAP-Helpers supplies the read side (Invoke-LDAPSearch / Expand-LDIFFoldedLine) and
# the RFC 3062 change (Set-LDAPUserPasswordWithPasswordModify): enforcement and the overlay's
# pwdChangedTime stamp can only be observed by reading the directory itself, not JIM's view of it.
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Primary
}

if (-not $ApiKey) {
    throw "API key required for authentication. Create one via the JIM portal: Admin > API Keys."
}

if ($DirectoryConfig.UserObjectClass -ne "inetOrgPerson") {
    throw "Scenario 22 requires OpenLDAP. The fixture is a ppolicy overlay on the Yellowstone suffix, and " +
          "$($DirectoryConfig.ConnectedSystemName) has no equivalent for it to assert against. " +
          "Run-IntegrationTests.ps1 should have rejected this combination before this script was invoked."
}

# The fixture. Populate-OpenLDAP-Scenario22.ps1 and Setup-Scenario22.ps1 carry the same defaults; they
# are passed explicitly here so one place decides them for a run.
$provisionerBindDN = "cn=jim-provisioner,dc=yellowstone,dc=local"
$provisionerPassword = "Provisioner-Meadow-41!"
$probeBindDN = "uid=s22probe,dc=yellowstone,dc=local"
$probePassword = "Probe-Lantern-88!"
$policyDN = "cn=default,ou=Policies,dc=yellowstone,dc=local"

# What the fixture publishes and what JIM must therefore report.
$expectedMinimumLength = 12
$expectedHistoryLength = 5
$expectedMaximumAgeDays = 90     # pwdMaxAge 7776000 seconds

# The values the probe user tries to change its own password to. The short one is what the policy must
# refuse; the compliant one is what proves the channel itself works (the step 0 spike, recorded).
$shortPassword = 'Ab1!x'
$compliantPassword = 'Spike-Harbour-2099!'

# One Micro export is what this scenario asserts against, so it always provisions at Micro no matter
# what the runner passed. A larger template would lengthen the export and prove nothing further.
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
        Write-Host "  ✓ PASSED: $Name" -ForegroundColor Green
    }
    else {
        Write-Host "  ✗ FAILED: $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "      $Detail" -ForegroundColor Yellow }
        if (-not $ContinueOnError) {
            throw "Assertion failed: $Name. $Detail"
        }
    }
}

function Get-PolicyProperty {
    <#
    .SYNOPSIS
        Reads a property off the discovered policy response, naming its absence rather than throwing.

    .DESCRIPTION
        Set-StrictMode turns a missing property into a terminating error, which would hide which
        assertion failed. discoveryOutcome, policyOverrideSignal and furtherChecksApply are added to
        the response by #1702 (Phase 1); against an older JIM they are absent, and the assertion
        should say so.
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

function Format-PolicyValue {
    param($Value)
    if ($null -eq $Value) { return '<null>' }
    return "$Value"
}

function Get-ProvisionedEntries {
    <#
    .SYNOPSIS
        Reads every inetOrgPerson under the managed container, with its pwdChangedTime, as the rootdn.

    .DESCRIPTION
        pwdChangedTime is an operational attribute stamped by the ppolicy overlay whenever it processes
        a password write, so its presence is the directory's own record that JIM's password reached the
        overlay. It has to be asked for by name. Returns an array of hashtables with dn, uid and
        pwdChangedTime (absent key when the entry carries none). Emitted unwrapped (no leading comma):
        the callers wrap the result in @(), and a comma here would hand them a one-element array holding
        the whole array, so every entry's DN would read as one joined string.
    #>
    $raw = Invoke-LDAPSearch `
        -ContainerName $DirectoryConfig.ContainerName `
        -Server "localhost" `
        -Port $DirectoryConfig.LdapSearchPort `
        -Scheme $DirectoryConfig.LdapSearchScheme `
        -BaseDN $DirectoryConfig.UserContainer `
        -BindDN $DirectoryConfig.BindDN `
        -BindPassword $DirectoryConfig.BindPassword `
        -Filter "(objectClass=$($DirectoryConfig.UserObjectClass))" `
        -Attributes @("uid", "pwdChangedTime")

    $entries = @()
    if (-not $raw) { return $entries }

    $current = $null
    foreach ($line in (Expand-LDIFFoldedLine -RawLdif ($raw -join "`n"))) {
        if ($line -match '^\s*#') { continue }
        if ($line -match '^dn:\s*(.+)$') {
            if ($current) { $entries += $current }
            $current = @{ dn = $matches[1] }
            continue
        }
        if ($current -and $line -match '^(uid|pwdChangedTime):\s*(.+)$') {
            $current[$matches[1]] = $matches[2]
        }
    }
    if ($current) { $entries += $current }
    return $entries
}

Write-TestSection "Scenario 22: OpenLDAP Password Policy"
Write-Host "Directory:   $($DirectoryConfig.ConnectedSystemName) ($($DirectoryConfig.ContainerName))" -ForegroundColor Gray
Write-Host "JIM binds as: $provisionerBindDN (non-root; the rootdn is exempt from the policy)" -ForegroundColor Gray
Write-Host "Template:    $effectiveTemplate (the -Template value is not used for sizing)" -ForegroundColor Gray
Write-Host "Step:        $Step (steps are cumulative)" -ForegroundColor Gray
Write-Host ""

# Cumulative dispatch: each step builds on the previous one's state.
$stepOrder = @("Discovery", "Provision", "Override")
$lastStepIndex = if ($Step -eq "All") { $stepOrder.Count - 1 } else { $stepOrder.IndexOf($Step) }

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Step 0: The fixture, and the proof that it enforces
# ─────────────────────────────────────────────────────────────────────────────────────────────
Write-TestSection "Step 0: Populating the directory and proving the policy is enforced"

Write-Host "Waiting for OpenLDAP to be healthy..." -ForegroundColor Gray
$maxWaitSeconds = 120
$elapsed = 0
$interval = 5
$containerStatus = ""
while ($elapsed -lt $maxWaitSeconds) {
    $containerStatus = docker inspect --format='{{.State.Health.Status}}' $DirectoryConfig.ContainerName 2>&1
    if ($containerStatus -eq "healthy") { break }
    Start-Sleep -Seconds $interval
    $elapsed += $interval
}
if ($containerStatus -ne "healthy") {
    throw "$($DirectoryConfig.ContainerName) container did not become healthy within ${maxWaitSeconds}s (status: $containerStatus)"
}
Write-Host "  ✓ OpenLDAP is healthy" -ForegroundColor Green

if (-not $SkipPopulate) {
    Write-Host "Populating the password policy fixture..." -ForegroundColor Gray
    & "$PSScriptRoot/../Populate-OpenLDAP-Scenario22.ps1" `
        -Container $DirectoryConfig.ContainerName `
        -ProvisionerBindDN $provisionerBindDN -ProvisionerPassword $provisionerPassword `
        -ProbeBindDN $probeBindDN -ProbePassword $probePassword
    Write-Host "  ✓ Fixture populated" -ForegroundColor Green
}
else {
    Write-Host "  Skipping population (-SkipPopulate); the fixture must already be in place" -ForegroundColor Yellow
}

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Test 1: the negative control that gives every later "nothing parked" its meaning
# ─────────────────────────────────────────────────────────────────────────────────────────────
Write-TestSection "Test 1: The directory refuses a 5-character password from a non-root account"

$shortChange = Set-LDAPUserPasswordWithPasswordModify `
    -BindDN $probeBindDN -BindPassword $probePassword -NewPassword $shortPassword -DirectoryConfig $DirectoryConfig

$enforced = ($shortChange.Outcome -eq 'ConstraintViolation')
Add-TestResult -Name "A 5-character password is refused with a constraint violation (policy enforced)" `
    -Passed $enforced `
    -Detail "Expected ConstraintViolation, got '$($shortChange.Outcome)' (exit code $($shortChange.ExitCode)). Directory said: $($shortChange.Output)"

if (-not $enforced) {
    # Deliberately not subject to -ContinueOnError: every assertion from here on reads "the policy
    # accepted it", which means nothing if the policy accepts everything.
    throw "ENFORCEMENT NOT PROVEN: the ppolicy overlay did not refuse a 5-character password from " +
          "$probeBindDN (outcome '$($shortChange.Outcome)': $($shortChange.Output)). Nothing this scenario " +
          "asserts afterwards can distinguish a satisfied policy from an absent one, so it stops here. " +
          "Check that Populate-OpenLDAP-Scenario22.ps1 ran, that the overlay's olcPPolicyDefault names " +
          "$policyDN, and (if the outcome is InsufficientAccess) the self-write olcAccess it prepends."
}

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Test 2: the channel itself works (the step 0 spike, recorded)
# ─────────────────────────────────────────────────────────────────────────────────────────────
Write-TestSection "Test 2: A compliant password is accepted over the same channel"

$compliantChange = Set-LDAPUserPasswordWithPasswordModify `
    -BindDN $probeBindDN -BindPassword $probePassword -NewPassword $compliantPassword -DirectoryConfig $DirectoryConfig

Add-TestResult -Name "A compliant password is accepted through RFC 3062 over $($DirectoryConfig.LdapSearchScheme):// as a non-root account" `
    -Passed ($compliantChange.Outcome -eq 'Success') `
    -Detail ("Expected Success, got '$($compliantChange.Outcome)' (exit code $($compliantChange.ExitCode)). Directory said: $($compliantChange.Output). " +
             "A 'Confidentiality required' or 'unwilling to perform' answer means the container refuses cleartext extended operations; see the STEP 0 SPIKE note at the top of this script for the TLS fallback.")

$probeRead = Invoke-LDAPSearch `
    -ContainerName $DirectoryConfig.ContainerName -Server "localhost" `
    -Port $DirectoryConfig.LdapSearchPort -Scheme $DirectoryConfig.LdapSearchScheme `
    -BaseDN $probeBindDN -BindDN $DirectoryConfig.BindDN -BindPassword $DirectoryConfig.BindPassword `
    -Filter "(objectClass=*)" -Attributes @("pwdChangedTime")
$probeStamped = ($null -ne $probeRead) -and (($probeRead -join "`n") -match '(?m)^pwdChangedTime:')
Add-TestResult -Name "The overlay stamped pwdChangedTime on the probe user after the change" `
    -Passed $probeStamped `
    -Detail "pwdChangedTime is what the overlay writes when it processes a password; its absence means the change bypassed the overlay. Read: $(($probeRead | Out-String).Trim())"

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Step 0b: Configure JIM (the substrate, bound as the provisioner, with a Discovered Initial Password)
# ─────────────────────────────────────────────────────────────────────────────────────────────
Write-TestSection "Step 0b: Configuring JIM"

Write-Host "Resetting CSV test data to baseline..." -ForegroundColor Gray
& "$PSScriptRoot/../Get-OrGenerate-TestCSV.ps1" -Template $effectiveTemplate -OutputPath "$PSScriptRoot/../../test-data"
Write-Host "  ✓ CSV test data reset to baseline" -ForegroundColor Green

$config = & "$PSScriptRoot/../Setup-Scenario22.ps1" `
    -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $effectiveTemplate -DirectoryConfig $DirectoryConfig `
    -ProvisionerBindDN $provisionerBindDN -ProvisionerBindPassword $provisionerPassword

if (-not $config) {
    throw "Failed to set up Scenario 22 configuration"
}
Write-Host "  ✓ JIM configured; the Export Synchronisation Rule sets a Discovered-policy Initial Password" -ForegroundColor Green

$modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

$provisionedEntries = @()

try {
    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 3: JIM read the policy the fixture publishes (PRD Scenario 1)
    # ─────────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("Discovery")) {
        Write-TestSection "Test 3: The discovered policy matches the fixture"

        # Setup-Scenario1's schema import is what triggers discovery, and it ran as the provisioner.
        $policy = Get-JIMConnectedSystemPasswordPolicy -Id $config.LDAPSystemId

        Add-TestResult -Name "JIM discovered at least one constraint" `
            -Passed ((Get-PolicyProperty -Policy $policy -Name 'hasAnyDiscoveredConstraint') -eq $true) `
            -Detail "hasAnyDiscoveredConstraint was '$(Get-PolicyProperty -Policy $policy -Name 'hasAnyDiscoveredConstraint')'. Nothing read at all usually means the reader did not recognise the directory or could not see the policy entry as $provisionerBindDN."

        Add-TestResult -Name "minimumLength is $expectedMinimumLength (pwdMinLength)" `
            -Passed ((Get-PolicyProperty -Policy $policy -Name 'minimumLength') -eq $expectedMinimumLength) `
            -Detail "minimumLength was '$(Get-PolicyProperty -Policy $policy -Name 'minimumLength')'"

        Add-TestResult -Name "passwordHistoryLength is $expectedHistoryLength (pwdInHistory)" `
            -Passed ((Get-PolicyProperty -Policy $policy -Name 'passwordHistoryLength') -eq $expectedHistoryLength) `
            -Detail "passwordHistoryLength was '$(Get-PolicyProperty -Policy $policy -Name 'passwordHistoryLength')'"

        Add-TestResult -Name "maximumPasswordAgeDays is $expectedMaximumAgeDays (pwdMaxAge 7776000 seconds)" `
            -Passed ((Get-PolicyProperty -Policy $policy -Name 'maximumPasswordAgeDays') -eq $expectedMaximumAgeDays) `
            -Detail "maximumPasswordAgeDays was '$(Get-PolicyProperty -Policy $policy -Name 'maximumPasswordAgeDays')'"

        Add-TestResult -Name "minimumPasswordAgeDays is null (pwdMinAge absent)" `
            -Passed ((Get-PolicyProperty -Policy $policy -Name 'minimumPasswordAgeDays') -eq '<null>') `
            -Detail "minimumPasswordAgeDays was '$(Get-PolicyProperty -Policy $policy -Name 'minimumPasswordAgeDays')'"

        Add-TestResult -Name "complexityRequired is null (OpenLDAP publishes no character class rule)" `
            -Passed ((Get-PolicyProperty -Policy $policy -Name 'complexityRequired') -eq '<null>') `
            -Detail "complexityRequired was '$(Get-PolicyProperty -Policy $policy -Name 'complexityRequired')'"

        Add-TestResult -Name "requiredCharacterClassCount is null" `
            -Passed ((Get-PolicyProperty -Policy $policy -Name 'requiredCharacterClassCount') -eq '<null>') `
            -Detail "requiredCharacterClassCount was '$(Get-PolicyProperty -Policy $policy -Name 'requiredCharacterClassCount')'"

        Add-TestResult -Name "discoveryOutcome is Read" `
            -Passed ((Get-PolicyProperty -Policy $policy -Name 'discoveryOutcome') -eq 'Read') `
            -Detail "discoveryOutcome was '$(Get-PolicyProperty -Policy $policy -Name 'discoveryOutcome')' ('<absent>' means the API predates #1702 Phase 1)"

        # Decision 2 in the plan: an empty pwdPolicySubentry probe is CouldNotDetermine, never Absent,
        # because the attribute is operational and may be hidden by access control.
        Add-TestResult -Name "policyOverrideSignal is CouldNotDetermine (empty pwdPolicySubentry probe)" `
            -Passed ((Get-PolicyProperty -Policy $policy -Name 'policyOverrideSignal') -eq 'CouldNotDetermine') `
            -Detail "policyOverrideSignal was '$(Get-PolicyProperty -Policy $policy -Name 'policyOverrideSignal')' ('<absent>' means the API still carries fineGrainedPolicySignal: '$(Get-PolicyProperty -Policy $policy -Name 'fineGrainedPolicySignal')')"

        # Decision 3: pwdCheckQuality 2 alone does not mean further checks; only a named check module
        # does, and none is. Reading that needs the overlay's configuration, which the provisioner's
        # narrow read on cn=config provides; a refused read falls back to flagging pwdCheckQuality.
        Add-TestResult -Name "furtherChecksApply is false (no check module named on the overlay or the policy)" `
            -Passed ((Get-PolicyProperty -Policy $policy -Name 'furtherChecksApply') -eq $false) `
            -Detail "furtherChecksApply was '$(Get-PolicyProperty -Policy $policy -Name 'furtherChecksApply')'. True with pwdCheckQuality 2 and no module means the reader fell back to the coarse rule: check the provisioner's read on cn=config (Populate-OpenLDAP-Scenario22.ps1)."
    }

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 4: accounts are provisioned by the non-root provisioner, each with a generated password
    # ─────────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("Provision")) {
        Write-TestSection "Test 4: Provisioning accounts into $($DirectoryConfig.ConnectedSystemName) as $provisionerBindDN"

        Write-Host "  [1/5] HR CSV Full Import..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Full Import"

        Write-Host "  [2/5] HR CSV Delta Sync..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Delta Sync"

        # The Create exports land here, and each one stages a Pending Initial Password that the delivery
        # pass then sets through the Connector's password channel: RFC 3062, as the provisioner, into
        # the overlay's quality check.
        Write-Host "  [3/5] Directory Export (accounts created, Initial Passwords set)..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Export"

        # A Full Import, not a Delta Import: this is the Connected System's first import, so there is no
        # persisted baseline for a delta to compare against (Scenario 17's reasoning).
        Write-Host "  [4/5] Directory Full Import (confirms the exports)..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Full Import"

        Write-Host "  [5/5] Directory Delta Sync..." -ForegroundColor DarkGray
        $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "Directory Delta Sync"

        Write-Host "  ✓ Provisioning complete" -ForegroundColor Green

        # ─────────────────────────────────────────────────────────────────────────────────────
        # Test 5: the directory took every generated password, and JIM agrees (PRD Scenario 2)
        # ─────────────────────────────────────────────────────────────────────────────────────
        Write-TestSection "Test 5: Every provisioned entry holds a policy-checked password and nothing is parked"

        $provisionedEntries = @(Get-ProvisionedEntries)
        $stampedEntries = @($provisionedEntries | Where-Object { $_.ContainsKey('pwdChangedTime') })
        $unstamped = @($provisionedEntries | Where-Object { -not $_.ContainsKey('pwdChangedTime') } | ForEach-Object { $_.dn })

        Add-TestResult -Name "The export provisioned at least one entry under $($DirectoryConfig.UserContainer)" `
            -Passed ($provisionedEntries.Count -gt 0) `
            -Detail "No $($DirectoryConfig.UserObjectClass) entries found. Either the export provisioned nothing or $provisionerBindDN could not write there; check the Directory Export Activity."

        Write-Host "  Provisioned entries: $($provisionedEntries.Count); with pwdChangedTime: $($stampedEntries.Count)" -ForegroundColor Cyan

        # pwdChangedTime is the overlay's own stamp: it is written when a password passes through the
        # overlay, and it is written for the provisioner's writes precisely because the provisioner is
        # not the rootdn. An entry without it was created but never given a password the overlay saw.
        Add-TestResult -Name "Every provisioned entry carries pwdChangedTime (the overlay processed its Initial Password)" `
            -Passed ($provisionedEntries.Count -gt 0 -and $unstamped.Count -eq 0) `
            -Detail "$($unstamped.Count) of $($provisionedEntries.Count) entries carry no pwdChangedTime: $($unstamped -join ', ')"

        $initialPasswordConfig = Get-JIMSyncRuleInitialPassword -Id $config.ExportSyncRuleId

        # A parked account is one the target refused. With Test 1 proving the overlay refuses short
        # passwords and JIM bound as a non-root account the policy applies to, zero parked means every
        # generated password was at least 12 characters, which is PRD Scenario 2.
        Add-TestResult -Name "No account was parked by the directory refusing a generated Initial Password" `
            -Passed ($initialPasswordConfig.parkedAccountCount -eq 0) `
            -Detail "parkedAccountCount was $($initialPasswordConfig.parkedAccountCount); reasons: $(($initialPasswordConfig.parkedReasons | ForEach-Object { $_.reason }) -join ', ')"

        Add-TestResult -Name "No account expired waiting for an Initial Password" `
            -Passed ($initialPasswordConfig.expiredAccountCount -eq 0) `
            -Detail "expiredAccountCount was $($initialPasswordConfig.expiredAccountCount)"
    }

    # ─────────────────────────────────────────────────────────────────────────────────────────
    # Test 6: an object governed by another policy is noticed
    # ─────────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("Override")) {
        Write-TestSection "Test 6: A pwdPolicySubentry on one entry turns the override signal to Present"

        if ($provisionedEntries.Count -eq 0) {
            throw "No provisioned entry to place a pwdPolicySubentry on; the Provision step must run first."
        }
        $overrideDn = $provisionedEntries[0].dn
        Write-Host "  Entry under test: $overrideDn" -ForegroundColor Gray

        # Pointing the entry at the same policy changes nothing about enforcement; only the fact that an
        # entry names a policy of its own is under test. Written as the rootdn: pwdPolicySubentry is
        # operational, and setting it is an administrator's act. replace is idempotent on a re-run.
        $ldif = "dn: $overrideDn`nchangetype: modify`nreplace: pwdPolicySubentry`npwdPolicySubentry: $policyDN`n"
        $ldifPath = [System.IO.Path]::GetTempFileName()
        Set-Content -Path $ldifPath -Value $ldif -NoNewline
        try {
            $ldapUri = "$($DirectoryConfig.LdapSearchScheme)://localhost:$($DirectoryConfig.LdapSearchPort)"
            $modifyResult = bash -c "cat '$ldifPath' | docker exec -i $($DirectoryConfig.ContainerName) ldapmodify -x -H $ldapUri -D '$($DirectoryConfig.BindDN)' -w '$($DirectoryConfig.BindPassword)'" 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "Could not set pwdPolicySubentry on $overrideDn (exit code $LASTEXITCODE): $modifyResult"
            }
        }
        finally {
            Remove-Item -Path $ldifPath -Force -ErrorAction SilentlyContinue
        }
        Write-Host "  ✓ pwdPolicySubentry set to $policyDN" -ForegroundColor Green

        # Discovery runs on every schema import, so a refresh is how JIM is asked to look again.
        Write-Host "  Refreshing the schema so JIM reads the policy again..." -ForegroundColor Gray
        Import-JIMConnectedSystemSchema -Id $config.LDAPSystemId -Confirm:$false | Out-Null

        $policyAfterOverride = Get-JIMConnectedSystemPasswordPolicy -Id $config.LDAPSystemId

        Add-TestResult -Name "policyOverrideSignal is Present after an entry names its own policy" `
            -Passed ((Get-PolicyProperty -Policy $policyAfterOverride -Name 'policyOverrideSignal') -eq 'Present') `
            -Detail "policyOverrideSignal was '$(Get-PolicyProperty -Policy $policyAfterOverride -Name 'policyOverrideSignal')'. The probe is a subtree search for (pwdPolicySubentry=*) as $provisionerBindDN; an operational attribute it cannot read leaves the signal at CouldNotDetermine."

        Add-TestResult -Name "The default policy's figures are unchanged by the refresh (minimumLength still $expectedMinimumLength)" `
            -Passed ((Get-PolicyProperty -Policy $policyAfterOverride -Name 'minimumLength') -eq $expectedMinimumLength) `
            -Detail "minimumLength was '$(Get-PolicyProperty -Policy $policyAfterOverride -Name 'minimumLength')'"
    }

    # The worker logs everything it does through the password channel; an error there means a
    # delivery that failed quietly behind a green Activity. The unencrypted-channel notice is a
    # Warning, not an Error, so it does not trip this.
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

Write-TestSection "Scenario 22 Summary"
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
Write-Host "✓ JIM read the OpenLDAP password policy as published, and every Initial Password it" -ForegroundColor Green
Write-Host "  generated from it was accepted by an overlay that provably refuses shorter ones." -ForegroundColor Green
exit 0
