# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Invoke-IntegrationScenario, which decides whether an integration
    scenario passed.

.DESCRIPTION
    Guards the verdict the integration test runner reports. Before #1382 the runner read
    $LASTEXITCODE straight after calling the scenario with the call operator, which
    PowerShell does not set for a .ps1 that returns rather than exits. The value that
    survived was whatever the last native command inside the scenario had left, so
    Scenario 16 printed "Result: PASS" and the run exited 1 because SQL*Plus had exited 1.

    The reverse is the costlier direction and is covered here too: a scenario that fails
    without throwing must not be reported as a pass just because its last native call
    happened to return 0.

    Each test writes a real scenario script to disk and runs it, rather than mocking the
    invocation, because the defect lives in how PowerShell propagates exit codes across
    the call operator and a mock would not reproduce it.
#>

BeforeAll {
    . "$PSScriptRoot/Invoke-IntegrationScenario.ps1"

    $script:scenarioRoot = Join-Path ([System.IO.Path]::GetTempPath()) "jim-scenario-outcome-$([System.Guid]::NewGuid())"
    New-Item -ItemType Directory -Path $script:scenarioRoot -Force | Out-Null

    # Writes a scenario script and hands back its path. Every scenario body here starts by
    # running a native command that exits non-zero, which is what a real scenario does when
    # it shells out to sqlplus, docker or psql on its way to a perfectly good result.
    function New-TestScenario {
        param(
            [Parameter(Mandatory = $true)][string]$Name,
            [Parameter(Mandatory = $true)][string]$Body,
            [switch]$NoDirtyExitCode
        )

        $preamble = if ($NoDirtyExitCode) { '' } else {
            '& pwsh -NoProfile -Command "exit 1" | Out-Null' + [Environment]::NewLine
        }

        $path = Join-Path $script:scenarioRoot "$Name.ps1"
        Set-Content -LiteralPath $path -Value ($preamble + $Body) -Encoding utf8
        return $path
    }
}

