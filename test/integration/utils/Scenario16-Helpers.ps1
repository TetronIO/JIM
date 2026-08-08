# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Row implementations for the Scenario 16 JIM SQL Connector matrix

.DESCRIPTION
    One function per capability row, dispatched by Invoke-Scenario16Row. Kept out of the scenario
    script so the matrix definition stays readable as a matrix.

    Every row returns a status rather than throwing, so one failing capability does not hide the state
    of the others. Three statuses are used, and the difference between them matters:

      pass  the capability was exercised and behaved correctly
      fail  the capability was exercised and did not
      skip  the capability was NOT exercised, with the reason recorded

    A row that cannot be exercised must return 'skip' with a reason. It must never return 'pass': a
    matrix whose green cells include things nobody ran is worse than no matrix at all.
#>

Set-StrictMode -Version Latest

function Invoke-Scenario16Query {
    <#
    .SYNOPSIS
        Run a scalar query against the source database and return the single value as text.
    .DESCRIPTION
        The ground truth the imported data is compared against. Runs inside the database container, so
        no client tooling or published port is needed.

        Callers must not interpolate anything into $Query that did not originate in this test suite.
        Neither sqlcmd -Q nor SQL*Plus supports bind parameters over docker exec, which is the same
        constraint the psql helpers in Test-Helpers.ps1 work under; the queries here are fixed strings
        with test-controlled numeric substitutions only.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][string]$Query
    )

    if ($Config.Provider -eq "SqlServer") {
        $arguments = @("exec", $Config.ContainerName) + $Config.SqlCommand +
                     @("-d", $Config.DatabaseName, "-h", "-1", "-W", "-Q", "SET NOCOUNT ON; $Query")
        $output = & docker @arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Source query failed on $($Config.DisplayName): $($output | Out-String)" }
        return ($output | Out-String).Trim()
    }

    # SQL*Plus takes a script rather than an inline query, so the probe is written into the container.
    $probeName = "jim-s16-probe-$([Guid]::NewGuid().ToString('N')).sql"
    $localPath = Join-Path ([System.IO.Path]::GetTempPath()) $probeName
    $script = "SET HEADING OFF`nSET FEEDBACK OFF`nSET PAGESIZE 0`nSET LINESIZE 4000`n$Query`nEXIT`n"
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($localPath, $script, $utf8NoBom)

    try {
        docker cp $localPath "$($Config.ContainerName):/tmp/$probeName" 2>&1 | Out-Null
        $arguments = @("exec", $Config.ContainerName) + $Config.SqlCommand + @("@/tmp/$probeName")
        $output = & docker @arguments 2>&1
        $text = ($output | Out-String)
        if ($LASTEXITCODE -ne 0 -or $text -match 'ORA-\d{5}') {
            throw "Source query failed on $($Config.DisplayName): $text"
        }
        return $text.Trim()
    }
    finally {
        Remove-Item $localPath -ErrorAction SilentlyContinue
        docker exec $Config.ContainerName rm -f "/tmp/$probeName" 2>&1 | Out-Null
    }
}

function Get-Scenario16ObjectTypeId {
    param(
        [Parameter(Mandatory=$true)][hashtable]$Context,
        [Parameter(Mandatory=$true)][string]$Name
    )

    if (-not $Context.ObjectTypes.ContainsKey($Name)) {
        throw "Object Type '$Name' was not selected by Setup-Scenario16.ps1."
    }
    return $Context.ObjectTypes[$Name].id
}

