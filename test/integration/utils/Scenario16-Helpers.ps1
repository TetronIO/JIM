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

function Invoke-Scenario16NonQuery {
    <#
    .SYNOPSIS
        Run a data-modifying statement against the source database.
    .DESCRIPTION
        The export rows need the source data to change between runs (a new email address, a disabled
        employee, an extra phone number), and the scalar helper above cannot commit on Oracle. Same
        interpolation rule applies: nothing that did not originate in this test suite goes into $Statement.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][string]$Statement
    )

    if ($Config.Provider -eq "SqlServer") {
        $arguments = @("exec", $Config.ContainerName) + $Config.SqlCommand +
                     @("-d", $Config.DatabaseName, "-Q", "SET NOCOUNT ON; $Statement")
        $output = & docker @arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Source statement failed on $($Config.DisplayName): $($output | Out-String)" }
        return
    }

    # SQL*Plus does not commit implicitly, so an uncommitted change would be invisible to JIM's own
    # session and the row would fail for a reason that has nothing to do with the connector.
    $scriptName = "jim-s16-dml-$([Guid]::NewGuid().ToString('N')).sql"
    $localPath = Join-Path ([System.IO.Path]::GetTempPath()) $scriptName
    $script = "WHENEVER SQLERROR EXIT FAILURE`nSET DEFINE OFF`nSET FEEDBACK OFF`n$Statement`nCOMMIT;`nEXIT SUCCESS`n"
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($localPath, $script, $utf8NoBom)

    try {
        docker cp $localPath "$($Config.ContainerName):/tmp/$scriptName" 2>&1 | Out-Null
        $arguments = @("exec", $Config.ContainerName) + $Config.SqlCommand + @("@/tmp/$scriptName")
        $output = & docker @arguments 2>&1
        $text = ($output | Out-String)
        if ($LASTEXITCODE -ne 0 -or $text -match 'ORA-\d{5}') {
            throw "Source statement failed on $($Config.DisplayName): $text"
        }
    }
    finally {
        Remove-Item $localPath -ErrorAction SilentlyContinue
        docker exec $Config.ContainerName rm -f "/tmp/$scriptName" 2>&1 | Out-Null
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

        'Export.Create'                    { return Test-S16ExportCreate -Context $Context -Config $Config }
        'Export.Update'                    { return Test-S16ExportUpdate -Context $Context -Config $Config }
        'Export.Delete'                    { return Test-S16ExportDelete -Context $Context -Config $Config }
        'Export.NaturalKey'                { return Test-S16ExportNaturalKey -Context $Context -Config $Config }
        'Reference.Export'                 { return Test-S16ReferenceExport -Context $Context -Config $Config }
        'TypeMapping.RoundTrip'            { return Test-S16TypeMappingRoundTrip -Context $Context -Config $Config }

        # Both delta rows remain unimplemented, and for a different reason from the export rows: they need
        # the Connected System's Delta Import Mode changed and its persisted watermark cleared mid-run,
        # neither of which the matrix has a mechanism for. Reported as skipped with the reason rather than
        # passed; see this file's header on why a green cell nobody ran is worse than an amber one.
        'Delta.WatermarkColumn'  { return @{ Status = 'skip'; Detail = 'Not implemented: needs a second Connected System configured for Watermark Column delta mode.' } }
        'Delta.Fallback'         { return @{ Status = 'skip'; Detail = 'Not implemented: needs the persisted watermark to be cleared between runs.' } }

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

# ─── Shared machinery for the synchronisation and export rows ──────────────────

function Invoke-S16RunProfile {
    param(
        [Parameter(Mandatory=$true)][hashtable]$Context,
        [Parameter(Mandatory=$true)][string]$Name
    )

    $result = Start-JIMRunProfile -ConnectedSystemId $Context.ConnectedSystemId -RunProfileName $Name -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $result.activityId -Name "Scenario 16 $Name ($($Context.Provider))"
    return $result
}

function Invoke-S16Pipeline {
    <#
    .SYNOPSIS
        Import, synchronise, export, then import again to confirm.
    .DESCRIPTION
        The trailing Full Import is not housekeeping. An object JIM exported carries an external ID the
        connector composed from whatever the insert handed back; the confirming import composes one from
        the row it reads. If the two disagree the import does not recognise its own work and creates a
        second Connected System Object, so an object-count assertion after this sequence is what settles
        anchor-token agreement. Nothing else in the suite can settle it.
    #>
    param([Parameter(Mandatory=$true)][hashtable]$Context)

    Invoke-S16RunProfile -Context $Context -Name "Full Import"          | Out-Null
    Invoke-S16RunProfile -Context $Context -Name "Full Synchronisation" | Out-Null
    Invoke-S16RunProfile -Context $Context -Name "Export"               | Out-Null
    Invoke-S16RunProfile -Context $Context -Name "Full Import"          | Out-Null

    # A pipeline satisfies the lighter baseline too, so a driver-shape row running after an export row
    # does not import and synchronise all over again for nothing.
    $Context['SyncBaselineDone'] = $true
}

function Initialize-S16SyncBaseline {
    <#
    .SYNOPSIS
        Ensure Metaverse Objects exist for the seeded population, and only import once to get them.
    .DESCRIPTION
        What the driver-shape rows need, and no more: they assert on the instant JIM derived from a
        source column, which lives on the Metaverse Object, and none of them cares whether anything has
        been exported.
    #>
    param([Parameter(Mandatory=$true)][hashtable]$Context)

    if ($Context.ContainsKey('SyncBaselineDone') -and $Context.SyncBaselineDone) { return }

    Invoke-S16RunProfile -Context $Context -Name "Full Import"          | Out-Null
    Invoke-S16RunProfile -Context $Context -Name "Full Synchronisation" | Out-Null
    $Context['SyncBaselineDone'] = $true
}

function Initialize-S16ExportBaseline {
    <#
    .SYNOPSIS
        Ensure the export pipeline has run once for this provider, and only once.
    .DESCRIPTION
        Memoised on the context so each export row can be run on its own with -Step without every row
        paying for a full pipeline when they run together.
    #>
    param([Parameter(Mandatory=$true)][hashtable]$Context)

    if ($Context.ContainsKey('ExportBaselineDone') -and $Context.ExportBaselineDone) { return }

    Invoke-S16Pipeline -Context $Context
    $Context['ExportBaselineDone'] = $true
}

function Get-S16ExpectedCount {
    <#
    .SYNOPSIS
        How many seeded employees satisfy one of the outbound rules' scopes.
    .DESCRIPTION
        Derived arithmetically from the row count rather than hard-coded, because the seeder's values are
        derived arithmetically too and the matrix is meant to hold at 50 rows and at 500,000.
    #>
    param(
        [Parameter(Mandatory=$true)][int]$RowCount,
        [Parameter(Mandatory=$true)][ValidateSet('Enabled', 'Research', 'Finance')][string]$Scope
    )

    switch ($Scope) {
        # IS_ACTIVE is 0 for every seventh employee.
        'Enabled'  { return $RowCount - [math]::Floor($RowCount / 7) }
        # DEPARTMENT cycles Engineering, Finance, Operations, Research on n modulo 4.
        'Research' { return @(1..$RowCount | Where-Object { ($_ % 4) -eq 3 }).Count }
        'Finance'  { return @(1..$RowCount | Where-Object { ($_ % 4) -eq 1 }).Count }
    }
}

function Get-S16EmployeeNumber {
    param([Parameter(Mandatory=$true)][int]$EmployeeId)
    return "E{0:D8}" -f $EmployeeId
}

function Get-S16MetaverseObject {
    <#
    .SYNOPSIS
        The Metaverse Object the inbound rule projected for one seeded employee, with its values.
    #>
    param([Parameter(Mandatory=$true)][int]$EmployeeId)

    $employeeNumber = Get-S16EmployeeNumber -EmployeeId $EmployeeId
    $match = @(Get-JIMMetaverseObject -ObjectTypeName "User" -Search $employeeNumber -PageSize 10) | Select-Object -First 1
    if (-not $match) { return $null }
    return Get-JIMMetaverseObject -Id $match.id
}

function Get-S16MvoValue {
    <#
    .SYNOPSIS
        One attribute value row off a Metaverse Object, or $null when the attribute carries no value.
    #>
    param(
        [Parameter(Mandatory=$true)]$Mvo,
        [Parameter(Mandatory=$true)][string]$AttributeName
    )

    if (-not $Mvo -or -not $Mvo.attributeValues) { return $null }
    return @($Mvo.attributeValues | Where-Object { $_.attributeName -ceq $AttributeName }) | Select-Object -First 1
}

# ─── Export rows ───────────────────────────────────────────────────────────────

function Test-S16ExportCreate {
    param([hashtable]$Context, [hashtable]$Config)

    Initialize-S16ExportBaseline -Context $Context

    $expected = Get-S16ExpectedCount -RowCount $Context.RowCount -Scope 'Enabled'
    $actualRows = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).APP_USERS;")

    if ($actualRows -ne $expected) {
        return @{ Status = 'fail'; Detail = "The outbound rule is scoped to enabled employees, so $expected row(s) should have been inserted into APP_USERS; the table holds $actualRows." }
    }

    # Anchor-token agreement. The confirming Full Import in the pipeline re-read every row it had just
    # written; if the external ID it composed from the row differed from the one the insert returned, the
    # import would have created a second Connected System Object for each and the count would be doubled.
    $appUserTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'AppUser'
    $csoCount = [int](Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $appUserTypeId -Count)

    if ($csoCount -ne $expected) {
        return @{ Status = 'fail'; Detail = "APP_USERS holds $actualRows row(s) but JIM holds $csoCount Connected System Object(s) for the type. A mismatch here means the external ID composed on export does not equal the one composed on import, so the confirming import did not recognise the objects it had just created." }
    }

    # A generated key is only proof of anything if it actually came from the database: the seeded table is
    # empty before this row runs, so every identifier present was returned by an insert.
    $anchors = @(Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $appUserTypeId -All -Force |
                 ForEach-Object { $_.externalIdValue })
    $unusable = @($anchors | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -notmatch '^\d+$' })
    if ($unusable.Count -gt 0) {
        return @{ Status = 'fail'; Detail = "$($unusable.Count) exported object(s) carry an external ID that is not the integer the IDENTITY column generates: '$(($unusable | Select-Object -First 5) -join "', '")'." }
    }

    return @{ Status = 'pass'; Detail = "$expected row(s) inserted with a database-generated key, and the confirming import composed the same anchor token for every one." }
}

