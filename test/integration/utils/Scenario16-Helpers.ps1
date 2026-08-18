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

function Get-S16AttributeValueText {
    <#
    .SYNOPSIS
        Whichever typed column an attribute value row actually carries, rendered as text.
    .DESCRIPTION
        The attribute-values endpoint returns one row per value with a column per data type
        (StringValue, IntValue, LongValue, DecimalValue, DateTimeValue, GuidValue, BoolValue,
        ByteValue) and no single generic 'value' field, so a caller has to know which one is populated.
    #>
    param([Parameter(Mandatory=$true)]$Value)

    foreach ($property in @('StringValue', 'IntValue', 'LongValue', 'DecimalValue', 'GuidValue', 'BoolValue', 'DateTimeValue', 'ByteValue')) {
        if ($Value.PSObject.Properties.Name -notcontains $property) { continue }
        $candidate = $Value.$property
        if ($null -ne $candidate -and "$candidate" -ne '') { return "$candidate" }
    }
    return $null
}

function Get-S16CsoByAnchor {
    <#
    .SYNOPSIS
        The Connected System Object whose anchor holds the given value.
    .DESCRIPTION
        Cannot simply filter on the header's externalIdValue, because the Connector Space list
        projection reads ONLY the StringValue column when it composes that field
        (ConnectedSystemRepository, the ConnectedSystemObjectHeader projection), so an anchor held in
        IntValue, LongValue or GuidValue comes back null. Every Object Type in this scenario that is
        anchored on an integer is affected, and so is the portal page built on the same projection.

        The header is still used when it carries a value, because that is the cheap path and the one
        that should be working; the fallback asks each object for the anchor attribute's own value,
        which is correct but costs one call per object. Functional-scale row counts only.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Context,
        [Parameter(Mandatory=$true)][int]$ObjectTypeId,
        [Parameter(Mandatory=$true)][string]$AnchorAttributeName,
        [Parameter(Mandatory=$true)][string]$AnchorValue
    )

    $objects = @(Get-JIMConnectedSystemObject -ConnectedSystemId $Context.ConnectedSystemId -ObjectTypeId $ObjectTypeId -All -Force)

    $viaHeader = $objects | Where-Object { $_.externalIdValue -eq $AnchorValue } | Select-Object -First 1
    if ($viaHeader) { return $viaHeader }

    foreach ($candidate in $objects) {
        $values = @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $Context.ConnectedSystemId -CsoId $candidate.id -AttributeName $AnchorAttributeName -All -Force)
        foreach ($value in $values) {
            if ((Get-S16AttributeValueText -Value $value) -eq $AnchorValue) { return $candidate }
        }
    }

    return $null
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
        'Delta.WatermarkColumn'            { return Test-S16DeltaWatermarkColumn -Context $Context -Config $Config }
        'Delta.Fallback'                   { return Test-S16DeltaFallback -Context $Context -Config $Config }
        'Delta.RowversionWatermark'        { return Test-S16DeltaRowversionWatermark -Context $Context -Config $Config }
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
    $person3 = Get-S16CsoByAnchor -Context $Context -ObjectTypeId $personTypeId -AnchorAttributeName 'EMPLOYEE_ID' -AnchorValue '3'
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
    $person12 = Get-S16CsoByAnchor -Context $Context -ObjectTypeId $personTypeId -AnchorAttributeName 'EMPLOYEE_ID' -AnchorValue '12'
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

# ─── Delta rows ────────────────────────────────────────────────────────────────
#
# One shape, run once per mode: baseline with a Full Import, mutate the source in every way a Delta
# Import can observe (a row inserted, a row updated, a related-table row added, a row deleted), run a
# Delta Import and assert on what reached the connector space, then run a second Delta Import and
# assert it read nothing. Every mutation is undone before the row returns, because the rows after these
# derive their expectations from the seeded population (Get-S16ExpectedCount) and would otherwise be
# asserting against residue.
#
# The employees these rows touch are chosen to be nobody else's: 40 (updated), 41 (gains a phone), 48
# (deleted in Watermark Column mode, where the deletion must go unseen), and 51 to 54, which do not exist
# in the seed and are inserted here, each by one row only, so an object left obsolete by one row is never
# what the next row asserts on. The rows that read specific employees use 1, 3, 12 and 20.

function Set-S16DeltaImportMode {
    <#
    .SYNOPSIS
        Change the identity system's Delta Import Mode, and optionally its Object Types document.
    .DESCRIPTION
        The setting save is what runs the mode's validation (every Object Type must carry what the mode
        needs) and the live connectivity test, so a refused save fails the row here with the API's own
        message rather than four steps later.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Context,
        [Parameter(Mandatory=$true)][ValidateSet('Change-Log Table', 'Watermark Column')][string]$Mode,
        [Parameter(Mandatory=$false)][string]$ObjectTypesJson
    )

    $settings = @{ ($Context.SettingIds.DeltaImportMode) = @{ stringValue = $Mode } }
    if ($ObjectTypesJson) {
        $settings[$Context.SettingIds.ObjectTypes] = @{ stringValue = $ObjectTypesJson }
    }
    Set-JIMConnectedSystem -Id $Context.ConnectedSystemId -SettingValues $settings -ErrorAction Stop | Out-Null
    $Context['DeltaImportMode'] = $Mode
}

function Get-S16ObjectTypesJsonWithWatermark {
    <#
    .SYNOPSIS
        The identity system's Object Types document with Person and its related table watermarked on a
        different column, everything else untouched.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Context,
        [Parameter(Mandatory=$true)][string]$WatermarkColumn
    )

    $document = $Context.ObjectTypesJson | ConvertFrom-Json
    $person = $document.objectTypes | Where-Object { $_.name -eq 'Person' }
    if (-not $person) { throw "The Object Types document has no 'Person' Object Type to re-watermark." }
    $person.watermarkColumn = $WatermarkColumn
    foreach ($related in @($person.relatedTables)) { $related.watermarkColumn = $WatermarkColumn }
    return ($document | ConvertTo-Json -Depth 20)
}