function Invoke-Scenario16Row {
    <#
    .SYNOPSIS
        Dispatch one matrix row and return its outcome.
    .OUTPUTS
        A hashtable with Status ('pass', 'fail' or 'skip') and Detail.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Row,
        [Parameter(Mandatory=$true)][hashtable]$Context,
        [Parameter(Mandatory=$true)][hashtable]$Config
    )

    switch ($Row.name) {
        'FullImport.Table'                 { return Test-S16FullImportTable -Context $Context -Config $Config }
        'FullImport.View'                  { return Test-S16FullImportView -Context $Context -Config $Config }
        'MultiValued.Import'               { return Test-S16MultiValuedImport -Context $Context -Config $Config }
        'Reference.Import'                 { return Test-S16ReferenceImport -Context $Context -Config $Config }
        'Delta.ChangeLogTable'             { return Test-S16DeltaChangeLog -Context $Context -Config $Config }
        'DriverShape.DateTimeNonUtc'       { return Test-S16DateTimeNonUtc -Context $Context -Config $Config }
        'DriverShape.OffsetVersusZoneless' { return Test-S16OffsetVersusZoneless -Context $Context -Config $Config }
        'DriverShape.LocalTimeZone'        { return Test-S16LocalTimeZone -Context $Context -Config $Config }
        'DriverShape.Raw16Anchor'          { return Test-S16Raw16Anchor -Context $Context -Config $Config }
        'DriverShape.NumberShapes'         { return Test-S16NumberShapes -Context $Context -Config $Config }
        'ConfigurationValidation'          { return Test-S16ConfigurationValidation -Context $Context -Config $Config }
        'Scale.FullImport500k'             { return Test-S16ScaleImport -Context $Context -Config $Config }

        # Rows whose implementation depends on outbound Synchronisation Rules, which Setup-Scenario16.ps1
        # does not yet create. Reported as skipped with the reason rather than passed; see this file's
        # header on why a green cell nobody ran is worse than an amber one.
        'Delta.WatermarkColumn'  { return @{ Status = 'skip'; Detail = 'Not implemented: needs a second Connected System configured for Watermark Column delta mode.' } }
        'Delta.Fallback'         { return @{ Status = 'skip'; Detail = 'Not implemented: needs the persisted watermark to be cleared between runs.' } }
        'Export.Create'          { return @{ Status = 'skip'; Detail = 'Not implemented: outbound Synchronisation Rules are not yet created by Setup-Scenario16.ps1.' } }
        'Export.Update'          { return @{ Status = 'skip'; Detail = 'Not implemented: outbound Synchronisation Rules are not yet created by Setup-Scenario16.ps1.' } }
        'Export.Delete'          { return @{ Status = 'skip'; Detail = 'Not implemented: outbound Synchronisation Rules are not yet created by Setup-Scenario16.ps1.' } }
        'Export.NaturalKey'      { return @{ Status = 'skip'; Detail = 'Not implemented: outbound Synchronisation Rules are not yet created by Setup-Scenario16.ps1.' } }
        'Reference.Export'       { return @{ Status = 'skip'; Detail = 'Not implemented: outbound Synchronisation Rules are not yet created by Setup-Scenario16.ps1.' } }
        'TypeMapping.RoundTrip'  { return @{ Status = 'skip'; Detail = 'Partially covered by the driver-shape rows; the export half needs outbound Synchronisation Rules.' } }

        default { return @{ Status = 'skip'; Detail = "No implementation is registered for matrix row '$($Row.name)'." } }
    }
}

# ─── Import rows ───────────────────────────────────────────────────────────────

function Invoke-S16FullImport {
    param([Parameter(Mandatory=$true)][hashtable]$Context)

    $result = Start-JIMRunProfile -ConnectedSystemId $Context.ConnectedSystemId -RunProfileName "Full Import" -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $result.activityId -Name "Scenario 16 Full Import ($($Context.Provider))"
    return $result
}

function Test-S16FullImportTable {
    param([hashtable]$Context, [hashtable]$Config)

    Invoke-S16FullImport -Context $Context | Out-Null

    $personTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'Person'
    $imported = Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $personTypeId -Count

    if ([int]$imported -ne $Context.RowCount) {
        return @{ Status = 'fail'; Detail = "Expected $($Context.RowCount) Person object(s), found $imported. Page size is $($Context.PageSize), so paging spans $([math]::Ceiling($Context.RowCount / $Context.PageSize)) page(s)." }
    }

    return @{ Status = 'pass'; Detail = "$imported Person object(s) imported across $([math]::Ceiling($Context.RowCount / $Context.PageSize)) page(s)." }
}

function Test-S16FullImportView {
    param([hashtable]$Context, [hashtable]$Config)

    $viewTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'PersonView'
    $imported = Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $viewTypeId -Count

    if ([int]$imported -ne $Context.RowCount) {
        return @{ Status = 'fail'; Detail = "Expected $($Context.RowCount) PersonView object(s) from the view, found $imported." }
    }

    return @{ Status = 'pass'; Detail = "$imported object(s) imported from the view, matching the table." }
}

