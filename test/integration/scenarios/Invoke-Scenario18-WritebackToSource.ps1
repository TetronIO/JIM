# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Scenario 18: does JIM write a derived value back into the Connected System it came from? (#1284)

.DESCRIPTION
    "Attribute writeback" is an ordinary requirement: a system is authoritative for identity, JIM
    derives something from it, and that derived value belongs back in the same system. This scenario
    asks whether JIM does it, and it asks on the JIM File Connector, because the question is about
    the synchronisation engine rather than about any one connector.

    Three assertions, run in this order because each one narrows what the next can mean:

      1. Projection: the seeded people reach the Metaverse. Without this, nothing below means anything.

      2. Writeback during HR's own synchronisation. Two outbound rules of the same shape, on the same
         Metaverse Objects, with the same scope, are evaluated in ONE run of HR's Full Synchronisation:
         one targets the Control system, one targets HR itself. The control is what proves the rule
         shape and the scope are sound, so a difference between them cannot be blamed on either.

      3. The same writeback rule during the CONTROL system's synchronisation. Nothing about the rule,
         the scope or the Metaverse Objects has changed; only the identity of the system whose run is
         executing. This is what separates "the rule is broken" from "the rule is skipped while its
         own system is the one being synchronised", and it is the difference #1284 described.

    Written red-first against #1284 (export evaluation skipped every rule targeting the system being
    synchronised) and green since its fix: circular sync is prevented at value level by no-net-change
    detection (an echo of a value the target already holds stages nothing), so a genuine writeback
    stages during the source system's own run and assertion 3's diagnostic contrast reports
    "not applicable".

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Accepted for runner compatibility; this scenario seeds its own three users.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility; this scenario has no directory target.
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$Step = "All",

    # Passed by the runner whenever a directory snapshot is in use. This scenario has no directory
    # data to populate, but it must still accept the parameter or the runner's splat fails outright.
    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

$null = $Step

. "$PSScriptRoot/../utils/Test-Helpers.ps1"

$testResults = @{
    Scenario = "Writeback To Source Connected System"
    Success  = $false
    Steps    = New-Object System.Collections.Generic.List[object]
    Error    = $null
}

function Add-StepResult {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail,
        # An observation that informs the diagnosis without being a claim the scenario stands or falls
        # on. Reported as INFO so a reader is never asked to treat "we learned something" as "we
        # asserted something".
        [switch]$Informational
    )
    $testResults.Steps.Add([ordered]@{ Name = $Name; Passed = $Passed; Detail = $Detail; Informational = [bool]$Informational }) | Out-Null
    if ($Informational) {
        Write-Host "  INFO $Name" -ForegroundColor Cyan
    } elseif ($Passed) {
        Write-Host "  PASS $Name" -ForegroundColor Green
    } else {
        Write-Host "  FAIL $Name" -ForegroundColor Red
    }
    if ($Detail) { Write-Host "       $Detail" -ForegroundColor DarkGray }
}