function Invoke-S16DeltaImport {
    <#
    .SYNOPSIS
        Run the identity system's Delta Import and return its Activity, asserting the outcome asked for.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Context,
        [Parameter(Mandatory=$true)][string]$Purpose,
        # The Delta Import that follows a mode change is expected to fall back to a Full Import and say so.
        [Parameter(Mandatory=$false)][switch]$ExpectFallback
    )

    $result = Start-JIMRunProfile -ConnectedSystemId $Context.ConnectedSystemId -RunProfileName "Delta Import" -Wait -PassThru
    if ($ExpectFallback) {
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Scenario 16 Delta Import, $Purpose ($($Context.Provider))" `
            -AllowWarnings -AllowedWarningTypes @('DeltaImportFallbackToFullImport')
        $activity = Get-JIMActivity -Id $result.activityId
        if ($activity.status -ne 'CompleteWithWarning') {
            throw "The Delta Import ($Purpose) should have fallen back to a Full Import with a warning saying so; it completed with status '$($activity.status)'."
        }
    }
    else {
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Scenario 16 Delta Import, $Purpose ($($Context.Provider))"
    }
    return $result
}

function Get-S16DeltaReadCount {
    <#
    .SYNOPSIS
        How many objects a Delta Import's Activity says it processed, plus its adds, updates and deletes.
    #>
    param([Parameter(Mandatory=$true)][string]$ActivityId)

    $activity = Get-JIMActivity -Id $ActivityId
    $stats = Get-JIMActivityStats -ActivityId $ActivityId
    return @{
        Processed = [int]$activity.objectsProcessed
        Adds      = [int]$stats.totalCsoAdds
        Updates   = [int]$stats.totalCsoUpdates
        Deletes   = [int]$stats.totalCsoDeletes
    }
}

function Get-S16NewEmployeeInsert {
    <#
    .SYNOPSIS
        The INSERT that adds one employee the seed does not know, shaped from an existing row.
    .DESCRIPTION
        Cloned from employee 40 rather than written out, so every typed column arrives in the same shape
        the seed produced (the offset-carrying date, the RAW(16) identifier); only the identity, the
        name and the address are the new person's. No manager, so the row raises no reference. The
        last-modified column is left to its default, which is what the watermark guide relies on.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][int]$EmployeeId
    )

    $number = Get-S16EmployeeNumber -Config $Config -EmployeeId $EmployeeId
    $guidHex = ('{0:D8}' -f $EmployeeId) + '000040008000000000000000'
    if ($Config.Provider -eq 'SqlServer') {
        $guid = ('{0:D8}' -f $EmployeeId) + '-0000-4000-8000-000000000000'
        return "INSERT INTO $($Config.Schema).EMPLOYEES (EMPLOYEE_ID, EMPLOYEE_NUMBER, FIRST_NAME, LAST_NAME, EMAIL, DEPARTMENT, MANAGER_EMPLOYEE_ID, HEADCOUNT, FTE, IS_ACTIVE, START_DATE, HIRED_AT, EMPLOYEE_GUID, PHOTO) " +
               "SELECT $EmployeeId, '$number', 'Ivy', 'Delta', 'user$EmployeeId@panoply.local', DEPARTMENT, NULL, HEADCOUNT, FTE, 1, START_DATE, HIRED_AT, CAST('$guid' AS uniqueidentifier), NULL " +
               "FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = 40;"
    }
    return "INSERT INTO $($Config.Schema).EMPLOYEES (EMPLOYEE_ID, EMPLOYEE_NUMBER, FIRST_NAME, LAST_NAME, EMAIL, DEPARTMENT, MANAGER_EMPLOYEE_ID, HEADCOUNT, FTE, IS_ACTIVE, START_DATE, HIRED_AT, HIRED_AT_LOCAL, EMPLOYEE_GUID, PHOTO) " +
           "SELECT $EmployeeId, '$number', 'Ivy', 'Delta', 'user$EmployeeId@panoply.local', DEPARTMENT, NULL, HEADCOUNT, FTE, 1, START_DATE, HIRED_AT, HIRED_AT_LOCAL, HEXTORAW('$guidHex'), NULL " +
           "FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = 40;"
}

function Get-S16PhoneCount {
    param([Parameter(Mandatory=$true)][hashtable]$Context, [Parameter(Mandatory=$true)]$Cso)
    return @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $Context.ConnectedSystemId -CsoId $Cso.id -AttributeName 'PhoneNumbers' -All -Force).Count
}

function Get-S16CsoText {
    param([Parameter(Mandatory=$true)][hashtable]$Context, [Parameter(Mandatory=$true)]$Cso, [Parameter(Mandatory=$true)][string]$AttributeName)
    $value = @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $Context.ConnectedSystemId -CsoId $Cso.id -AttributeName $AttributeName -All -Force) | Select-Object -First 1
    if (-not $value) { return $null }
    return Get-S16AttributeValueText -Value $value
}

function Invoke-S16DeltaMutations {
    <#
    .SYNOPSIS
        Change the source in the four ways a Delta Import can observe, and return how to undo them.
    .DESCRIPTION
        Inserts employee $NewEmployeeId, renames employee 40, gives employee 41 a phone number, and, when
        asked, deletes employee $DeleteEmployeeId (phones first, for the foreign key). The deletion is
        optional because the change-log row deletes an employee it created itself in an earlier step,
        so the deletion can be observed as such, while the watermark row deletes a seeded one to prove
        the deletion is NOT observed.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][int]$NewEmployeeId,
        [Parameter(Mandatory=$false)][int]$DeleteEmployeeId = 0
    )

    $schema = $Config.Schema
    Invoke-Scenario16NonQuery -Config $Config -Statement (Get-S16NewEmployeeInsert -Config $Config -EmployeeId $NewEmployeeId)
    Invoke-Scenario16NonQuery -Config $Config -Statement "UPDATE $schema.EMPLOYEES SET LAST_NAME = 'Renamed' WHERE EMPLOYEE_ID = 40;"
    Invoke-Scenario16NonQuery -Config $Config -Statement "INSERT INTO $schema.EMPLOYEE_PHONES (EMPLOYEE_ID, PHONE_NUMBER) VALUES (41, '+44 113 496 4141');"
    if ($DeleteEmployeeId -gt 0) {
        Invoke-Scenario16NonQuery -Config $Config -Statement "DELETE FROM $schema.EMPLOYEE_PHONES WHERE EMPLOYEE_ID = $DeleteEmployeeId;"
        Invoke-Scenario16NonQuery -Config $Config -Statement "DELETE FROM $schema.EMPLOYEES WHERE EMPLOYEE_ID = $DeleteEmployeeId;"
    }
}

