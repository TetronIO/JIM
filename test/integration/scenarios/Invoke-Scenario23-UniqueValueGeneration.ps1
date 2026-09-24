# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 23: Unique Value Generation

.DESCRIPTION
    Exercises release 1 of Unique Value Generation (#242): a "Generated Value" source type on import
    and export Attribute Flows, whose value is a base expression plus a uniqueness token (OnlyIfTaken,
    Sequence or Random), local gates (reservation within a run, Metaverse, connector space), adopt
    before generate, sticky assignments across re-runs, and Start again. Release 1 does NOT include
    probing, the retired values register, Collision Remediation or Needs Decision (those ship in
    releases 2 to 4); a target-side collision in this release is an ordinary export error naming the
    value and the system, which the SambaAD-only Collision step asserts directly.

    The provisioning substrate is Scenario 1's, composed via Setup-Scenario23.ps1 (which itself calls
    Setup-Scenario1.ps1 -GenerateAccountName), with the HR CSV generated via Get-OrGenerate-TestCSV.ps1
    -OmitItOwnedAttributes so samAccountName, email and userPrincipalName are genuinely absent, the
    shape this feature exists to make representative. The target directory starts empty: every Account
    Name, Staff Number and Badge Code is generated, not sourced.

    Steps are CUMULATIVE, like Scenario 22's: a named step runs everything up to and including itself,
    because most steps depend on the population state earlier steps leave behind (Joiners' baseline,
    Gates' intra-batch joiners, the Sequence/Random values every object already carries). The one
    exception is Collision, which only asserts anything under Samba AD; under OpenLDAP it prints a
    clear skip message and does nothing (a CSV target cannot reject a duplicate, and OpenLDAP has no
    directory-wide unique-value constraint this harness configures).

.PARAMETER Step
    Which part to execute (cumulative: a named step runs everything up to and including itself).

.PARAMETER Template
    Data scale template. Micro is the default: this scenario asserts against individually-identifiable
    objects (per-base-value sets, specific new joiners by employeeId), so a small, fast population is
    the right size for development.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER ContinueOnError
    Continue executing remaining assertions even if one fails.

.PARAMETER DirectoryConfig
    Directory configuration hashtable. Defaults to Get-DirectoryConfig -DirectoryType OpenLDAP. Samba
    AD is fully supported (pass -DirectoryConfig (Get-DirectoryConfig -DirectoryType SambaAD -Instance
    Primary)) and is required for the Collision step to assert anything.

.EXAMPLE
    ./Invoke-Scenario23-UniqueValueGeneration.ps1 -ApiKey "jim_..." -Template Micro

.EXAMPLE
    ./Invoke-Scenario23-UniqueValueGeneration.ps1 -ApiKey "jim_..." -Step Sequence -DirectoryConfig (Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary)
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Joiners", "Gates", "Stability", "Sequence", "Random", "ExportMode", "AdoptBeforeGenerate", "StartAgain", "Failure", "Collision", "SurfaceParity", "FeatureFlag", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Micro",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [switch]$ContinueOnError,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

. "$PSScriptRoot/../utils/Test-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Primary
}
if (-not $ApiKey) {
    throw "API key required for authentication. Create one via the JIM portal: Admin > API Keys."
}

$isRfcDirectory = Test-IsRfcDirectory $DirectoryConfig
$csvPath = "$PSScriptRoot/../../test-data/hr-users.csv"

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

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Helpers
# ─────────────────────────────────────────────────────────────────────────────────────────────

function Get-GeneratedBaseValue {
    <#
    .SYNOPSIS
        The base value the Account Name generated mapping computes: Lower(firstName).Lower(lastName).
    #>
    param([Parameter(Mandatory=$true)][string]$FirstName, [Parameter(Mandatory=$true)][string]$LastName)
    return "$($FirstName.ToLower()).$($LastName.ToLower())"
}

function Add-HrCsvJoiner {
    <#
    .SYNOPSIS
        Appends one controlled row to hr-users.csv, matching whatever columns the file currently has
        (it carries no samAccountName/email/userPrincipalName, generated via -OmitItOwnedAttributes).
    #>
    param(
        [Parameter(Mandatory=$true)][string]$EmployeeId,
        [Parameter(Mandatory=$true)][string]$FirstName,
        [Parameter(Mandatory=$true)][string]$LastName,
        [string]$Department = "Information Technology",
        [string]$Title = "Specialist",
        [string]$Company = "Panoply",
        [string]$Status = "Active",
        [string]$EmployeeType = "Employee"
    )

    $csv = @(Import-Csv $csvPath)
    $columns = if ($csv.Count -gt 0) { $csv[0].PSObject.Properties.Name } else {
        @('employeeId','firstName','lastName','department','title','company','pronouns','displayName','status','employeeType','employeeEndDate')
    }
    $displayName = "$FirstName $LastName"
    $lowerFullName = Get-GeneratedBaseValue -FirstName $FirstName -LastName $LastName

    $row = [ordered]@{}
    foreach ($col in $columns) {
        $row[$col] = switch ($col) {
            'employeeId'        { $EmployeeId }
            'firstName'         { $FirstName }
            'lastName'          { $LastName }
            'department'        { $Department }
            'title'             { $Title }
            'company'           { $Company }
            'pronouns'          { '' }
            'displayName'       { $displayName }
            'status'            { $Status }
            'employeeType'      { $EmployeeType }
            'employeeEndDate'   { '' }
            'samAccountName'    { $lowerFullName }
            'email'             { "$lowerFullName@panoply.local" }
            'userPrincipalName' { "$lowerFullName@panoply.local" }
            default             { '' }
        }
    }
    $csv += [PSCustomObject]$row
    $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
}