AfterAll {
    if (Test-Path $script:scenarioRoot) {
        Remove-Item -LiteralPath $script:scenarioRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Describe 'Invoke-IntegrationScenario' {

    Context 'when the scenario returns a result object' {

        It 'reports success even though an internal native command exited non-zero' {
            # This is #1382 exactly: Scenario 16 against Oracle.
            $path = New-TestScenario -Name 'ReturnsSuccess' -Body @'
Write-Host "  Result: PASS"
return @{ Scenario = "Fake"; Success = $true }
'@

            $outcome = Invoke-IntegrationScenario -Path $path

            $outcome.ExitCode | Should -Be 0
        }

        It 'reports failure when the object says so, whatever the exit code holds' {
            # The costlier direction: a scenario that fails without throwing must not pass
            # just because its last native call returned 0.
            $path = New-TestScenario -Name 'ReturnsFailure' -NoDirtyExitCode -Body @'
& pwsh -NoProfile -Command "exit 0" | Out-Null
Write-Host "  Result: FAIL"
return @{ Scenario = "Fake"; Success = $false }
'@

            $outcome = Invoke-IntegrationScenario -Path $path

            $outcome.ExitCode | Should -Be 1
        }

        It 'hands the result object back to the caller' {
            $path = New-TestScenario -Name 'ReturnsDetail' -Body @'
return @{ Scenario = "Fake"; Cells = @("a", "b"); Success = $true }
'@

            $outcome = Invoke-IntegrationScenario -Path $path

            $outcome.Result.Scenario | Should -Be 'Fake'
            $outcome.Result.Cells.Count | Should -Be 2
        }

        It 'accepts a PSCustomObject result as readily as a hashtable' {
            $path = New-TestScenario -Name 'ReturnsPsCustomObject' -Body @'
return [PSCustomObject]@{ Scenario = "Fake"; Success = $false }
'@

            $outcome = Invoke-IntegrationScenario -Path $path

            $outcome.ExitCode | Should -Be 1
        }
    }

    Context 'when the scenario exits' {

        It 'reports the code the scenario exited with' {
            $path = New-TestScenario -Name 'ExitsThree' -NoDirtyExitCode -Body @'
Write-Host "  giving up"
exit 3
'@

            $outcome = Invoke-IntegrationScenario -Path $path

            $outcome.ExitCode | Should -Be 3
        }

        It 'reports success when the scenario exits zero after a dirty native call' {
            $path = New-TestScenario -Name 'ExitsZero' -Body @'
exit 0
'@

            $outcome = Invoke-IntegrationScenario -Path $path

            $outcome.ExitCode | Should -Be 0
        }
    }

    Context 'when the scenario neither returns a result object nor exits' {

        It 'is not coloured by an exit code left over from before the scenario started' {
            $path = New-TestScenario -Name 'ReturnsNothingCleanly' -NoDirtyExitCode -Body @'
Write-Host "  Result: PASS"
'@
            $global:LASTEXITCODE = 1

            $outcome = Invoke-IntegrationScenario -Path $path

            $outcome.ExitCode | Should -Be 0
        }

        It 'falls back to the exit code, which is why a returning scenario must return a result object' {
            # Pinning the one case the function cannot get right, so that the next person to
            # read it knows it is a known limit rather than an oversight. A scenario that
            # returns normally leaves $LASTEXITCODE holding whatever its last native call
            # set, and PowerShell offers no way to tell that apart from a genuine exit 1.
            # The remedy is the contract, not a cleverer heuristic: Scenario 6 and Scenario
            # 11 were given result objects so that nothing relies on this path.
            $path = New-TestScenario -Name 'ReturnsNothingAfterDirtyCall' -Body @'
Write-Host "  Result: PASS"
'@

            $outcome = Invoke-IntegrationScenario -Path $path

            $outcome.ExitCode | Should -Be 1
        }
    }

    Context 'when the scenario throws' {

        It 'lets the exception reach the caller so the runner can report it' {
            $path = New-TestScenario -Name 'Throws' -Body @'
throw "Scenario 11 failed: 2 cell(s) did not match expected results."
'@

            { Invoke-IntegrationScenario -Path $path } | Should -Throw '*did not match expected results*'
        }
    }

    Context 'output' {

        It 'passes the scenario parameters through' {
            $path = New-TestScenario -Name 'TakesParameters' -NoDirtyExitCode -Body @'
param([string]$Template, [string]$Provider)
return @{ Success = ($Template -eq "Nano" -and $Provider -eq "Oracle") }
'@

            $outcome = Invoke-IntegrationScenario -Path $path -Parameters @{ Template = 'Nano'; Provider = 'Oracle' }

            $outcome.ExitCode | Should -Be 0
        }

        It 'still emits the scenario output that is not the result object' {
            $path = New-TestScenario -Name 'WritesOutput' -NoDirtyExitCode -Body @'
Write-Output "a line the operator needs to see"
return @{ Success = $true }
'@

            $emitted = Invoke-IntegrationScenario -Path $path 6>&1 | Out-String

            $emitted | Should -BeLike '*a line the operator needs to see*'
        }

        It 'returns a single outcome even when the scenario also writes pipeline output' {
            # The regression this pins: passing non-result objects back out through the function's own
            # output stream turned the return value into an array whenever a scenario emitted anything,
            # and the runner's $outcome.ExitCode then threw under strict mode. Scenario 16 (all
            # Write-Host) never tripped it; Scenario 8 did, on the first Phase 0 baseline run for #288.
            $path = New-TestScenario -Name 'NoisyPipeline' -NoDirtyExitCode -Body @'
Write-Output "pipeline noise before the verdict"
"another stray pipeline object"
return @{ Success = $true }
'@

            $outcome = Invoke-IntegrationScenario -Path $path 6> $null

            $outcome -is [System.Collections.IDictionary] | Should -BeTrue
            $outcome.ExitCode | Should -Be 0
        }
    }
}