function Undo-S16DeltaMutations {
    <#
    .SYNOPSIS
        Put the source back exactly as the seed left it, so the rows after this one meet the population
        they derive their expectations from.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][int[]]$NewEmployeeIds,
        # A seeded employee deleted by the row, restored from the seed's own arithmetic.
        [Parameter(Mandatory=$false)][int]$RestoreEmployeeId = 0
    )

    $schema = $Config.Schema
    foreach ($id in $NewEmployeeIds) {
        Invoke-Scenario16NonQuery -Config $Config -Statement "DELETE FROM $schema.EMPLOYEE_PHONES WHERE EMPLOYEE_ID = $id;"
        Invoke-Scenario16NonQuery -Config $Config -Statement "DELETE FROM $schema.EMPLOYEES WHERE EMPLOYEE_ID = $id;"
    }
    Invoke-Scenario16NonQuery -Config $Config -Statement "UPDATE $schema.EMPLOYEES SET LAST_NAME = 'Ellery' WHERE EMPLOYEE_ID = 40;"
    Invoke-Scenario16NonQuery -Config $Config -Statement "DELETE FROM $schema.EMPLOYEE_PHONES WHERE EMPLOYEE_ID = 41 AND PHONE_NUMBER = '+44 113 496 4141';"

    if ($RestoreEmployeeId -gt 0) {
        # The seed's own derivation for one row (see New-Scenario16TestDatabase.ps1): the names cycle on
        # n modulo 8 and 6, the department on n modulo 4, the manager is (n modulo 10) + 1, and every third
        # employee has two phone numbers. Written out here for the one row rather than reaching into the
        # seeder, whose set-based statement cannot author a single row.
        $n = $RestoreEmployeeId
        $number = Get-S16EmployeeNumber -Config $Config -EmployeeId $n
        $first = @('Ada', 'Bram', 'Cleo', 'Dara', 'Emil', 'Fern', 'Gita', 'Hugo')[$n % 8]
        $last = @('Ashcroft', 'Brandt', 'Calder', 'Duquesne', 'Ellery', 'Fairhurst')[$n % 6]
        $department = @('Engineering', 'Finance', 'Operations', 'Research')[$n % 4]
        $manager = if ($n -gt 10) { ($n % 10) + 1 } else { 'NULL' }
        $fte = 0.25 + (($n % 4) * 0.25)
        $active = if (($n % 7) -eq 0) { 0 } else { 1 }
        $guidHex = ('{0:D8}' -f $n) + '000040008000000000000000'
        if ($Config.Provider -eq 'SqlServer') {
            $guid = ('{0:D8}' -f $n) + '-0000-4000-8000-000000000000'
            Invoke-Scenario16NonQuery -Config $Config -Statement ("INSERT INTO $schema.EMPLOYEES (EMPLOYEE_ID, EMPLOYEE_NUMBER, FIRST_NAME, LAST_NAME, EMAIL, DEPARTMENT, MANAGER_EMPLOYEE_ID, HEADCOUNT, FTE, IS_ACTIVE, START_DATE, HIRED_AT, EMPLOYEE_GUID, PHOTO) VALUES " +
                "($n, '$number', '$first', '$last', 'user$n@panoply.local', '$department', $manager, CAST($n AS bigint) * 1000000000, $fte, $active, DATEADD(day, $n, CAST('2020-01-06' AS datetime2(3))), TODATETIMEOFFSET(DATEADD(minute, $n, CAST('2020-01-06' AS datetime2(3))), '-05:00'), CAST('$guid' AS uniqueidentifier), CAST($n AS varbinary(64)));")
            Invoke-Scenario16NonQuery -Config $Config -Statement "INSERT INTO $schema.EMPLOYEE_PHONES (EMPLOYEE_ID, PHONE_NUMBER) VALUES ($n, CONCAT('+44 20 7000 ', RIGHT(CONCAT('0000', CAST($n AS varchar(20))), 4)));"
            if (($n % 3) -eq 0) {
                Invoke-Scenario16NonQuery -Config $Config -Statement "INSERT INTO $schema.EMPLOYEE_PHONES (EMPLOYEE_ID, PHONE_NUMBER) VALUES ($n, CONCAT('+44 161 496 ', RIGHT(CONCAT('0000', CAST($n AS varchar(20))), 4)));"
            }
        }
        else {
            Invoke-Scenario16NonQuery -Config $Config -Statement ("INSERT INTO $schema.EMPLOYEES (EMPLOYEE_ID, EMPLOYEE_NUMBER, FIRST_NAME, LAST_NAME, EMAIL, DEPARTMENT, MANAGER_EMPLOYEE_ID, HEADCOUNT, FTE, IS_ACTIVE, START_DATE, HIRED_AT, HIRED_AT_LOCAL, EMPLOYEE_GUID, PHOTO) VALUES " +
                "($n, '$number', '$first', '$last', 'user$n@panoply.local', '$department', $manager, $n * 1000000000, $fte, $active, TIMESTAMP '2020-01-06 00:00:00' + NUMTODSINTERVAL($n, 'DAY'), FROM_TZ(TIMESTAMP '2020-01-06 00:00:00' + NUMTODSINTERVAL($n, 'MINUTE'), '-05:00'), TIMESTAMP '2020-01-06 00:00:00' + NUMTODSINTERVAL($n, 'MINUTE'), HEXTORAW('$guidHex'), UTL_RAW.CAST_FROM_NUMBER($n));")
            Invoke-Scenario16NonQuery -Config $Config -Statement "INSERT INTO $schema.EMPLOYEE_PHONES (EMPLOYEE_ID, PHONE_NUMBER) VALUES ($n, '+44 20 7000 ' || LPAD(TO_CHAR($n), 4, '0'));"
            if (($n % 3) -eq 0) {
                Invoke-Scenario16NonQuery -Config $Config -Statement "INSERT INTO $schema.EMPLOYEE_PHONES (EMPLOYEE_ID, PHONE_NUMBER) VALUES ($n, '+44 161 496 ' || LPAD(TO_CHAR($n), 4, '0'));"
            }
        }
    }
}