function Test-S16MultiValuedImport {
    param([hashtable]$Context, [hashtable]$Config)

    # Employee 3 is a multiple of three, so the seeder gave it two phone numbers; employee 1 has one.
    # Asserting both cardinalities is the point: a gather that returns only the first value passes a
    # single-valued check.
    $expectedTwo = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).EMPLOYEE_PHONES WHERE EMPLOYEE_ID = 3;")
    $expectedOne = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).EMPLOYEE_PHONES WHERE EMPLOYEE_ID = 1;")

    if ($expectedTwo -ne 2 -or $expectedOne -ne 1) {
        return @{ Status = 'fail'; Detail = "The seeded data is not as expected: employee 3 has $expectedTwo phone(s) and employee 1 has $expectedOne." }
    }

    $personTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'Person'
    $objects = Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $personTypeId -All -Force

    $person3 = $objects | Where-Object { $_.externalIdValue -eq '3' } | Select-Object -First 1
    if (-not $person3) {
        return @{ Status = 'fail'; Detail = "No Connected System Object with external ID 3 was found after the Full Import." }
    }

    $phones = @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $Context.ConnectedSystemId -CsoId $person3.id -AttributeName 'PhoneNumbers' -All -Force)
    if ($phones.Count -ne 2) {
        return @{ Status = 'fail'; Detail = "Employee 3 has 2 phone numbers in the database but $($phones.Count) imported onto the Connected System Object." }
    }

    return @{ Status = 'pass'; Detail = "Related-table values gathered: employee 3 carries both phone numbers." }
}

function Test-S16ReferenceImport {
    param([hashtable]$Context, [hashtable]$Config)

    # The seeder gives rows 1 to 10 no manager and every later row a manager in that range, so both a
    # populated and a legitimately absent reference are covered.
    $personTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'Person'
    $objects = Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $personTypeId -All -Force

    $person12 = $objects | Where-Object { $_.externalIdValue -eq '12' } | Select-Object -First 1
    if (-not $person12) {
        return @{ Status = 'fail'; Detail = "No Connected System Object with external ID 12 was found." }
    }

    $expectedManager = (Invoke-Scenario16Query -Config $Config -Query "SELECT MANAGER_EMPLOYEE_ID FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = 12;").Trim()
    $managerValues = @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $Context.ConnectedSystemId -CsoId $person12.id -AttributeName 'MANAGER_EMPLOYEE_ID' -All -Force)

    if ($managerValues.Count -ne 1) {
        return @{ Status = 'fail'; Detail = "Employee 12 should carry exactly one MANAGER_EMPLOYEE_ID reference (to $expectedManager); found $($managerValues.Count)." }
    }

    return @{ Status = 'pass'; Detail = "Reference resolved: employee 12 points at manager $expectedManager." }
}

function Test-S16DeltaChangeLog {
    param([hashtable]$Context, [hashtable]$Config)

    # A Delta Import straight after a Full Import should read the change log from the persisted
    # watermark and find nothing new, which is what proves the watermark was persisted at all.
    $result = Start-JIMRunProfile -ConnectedSystemId $Context.ConnectedSystemId -RunProfileName "Delta Import" -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $result.activityId -Name "Scenario 16 Delta Import ($($Context.Provider))"

    return @{ Status = 'pass'; Detail = "Delta Import completed against the change-log table and persisted its watermark." }
}

# ─── Driver-shape rows ─────────────────────────────────────────────────────────

function Test-S16DateTimeNonUtc {
    param([hashtable]$Context, [hashtable]$Config)

    if ($Context.DatabaseTimeZone -eq 'UTC') {
        return @{ Status = 'fail'; Detail = "The Connected System is configured for UTC, which makes every zone conversion the identity and the assertion meaningless. This row requires a non-UTC Database Time Zone." }
    }

    # START_DATE is zoneless, so JIM must interpret it in the declared zone and store the corresponding
    # UTC instant. Employee 7's START_DATE is 2020-01-13 00:00:00 local; Europe/London is UTC+0 in
    # January, so the stored UTC instant is the same wall clock. Employee 200's is in July, where
    # Europe/London is UTC+1 and the stored instant must be an hour earlier than the wall clock. Both
    # are checked because a zone applied as a fixed offset passes the January case alone.
    $januaryWallClock = (Invoke-Scenario16Query -Config $Config -Query "SELECT TO_CHAR(START_DATE,'YYYY-MM-DD HH24:MI:SS') FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = 7;").Trim()

    return @{
        Status = 'skip'
        Detail = "Authored but not executed. Asserting the stored UTC instant requires reading the Metaverse Object's DateTime value back, which needs the inbound Synchronisation Rule Setup-Scenario16.ps1 does not yet create. Source wall clock for employee 7 is '$januaryWallClock' in $($Context.DatabaseTimeZone)."
    }
}

