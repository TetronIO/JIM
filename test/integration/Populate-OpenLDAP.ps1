<#
.SYNOPSIS
    Populate OpenLDAP with test data across two suffixes

.DESCRIPTION
    Creates users and groups in both OpenLDAP suffixes (dc=yellowstone,dc=local
    and dc=glitterband,dc=local) for multi-partition testing (Issue #72, Phase 1b).

    Each suffix gets distinct users so Scenario 9 can assert that partition-scoped
    import only returns objects from the targeted partition.

    Users are split: odd indices go to Yellowstone, even indices go to Glitterband.

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, MediumLarge, Large, XLarge, XXLarge)

.EXAMPLE
    ./Populate-OpenLDAP.ps1 -Template Micro
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Populating OpenLDAP with $Template template"

# Get scale for template
$scale = Get-TemplateScale -Template $Template

# OpenLDAP configuration
$container = "openldap-primary"
$ldapPort = 1389
$ldapUri = "ldap://localhost:$ldapPort"
$adminPassword = "Test@123!"

$suffixes = @{
    Yellowstone = @{
        Suffix   = "dc=yellowstone,dc=local"
        AdminDN  = "cn=admin,dc=yellowstone,dc=local"
        PeopleDN = "ou=People,dc=yellowstone,dc=local"
        GroupsDN = "ou=Groups,dc=yellowstone,dc=local"
        Domain   = "yellowstone.local"
    }
    Glitterband = @{
        Suffix   = "dc=glitterband,dc=local"
        AdminDN  = "cn=admin,dc=glitterband,dc=local"
        PeopleDN = "ou=People,dc=glitterband,dc=local"
        GroupsDN = "ou=Groups,dc=glitterband,dc=local"
        Domain   = "glitterband.local"
    }
}

# Split users between suffixes: odd indices -> Yellowstone, even indices -> Glitterband
$yellowstoneUserCount = [Math]::Ceiling($scale.Users / 2)
$glitterbandUserCount = [Math]::Floor($scale.Users / 2)
# Groups are split the same way (at least 1 per suffix)
$yellowstoneGroupCount = [Math]::Max(1, [Math]::Ceiling($scale.Groups / 2))
$glitterbandGroupCount = [Math]::Max(1, [Math]::Floor($scale.Groups / 2))

Write-Host "Container:            $container" -ForegroundColor Gray
Write-Host "Total users:          $($scale.Users) (Yellowstone: $yellowstoneUserCount, Glitterband: $glitterbandUserCount)" -ForegroundColor Gray
Write-Host "Total groups:         $($scale.Groups) (Yellowstone: $yellowstoneGroupCount, Glitterband: $glitterbandGroupCount)" -ForegroundColor Gray

$departments = @("IT", "HR", "Sales", "Finance", "Operations", "Marketing", "Legal", "Engineering", "Support", "Admin")
$titles = @("Manager", "Director", "Analyst", "Specialist", "Coordinator", "Administrator", "Engineer", "Developer", "Consultant", "Associate")

function Import-LdifToOpenLDAP {
    <#
    .SYNOPSIS
        Write LDIF content to a temp file, copy into the container, and load via ldapadd
    #>
    param(
        [string]$LdifContent,
        [string]$AdminDN,
        [string]$Password,
        [string]$Description
    )

    $ldifPath = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($ldifPath, $LdifContent)
        $ldifSizeKB = [Math]::Round((Get-Item $ldifPath).Length / 1024, 1)
        Write-Host "    LDIF: $ldifSizeKB KB — $Description" -ForegroundColor Gray

        # Pipe LDIF via stdin — docker cp creates root-owned files that uid 1001 can't read
        # Use cmd to pipe raw bytes to avoid PowerShell encoding issues
        $result = bash -c "cat '$ldifPath' | docker exec -i $container ldapadd -x -H $ldapUri -D '$AdminDN' -w '$Password' -c" 2>&1
        $exitCode = $LASTEXITCODE

        $resultText = if ($result -is [array]) { $result -join "`n" } else { "$result" }

        if ($exitCode -ne 0) {
            if ($resultText -match "Already exists") {
                Write-Host "    Some entries already exist (idempotent)" -ForegroundColor Yellow
            }
            else {
                throw "ldapadd failed (exit $exitCode): $resultText"
            }
        }
    }
    finally {
        Remove-Item $ldifPath -Force -ErrorAction SilentlyContinue
    }
}