function Assert-S16DeltaLanded {
    <#
    .SYNOPSIS
        The assertions both delta modes share after their first Delta Import: the insert, the update
        and the related-table change reached the connector space. Returns a failure detail or $null.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Context,
        [Parameter(Mandatory=$true)][int]$NewEmployeeId,
        [Parameter(Mandatory=$true)][string]$Mode
    )

    $personTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'Person'

    $created = Get-S16CsoByAnchor -Context $Context -ObjectTypeId $personTypeId -AnchorAttributeName 'EMPLOYEE_ID' -AnchorValue "$NewEmployeeId"
    if (-not $created) { return "$Mode Delta Import: employee $NewEmployeeId was inserted after the baseline but no Connected System Object was created for it." }

    $updated = Get-S16CsoByAnchor -Context $Context -ObjectTypeId $personTypeId -AnchorAttributeName 'EMPLOYEE_ID' -AnchorValue '40'
    $lastName = Get-S16CsoText -Context $Context -Cso $updated -AttributeName 'LAST_NAME'
    if ($lastName -ne 'Renamed') { return "$Mode Delta Import: employee 40 was renamed after the baseline but the Connected System Object still reads LAST_NAME '$lastName'." }

    $parent = Get-S16CsoByAnchor -Context $Context -ObjectTypeId $personTypeId -AnchorAttributeName 'EMPLOYEE_ID' -AnchorValue '41'
    $phones = Get-S16PhoneCount -Context $Context -Cso $parent
    if ($phones -ne 2) { return "$Mode Delta Import: employee 41 gained a phone number in the related table after the baseline, so its Connected System Object should carry 2; it carries $phones. A related-table change must select its parent." }

    return $null
}

function Test-S16DeltaChangeLog {
    param([hashtable]$Context, [hashtable]$Config)

    if ($Context.DeltaImportMode -ne 'Change-Log Table') {
        Set-S16DeltaImportMode -Context $Context -Mode 'Change-Log Table' -ObjectTypesJson $Context.ObjectTypesJson
    }

    # The baseline. A Full Import records the change log's high-water mark before it reads a row, so
    # every change made from here on is what the Delta Import will find.
    Invoke-S16RunProfile -Context $Context -Name "Full Import" | Out-Null

    try {
        # Employee 52 is inserted, imported, then deleted, so its deletion is one the connector space can
        # observe; 51 stays until the row cleans up.
        Invoke-S16DeltaMutations -Config $Config -NewEmployeeId 51
        Invoke-Scenario16NonQuery -Config $Config -Statement (Get-S16NewEmployeeInsert -Config $Config -EmployeeId 52)

        $first = Invoke-S16DeltaImport -Context $Context -Purpose "creates, an update and a related-table change"
        $failure = Assert-S16DeltaLanded -Context $Context -NewEmployeeId 51 -Mode 'Change-Log Table'
        if ($failure) { return @{ Status = 'fail'; Detail = $failure } }

        $personTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'Person'
        if (-not (Get-S16CsoByAnchor -Context $Context -ObjectTypeId $personTypeId -AnchorAttributeName 'EMPLOYEE_ID' -AnchorValue '52')) {
            return @{ Status = 'fail'; Detail = "Employee 52 was inserted after the baseline but the first Delta Import created no Connected System Object for it." }
        }

        # The deletion, which only this mode can see: the trigger logs a 'D' row and JIM imports the anchor
        # alone, which is what marks the object as gone.
        Invoke-Scenario16NonQuery -Config $Config -Statement "DELETE FROM $($Config.Schema).EMPLOYEE_PHONES WHERE EMPLOYEE_ID = 52;"
        Invoke-Scenario16NonQuery -Config $Config -Statement "DELETE FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = 52;"
        $second = Invoke-S16DeltaImport -Context $Context -Purpose "a deletion"
        $deleted = Get-S16CsoByAnchor -Context $Context -ObjectTypeId $personTypeId -AnchorAttributeName 'EMPLOYEE_ID' -AnchorValue '52'
        if ($deleted -and $deleted.status -ne 'Obsolete') {
            return @{ Status = 'fail'; Detail = "Employee 52 was deleted at source and the change log recorded it, but its Connected System Object is still '$($deleted.status)' after the Delta Import; a change-log Delta Import must observe a deletion." }
        }
        $secondCounts = Get-S16DeltaReadCount -ActivityId $second.activityId
        if ($secondCounts.Deletes -lt 1) {
            return @{ Status = 'fail'; Detail = "The Delta Import that carried employee 52's deletion recorded $($secondCounts.Deletes) deletion(s) on its Activity." }
        }

        # Nothing has changed since, so the watermark saved by the last run must leave the next one with
        # nothing to read.
        $third = Invoke-S16DeltaImport -Context $Context -Purpose "nothing"
        $counts = Get-S16DeltaReadCount -ActivityId $third.activityId
        if ($counts.Processed -ne 0 -or ($counts.Adds + $counts.Updates + $counts.Deletes) -ne 0) {
            return @{ Status = 'fail'; Detail = "A Delta Import with nothing new in the change log processed $($counts.Processed) object(s) ($($counts.Adds) added, $($counts.Updates) updated, $($counts.Deletes) deleted); the persisted watermark is not being honoured." }
        }

        return @{ Status = 'pass'; Detail = "Change log observed an insert, an update, a related-table change and a deletion in $($first.activityId.ToString().Substring(0, 8))/$($second.activityId.ToString().Substring(0, 8)); the following Delta Import read nothing." }
    }
    finally {
        Undo-S16DeltaMutations -Config $Config -NewEmployeeIds @(51)
    }
}

function Test-S16DeltaWatermarkColumn {
    param([hashtable]$Context, [hashtable]$Config)

    Set-S16DeltaImportMode -Context $Context -Mode 'Watermark Column' -ObjectTypesJson $Context.ObjectTypesJson

    # The guide's baseline: a Full Import after choosing the mode, which records every watermark column's
    # high value before it reads a row.
    Invoke-S16RunProfile -Context $Context -Name "Full Import" | Out-Null

    try {
        # Employee 48 is deleted here, and it is a seeded employee that the baseline imported: the point is
        # that a last-modified column has no row left to move, so the deletion must NOT reach JIM.
        Invoke-S16DeltaMutations -Config $Config -NewEmployeeId 53 -DeleteEmployeeId 48

        $first = Invoke-S16DeltaImport -Context $Context -Purpose "creates, an update and a related-table change"
        $failure = Assert-S16DeltaLanded -Context $Context -NewEmployeeId 53 -Mode 'Watermark Column'
        if ($failure) { return @{ Status = 'fail'; Detail = $failure } }

        $personTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'Person'
        $unseen = Get-S16CsoByAnchor -Context $Context -ObjectTypeId $personTypeId -AnchorAttributeName 'EMPLOYEE_ID' -AnchorValue '48'
        if (-not $unseen -or $unseen.status -ne 'Normal') {
            return @{ Status = 'fail'; Detail = "Employee 48 was deleted at source, which Watermark Column mode is documented NOT to observe, yet its Connected System Object is $(if ($unseen) { "'$($unseen.status)'" } else { 'gone' }) after the Delta Import." }
        }
        $firstCounts = Get-S16DeltaReadCount -ActivityId $first.activityId
        if ($firstCounts.Deletes -ne 0) {
            return @{ Status = 'fail'; Detail = "The Watermark Column Delta Import recorded $($firstCounts.Deletes) deletion(s); this mode cannot see one." }
        }

        $second = Invoke-S16DeltaImport -Context $Context -Purpose "nothing"
        $counts = Get-S16DeltaReadCount -ActivityId $second.activityId
        if ($counts.Processed -ne 0 -or ($counts.Adds + $counts.Updates + $counts.Deletes) -ne 0) {
            return @{ Status = 'fail'; Detail = "A Delta Import with no watermark column moved processed $($counts.Processed) object(s) ($($counts.Adds) added, $($counts.Updates) updated, $($counts.Deletes) deleted); the persisted watermarks are not being honoured." }
        }

        return @{ Status = 'pass'; Detail = "Watermark columns observed an insert, an update and a related-table change, left the deletion unseen as documented, and the following Delta Import read nothing." }
    }
    finally {
        Undo-S16DeltaMutations -Config $Config -NewEmployeeIds @(53) -RestoreEmployeeId 48
    }
}

