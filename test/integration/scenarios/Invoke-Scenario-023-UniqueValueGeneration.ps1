# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 023: Unique Value Generation

.DESCRIPTION
    Exercises release 1 of Unique Value Generation (#242): a "Generated Value" source type on import
    and export Attribute Flows, whose value is a base expression plus a uniqueness token (OnlyIfTaken,
    Sequence or Random), local gates (reservation within a run, Metaverse, connector space), a brownfield account kept
    by Attribute Priority (and, when that higher-priority flow is disabled, the generated flow taking the
    attribute back exactly as any Attribute Flow would, renaming the account), sticky assignments across
    re-runs, Start again, and (release 2, Phase 6) the retired values register: a leaver's generated
    values are retired and never reissued while "Never reuse a value" is on, and are free again with it
    off, and (release 3, Phase 7) the live probe of the directory: an account created outside JIM is
    caught by the probe; a bind that cannot see the directory's values, and an unreachable directory,
    each degrade to the local gates with one Activity warning line and never fail the run. This
    scenario does NOT include Collision Remediation or Needs Decision (release 4). A target-side
    collision is still an ordinary export error, which is
    existing export behaviour rather than generation; it gets integration coverage with release 4's
    Collision Remediation, which reworks that path (and needs the harness to accept an intended export
    error, which its end-of-run log scan does not today).

    The provisioning substrate is Scenario 001's, composed via Setup-Scenario-023.ps1 (which itself calls
    Setup-Scenario-001.ps1 -GenerateAccountName -DeriveFromAccountName, so Email and User Principal Name
    are derived from the generated Account Name, Metaverse-Derived Attribute Flows, #1750), with the HR CSV generated via Get-OrGenerate-TestCSV.ps1
    -OmitItOwnedAttributes so samAccountName, email and userPrincipalName are genuinely absent, the
    shape this feature exists to make representative. The target directory starts empty: every Account
    Name, Staff Number and Badge Code is generated, not sourced.

    Steps are CUMULATIVE, like Scenario 022's: a named step runs everything up to and including itself,
    because most steps depend on the population state earlier steps leave behind (Joiners' baseline,
    Gates' intra-batch joiners, the Sequence/Random values every object already carries).

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

.PARAMETER SkipPopulate
    Accepted for runner compatibility. This scenario provisions the accounts it asserts against and
    needs no pre-populated directory data.

.PARAMETER DirectoryConfig
    Directory configuration hashtable. Defaults to Get-DirectoryConfig -DirectoryType OpenLDAP. Samba
    AD is fully supported (pass -DirectoryConfig (Get-DirectoryConfig -DirectoryType SambaAD -Instance
    Primary)).

.EXAMPLE
    ./Invoke-Scenario-023-UniqueValueGeneration.ps1 -ApiKey "jim_..." -Template Micro

.EXAMPLE
    ./Invoke-Scenario-023-UniqueValueGeneration.ps1 -ApiKey "jim_..." -Step Sequence -DirectoryConfig (Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary)
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Joiners", "Gates", "Stability", "Sequence", "Random", "ExportMode", "Brownfield", "StartAgain", "Failure", "SurfaceParity", "NeverReuse", "ProbeBrownfield", "ProbeRestrictedBind", "ProbeUnreachable", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Micro",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

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

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Primary
}
if (-not $ApiKey) {
    throw "API key required for authentication. Create one via the JIM portal: Admin > API Keys."
}

$isRfcDirectory = Test-IsRfcDirectory $DirectoryConfig
$csvPath = "$(Get-IntegrationTestDataPath)/hr-users.csv"

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
    Copy-CsvToConnectorFiles -SourcePath $csvPath
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
        Creates a directory account directly against OpenLDAP, Samba AD or Active Directory, never through
        JIM, for the Brownfield test step. Returns the account's DN.
    .DESCRIPTION
        OpenLDAP: a plain ldapadd over the configured bind. Active Directory: the same ldapadd of the Samba
        AD account's attributes, over LDAPS through the toolbox (there is no ldb on a real domain controller,
        and Invoke-DirectoryLdif sends LF-only LDIF regardless). Samba AD: ldbadd routed through the running
        server (never direct sam.ldb file access, which races the server's own writes; see Scenario 005's
        out-of-band account, whose pattern this follows), with LF-only line endings (Samba's ldb LDIF
        parser, unlike OpenLDAP's, does not tolerate a trailing \r from this file's CRLF here-strings).
    #>
    param(
        [Parameter(Mandatory=$true)][string]$AccountName,
        [Parameter(Mandatory=$true)][string]$FirstName,
        [Parameter(Mandatory=$true)][string]$LastName,
        [string]$EmployeeIdValue,
        [string]$PreferredLanguage
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
        $ldif = ($lines -join "`n") + "`n"

        $result = Invoke-DirectoryLdif -DirectoryConfig $DirectoryConfig -Ldif $ldif -Operation add
        if ($result.Outcome -eq 'Failed') {
            throw "Failed to create out-of-band OpenLDAP account '$AccountName': $($result.Output)"
        }
        return $dn
    }
    else {
        $containerDn = "OU=Users,OU=Corp,$($DirectoryConfig.BaseDN)"
        $dn = "CN=$displayName,$containerDn"
        $lines = @(
            "dn: $dn", "objectClass: top", "objectClass: person", "objectClass: organizationalPerson", "objectClass: user",
            "cn: $displayName", "sn: $LastName", "givenName: $FirstName", "sAMAccountName: $AccountName",
            "displayName: $displayName", "userPrincipalName: $AccountName@panoply.local"
        )
        if ($EmployeeIdValue) { $lines += "employeeID: $EmployeeIdValue" }
        if ($PreferredLanguage) { $lines += "preferredLanguage: $PreferredLanguage" }
        $ldif = ($lines -join "`n") + "`n"

        if (Test-ActiveDirectoryConfig -DirectoryConfig $DirectoryConfig) {
            $result = Invoke-DirectoryLdif -DirectoryConfig $DirectoryConfig -Ldif $ldif -Operation add
            if ($result.Outcome -eq 'Failed') {
                throw "Failed to create out-of-band Active Directory account '$AccountName': $($result.Output)"
            }
            return $dn
        }

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

function Invoke-HrJoinerOrLeaverSync {
    <#
    .SYNOPSIS
        HR CSV Full Import then HR CSV Delta Sync, and nothing else.
    .DESCRIPTION
        The Never reuse step's joiners and leavers only need the HR side: with a zero deletion grace period a
        leaver's Metaverse Object is deleted in the Delta Sync itself, and a joiner withdrawn before any export
        has its provisioning cancelled outright, so no directory round trip is needed to make or remove one.
    #>
    param([Parameter(Mandatory=$true)][hashtable]$Config, [Parameter(Mandatory=$true)][string]$Context)
    $import = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVImportProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $import.activityId -Name "HR CSV Full Import ($Context)"
    $sync = Start-JIMRunProfile -ConnectedSystemId $Config.CSVSystemId -RunProfileId $Config.CSVDeltaSyncProfileId -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $sync.activityId -Name "HR CSV Delta Sync ($Context)"
}

function Remove-HrCsvRow {
    <#
    .SYNOPSIS
        Removes one person (by employeeId) from hr-users.csv, making them a leaver at the next import.
    #>
    param([Parameter(Mandatory=$true)][string]$EmployeeId)
    $csv = @(Import-Csv $csvPath | Where-Object { $_.employeeId -ne $EmployeeId })
    $csv | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    Copy-CsvToConnectorFiles -SourcePath $csvPath
}

function Get-PersonByEmployeeId {
    <#
    .SYNOPSIS
        One User Metaverse Object, by its HR Employee ID, with its generated attributes; $null when absent.
    #>
    param([Parameter(Mandatory=$true)][string]$EmployeeId)
    return @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue $EmployeeId `
        -Attributes @("Account Name", "Staff Number", "Badge Code") -ErrorAction SilentlyContinue) | Select-Object -First 1
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
# Probing (release 3, Phase 7): accounts and binds JIM's own records know nothing about
# ─────────────────────────────────────────────────────────────────────────────────────────────

function New-ProbeBrownfieldAccount {
    <#
    .SYNOPSIS
        Creates an account outside JIM, in an OU beside the Connected System's selected container (so the import
        never brings it into JIM's records) but under the partition root the probe searches. Returns its DN.
    .DESCRIPTION
        Samba AD: samba-tool in the container (New-DirectoryOu -ViaServer, New-DirectoryUser). Active Directory:
        the same helpers' LDAP path. OpenLDAP: an inetOrgPerson added over LDAP as the directory administrator.
        The runner's Samba AD lightweight reset removes OU=Legacy again.
    #>
    param(
        [Parameter(Mandatory=$true)][string]$AccountName,
        [Parameter(Mandatory=$true)][string]$FirstName,
        [Parameter(Mandatory=$true)][string]$LastName
    )

    $ouDn = if ($isRfcDirectory) { "ou=Legacy,$($DirectoryConfig.BaseDN)" } else { "OU=Legacy,$($DirectoryConfig.BaseDN)" }
    $ou = New-DirectoryOu -DirectoryConfig $DirectoryConfig -Dn $ouDn -ViaServer
    if (-not $ou.Success) {
        throw "Failed to create the out-of-scope OU '$ouDn': $($ou.Output)"
    }

    if ($isRfcDirectory) {
        $dn = "uid=$AccountName,$ouDn"
        $ldif = (@(
            "dn: $dn", "objectClass: inetOrgPerson", "uid: $AccountName", "cn: $FirstName $LastName",
            "sn: $LastName", "givenName: $FirstName", "displayName: $FirstName $LastName",
            "description: Created outside JIM", "userPassword: Legacy@Acct123!"
        ) -join "`n") + "`n"
        $result = Invoke-DirectoryLdif -DirectoryConfig $DirectoryConfig -Ldif $ldif -Operation add
        if ($result.Outcome -eq 'Failed') {
            throw "Failed to create the brownfield account '$dn': $($result.Output)"
        }
        return $dn
    }

    $dn = "CN=$FirstName $LastName,$ouDn"
    $user = New-DirectoryUser -DirectoryConfig $DirectoryConfig -Dn $dn -SamAccountName $AccountName -Password 'Legacy@Acct123!' `
        -Attributes ([ordered]@{ givenName = $FirstName; sn = $LastName; description = 'Created outside JIM' })
    if (-not $user.Success) {
        throw "Failed to create the brownfield account '$dn': $($user.Output)"
    }
    return $dn
}

function ConvertTo-DirectoryEntrySnapshot {
    <#
    .SYNOPSIS
        One directory entry's attributes as sorted "name=value" lines, so two reads can be compared exactly.
    #>
    param([Parameter(Mandatory=$true)][System.Collections.IDictionary]$Entry)
    $lines = foreach ($name in ($Entry.Keys | Sort-Object)) {
        foreach ($value in @($Entry[$name])) { "$($name.ToLowerInvariant())=$value" }
    }
    return @($lines)
}

function Get-JimRecordsHoldingValue {
    <#
    .SYNOPSIS
        How many rows anywhere in JIM's own records (Metaverse values, Connector Space values, generated value
        assignments, the retired values register) hold a value, compared case-insensitively. Read from the
        database because no single API read covers all four.
    #>
    param([Parameter(Mandatory=$true)][string]$Value)
    if ($Value -notmatch '^[A-Za-z0-9.\-]+$') { throw "Get-JimRecordsHoldingValue: '$Value' is not a plain account-name value" }
    $lower = $Value.ToLowerInvariant()
    $sql = "SELECT (SELECT count(*) FROM ""MetaverseObjectAttributeValues"" WHERE lower(""StringValue"") = '$lower') + " +
           "(SELECT count(*) FROM ""ConnectedSystemObjectAttributeValues"" WHERE lower(""StringValue"") = '$lower') + " +
           "(SELECT count(*) FROM ""GeneratedValueAssignments"" WHERE lower(""Value"") = '$lower') + " +
           "(SELECT count(*) FROM ""RetiredGeneratedValues"" WHERE lower(""Value"") = '$lower');"
    $raw = docker exec (Get-IntegrationLane).DatabaseContainer psql -U jim -d jim -t -A -c $sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Get-JimRecordsHoldingValue: psql failed: $raw" }
    return [int]("$raw".Trim())
}