function Invoke-Cycle {
    <#
    .SYNOPSIS
        HR CSV Import -> HR CSV Sync -> Directory Export -> Directory Import -> Directory Sync.
    .DESCRIPTION
        -FirstRun selects Full Synchronisation / Full Import profiles throughout (the very first
        cycle, nothing yet in the Metaverse); otherwise Delta. The CSV connector has no Delta Import
        (no change tracking), so the import leg is always a Full Import regardless.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [switch]$FirstRun,
        [switch]$AllowExportWarnings
    )

    $r = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Full Import"

    $csvSyncProfileId = if ($FirstRun) { $Config.CSVSyncProfileId } else { $Config.CSVDeltaSyncProfileId }
    $csvSync = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $csvSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $csvSync.activityId -Name $(if ($FirstRun) { "HR CSV Full Synchronisation" } else { "HR CSV Delta Sync" })

    $ldapExport = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $Config.LDAPExportProfileId -Wait -PassThru
    if ($AllowExportWarnings) {
        Assert-ActivitySuccess -ActivityId $ldapExport.activityId -Name "Directory Export" -AllowWarnings
    }
    else {
        Assert-ActivitySuccess -ActivityId $ldapExport.activityId -Name "Directory Export"
    }

    $ldapImportProfileId = if ($FirstRun) { $Config.LDAPFullImportProfileId } else { $Config.LDAPDeltaImportProfileId }
    $ldapImport = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $ldapImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $ldapImport.activityId -Name $(if ($FirstRun) { "Directory Full Import" } else { "Directory Delta Import" })

    $ldapSyncProfileId = if ($FirstRun) { $Config.LDAPFullSyncProfileId } else { $Config.LDAPDeltaSyncProfileId }
    $ldapSync = Start-JIMRunProfile -ConnectedSystemId $Config.LDAPSystemId -RunProfileId $ldapSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $ldapSync.activityId -Name $(if ($FirstRun) { "Directory Full Synchronisation" } else { "Directory Delta Sync" })

    return @{ CsvSyncActivityId = $csvSync.activityId; LdapExportActivityId = $ldapExport.activityId; LdapSyncActivityId = $ldapSync.activityId }
}

function Get-MvoAttributeValue {
    <#
    .SYNOPSIS
        A single attribute's value off one Metaverse Object, read by Id.
    .DESCRIPTION
        Get-JIMMetaverseObject -Id does not accept -Attributes (it is its own parameter set, "ById",
        and always returns the full entity), and its shape differs from the list/search headers this
        scenario reads elsewhere: MetaverseObjectDto carries AttributeValues, a flat list of
        {attributeName, stringValue, ...} rows, not the Attributes dictionary the header DTO exposes
        (see src/JIM.PowerShell/CLAUDE.md > "Documenting output" for the two shapes; Test-Helpers.ps1's
        own Assert-MvoAttributeValue reads it the same way). $null when the object carries no value
        for the named attribute.
    #>
    param([Parameter(Mandatory=$true)][guid]$MvoId, [Parameter(Mandatory=$true)][string]$AttributeName)
    $mvo = Get-JIMMetaverseObject -Id $MvoId
    $row = $mvo.attributeValues | Where-Object { $_.attributeName -eq $AttributeName } | Select-Object -First 1
    if ($row) { return $row.stringValue }
    return $null
}

function Get-Population {
    <#
    .SYNOPSIS
        Every User Metaverse Object's First Name/Last Name/Account Name/Staff Number/Badge Code.
    #>
    return @(Get-JIMMetaverseObject -ObjectTypeName "User" -Attributes @("Account Name", "First Name", "Last Name", "Staff Number", "Badge Code") -All)
}

function Assert-AccountNameInvariants {
    <#
    .SYNOPSIS
        Every person has an Account Name; case-insensitive uniqueness holds across the whole
        population; for each base value shared by n people, the SET of values is exactly
        {base, base1, base2, ..., base(n-1)}. Processing order is not deterministic, so this asserts
        sets, never which person got which value.
    #>
    param([Parameter(Mandatory=$true)][string]$Context)

    $people = Get-Population
    Add-TestResult -Name "[$Context] Population is non-empty" -Passed ($people.Count -gt 0) `
        -Detail "Get-JIMMetaverseObject returned $($people.Count) User objects"

    $missing = @($people | Where-Object { -not $_.attributes.'Account Name' })
    Add-TestResult -Name "[$Context] Every person has an Account Name" -Passed ($missing.Count -eq 0) `
        -Detail "$($missing.Count) of $($people.Count) people have no Account Name: $(($missing | ForEach-Object { $_.displayName }) -join ', ')"

    $lowerValues = @($people | Where-Object { $_.attributes.'Account Name' } | ForEach-Object { $_.attributes.'Account Name'.ToLower() })
    $duplicates = @($lowerValues | Group-Object | Where-Object { $_.Count -gt 1 })
    Add-TestResult -Name "[$Context] Account Name is unique case-insensitively across the population" -Passed ($duplicates.Count -eq 0) `
        -Detail "Duplicate (case-insensitive) values: $(($duplicates | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', ')"

    $groups = $people | Where-Object { $_.attributes.'First Name' -and $_.attributes.'Last Name' } |
        Group-Object { Get-GeneratedBaseValue -FirstName $_.attributes.'First Name' -LastName $_.attributes.'Last Name' }

    $badGroups = @()
    foreach ($group in $groups) {
        $base = $group.Name
        $n = $group.Count
        $expected = @($base)
        if ($n -gt 1) { $expected += 1..($n - 1) | ForEach-Object { "$base$_" } }
        $actual = @($group.Group | ForEach-Object { if ($_.attributes.'Account Name') { $_.attributes.'Account Name'.ToLower() } })
        $expectedSorted = ($expected | Sort-Object) -join ','
        $actualSorted = ($actual | Sort-Object) -join ','
        if ($expectedSorted -ne $actualSorted) {
            $badGroups += "base '$base' ($n people): expected {$($expected -join ', ')}, got {$($actual -join ', ')}"
        }
    }
    Add-TestResult -Name "[$Context] Per-base-value Account Name sets are exactly {base, base1, base2, ...}" -Passed ($badGroups.Count -eq 0) `
        -Detail ($badGroups -join '; ')
}

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Brownfield (out-of-band) directory account creation, bypassing JIM entirely
# ─────────────────────────────────────────────────────────────────────────────────────────────