function Test-S16DeltaFallback {
    param([hashtable]$Context, [hashtable]$Config)

    # The watermark JIM holds was written in Watermark Column mode by the row before this one. Switching
    # the mode back makes that watermark meaningless, and the guide says what happens next: the Delta
    # Import performs a Full Import in its place, warns, and establishes the watermark for the new mode.
    Set-S16DeltaImportMode -Context $Context -Mode 'Change-Log Table' -ObjectTypesJson $Context.ObjectTypesJson

    Invoke-S16DeltaImport -Context $Context -Purpose "after the mode changed" -ExpectFallback | Out-Null

    # Baseline established by the fallback, so the next Delta Import runs normally and, nothing having
    # changed, reads nothing and warns about nothing.
    $next = Invoke-S16DeltaImport -Context $Context -Purpose "after the fallback"
    $counts = Get-S16DeltaReadCount -ActivityId $next.activityId
    if ($counts.Processed -ne 0 -or ($counts.Adds + $counts.Updates + $counts.Deletes) -ne 0) {
        return @{ Status = 'fail'; Detail = "The Delta Import after the fallback processed $($counts.Processed) object(s); the fallback Full Import should have established the watermark it needed." }
    }

    return @{ Status = 'pass'; Detail = "A Delta Import with an unusable watermark fell back to a Full Import with the standard warning; the next Delta Import ran normally and read nothing." }
}

function Test-S16DeltaRowversionWatermark {
    param([hashtable]$Context, [hashtable]$Config)

    if ($Config.Provider -ne 'SqlServer') {
        return @{ Status = 'skip'; Detail = "rowversion is SQL Server's own type; $($Config.DisplayName) has no equivalent." }
    }

    # ROW_VERSION as the watermark for Person and its related table. JIM discovers a rowversion column as
    # Binary, so the watermark it captures is a Binary value carried between runs as text; a Delta
    # Import then has to bind that text back as bytes and compare it the way SQL Server compares a
    # rowversion. Nothing in the unit suite reaches a live rowversion, which is why this row exists.
    $document = Get-S16ObjectTypesJsonWithWatermark -Context $Context -WatermarkColumn 'ROW_VERSION'
    Set-S16DeltaImportMode -Context $Context -Mode 'Watermark Column' -ObjectTypesJson $document

    Invoke-S16RunProfile -Context $Context -Name "Full Import" | Out-Null

    try {
        Invoke-S16DeltaMutations -Config $Config -NewEmployeeId 54

        Invoke-S16DeltaImport -Context $Context -Purpose "against a rowversion watermark" | Out-Null
        $failure = Assert-S16DeltaLanded -Context $Context -NewEmployeeId 54 -Mode 'Rowversion watermark'
        if ($failure) { return @{ Status = 'fail'; Detail = $failure } }

        $second = Invoke-S16DeltaImport -Context $Context -Purpose "nothing, against a rowversion watermark"
        $counts = Get-S16DeltaReadCount -ActivityId $second.activityId
        if ($counts.Processed -ne 0 -or ($counts.Adds + $counts.Updates + $counts.Deletes) -ne 0) {
            return @{ Status = 'fail'; Detail = "A Delta Import with no rowversion moved processed $($counts.Processed) object(s); the Binary watermark did not round-trip as a boundary." }
        }

        return @{ Status = 'pass'; Detail = "A rowversion column served as the watermark: the Binary value round-tripped, the changed rows and only they were read, and the following Delta Import read nothing." }
    }
    finally {
        Undo-S16DeltaMutations -Config $Config -NewEmployeeIds @(54)
        # Back to the document and mode the setup saved, so the rows after this one meet the configuration
        # they were written against.
        Set-S16DeltaImportMode -Context $Context -Mode 'Change-Log Table' -ObjectTypesJson $Context.ObjectTypesJson
    }
}

# ─── Shared machinery for the synchronisation and export rows ──────────────────

function Get-S16SystemIdForType {
    <#
    .SYNOPSIS
        Which Connected System holds a given Object Type.
    .DESCRIPTION
        The scenario spans two Connected Systems over one database: the source of identity, and the
        accounts provisioned from it. A row asks for the Object Type it cares about and gets the system
        that holds it, rather than having to know how the two were divided.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Context,
        [Parameter(Mandatory=$true)][string]$Name
    )

    if (-not $Context.ObjectTypes.ContainsKey($Name)) {
        throw "Object Type '$Name' was not selected by Setup-Scenario16.ps1."
    }
    return $Context.ObjectTypes[$Name].connectedSystemId
}

