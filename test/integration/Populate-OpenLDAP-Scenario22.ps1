# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Populate OpenLDAP with the password policy fixture for Scenario 22: OpenLDAP Password Policy

.DESCRIPTION
    Turns the Yellowstone suffix (dc=yellowstone,dc=local) of the OpenLDAP container into a directory
    that ENFORCES a password policy, so that Scenario 22 can prove JIM reads that policy and generates
    Initial Passwords that satisfy it (#1702).

    The base image (docker/openldap/scripts/01-add-second-suffix.sh) loads the ppolicy overlay on both
    databases with no default policy, which enforces nothing. This script is what gives the Yellowstone
    overlay something to enforce:

    As the config admin (cn=config):
    - olcPPolicyDefault on the Yellowstone ppolicy overlay is pointed at
      cn=default,ou=Policies,dc=yellowstone,dc=local, so the policy applies to every entry in the
      suffix that does not name its own with pwdPolicySubentry.
    - The Bitnami image gives the Yellowstone database no olcAccess of its own, so slapd's implicit
      default ("to * by * read", which is what lets anyone bind: read implies auth) is what has been
      in force. The first explicit rule replaces that implicit one with an implicit DENY for anything
      it does not decide, so before any rule is prepended the default is written out as an explicit
      last rule. Without it, every non-rootdn bind on the suffix fails with Invalid credentials (49)
      the moment the rules below exist (found when this fixture was first run).
    - An olcAccess granting cn=jim-provisioner,dc=yellowstone,dc=local write on the whole Yellowstone
      database is PREPENDED to the database's ACL, so the provisioner can create entries and set their
      passwords without being the rootdn.
    - An olcAccess letting the probe user change its own userPassword is prepended after it, so the
      scenario's negative control (an RFC 3062 change as the probe user) is decided by the policy and
      not by access control.
    - A narrow read on cn=config (the database, overlay and policy-default attributes only) is
      prepended to the config database's ACL for the provisioner, so JIM's reader can correlate the
      overlay's olcPPolicyDefault with the database it belongs to. Without it the reader falls back to
      "the only pwdPolicy entry in the suffix" and, because it cannot see whether a check module is
      named, reports that further checks apply; with it the reader knows none is.

    As the data admin (cn=admin,dc=yellowstone,dc=local):
    - ou=Policies and the policy entry cn=default (pwdPolicy alongside applicationProcess, the
      structural class a policy entry needs): pwdAttribute userPassword, pwdMinLength 12,
      pwdInHistory 5, pwdMaxAge 7776000 (90 days), pwdCheckQuality 2.
    - cn=jim-provisioner (organizationalRole plus simpleSecurityObject, which carries userPassword),
      the account JIM binds as. The rootdn is exempt from the policy (slapo-ppolicy(5)), so a scenario
      that bound JIM as cn=admin would prove nothing: every password would be accepted.
    - uid=s22probe, an ordinary inetOrgPerson with a known password, placed OUTSIDE ou=People so the
      Scenario 1 substrate's import never sees it. The scenario changes this account's own password
      through RFC 3062 to show the policy refusing a short value and accepting a compliant one.

    Why pwdCheckQuality is 2: with 2 the overlay refuses a value it cannot check (a pre-hashed one),
    which is the setting that makes the negative control airtight; 1 would let a hashed value through
    unchecked and a "refused" assertion could then pass for the wrong reason.

    Idempotent: every modify is replace-or-check-first, every add tolerates "already exists", the
    provisioner's password is reset to its known value on every run, and the probe user is deleted and
    recreated on every run. Recreated rather than reset, because a reset is itself a password change
    the overlay records in pwdHistory with a timestamp of one-second resolution, and the scenario's own
    change moments later would then try to record an identical history value and be refused with
    "Type or value exists (20)" (found on the first full run). A fresh entry carries no history.

.PARAMETER Container
    The Docker container name running OpenLDAP (default: openldap-primary).

.PARAMETER ProvisionerBindDN
    The account JIM binds as. Setup-Scenario22.ps1 takes the same value.

.PARAMETER ProvisionerPassword
    The provisioner's password. Setup-Scenario22.ps1 takes the same value.

.PARAMETER ProbeBindDN
    The ordinary account the scenario changes the password of.

.PARAMETER ProbePassword
    The probe account's password after this script has run. Compliant with the policy so that a
    later change is judged on the new value alone.

.EXAMPLE
    ./Populate-OpenLDAP-Scenario22.ps1
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$Container = "openldap-primary",

    [Parameter(Mandatory=$false)]
    [string]$ProvisionerBindDN = "cn=jim-provisioner,dc=yellowstone,dc=local",

    [Parameter(Mandatory=$false)]
    [string]$ProvisionerPassword = "Provisioner-Meadow-41!",

    [Parameter(Mandatory=$false)]
    [string]$ProbeBindDN = "uid=s22probe,dc=yellowstone,dc=local",

    [Parameter(Mandatory=$false)]
    [string]$ProbePassword = "Probe-Lantern-88!"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Scenario 22: Populating OpenLDAP (Yellowstone) with the password policy fixture"

$containerName = $Container
$ldapUri = "ldap://localhost:1389"

$suffix = "dc=yellowstone,dc=local"
$configAdminDN = "cn=admin,cn=config"           # LDAP_CONFIG_ADMIN_* in docker-compose.integration-tests.yml
$configAdminPassword = "Test@123!"
$dataAdminDN = "cn=admin,$suffix"               # LDAP_ADMIN_* in docker-compose.integration-tests.yml
$dataAdminPassword = "Test@123!"
$policyDN = "cn=default,ou=Policies,$suffix"

function Invoke-Scenario22Ldap {
    <#
    .SYNOPSIS
        Runs ldapadd or ldapmodify inside the container with an LDIF payload, following the
        temp-file + docker-exec pattern of Populate-OpenLDAP-Scenario19.ps1.
    #>
    param(
        [Parameter(Mandatory=$true)] [ValidateSet("ldapadd", "ldapmodify")] [string]$Tool,
        [Parameter(Mandatory=$true)] [string]$BindDN,
        [Parameter(Mandatory=$true)] [string]$BindPassword,
        [Parameter(Mandatory=$true)] [string]$Ldif,
        [Parameter(Mandatory=$false)] [string]$What = ""
    )

    $ldifPath = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $ldifPath -Value $Ldif -NoNewline
    try {
        $result = bash -c "cat '$ldifPath' | docker exec -i $containerName $Tool -x -H $ldapUri -D '$BindDN' -w '$BindPassword' -c" 2>&1
        if ($LASTEXITCODE -ne 0 -and "$result" -notmatch "already exists") {
            throw "$Tool failed$(if ($What) { " ($What)" }) (exit code $LASTEXITCODE): $result"
        }
    }
    finally {
        Remove-Item -Path $ldifPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-Scenario22ConfigEntryDN {
    <#
    .SYNOPSIS
        Finds one entry under cn=config by filter, as the config admin; returns its DN or $null.
    #>
    param(
        [Parameter(Mandatory=$true)] [string]$BaseDN,
        [Parameter(Mandatory=$true)] [string]$Filter
    )

    $raw = & docker exec $containerName ldapsearch -x -LLL -H $ldapUri -D $configAdminDN -w $configAdminPassword `
        -b $BaseDN $Filter dn 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not search cn=config as $configAdminDN (exit code $LASTEXITCODE): $raw"
    }
    $dnLine = @($raw | Where-Object { "$_" -match '^dn:\s*(.+)$' } | Select-Object -First 1)
    if ($dnLine.Count -eq 0) { return $null }
    return ("$($dnLine[0])" -replace '^dn:\s*', '')
}

function Get-Scenario22AccessRules {
    <#
    .SYNOPSIS
        The olcAccess values on a cn=config database entry, in order; an empty array when it has none.
    #>
    param(
        [Parameter(Mandatory=$true)] [string]$DatabaseDN
    )

    $raw = & docker exec $containerName ldapsearch -x -LLL -H $ldapUri -D $configAdminDN -w $configAdminPassword `
        -b $DatabaseDN -s base "(objectClass=*)" olcAccess 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read olcAccess on $DatabaseDN (exit code $LASTEXITCODE): $raw"
    }
    # ldapsearch folds long values at 78 columns with a leading space on the continuation line.
    $logical = @()
    foreach ($line in @($raw)) {
        $text = "$line"
        if ($text -match '^ ' -and $logical.Count -gt 0) {
            $logical[$logical.Count - 1] += $text.Substring(1)
        }
        else {
            $logical += $text
        }
    }
    # Emitted unwrapped: a leading comma would hand the caller a one-element array holding an empty array
    # when there are no rules, and the callers wrap the result in @() themselves.
    return @($logical | Where-Object { $_ -match '^olcAccess:\s*' } | ForEach-Object { $_ -replace '^olcAccess:\s*', '' })
}

function Test-Scenario22AccessRulePresent {
    <#
    .SYNOPSIS
        Whether an olcAccess value mentioning the marker already exists on a cn=config database entry.
    #>
    param(
        [Parameter(Mandatory=$true)] [string]$DatabaseDN,
        [Parameter(Mandatory=$true)] [string]$Marker
    )

    # Matched per unfolded rule: ldapsearch folds values at 78 columns, and a marker that straddles the
    # fold (the provisioner's DN does, on the cn=config rule) would otherwise never be found, so the rule
    # would be added again on every run.
    foreach ($rule in @(Get-Scenario22AccessRules -DatabaseDN $DatabaseDN)) {
        if ($rule -match [regex]::Escape($Marker)) { return $true }
    }
    return $false
}

# ---------------------------------------------------------------------------------------------
# Step 1: Find the Yellowstone database and its ppolicy overlay in cn=config
# ---------------------------------------------------------------------------------------------
Write-TestStep "Step 1" "Locating the Yellowstone database and its ppolicy overlay in cn=config"

$databaseDN = Get-Scenario22ConfigEntryDN -BaseDN "cn=config" -Filter "(olcSuffix=$suffix)"
if (-not $databaseDN) {
    throw "No database with olcSuffix $suffix found in cn=config. The OpenLDAP container is not the JIM integration image."
}

$overlayDN = Get-Scenario22ConfigEntryDN -BaseDN $databaseDN -Filter "(objectClass=olcPPolicyConfig)"
if (-not $overlayDN) {
    throw "No ppolicy overlay found under $databaseDN. The base image predates the ppolicy overlay " +
          "(docker/openldap/scripts/01-add-second-suffix.sh); rebuild it with docker/openldap/Build-OpenLdapImage.ps1 " +
          "(Run-IntegrationTests.ps1 does this itself when the build hash label no longer matches)."
}

Write-Host "  Database: $databaseDN" -ForegroundColor Gray
Write-Host "  Overlay:  $overlayDN" -ForegroundColor Gray

# ---------------------------------------------------------------------------------------------
# Step 2: Point the overlay at the default policy and open the database to the provisioner
# ---------------------------------------------------------------------------------------------
Write-TestStep "Step 2" "Configuring the overlay's default policy and the provisioner's access (config admin)"

# replace is idempotent: the same value on a re-run is a no-op.
Invoke-Scenario22Ldap -Tool ldapmodify -BindDN $configAdminDN -BindPassword $configAdminPassword -What "olcPPolicyDefault" -Ldif @"
dn: $overlayDN
changetype: modify
replace: olcPPolicyDefault
olcPPolicyDefault: $policyDN
"@
Write-Host "  OK olcPPolicyDefault = $policyDN" -ForegroundColor Green

# A database with no olcAccess runs on slapd's implicit default, "to * by * read" (slapd.access(5)),
# and that is what every non-rootdn bind on this suffix has relied on: read on userPassword implies
# auth. The first explicit rule switches the implicit default to deny, so the rules prepended below,
# each ending in "by * break", would fall through to a refusal for everyone they do not name and every
# bind but the rootdn's would answer Invalid credentials (49). The default is therefore written out as
# the last rule first, before anything is prepended in front of it. A database that already carries
# rules keeps them as its tail and needs nothing added.
$defaultReadRule = 'to * by * read'
$existingRules = @(Get-Scenario22AccessRules -DatabaseDN $databaseDN)
if ($existingRules.Count -eq 0) {
    Invoke-Scenario22Ldap -Tool ldapmodify -BindDN $configAdminDN -BindPassword $configAdminPassword -What "explicit default olcAccess" -Ldif @"
dn: $databaseDN
changetype: modify
add: olcAccess
olcAccess: {0}$defaultReadRule
"@
    Write-Host "  OK $databaseDN had no olcAccess; slapd's implicit default ($defaultReadRule) written as its last rule" -ForegroundColor Green
}
else {
    Write-Host "  $databaseDN already carries $($existingRules.Count) olcAccess rule(s); they remain its tail" -ForegroundColor Gray
}

# olcAccess is X-ORDERED: adding a value with the {0} prefix inserts it first and renumbers the
# rest, which is how a rule is prepended. "by * break" hands everyone else on to the rules that
# were there before, so nothing about the admin's or anonymous access changes.
if (Test-Scenario22AccessRulePresent -DatabaseDN $databaseDN -Marker $ProvisionerBindDN) {
    Write-Host "  Provisioner write access already granted on $databaseDN" -ForegroundColor Gray
}
else {
    Invoke-Scenario22Ldap -Tool ldapmodify -BindDN $configAdminDN -BindPassword $configAdminPassword -What "provisioner olcAccess" -Ldif @"
dn: $databaseDN
changetype: modify
add: olcAccess
olcAccess: {0}to * by dn.exact="$ProvisionerBindDN" write by * break
"@
    Write-Host "  OK Provisioner granted write on $databaseDN" -ForegroundColor Green
}

# The probe user changes its OWN password in the scenario's negative control, and that change must be
# refused by the policy, not by access control: an "insufficient access" would otherwise read as
# enforcement. The Bitnami image's own ACL may or may not grant self-write on userPassword, so it is
# granted here explicitly. Inserted at {1}, after the provisioner rule.
$selfWriteMarker = 'to attrs=userPassword by self write by * break'
if (Test-Scenario22AccessRulePresent -DatabaseDN $databaseDN -Marker $selfWriteMarker) {
    Write-Host "  Self-write on userPassword already granted on $databaseDN" -ForegroundColor Gray
}
else {
    Invoke-Scenario22Ldap -Tool ldapmodify -BindDN $configAdminDN -BindPassword $configAdminPassword -What "self-write olcAccess" -Ldif @"
dn: $databaseDN
changetype: modify
add: olcAccess
olcAccess: {1}$selfWriteMarker
"@
    Write-Host "  OK Accounts may change their own userPassword on $databaseDN" -ForegroundColor Green
}

# A narrow read on cn=config for the provisioner: the Yellowstone database's own subtree (its
# database entry and the ppolicy overlay entry beneath it, since overlays are children of the
# database they attach to), and only the attributes JIM's reader asks for, nothing else (no
# olcRootPW, no ACLs). This is what a customer would grant a service account so JIM can tell which
# policy is the default for which database.
#
# Scoped to $databaseDN, not the whole cn=config subtree: the lab's base image (#1715) now attaches
# a default policy to BOTH suffix databases (Yellowstone and Glitterband share the same lab default,
# pwdMinLength 7), and this fixture then repoints only Yellowstone's to a stricter one (pwdMinLength
# 12). A grant covering the whole of cn=config would let the provisioner also read Glitterband's
# olcOverlay=ppolicy entry, and JIM's override heuristic treats "two databases with different
# default policies" as evidence an override exists (see docs/connectors/jim-ldap-connector.md >
# Password policy discovery) - which would make Step 5's later assertion see Present before Step 6
# ever sets a pwdPolicySubentry, for a reason that has nothing to do with what Step 6 is testing.
$configDatabaseDN = "olcDatabase={0}config,cn=config"
if (Test-Scenario22AccessRulePresent -DatabaseDN $configDatabaseDN -Marker $ProvisionerBindDN) {
    Write-Host "  Provisioner read access already granted on cn=config" -ForegroundColor Gray
}
else {
    # Two rules, not one: JIM's reader issues its search with "cn=config" itself as the base (the
    # rootDSE's configContext) and Subtree scope, and a search whose declared base is not covered by
    # any olcAccess at all is refused outright before candidates are even filtered, regardless of
    # what is readable underneath. A bare `search` grant on the literal "cn=config" entry (never
    # matched by the reader's own filter, so nothing about it is ever returned) satisfies that; the
    # actual data stays governed by the narrower `read` grant on $databaseDN below.
    Invoke-Scenario22Ldap -Tool ldapmodify -BindDN $configAdminDN -BindPassword $configAdminPassword -What "cn=config read olcAccess" -Ldif @"
dn: $configDatabaseDN
changetype: modify
add: olcAccess
olcAccess: {0}to dn.exact="cn=config" by dn.exact="$ProvisionerBindDN" search by * break
olcAccess: {1}to dn.subtree="$databaseDN" attrs=entry,objectClass,olcDatabase,olcSuffix,olcOverlay,olcPPolicyDefault,olcPPolicyCheckModule by dn.exact="$ProvisionerBindDN" read by * break
"@
    Write-Host "  OK Provisioner granted search on cn=config (base resolution) and a narrow read on $databaseDN (database, overlay and policy-default attributes)" -ForegroundColor Green
}

# ---------------------------------------------------------------------------------------------
# Step 3: Create the policy, the provisioner and the probe user
# ---------------------------------------------------------------------------------------------
Write-TestStep "Step 3" "Creating ou=Policies, the default policy, the provisioner and the probe user (data admin)"

$probeUid = if ($ProbeBindDN -match '^uid=([^,]+)') { $matches[1] } else { throw "ProbeBindDN must start with uid=" }
$provisionerCn = if ($ProvisionerBindDN -match '^cn=([^,]+)') { $matches[1] } else { throw "ProvisionerBindDN must start with cn=" }

# The probe user is recreated from scratch on every run. Its previous life may hold pwdHistory values
# stamped to the second, and the scenario's negative control and compliant change follow within
# seconds of this script; a history value identical to one the overlay is about to write is refused
# with "Type or value exists (20)", which would read as the channel failing. "No such object (32)" on
# the delete is the first run.
$deleteResult = & docker exec $containerName ldapdelete -x -H $ldapUri -D $dataAdminDN -w $dataAdminPassword $ProbeBindDN 2>&1
if ($LASTEXITCODE -ne 0 -and "$deleteResult" -notmatch "No such object") {
    throw "ldapdelete failed (probe user reset) (exit code $LASTEXITCODE): $deleteResult"
}

# The data admin is the rootdn and so is exempt from the policy; that is what lets these entries be
# created with whatever password the fixture needs, and why nothing below proves enforcement.
Invoke-Scenario22Ldap -Tool ldapadd -BindDN $dataAdminDN -BindPassword $dataAdminPassword -What "fixture entries" -Ldif @"
dn: ou=Policies,$suffix
objectClass: organizationalUnit
ou: Policies

dn: $policyDN
objectClass: applicationProcess
objectClass: pwdPolicy
cn: default
description: Scenario 22 default password policy (minimum length 12, history 5, maximum age 90 days)
pwdAttribute: userPassword
pwdMinLength: 12
pwdInHistory: 5
pwdMaxAge: 7776000
pwdCheckQuality: 2

dn: $ProvisionerBindDN
objectClass: organizationalRole
objectClass: simpleSecurityObject
cn: $provisionerCn
description: The account JIM binds as in Scenario 22; not the rootdn, so the password policy applies to what it writes
userPassword: $ProvisionerPassword

dn: $ProbeBindDN
objectClass: inetOrgPerson
uid: $probeUid
cn: Scenario 22 Probe
sn: Probe
givenName: Scenario
description: Changes its own password in Scenario 22's negative control; outside ou=People so the Scenario 1 substrate never imports it
userPassword: $ProbePassword

"@
Write-Host "  OK ou=Policies, $policyDN, $ProvisionerBindDN and $ProbeBindDN present" -ForegroundColor Green

# The lab's base image (docker/openldap/scripts/01-add-second-suffix.sh, #1715) now creates its own
# cn=default,ou=Policies,<suffix> entry on every container (pwdMinLength 7, matching the Samba domain
# minimum), so the ldapadd above tolerates "already exists" on $policyDN and moves on without touching
# it, leaving the lab's baseline values in place rather than this fixture's stricter ones. Force this
# fixture's values with an idempotent replace, the same pattern Step 2 already uses for
# olcPPolicyDefault: harmless (a no-op) on a genuinely fresh entry the add just created, and the fix on
# a directory that already carried the lab's own default policy.
Invoke-Scenario22Ldap -Tool ldapmodify -BindDN $dataAdminDN -BindPassword $dataAdminPassword -What "policy values (idempotent)" -Ldif @"
dn: $policyDN
changetype: modify
replace: description
description: Scenario 22 default password policy (minimum length 12, history 5, maximum age 90 days)
-
replace: pwdMinLength
pwdMinLength: 12
-
replace: pwdInHistory
pwdInHistory: 5
-
replace: pwdMaxAge
pwdMaxAge: 7776000
-
replace: pwdCheckQuality
pwdCheckQuality: 2
"@
Write-Host "  OK $policyDN carries Scenario 22's values (pwdMinLength 12, pwdInHistory 5, pwdMaxAge 7776000, pwdCheckQuality 2)" -ForegroundColor Green

# Reset the provisioner's password to its known value so a re-run still starts from the same place (the
# probe user was recreated above, so it needs no reset). The rootdn is exempt from pwdInHistory, so
# the reset cannot be refused; the overlay does record it in pwdHistory, which is harmless here because
# nothing changes the provisioner's password again.
Invoke-Scenario22Ldap -Tool ldapmodify -BindDN $dataAdminDN -BindPassword $dataAdminPassword -What "provisioner password reset" -Ldif @"
dn: $ProvisionerBindDN
changetype: modify
replace: userPassword
userPassword: $ProvisionerPassword
"@
Write-Host "  OK Provisioner password set to its known value; probe user recreated with its known password" -ForegroundColor Green

# ---------------------------------------------------------------------------------------------
# Step 4: Prove the fixture from the provisioner's side
# ---------------------------------------------------------------------------------------------
Write-TestStep "Step 4" "Verifying the provisioner can bind and read the policy"

$whoami = & docker exec $containerName ldapwhoami -x -H $ldapUri -D $ProvisionerBindDN -w $ProvisionerPassword 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "The provisioner cannot bind as $ProvisionerBindDN`: $whoami. If the answer is Invalid credentials (49) " +
          "and the password is right, the database's ACL ends in a rule that falls through to a deny; read the " +
          "olcAccess on $databaseDN and check the explicit default read rule is its last value."
}

$probeWhoami = & docker exec $containerName ldapwhoami -x -H $ldapUri -D $ProbeBindDN -w $ProbePassword 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "The probe user cannot bind as $ProbeBindDN`: $probeWhoami. The negative control changes this account's own " +
          "password, so a bind that fails here would read as a refusal there."
}