function Set-DirectoryBindAccount {
    <#
    .SYNOPSIS
        Points the directory Connected System's Username and Password settings at another account.
    #>
    param([Parameter(Mandatory=$true)][string]$Username, [Parameter(Mandatory=$true)][string]$Password)
    $ldapDefinition = Get-JIMConnectorDefinition | Where-Object { $_.name -eq "JIM LDAP Connector" } | Select-Object -First 1
    $ldapDefinition = Get-JIMConnectorDefinition -Id $ldapDefinition.id
    $usernameSetting = $ldapDefinition.settings | Where-Object { $_.name -eq "Username" }
    $passwordSetting = $ldapDefinition.settings | Where-Object { $_.name -eq "Password" }
    Set-JIMConnectedSystem -Id $config.LDAPSystemId -SettingValues @{
        $usernameSetting.id = @{ stringValue = $Username }
        $passwordSetting.id = @{ stringValue = $Password }
    } | Out-Null
}

function Assert-SingleProbeWarning {
    <#
    .SYNOPSIS
        Asserts an HR synchronisation completed with exactly one probe warning line naming the directory, and
        nothing else wrong (no other warning, no object error). Returns the warning line.
    #>
    param([Parameter(Mandatory=$true)][string]$ActivityId, [Parameter(Mandatory=$true)][string]$Context)

    $activity = Get-JIMActivity -Id $ActivityId
    Add-TestResult -Name "[$Context] The HR synchronisation completed with a warning, not a failure" -Passed ($activity.status -eq 'CompleteWithWarning') `
        -Detail "Status '$($activity.status)'; warning: '$($activity.warningMessage)'"

    $allowed = $true
    $allowedDetail = ""
    try {
        Assert-ActivitySuccess -ActivityId $ActivityId -Name "HR CSV Delta Sync ($Context)" `
            -AllowedWarningMessagePattern (Get-UniquenessProbeWarningPattern -ConnectedSystemName $DirectoryConfig.ConnectedSystemName)
    }
    catch {
        $allowed = $false
        $allowedDetail = "$_"
    }
    Add-TestResult -Name "[$Context] The only warning is the probe warning for $($DirectoryConfig.ConnectedSystemName), and no object failed" -Passed $allowed -Detail $allowedDetail

    $lines = @("$($activity.warningMessage)" -split "\r?\n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    Add-TestResult -Name "[$Context] Exactly one probe warning line for the run, not one per object" -Passed ($lines.Count -eq 1) `
        -Detail "$($lines.Count) lines: $($lines -join ' | ')"
    return $(if ($lines.Count -gt 0) { $lines[0] } else { "" })
}

function Wait-DirectoryAnswering {
    <#
    .SYNOPSIS
        Waits until the directory answers a search again (after it was paused), up to a minute.
    #>
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        try {
            $null = Find-DirectoryEntry -DirectoryConfig $DirectoryConfig -Filter "(objectClass=*)" -Scope base -Attributes @('dn')
            return
        }
        catch { Start-Sleep -Seconds 2 }
    }
    throw "The directory did not answer within 60 seconds of being unpaused"
}