function New-OpenLDAPUserLdif {
    <#
    .SYNOPSIS
        Generate an inetOrgPerson LDIF entry with explicit LF line endings
    #>
    param(
        [hashtable]$User,
        [string]$PeopleDN,
        [string]$Domain
    )

    $uid = "$($User.FirstName.ToLower()).$($User.LastName.ToLower())$($User.Index)"
    $dn = "uid=$uid,$PeopleDN"
    $lf = "`n"

    $ldif = "dn: $dn" + $lf +
            "objectClass: inetOrgPerson" + $lf +
            "objectClass: organizationalPerson" + $lf +
            "objectClass: person" + $lf +
            "objectClass: top" + $lf +
            "uid: $uid" + $lf +
            "cn: $($User.DisplayName)" + $lf +
            "sn: $($User.LastName)" + $lf +
            "givenName: $($User.FirstName)" + $lf +
            "displayName: $($User.DisplayName)" + $lf +
            "mail: $uid@$Domain" + $lf +
            "title: $($User.Title)" + $lf +
            "departmentNumber: $($User.Department)" + $lf +
            "userPassword: Test@123!" + $lf + $lf

    return @{
        Ldif = $ldif
        Uid  = $uid
        DN   = $dn
        Department = $User.Department
    }
}

# Populate each suffix
foreach ($suffixName in @("Yellowstone", "Glitterband")) {
    $config = $suffixes[$suffixName]
    $userCount = if ($suffixName -eq "Yellowstone") { $yellowstoneUserCount } else { $glitterbandUserCount }
    $groupCount = if ($suffixName -eq "Yellowstone") { $yellowstoneGroupCount } else { $glitterbandGroupCount }
    # Offset indices by 500,000 to avoid uid collisions with CSV-generated users.
    # The CSV generator uses indices 0..N, so seeded OpenLDAP users at index 500,001+
    # will never produce the same uid (e.g., alice.smith1 vs alice.smith500001).
    # This matches the Samba AD approach in Populate-SambaAD.ps1 ($adIndexOffset = 500000)
    # and scales across all templates (XXLarge = 200K users).
    # Within the offset range, Yellowstone and Glitterband get distinct index ranges
    # so users are unique across suffixes.
    $ldapIndexOffset = 500000
    $indexStart = if ($suffixName -eq "Yellowstone") { $ldapIndexOffset + 1 } else { $ldapIndexOffset + $yellowstoneUserCount + 1 }

    Write-TestSection "Populating $suffixName ($($config.Suffix))"
    Write-Host "  Users: $userCount, Groups: $groupCount" -ForegroundColor Gray

    # Step 1: Create users
    Write-TestStep "Step 1" "Creating $userCount users in $suffixName"

    $nameData = Get-TestNameData
    $firstNames = $nameData.FirstNames
    $lastNames = $nameData.LastNames

    $ldifBuilder = [System.Text.StringBuilder]::new()
    $ldifChunkSize = 5000
    $totalAdded = 0
    $chunkIndex = 0
    $createdUsers = @()

    for ($i = 0; $i -lt $userCount; $i++) {
        $index = $indexStart + $i
        $firstNameIndex = $index % $firstNames.Count
        $lastNameIndex = ($index * 97) % $lastNames.Count

        $firstName = $firstNames[$firstNameIndex]
        $lastName = $lastNames[$lastNameIndex]
        $department = $departments[$index % $departments.Length]
        $title = $titles[$index % $titles.Length]

        $displayName = if ($index -ge $firstNames.Count) {
            "$firstName $lastName ($index)"
        } else {
            "$firstName $lastName"
        }

        $user = @{
            Index       = $index
            FirstName   = $firstName
            LastName    = $lastName
            DisplayName = $displayName
            Department  = $department
            Title       = $title
        }

        $entry = New-OpenLDAPUserLdif -User $user -PeopleDN $config.PeopleDN -Domain $config.Domain
        [void]$ldifBuilder.Append($entry.Ldif)
        $createdUsers += $entry

        # Import in chunks
        if ((($i + 1) % $ldifChunkSize -eq 0) -or ($i -eq $userCount - 1)) {
            $chunkIndex++
            $chunkCount = if (($i + 1) % $ldifChunkSize -eq 0) { $ldifChunkSize } else { ($i + 1) % $ldifChunkSize }
            if ($chunkCount -eq 0) { $chunkCount = $ldifChunkSize }

            Import-LdifToOpenLDAP -LdifContent $ldifBuilder.ToString() `
                -AdminDN $config.AdminDN -Password $adminPassword `
                -Description "chunk $chunkIndex ($chunkCount users, total $($i + 1)/$userCount)"

            $totalAdded += $chunkCount
            $ldifBuilder.Clear() | Out-Null
        }
    }

    Write-Host "  Created $totalAdded users in $suffixName" -ForegroundColor Green

    # Step 2: Create groups with initial members
    # groupOfNames requires at least one member (MUST attribute), so we assign
    # the first matching department user as the initial member during creation.
    Write-TestStep "Step 2" "Creating $groupCount groups in $suffixName"

    $groupLdifBuilder = [System.Text.StringBuilder]::new()
    $createdGroups = @()

    for ($g = 0; $g -lt $groupCount; $g++) {
        $dept = $departments[$g % $departments.Length]
        $groupName = "Group-$dept-$($g + 1)"

        # Find a member from this department (required for groupOfNames)
        $deptMembers = @($createdUsers | Where-Object { $_.Department -eq $dept })
        if ($deptMembers.Count -eq 0) {
            # Fallback: use the first user
            $deptMembers = @($createdUsers[0])
        }
        $initialMember = $deptMembers[0]

        $dn = "cn=$groupName,$($config.GroupsDN)"

        [void]$groupLdifBuilder.AppendLine("dn: $dn")
        [void]$groupLdifBuilder.AppendLine("objectClass: groupOfNames")
        [void]$groupLdifBuilder.AppendLine("cn: $groupName")
        [void]$groupLdifBuilder.AppendLine("description: $dept department group for $suffixName")
        [void]$groupLdifBuilder.AppendLine("member: $($initialMember.DN)")
        [void]$groupLdifBuilder.AppendLine("")

        $createdGroups += @{
            Name       = $groupName
            DN         = $dn
            Department = $dept
            Members    = @($initialMember.DN)
        }
    }

    if ($groupCount -gt 0) {
        Import-LdifToOpenLDAP -LdifContent $groupLdifBuilder.ToString() `
            -AdminDN $config.AdminDN -Password $adminPassword `
            -Description "$groupCount groups"
    }

    Write-Host "  Created $groupCount groups in $suffixName" -ForegroundColor Green

    # Step 3: Add additional group memberships via ldapmodify
    Write-TestStep "Step 3" "Adding group memberships (avg: $($scale.AvgMemberships) per user)"

    $totalMemberships = 0

    foreach ($group in $createdGroups) {
        # Find users in the same department
        $deptUsers = @($createdUsers | Where-Object { $_.Department -eq $group.Department })

        # Select up to AvgMemberships users (skip the initial member already added)
        $numToAdd = [Math]::Min($scale.AvgMemberships, $deptUsers.Count) - 1
        if ($numToAdd -le 0) { continue }

        $additionalMembers = @($deptUsers | Select-Object -Skip 1 -First $numToAdd)

        if ($additionalMembers.Count -eq 0) { continue }

        # Build ldapmodify LDIF to add members
        $modifyLdif = [System.Text.StringBuilder]::new()
        foreach ($member in $additionalMembers) {
            [void]$modifyLdif.AppendLine("dn: $($group.DN)")
            [void]$modifyLdif.AppendLine("changetype: modify")
            [void]$modifyLdif.AppendLine("add: member")
            [void]$modifyLdif.AppendLine("member: $($member.DN)")
            [void]$modifyLdif.AppendLine("")
        }

        # Write and execute
        $modLdifPath = [System.IO.Path]::GetTempFileName()
        try {
            [System.IO.File]::WriteAllText($modLdifPath, $modifyLdif.ToString())
            $result = bash -c "cat '$modLdifPath' | docker exec -i $container ldapmodify -x -H $ldapUri -D '$($config.AdminDN)' -w '$adminPassword' -c" 2>&1

            $totalMemberships += $additionalMembers.Count
        }
        finally {
            Remove-Item $modLdifPath -Force -ErrorAction SilentlyContinue
        }
    }

    # Count initial members too
    $totalMemberships += $createdGroups.Count

    Write-Host "  Added $totalMemberships total memberships across $groupCount groups in $suffixName" -ForegroundColor Green
}

# Summary
Write-TestSection "OpenLDAP Population Summary"
Write-Host "Template:              $Template" -ForegroundColor Cyan
Write-Host "Yellowstone users:     $yellowstoneUserCount" -ForegroundColor Cyan
Write-Host "Glitterband users:     $glitterbandUserCount" -ForegroundColor Cyan
Write-Host "Yellowstone groups:    $yellowstoneGroupCount" -ForegroundColor Cyan
Write-Host "Glitterband groups:    $glitterbandGroupCount" -ForegroundColor Cyan
Write-Host ""