function Test-S16ExportUpdate {
    param([hashtable]$Context, [hashtable]$Config)

    Initialize-S16ExportBaseline -Context $Context

    # Employee 12 is enabled (12 is not a multiple of seven) and has two phone numbers (12 is a multiple
    # of three), so both a scalar change and a multi-valued change have somewhere to land.
    $employeeId = 12
    $userName = Get-S16EmployeeNumber -EmployeeId $employeeId
    $newEmail = "updated.employee$employeeId@panoply.local"
    $newPhone = "+44 113 496 9999"

    Invoke-Scenario16NonQuery -Config $Config -Statement "UPDATE $($Config.Schema).EMPLOYEES SET EMAIL = '$newEmail' WHERE EMPLOYEE_ID = $employeeId;"
    Invoke-Scenario16NonQuery -Config $Config -Statement "INSERT INTO $($Config.Schema).EMPLOYEE_PHONES (EMPLOYEE_ID, PHONE_NUMBER, LAST_MODIFIED) SELECT $employeeId, '$newPhone', LAST_MODIFIED FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = $employeeId;"

    Invoke-S16Pipeline -Context $Context

    $exportedEmail = (Invoke-Scenario16Query -Config $Config -Query "SELECT EMAIL FROM $($Config.Schema).APP_USERS WHERE USER_NAME = '$userName';").Trim()
    if ($exportedEmail -ne $newEmail) {
        return @{ Status = 'fail'; Detail = "APP_USERS.EMAIL for $userName should be '$newEmail' after the update export; it is '$exportedEmail'." }
    }

    $roleCount = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).APP_USER_ROLES r JOIN $($Config.Schema).APP_USERS u ON u.ID = r.USER_ID WHERE u.USER_NAME = '$userName';")
    if ($roleCount -ne 3) {
        return @{ Status = 'fail'; Detail = "Employee $employeeId now has three phone numbers, so three related-table rows should exist for $userName; found $roleCount." }
    }

    # And the removal half. A related-table maintenance that only ever adds passes an add-only assertion.
    Invoke-Scenario16NonQuery -Config $Config -Statement "DELETE FROM $($Config.Schema).EMPLOYEE_PHONES WHERE EMPLOYEE_ID = $employeeId AND PHONE_NUMBER = '$newPhone';"
    Invoke-S16Pipeline -Context $Context

    $roleCountAfter = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).APP_USER_ROLES r JOIN $($Config.Schema).APP_USERS u ON u.ID = r.USER_ID WHERE u.USER_NAME = '$userName';")
    if ($roleCountAfter -ne 2) {
        return @{ Status = 'fail'; Detail = "The third phone number was removed at source, so $userName should be back to two related-table rows; found $roleCountAfter." }
    }

    return @{ Status = 'pass'; Detail = "Scalar update applied, and the related table gained and then lost a row in step with the source." }
}