# ─────────────────────────────────────────────────────────────────────────────────────────────
# Setup
# ─────────────────────────────────────────────────────────────────────────────────────────────

Write-TestSection "Scenario 023: Unique Value Generation"
Write-Host "Directory:   $($DirectoryConfig.ConnectedSystemName) ($($DirectoryConfig.DirectoryType))" -ForegroundColor Gray
Write-Host "Template:    $Template" -ForegroundColor Gray
Write-Host "Step:        $Step (steps are cumulative)" -ForegroundColor Gray
Write-Host ""

Write-TestSection "Step 0: Generating the HR CSV without IT-owned attributes"
& "$PSScriptRoot/../Get-OrGenerate-TestCSV.ps1" -Template $Template -OutputPath "$(Get-IntegrationTestDataPath)" -OmitItOwnedAttributes
Write-Host "  ✓ hr-users.csv generated without samAccountName/email/userPrincipalName" -ForegroundColor Green

Write-TestSection "Step 0b: Configuring JIM (Setup-Scenario-023.ps1)"
$config = & "$PSScriptRoot/../Setup-Scenario-023.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template -DirectoryConfig $DirectoryConfig
if (-not $config) {
    throw "Setup-Scenario-023.ps1 returned no configuration"
}
Write-Host "  ✓ JIM configured" -ForegroundColor Green

$modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

