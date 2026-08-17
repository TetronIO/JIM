# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Runs an integration scenario script and works out whether it passed.

.DESCRIPTION
    The runner used to read $LASTEXITCODE straight after calling the scenario with the call
    operator. PowerShell does not set $LASTEXITCODE for a .ps1 that returns rather than
    exits, so the value that survived was whatever the last native command inside the
    scenario had left behind. Scenario 16 against Oracle printed "Result: PASS" and the run
    exited 1, because it reaches Oracle through docker exec ... sqlplus and SQL*Plus had
    exited 1. The same scenario against SQL Server exited 0, which made it look like an
    Oracle quirk rather than the general defect it is (#1382).

    The costlier direction is the reverse: a scenario that fails without throwing and
    without exiting would be reported as a pass whenever its last native call returned 0.

    A scenario therefore signals its outcome in exactly one of three ways, and this
    function reads them in that order of authority:

      1. It throws. The exception is left to propagate so the runner reports it.
      2. It returns a result object carrying a Success property. That is the scenario's own
         verdict and beats anything $LASTEXITCODE holds.
      3. It calls exit. PowerShell does set $LASTEXITCODE for that, reliably, and it
         overwrites any stray code from within the scenario.

    A scenario that returns normally without a result object is left with case 3's answer,
    which is the one case that can still inherit a stray code. That is why every scenario
    which returns rather than exits must return a result object; Scenario 6 and Scenario 11
    were changed alongside this function so that none currently relies on it.
#>

function Invoke-IntegrationScenario {
    [CmdletBinding()]
    param(
        # The scenario script to run.
        [Parameter(Mandatory = $true)]
        [string]$Path,

        # Splatted to the scenario script.
        [Parameter(Mandatory = $false)]
        [hashtable]$Parameters = @{}
    )

    # Nothing the runner did during setup should be able to colour the scenario's verdict.
    $global:LASTEXITCODE = 0

    # Collected through a list rather than a variable assignment because the ForEach-Object
    # block runs in its own scope; a method call on an object resolved from this scope works
    # regardless. Everything that is not the result object is passed straight back out, so
    # the scenario's output still streams to the operator as it is produced.
    $results = [System.Collections.Generic.List[object]]::new()

    & $Path @Parameters | ForEach-Object {
        if (Test-ScenarioResultObject -Candidate $_) { $results.Add($_) } else { $_ }
    }

    $exitCode = $LASTEXITCODE
    $result = if ($results.Count -gt 0) { $results[$results.Count - 1] } else { $null }

    if ($null -ne $result) {
        $exitCode = if ($result.Success) { 0 } else { 1 }
    }

    return @{
        ExitCode = $exitCode
        Result   = $result
    }
}

<#
.SYNOPSIS
    Tells a scenario's result object apart from its ordinary output.

.DESCRIPTION
    Scenarios return either a hashtable (Scenario 16) or a PSCustomObject, so both shapes
    are recognised. The Success property is what makes an object a verdict rather than a
    piece of output that happens to be a dictionary.
#>
function Test-ScenarioResultObject {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $false)]
        [AllowNull()]
        [object]$Candidate
    )

    if ($null -eq $Candidate) { return $false }

    if ($Candidate -is [System.Collections.IDictionary]) {
        return $Candidate.Contains('Success')
    }

    return $null -ne $Candidate.PSObject.Properties['Success']
}
