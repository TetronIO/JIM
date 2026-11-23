# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Starts the nightly Active Directory lab run (ad-lab.yml on main) from the lab host at 01:23 UTC, because GitHub's
    own schedule for it fires six to eight hours late.

.DESCRIPTION
    GitHub created every scheduled jim-ad-lab run of November 2026 between 07:16 and 09:17 UTC, for a cron of 01:23, so
    the four-hour nightly ran into the working day. The scheduled task Register-LabNightlyTask.ps1 creates runs this at
    01:23 UTC instead: it dispatches ad-lab.yml on main with the nightly's inputs (every scenario, Medium, the probes)
    and trigger=host-nightly, which names the run "jim-ad-lab nightly (lab host)".

    One nightly a day, whichever starts first. This dispatches nothing if a scheduled run of ad-lab.yml, or one this
    script started, was created since 00:00 UTC today; and the workflow's own schedule, kept as the fallback for a
    host that is off or a token that has expired, skips itself when this script's run was created since 00:00 UTC
    that day (the "Is the lab host's nightly already running?" job).

    The token is a fine-grained personal access token for TetronIO/JIM with Actions: read and write, and nothing else.
    -SetToken asks for it (it is never a parameter, so it is never in a command line or history) and stores it in
    <TokenPath>, encrypted with DPAPI for this machine and readable only by SYSTEM and Administrators: not in
    C:\jim-ad-lab, which the lab's control account may modify. Nothing here prints it.

    Appends what it did to <LogPath>, keeping the last 2000 lines, and exits non-zero on failure.

.PARAMETER SetToken
    Ask for the token and store it, then exit. Run it from an elevated session; run it again to replace the token.

.PARAMETER Repository
    owner/name. Default TetronIO/JIM.

.PARAMETER Workflow
    The workflow file. Default ad-lab.yml.

.PARAMETER Branch
    The ref to run. Default main: a schedule only ever runs main, and only main's copy of the workflow may reach the lab.

.PARAMETER TokenPath
    Default C:\ProgramData\JIM\ad-lab\nightly-dispatch-token.bin.

.PARAMETER LogPath
    Default C:\jim-ad-lab\logs\start-lab-nightly.log.

.EXAMPLE
    C:\jim-ad-lab\Start-LabNightly.ps1 -SetToken

.EXAMPLE
    C:\jim-ad-lab\Start-LabNightly.ps1 -WhatIf
    Reports whether today's nightly would be started, and starts nothing.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Progress for an operator running it by hand; the scheduled task reads the log.')]
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$SetToken,

    [ValidatePattern('^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$')]
    [string]$Repository = 'TetronIO/JIM',

    [ValidatePattern('^[A-Za-z0-9._-]+\.ya?ml$')]
    [string]$Workflow = 'ad-lab.yml',

    [ValidatePattern('^[A-Za-z0-9._/-]+$')]
    [string]$Branch = 'main',

    [string]$TokenPath = 'C:\ProgramData\JIM\ad-lab\nightly-dispatch-token.bin',

    [string]$LogPath = 'C:\jim-ad-lab\logs\start-lab-nightly.log'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The run name ad-lab.yml gives a run started with trigger=host-nightly (its run-name), which is how either side
# recognises this script's runs.
$hostRunName = 'jim-ad-lab nightly (lab host)'

# The log is written under -WhatIf too: it is what -WhatIf reports to.
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $LogPath) -Force -WhatIf:$false

function Write-Log {
    param([string]$Text)
    $line = "$(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK') $Text"
    Add-Content -LiteralPath $LogPath -Value $line -Encoding utf8 -WhatIf:$false
    Write-Host $line
    $lines = @(Get-Content -LiteralPath $LogPath)
    if ($lines.Count -gt 2000) { $lines[-2000..-1] | Set-Content -LiteralPath $LogPath -Encoding utf8 -WhatIf:$false }
}

Add-Type -AssemblyName System.Security

if ($SetToken) {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run -SetToken from an elevated session: the token file is readable only by SYSTEM and Administrators.'
    }
    $secure = Read-Host -AsSecureString -Prompt "Fine-grained token for $Repository (Actions: read and write)"
    $plain = [Net.NetworkCredential]::new('', $secure).Password
    if ([string]::IsNullOrWhiteSpace($plain)) { throw 'No token was entered; nothing was stored.' }

    $directory = Split-Path -Parent $TokenPath
    $null = New-Item -ItemType Directory -Path $directory -Force
    # SYSTEM and Administrators only, nothing inherited, on the folder and so on the file.
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $directory -AclObject $acl

    $bytes = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($plain), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    [IO.File]::WriteAllBytes($TokenPath, $bytes)
    $plain = $null
    Write-Log "Stored the dispatch token in $TokenPath (DPAPI, machine scope; SYSTEM and Administrators only)."
    return
}

try {
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $haveToken = Test-Path -LiteralPath $TokenPath
    if ($haveToken) {
        $token = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
                [IO.File]::ReadAllBytes($TokenPath), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine))
        $headers.Authorization = "Bearer $token"
    }
    elseif (-not $WhatIfPreference) {
        throw "There is no dispatch token at $TokenPath. Store one with: Start-LabNightly.ps1 -SetToken"
    }

    # Today's nightly, if one has started: a scheduled run, or one this script started, created since 00:00 UTC.
    $today = [DateTime]::UtcNow.Date
    $uri = "https://api.github.com/repos/$Repository/actions/workflows/$Workflow/runs?branch=$Branch&created=>=$($today.ToString('yyyy-MM-dd'))&per_page=100"
    $runs = @((Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 60).workflow_runs)
    $started = @($runs | Where-Object { $_.event -eq 'schedule' -or $_.display_title -eq $hostRunName })
    if ($started.Count -gt 0) {
        $first = $started | Sort-Object created_at | Select-Object -First 1
        Write-Log "Today's nightly has already started: run $($first.id) ($($first.event), created $($first.created_at)). Nothing to do."
        return
    }

    $body = @{ ref = $Branch; inputs = @{ scenario = 'All'; template = 'Medium'; run_probes = 'true'; trigger = 'host-nightly' } } | ConvertTo-Json -Compress
    if ($PSCmdlet.ShouldProcess("$Repository $Workflow on $Branch", 'Start the nightly')) {
        Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repository/actions/workflows/$Workflow/dispatches" `
            -Headers $headers -Body $body -ContentType 'application/json' -TimeoutSec 60 | Out-Null
        Write-Log "Started the nightly: $Workflow on $Branch (trigger=host-nightly)."
    }
    else {
        Write-Log "WhatIf: no nightly has started today; would start $Workflow on $Branch (trigger=host-nightly)$(if (-not $haveToken) { ', but there is no token yet' })."
    }
}
catch {
    $message = $_.Exception.Message
    if ($headers.ContainsKey('Authorization')) { $message = $message.Replace($headers.Authorization, 'Bearer ***') }
    Write-Log "FAILED: $message"
    exit 1
}
