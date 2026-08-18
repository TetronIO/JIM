# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    JIM SQL Connector provider x capability matrix (SQL Server, Oracle)

.DESCRIPTION
    The JIM SQL Connector's correctness gate. One scenario implementation, executed once per supported
    database server, covering every capability row of the PRD's Testing Requirements table.

    Four rows carry more weight than the rest, because they are the only way to settle assumptions the
    unit suite cannot reach. Each is marked "driver shape" in the matrix below:

      1. Oracle RAW(16) anchor. The unit tests assume ODP.NET hands a RAW(16) column back as byte[],
         and that an OracleDbType.Raw output parameter returns a SYS_GUID()-generated key from
         RETURNING ... INTO. A table keyed RAW(16) DEFAULT SYS_GUID(), imported, exported and
         re-imported, settles both.
      2. Date and time round trip on a NON-UTC Connected System. At the UTC default every zone
         conversion is the identity, so a zone-inversion defect passes a UTC-configured test unnoticed.
         The setup configures Europe/London for exactly this reason.
      3. Offset-carrying versus zoneless columns in the same table. Import decides which is which from
         the runtime CLR type the driver returns; export decides from the catalogue's type name. Those
         are two different oracles for one question, and they only have to disagree once. Oracle's
         TIMESTAMP WITH LOCAL TIME ZONE is the case where they plausibly do.
      4. NUMBER column shapes. The unit tests assume ODP.NET answers every NUMBER with a decimal.

    Coverage tiers, following the Scenario 11 precedent:
      -Quick      the representative subset for the regular gate
      (default)   every functional row, both providers
      -FullMatrix everything plus the 500,000-row scale import

.PARAMETER Provider
    Which database server(s) to run against: SqlServer, Oracle, or Both.

.PARAMETER Quick
    Representative subset only. Mutually exclusive with -FullMatrix.

.PARAMETER FullMatrix
    Every row including the 500,000-row scale import. Mutually exclusive with -Quick.
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("SqlServer", "Oracle", "Both")]
    [string]$Provider = "Both",

    [Parameter(Mandatory=$false)]
    [switch]$Quick,

    [Parameter(Mandatory=$false)]
    [switch]$FullMatrix,

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig,

    # Accepted for runner-API compatibility. This scenario has no directory to populate.
    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

$null = $DirectoryConfig
$null = $Template
$null = $SkipPopulate

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/Scenario16-Helpers.ps1"

if ($Quick -and $FullMatrix) {
    throw "-Quick and -FullMatrix are mutually exclusive. Pick one tier (or neither for the default)."
}
if (-not $ApiKey) {
    throw "API key required for authentication. Pass -ApiKey or set it via the runner."
}

$activeTier = if ($Quick) { 'Quick' } elseif ($FullMatrix) { 'FullMatrix' } else { 'Default' }
$providers = if ($Provider -eq "Both") { @("SqlServer", "Oracle") } else { @($Provider) }

# Rows below this row count are the functional matrix; the scale row overrides it.
$functionalRowCount = 50
$scaleRowCount = 500000

Write-TestSection "Scenario 16: JIM SQL Connector Provider x Capability Matrix"
Write-Host "  Coverage tier:  $activeTier" -ForegroundColor Cyan
Write-Host "  Providers:      $($providers -join ', ')" -ForegroundColor Cyan
Write-Host "  Step filter:    $Step" -ForegroundColor Cyan
Write-Host ""

# ─── The matrix ────────────────────────────────────────────────────────────────
#
# One row per capability from the PRD's Testing Requirements table. 'tiers' controls which coverage
# tiers run the row; 'providers' restricts a row to the servers it can apply to (Oracle's RAW(16)
# anchor and TIMESTAMP WITH LOCAL TIME ZONE have no SQL Server equivalent).