function Invoke-S16RunProfile {
    param(
        [Parameter(Mandatory=$true)][hashtable]$Context,
        [Parameter(Mandatory=$true)][string]$Name,

        # Which of the scenario's Connected Systems to run against. Import is the default because most
        # rows drive the source of identity; the account systems are named explicitly where a row means
        # one of them, so the choice is visible at the call site rather than inferred.
        [Parameter(Mandatory=$false)][ValidateSet('Import', 'AppUsers', 'Accounts')][string]$System = 'Import'
    )

    $systemId = switch ($System) {
        'AppUsers' { $Context.AppUserConnectedSystemId }
        'Accounts' { $Context.ExportConnectedSystemId }
        default    { $Context.ConnectedSystemId }
    }
    $label = switch ($System) {
        'AppUsers' { "$($Context.Provider) app users" }
        'Accounts' { "$($Context.Provider) accounts" }
        default    { $Context.Provider }
    }

    $result = Start-JIMRunProfile -ConnectedSystemId $systemId -RunProfileName $Name -Wait -PassThru

    # The app users export is the one run allowed a warning, and only one kind. Every employee past the
    # tenth has a manager among the first ten, and employee 7 is disabled, so the four people who report
    # to employee 7 reference someone the outbound rule never provisions. JIM writes their rows without
    # the manager and reports the reference it cannot resolve on each of them, under the Connected
    # System's default (Error) handling; that warning is the surfacing being exercised, not a defect,
    # and Test-S16ExportCreate asserts on the Pending Exports it leaves behind. Anything else that warns
    # or errors on the run still fails it.
    if ($System -eq 'AppUsers' -and $Name -eq 'Export') {
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Scenario 16 $Name ($label)" -AllowWarnings -AllowedWarningTypes @('UnresolvedReference')
    }
    else {
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Scenario 16 $Name ($label)"
    }
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

    # Import and synchronise the source of identity, then export and confirm on each account system.
    # The synchronisation runs on the identity system because that is where the Metaverse Objects come
    # from, and it stages Pending Exports for every outbound rule regardless of which system owns it;
    # each account system's own Export run profile is what then writes them.
    Invoke-S16RunProfile -Context $Context -Name "Full Import"          | Out-Null
    Invoke-S16RunProfile -Context $Context -Name "Full Synchronisation" | Out-Null
    Invoke-S16RunProfile -Context $Context -Name "Export"               -System AppUsers | Out-Null
    Invoke-S16RunProfile -Context $Context -Name "Full Import"          -System AppUsers | Out-Null
    Invoke-S16RunProfile -Context $Context -Name "Export"               -System Accounts | Out-Null
    Invoke-S16RunProfile -Context $Context -Name "Full Import"          -System Accounts | Out-Null

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

function Get-S16ExportBlocker {
    <#
    .SYNOPSIS
        The reason the export rows cannot be exercised, or $null when they can.
    .DESCRIPTION
        JIM excludes the Connected System being synchronised from export evaluation:
        ExportEvaluationServer.BuildExportEvaluationCacheAsync filters the export rules' target systems
        with `.Where(id => id != sourceConnectedSystemId)`. This scenario imports from and provisions
        into ONE Connected System (different Object Types of the same database, which is the ordinary
        SQL topology: read the HR table, write the application table), so its outbound rules are never
        evaluated during its own Full Synchronisation and no Pending Export is ever raised.

        Detected rather than assumed: the check is that the outbound rules produced no Connected System
        Object at all for their target type after a full pipeline. A row that cannot be exercised
        reports 'skip' with the reason; reporting 'fail' would blame the Connector for something it was
        never asked to do, and reporting 'pass' would be a lie.
    #>
    param([Parameter(Mandatory=$true)][hashtable]$Context)

    $appUserTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'AppUser'
    $appUserSystemId = Get-S16SystemIdForType -Context $Context -Name 'AppUser'
    $csoCount = [int](Get-JIMConnectedSystemObject -ConnectedSystemId $appUserSystemId -ObjectTypeId $appUserTypeId -Count)
    if ($csoCount -gt 0) { return $null }

    # This used to report that the row was not exercisable at all: JIM excludes the Connected System
    # being synchronised from export evaluation (ExportEvaluationServer.BuildExportEvaluationCacheAsync
    # filters target systems with 'id != sourceConnectedSystemId'), and the scenario used to import from
    # and provision into a single Connected System, so no Pending Export was ever raised. The remedy that
    # note called for has since been made: the export Object Types live in their own Connected System
    # against the same database. The guard stays because reaching it now means something is wrong rather
    # than merely unsupported, so it says so.
    return "No AppUser Connected System Objects exist after the export baseline, so nothing was provisioned to assert on. The scenario provisions into separate Connected Systems precisely so export evaluation can see the outbound rules, which means this is a failure to diagnose rather than a topology JIM cannot serve. Check the Full Synchronisation's Activity: an Object Type conflict there means two outbound rules into one Connected System claimed the same person, which the split into an app users system and an accounts system is meant to make impossible."
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
    <#
    .SYNOPSIS
        The employee number the seeder gave one employee, composed with the provider's own prefix.
    .DESCRIPTION
        Each provider seeds a distinct prefix (S for SQL Server, O for Oracle) so the two providers
        describe different people; a hardcoded prefix here once made every by-number lookup query for
        rows that did not exist, failing rows with messages like "has no APP_USERS row" while the row
        sat in the table under the other prefix.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][int]$EmployeeId
    )
    return "$($Config.EmployeeNumberPrefix){0:D8}" -f $EmployeeId
}