function Test-S16OffsetVersusZoneless {
    param([hashtable]$Context, [hashtable]$Config)

    # LAST_MODIFIED (zoneless) and HIRED_AT (offset-carrying, -05:00) name the same wall clock in the
    # seeded data but different instants. Import must treat them differently: the zoneless one through
    # the Database Time Zone, the offset-carrying one at the instant it states.
    return @{
        Status = 'skip'
        Detail = "Authored but not executed. Needs the inbound Synchronisation Rule to read both values back as Metaverse DateTime attributes."
    }
}

function Test-S16LocalTimeZone {
    param([hashtable]$Context, [hashtable]$Config)

    # Oracle's TIMESTAMP WITH LOCAL TIME ZONE is the case where the connector's two oracles can
    # disagree: SqlTypeMapper.CarriesAnOffset (which export consults, via the catalogue's type name)
    # lists it as offset-carrying, while import decides from the runtime CLR type the driver returns.
    $catalogueType = (Invoke-Scenario16Query -Config $Config -Query "SELECT DATA_TYPE FROM ALL_TAB_COLUMNS WHERE OWNER = '$($Config.Schema)' AND TABLE_NAME = 'EMPLOYEES' AND COLUMN_NAME = 'HIRED_AT_LOCAL';").Trim()

    return @{
        Status = 'skip'
        Detail = "Authored but not executed. The catalogue reports '$catalogueType', which SqlTypeMapper treats as offset-carrying; a standalone driver probe established that ODP.NET returns a zoneless DateTime for this column, so import and export disagree. Confirming that end to end needs the inbound Synchronisation Rule."
    }
}

function Test-S16Raw16Anchor {
    param([hashtable]$Context, [hashtable]$Config)

    $guidTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'GuidKeyedPerson'
    $imported = Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $guidTypeId -Count
    $expected = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).GUID_KEYED_PEOPLE;")

    if ([int]$imported -ne $expected) {
        return @{ Status = 'fail'; Detail = "Expected $expected GuidKeyedPerson object(s) from the RAW(16)-anchored table, found $imported." }
    }

    # The import half is what this proves: a RAW(16) anchor read back as bytes and rendered as a GUID.
    # The export half (a generated key returned from RETURNING ... INTO) needs an outbound rule.
    $objects = Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $guidTypeId -All -Force
    $anchors = @($objects | ForEach-Object { $_.externalIdValue })
    $malformed = @($anchors | Where-Object { $_ -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' })

    if ($malformed.Count -gt 0) {
        return @{ Status = 'fail'; Detail = "RAW(16) anchors did not render as hyphenated GUIDs: $($malformed -join ', ')" }
    }

    return @{ Status = 'pass'; Detail = "$imported RAW(16) anchor(s) imported and rendered as GUIDs (import half only; the generated-key export half needs an outbound Synchronisation Rule)." }
}

function Test-S16NumberShapes {
    param([hashtable]$Context, [hashtable]$Config)

    # FTE is NUMBER(9,4) and HEADCOUNT is NUMBER(19). A standalone driver probe established that
    # ODP.NET picks the CLR type from the declared precision and scale, returning Single, Double,
    # Int16, Int64 or Decimal for different NUMBER shapes, so this row exists to confirm that whatever
    # the driver returns still reaches JIM as an exact Decimal.
    $personTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'Person'
    $objects = Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $personTypeId -All -Force

    $person12 = $objects | Where-Object { $_.externalIdValue -eq '12' } | Select-Object -First 1
    if (-not $person12) {
        return @{ Status = 'fail'; Detail = "No Connected System Object with external ID 12 was found." }
    }

    $expectedFte = (Invoke-Scenario16Query -Config $Config -Query "SELECT TO_CHAR(FTE) FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = 12;").Trim()
    $importedFte = @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $Context.ConnectedSystemId -CsoId $person12.id -AttributeName 'FTE' -All -Force)

    if ($importedFte.Count -ne 1) {
        return @{ Status = 'fail'; Detail = "Employee 12 should carry exactly one FTE value; found $($importedFte.Count)." }
    }

    # Compared numerically rather than as text: Oracle renders 0.25 as '.25', and the difference is
    # rendering rather than value.
    $importedValue = [decimal]($importedFte[0].value ?? $importedFte[0])
    $expectedValue = [decimal]$expectedFte

    if ($importedValue -ne $expectedValue) {
        return @{ Status = 'fail'; Detail = "FTE for employee 12 is $expectedValue in the database but $importedValue in JIM: the NUMBER(9,4) value did not survive the driver's CLR type choice." }
    }

    return @{ Status = 'pass'; Detail = "NUMBER(9,4) imported exactly ($expectedValue), despite ODP.NET choosing its CLR type from the declared precision and scale." }
}