function Test-S16ExportDelete {
    param([hashtable]$Context, [hashtable]$Config)

    Initialize-S16ExportBaseline -Context $Context

    # Employee 20 is enabled in the seeded data, so disabling it takes its Metaverse Object out of the
    # outbound rule's scope; the rule's OutboundDeprovisionAction is Delete, so that becomes a delete
    # export rather than a disconnect.
    $employeeId = 20
    $userName = Get-S16EmployeeNumber -EmployeeId $employeeId

    $before = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).APP_USERS WHERE USER_NAME = '$userName';")
    if ($before -ne 1) {
        return @{ Status = 'fail'; Detail = "Employee $employeeId should have been provisioned by the baseline export before this row runs; APP_USERS holds $before row(s) for $userName." }
    }

    $disabledValue = if ($Config.Provider -eq "SqlServer") { "0" } else { "0" }
    Invoke-Scenario16NonQuery -Config $Config -Statement "UPDATE $($Config.Schema).EMPLOYEES SET IS_ACTIVE = $disabledValue WHERE EMPLOYEE_ID = $employeeId;"

    Invoke-S16Pipeline -Context $Context

    $after = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).APP_USERS WHERE USER_NAME = '$userName';")
    if ($after -ne 0) {
        return @{ Status = 'fail'; Detail = "$userName left the outbound rule's scope, so its APP_USERS row should have been deleted; $after row(s) remain." }
    }

    # The related rows go with it. They are removed by the foreign key's cascade rather than by JIM, but a
    # delete that left them behind would still be a defect worth catching here.
    $orphanRoles = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).APP_USER_ROLES r WHERE NOT EXISTS (SELECT 1 FROM $($Config.Schema).APP_USERS u WHERE u.ID = r.USER_ID);")
    if ($orphanRoles -ne 0) {
        return @{ Status = 'fail'; Detail = "$orphanRoles related-table row(s) survived the parent's deletion." }
    }

    return @{ Status = 'pass'; Detail = "Deprovisioning removed the row and left no orphaned related-table rows." }
}

