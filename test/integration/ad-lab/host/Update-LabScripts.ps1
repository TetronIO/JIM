# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Keeps the lab's deployed scripts and JIM PowerShell module at the head of main: brings a dedicated clone up to date
    and runs Install-LabScripts.ps1 from it.

.DESCRIPTION
    The AD FS registration refuses a module copy that is not the commit under test (Register-LabIdentityProvider.ps1),
    so a merge that changes src/JIM.PowerShell fails the AD FS scenarios, Scenario 030 in jim-ad-lab among them, until
    the host's copy is redeployed. Run hourly by the scheduled task Register-LabScriptsUpdateTask.ps1 creates, this keeps
    the copy at the head of main, which is what the nightly and a dispatch on main test.

    The clone is this script's own (<SourceDirectory>), never a working checkout: it is reset hard to origin/<Branch> and
    cleaned on every run. The repository is public, so it needs no credentials. Appends what it did to <LogPath>, keeping
    the last 2000 lines, and exits non-zero on failure.

.PARAMETER SourceDirectory
    The dedicated clone. Created on the first run. Default C:\jim-ad-lab-source.

.PARAMETER Destination
    Where Install-LabScripts.ps1 deploys to. Default C:\jim-ad-lab.

.PARAMETER Repository
    The repository to clone. Default https://github.com/TetronIO/JIM.git.

.PARAMETER Branch
    The branch to follow. Default main.

.PARAMETER LogPath
    The log. Default <Destination>\logs\update-lab-scripts.log.

.EXAMPLE
    C:\jim-ad-lab\Update-LabScripts.ps1
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Progress for an operator running it by hand; the scheduled task reads the log.')]
[CmdletBinding()]
param(
    [string]$SourceDirectory = 'C:\jim-ad-lab-source',

    [string]$Destination = 'C:\jim-ad-lab',

    [string]$Repository = 'https://github.com/TetronIO/JIM.git',

    [ValidatePattern('^[A-Za-z0-9._/-]+$')]
    [string]$Branch = 'main',

    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $LogPath) { $LogPath = Join-Path $Destination 'logs' 'update-lab-scripts.log' }
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force

function Write-Log {
    param([string]$Text)
    $line = "$(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK') $Text"
    Add-Content -LiteralPath $LogPath -Value $line -Encoding utf8
    Write-Host $line
}

function Invoke-Git {
    param([string[]]$Arguments)
    $output = @(& git @Arguments 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments[0]) failed (exit $LASTEXITCODE): $($output -join ' ')" }
    return $output
}

$exitCode = 0
try {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory '.git'))) {
        Write-Log "Cloning $Repository ($Branch) into $SourceDirectory"
        $null = Invoke-Git @('clone', '--quiet', '--branch', $Branch, '--single-branch', $Repository, $SourceDirectory)
    }
    $before = @(Invoke-Git @('-C', $SourceDirectory, 'rev-parse', 'HEAD'))[0]
    $null = Invoke-Git @('-C', $SourceDirectory, 'fetch', '--quiet', 'origin', $Branch)
    $null = Invoke-Git @('-C', $SourceDirectory, 'reset', '--quiet', '--hard', "origin/$Branch")
    $null = Invoke-Git @('-C', $SourceDirectory, 'clean', '--quiet', '-fdx')
    $after = @(Invoke-Git @('-C', $SourceDirectory, 'rev-parse', 'HEAD'))[0]

    $install = Join-Path $SourceDirectory 'test' 'integration' 'ad-lab' 'host' 'Install-LabScripts.ps1'
    $installed = @(& pwsh -NoProfile -NonInteractive -File $install -Destination $Destination -RepositoryRoot $SourceDirectory 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { throw "Install-LabScripts.ps1 failed (exit $LASTEXITCODE): $($installed | Select-Object -Last 3)" }

    $changes = @($installed | Where-Object { $_ -match '^\s+(new|updated|removed)\s' } | ForEach-Object { $_.Trim() })
    $commit = if ($before -eq $after) { "at $($after.Substring(0, 9))" } else { "$($before.Substring(0, 9)) -> $($after.Substring(0, 9))" }
    Write-Log "main $commit; $(if ($changes.Count -eq 0) { 'deployed scripts already current' } else { "$($changes.Count) file(s): $($changes -join '; ')" })"
}
catch {
    Write-Log "FAILED: $($_.Exception.Message)"
    $exitCode = 1
}
finally {
    $lines = @(Get-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue)
    if ($lines.Count -gt 2000) { Set-Content -LiteralPath $LogPath -Value ($lines | Select-Object -Last 2000) -Encoding utf8 }
}
exit $exitCode
