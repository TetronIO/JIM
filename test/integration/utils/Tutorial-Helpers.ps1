# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Reads a docs tutorial's PowerShell steps and checkpoints, for the scenario that executes it.

.DESCRIPTION
    A tested tutorial (TUT-1 in engineering/DOCUMENTATION_STANDARD.md) is executed by an integration
    scenario that keeps no copy of its steps: it reads them from the page, so a change to the page is
    a change to the test. Get-TutorialStep returns, in page order:

      - each fenced powershell block inside a `=== "PowerShell"` content tab (Kind 'Code'). Other tabs
        (Portal, Docker, Podman) are the same step done another way, and code outside any tab (the
        Connect-JIM in "Before you begin") is the reader's own setup, which the scenario does itself;
      - each `<!-- checkpoint: <expression> -->` marker (Kind 'Checkpoint'): a PowerShell expression,
        run after the steps above it, that must return $true;
      - each `<!-- checkpoint-file: <path> rows=<n> -->` marker (Kind 'FileCheckpoint'): a file in the
        connector files volume that must hold <n> data rows below its header.

    Every item carries the step it belongs to (the nearest H2 above it) and the line it starts on. A
    checkpoint marker that cannot be read is an error, never skipped: a dropped checkpoint is an
    assertion the release suite silently stops making.
#>

function Get-TutorialStep {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $lines = @(Get-Content -LiteralPath $Path)
    $step = $null
    $inPowerShellTab = $false
    $fence = $null          # the open code block's indentation, and whether it runs
    $code = @()
    $codeLine = 0

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $number = $i + 1

        if ($fence) {
            if ($line -match "^$([regex]::Escape($fence.Indent))``````\s*$") {
                if ($fence.Run) {
                    [pscustomobject]@{ Kind = 'Code'; Step = $step; Line = $codeLine; Code = ($code -join "`n") }
                }
                $fence = $null
                continue
            }
            if ($fence.Run) {
                # Remove the tab's indentation only; the block's own indentation is the reader's code.
                $code += if ($line.StartsWith($fence.Indent)) { $line.Substring($fence.Indent.Length) } else { $line.TrimStart() }
            }
            continue
        }

        if ($line -match '^##\s+(?<heading>.+?)\s*$') {
            $step = $Matches.heading
            $inPowerShellTab = $false
            continue
        }

        if ($line -match '^===\s+"(?<tab>[^"]+)"') {
            $inPowerShellTab = $Matches.tab -eq 'PowerShell'
            continue
        }

        # A non-indented, non-blank line ends a content tab.
        if ($inPowerShellTab -and $line -match '^\S') { $inPowerShellTab = $false }

        # A language may hold a hyphen (jim-diagram), so it is any run of characters but a space or backtick.
        if ($line -match '^(?<indent>\s*)```(?<lang>[^\s`]+)?\s*$') {
            $isTabbed = $Matches.indent.Length -ge 4
            $fence = @{ Indent = $Matches.indent; Run = ($inPowerShellTab -and $isTabbed -and $Matches.lang -eq 'powershell') }
            $code = @()
            $codeLine = $number + 1
            continue
        }

        if ($line -match '^\s*<!--\s*checkpoint-file:') {
            if ($line -notmatch '^\s*<!--\s*checkpoint-file:\s*(?<path>/\S+)\s+rows=(?<rows>\d+)\s*-->\s*$') {
                throw "Tutorial checkpoint at line ${number} of ${Path} cannot be read; write it as <!-- checkpoint-file: /connector-files/<path> rows=<n> -->."
            }
            [pscustomobject]@{ Kind = 'FileCheckpoint'; Step = $step; Line = $number; Path = $Matches.path; Rows = [int]$Matches.rows }
            continue
        }

        if ($line -match '^\s*<!--\s*checkpoint:') {
            if ($line -notmatch '^\s*<!--\s*checkpoint:\s*(?<expr>\S.*?)\s*-->\s*$') {
                throw "Tutorial checkpoint at line ${number} of ${Path} has no expression; write it as <!-- checkpoint: <PowerShell that returns `$true> -->."
            }
            [pscustomobject]@{ Kind = 'Checkpoint'; Step = $step; Line = $number; Expression = $Matches.expr }
        }
    }

    if ($fence) { throw "Tutorial ${Path} has a code block opened at line $($codeLine - 1) and never closed." }
}