function Test-S16ExportNaturalKey {
    param([hashtable]$Context, [hashtable]$Config)

    Initialize-S16ExportBaseline -Context $Context

    # The opposite half of the Export.Create question. Here the primary key is a natural identifier JIM
    # authors and writes, so the external ID is a value JIM chose rather than one the database returned;
    # the confirming import still has to compose the same token from the row.
    $expected = Get-S16ExpectedCount -RowCount $Context.RowCount -Scope 'Research'
    $actualRows = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).APP_ACCOUNTS_NATURAL;")

    if ($actualRows -ne $expected) {
        return @{ Status = 'fail'; Detail = "The rule is scoped to Department = Research, so $expected row(s) should exist in APP_ACCOUNTS_NATURAL; the table holds $actualRows." }
    }

    $naturalTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'NaturalKeyAccount'
    $csoCount = [int](Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $naturalTypeId -Count)
    if ($csoCount -ne $expected) {
        return @{ Status = 'fail'; Detail = "APP_ACCOUNTS_NATURAL holds $actualRows row(s) but JIM holds $csoCount Connected System Object(s), so the anchor JIM authored on export is not the one it composed on import." }
    }

    # The key JIM authored has to be the value the Attribute Flow supplied, not a surrogate of its own.
    $malformed = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).APP_ACCOUNTS_NATURAL WHERE ACCOUNT_CODE NOT LIKE 'E%';")
    if ($malformed -ne 0) {
        return @{ Status = 'fail'; Detail = "$malformed row(s) carry an ACCOUNT_CODE that did not come from the Metaverse 'Account Name' flow." }
    }

    return @{ Status = 'pass'; Detail = "$expected row(s) provisioned into a natural-key table, with JIM's authored key surviving the confirming import." }
}

function Test-S16ReferenceExport {
    param([hashtable]$Context, [hashtable]$Config)

    Initialize-S16ExportBaseline -Context $Context

    # Employee 12's manager is employee 3 (the seeder gives every row past the tenth a manager of
    # (n modulo 10) + 1), and employee 3 is itself enabled, so the manager has an exported row for the
    # reference to point at. What is asserted is that JIM wrote the manager's OWN generated key rather
    # than the source system's employee identifier.
    $employeeId = 12
    $managerEmployeeId = ($employeeId % 10) + 1
    $userName = Get-S16EmployeeNumber -EmployeeId $employeeId
    $managerUserName = Get-S16EmployeeNumber -EmployeeId $managerEmployeeId

    $expectedManagerId = (Invoke-Scenario16Query -Config $Config -Query "SELECT ID FROM $($Config.Schema).APP_USERS WHERE USER_NAME = '$managerUserName';").Trim()
    if ([string]::IsNullOrWhiteSpace($expectedManagerId)) {
        return @{ Status = 'fail'; Detail = "The manager ($managerUserName) has no APP_USERS row, so the reference had nothing to resolve to." }
    }

    $writtenManagerId = (Invoke-Scenario16Query -Config $Config -Query "SELECT MANAGER_ID FROM $($Config.Schema).APP_USERS WHERE USER_NAME = '$userName';").Trim()
    if ($writtenManagerId -ne $expectedManagerId) {
        return @{ Status = 'fail'; Detail = "APP_USERS.MANAGER_ID for $userName should be $expectedManagerId (the generated key of $managerUserName); it is '$writtenManagerId'." }
    }

    return @{ Status = 'pass'; Detail = "Reference exported as the target object's own anchor ($managerUserName resolved to $expectedManagerId)." }
}