$policyRead = & docker exec $containerName ldapsearch -x -LLL -H $ldapUri -D $ProvisionerBindDN -w $ProvisionerPassword `
    -b $policyDN -s base "(objectClass=pwdPolicy)" pwdMinLength 2>&1
if ($LASTEXITCODE -ne 0 -or (($policyRead -join "`n") -notmatch 'pwdMinLength:\s*12')) {
    throw "The provisioner cannot read pwdMinLength on $policyDN (exit code $LASTEXITCODE): $policyRead"
}
Write-Host "  OK $ProvisionerBindDN and $ProbeBindDN bind; the provisioner reads pwdMinLength 12 on $policyDN" -ForegroundColor Green

Write-Host "  olcAccess on $databaseDN, in order:" -ForegroundColor Gray
$finalRules = @(Get-Scenario22AccessRules -DatabaseDN $databaseDN)
foreach ($rule in $finalRules) { Write-Host "    $rule" -ForegroundColor DarkGray }

Write-TestSection "Scenario 22 OpenLDAP Population Complete"
Write-Host "Suffix:          $suffix" -ForegroundColor Cyan
Write-Host "Default policy:  $policyDN (pwdMinLength 12, pwdInHistory 5, pwdMaxAge 7776000, pwdCheckQuality 2)" -ForegroundColor Cyan
Write-Host "Provisioner:     $ProvisionerBindDN (write on the database, narrow read on cn=config)" -ForegroundColor Cyan
Write-Host "Probe user:      $ProbeBindDN" -ForegroundColor Cyan
Write-Host ""
Write-Host "Scenario 22 OpenLDAP population complete" -ForegroundColor Green