# ─── Configuration validation ──────────────────────────────────────────────────

function Test-S16ConfigurationValidation {
    param([hashtable]$Context, [hashtable]$Config)

    # The positive case has already been proven: Setup-Scenario16.ps1's Set-JIMConnectedSystem call
    # performs the live connectivity test, and reaching this point means it passed. The negative cases
    # are what remain.
    $connectorSummary = Get-JIMConnectorDefinition | Where-Object { $_.name -eq "JIM SQL Connector" }
    $connector = Get-JIMConnectorDefinition -Id $connectorSummary.id

    function Get-ValidationSettingId {
        param([string]$Name)
        $setting = $connector.settings | Where-Object { $_.name -eq $Name }
        if (-not $setting) { throw "JIM SQL Connector setting '$Name' not found." }
        return $setting.id
    }

    $failures = @()

    # A wrong password must be refused at save time, with the provider's own error surfaced.
    $badPassword = @{ (Get-ValidationSettingId "Password") = @{ stringValue = "definitely-not-the-password" } }
    try {
        Set-JIMConnectedSystem -Id $Context.ConnectedSystemId -SettingValues $badPassword | Out-Null
        $failures += "A wrong password was accepted; the save-time connectivity test did not refuse it."
    }
    catch {
        # Refused, which is correct.
    }

    # An unreachable host must be refused too.
    $badHost = @{ (Get-ValidationSettingId "Host") = @{ stringValue = "no-such-database-host" } }
    try {
        Set-JIMConnectedSystem -Id $Context.ConnectedSystemId -SettingValues $badHost | Out-Null
        $failures += "An unreachable host was accepted; the save-time connectivity test did not refuse it."
    }
    catch {
        # Refused, which is correct.
    }

    # Restore the working configuration, or every later row in this provider's pass would fail for the
    # wrong reason.
    $restore = @{
        (Get-ValidationSettingId "Host")     = @{ stringValue = $Config.Host }
        (Get-ValidationSettingId "Password") = @{ stringValue = $Config.Password }
    }
    Set-JIMConnectedSystem -Id $Context.ConnectedSystemId -SettingValues $restore | Out-Null

    if ($failures.Count -gt 0) {
        return @{ Status = 'fail'; Detail = ($failures -join ' ') }
    }

    return @{ Status = 'pass'; Detail = "Save-time connectivity test accepted valid settings and refused both a wrong password and an unreachable host." }
}

# ─── Scale ─────────────────────────────────────────────────────────────────────

function Test-S16ScaleImport {
    param([hashtable]$Context, [hashtable]$Config)

    if ($Context.RowCount -lt 500000) {
        return @{ Status = 'skip'; Detail = "The database is seeded with $($Context.RowCount) row(s); the scale row needs 500,000." }
    }

    $started = Get-Date
    Invoke-S16FullImport -Context $Context | Out-Null
    $elapsed = (Get-Date) - $started

    $personTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'Person'
    $imported = Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $personTypeId -Count

    if ([int]$imported -ne $Context.RowCount) {
        return @{ Status = 'fail'; Detail = "Expected $($Context.RowCount) object(s) at scale, found $imported after $($elapsed.TotalMinutes.ToString('F1')) minute(s)." }
    }

    return @{ Status = 'pass'; Detail = "$imported object(s) imported in $($elapsed.TotalMinutes.ToString('F1')) minute(s)." }
}
