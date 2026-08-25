# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Populate OpenLDAP with test data for Scenario 19: Auxiliary Classes

.DESCRIPTION
    Seeds the two suffixes of the OpenLDAP container (Yellowstone/Source and Glitterband/Target;
    see docker/openldap/scripts/01-add-second-suffix.sh) for auxiliary class testing (#492).
    Sharing Employee ID across suffixes is what joins each pair to a single Metaverse Object,
    following Scenario 14's two-suffix shape.

    Yellowstone (Source) gets six jimPerson users:
    - Three badge carriers (indices 0-2) additionally carry the JIM-owned jimBadgeHolder
      auxiliary class with jimBadgeNumber (its MUST) and jimBadgeColour. Boris (index 1) lists
      jimBadgeHolder BEFORE jimPerson in his objectClass values, so the import-side
      one-CSO-per-entry assertion is exercised against both orderings a real directory can serve.
    - Dora (index 3) carries no badge class in either suffix; her GLITTERBAND entry carries
      roomNumber, which the MustEnforcement step later flows into the Badge Colour Metaverse
      attribute as a second-system contributor (the cross-system priority fallback Scenario 14
      proves): colour without a badge number is what makes her export refusable.
    - Elena and Felix (indices 4-5) are plain jimPerson controls carrying nothing badge-related.

    Glitterband (Target) gets the six counterpart entries as plain jimPerson: no auxiliary class
    and no badge attributes (only Dora's roomNumber), so the export steps can observe JIM adding
    the class per entry.

    Gina (S19-6), the carrier-provisioning subject, is deliberately NOT seeded here; the
    CarrierProvisioning step adds her to Yellowstone itself so earlier steps' counts hold.

    Fixed six-user set (no template scaling): this scenario tests class composition logic, not
    import throughput.

.PARAMETER Container
    The Docker container name running OpenLDAP (default: openldap-primary).

.EXAMPLE
    ./Populate-OpenLDAP-Scenario19.ps1
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$Container = "openldap-primary"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Scenario 19: Populating OpenLDAP (Source + Target) with Auxiliary Class test data"

$containerName = $Container
$ldapUri = "ldap://localhost:1389"

$configMap = @{
    Source = @{
        Suffix   = "dc=yellowstone,dc=local"
        AdminDN  = "cn=admin,dc=yellowstone,dc=local"
        Password = "Test@123!"
        PeopleOU = "ou=People,dc=yellowstone,dc=local"
        Domain   = "yellowstone.local"
    }
    Target = @{
        Suffix   = "dc=glitterband,dc=local"
        AdminDN  = "cn=admin,dc=glitterband,dc=local"
        Password = "Test@123!"
        PeopleOU = "ou=People,dc=glitterband,dc=local"
        Domain   = "glitterband.local"
    }
}

# Fixed, deterministic person set. Same Employee ID in both suffixes, so each pair joins to one
# Metaverse Object. BadgeColour non-null marks a Yellowstone badge carrier; RoomNumber (seeded in
# the TARGET suffix) gives the MustEnforcement subject a colour source contributed by the second
# system, needing no badge class on either of her entries.
$people = @(
    @{ Index = 0; FirstName = "Amber"; LastName = "Archer"; BadgeColour = "Blue";  RoomNumber = $null }
    @{ Index = 1; FirstName = "Boris"; LastName = "Blake";  BadgeColour = "Green"; RoomNumber = $null }
    @{ Index = 2; FirstName = "Clara"; LastName = "Cross";  BadgeColour = "Red";   RoomNumber = $null }
    @{ Index = 3; FirstName = "Dora";  LastName = "Dean";   BadgeColour = $null;   RoomNumber = "Yellow" }
    @{ Index = 4; FirstName = "Elena"; LastName = "East";   BadgeColour = $null;   RoomNumber = $null }
    @{ Index = 5; FirstName = "Felix"; LastName = "Ford";   BadgeColour = $null;   RoomNumber = $null }
)
$userCount = $people.Count

function Get-Scenario19Uid {
    param([hashtable]$Person)
    return "$($Person.FirstName.ToLower())19"
}

foreach ($role in @("Source", "Target")) {
    $config = $configMap[$role]

    Write-TestStep "Step ($role)" "Creating $userCount users in $($config.Suffix)"

    $ldifBuilder = [System.Text.StringBuilder]::new()
    foreach ($person in $people) {
        $i = $person.Index
        $uid = Get-Scenario19Uid -Person $person
        $displayName = "$($person.FirstName) $($person.LastName) (S19)"
        $employeeNumber = "S19-$i"
        $mail = "$uid@$($config.Domain)"
        $dn = "uid=$uid,$($config.PeopleOU)"
        $isCarrier = ($role -eq "Source" -and $null -ne $person.BadgeColour)

        [void]$ldifBuilder.AppendLine("dn: $dn")
        if ($isCarrier -and $i -eq 1) {
            # Boris lists the auxiliary class FIRST: the one-CSO-per-entry criterion (#492
            # Phase 6) must hold whatever order the directory serves objectClass values in.
            [void]$ldifBuilder.AppendLine("objectClass: jimBadgeHolder")
            [void]$ldifBuilder.AppendLine("objectClass: jimPerson")
        }
        elseif ($isCarrier) {
            [void]$ldifBuilder.AppendLine("objectClass: jimPerson")
            [void]$ldifBuilder.AppendLine("objectClass: jimBadgeHolder")
        }
        else {
            [void]$ldifBuilder.AppendLine("objectClass: jimPerson")
        }
        [void]$ldifBuilder.AppendLine("uid: $uid")
        [void]$ldifBuilder.AppendLine("cn: $displayName")
        [void]$ldifBuilder.AppendLine("sn: $($person.LastName)")
        [void]$ldifBuilder.AppendLine("givenName: $($person.FirstName)")
        [void]$ldifBuilder.AppendLine("displayName: $displayName")
        [void]$ldifBuilder.AppendLine("mail: $mail")
        [void]$ldifBuilder.AppendLine("employeeNumber: $employeeNumber")
        if ($isCarrier) {
            [void]$ldifBuilder.AppendLine("jimBadgeNumber: B19-$i")
            [void]$ldifBuilder.AppendLine("jimBadgeColour: $($person.BadgeColour)")
        }
        if ($role -eq "Target" -and $null -ne $person.RoomNumber) {
            [void]$ldifBuilder.AppendLine("roomNumber: $($person.RoomNumber)")
        }
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

Write-TestSection "Scenario 19 OpenLDAP Population Complete"
Write-Host "Users per suffix: $userCount (badge carriers in Source: 3)" -ForegroundColor Cyan
Write-Host "Source suffix: $($configMap.Source.Suffix)" -ForegroundColor Cyan
Write-Host "Target suffix: $($configMap.Target.Suffix)" -ForegroundColor Cyan
Write-Host ""
Write-Host "Scenario 19 OpenLDAP population complete" -ForegroundColor Green