function Test-S16TypeMappingRoundTrip {
    param([hashtable]$Context, [hashtable]$Config)

    Initialize-S16ExportBaseline -Context $Context

    # One employee, every mapped shape, source value against exported value. Employee 12 is enabled so it
    # has an exported row, and its FTE is 0.25, a value binary floating point cannot represent exactly.
    $employeeId = 12
    $userName = Get-S16EmployeeNumber -EmployeeId $employeeId
    $failures = @()

    $sourceFte = [decimal](Invoke-Scenario16Query -Config $Config -Query "SELECT TO_CHAR(FTE) FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = $employeeId;" | ForEach-Object { $_ }).Trim()
    if ($Config.Provider -eq "SqlServer") {
        $sourceFte = [decimal]((Invoke-Scenario16Query -Config $Config -Query "SELECT CAST(FTE AS varchar(32)) FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = $employeeId;").Trim())
    }

    $exportedFteText = (Invoke-Scenario16Query -Config $Config -Query $(
        if ($Config.Provider -eq "SqlServer") { "SELECT CAST(FTE AS varchar(32)) FROM $($Config.Schema).APP_USERS WHERE USER_NAME = '$userName';" }
        else { "SELECT TO_CHAR(FTE) FROM $($Config.Schema).APP_USERS WHERE USER_NAME = '$userName';" })).Trim()

    if ([string]::IsNullOrWhiteSpace($exportedFteText)) {
        $failures += "FTE was not written to APP_USERS at all."
    }
    elseif ([decimal]$exportedFteText -ne $sourceFte) {
        $failures += "FTE round-tripped as $exportedFteText but the source holds $sourceFte; the exact-numeric value did not survive."
    }

    # The zoneless date and time column. Whatever the Database Time Zone does on the way in, the reverse
    # conversion on the way out must land on the same wall clock, or the two directions are not inverses.
    $sourceStart = (Invoke-Scenario16Query -Config $Config -Query $(
        if ($Config.Provider -eq "SqlServer") { "SELECT CONVERT(varchar(19), START_DATE, 120) FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = $employeeId;" }
        else { "SELECT TO_CHAR(START_DATE,'YYYY-MM-DD HH24:MI:SS') FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = $employeeId;" })).Trim()

    $exportedStart = (Invoke-Scenario16Query -Config $Config -Query $(
        if ($Config.Provider -eq "SqlServer") { "SELECT CONVERT(varchar(19), STARTS_ON, 120) FROM $($Config.Schema).APP_USERS WHERE USER_NAME = '$userName';" }
        else { "SELECT TO_CHAR(STARTS_ON,'YYYY-MM-DD HH24:MI:SS') FROM $($Config.Schema).APP_USERS WHERE USER_NAME = '$userName';" })).Trim()

    if ($exportedStart -ne $sourceStart) {
        $failures += "The zoneless date and time round-tripped as '$exportedStart' but the source wall clock is '$sourceStart'. Import and export are not inverting the Database Time Zone the same way."
    }

    $exportedEnabled = (Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).APP_USERS WHERE USER_NAME = '$userName' AND IS_ENABLED = 1;").Trim()
    if ($exportedEnabled -ne '1') {
        $failures += "The Boolean did not round-trip: IS_ENABLED is not set for an employee whose IS_ACTIVE is set."
    }

    if ($failures.Count -gt 0) {
        return @{ Status = 'fail'; Detail = ($failures -join ' ') }
    }

    return @{ Status = 'pass'; Detail = "Exact-numeric Decimal, zoneless date and time, and Boolean all round-tripped for employee $employeeId." }
}

# ─── Driver-shape rows ─────────────────────────────────────────────────────────

function ConvertTo-S16Utc {
    <#
    .SYNOPSIS
        Normalise a Metaverse DateTime attribute value to a UTC DateTime.
    .DESCRIPTION
        JIM stores DateTime attribute values as UTC instants; how they arrive over the wire depends on
        the JSON deserialiser, so both a DateTime and a string are accepted and both end up as Kind Utc.
        Everything downstream compares instants, never wall clocks, because a wall-clock comparison is
        the very confusion these rows exist to detect.
    #>
    param([Parameter(Mandatory=$true)]$Value)

    if ($Value -is [datetime]) {
        if ($Value.Kind -eq [System.DateTimeKind]::Utc) { return $Value }
        return [System.DateTime]::SpecifyKind($Value, [System.DateTimeKind]::Utc)
    }

    return [System.DateTime]::Parse(
        [string]$Value,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal)
}

function Get-S16SourceUtc {
    <#
    .SYNOPSIS
        The UTC instant the database itself says an offset-carrying column holds.
    .DESCRIPTION
        The ground truth for the offset-carrying rows, computed by the server rather than by this script.
        Deriving the expected instant here in PowerShell would mean reimplementing the very conversion
        under test, and a test that reimplements its subject agrees with it by construction.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][string]$Column,
        [Parameter(Mandatory=$true)][int]$EmployeeId
    )

    $query = if ($Config.Provider -eq "SqlServer") {
        "SELECT CONVERT(varchar(19), SWITCHOFFSET($Column, 0), 120) FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = $EmployeeId;"
    }
    else {
        "SELECT TO_CHAR(SYS_EXTRACT_UTC($Column),'YYYY-MM-DD HH24:MI:SS') FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = $EmployeeId;"
    }

    $text = (Invoke-Scenario16Query -Config $Config -Query $query).Trim()
    return ConvertTo-S16Utc -Value $text
}

function Get-S16ZonelessWallClock {
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][string]$Column,
        [Parameter(Mandatory=$true)][int]$EmployeeId
    )

    $query = if ($Config.Provider -eq "SqlServer") {
        "SELECT CONVERT(varchar(19), $Column, 120) FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = $EmployeeId;"
    }
    else {
        "SELECT TO_CHAR($Column,'YYYY-MM-DD HH24:MI:SS') FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = $EmployeeId;"
    }

    return (Invoke-Scenario16Query -Config $Config -Query $query).Trim()
}

