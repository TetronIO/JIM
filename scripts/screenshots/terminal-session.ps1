# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Runs one terminal screenshot's commands inside the pseudo-terminal Capture-Screenshots.ps1 records (#582).

.DESCRIPTION
    Not for running by hand. The capture script starts this under `script`, in a 120x30 terminal, so every command's
    output is exactly what a terminal shows: its colours, its Format-List and Format-Table layout, and its width.

    A line starting with '!' is set-up (signing in, say): it runs first and silently. Before each command shown, this
    writes an invisible marker, and one more after the last, so TerminalScreen.ps1 can tell which output belongs to
    which command. A failure stops the session before the closing marker, which the capture script reports with the
    error the terminal showed.

    The API key is read from a file the capture script wrote for this run and is available to the commands as
    $apiKey. It is never on a command line; Connect-JIM itself shows only a masked form of it.
#>
param(
    [Parameter(Mandatory)][string]$CommandsFile,
    [Parameter(Mandatory)][string]$KeyFile,
    [Parameter(Mandatory)][string]$ModulePath
)

$ErrorActionPreference = 'Stop'

# Dates and numbers as the published images show them (23/04/2026), whatever locale the recording host has.
[System.Globalization.CultureInfo]::CurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo('en-GB')
[System.Globalization.CultureInfo]::CurrentUICulture = [System.Globalization.CultureInfo]::GetCultureInfo('en-GB')

try {
    Import-Module -Name $ModulePath -Force
    $apiKey = (Get-Content -LiteralPath $KeyFile -Raw).Trim()
    $lines = @(Get-Content -LiteralPath $CommandsFile | Where-Object { $_.Trim() -ne '' })

    foreach ($setup in @($lines | Where-Object { $_.StartsWith('!') })) {
        & ([scriptblock]::Create($setup.Substring(1))) *> $null
    }

    $shown = @($lines | Where-Object { -not $_.StartsWith('!') })
    for ($i = 0; $i -lt $shown.Count; $i++) {
        [Console]::Out.Write("`e]9999;cmd:$i`a")
        [Console]::Out.Flush()
        & ([scriptblock]::Create($shown[$i])) | Out-Default
    }
    [Console]::Out.Write("`e]9999;end`a")
    [Console]::Out.Flush()
}
catch {
    Write-Host ($_ | Out-String)
    exit 1
}