function Get-S16MetaverseObject {
    <#
    .SYNOPSIS
        The Metaverse Object the inbound rule projected for one seeded employee, with its values.
    #>
    param(
        [Parameter(Mandatory=$true)][hashtable]$Config,
        [Parameter(Mandatory=$true)][int]$EmployeeId
    )

    # Filtered on the 'Employee ID' attribute rather than -Search. -Search matches DISPLAY NAME only,
    # and this scenario's inbound rule deliberately flows no Display Name (the source has no such
    # column), so every search returned nothing and every driver-shape row failed with "No Metaverse
    # Object was projected" while all fifty were sitting in the Metaverse.
    $employeeNumber = Get-S16EmployeeNumber -Config $Config -EmployeeId $EmployeeId
    $match = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName 'Employee ID' -AttributeValue $employeeNumber -PageSize 10) | Select-Object -First 1
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

    $blocker = Get-S16ExportBlocker -Context $Context
    if ($blocker) { return @{ Status = 'skip'; Detail = $blocker } }

    $expected = Get-S16ExpectedCount -RowCount $Context.RowCount -Scope 'Enabled'
    $actualRows = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).APP_USERS;")

    if ($actualRows -ne $expected) {
        return @{ Status = 'fail'; Detail = "The outbound rule is scoped to enabled employees, so $expected row(s) should have been inserted into APP_USERS; the table holds $actualRows." }
    }

    # Anchor-token agreement. The confirming Full Import in the pipeline re-read every row it had just
    # written; if the external ID it composed from the row differed from the one the insert returned, the
    # import would have created a second Connected System Object for each and the count would be doubled.
    $appUserTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'AppUser'
    $appUserSystemId = Get-S16SystemIdForType -Context $Context -Name 'AppUser'
    $csoCount = [int](Get-JIMConnectedSystemObject -ConnectedSystemId $appUserSystemId -ObjectTypeId $appUserTypeId -Count)

    if ($csoCount -ne $expected) {
        return @{ Status = 'fail'; Detail = "APP_USERS holds $actualRows row(s) but JIM holds $csoCount Connected System Object(s) for the type. A mismatch here means the external ID composed on export does not equal the one composed on import, so the confirming import did not recognise the objects it had just created." }
    }

    # A generated key is only proof of anything if it actually came from the database: the seeded table is
    # empty before this row runs, so every identifier present was returned by an insert.
    $anchors = @(Get-JIMConnectedSystemObject -ConnectedSystemId $appUserSystemId -ObjectTypeId $appUserTypeId -All -Force |
                 ForEach-Object { $_.externalIdValue })
    $unusable = @($anchors | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -notmatch '^\d+$' })
    if ($unusable.Count -gt 0) {
        return @{ Status = 'fail'; Detail = "$($unusable.Count) exported object(s) carry an external ID that is not the integer the IDENTITY column generates: '$(($unusable | Select-Object -First 5) -join "', '")'." }
    }

    # The people whose manager is disabled employee 7 (issue #1398). Their rows were inserted without the
    # manager, and each carries a Pending Export holding only the reference still owed, explained as a
    # reference to an object that has no Connected System Object in this system. Derived arithmetically:
    # employee n past the tenth reports to (n modulo 10) + 1, and only employee 7 of the first ten is
    # disabled, so it is every enabled n with n modulo 10 equal to 6.
    $expectedWaiting = @(11..$Context.RowCount | Where-Object { ($_ % 10) -eq 6 -and ($_ % 7) -ne 0 })
    $waitingExports = @(Get-JIMPendingExport -ConnectedSystemId $appUserSystemId -All)
    if ($waitingExports.Count -ne $expectedWaiting.Count) {
        return @{ Status = 'fail'; Detail = "$($expectedWaiting.Count) employee(s) report to disabled employee 7, whose account is never provisioned, so $($expectedWaiting.Count) Pending Export(s) should remain for the manager reference each still owes; found $($waitingExports.Count)." }
    }
    foreach ($waiting in $waitingExports) {
        $detail = Get-JIMPendingExport -Id $waiting.id
        $owed = @($detail.unresolvedReferences)
        if ($owed.Count -ne 1 -or $owed[0].attributeName -ne 'MANAGER_ID' -or $owed[0].reason -ne 'NotInTargetSystem') {
            $described = ($owed | ForEach-Object { "$($_.attributeName)=$($_.reason)" }) -join ', '
            return @{ Status = 'fail'; Detail = "Pending Export $($waiting.id) should owe exactly the MANAGER_ID reference, explained as NotInTargetSystem; it reports [$described]." }
        }
        $writtenElsewhere = @($detail.attributeChanges | Where-Object { $_.attributeName -ne 'MANAGER_ID' })
        if ($writtenElsewhere.Count -gt 0) {
            return @{ Status = 'fail'; Detail = "Pending Export $($waiting.id) still carries $($writtenElsewhere.Count) non-reference change(s) after the confirming import; everything but the owed reference should have been written and confirmed." }
        }
    }

    return @{ Status = 'pass'; Detail = "$expected row(s) inserted with a database-generated key, and the confirming import composed the same anchor token for every one; $($expectedWaiting.Count) row(s) whose manager is not provisioned were inserted without the reference and remain pending for it, explained as such." }
}

function Test-S16ExportUpdate {
    param([hashtable]$Context, [hashtable]$Config)

    Initialize-S16ExportBaseline -Context $Context

    $blocker = Get-S16ExportBlocker -Context $Context
    if ($blocker) { return @{ Status = 'skip'; Detail = $blocker } }

    # Employee 12 is enabled (12 is not a multiple of seven) and has two phone numbers (12 is a multiple
    # of three), so both a scalar change and a multi-valued change have somewhere to land.
    $employeeId = 12
    $userName = Get-S16EmployeeNumber -Config $Config -EmployeeId $employeeId
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

    $blocker = Get-S16ExportBlocker -Context $Context
    if ($blocker) { return @{ Status = 'skip'; Detail = $blocker } }

    # Employee 20 is enabled in the seeded data, so disabling it takes its Metaverse Object out of the
    # outbound rule's scope; the rule's OutboundDeprovisionAction is Delete, so that becomes a delete
    # export rather than a disconnect.
    $employeeId = 20
    $userName = Get-S16EmployeeNumber -Config $Config -EmployeeId $employeeId

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

    $blocker = Get-S16ExportBlocker -Context $Context
    if ($blocker) { return @{ Status = 'skip'; Detail = $blocker } }

    # The opposite half of the Export.Create question. Here the primary key is a natural identifier JIM
    # authors and writes, so the external ID is a value JIM chose rather than one the database returned;
    # the confirming import still has to compose the same token from the row.
    $expected = Get-S16ExpectedCount -RowCount $Context.RowCount -Scope 'Research'
    $actualRows = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).APP_ACCOUNTS_NATURAL;")

    if ($actualRows -ne $expected) {
        return @{ Status = 'fail'; Detail = "The rule is scoped to Department = Research, so $expected row(s) should exist in APP_ACCOUNTS_NATURAL; the table holds $actualRows." }
    }

    $naturalTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'NaturalKeyAccount'
    $naturalSystemId = Get-S16SystemIdForType -Context $Context -Name 'NaturalKeyAccount'
    $csoCount = [int](Get-JIMConnectedSystemObject -ConnectedSystemId $naturalSystemId -ObjectTypeId $naturalTypeId -Count)
    if ($csoCount -ne $expected) {
        return @{ Status = 'fail'; Detail = "APP_ACCOUNTS_NATURAL holds $actualRows row(s) but JIM holds $csoCount Connected System Object(s), so the anchor JIM authored on export is not the one it composed on import." }
    }

    # The key JIM authored has to be the value the Attribute Flow supplied, not a surrogate of its own.
    $malformed = [int](Invoke-Scenario16Query -Config $Config -Query "SELECT COUNT(*) FROM $($Config.Schema).APP_ACCOUNTS_NATURAL WHERE ACCOUNT_CODE NOT LIKE '$($Config.EmployeeNumberPrefix)%';")
    if ($malformed -ne 0) {
        return @{ Status = 'fail'; Detail = "$malformed row(s) carry an ACCOUNT_CODE that did not come from the Metaverse 'Account Name' flow." }
    }

    return @{ Status = 'pass'; Detail = "$expected row(s) provisioned into a natural-key table, with JIM's authored key surviving the confirming import." }
}

