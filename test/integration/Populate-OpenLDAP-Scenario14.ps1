# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Populate OpenLDAP with test data for Scenario 14: Attribute Priority

.DESCRIPTION
    Creates the same six users, by Employee ID, in both suffixes of the OpenLDAP container
    (Yellowstone/Primary and Glitterband/Secondary; see
    docker/openldap/scripts/01-add-second-suffix.sh). Sharing Employee ID across suffixes is
    what makes the two Connected Systems configured by Setup-Scenario14.ps1 join to a single
    Metaverse Object per user rather than projecting two.

    Deliberately differs between Primary and Secondary, per user, so Attribute Priority
    resolution is observable:
    - Description and Job Title: distinct per-suffix strings
    - Manager: a reference to a different co-worker in each suffix (rotation offset 1 in
      Primary, offset 3 in Secondary; both non-zero and distinct mod 6, so no user's manager
      matches between suffixes). Every manager target's Employee ID exists in both suffixes,
      so the reference still resolves to a single joined Metaverse Object either way.
    - Other Telephones (telephoneNumber, multi-valued): two numbers per user, drawn from
      disjoint per-suffix ranges, so winner-takes-all-values is directly checkable.

    Identical between Primary and Secondary (identity plumbing, not contested): Account Name,
    First Name, Last Name, Display Name. Email differs only in domain (suffix-specific), which
    is a useful bonus check that the winner's domain, not just its local part, comes through.

    Fixed six-user set (no template scaling): this scenario tests attribute contribution logic,
    not import throughput.

.PARAMETER Container
    The Docker container name running OpenLDAP (default: openldap-primary).

.EXAMPLE
    ./Populate-OpenLDAP-Scenario14.ps1
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$Container = "openldap-primary"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Scenario 14: Populating OpenLDAP (Primary + Secondary) with Attribute Priority test data"

$containerName = $Container
$ldapUri = "ldap://localhost:1389"

$configMap = @{
    Primary = @{
        Suffix   = "dc=yellowstone,dc=local"
        AdminDN  = "cn=admin,dc=yellowstone,dc=local"
        Password = "Test@123!"
        PeopleOU = "ou=People,dc=yellowstone,dc=local"
        Domain   = "yellowstone.local"
    }
    Secondary = @{
        Suffix   = "dc=glitterband,dc=local"
        AdminDN  = "cn=admin,dc=glitterband,dc=local"
        Password = "Test@123!"
        PeopleOU = "ou=People,dc=glitterband,dc=local"
        Domain   = "glitterband.local"
    }
}

# Fixed, deterministic person set shared by both suffixes (same Employee ID each side, so they
# join to the same Metaverse Object).
$people = @(
    @{ Index = 0; FirstName = "Alice"; LastName = "Anderson" }
    @{ Index = 1; FirstName = "Bob";   LastName = "Baker" }
    @{ Index = 2; FirstName = "Carol"; LastName = "Clarke" }
    @{ Index = 3; FirstName = "Dave";  LastName = "Dixon" }
    @{ Index = 4; FirstName = "Erin";  LastName = "Ellis" }
    @{ Index = 5; FirstName = "Frank"; LastName = "Foster" }
)
$userCount = $people.Count

$jobTitles = @("Engineer", "Manager", "Analyst", "Coordinator", "Consultant", "Architect")

function Get-Scenario14Uid {
    param([int]$Index)
    return "$($people[$Index].FirstName.ToLower())14"
}

foreach ($role in @("Primary", "Secondary")) {
    $config = $configMap[$role]
    # Manager rotation offset: non-zero and distinct between Primary (1) and Secondary (3) mod
    # $userCount, so every user's manager target differs between suffixes.
    $managerOffset = if ($role -eq "Primary") { 1 } else { 3 }
    # Telephone number prefix: disjoint per-suffix ranges so winner-takes-all-values is checkable.
    $phonePrefix = if ($role -eq "Primary") { "10" } else { "20" }

    Write-TestStep "Step ($role)" "Creating $userCount users in $($config.Suffix)"

    $ldifBuilder = [System.Text.StringBuilder]::new()
    foreach ($person in $people) {
        $i = $person.Index
        $uid = Get-Scenario14Uid -Index $i
        $displayName = "$($person.FirstName) $($person.LastName) (S14)"
        $employeeNumber = "S14-$i"
        $managerUid = Get-Scenario14Uid -Index (($i + $managerOffset) % $userCount)
        $managerDn = "uid=$managerUid,$($config.PeopleOU)"
        $jobTitle = "$($jobTitles[$i % $jobTitles.Count]) ($role)"
        $description = "$role-sourced description for $displayName"
        $mail = "$uid@$($config.Domain)"
        $dn = "uid=$uid,$($config.PeopleOU)"
        $phone1 = "+44 20 7946 $phonePrefix$($i)0"
        $phone2 = "+44 20 7946 $phonePrefix$($i)1"

        [void]$ldifBuilder.AppendLine("dn: $dn")
        [void]$ldifBuilder.AppendLine("objectClass: inetOrgPerson")
        [void]$ldifBuilder.AppendLine("uid: $uid")
        [void]$ldifBuilder.AppendLine("cn: $displayName")
        [void]$ldifBuilder.AppendLine("sn: $($person.LastName)")
        [void]$ldifBuilder.AppendLine("givenName: $($person.FirstName)")
        [void]$ldifBuilder.AppendLine("displayName: $displayName")
        [void]$ldifBuilder.AppendLine("mail: $mail")
        [void]$ldifBuilder.AppendLine("employeeNumber: $employeeNumber")
        [void]$ldifBuilder.AppendLine("description: $description")
        [void]$ldifBuilder.AppendLine("title: $jobTitle")
        [void]$ldifBuilder.AppendLine("manager: $managerDn")
        [void]$ldifBuilder.AppendLine("telephoneNumber: $phone1")
        [void]$ldifBuilder.AppendLine("telephoneNumber: $phone2")
        [void]$ldifBuilder.AppendLine("userPassword: Test@123!")
        [void]$ldifBuilder.AppendLine("")
    }

    $ldifContent = $ldifBuilder.ToString()
    $ldifPath = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $ldifPath -Value $ldifContent -NoNewline

    try {
        $result = bash -c "cat '$ldifPath' | docker exec -i $containerName ldapadd -x -H $ldapUri -D '$($config.AdminDN)' -w '$($config.Password)' -c" 2>&1
        if ($LASTEXITCODE -ne 0 -and "$result" -notmatch "already exists") {
            throw "Failed to import $role users (exit code $LASTEXITCODE): $result"
        }
    }
    finally {
        Remove-Item -Path $ldifPath -Force -ErrorAction SilentlyContinue
    }

    Write-Host "  OK Created $userCount users in $role ($($config.Suffix))" -ForegroundColor Green
}

Write-TestSection "Scenario 14 OpenLDAP Population Complete"
Write-Host "Users per suffix: $userCount" -ForegroundColor Cyan
Write-Host "Primary suffix:   $($configMap.Primary.Suffix)" -ForegroundColor Cyan
Write-Host "Secondary suffix: $($configMap.Secondary.Suffix)" -ForegroundColor Cyan
Write-Host ""
Write-Host "Scenario 14 OpenLDAP population complete" -ForegroundColor Green