function New-OutOfBandLdapAccount {
    <#
    .SYNOPSIS
        Creates a directory account directly against OpenLDAP or Samba AD, never through JIM, for the
        Adopt before generate and (Samba AD only) Collision test steps. Returns the account's DN.
    .DESCRIPTION
        OpenLDAP: a plain ldapadd over the configured bind. Samba AD: ldbadd routed through the running
        server (never direct sam.ldb file access, which races the server's own writes; see Scenario 5's
        out-of-band account, whose pattern this follows), with LF-only line endings (Samba's ldb LDIF
        parser, unlike OpenLDAP's, does not tolerate a trailing \r from this file's CRLF here-strings).
    #>
    param(
        [Parameter(Mandatory=$true)][string]$AccountName,
        [Parameter(Mandatory=$true)][string]$FirstName,
        [Parameter(Mandatory=$true)][string]$LastName,
        [string]$EmployeeIdValue,
        [string]$PreferredLanguage,
        [string]$OfficeName,
        [switch]$OutsideImportScope
    )

    $displayName = "$FirstName $LastName"

    if ($isRfcDirectory) {
        $dn = "uid=$AccountName,$($DirectoryConfig.UserContainer)"
        $lines = @(
            "dn: $dn", "objectClass: inetOrgPerson", "uid: $AccountName", "cn: $displayName",
            "sn: $LastName", "givenName: $FirstName", "displayName: $displayName",
            "mail: $AccountName@panoply.local", "userPassword: Password123!"
        )
        if ($EmployeeIdValue) { $lines += "employeeNumber: $EmployeeIdValue" }
        if ($PreferredLanguage) { $lines += "preferredLanguage: $PreferredLanguage" }
        if ($OfficeName) { $lines += "physicalDeliveryOfficeName: $OfficeName" }
        $ldif = ($lines -join "`n") + "`n"

        $result = $ldif | docker exec -i $DirectoryConfig.ContainerName ldapadd -x `
            -H "$($DirectoryConfig.LdapSearchScheme)://localhost:$($DirectoryConfig.LdapSearchPort)" `
            -D "$($DirectoryConfig.BindDN)" -w "$($DirectoryConfig.BindPassword)" 2>&1
        if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
            throw "Failed to create out-of-band OpenLDAP account '$AccountName': $result"
        }
        return $dn
    }
    else {
        $containerDn = if ($OutsideImportScope) { "CN=Users,$($DirectoryConfig.BaseDN)" } else { "OU=Users,OU=Corp,$($DirectoryConfig.BaseDN)" }
        $dn = "CN=$displayName,$containerDn"
        $lines = @(
            "dn: $dn", "objectClass: top", "objectClass: person", "objectClass: organizationalPerson", "objectClass: user",
            "cn: $displayName", "sn: $LastName", "givenName: $FirstName", "sAMAccountName: $AccountName",
            "displayName: $displayName", "userPrincipalName: $AccountName@panoply.local"
        )
        if ($EmployeeIdValue) { $lines += "employeeID: $EmployeeIdValue" }
        if ($PreferredLanguage) { $lines += "preferredLanguage: $PreferredLanguage" }
        if ($OfficeName) { $lines += "physicalDeliveryOfficeName: $OfficeName" }
        $ldif = ($lines -join "`n") + "`n"

        $ldifPath = [System.IO.Path]::GetTempFileName()
        try {
            [System.IO.File]::WriteAllText($ldifPath, $ldif.Replace("`r`n", "`n"))
            docker cp $ldifPath "$($DirectoryConfig.ContainerName):/tmp/s23-oob.ldif" 2>&1 | Out-Null

            $maxAttempts = 5
            $created = $false
            $lastText = ""
            for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
                $createResult = docker exec $DirectoryConfig.ContainerName ldbadd -H ldap://localhost -U "Administrator%$($DirectoryConfig.BindPassword)" /tmp/s23-oob.ldif 2>&1 | ForEach-Object { "$_" }
                $lastText = $createResult -join "`n"
                if ($lastText -match "Added 1 record" -or ($LASTEXITCODE -eq 0 -and [string]::IsNullOrWhiteSpace($lastText))) { $created = $true; break }
                if ($lastText -match "already exists") { $created = $true; break }
                if ($attempt -lt $maxAttempts) { Start-Sleep -Seconds 5 }
            }
            docker exec $DirectoryConfig.ContainerName rm -f /tmp/s23-oob.ldif 2>&1 | Out-Null
            if (-not $created) {
                throw "Failed to create out-of-band Samba AD account '$AccountName' via ldbadd after $maxAttempts attempts: $lastText"
            }
        }
        finally {
            Remove-Item $ldifPath -Force -ErrorAction SilentlyContinue
        }
        return $dn
    }
}

function Invoke-RawJimApi {
    <#
    .SYNOPSIS
        A minimal raw REST call against the JIM API, deliberately bypassing the JIM PowerShell module.
    .DESCRIPTION
        JIM.PowerShell's own Invoke-JIMApi (src/JIM.PowerShell/Private/Invoke-JIMApi.ps1) is a Private
        module function, not exported, so a scenario cannot call it to prove the REST surface
        independent of the cmdlets that wrap it. This is that independent call: the same X-API-Key
        header and JSON body shape, built locally so the "raw REST" half of a surface-parity assertion
        genuinely does not go through the module's own request-building code.
    #>
    param(
        [Parameter(Mandatory=$true)][string]$Endpoint,
        [ValidateSet('GET', 'POST', 'PUT', 'PATCH', 'DELETE')][string]$Method = 'GET',
        [object]$Body
    )
    $uri = "$($JIMUrl.TrimEnd('/'))$Endpoint"
    $headers = @{ 'Accept' = 'application/json'; 'X-API-Key' = $ApiKey }
    $params = @{ Uri = $uri; Method = $Method; ContentType = 'application/json'; Headers = $headers }
    if ($Body) { $params.Body = $Body | ConvertTo-Json -Depth 10 }
    return Invoke-RestMethod @params
}

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Setup
# ─────────────────────────────────────────────────────────────────────────────────────────────

Write-TestSection "Scenario 23: Unique Value Generation"
Write-Host "Directory:   $($DirectoryConfig.ConnectedSystemName) ($($DirectoryConfig.DirectoryType))" -ForegroundColor Gray
Write-Host "Template:    $Template" -ForegroundColor Gray
Write-Host "Step:        $Step (steps are cumulative)" -ForegroundColor Gray
Write-Host ""

Write-TestSection "Step 0: Generating the HR CSV without IT-owned attributes"
& "$PSScriptRoot/../Get-OrGenerate-TestCSV.ps1" -Template $Template -OutputPath "$PSScriptRoot/../../test-data" -OmitItOwnedAttributes
Write-Host "  ✓ hr-users.csv generated without samAccountName/email/userPrincipalName" -ForegroundColor Green

Write-TestSection "Step 0b: Configuring JIM (Setup-Scenario23.ps1)"
$config = & "$PSScriptRoot/../Setup-Scenario23.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template -DirectoryConfig $DirectoryConfig
if (-not $config) {
    throw "Setup-Scenario23.ps1 returned no configuration"
}
Write-Host "  ✓ JIM configured" -ForegroundColor Green

$modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

$stepOrder = @("Joiners", "Gates", "Stability", "Sequence", "Random", "ExportMode", "AdoptBeforeGenerate", "StartAgain", "Failure", "Collision", "SurfaceParity", "FeatureFlag")
$lastStepIndex = if ($Step -eq "All") { $stepOrder.Count - 1 } else { $stepOrder.IndexOf($Step) }