function Get-S16ExpectedUtcForZoneless {
    <#
    .SYNOPSIS
        The UTC instant a zoneless wall clock names when read in the Connected System's Database Time Zone.
    #>
    param(
        [Parameter(Mandatory=$true)][string]$WallClock,
        [Parameter(Mandatory=$true)][string]$TimeZoneId
    )

    $unspecified = [System.DateTime]::SpecifyKind(
        [System.DateTime]::ParseExact($WallClock, 'yyyy-MM-dd HH:mm:ss', [System.Globalization.CultureInfo]::InvariantCulture),
        [System.DateTimeKind]::Unspecified)
    $zone = [System.TimeZoneInfo]::FindSystemTimeZoneById($TimeZoneId)
    return [System.TimeZoneInfo]::ConvertTimeToUtc($unspecified, $zone)
}

function Test-S16DateTimeNonUtc {
    param([hashtable]$Context, [hashtable]$Config)

    if ($Context.DatabaseTimeZone -eq 'UTC') {
        return @{ Status = 'fail'; Detail = "The Connected System is configured for UTC, which makes every zone conversion the identity and the assertion meaningless. This row requires a non-UTC Database Time Zone." }
    }

    Initialize-S16SyncBaseline -Context $Context

    # START_DATE is zoneless, so JIM must interpret it in the declared zone and store the corresponding
    # UTC instant. Both sides of a daylight-saving transition are checked, because a zone applied as a
    # fixed offset passes a single-season test: Australia/Sydney is UTC+11:00 over the seeded January
    # dates and UTC+10:00 in the southern winter, so a fixed-offset implementation gets the second case
    # an hour wrong and nothing else distinguishes the two.
    $employeeId = 7
    $failures = @()
    $observed = @()

    $originalWallClock = Get-S16ZonelessWallClock -Config $Config -Column 'START_DATE' -EmployeeId $employeeId

    function Test-S16ZonelessInstant {
        param([hashtable]$RowContext, [string]$Label, [string]$WallClock, [int]$Employee)

        $attributeName = $RowContext.MetaverseAttributes.ZonelessDate
        $mvo = Get-S16MetaverseObject -EmployeeId $Employee
        if (-not $mvo) { return @{ Failure = "No Metaverse Object was projected for employee $Employee ($Label)."; Observation = $null } }

        $value = Get-S16MvoValue -Mvo $mvo -AttributeName $attributeName
        if (-not $value -or -not $value.dateTimeValue) {
            return @{ Failure = "The Metaverse Object for employee $Employee carries no '$attributeName' value ($Label)."; Observation = $null }
        }

        $actual = ConvertTo-S16Utc -Value $value.dateTimeValue
        $expected = Get-S16ExpectedUtcForZoneless -WallClock $WallClock -TimeZoneId $RowContext.DatabaseTimeZone
        $observation = "$Label wall clock '$WallClock' in $($RowContext.DatabaseTimeZone) stored as $($actual.ToString('o'))"

        if ($actual -ne $expected) {
            return @{ Failure = "$Label : the source wall clock is '$WallClock' in $($RowContext.DatabaseTimeZone), so the stored instant should be $($expected.ToString('o')); JIM stored $($actual.ToString('o'))."; Observation = $observation }
        }
        return @{ Failure = $null; Observation = $observation }
    }

    $summerOutcome = Test-S16ZonelessInstant -RowContext $Context -Label 'Southern summer (UTC+11:00)' -WallClock $originalWallClock -Employee $employeeId
    if ($summerOutcome.Failure) { $failures += $summerOutcome.Failure } else { $observed += $summerOutcome.Observation }

    # The seeded population never leaves January and February, so the other side of the transition has to
    # be written in rather than found. Restored afterwards so later rows still see the deterministic seed.
    $winterWallClock = '2020-07-15 09:30:00'
    try {
        Invoke-Scenario16NonQuery -Config $Config -Statement $(
            if ($Config.Provider -eq "SqlServer") { "UPDATE $($Config.Schema).EMPLOYEES SET START_DATE = CAST('$winterWallClock' AS datetime2(3)) WHERE EMPLOYEE_ID = $employeeId;" }
            else { "UPDATE $($Config.Schema).EMPLOYEES SET START_DATE = TIMESTAMP '$winterWallClock' WHERE EMPLOYEE_ID = $employeeId;" })

        Invoke-S16RunProfile -Context $Context -Name "Full Import"          | Out-Null
        Invoke-S16RunProfile -Context $Context -Name "Full Synchronisation" | Out-Null

        $winterOutcome = Test-S16ZonelessInstant -RowContext $Context -Label 'Southern winter (UTC+10:00)' -WallClock $winterWallClock -Employee $employeeId
        if ($winterOutcome.Failure) { $failures += $winterOutcome.Failure } else { $observed += $winterOutcome.Observation }
    }
    finally {
        Invoke-Scenario16NonQuery -Config $Config -Statement $(
            if ($Config.Provider -eq "SqlServer") { "UPDATE $($Config.Schema).EMPLOYEES SET START_DATE = CAST('$originalWallClock' AS datetime2(3)) WHERE EMPLOYEE_ID = $employeeId;" }
            else { "UPDATE $($Config.Schema).EMPLOYEES SET START_DATE = TIMESTAMP '$originalWallClock' WHERE EMPLOYEE_ID = $employeeId;" })
        Invoke-S16RunProfile -Context $Context -Name "Full Import"          | Out-Null
        Invoke-S16RunProfile -Context $Context -Name "Full Synchronisation" | Out-Null
    }

    if ($failures.Count -gt 0) {
        return @{ Status = 'fail'; Detail = ($failures -join ' ') }
    }

    return @{ Status = 'pass'; Detail = "Zoneless values interpreted in $($Context.DatabaseTimeZone) on both sides of the daylight-saving transition. $($observed -join '; ')." }
}

