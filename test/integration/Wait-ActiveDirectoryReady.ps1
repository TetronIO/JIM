# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Waits for an Active Directory lab domain controller to be ready for integration testing.

.DESCRIPTION
    The Active Directory lab is real Windows Server domain controllers running as Hyper-V virtual
    machines, reverted to a checkpoint at the start of a run. A restored machine answers on 636 before
    it is usable, and can come back with a stale clock until the host's time is applied to it, so this
    polls until both of these are true:

      1. an LDAPS simple bind as JIM's service account (JimBindDN, svc-jim) succeeds, checked with
         ldapwhoami through the LDAP toolbox container; and
      2. the domain controller's own clock (root DSE currentTime) is within 5 seconds of this
         machine's UTC clock.

    If either is still failing at the timeout it exits 1, naming what it last saw, including the
    observed clock skew in seconds.

    Because the bind is over LDAPS with the certificate verified, the toolbox needs the domain
    controller's certificate first. Unless -SkipCertificateRefresh is given this fetches it
    (Save-ActiveDirectoryCertificate: openssl s_client through the toolbox, Subject Alternative Name
    checked, written to test/integration/ad-lab-certs) on the first poll that can reach the domain
    controller, so the readiness check can be the first thing the runner calls after a revert.

    The toolbox container (compose profile ad-lab) must already be running.

.PARAMETER DirectoryConfig
    An ActiveDirectory config from Get-DirectoryConfig.

.PARAMETER TimeoutSeconds
    Maximum time to wait (default: 300 seconds / 5 minutes).

.PARAMETER CheckIntervalSeconds
    How often to check (default: 5 seconds).

.PARAMETER ToleranceSeconds
    The largest clock skew accepted (default: 5 seconds).

.PARAMETER SkipCertificateRefresh
    Do not fetch the domain controller's certificate; the caller has already done it.

.EXAMPLE
    ./Wait-ActiveDirectoryReady.ps1 -DirectoryConfig (Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Primary)
#>