try {
    Write-TestSection "Scenario 18: Writeback into the source Connected System (#1284)"

    if (-not $ApiKey) { throw "API key required for authentication" }

    # ─── Step 0: seed the two files ────────────────────────────────────────────

    Write-TestStep "Step 0" "Seeding the HR and Control CSVs"

    $stage = Join-Path ([IO.Path]::GetTempPath()) "scenario18"
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    # 'writeback' is present but empty for every row: the value JIM should put there has to be a
    # genuine change, not one that already agrees with what was imported.
    $hrCsv = Join-Path $stage "scenario18-hr.csv"
    @(
        "employeeId,accountName,jobTitle,writeback"
        "S18-001,ada.ashcroft,Engineer,"
        "S18-002,bram.brandt,Engineer,"
        "S18-003,cleo.calder,Engineer,"
    ) | Set-Content -Path $hrCsv -Encoding UTF8

    # Header-only: the Control system starts empty and receives everything from the Metaverse.
    $controlCsv = Join-Path $stage "scenario18-control.csv"
    "employeeId,writeback" | Set-Content -Path $controlCsv -Encoding UTF8

    Write-FileToConnectorVolume -SourcePath $hrCsv      -DestinationPath "/connector-files/test-data/scenario18-hr.csv"
    Write-FileToConnectorVolume -SourcePath $controlCsv -DestinationPath "/connector-files/test-data/scenario18-control.csv"
    Write-Host "  OK Seeded 3 people into HR, and a header-only Control file" -ForegroundColor Green

    Write-Host "Running Scenario 18 setup..." -ForegroundColor Gray
    $context = & "$PSScriptRoot/../Setup-Scenario18.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template
    if (-not $context) { throw "Setup-Scenario18.ps1 returned no configuration." }

    # Setup removes and re-imports the module, so reconnect before issuing cmdlets here.
    Import-Module "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1" -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $hrId      = $context.HrConnectedSystemId
    $controlId = $context.ControlConnectedSystemId

    function Invoke-S18RunProfile {
        param([int]$ConnectedSystemId, [string]$Name, [string]$Label)
        $result = Start-JIMRunProfile -ConnectedSystemId $ConnectedSystemId -RunProfileName $Name -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Scenario 18 $Name ($Label)"
        return $result
    }

    # ─── Assertion 1: the people reach the Metaverse ───────────────────────────

    Write-TestSection "Assertion 1: the seeded people project into the Metaverse"

    Invoke-S18RunProfile -ConnectedSystemId $hrId -Name "Full Import"          -Label "HR" | Out-Null
    Invoke-S18RunProfile -ConnectedSystemId $hrId -Name "Full Synchronisation" -Label "HR" | Out-Null

    $projected = 0
    foreach ($employeeId in @("S18-001", "S18-002", "S18-003")) {
        $match = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue $employeeId -PageSize 5)
        if ($match.Count -ge 1) { $projected++ }
    }

    if ($projected -ne 3) {
        Add-StepResult -Name "Projection" -Passed $false -Detail "Expected 3 Metaverse Objects for the seeded people; found $projected. Nothing below can be interpreted until this passes."
        throw "Scenario 18 cannot continue: only $projected of 3 people projected."
    }
    Add-StepResult -Name "Projection" -Passed $true -Detail "All 3 seeded people projected into the Metaverse."

    # ─── Assertion 2: writeback during HR's own synchronisation ────────────────

    Write-TestSection "Assertion 2: the writeback rule is evaluated during HR's own synchronisation"

    $controlPending  = [int](Get-JIMPendingExport -ConnectedSystemId $controlId -Count)
    $writebackPending = [int](Get-JIMPendingExport -ConnectedSystemId $hrId -Count)

    Write-Host "  Pending Exports after HR's Full Synchronisation:" -ForegroundColor Gray
    Write-Host "    Control system (a different system) : $controlPending" -ForegroundColor Gray
    Write-Host "    HR system      (the run's source)   : $writebackPending" -ForegroundColor Gray

    if ($controlPending -lt 1) {
        Add-StepResult -Name "Control rule staged exports" -Passed $false `
            -Detail "The control outbound rule staged no Pending Exports either, so the rule shape, the Attribute Flows or the Job Title scope are wrong. Fix the scenario before drawing any conclusion about #1284."
        throw "Scenario 18's control assertion failed; the comparison is not meaningful."
    }
    Add-StepResult -Name "Control rule staged exports" -Passed $true `
        -Detail "$controlPending Pending Export(s) for the Control system, so the rule shape and the scope are sound."

    $writebackEvaluated = ($writebackPending -ge 1)
    Add-StepResult -Name "Writeback rule staged exports" -Passed $writebackEvaluated `
        -Detail $(if ($writebackEvaluated) {
            "$writebackPending Pending Export(s) staged back into HR."
        } else {
            "No Pending Export was staged for HR, though the identically shaped control rule staged $controlPending. This is #1284: export evaluation excludes the Connected System being synchronised, so a writeback into the source system never happens during that system's own run."
        })

    # ─── Assertion 3: the same rule, a different run ───────────────────────────

    Write-TestSection "Assertion 3: the same writeback rule during the Control system's synchronisation"

    # Flush the control exports so the Control system has objects of its own to import, then
    # synchronise from Control. HR is not the source of this run, so if the exclusion is what
    # suppresses the writeback, HR's rule is evaluated this time and the counts move.
    Invoke-S18RunProfile -ConnectedSystemId $controlId -Name "Export"               -Label "Control" | Out-Null
    Invoke-S18RunProfile -ConnectedSystemId $controlId -Name "Full Import"          -Label "Control" | Out-Null
    Invoke-S18RunProfile -ConnectedSystemId $controlId -Name "Full Synchronisation" -Label "Control" | Out-Null

    $writebackAfterControlRun = [int](Get-JIMPendingExport -ConnectedSystemId $hrId -Count)
    Write-Host "  Pending Exports for HR after the Control system's Full Synchronisation: $writebackAfterControlRun" -ForegroundColor Gray

    if ($writebackEvaluated) {
        Add-StepResult -Name "Diagnosis: how the writeback behaves in a later run" -Passed $true -Informational `
            -Detail "Not applicable: the writeback already staged during HR's own run, which is the behaviour #1284 asks for."
    }
    elseif ($writebackAfterControlRun -ge 1) {
        Add-StepResult -Name "Diagnosis: how the writeback behaves in a later run" -Passed $true -Informational `
            -Detail "The SAME writeback rule staged $writebackAfterControlRun Pending Export(s) once a different system was the one being synchronised, so the rule is sound and the writeback is MISTIMED rather than lost: it lands whenever some unrelated run happens to touch the object."
    }
    else {
        Add-StepResult -Name "Diagnosis: how the writeback behaves in a later run" -Passed $true -Informational `
            -Detail "The writeback staged nothing in the later run either. Export evaluation is driven by Metaverse Object CHANGES, and the change that should have triggered the writeback was consumed by the very run that skipped it, so a later synchronisation finds nothing to re-evaluate. On this evidence the writeback is LOST rather than merely deferred, which is worse than the mistiming this scenario was written expecting."
    }

    # ─── Verdict ───────────────────────────────────────────────────────────────

    $testResults.Success = $writebackEvaluated

    Write-TestSection "Scenario 18 Summary"
    # NOT $step: PowerShell variable names are case-insensitive, so $step is this script's own [string]
    # $Step parameter, and every property read off it silently fails.
    foreach ($stepResult in $testResults.Steps) {
        if ($stepResult.Informational) {
            Write-Host ("  INFO {0}" -f $stepResult.Name) -ForegroundColor Cyan
            continue
        }
        $colour = if ($stepResult.Passed) { "Green" } else { "Red" }
        Write-Host ("  {0} {1}" -f $(if ($stepResult.Passed) { "OK  " } else { "FAIL" }), $stepResult.Name) -ForegroundColor $colour
    }
    Write-Host ""
    if ($testResults.Success) {
        Write-Host "  Result: PASS (a writeback into the source Connected System is staged)" -ForegroundColor Green
    } else {
        Write-Host "  Result: FAIL (#1284 regression: no writeback was staged into the Connected System being synchronised)" -ForegroundColor Red
    }
}
catch {
    $testResults.Success = $false
    $testResults.Error = $_.Exception.Message
    Write-Failure "Scenario 18 failed: $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    throw
}

return $testResults