$matrixRows = @(
    @{
        name = 'FullImport.Table'
        description = 'Full Import from a table, keyset paging across multiple pages, typed values'
        tiers = @('Quick', 'Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'FullImport.View'
        description = 'Full Import from a view; anchor stays read-only because a view is unwritable'
        tiers = @('Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'MultiValued.Import'
        description = 'Related-table values gathered onto the parent object as a multi-valued attribute'
        tiers = @('Quick', 'Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'Reference.Import'
        description = 'Anchor-carrying column resolved to a Connected System Object reference'
        tiers = @('Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'Delta.ChangeLogTable'
        description = 'Creates, updates, a related-table change and a deletion propagated from the change log; watermark persisted and honoured'
        tiers = @('Quick', 'Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'Delta.WatermarkColumn'
        description = 'Creates, updates and a related-table change propagated from watermark columns; a deletion is NOT detected, as documented'
        tiers = @('Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'Delta.Fallback'
        description = 'An unusable watermark (the mode changed) falls back to Full Import with the standard warning'
        tiers = @('Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'Delta.RowversionWatermark'
        description = 'A SQL Server rowversion column as the watermark: the Binary watermark round-trips as a boundary'
        tiers = @('Default', 'FullMatrix')
        providers = @('SqlServer')
    }
    @{
        name = 'Export.Create'
        description = 'Row inserted; database-generated key returned as the external ID; auto-confirmed'
        tiers = @('Quick', 'Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'Export.Update'
        description = 'Attribute changes applied; related-table rows added and removed transactionally'
        tiers = @('Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'Export.Delete'
        description = 'Row and related rows removed; per-object error isolation on failure'
        tiers = @('Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'Export.NaturalKey'
        description = 'Provisioning into a table whose primary key is a natural identifier JIM authors'
        tiers = @('Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'Reference.Export'
        description = 'Anchor value written for a reference attribute'
        tiers = @('Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'TypeMapping.RoundTrip'
        description = 'Each mapped SQL type imports and exports losslessly, including exact-numeric Decimal'
        tiers = @('Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    # ── The four driver-shape rows ──
    @{
        name = 'DriverShape.DateTimeNonUtc'
        description = 'DRIVER SHAPE: zoneless date and time round trip on a NON-UTC Connected System'
        tiers = @('Quick', 'Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'DriverShape.OffsetVersusZoneless'
        description = 'DRIVER SHAPE: offset-carrying and zoneless columns in one table agree end to end'
        tiers = @('Quick', 'Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'DriverShape.LocalTimeZone'
        description = 'DRIVER SHAPE: Oracle TIMESTAMP WITH LOCAL TIME ZONE; import and export must agree'
        tiers = @('Quick', 'Default', 'FullMatrix')
        providers = @('Oracle')
    }
    @{
        name = 'DriverShape.Raw16Anchor'
        description = 'DRIVER SHAPE: Oracle RAW(16) DEFAULT SYS_GUID() anchor imported, exported, re-imported'
        tiers = @('Quick', 'Default', 'FullMatrix')
        providers = @('Oracle')
    }
    @{
        name = 'DriverShape.NumberShapes'
        description = 'DRIVER SHAPE: every NUMBER precision and scale arrives as an exact Decimal'
        tiers = @('Quick', 'Default', 'FullMatrix')
        providers = @('Oracle')
    }
    @{
        name = 'ConfigurationValidation'
        description = 'Save-time connectivity test passes and fails correctly (bad credentials, bad host)'
        tiers = @('Quick', 'Default', 'FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
    @{
        name = 'Scale.FullImport500k'
        description = '500,000-row Full Import without unbounded memory growth'
        tiers = @('FullMatrix')
        providers = @('SqlServer', 'Oracle')
    }
)

# ─── Result accumulation ───────────────────────────────────────────────────────
#
# Rows accumulate rather than throwing individually, following Scenario 11: one failing capability
# should not hide the state of the other nineteen, and the point of a matrix is the whole grid.

$cellResults = New-Object System.Collections.Generic.List[object]
$cellPass = 0
$cellFail = 0
$cellSkip = 0

function Add-CellResult {
    param(
        [Parameter(Mandatory=$true)][string]$Provider,
        [Parameter(Mandatory=$true)][string]$Row,
        [Parameter(Mandatory=$true)][string]$Status,
        [Parameter(Mandatory=$false)][string]$Detail = ''
    )

    $script:cellResults.Add([ordered]@{ provider = $Provider; row = $Row; status = $Status; detail = $Detail }) | Out-Null

    switch ($Status) {
        'pass' {
            $script:cellPass++
            Write-Host "    PASS $Provider / $Row" -ForegroundColor Green
        }
        'fail' {
            $script:cellFail++
            Write-Host "    FAIL $Provider / $Row" -ForegroundColor Red
            if ($Detail) { Write-Host "      $Detail" -ForegroundColor DarkGray }
        }
        default {
            $script:cellSkip++
            Write-Host "    SKIP $Provider / $Row" -ForegroundColor DarkYellow
            if ($Detail) { Write-Host "      $Detail" -ForegroundColor DarkGray }
        }
    }
}

# ─── Per-provider execution ────────────────────────────────────────────────────

$modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

foreach ($currentProvider in $providers) {
    $config = Get-DatabaseConfig -Provider $currentProvider

    $rowsForProvider = @($matrixRows | Where-Object {
        $_.tiers -contains $activeTier -and $_.providers -contains $currentProvider
    })

    if ($Step -ne 'All') {
        $rowsForProvider = @($rowsForProvider | Where-Object { $_.name -eq $Step })
        if ($rowsForProvider.Count -eq 0) {
            throw "-Step '$Step' did not match any matrix row for provider '$currentProvider' in tier '$activeTier'."
        }
    }

    if ($rowsForProvider.Count -eq 0) {
        Write-Host "  No rows selected for $currentProvider in tier '$activeTier'; skipping" -ForegroundColor DarkYellow
        continue
    }

    Write-TestSection "Provider: $($config.DisplayName) ($($rowsForProvider.Count) row(s))"

    # The scale row needs a different database from the functional rows, so it decides the seed size.
    $needsScale = @($rowsForProvider | Where-Object { $_.name -eq 'Scale.FullImport500k' }).Count -gt 0
    $seedRowCount = if ($needsScale) { $scaleRowCount } else { $functionalRowCount }

    Write-TestStep "Seed" "Seeding $($config.DisplayName) with $seedRowCount employee row(s)"
    & "$PSScriptRoot/../New-Scenario16TestDatabase.ps1" -Provider $currentProvider -RowCount $seedRowCount | Out-Null

    Write-TestStep "Setup" "Configuring JIM against $($config.DisplayName)"
    $context = & "$PSScriptRoot/../Setup-Scenario16.ps1" `
        -JIMUrl $JIMUrl -ApiKey $ApiKey `
        -Provider $currentProvider -RowCount $seedRowCount

    if (-not $context) {
        throw "Setup-Scenario16.ps1 returned no configuration for $currentProvider."
    }

    Write-TestStep "Matrix" "Running $($rowsForProvider.Count) capability row(s)"

    foreach ($row in $rowsForProvider) {
        # Start each row on an empty sentinel. Start-JIMRunProfile aborts its wait whenever the watcher
        # has captured anything, and the sentinel accumulates for the whole run, so without this one
        # row's errors abort every Run Profile after it and the rest of the matrix reports failures it
        # never had. That is not hypothetical: four export errors on the first provider once cost every
        # remaining SQL Server row and all nineteen Oracle rows, which made a two-defect run look like
        # twenty-three.
        $leakedFromPreviousRow = @(Clear-JimErrorWatcher)
        if ($leakedFromPreviousRow.Count -gt 0) {
            Write-Host "    (cleared $($leakedFromPreviousRow.Count) error line(s) left by the previous row)" -ForegroundColor DarkGray
        }

        try {
            $outcome = Invoke-Scenario16Row -Row $row -Context $context -Config $config
            $detail = $outcome.Detail
        }
        catch {
            $outcome = @{ Status = 'fail' }
            $detail = $_.Exception.Message
        }

        # Errors this row provoked are named on the row itself rather than left to abort the next one.
        # A row that passed while the services logged an error is still reported as passing: the row's
        # own assertion is what decides, and Assert-NoWorkerErrors at the end of the run is what holds
        # the whole run to account for the errors themselves.
        $rowErrors = @(Clear-JimErrorWatcher)
        if ($rowErrors.Count -gt 0) {
            $detail = "$detail Services logged $($rowErrors.Count) error line(s) during this row; first: $($rowErrors[0])"
        }

        Add-CellResult -Provider $currentProvider -Row $row.name -Status $outcome.Status -Detail $detail
    }
}

# ─── Summary ───────────────────────────────────────────────────────────────────

$overallPass = ($cellFail -eq 0)

Write-TestSection "Scenario 16 Summary"
Write-Host "  Coverage tier:  $activeTier" -ForegroundColor Cyan
Write-Host "  Providers:      $($providers -join ', ')" -ForegroundColor Cyan
Write-Host "  Matrix cells:   $cellPass passed, $cellFail failed, $cellSkip not exercised" -ForegroundColor $(if ($cellFail -eq 0) { 'Green' } else { 'Red' })

# Skipped cells are listed rather than summarised away. A row nobody ran is a gap in the gate, and the
# whole point of recording it as 'skip' instead of 'pass' is that it stays visible.
if ($cellSkip -gt 0) {
    Write-Host ""
    Write-Host "  Not exercised:" -ForegroundColor DarkYellow
    foreach ($skipped in @($cellResults | Where-Object { $_.status -eq 'skip' })) {
        Write-Host "    - $($skipped.provider) / $($skipped.row): $($skipped.detail)" -ForegroundColor DarkGray
    }
}
Write-Host ""
Write-Host "  Result: $(if ($overallPass) { 'PASS' } else { 'FAIL' })" -ForegroundColor $(if ($overallPass) { 'Green' } else { 'Red' })

if (-not $overallPass) {
    $failed = @($cellResults | Where-Object { $_.status -eq 'fail' } | ForEach-Object { "$($_.provider)/$($_.row)" })
    throw "Scenario 16 failed: $cellFail cell(s) did not pass ($($failed -join ', '))."
}

return @{
    Scenario = "JIM SQL Connector Matrix"
    Tier     = $activeTier
    Cells    = $cellResults
    Success  = $overallPass
}