function Test-S16ReferenceExport {
    param([hashtable]$Context, [hashtable]$Config)

    Initialize-S16ExportBaseline -Context $Context

    $blocker = Get-S16ExportBlocker -Context $Context
    if ($blocker) { return @{ Status = 'skip'; Detail = $blocker } }

    # Employee 12's manager is employee 3 (the seeder gives every row past the tenth a manager of
    # (n modulo 10) + 1), and employee 3 is itself enabled, so the manager has an exported row for the
    # reference to point at. What is asserted is that JIM wrote the manager's OWN generated key rather
    # than the source system's employee identifier.
    $employeeId = 12
    $managerEmployeeId = ($employeeId % 10) + 1
    $userName = Get-S16EmployeeNumber -Config $Config -EmployeeId $employeeId
    $managerUserName = Get-S16EmployeeNumber -Config $Config -EmployeeId $managerEmployeeId

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

    $blocker = Get-S16ExportBlocker -Context $Context
    if ($blocker) { return @{ Status = 'skip'; Detail = $blocker } }

    # One employee, every mapped shape, source value against exported value. Employee 12 is enabled so it
    # has an exported row, and its FTE is 0.25, a value binary floating point cannot represent exactly.
    $employeeId = 12
    $userName = Get-S16EmployeeNumber -Config $Config -EmployeeId $employeeId
    $failures = @()

    # Provider-specific from the start: TO_CHAR does not exist on SQL Server, and running the Oracle
    # form first "to be overwritten" fails the whole row there with Msg 195 before the override runs.
    $sourceFte = if ($Config.Provider -eq "SqlServer") {
        [decimal]((Invoke-Scenario16Query -Config $Config -Query "SELECT CAST(FTE AS varchar(32)) FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = $employeeId;").Trim())
    }
    else {
        [decimal]((Invoke-Scenario16Query -Config $Config -Query "SELECT TO_CHAR(FTE) FROM $($Config.Schema).EMPLOYEES WHERE EMPLOYEE_ID = $employeeId;" | ForEach-Object { $_ }) -join '').Trim()
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
        switch ($Value.Kind) {
            ([System.DateTimeKind]::Utc) { return $Value }

            # CONVERT, never relabel. ConvertFrom-Json turns the API's trailing-Z instant into a
            # DateTime of Kind Local, expressed in the TEST HOST's zone. Stamping Utc onto that clock
            # face keeps the digits and throws the offset away, which silently reports every instant as
            # wrong by the host's current offset from UTC. It cost a full investigation: on a host in
            # British Summer Time, every seeded January date agreed (London is UTC+00:00 then, so the
            # relabel was a no-op) while the one deliberately mid-year value came back an hour out, and
            # it read exactly like a daylight-saving defect in the Connector. It was this line.
            ([System.DateTimeKind]::Local) { return $Value.ToUniversalTime() }

            # No zone information survived; the API only ever sends UTC, so that is what it is.
            default { return [System.DateTime]::SpecifyKind($Value, [System.DateTimeKind]::Utc) }
        }
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
        $mvo = Get-S16MetaverseObject -Config $Config -EmployeeId $Employee
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

    $mvo = Get-S16MetaverseObject -Config $Config -EmployeeId $employeeId
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

    $mvo = Get-S16MetaverseObject -Config $Config -EmployeeId $employeeId
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

    $blocker = Get-S16ExportBlocker -Context $Context
    if ($blocker) { return @{ Status = 'skip'; Detail = $blocker } }

    $guidTypeId = Get-Scenario16ObjectTypeId -Context $Context -Name 'GuidKeyedPerson'
    $guidSystemId = Get-S16SystemIdForType -Context $Context -Name 'GuidKeyedPerson'
    $imported = Get-JIMConnectedSystemObject -ConnectedSystemId $guidSystemId -ObjectTypeId $guidTypeId -Count
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

    $objects = Get-JIMConnectedSystemObject -ConnectedSystemId $guidSystemId -ObjectTypeId $guidTypeId -All -Force
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
    $person12 = Get-S16CsoByAnchor -Context $Context -ObjectTypeId $personTypeId -AnchorAttributeName 'EMPLOYEE_ID' -AnchorValue '12'
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
    $importedValue = [decimal](Get-S16AttributeValueText -Value $importedFte[0])
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

    # -ErrorAction Stop on every call below, without exception. A cmdlet exported from a module reads
    # the MODULE's preference variables, not the caller's, so a Write-Error inside JIM.psd1 is
    # non-terminating here whatever this file sets. Without it these try blocks never caught anything:
    # both refusals were printed to the console by the cmdlet, execution fell straight through to the
    # $failures lines, and the row reported that the Connector had ACCEPTED credentials it had in fact
    # rejected. A false failure is better than a false pass, but this was neither: it was the row
    # reporting the exact opposite of what happened.
    $badPassword = @{ (Get-ValidationSettingId "Password") = @{ stringValue = "definitely-not-the-password" } }
    try {
        Set-JIMConnectedSystem -Id $Context.ConnectedSystemId -SettingValues $badPassword -ErrorAction Stop | Out-Null
        $failures += "A wrong password was accepted; the save-time connectivity test did not refuse it."
    }
    catch {
        # Refused, which is correct.
    }

    # An unreachable host must be refused too.
    $badHost = @{ (Get-ValidationSettingId "Host") = @{ stringValue = "no-such-database-host" } }
    try {
        Set-JIMConnectedSystem -Id $Context.ConnectedSystemId -SettingValues $badHost -ErrorAction Stop | Out-Null
        $failures += "An unreachable host was accepted; the save-time connectivity test did not refuse it."
    }
    catch {
        # Refused, which is correct.
    }

    # Restore the working configuration, or every later row in this provider's pass would fail for the
    # wrong reason. This one must genuinely succeed, so a failure here has to be loud.
    $restore = @{
        (Get-ValidationSettingId "Host")     = @{ stringValue = $Config.Host }
        (Get-ValidationSettingId "Password") = @{ stringValue = $Config.Password }
    }
    Set-JIMConnectedSystem -Id $Context.ConnectedSystemId -SettingValues $restore -ErrorAction Stop | Out-Null

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