param(
    [Parameter(Mandatory=$true)]
    [hashtable]$DirectoryConfig,

    [Parameter(Mandatory=$false)]
    [int]$TimeoutSeconds = 300,

    [Parameter(Mandatory=$false)]
    [int]$CheckIntervalSeconds = 5,

    [Parameter(Mandatory=$false)]
    [double]$ToleranceSeconds = 5,

    [Parameter(Mandatory=$false)]
    [switch]$SkipCertificateRefresh
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. "$PSScriptRoot/utils/LDAP-Helpers.ps1"
if (-not $SkipCertificateRefresh) {
    . "$PSScriptRoot/utils/Test-Helpers.ps1"
}

# Color codes
$ESC = [char]27
$BLUE = "$ESC[34m"
$GREEN = "$ESC[32m"
$YELLOW = "$ESC[33m"
$RED = "$ESC[31m"
$GRAY = "$ESC[90m"
$NC = "$ESC[0m"  # No Color

if (-not (Test-ActiveDirectoryConfig -DirectoryConfig $DirectoryConfig)) {
    Write-Host "${RED}✗ Wait-ActiveDirectoryReady needs an ActiveDirectory directory config, not '$($DirectoryConfig['DirectoryType'])'${NC}"
    exit 1
}

$vmName = $DirectoryConfig.VmName
$hostName = $DirectoryConfig.Host

Write-Host ""
Write-Host "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
Write-Host "${BLUE}  Waiting for Active Directory ($vmName, $hostName) to become ready...${NC}"
Write-Host "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
Write-Host ""

# The toolbox runs every LDAP tool; without it nothing below can work.
Write-Host "${GRAY}Checking if the LDAP toolbox container is running...${NC}"
$toolboxStatus = docker ps --filter "name=^/jim-ldap-toolbox$" --format "{{.Status}}" 2>$null

if (-not $toolboxStatus) {
    Write-Host "${RED}✗ The LDAP toolbox container (jim-ldap-toolbox) is not running${NC}"
    Write-Host "${YELLOW}  Start it with: docker compose -f test/integration/docker/docker-compose.integration-tests.yml --profile ad-lab up -d ldap-toolbox${NC}"
    exit 1
}

Write-Host "${GREEN}✓ Toolbox is running${NC}"
Write-Host ""

$startTime = Get-Date
$elapsed = 0
$ready = $false
$lastDot = 0
$certificateSaved = [bool]$SkipCertificateRefresh
$lastBindOutcome = "not attempted"
$lastSkewSeconds = $null
$lastProblem = "no attempt made"

Write-Host "${GRAY}Waiting for the domain controller to accept an LDAPS bind as $($DirectoryConfig.JimBindDN) and report the right time...${NC}"
Write-Host ""

while ($elapsed -lt $TimeoutSeconds) {
    try {
        if (-not $certificateSaved) {
            Save-ActiveDirectoryCertificate -DirectoryConfig $DirectoryConfig | Out-Null
            $certificateSaved = $true
        }

        $bind = Test-LDAPBind -BindDN $DirectoryConfig.JimBindDN -BindPassword $DirectoryConfig.JimBindPassword -DirectoryConfig $DirectoryConfig
        $lastBindOutcome = $bind.Outcome

        if ($bind.Outcome -eq 'Success') {
            $directoryTime = Get-LdapRootDseTime -DirectoryConfig $DirectoryConfig `
                -BindDN $DirectoryConfig.JimBindDN -BindPassword $DirectoryConfig.JimBindPassword
            $skew = Test-LdapClockSkew -DirectoryTime $directoryTime -ToleranceSeconds $ToleranceSeconds
            $lastSkewSeconds = $skew.SkewSeconds

            if ($skew.WithinTolerance) {
                $ready = $true
                break
            }
            $lastProblem = "the domain controller's clock is $([math]::Round($skew.SkewSeconds, 1)) seconds from this machine's (tolerance $ToleranceSeconds)"
        }
        else {
            $lastProblem = "the LDAPS bind as svc-jim was not accepted ($($bind.Outcome)): $($bind.Output)"
        }
    }
    catch {
        # Not reachable yet, or not serving a usable certificate yet: keep polling until the timeout.
        $lastProblem = $_.Exception.Message
    }

    # Show progress
    $elapsed = ((Get-Date) - $startTime).TotalSeconds
    $currentSecond = [math]::Floor($elapsed)
    if ($currentSecond -ne $lastDot) {
        $lastDot = $currentSecond
        $dots = [math]::Floor($elapsed / $CheckIntervalSeconds)
        $bar = "=" * [math]::Min(50, $dots)
        $spaces = " " * (50 - $bar.Length)
        Write-Host -NoNewline "`r  [${bar}${spaces}] ${currentSecond}s / ${TimeoutSeconds}s"
    }

    Start-Sleep -Seconds $CheckIntervalSeconds
    $elapsed = ((Get-Date) - $startTime).TotalSeconds
}

Write-Host ""  # New line after progress bar
Write-Host ""

if ($ready) {
    Write-Host "${GREEN}✓ Active Directory ($vmName) is ready!${NC}"
    Write-Host ""
    Write-Host "${GRAY}  Bind as $($DirectoryConfig.JimBindDN): Success${NC}"
    Write-Host "${GRAY}  Clock skew against this machine: $([math]::Round($lastSkewSeconds, 1)) seconds (tolerance $ToleranceSeconds)${NC}"
    Write-Host ""
    Write-Host "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    Write-Host "${GREEN}  Active Directory ($vmName) is ready for integration testing!${NC}"
    Write-Host "${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    Write-Host ""

    exit 0
}
else {
    Write-Host "${RED}✗ Timed out waiting for Active Directory ($vmName) to become ready${NC}"
    Write-Host ""
    Write-Host "${YELLOW}Last observed:${NC}"
    Write-Host "  Bind as svc-jim: $lastBindOutcome"
    if ($null -ne $lastSkewSeconds) {
        Write-Host "  Clock skew: $([math]::Round($lastSkewSeconds, 1)) seconds (tolerance $ToleranceSeconds)"
    }
    Write-Host "  Problem: $lastProblem"
    Write-Host ""
    Write-Host "${YELLOW}Troubleshooting:${NC}"
    Write-Host "  1. Check the VM is running and its checkpoint restored: Get-LabDomainController.ps1 -Name $vmName"
    Write-Host "  2. Check the toolbox reaches it: docker exec jim-ldap-toolbox openssl s_client -connect $($DirectoryConfig.Address):636 -servername $hostName </dev/null"
    Write-Host "  3. A clock skew means the Hyper-V Time Synchronization integration service is off for the VM, or the host's own clock is wrong"
    Write-Host ""

    exit 1
}