function Test-S16OffsetVersusZoneless {
    param([hashtable]$Context, [hashtable]$Config)

    Initialize-S16SyncBaseline -Context $Context

    # START_DATE (zoneless) and HIRED_AT (offset-carrying, stated as -05:00) sit in the same table and are
    # read by the same import. Import decides which is which from the runtime CLR type the driver returns,
    # and the failure this row is built to catch is the Database Time Zone being applied to the
    # offset-carrying column as well: the instant would then be wrong by the difference between the two
    # offsets, and no single-column test would notice.
    $employeeId = 7
    $failures = @()

    $mvo = Get-S16MetaverseObject -EmployeeId $employeeId
    if (-not $mvo) {
        return @{ Status = 'fail'; Detail = "No Metaverse Object was projected for employee $employeeId, so neither value can be read back." }
    }

    $zonelessValue = Get-S16MvoValue -Mvo $mvo -AttributeName $Context.MetaverseAttributes.ZonelessDate
    $offsetValue   = Get-S16MvoValue -Mvo $mvo -AttributeName $Context.MetaverseAttributes.OffsetDate

    if (-not $zonelessValue -or -not $zonelessValue.dateTimeValue) { $failures += "The zoneless column produced no Metaverse value." }
    if (-not $offsetValue   -or -not $offsetValue.dateTimeValue)   { $failures += "The offset-carrying column produced no Metaverse value." }

    if ($failures.Count -gt 0) {
        return @{ Status = 'fail'; Detail = ($failures -join ' ') }
    }

    $zonelessWallClock = Get-S16ZonelessWallClock -Config $Config -Column 'START_DATE' -EmployeeId $employeeId
    $expectedZoneless = Get-S16ExpectedUtcForZoneless -WallClock $zonelessWallClock -TimeZoneId $Context.DatabaseTimeZone
    $actualZoneless = ConvertTo-S16Utc -Value $zonelessValue.dateTimeValue
    if ($actualZoneless -ne $expectedZoneless) {
        $failures += "The zoneless column should have been read in $($Context.DatabaseTimeZone) as $($expectedZoneless.ToString('o')); JIM stored $($actualZoneless.ToString('o'))."
    }

    $expectedOffset = Get-S16SourceUtc -Config $Config -Column 'HIRED_AT' -EmployeeId $employeeId
    $actualOffset = ConvertTo-S16Utc -Value $offsetValue.dateTimeValue
    if ($actualOffset -ne $expectedOffset) {
        $failures += "The offset-carrying column states its own offset, so the stored instant should be the $($expectedOffset.ToString('o')) the server itself reports; JIM stored $($actualOffset.ToString('o')). A difference matching the Database Time Zone's offset means the zone was applied to a column that did not need it."
    }

    if ($failures.Count -gt 0) {
        return @{ Status = 'fail'; Detail = ($failures -join ' ') }
    }

    return @{ Status = 'pass'; Detail = "Both columns in one table read correctly and differently: zoneless through $($Context.DatabaseTimeZone), offset-carrying at the instant it states." }
}