$stepOrder = @("Joiners", "Gates", "Stability", "Sequence", "Random", "ExportMode", "Brownfield", "StartAgain", "Failure", "SurfaceParity", "NeverReuse", "ProbeBrownfield", "ProbeRestrictedBind", "ProbeUnreachable")
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
        # Each joiner gets its own department: Active Directory builds the DN from CN={Display Name} in
        # an OU per department, so two same-named people in one department would collide on the DN
        # itself, a directory naming constraint unrelated to generation.
        $existingPerson = New-TestUser -Index 1
        Add-HrCsvJoiner -EmployeeId "EMP900001" -FirstName "Marisol" -LastName "Fenwick" -Department "Executive"
        Add-HrCsvJoiner -EmployeeId "EMP900002" -FirstName "Marisol" -LastName "Fenwick" -Department "Legal"
        Add-HrCsvJoiner -EmployeeId "EMP900003" -FirstName $existingPerson.FirstName -LastName $existingPerson.LastName -Department "Facilities"

        Invoke-Cycle -Config $config | Out-Null

        $population = Get-Population
        $marisolGroup = @($population | Where-Object { $_.attributes.'First Name' -eq 'Marisol' -and $_.attributes.'Last Name' -eq 'Fenwick' })
        Add-TestResult -Name "Two intra-batch joiners with the same name both projected" -Passed ($marisolGroup.Count -eq 2) `
            -Detail "Expected 2 Marisol Fenwick objects, found $($marisolGroup.Count)"

        $marisolValues = @($marisolGroup | ForEach-Object { $_.attributes.'Account Name'.ToLower() } | Sort-Object)
        $expectedMarisol = @('marisol.fenwick', 'marisol.fenwick1')
        Add-TestResult -Name "Intra-batch collision resolves to {marisol.fenwick, marisol.fenwick1}" -Passed (($marisolValues -join ',') -eq ($expectedMarisol -join ',')) `
            -Detail "Expected $($expectedMarisol -join ', '); got $($marisolValues -join ', ')"

        # Email is derived from the generated Account Name (Setup-Scenario-001.ps1 -DeriveFromAccountName),
        # so the suffix lands before the "@", not after it.
        $marisolEmails = @($marisolGroup | ForEach-Object { (Get-MvoAttributeValue -MvoId $_.id -AttributeName "Email").ToLower() } | Sort-Object)
        $expectedEmails = @(@('marisol.fenwick@panoply.local', 'marisol.fenwick1@panoply.local') | Sort-Object)
        Add-TestResult -Name "Same-name joiners get distinct Emails with the suffix before the '@'" -Passed (($marisolEmails -join ',') -eq ($expectedEmails -join ',')) `
            -Detail "Expected $($expectedEmails -join ', '); got $($marisolEmails -join ', ')"

        # Metaverse-Derived Attribute Flows (#1750; Setup-Scenario-023.ps1 composes Setup-Scenario-001.ps1
        # -DeriveFromAccountName): Email is derived from the generated Account Name and User Principal Name
        # from Email, so each person's three values carry the SAME suffix, whichever of the pair got it.
        # The set assertion above cannot tell that apart from two independent generations that happened to
        # agree; this pairs the values per person, in the Metaverse and in the directory.
        $suffixMismatches = @()
        foreach ($person in $marisolGroup) {
            $accountName = $person.attributes.'Account Name'
            $email = Get-MvoAttributeValue -MvoId $person.id -AttributeName "Email"
            $upn = Get-MvoAttributeValue -MvoId $person.id -AttributeName "User Principal Name"
            $expectedEmail = "$accountName@panoply.local"
            if ($email -ne $expectedEmail -or $upn -ne $expectedEmail) {
                $suffixMismatches += "Account Name '$accountName': Email '$email', User Principal Name '$upn' (expected '$expectedEmail' for both)"
            }
            $directoryUser = Get-LDAPUser -UserIdentifier $accountName -DirectoryConfig $DirectoryConfig
            if (-not $directoryUser) {
                $suffixMismatches += "Account Name '$accountName': no directory entry"
                continue
            }
            if ($directoryUser['mail'] -ne $expectedEmail) {
                $suffixMismatches += "Account Name '$accountName': directory mail '$($directoryUser['mail'])' (expected '$expectedEmail')"
            }
            if (-not $isRfcDirectory -and $directoryUser['userPrincipalName'] -ne $expectedEmail) {
                $suffixMismatches += "Account Name '$accountName': directory userPrincipalName '$($directoryUser['userPrincipalName'])' (expected '$expectedEmail')"
            }
        }
        Add-TestResult -Name "Email and User Principal Name follow each joiner's suffixed generated Account Name (derived, in the Metaverse and the directory)" `
            -Passed ($marisolGroup.Count -eq 2 -and $suffixMismatches.Count -eq 0) -Detail ($suffixMismatches -join '; ')

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
        Copy-CsvToConnectorFiles -SourcePath $csvPath

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
        Add-TestResult -Name "Every person has a Staff Number matching EMP-NNNNNN" -Passed ($badFormat.Count -eq 0) `
            -Detail "$($badFormat.Count) values do not match: $(($badFormat | ForEach-Object { $_.attributes.'Staff Number' }) -join ', ')"

        $empNumbers = @($population | ForEach-Object { $_.attributes.'Staff Number' })
        $dupeNumbers = @($empNumbers | Group-Object | Where-Object { $_.Count -gt 1 })
        Add-TestResult -Name "Staff Number is unique across the population" -Passed ($dupeNumbers.Count -eq 0) `
            -Detail "Duplicates: $(($dupeNumbers | ForEach-Object { $_.Name }) -join ', ')"

        $sequenceState = Get-JIMGeneratedValueSequence -SyncRuleId $config.ImportRuleId -MappingId $config.EmployeeNumberMappingId
        Add-TestResult -Name "Get-JIMGeneratedValueSequence reports AssignedCount matching the population size" -Passed ($sequenceState.assignedCount -eq $population.Count) `
            -Detail "Expected $($population.Count), got $($sequenceState.assignedCount)"

        # Raise Sequence Start: new joiners must skip ahead to (at least) the new start. The new start is set
        # relative to the live counter, because a start at or below the counter is a no-op by design, and the
        # counter's position depends on the template (a fixed 5000 is already behind it at Large).
        $raisedStart = [long]$sequenceState.nextNumber + 1000
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
    # Metaverse Object never receives the value.
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("ExportMode")) {
        Write-TestSection "Test 6: Export mode (preferredLanguage, Random Digits)"

        $population = Get-Population
        $sample = @($population | Select-Object -First ([Math]::Min(3, $population.Count)))
        $badPreferredLanguage = @()
        $exportedValues = @()
        foreach ($person in $sample) {
            $ldapUser = Get-LDAPUser -UserIdentifier $person.attributes.'Account Name' -DirectoryConfig $DirectoryConfig
            $preferredLanguage = if ($ldapUser) { $ldapUser['preferredLanguage'] } else { $null }
            if ($preferredLanguage -notmatch '^\d{6}$') {
                $badPreferredLanguage += "$($person.displayName): preferredLanguage='$preferredLanguage'"
            }
            else {
                $exportedValues += @{ MvoId = $person.id; DisplayName = $person.displayName; Value = $preferredLanguage }
            }
        }
        Add-TestResult -Name "Every sampled directory entry carries a 6-digit generated preferredLanguage" -Passed ($badPreferredLanguage.Count -eq 0) `
            -Detail ($badPreferredLanguage -join '; ')

        # Export-mode assignments are keyed on the Connected System Object, never the Metaverse
        # Object: the value generated for the directory must not appear on the Metaverse Object.
        $leaked = @()
        foreach ($entry in $exportedValues) {
            $mvo = Get-JIMMetaverseObject -Id $entry.MvoId
            $holding = @($mvo.attributeValues | Where-Object { $_.stringValue -eq $entry.Value })
            if ($holding.Count -gt 0) {
                $leaked += "$($entry.DisplayName): '$($entry.Value)' held by Metaverse attribute(s) $(($holding.attributeName | Sort-Object -Unique) -join ', ')"
            }
        }
        Add-TestResult -Name "The export-mode generated value is not written to the Metaverse Object" -Passed ($exportedValues.Count -gt 0 -and $leaked.Count -eq 0) `
            -Detail $(if ($exportedValues.Count -eq 0) { "No exported values were sampled" } else { $leaked -join '; ' })

        # Drift Detection (product-owner decision 2026-10-01): an export-mode generated value is checked like
        # any export Attribute Flow, so a value changed in the directory outside JIM is corrected back to the
        # assignment by the directory's own synchronisation, then exported. One object is enough.
        Write-TestSection "Test 6b: Export mode drift is corrected back to the assignment"
        if ($exportedValues.Count -gt 0) {
            $driftTarget = $exportedValues[0]
            $driftAccountName = ($population | Where-Object { $_.id -eq $driftTarget.MvoId } | Select-Object -First 1).attributes.'Account Name'
            $driftUser = Get-LDAPUser -UserIdentifier $driftAccountName -DirectoryConfig $DirectoryConfig
            $driftedValue = if ($driftTarget.Value -eq '000000') { '111111' } else { '000000' }
            $driftLdif = @("dn: $($driftUser['dn'])", "changetype: modify", "replace: preferredLanguage", "preferredLanguage: $driftedValue") -join "`n"
            $driftResult = Invoke-DirectoryLdif -DirectoryConfig $DirectoryConfig -Ldif $driftLdif -Operation modify
            if (-not $driftResult.Success) {
                throw "Failed to change preferredLanguage outside JIM for '$driftAccountName': $($driftResult.Output)"
            }

            foreach ($run in @(
                @{ Profile = $config.LDAPDeltaImportProfileId; Name = "Directory Delta Import (drift)" },
                @{ Profile = $config.LDAPDeltaSyncProfileId;   Name = "Directory Delta Sync (drift)" },
                @{ Profile = $config.LDAPExportProfileId;      Name = "Directory Export (drift correction)" },
                @{ Profile = $config.LDAPDeltaImportProfileId; Name = "Directory Delta Import (confirming)" },
                @{ Profile = $config.LDAPDeltaSyncProfileId;   Name = "Directory Delta Sync (confirming)" }
            )) {
                $r = Start-JIMRunProfile -ConnectedSystemId $config.LDAPSystemId -RunProfileId $run.Profile -Wait -PassThru
                Assert-ActivitySuccess -ActivityId $r.activityId -Name $run.Name
            }

            $correctedUser = Get-LDAPUser -UserIdentifier $driftAccountName -DirectoryConfig $DirectoryConfig
            $correctedValue = if ($correctedUser) { $correctedUser['preferredLanguage'] } else { $null }
            Add-TestResult -Name "A preferredLanguage changed in the directory outside JIM is corrected back to the generated value" `
                -Passed ($correctedValue -eq $driftTarget.Value) `
                -Detail "$($driftTarget.DisplayName): expected '$($driftTarget.Value)', directory holds '$correctedValue' (was changed to '$driftedValue')"
        }
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Brownfield: an existing directory account keeps its account name through Attribute
    # Priority, not through any special rule. The administrator adds an import Attribute Flow
    # from the directory at higher priority than the generated flow and initialises in the
    # documented order (Full Import everything, Full Synchronisation sources then targets, then
    # Export). Then (7b) the directory flow is disabled: the generated flow is the winning contributor
    # again and, like any Attribute Flow, contributes its own value (its existing assignment) over the
    # one the directory left behind; the ordinary export then renames the account (PRD FR 30's
    # behavioural implication). Nothing is adopted.
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("Brownfield")) {
        Write-TestSection "Test 7: Brownfield account kept by Attribute Priority"

        $dn = New-OutOfBandLdapAccount -AccountName "pashworth99" -FirstName "Percival" -LastName "Ashworth" -EmployeeIdValue "EMP900020"
        Write-Host "  Created out-of-band account: $dn" -ForegroundColor Gray
        Add-HrCsvJoiner -EmployeeId "EMP900020" -FirstName "Percival" -LastName "Ashworth"

        # The directory import Synchronisation Rule: no projection (HR is authoritative for people),
        # one Attribute Flow from the directory's account name attribute to Account Name, ranked
        # above the generated flow.
        $directoryImportRule = New-JIMSyncRule -Name "$($DirectoryConfig.ConnectedSystemName) Import Users (Account Name)" `
            -ConnectedSystemId $config.LDAPSystemId -ConnectedSystemObjectTypeId $config.LdapUserTypeId `
            -MetaverseObjectTypeId $config.MvUserTypeId -Direction Import -PassThru
        $directoryAccountNameMapping = New-JIMSyncRuleMapping -SyncRuleId $directoryImportRule.id `
            -TargetMetaverseAttributeId $config.AccountNameMvAttributeId `
            -SourceConnectedSystemAttributeId $config.AccountNameLdapAttributeId
        Set-JIMMetaverseAttributePriority -AttributeId $config.AccountNameMvAttributeId -ObjectTypeId $config.MvUserTypeId `
            -MappingId @($directoryAccountNameMapping.id, $config.AccountNameMappingId) | Out-Null
        $priority = @((Get-JIMMetaverseAttributePriority -AttributeId $config.AccountNameMvAttributeId -ObjectTypeId $config.MvUserTypeId).contributors)
        Add-TestResult -Name "The directory's Account Name flow is ranked above the generated flow" `
            -Passed ($priority.Count -ge 2 -and $priority[0].mappingId -eq $directoryAccountNameMapping.id -and $priority[1].mappingId -eq $config.AccountNameMappingId) `
            -Detail "Contributors: $(@($priority | ForEach-Object { "$($_.mappingId)=$($_.priority)" }) -join ', ')"

        $beforeNames = @{}
        foreach ($p in (Get-Population)) { $beforeNames[$p.id] = $p.attributes.'Account Name' }

        # Initialise in the documented order: Full Import every Connected System, Full
        # Synchronisation sources then targets, then Export.
        $initRuns = @(
            @{ System = $config.CSVSystemId;  Profile = $config.CSVImportProfileId;      Name = "HR CSV Full Import" }
            @{ System = $config.LDAPSystemId; Profile = $config.LDAPFullImportProfileId; Name = "Directory Full Import" }
            @{ System = $config.CSVSystemId;  Profile = $config.CSVSyncProfileId;        Name = "HR CSV Full Synchronisation" }
            @{ System = $config.LDAPSystemId; Profile = $config.LDAPFullSyncProfileId;   Name = "Directory Full Synchronisation" }
            @{ System = $config.LDAPSystemId; Profile = $config.LDAPExportProfileId;     Name = "Directory Export" }
            @{ System = $config.LDAPSystemId; Profile = $config.LDAPFullImportProfileId; Name = "Directory Full Import (confirming)" }
            @{ System = $config.LDAPSystemId; Profile = $config.LDAPFullSyncProfileId;   Name = "Directory Full Synchronisation (confirming)" }
        )
        foreach ($initRun in $initRuns) {
            $r = Start-JIMRunProfile -ConnectedSystemId $initRun.System -RunProfileId $initRun.Profile -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $r.activityId -Name $initRun.Name
        }

        $percival = @(Get-JIMMetaverseObject -AttributeName "Employee ID" -AttributeValue "EMP900020" -Attributes @("Account Name")) | Select-Object -First 1
        Assert-NotNull -Value $percival -Message "Percival Ashworth's Metaverse Object was projected"
        Add-TestResult -Name "Percival's Account Name is the existing account's 'pashworth99' (the higher-priority directory flow won)" `
            -Passed ($percival.attributes.'Account Name' -eq 'pashworth99') -Detail "Got '$($percival.attributes.'Account Name')'"

        $existing = Get-LDAPUser -UserIdentifier "pashworth99" -DirectoryConfig $DirectoryConfig
        $renamed = Get-LDAPUser -UserIdentifier "percival.ashworth" -DirectoryConfig $DirectoryConfig
        Add-TestResult -Name "The existing directory account was not renamed" -Passed ($null -ne $existing -and $null -eq $renamed) `
            -Detail "pashworth99 present: $($null -ne $existing); percival.ashworth present: $($null -ne $renamed)"

        $afterInit = @{}
        foreach ($p in (Get-Population)) { $afterInit[$p.id] = $p.attributes.'Account Name' }
        $changedDuringInit = @($beforeNames.Keys | Where-Object { $afterInit[$_] -ne $beforeNames[$_] })
        Add-TestResult -Name "No existing person's Account Name changed when the directory flow took priority" -Passed ($changedDuringInit.Count -eq 0) `
            -Detail "$($changedDuringInit.Count) changed: $(@($changedDuringInit | Select-Object -First 5 | ForEach-Object { "$($beforeNames[$_]) -> $($afterInit[$_])" }) -join '; ')"

        # 7b: disable the directory flow. The generated flow becomes the winning contributor again and
        # contributes its existing assignment, exactly as a direct or expression flow would contribute its
        # value (product-owner decision 2026-10-01). Everyone whose directory account already holds the
        # generated value is unchanged; Percival, whose account holds 'pashworth99' while HR's assignment
        # holds 'percival.ashworth' (generated in the initialisation's HR synchronisation, before the
        # directory joined and took the attribute over), is renamed by the ordinary export.
        Write-TestSection "Test 7b: Directory flow disabled; the generated flow takes the attribute back"
        $percivalAssignmentBefore = @(Get-JIMGeneratedValue -MetaverseObjectId $percival.id) | Where-Object { $_.attributeName -eq 'Account Name' }
        Add-TestResult -Name "Percival's generated Account Name assignment survived the directory taking the attribute over" `
            -Passed ($null -ne $percivalAssignmentBefore -and $percivalAssignmentBefore.value -eq 'percival.ashworth') `
            -Detail "Assignment: $($percivalAssignmentBefore | ConvertTo-Json -Compress)"

        Set-JIMSyncRuleMapping -SyncRuleId $directoryImportRule.id -MappingId $directoryAccountNameMapping.id -Enabled $false | Out-Null
        Invoke-Cycle -Config $config -FirstRun | Out-Null  # a configuration change needs a Full Synchronisation

        $afterWithdraw = @{}
        foreach ($p in (Get-Population)) { $afterWithdraw[$p.id] = $p.attributes.'Account Name' }
        $changedOnWithdraw = @($afterInit.Keys | Where-Object { $afterWithdraw[$_] -ne $afterInit[$_] })
        $othersChanged = @($changedOnWithdraw | Where-Object { $_ -ne $percival.id })
        Add-TestResult -Name "Disabling the directory flow changes nobody whose account already holds their generated value" -Passed ($othersChanged.Count -eq 0) `
            -Detail "$($othersChanged.Count) changed: $(@($othersChanged | Select-Object -First 5 | ForEach-Object { "$($afterInit[$_]) -> $($afterWithdraw[$_])" }) -join '; ')"
        Add-TestResult -Name "Percival's Account Name is his generated 'percival.ashworth' again (the generated flow won the attribute back)" `
            -Passed ($afterWithdraw[$percival.id] -eq 'percival.ashworth') -Detail "Got '$($afterWithdraw[$percival.id])'"

        $percivalAssignment = @(Get-JIMGeneratedValue -MetaverseObjectId $percival.id) | Where-Object { $_.attributeName -eq 'Account Name' }
        Add-TestResult -Name "Percival's assignment is the same one, unchanged (generate once; nothing adopted)" `
            -Passed ($null -ne $percivalAssignment -and $null -ne $percivalAssignmentBefore -and $percivalAssignment.assignmentId -eq $percivalAssignmentBefore.assignmentId -and $percivalAssignment.value -eq 'percival.ashworth') `
            -Detail "Assignment: $($percivalAssignment | ConvertTo-Json -Compress)"

        $renamedAccount = Get-LDAPUser -UserIdentifier "percival.ashworth" -DirectoryConfig $DirectoryConfig
        $oldAccount = Get-LDAPUser -UserIdentifier "pashworth99" -DirectoryConfig $DirectoryConfig
        Add-TestResult -Name "The ordinary export renamed the directory account to 'percival.ashworth'" `
            -Passed ($null -ne $renamedAccount -and $null -eq $oldAccount) `
            -Detail "percival.ashworth present: $($null -ne $renamedAccount); pashworth99 present: $($null -ne $oldAccount)"
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
        # The flow's configured Start as it stands now (the Sequence step raised it above the counter and then
        # lowered it to 1; lowering never moves the counter, but it is the value Start again returns to).
        $configuredStart = (@(Get-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId) | Where-Object { $_.id -eq $config.EmployeeNumberMappingId }).generation.sequenceStart

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

        # Call Sign (see Setup-Scenario-023.ps1): every object shares the one constant candidate
        # "CALLSIGN", with an attempt limit of 1. Enabling it against the whole existing population
        # in one run guarantees exactly one winner and every other object exhausted on its sole
        # attempt (the bare value is already taken, and no suffix attempt is allowed).
        Set-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId -MappingId $config.CallSignMappingId -Enabled $true | Out-Null

        $r = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $r.activityId -Name "HR CSV Full Import (Failure arrange)"
        $failureSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVSyncProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $failureSync.activityId -Name "HR CSV Full Synchronisation (Call Sign exhaustion)" -AllowWarnings

        $items = @(Get-JIMActivity -Id $failureSync.activityId -ExecutionItems)
        $exhausted = @($items | Where-Object { $_.errorType -eq 'GeneratedValueExhausted' })
        Add-TestResult -Name "At least one object failed with GeneratedValueExhausted" -Passed ($exhausted.Count -gt 0) `
            -Detail "Found $($exhausted.Count) items with that error type among $($items.Count) execution items"

        $population2 = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Attributes @("Call Sign") -All)
        $withValue = @($population2 | Where-Object { $_.attributes.PSObject.Properties['Call Sign'] -and $_.attributes.'Call Sign' -eq 'CALLSIGN' })
        Add-TestResult -Name "Exactly one object won the Call Sign value; nothing else was written for the rest" -Passed ($withValue.Count -eq 1) `
            -Detail "Expected exactly 1 object with Call Sign='CALLSIGN', found $($withValue.Count)"

        # Leave the mapping disabled again: it has done its job and would otherwise fail every
        # future joiner's synchronisation for the rest of this run.
        Set-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId -MappingId $config.CallSignMappingId -Enabled $false | Out-Null
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Surface parity: the same shape of mapping configured via raw REST and via PowerShell
    # produces identical (same-shaped) generated behaviour.
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("SurfaceParity")) {
        Write-TestSection "Test 10: Surface parity (raw REST vs PowerShell)"

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

        Invoke-Cycle -Config $config -FirstRun | Out-Null  # new mappings reach unchanged objects only through a Full Synchronisation

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
    # Never reuse (release 2, Phase 6): a leaver's generated values are retired, and a new
    # joiner with the same name is not given the leaver's Account Name; with "Never reuse a
    # value" off, nothing is retired and the same joiner gets it. After every step that checks the invariant, because a
    # retired value leaves a gap in a base's {base, base1, ...} set, which the Joiners and Gates
    # steps' invariant does not allow for.
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("NeverReuse")) {
        Write-TestSection "Test 11: Never reuse a value (retired values register)"

        # Immediate deletion for this step only (the 7-day grace period would merely schedule it); restored in
        # the finally block, whatever happens. Nobody has left before this step, so nothing is pending deletion
        # that zeroing the grace period could suddenly make eligible.
        $userType = Get-JIMMetaverseObjectType -Name "User"
        Assert-NotNull -Value $userType -Message "The 'User' Metaverse Object Type exists"
        Set-JIMMetaverseObjectType -Id $userType.id -DeletionGracePeriod ([TimeSpan]::Zero) | Out-Null
        try {
            # ─── 11a: name-based (Account Name, "only if taken") and random (Badge Code), Never reuse on ───
            Write-TestSection "Test 11a: A leaver's values are retired and not reissued"
            Add-HrCsvJoiner -EmployeeId "EMP900060" -FirstName "Ottoline" -LastName "Vantreight" -Department "Finance"
            Invoke-HrJoinerOrLeaverSync -Config $config -Context "Never reuse: first joiner"
            $leaver = Get-PersonByEmployeeId -EmployeeId "EMP900060"
            Assert-NotNull -Value $leaver -Message "The first Ottoline Vantreight was projected"
            Add-TestResult -Name "The first Ottoline Vantreight is given the bare base value" `
                -Passed ($leaver.attributes.'Account Name' -eq 'ottoline.vantreight') -Detail "Got '$($leaver.attributes.'Account Name')'"
            $leaverBadgeCode = $leaver.attributes.'Badge Code'
            $leaverStaffNumber = $leaver.attributes.'Staff Number'

            Remove-HrCsvRow -EmployeeId "EMP900060"
            Invoke-HrJoinerOrLeaverSync -Config $config -Context "Never reuse: leaver"
            Add-TestResult -Name "The leaver's Metaverse Object is deleted (zero grace period)" `
                -Passed ($null -eq (Get-PersonByEmployeeId -EmployeeId "EMP900060")) -Detail "The Metaverse Object still exists"

            $retiredAccountName = @(Get-JIMRetiredGeneratedValue -MetaverseAttributeName "Account Name" -Search "ottoline")
            Add-TestResult -Name "The leaver's Account Name is in the retired values register, as Object deleted" `
                -Passed ($retiredAccountName.Count -eq 1 -and $retiredAccountName[0].Value -eq 'ottoline.vantreight' -and $retiredAccountName[0].Reason -eq 'ObjectDeleted' -and -not $retiredAccountName[0].FromObjectExists) `
                -Detail "Register entries: $($retiredAccountName | ConvertTo-Json -Compress)"
            Add-TestResult -Name "The retired entry names its former holder and is read as deleted (HeldBy)" `
                -Passed ($retiredAccountName.Count -eq 1 -and $retiredAccountName[0].HeldBy -eq 'Ottoline Vantreight (deleted)') `
                -Detail "HeldBy: '$(if ($retiredAccountName.Count -gt 0) { $retiredAccountName[0].HeldBy })'"

            $retiredBadgeCode = @(Get-JIMRetiredGeneratedValue -MetaverseAttributeName "Badge Code" -Search $leaverBadgeCode)
            Add-TestResult -Name "The leaver's random Badge Code is retired too" `
                -Passed ($retiredBadgeCode.Count -eq 1 -and $retiredBadgeCode[0].Reason -eq 'ObjectDeleted') `
                -Detail "Badge Code '$leaverBadgeCode'; register entries: $($retiredBadgeCode | ConvertTo-Json -Compress)"
            $retiredStaffNumber = @(Get-JIMRetiredGeneratedValue -MetaverseAttributeName "Staff Number" -Search $leaverStaffNumber)
            Add-TestResult -Name "The leaver's Staff Number is retired (a Sequence always never reuses)" `
                -Passed ($retiredStaffNumber.Count -eq 1) -Detail "Staff Number '$leaverStaffNumber'; register entries: $($retiredStaffNumber.Count)"

            # REST read parity: the same register through the raw endpoint, with paging metadata.
            $accountNameAttribute = Get-JIMMetaverseAttribute | Where-Object { $_.name -eq "Account Name" } | Select-Object -First 1
            $rawRetired = Invoke-RawJimApi -Endpoint "/api/v1/metaverse/attributes/$($accountNameAttribute.id)/retired-generated-values?search=ottoline"
            Add-TestResult -Name "The REST register read returns the same retired Account Name (surface parity)" `
                -Passed ($null -ne $rawRetired -and @($rawRetired.items).Count -eq 1 -and @($rawRetired.items)[0].value -eq 'ottoline.vantreight') `
                -Detail "Raw response: $($rawRetired | ConvertTo-Json -Compress -Depth 4)"

            $mappingAfterRetire = @(Get-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId) | Where-Object { $_.id -eq $config.AccountNameMappingId }
            Add-TestResult -Name "The Account Name mapping reports its retired value count" `
                -Passed ($mappingAfterRetire.generation.retiredValueCount -ge 1) -Detail "retiredValueCount: $($mappingAfterRetire.generation.retiredValueCount)"

            Add-HrCsvJoiner -EmployeeId "EMP900061" -FirstName "Ottoline" -LastName "Vantreight" -Department "Finance"
            Invoke-HrJoinerOrLeaverSync -Config $config -Context "Never reuse: new joiner, same name"
            $rejoiner = Get-PersonByEmployeeId -EmployeeId "EMP900061"
            Assert-NotNull -Value $rejoiner -Message "The new Ottoline Vantreight was projected"
            Add-TestResult -Name "A new joiner with the leaver's name is NOT given the retired Account Name" `
                -Passed ($rejoiner.attributes.'Account Name' -eq 'ottoline.vantreight1') `
                -Detail "Expected 'ottoline.vantreight1' (the bare base is retired), got '$($rejoiner.attributes.'Account Name')'"
            Add-TestResult -Name "The new joiner's Badge Code is not the retired one" `
                -Passed ($rejoiner.attributes.'Badge Code' -and $rejoiner.attributes.'Badge Code' -ne $leaverBadgeCode) `
                -Detail "Retired '$leaverBadgeCode', new '$($rejoiner.attributes.'Badge Code')'"

            # ─── 11b: Never reuse off: nothing is retired, and the same name gets the same value ───
            Write-TestSection "Test 11b: With Never reuse off, a leaver's value is free again"
            Set-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId -MappingId $config.AccountNameMappingId -NeverReuse $false | Out-Null
            try {
                Add-HrCsvJoiner -EmployeeId "EMP900062" -FirstName "Wilhelmina" -LastName "Strachan" -Department "Legal"
                Invoke-HrJoinerOrLeaverSync -Config $config -Context "Reuse: first joiner"
                $reuseLeaver = Get-PersonByEmployeeId -EmployeeId "EMP900062"
                Assert-NotNull -Value $reuseLeaver -Message "The first Wilhelmina Strachan was projected"
                Add-TestResult -Name "The first Wilhelmina Strachan is given the bare base value" `
                    -Passed ($reuseLeaver.attributes.'Account Name' -eq 'wilhelmina.strachan') -Detail "Got '$($reuseLeaver.attributes.'Account Name')'"

                Remove-HrCsvRow -EmployeeId "EMP900062"
                Invoke-HrJoinerOrLeaverSync -Config $config -Context "Reuse: leaver"
                $notRetired = @(Get-JIMRetiredGeneratedValue -MetaverseAttributeName "Account Name" -Search "wilhelmina")
                Add-TestResult -Name "With Never reuse off, the leaver's Account Name is not retired" -Passed ($notRetired.Count -eq 0) `
                    -Detail "Register entries: $($notRetired | ConvertTo-Json -Compress)"

                Add-HrCsvJoiner -EmployeeId "EMP900063" -FirstName "Wilhelmina" -LastName "Strachan" -Department "Legal"
                Invoke-HrJoinerOrLeaverSync -Config $config -Context "Reuse: new joiner, same name"
                $reuseJoiner = Get-PersonByEmployeeId -EmployeeId "EMP900063"
                Assert-NotNull -Value $reuseJoiner -Message "The new Wilhelmina Strachan was projected"
                Add-TestResult -Name "With Never reuse off, a new joiner with the leaver's name IS given the leaver's Account Name" `
                    -Passed ($reuseJoiner.attributes.'Account Name' -eq 'wilhelmina.strachan') `
                    -Detail "Expected 'wilhelmina.strachan', got '$($reuseJoiner.attributes.'Account Name')'"
            }
            finally {
                Set-JIMSyncRuleMapping -SyncRuleId $config.ImportRuleId -MappingId $config.AccountNameMappingId -NeverReuse $true | Out-Null
            }

            # Provision the two remaining joiners, so the run ends with Metaverse and directory in step.
            Invoke-Cycle -Config $config | Out-Null
        }
        finally {
            Set-JIMMetaverseObjectType -Id $userType.id -DeletionGracePeriod ([TimeSpan]::FromDays(7)) | Out-Null
        }
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Probe brownfield (release 3, Phase 7): an account created outside JIM, in an OU outside
    # the import scope, holds exactly the value JIM would generate for a new joiner. JIM's own
    # records cannot know about it, so only the live probe of the directory can catch it.
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("ProbeBrownfield")) {
        Write-TestSection "Test 12: The probe catches an account JIM's own records do not hold"

        $brownfieldValue = 'thaddeus.quorne'
        $brownfieldDn = New-ProbeBrownfieldAccount -AccountName $brownfieldValue -FirstName "Thaddeus" -LastName "Quorne"
        Write-Host "  Created out-of-scope account outside JIM: $brownfieldDn" -ForegroundColor Gray
        $brownfieldBefore = Get-DirectoryEntry -DirectoryConfig $DirectoryConfig -Dn $brownfieldDn
        Assert-NotNull -Value $brownfieldBefore -Message "The brownfield account '$brownfieldDn' reads back"
        $snapshotBefore = ConvertTo-DirectoryEntrySnapshot -Entry $brownfieldBefore

        Add-HrCsvJoiner -EmployeeId "EMP900070" -FirstName "Thaddeus" -LastName "Quorne" -Department "Marketing"
        $import = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $import.activityId -Name "HR CSV Full Import (probe brownfield)"
        $probeSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
        $probeSyncActivity = Get-JIMActivity -Id $probeSync.activityId
        Add-TestResult -Name "The HR synchronisation that probed the directory completed with no warning" `
            -Passed ($probeSyncActivity.status -eq 'Complete') `
            -Detail "Status '$($probeSyncActivity.status)'; warning: '$($probeSyncActivity.warningMessage)'"

        $thaddeus = Get-PersonByEmployeeId -EmployeeId "EMP900070"
        Assert-NotNull -Value $thaddeus -Message "Thaddeus Quorne was projected"
        Add-TestResult -Name "The joiner is given '$($brownfieldValue)1', because the probe found '$brownfieldValue' in use in the directory" `
            -Passed ($thaddeus.attributes.'Account Name' -eq "$($brownfieldValue)1") -Detail "Got '$($thaddeus.attributes.'Account Name')'"

        # Before anything imports from the directory again: the local gates had nothing to go on.
        $heldByJim = Get-JimRecordsHoldingValue -Value $brownfieldValue
        Add-TestResult -Name "JIM's own records never held '$brownfieldValue' (only the probe could have caught it)" -Passed ($heldByJim -eq 0) `
            -Detail "$heldByJim row(s) in the Metaverse, Connector Space, assignments or retired register hold it"

        Invoke-Cycle -Config $config | Out-Null

        $provisioned = Get-LDAPUser -UserIdentifier "$($brownfieldValue)1" -DirectoryConfig $DirectoryConfig
        Add-TestResult -Name "The joiner is provisioned to the directory as '$($brownfieldValue)1'" `
            -Passed ($null -ne $provisioned -and $provisioned['dn'] -ne $brownfieldDn) -Detail "Directory entry: $(if ($provisioned) { $provisioned['dn'] } else { 'none' })"

        $brownfieldAfter = Get-DirectoryEntry -DirectoryConfig $DirectoryConfig -Dn $brownfieldDn
        $snapshotAfter = if ($brownfieldAfter) { ConvertTo-DirectoryEntrySnapshot -Entry $brownfieldAfter } else { @() }
        $differences = @(Compare-Object -ReferenceObject $snapshotBefore -DifferenceObject $snapshotAfter | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })
        Add-TestResult -Name "Every attribute of the brownfield account is unchanged" -Passed ($null -ne $brownfieldAfter -and $differences.Count -eq 0) `
            -Detail $(if ($null -eq $brownfieldAfter) { "The account no longer exists" } else { $differences -join '; ' })
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Probe restricted bind (Samba AD only): the Connected System binds as an account denied
    # List Contents on every OU, so the probe's search completes but cannot return the control
    # value JIM knows is there. The directory is undetermined for the run: one warning line, the
    # joiner's value comes from the local gates, and nothing fails.
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("ProbeRestrictedBind")) {
        Write-TestSection "Test 13: A bind that cannot see the directory's values degrades to the local gates"

        if ($DirectoryConfig.DirectoryType -ne 'SambaAD') {
            Write-Host "  Skipped: the restricted bind is built with samba-tool, so it runs on Samba AD only" -ForegroundColor Yellow
        }
        else {
            $restrictedSam = 'svc-jim-restricted'
            $restrictedDn = "CN=$restrictedSam,CN=Users,$($DirectoryConfig.BaseDN)"
            $restrictedPassword = 'Probe-Lab@4821!'
            $adminCredential = "Administrator%$($DirectoryConfig.BindPassword)"

            $restrictedUser = New-DirectoryUser -DirectoryConfig $DirectoryConfig -Dn $restrictedDn -SamAccountName $restrictedSam -Password $restrictedPassword
            if (-not $restrictedUser.Success) { throw "Failed to create '$restrictedDn': $($restrictedUser.Output)" }

            $sidRead = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig `
                -Command @('samba-tool', 'user', 'show', $restrictedSam, '--attributes=objectSid', '-H', 'ldap://localhost', '-U', $adminCredential)
            if ($sidRead.Output -notmatch 'objectSid: (S-1-5-[0-9-]+)') { throw "Could not read the objectSid of '$restrictedSam': $($sidRead.Output)" }
            $restrictedSid = $Matches[1]

            # An explicit List Contents deny on every OU. An inherited deny on reading sAMAccountName is not enough:
            # each user's explicit General Information read for Authenticated Users wins over an inherited deny.
            $ous = @(Find-DirectoryEntry -DirectoryConfig $DirectoryConfig -Filter "(objectClass=organizationalUnit)" -Attributes @('dn'))
            foreach ($ou in $ous) {
                $deny = Invoke-DirectoryContainerCommand -DirectoryConfig $DirectoryConfig `
                    -Command @('samba-tool', 'dsacl', 'set', '-H', 'ldap://localhost', '-U', $adminCredential, "--objectdn=$($ou.dn)", "--sddl=(D;;LC;;;$restrictedSid)")
                if ($deny.ExitCode -ne 0) { throw "Failed to deny List Contents on '$($ou.dn)' to '$restrictedSam': $($deny.Output)" }
            }
            Write-Host "  Denied List Contents to $restrictedSam on $($ous.Count) OU(s)" -ForegroundColor Gray

            # The arrangement itself: the restricted account binds, but cannot see an account JIM holds.
            $knownAccountName = (Get-PersonByEmployeeId -EmployeeId "EMP900070").attributes.'Account Name'
            $restrictedSearch = Invoke-DirectoryLdapTool -DirectoryConfig $DirectoryConfig -Tool ldapsearch -Arguments @(
                '-x', '-H', (Get-DirectoryToolUri -DirectoryConfig $DirectoryConfig), '-D', $restrictedDn, '-w', $restrictedPassword,
                '-LLL', '-b', $DirectoryConfig.BaseDN, "(sAMAccountName=$knownAccountName)", 'dn')
            Add-TestResult -Name "The restricted account binds but cannot see '$knownAccountName' (arrangement)" `
                -Passed ($restrictedSearch.ExitCode -eq 0 -and $restrictedSearch.Output -notmatch '(?m)^dn:') `
                -Detail "ldapsearch exit $($restrictedSearch.ExitCode): $($restrictedSearch.Output)"

            Add-HrCsvJoiner -EmployeeId "EMP900071" -FirstName "Marisol" -LastName "Fenwick" -Department "Finance"
            $import = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $import.activityId -Name "HR CSV Full Import (restricted bind)"

            Set-DirectoryBindAccount -Username $restrictedDn -Password $restrictedPassword
            try {
                $restrictedSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
            }
            finally {
                Set-DirectoryBindAccount -Username $DirectoryConfig.JimBindDN -Password $DirectoryConfig.JimBindPassword
            }

            $warningLine = Assert-SingleProbeWarning -ActivityId $restrictedSync.activityId -Context "Restricted bind"
            Add-TestResult -Name "The warning says the probe did not return a value JIM knows is there (the control value path)" `
                -Passed ($warningLine -match 'did not return a .+ value JIM knows is there') -Detail "Warning: '$warningLine'"

            $marisol = Get-PersonByEmployeeId -EmployeeId "EMP900071"
            Add-TestResult -Name "The joiner is given 'marisol.fenwick2' by the local gates" `
                -Passed ($null -ne $marisol -and $marisol.attributes.'Account Name' -eq 'marisol.fenwick2') `
                -Detail "Got '$(if ($marisol) { $marisol.attributes.'Account Name' })'"

            Invoke-Cycle -Config $config | Out-Null
            Add-TestResult -Name "With the lab account restored, the joiner is provisioned as 'marisol.fenwick2'" `
                -Passed ($null -ne (Get-LDAPUser -UserIdentifier 'marisol.fenwick2' -DirectoryConfig $DirectoryConfig)) -Detail "No directory entry"
        }
    }

    # ─────────────────────────────────────────────────────────────────────────────────────
    # Probe unreachable (container labs): the directory container is paused for one HR
    # synchronisation. Changing the port instead does not work: saving the Connected System
    # tests its connectivity, and the failure is logged at Error in jim.web.
    # ─────────────────────────────────────────────────────────────────────────────────────
    if ($lastStepIndex -ge $stepOrder.IndexOf("ProbeUnreachable")) {
        Write-TestSection "Test 14: An unreachable directory does not fail the run"

        if (-not $DirectoryConfig.ContainerName) {
            Write-Host "  Skipped: needs a directory container to pause" -ForegroundColor Yellow
        }
        else {
            Add-HrCsvJoiner -EmployeeId "EMP900072" -FirstName "Cordelia" -LastName "Thistlewood" -Department "Procurement"
            $import = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVImportProfileId -Wait -PassThru
            Assert-ActivitySuccess -ActivityId $import.activityId -Name "HR CSV Full Import (unreachable directory)"

            docker pause $DirectoryConfig.ContainerName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Failed to pause '$($DirectoryConfig.ContainerName)'" }
            try {
                $unreachableSync = Start-JIMRunProfile -ConnectedSystemId $config.CSVSystemId -RunProfileId $config.CSVDeltaSyncProfileId -Wait -PassThru
            }
            finally {
                docker unpause $DirectoryConfig.ContainerName | Out-Null
            }
            Wait-DirectoryAnswering

            $warningLine = Assert-SingleProbeWarning -ActivityId $unreachableSync.activityId -Context "Unreachable directory"
            Add-TestResult -Name "The warning says JIM could not connect to the directory" `
                -Passed ($warningLine -match 'for values already in use\. Connecting to it ') -Detail "Warning: '$warningLine'"

            $cordelia = Get-PersonByEmployeeId -EmployeeId "EMP900072"
            Add-TestResult -Name "The joiner is given 'cordelia.thistlewood' by the local gates" `
                -Passed ($null -ne $cordelia -and $cordelia.attributes.'Account Name' -eq 'cordelia.thistlewood') `
                -Detail "Got '$(if ($cordelia) { $cordelia.attributes.'Account Name' })'"

            Invoke-Cycle -Config $config | Out-Null
            Add-TestResult -Name "Once the directory is back, the joiner is provisioned as 'cordelia.thistlewood'" `
                -Passed ($null -ne (Get-LDAPUser -UserIdentifier 'cordelia.thistlewood' -DirectoryConfig $DirectoryConfig)) -Detail "No directory entry"
        }
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

Write-TestSection "Scenario 023 Summary"
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
Write-Host "✓ Unique Value Generation behaves as designed: tokens, gates, priority hand-over, stability," -ForegroundColor Green
Write-Host "  Start again, surface parity, the retired values register and the directory probe all hold." -ForegroundColor Green
exit 0
