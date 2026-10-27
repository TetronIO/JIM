# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# What Register-JIMIdentityProvider's providers share (#2364): AdfsRegistration.ps1 and KeycloakRegistration.ps1.

# JIM's loopback redirect URIs: Invoke-OAuthBrowserFlow listens on the first free port from 8400, trying ten.
$script:JIMLoopbackPorts = 8400..8409

function Get-JIMLoopbackRedirectUri {
    # The PowerShell module's redirect URIs, one for each port it may listen on.
    [CmdletBinding()]
    [OutputType([string[]])]
    param()

    @($script:JIMLoopbackPorts | ForEach-Object { "http://localhost:$_/callback/" })
}

function Test-JIMSameSet {
    # Whether two lists hold the same values, in any order.
    param([AllowNull()][object[]]$Left, [AllowNull()][object[]]$Right)

    $l = @($Left | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ } | Sort-Object -Unique -CaseSensitive)
    $r = @($Right | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ } | Sort-Object -Unique -CaseSensitive)
    if ($l.Count -ne $r.Count) { return $false }
    for ($i = 0; $i -lt $l.Count; $i++) {
        if ($l[$i] -cne $r[$i]) { return $false }
    }
    return $true
}

function Invoke-JIMRegistrationPlan {
    <#
    .SYNOPSIS
        Makes a registration plan's changes one at a time, each asked of -WhatIf and -Confirm on its own.

    .PARAMETER Apply
        Makes one change; returns the client secret when the change made one.

    .OUTPUTS
        The changes made and the client secret, if one was made, as a secure string. Nothing when a change was
        declined, because the values are only worth returning once they exist on the identity provider.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Plan,
        [Parameter(Mandatory)][System.Management.Automation.PSCmdlet]$Cmdlet,
        [Parameter(Mandatory)][scriptblock]$Apply
    )

    $made = [System.Collections.Generic.List[string]]::new()
    $secret = $null
    $declined = $false
    try {
        foreach ($change in $Plan.Changes) {
            if (-not $Cmdlet.ShouldProcess($change.Target, $change.Description)) {
                $declined = $true
                continue
            }
            $result = & $Apply $change
            if ($result) { $secret = ConvertTo-SecureString -String ([string]$result) -AsPlainText -Force }
            $made.Add("$($change.Target): $($change.Description)")
        }
    }
    catch {
        # The failure means the secret is never returned, so say how to get another.
        if ($secret) {
            Write-Warning "The identity provider made the portal's client secret before this run failed, and this run cannot return it. Once the cause is fixed, run this again with -ResetClientSecret for a new one."
        }
        throw
    }

    if ($declined) { return }
    [PSCustomObject]@{ Changes = $made.ToArray(); Secret = $secret }
}