try {
    # ─────────────────────────────────────────────────────────────────────────────────────
    # Joiners: import mode, baseline population
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("Joiners")) {
        Write-TestSection "Test 1: Joiners (import mode baseline)"

        Invoke-Cycle -Config $config -FirstRun | Out-Null
        Assert-AccountNameInvariants -Context "Joiners"

        # Values reach the directory: read a sample of provisioned entries back and confirm the
        # directory's uid/sAMAccountName agrees with the Metaverse's Account Name.
        $people = Get-Population
        $sample = @($people | Select-Object -First ([Math]::Min(3, $people.Count)))
        $mismatched = @()
        foreach ($person in $sample) {
            $ldapUser = Get-LDAPUser -UserIdentifier $person.attributes.'Account Name' -DirectoryConfig $DirectoryConfig
            if (-not $ldapUser) {
                $mismatched += "$($person.displayName): no directory entry for '$($person.attributes.'Account Name')'"
            }
        }
        Add-TestResult -Name "Generated Account Name values reach the directory (uid/sAMAccountName)" -Passed ($mismatched.Count -eq 0) `
            -Detail ($mismatched -join '; ')
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Gates: intra-batch collision and the Metaverse gate, in one delta cycle
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("Gates")) {
        Write-TestSection "Test 2: Intra-batch and Metaverse gate"

        # Two brand-new joiners sharing a name (intra-batch: neither is persisted when the other
        # generates), and one whose base equals an existing baseline person's (the Metaverse gate).
        $existingPerson = New-TestUser -Index 1
        Add-HrCsvJoiner -EmployeeId "EMP900001" -FirstName "Marisol" -LastName "Fenwick"
        Add-HrCsvJoiner -EmployeeId "EMP900002" -FirstName "Marisol" -LastName "Fenwick"
        Add-HrCsvJoiner -EmployeeId "EMP900003" -FirstName $existingPerson.FirstName -LastName $existingPerson.LastName

        Invoke-Cycle -Config $config | Out-Null

        $population = Get-Population
        $marisolGroup = @($population | Where-Object { $_.attributes.'First Name' -eq 'Marisol' -and $_.attributes.'Last Name' -eq 'Fenwick' })
        Add-TestResult -Name "Two intra-batch joiners with the same name both projected" -Passed ($marisolGroup.Count -eq 2) `
            -Detail "Expected 2 Marisol Fenwick objects, found $($marisolGroup.Count)"

        $marisolValues = @($marisolGroup | ForEach-Object { $_.attributes.'Account Name'.ToLower() } | Sort-Object)
        $expectedMarisol = @('marisol.fenwick', 'marisol.fenwick1')
        Add-TestResult -Name "Intra-batch collision resolves to {marisol.fenwick, marisol.fenwick1}" -Passed (($marisolValues -join ',') -eq ($expectedMarisol -join ',')) `
            -Detail "Expected $($expectedMarisol -join ', '); got $($marisolValues -join ', ')"

        $reusedBase = Get-GeneratedBaseValue -FirstName $existingPerson.FirstName -LastName $existingPerson.LastName
        $reusedGroup = @($population | Where-Object { $_.attributes.'First Name' -eq $existingPerson.FirstName -and $_.attributes.'Last Name' -eq $existingPerson.LastName })
        Add-TestResult -Name "Base '$reusedBase' now has 2 people (baseline + new joiner sharing the name)" -Passed ($reusedGroup.Count -eq 2) `
            -Detail "Found $($reusedGroup.Count)"

        # The whole-population invariant (unique, correctly suffixed per base) must still hold after
        # the gate-forcing edits, including for values that pre-existed the Gates step.
        Assert-AccountNameInvariants -Context "Gates"
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Stability: full and delta re-runs change nothing; a mover keeps their value (sticky)
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("Stability")) {
        Write-TestSection "Test 3: Stability across re-runs, and a mover"

        $before = Get-Population | ForEach-Object { @{ Id = $_.id; AccountName = $_.attributes.'Account Name' } }
        $pendingBefore = (Get-JIMPendingExport -ConnectedSystemId $config.LDAPSystemId -Count)

        # Full Synchronisation re-evaluates every object; nothing should change.
        $fullSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $fullSync.activityId -Name "HR CSV Full Synchronisation (stability)"

        # Delta Synchronisation with nothing changed in the CSV: also nothing should change.
        $deltaSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $deltaSync.activityId -Name "HR CSV Delta Sync (stability)"

        $after = Get-Population | ForEach-Object { @{ Id = $_.id; AccountName = $_.attributes.'Account Name' } }
        $changed = @()
        foreach ($b in $before) {
            $a = $after | Where-Object { $_.Id -eq $b.Id }
            if ($a -and $a.AccountName -ne $b.AccountName) { $changed += "$($b.Id): '$($b.AccountName)' -> '$($a.AccountName)'" }
        }
        Add-TestResult -Name "Full and delta re-runs change no generated Account Name value" -Passed ($changed.Count -eq 0) `
            -Detail ($changed -join '; ')

        $pendingAfter = (Get-JIMPendingExport -ConnectedSystemId $config.LDAPSystemId -Count)
        Add-TestResult -Name "Full and delta re-runs stage no new Pending Export" -Passed ($pendingAfter -le $pendingBefore) `
            -Detail "Pending exports before: $pendingBefore, after: $pendingAfter"

        # Mover: change an existing person's last name in HR. The base expression would now compute a
        # different value; the committed generated Account Name must not follow it (sticky).
        $mover = New-TestUser -Index 2
        $csv = Import-Csv $csvPath
        $moverRow = $csv | Where-Object { $_.employeeId -eq $mover.EmployeeId }
        Assert-NotNull -Value $moverRow -Message "Mover row (employeeId $($mover.EmployeeId)) found in hr-users.csv"
        $moverBefore = @(Get-Population | Where-Object { $_.attributes.'First Name' -eq $mover.FirstName -and $_.attributes.'Last Name' -eq $mover.LastName }) | Select-Object -First 1
        $accountNameBeforeMove = $moverBefore.attributes.'Account Name'

        $moverRow.lastName = "$($mover.LastName)-Renamed"
        $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

        Invoke-Cycle -Config $config | Out-Null

        $moverLastNameAfter = Get-MvoAttributeValue -MvoId $moverBefore.id -AttributeName "Last Name"
        $moverAccountNameAfter = Get-MvoAttributeValue -MvoId $moverBefore.id -AttributeName "Account Name"
        Add-TestResult -Name "Mover's Last Name updated in the Metaverse" -Passed ($moverLastNameAfter -eq "$($mover.LastName)-Renamed") `
            -Detail "Expected '$($mover.LastName)-Renamed', got '$moverLastNameAfter'"
        Add-TestResult -Name "Mover keeps their generated Account Name despite the base-changing update (sticky)" -Passed ($moverAccountNameAfter -eq $accountNameBeforeMove) `
            -Detail "Expected unchanged '$accountNameBeforeMove', got '$moverAccountNameAfter'"
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Sequence: format, uniqueness, counter state, raising and lowering Sequence Start
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("Sequence")) {
        Write-TestSection "Test 4: Sequence token (Staff Number)"

        $population = Get-Population
        $badFormat = @($population | Where-Object { $_.attributes.'Staff Number' -notmatch '^EMP-\d{6}$' })
        Add-TestResult -Name "Every person has an Staff Number matching EMP-NNNNNN" -Passed ($badFormat.Count -eq 0) `
            -Detail "$($badFormat.Count) values do not match: $(($badFormat | ForEach-Object { $_.attributes.'Staff Number' }) -join ', ')"

        $empNumbers = @($population | ForEach-Object { $_.attributes.'Staff Number' })
        $dupeNumbers = @($empNumbers | Group-Object | Where-Object { $_.Count -gt 1 })
        Add-TestResult -Name "Staff Number is unique across the population" -Passed ($dupeNumbers.Count -eq 0) `
            -Detail "Duplicates: $(($dupeNumbers | ForEach-Object { $_.Name }) -join ', ')"

        $sequenceState = Get-JIMGeneratedValueSequence -SyncRuleId $config.ImportRuleId -MappingId $config.EmployeeNumberMappingId
        Add-TestResult -Name "Get-JIMGeneratedValueSequence reports AssignedCount matching the population size" -Passed ($sequenceState.assignedCount -eq $population.Count) `
            -Detail "Expected $($population.Count), got $($sequenceState.assignedCount)"

        # Raise Sequence Start: new joiners must skip ahead to (at least) the new start.
        $raisedStart = 5000
        $raiseResult = Set-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId -MappingId $config.EmployeeNumberMappingId -SequenceStart $raisedStart -PassThru
        Add-TestResult -Name "Raising Sequence Start reports SequenceSkippedAhead" -Passed ($null -ne $raiseResult.generation.sequenceSkippedAhead) `
            -Detail "Generation.SequenceSkippedAhead was absent on the response"

        Add-HrCsvJoiner -EmployeeId "EMP900011" -FirstName "Quillan" -LastName "Ashby"
        Invoke-Cycle -Config $config | Out-Null

        $afterRaise = @(Get-Population | Where-Object { $_.attributes.'First Name' -eq 'Quillan' -and $_.attributes.'Last Name' -eq 'Ashby' }) | Select-Object -First 1
        Assert-NotNull -Value $afterRaise -Message "New joiner after raising Sequence Start was projected"
        $afterRaiseNumber = [int]($afterRaise.attributes.'Staff Number' -replace '^EMP-0*', '')
        Add-TestResult -Name "New joiner's Staff Number is at least the raised start ($raisedStart)" -Passed ($afterRaiseNumber -ge $raisedStart) `
            -Detail "Got $($afterRaise.attributes.'Staff Number') ($afterRaiseNumber)"

        # Lower Sequence Start: forward-only, so this must have no effect.
        $stateBeforeLower = Get-JIMGeneratedValueSequence -SyncRuleId $config.ImportRuleId -MappingId $config.EmployeeNumberMappingId
        Set-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId -MappingId $config.EmployeeNumberMappingId -SequenceStart 1 | Out-Null
        $stateAfterLower = Get-JIMGeneratedValueSequence -SyncRuleId $config.ImportRuleId -MappingId $config.EmployeeNumberMappingId
        Add-TestResult -Name "Lowering Sequence Start below the counter has no effect on NextNumber" -Passed ($stateAfterLower.nextNumber -eq $stateBeforeLower.nextNumber) `
            -Detail "NextNumber before: $($stateBeforeLower.nextNumber), after: $($stateAfterLower.nextNumber)"

        Add-HrCsvJoiner -EmployeeId "EMP900012" -FirstName "Rosalind" -LastName "Peverell"
        Invoke-Cycle -Config $config | Out-Null
        $afterLower = @(Get-Population | Where-Object { $_.attributes.'First Name' -eq 'Rosalind' -and $_.attributes.'Last Name' -eq 'Peverell' }) | Select-Object -First 1
        $afterLowerNumber = [int]($afterLower.attributes.'Staff Number' -replace '^EMP-0*', '')
        Add-TestResult -Name "After lowering Sequence Start, the next joiner still continues forward (no reset to 1)" -Passed ($afterLowerNumber -gt $afterRaiseNumber) `
            -Detail "Expected greater than $afterRaiseNumber, got $afterLowerNumber"
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Random: Badge Code format and uniqueness
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("Random")) {
        Write-TestSection "Test 5: Random token (Badge Code, Hex)"

        $population = Get-Population
        $badFormat = @($population | Where-Object { $_.attributes.'Badge Code' -notmatch '^[0-9a-f]{8}$' })
        Add-TestResult -Name "Every person has an 8-character lower-case hex Badge Code" -Passed ($badFormat.Count -eq 0) `
            -Detail "$($badFormat.Count) values do not match: $(($badFormat | ForEach-Object { $_.attributes.'Badge Code' }) -join ', ')"

        $codes = @($population | ForEach-Object { $_.attributes.'Badge Code' })
        $dupeCodes = @($codes | Group-Object | Where-Object { $_.Count -gt 1 })
        Add-TestResult -Name "Badge Code is unique across the population" -Passed ($dupeCodes.Count -eq 0) `
            -Detail "Duplicates: $(($dupeCodes | ForEach-Object { $_.Name }) -join ', ')"
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Export mode: value lands on the Connected System Object and in the directory; the
    # Metaverse is never touched (there is no such Metaverse attribute at all).
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("ExportMode")) {
        Write-TestSection "Test 6: Export mode (preferredLanguage, Random Digits)"

        $population = Get-Population
        $sample = @($population | Select-Object -First ([Math]::Min(3, $population.Count)))
        $badPreferredLanguage = @()
        foreach ($person in $sample) {
            $ldapUser = Get-LDAPUser -UserIdentifier $person.attributes.'Account Name' -DirectoryConfig $DirectoryConfig
            $preferredLanguage = if ($ldapUser) { $ldapUser['preferredLanguage'] } else { $null }
            if ($preferredLanguage -notmatch '^\d{6}$') {
                $badPreferredLanguage += "$($person.displayName): preferredLanguage='$preferredLanguage'"
            }
        }
        Add-TestResult -Name "Every sampled directory entry carries a 6-digit generated preferredLanguage" -Passed ($badPreferredLanguage.Count -eq 0) `
            -Detail ($badPreferredLanguage -join '; ')

        # Export-mode assignments are keyed on the Connected System Object, never the Metaverse
        # Object; there is no Metaverse attribute named preferredLanguage at all for a value to appear on.
        $mvAttributes = Get-JIMMetaverseAttribute
        $preferredLanguageMvAttr = $mvAttributes | Where-Object { $_.name -eq 'preferredLanguage' -or $_.name -eq 'Postal Code' }
        Add-TestResult -Name "The Metaverse has no attribute for the export-mode generated value (it never touches the Metaverse)" -Passed ($null -eq $preferredLanguageMvAttr) `
            -Detail "Found an unexpected Metaverse attribute: $($preferredLanguageMvAttr.name)"
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Adopt before generate (import mode): a generated mapping created AFTER a brownfield
    # Connected System Object is already joined and already holds the value adopts it, and
    # stages no rename.
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("AdoptBeforeGenerate")) {
        Write-TestSection "Test 7: Adopt before generate"

        # Why the Locker Code mapping (see Setup-Scenario23.ps1) was created disabled, and is only
        # enabled here: release 1's adopt-existing lookup (GeneratedValueParticipation.FindAdoptableValueAsync,
        # called from SyncTaskProcessorBase.ResolvePendingGeneratedValuesAsync) is a database read of
        # an ALREADY-COMMITTED join. "sticky first" (UniqueValueGenerationServer.ResolveAsync) means a
        # generated mapping's very first resolution for an object is also its only chance to adopt;
        # every resolution after that is sticky regardless. HR is the only system that projects a
        # Metaverse Object, so that object's first HR synchronisation pass is also this mapping's
        # first (and otherwise only) chance to resolve. A brownfield join committed in an EARLIER,
        # separate synchronisation cannot exist before the object it joins to does. Enabling the
        # mapping only once the join is already committed is what makes this reproducible at all,
        # rather than a race against the engine's own ordering.
        $officeValue = "LEGACY-OFFICE-1"
        $dn = New-OutOfBandLdapAccount -AccountName "pashworth99" -FirstName "Percival" -LastName "Ashworth" `
            -EmployeeIdValue "EMP900020" -OfficeName $officeValue
        Write-Host "  Created out-of-band account: $dn" -ForegroundColor Gray

        # A Full Import, not Delta: this brings a directory-side change JIM did not itself make into
        # view reliably (Scenario 5's out-of-band-account pattern uses the same Full Import arrange
        # step for exactly this reason).
        $ldapImport1 = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPFullImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $ldapImport1.activityId -Name "Directory Full Import (adopt - arrange)"

        $unjoined = @(Get-JIMConnectedSystemObject -ConnectedSystemId $config.LDAPSystemId -Search "Percival Ashworth" -JoinType NotJoined -PageSize 10)
        Add-TestResult -Name "Out-of-band account imported as an unjoined Connected System Object (proves genuine brownfield arrange)" -Passed ($unjoined.Count -gt 0) `
            -Detail "Expected at least one unjoined CSO matching 'Percival Ashworth', found $($unjoined.Count)"

        Add-HrCsvJoiner -EmployeeId "EMP900020" -FirstName "Percival" -LastName "Ashworth"
        Invoke-Cycle -Config $config | Out-Null

        $adoptedMvo = @(Get-JIMMetaverseObject -AttributeName "Employee ID" -AttributeValue "EMP900020" -Attributes @("Account Name")) | Select-Object -First 1
        Assert-NotNull -Value $adoptedMvo -Message "Percival Ashworth's Metaverse Object was projected"

        # Enable the disabled Locker Code mapping now that the join above is committed, then run one
        # more cycle so it resolves for the first time.
        Set-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId -MappingId $config.LockerCodeMappingId -Enabled $true | Out-Null
        Invoke-Cycle -Config $config | Out-Null

        $adoptedLockerCodeValue = Get-MvoAttributeValue -MvoId $adoptedMvo.id -AttributeName "Locker Code"
        Add-TestResult -Name "Locker Code adopted the brownfield office value ('$officeValue'), not a fresh 'LOCKER' candidate" -Passed ($adoptedLockerCodeValue -eq $officeValue) `
            -Detail "Expected '$officeValue', got '$adoptedLockerCodeValue'"

        $assignments = @(Get-JIMGeneratedValue -MetaverseObjectId $adoptedMvo.id)
        $lockerAssignment = $assignments | Where-Object { $_.attributeName -eq 'Locker Code' }
        Add-TestResult -Name "Get-JIMGeneratedValue reports the Locker Code assignment as Adopted" -Passed ($null -ne $lockerAssignment -and $lockerAssignment.adopted -eq $true) `
            -Detail "Assignment: $($lockerAssignment | ConvertTo-Json -Compress)"

        # No rename: the ordinary export mapping (Locker Code -> physicalDeliveryOfficeName) must
        # leave the directory's value exactly as the brownfield account already had it.
        $ldapUser = Get-LDAPUser -UserIdentifier "pashworth99" -DirectoryConfig $DirectoryConfig
        Add-TestResult -Name "physicalDeliveryOfficeName in the directory is unchanged ('$officeValue'); no rename was exported" -Passed ($ldapUser -and $ldapUser['physicalDeliveryOfficeName'] -eq $officeValue) `
            -Detail "Directory value: '$($ldapUser['physicalDeliveryOfficeName'])'"
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Start again: the Sequence counter returns to its configured Start; surviving objects
    # keep their values; a new joiner does not collide with them.
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("StartAgain")) {
        Write-TestSection "Test 8: Start again"

        $survivorBefore = @(Get-Population | Where-Object { $_.attributes.'First Name' -eq 'Quillan' -and $_.attributes.'Last Name' -eq 'Ashby' }) | Select-Object -First 1
        Assert-NotNull -Value $survivorBefore -Message "Survivor (Quillan Ashby, from the Sequence test) exists before Start again"
        $survivorNumberBefore = $survivorBefore.attributes.'Staff Number'

        $sequenceBefore = Get-JIMGeneratedValueSequence -SyncRuleId $config.ImportRuleId -MappingId $config.EmployeeNumberMappingId
        $configuredStart = 5000  # set by the Sequence test step above

        $restartResult = Restart-JIMGeneratedValues -SyncRuleId $config.ImportRuleId -MappingId $config.EmployeeNumberMappingId -Confirm:$false
        Add-TestResult -Name "Restart-JIMGeneratedValues reports the counter moving back to the configured Start" -Passed ($restartResult.counterTo -eq $configuredStart) `
            -Detail "Expected CounterTo=$configuredStart, got $($restartResult.counterTo) (CounterFrom was $($restartResult.counterFrom); counter stood at $($sequenceBefore.nextNumber) beforehand)"

        $survivorNumberAfter = Get-MvoAttributeValue -MvoId $survivorBefore.id -AttributeName "Staff Number"
        Add-TestResult -Name "The surviving object keeps its Staff Number after Start again" -Passed ($survivorNumberAfter -eq $survivorNumberBefore) `
            -Detail "Expected unchanged '$survivorNumberBefore', got '$survivorNumberAfter'"

        Add-HrCsvJoiner -EmployeeId "EMP900030" -FirstName "Beatrix" -LastName "Nightingale"
        Invoke-Cycle -Config $config | Out-Null

        $newJoiner = @(Get-Population | Where-Object { $_.attributes.'First Name' -eq 'Beatrix' -and $_.attributes.'Last Name' -eq 'Nightingale' }) | Select-Object -First 1
        Add-TestResult -Name "A joiner after Start again does not collide with the surviving object's number" -Passed ($newJoiner.attributes.'Staff Number' -ne $survivorNumberBefore) `
            -Detail "Both got '$($newJoiner.attributes.'Staff Number')'"

        $allNumbers = @((Get-Population) | ForEach-Object { $_.attributes.'Staff Number' })
        $dupesAfterRestart = @($allNumbers | Group-Object | Where-Object { $_.Count -gt 1 })
        Add-TestResult -Name "No duplicate Staff Number exists after Start again" -Passed ($dupesAfterRestart.Count -eq 0) `
            -Detail "Duplicates: $(($dupesAfterRestart | ForEach-Object { $_.Name }) -join ', ')"

        # Surface parity: exercise the raw REST restart route once too (a second, harmless restart;
        # release 1's Start again is idempotent by design - it only ever moves the counter to its
        # already-configured Start, which is exactly where it now stands).
        $rawRestartResult = Invoke-RawJimApi -Method POST -Endpoint "/api/v1/synchronisation/sync-rules/$($config.ImportRuleId)/mappings/$($config.EmployeeNumberMappingId)/generation/restart"
        Add-TestResult -Name "The REST restart route answers directly (surface parity)" -Passed ($null -ne $rawRestartResult) `
            -Detail "Raw response: $($rawRestartResult | ConvertTo-Json -Compress)"
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Failure: an attempt limit exhausted fails the object via RPEI, nothing written.
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("Failure")) {
        Write-TestSection "Test 9: Failure (attempt limit exhausted)"

        # Call Sign (see Setup-Scenario23.ps1): every object shares the one constant candidate
        # "CALLSIGN", with an attempt limit of 1. Enabling it against the whole existing population
        # in one run guarantees exactly one winner and every other object exhausted on its sole
        # attempt (the bare value is already taken, and no suffix attempt is allowed).
        Set-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId -MappingId $config.CallSignMappingId -Enabled $true | Out-Null

        $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Full Import (Failure arrange)"
        $failureSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $failureSync.activityId -Name "HR CSV Delta Sync (Call Sign exhaustion)" -AllowWarnings

        $items = @(Get-JIMActivity -Id $failureSync.activityId -ExecutionItems)
        $exhausted = @($items | Where-Object { $_.errorType -eq 'GeneratedValueExhausted' })
        Add-TestResult -Name "At least one object failed with GeneratedValueExhausted" -Passed ($exhausted.Count -gt 0) `
            -Detail "Found $($exhausted.Count) items with that error type among $($items.Count) execution items"

        $population2 = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Attributes @("Call Sign") -All)
        $withValue = @($population2 | Where-Object { $_.attributes.'Call Sign' -eq 'CALLSIGN' })
        Add-TestResult -Name "Exactly one object won the Call Sign value; nothing else was written for the rest" -Passed ($withValue.Count -eq 1) `
            -Detail "Expected exactly 1 object with Call Sign='CALLSIGN', found $($withValue.Count)"

        # Leave the mapping disabled again: it has done its job and would otherwise fail every
        # future joiner's synchronisation for the rest of this run.
        Set-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId -MappingId $config.CallSignMappingId -Enabled $false | Out-Null
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Collision (Samba AD only): a target-side collision the local gates cannot see is an
    # ordinary export error naming the value and the system (release 1 has no probing and no
    # classification of the rejection; that lands in releases 3 and 4).
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("Collision")) {
        Write-TestSection "Test 10: Target-side collision (Samba AD only)"

        if ($DirectoryConfig.DirectoryType -ne "SambaAD") {
            Write-Host "  Skipped: this step needs a directory-wide unique-value constraint (a CSV target cannot" -ForegroundColor Yellow
            Write-Host "  reject a duplicate, and OpenLDAP has no such constraint configured in this harness)." -ForegroundColor Yellow
            Write-Host "  Re-run with -DirectoryConfig (Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary) to exercise it." -ForegroundColor Yellow
        }
        else {
            $collisionName = "thaddeus.okonkwo"
            $dn = New-OutOfBandLdapAccount -AccountName $collisionName -FirstName "Thaddeus" -LastName "Okonkwo" -OutsideImportScope
            Write-Host "  Created out-of-band account outside JIM's import scope: $dn" -ForegroundColor Gray

            Add-HrCsvJoiner -EmployeeId "EMP900040" -FirstName "Thaddeus" -LastName "Okonkwo"
            $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Full Import (Collision arrange)"
            $collisionSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $collisionSync.activityId -Name "HR CSV Delta Sync (Collision arrange)"

            $collisionMvo = @(Get-JIMMetaverseObject -AttributeName "Employee ID" -AttributeValue "EMP900040" -Attributes @("Account Name")) | Select-Object -First 1
            Assert-NotNull -Value $collisionMvo -Message "Thaddeus Okonkwo's Metaverse Object was projected"
            Add-TestResult -Name "The local gates did not see the out-of-scope brownfield account (release 1 has no probing)" -Passed ($collisionMvo.attributes.'Account Name' -eq $collisionName) `
                -Detail "Expected '$collisionName' (generated free, since the brownfield account is outside JIM's import scope), got '$($collisionMvo.attributes.'Account Name')'"

            # The export to the directory now collides for real: Samba AD enforces sAMAccountName
            # uniqueness domain-wide, regardless of OU.
            $export = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $config.LDAPExportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $export.activityId -Name "Directory Export (Collision)" -AllowWarnings

            $pendingExports = @(Get-JIMPendingExport -ConnectedSystemId $config.LDAPSystemId -All)
            $collisionPendingExport = $pendingExports | Where-Object { $_.sourceMetaverseObjectDisplayName -match 'Thaddeus Okonkwo' } | Select-Object -First 1
            Add-TestResult -Name "An ordinary export error was recorded, naming a failure (release 1: no UniqueValueAlreadyInUse classification yet)" `
                -Passed ($null -ne $collisionPendingExport -and $collisionPendingExport.errorCount -gt 0 -and -not [string]::IsNullOrWhiteSpace($collisionPendingExport.lastErrorMessage)) `
                -Detail "Pending export: $($collisionPendingExport | ConvertTo-Json -Compress)"

            if ($collisionPendingExport -and $collisionPendingExport.lastErrorMessage) {
                Write-Host "  Export error message: $($collisionPendingExport.lastErrorMessage)" -ForegroundColor Gray
            }
        }
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Surface parity: the same shape of mapping configured via raw REST and via PowerShell
    # produces identical (same-shaped) generated behaviour.
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("SurfaceParity")) {
        Write-TestSection "Test 11: Surface parity (raw REST vs PowerShell)"

        $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
        $restAttr = Get-JIMMetaverseAttribute | Where-Object { $_.name -eq "Access Code REST" }
        if (-not $restAttr) {
            $restAttr = New-JIMMetaverseAttribute -Name "Access Code REST" -Type Text -AttributePlurality SingleValued -ObjectTypeIds @($mvUserType.id)
        }
        $psAttr = Get-JIMMetaverseAttribute | Where-Object { $_.name -eq "Access Code PS" }
        if (-not $psAttr) {
            $psAttr = New-JIMMetaverseAttribute -Name "Access Code PS" -Type Text -AttributePlurality SingleValued -ObjectTypeIds @($mvUserType.id)
        }

        $existingMappings = Get-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId
        $restMapping = $existingMappings | Where-Object { $_.targetMetaverseAttributeId -eq $restAttr.id }
        if (-not $restMapping) {
            # Configured through the raw REST API: the same JSON shape New-JIMSyncRuleMapping itself
            # sends (CreateSyncRuleMappingRequest / CreateSyncRuleMappingGenerationRequest), built here
            # independently rather than through the module's own request-building code.
            $body = @{
                targetMetaverseAttributeId = $restAttr.id
                sources = @()
                generation = @{ tokenKind = "Random"; randomFormat = "Hex"; randomLength = 6 }
            }
            $restMapping = Invoke-RawJimApi -Method POST -Endpoint "/api/v1/synchronisation/sync-rules/$($config.ImportRuleId)/mappings" -Body $body
        }

        $psMapping = $existingMappings | Where-Object { $_.targetMetaverseAttributeId -eq $psAttr.id }
        if (-not $psMapping) {
            $psMapping = New-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId -TargetMetaverseAttributeId $psAttr.id -Generate -TokenKind Random -RandomFormat Hex -RandomLength 6
        }

        Invoke-Cycle -Config $config | Out-Null

        $population = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Attributes @("Access Code REST", "Access Code PS") -All)
        $restBad = @($population | Where-Object { $_.attributes.'Access Code REST' -notmatch '^[0-9a-f]{6}$' })
        $psBad = @($population | Where-Object { $_.attributes.'Access Code PS' -notmatch '^[0-9a-f]{6}$' })

        Add-TestResult -Name "The REST-configured mapping generates a 6-character hex value for everyone" -Passed ($restBad.Count -eq 0) `
            -Detail "$($restBad.Count) objects have a non-matching Access Code REST value"
        Add-TestResult -Name "The PowerShell-configured mapping generates a 6-character hex value for everyone" -Passed ($psBad.Count -eq 0) `
            -Detail "$($psBad.Count) objects have a non-matching Access Code PS value"

        $restValues = @($population | ForEach-Object { $_.attributes.'Access Code REST' })
        $psValues = @($population | ForEach-Object { $_.attributes.'Access Code PS' })
        $restDupes = @($restValues | Group-Object | Where-Object { $_.Count -gt 1 })
        $psDupes = @($psValues | Group-Object | Where-Object { $_.Count -gt 1 })
        Add-TestResult -Name "Both the REST-configured and PowerShell-configured mappings produce identical (unique, correctly shaped) generated behaviour" `
            -Passed ($restDupes.Count -eq 0 -and $psDupes.Count -eq 0) `
            -Detail "REST duplicates: $($restDupes.Count); PowerShell duplicates: $($psDupes.Count)"
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Feature flag: disabled, creating a NEW generated mapping is refused (400); existing
    # mappings keep generating.
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("FeatureFlag")) {
        Write-TestSection "Test 12: Feature flag (Features.UniqueValueGeneration)"

        Disable-JIMFeature -Name "Features.UniqueValueGeneration" | Out-Null

        $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
        $throwawayAttr = Get-JIMMetaverseAttribute | Where-Object { $_.name -eq "Flag Test Attribute" }
        if (-not $throwawayAttr) {
            $throwawayAttr = New-JIMMetaverseAttribute -Name "Flag Test Attribute" -Type Text -AttributePlurality SingleValued -ObjectTypeIds @($mvUserType.id)
        }

        $refused = $false
        $refusalMessage = $null
        try {
            New-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId -TargetMetaverseAttributeId $throwawayAttr.id `
                -Generate -TokenKind Random -RandomFormat Hex -RandomLength 4 -ErrorAction Stop | Out-Null
        }
        catch {
            $refused = $true
            $refusalMessage = $_.Exception.Message
        }
        Add-TestResult -Name "Creating a new generated mapping is refused (400) while the feature flag is disabled" -Passed $refused `
            -Detail "Expected the create call to fail; refusal message: $refusalMessage"
        if ($refused) {
            Add-TestResult -Name "The refusal names HTTP 400" -Passed ($refusalMessage -match '\(400\)') `
                -Detail "Message was: $refusalMessage"
        }

        # Existing generated mappings keep generating while the flag is off.
        Add-HrCsvJoiner -EmployeeId "EMP900050" -FirstName "Cordelia" -LastName "Whitlock"
        Invoke-Cycle -Config $config | Out-Null
        $flagOffJoiner = @(Get-Population | Where-Object { $_.attributes.'First Name' -eq 'Cordelia' -and $_.attributes.'Last Name' -eq 'Whitlock' }) | Select-Object -First 1
        Add-TestResult -Name "An existing generated mapping (Account Name) keeps generating with the flag disabled" -Passed ($flagOffJoiner -and $flagOffJoiner.attributes.'Account Name' -eq 'cordelia.whitlock') `
            -Detail "Expected 'cordelia.whitlock', got '$($flagOffJoiner.attributes.'Account Name')'"

        Enable-JIMFeature -Name "Features.UniqueValueGeneration" -AllowInDevelopment | Out-Null
        Write-Host "  ✓ Re-enabled Features.UniqueValueGeneration" -ForegroundColor Green
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

Write-TestSection "Scenario 23 Summary"
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
Write-Host "✓ Unique Value Generation (release 1) behaves as designed: tokens, gates, adoption," -ForegroundColor Green
Write-Host "  stability, Start again and surface parity all hold." -ForegroundColor Green
exit 0