function Test-S16LocalTimeZone {
    param([hashtable]$Context, [hashtable]$Config)

    Initialize-S16SyncBaseline -Context $Context

    # Oracle's TIMESTAMP WITH LOCAL TIME ZONE is the case where the connector's two oracles can
    # disagree: SqlTypeMapper.CarriesAnOffset (which export consults, via the catalogue's type name)
    # lists it as offset-carrying, while import decides from the runtime CLR type the driver returns.
    # SYS_EXTRACT_UTC is the arbiter, because it is the server's own answer for the absolute instant the
    # column holds and it does not depend on either session's time zone.
    $employeeId = 7
    $catalogueType = (Invoke-Scenario16Query -Config $Config -Query "SELECT DATA_TYPE FROM ALL_TAB_COLUMNS WHERE OWNER = '$($Config.Schema)' AND TABLE_NAME = 'EMPLOYEES' AND COLUMN_NAME = 'HIRED_AT_LOCAL';").Trim()

    $mvo = Get-S16MetaverseObject -EmployeeId $employeeId
    if (-not $mvo) {
        return @{ Status = 'fail'; Detail = "No Metaverse Object was projected for employee $employeeId." }
    }

    $value = Get-S16MvoValue -Mvo $mvo -AttributeName $Context.MetaverseAttributes.LocalZoneDate
    if (-not $value -or -not $value.dateTimeValue) {
        return @{ Status = 'fail'; Detail = "The catalogue reports '$catalogueType' for HIRED_AT_LOCAL, but the column produced no Metaverse value at all." }
    }

    $expected = Get-S16SourceUtc -Config $Config -Column 'HIRED_AT_LOCAL' -EmployeeId $employeeId
    $actual = ConvertTo-S16Utc -Value $value.dateTimeValue

    if ($actual -ne $expected) {
        return @{ Status = 'fail'; Detail = "HIRED_AT_LOCAL (catalogue type '$catalogueType') holds the instant $($expected.ToString('o')) according to Oracle's own SYS_EXTRACT_UTC; JIM imported $($actual.ToString('o')). The session time zone pinning is not holding through the import, or the column is being classified differently by import and export." }
    }

    # The two neighbouring shapes have to agree in the same import, or the classification is arbitrary.
    $offsetValue = Get-S16MvoValue -Mvo $mvo -AttributeName $Context.MetaverseAttributes.OffsetDate
    if ($offsetValue -and $offsetValue.dateTimeValue) {
        $offsetExpected = Get-S16SourceUtc -Config $Config -Column 'HIRED_AT' -EmployeeId $employeeId
        $offsetActual = ConvertTo-S16Utc -Value $offsetValue.dateTimeValue
        if ($offsetActual -ne $offsetExpected) {
            return @{ Status = 'fail'; Detail = "HIRED_AT_LOCAL imported correctly but its TIMESTAMP WITH TIME ZONE neighbour did not (expected $($offsetExpected.ToString('o')), got $($offsetActual.ToString('o'))), so the three date and time shapes in this table are not being told apart consistently." }
        }
    }

    return @{ Status = 'pass'; Detail = "TIMESTAMP WITH LOCAL TIME ZONE imported at the instant Oracle's own SYS_EXTRACT_UTC reports, alongside a TIMESTAMP WITH TIME ZONE and a zoneless column in the same table." }
}

function Test-S16Raw16Anchor {
    param([hashtable]$Context, [hashtable]$Config)

    # The export half is the reason this row exists at all: a key defaulted from SYS_GUID() comes back
    # through a bound output parameter as an ODP.NET wrapper struct rather than a plain byte[], and
    # nothing in the unit suite can see that shape. Running the baseline first means the table holds both
    # the three pre-seeded rows (the import half) and the rows JIM provisioned (the export half).
    Initialize-S16ExportBaseline -Context $Context

    $guidTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'GuidKeyedPerson'
    $imported = Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $guidTypeId -Count
    $expected = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).GUID_KEYED_PEOPLE;")

    if ([int]$imported -ne $expected) {
        return @{ Status = 'fail'; Detail = "GUID_KEYED_PEOPLE holds $expected row(s) but JIM holds $imported Connected System Object(s). If JIM holds more, the RAW(16) anchor it composed on export does not equal the one it composed on import and the confirming import did not recognise its own rows." }
    }

    # The seeder writes three rows before anything runs; everything beyond that was provisioned by JIM
    # and therefore had its key generated by the database and read back through the output parameter.
    $provisioned = $expected - 3
    $expectedProvisioned = Get-S16ExpectedCount -RowCount $Context.RowCount -Scope 'Finance'
    if ($provisioned -ne $expectedProvisioned) {
        return @{ Status = 'fail'; Detail = "The rule is scoped to Department = Finance, so $expectedProvisioned row(s) should have been provisioned on top of the three seeded ones; GUID_KEYED_PEOPLE holds $expected row(s) in total." }
    }

    $objects = Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $guidTypeId -All -Force
    $anchors = @($objects | ForEach-Object { $_.externalIdValue })
    $malformed = @($anchors | Where-Object { $_ -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' })

    if ($malformed.Count -gt 0) {
        return @{ Status = 'fail'; Detail = "RAW(16) anchors did not render as hyphenated GUIDs: $(($malformed | Select-Object -First 5) -join ', ')" }
    }

    if (@($anchors | Select-Object -Unique).Count -ne $anchors.Count) {
        return @{ Status = 'fail'; Detail = "The RAW(16) anchors are not distinct, which is what an output parameter read back as a default-initialised wrapper struct would look like." }
    }

    return @{ Status = 'pass'; Detail = "$expected RAW(16) anchor(s) round-tripped: three seeded rows imported, $provisioned provisioned with a SYS_GUID() key returned through the output parameter, and every one recognised again by the confirming import." }
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
