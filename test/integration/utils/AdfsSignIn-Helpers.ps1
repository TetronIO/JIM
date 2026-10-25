# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The runner's side of the AD FS sign-in checks (#2441): puts the browserless sign-in client in the LDAP toolbox
    container and runs one check there.

.DESCRIPTION
    The checks run in the toolbox (jim-ldap-toolbox), the lab's stand-in for an administrator's workstation: it resolves
    jim.web and the AD FS server's name, which the runner cannot, and trusts their certificates once
    Initialize-AdfsSignInClient.ps1 has run. Each check is Invoke-AdfsSignInCheck.ps1 in the container, which prints one
    ADFS-SIGN-IN-RESULT line; ConvertFrom-AdfsSignInCheckOutput reads it.

    A person's name and password reach the container as environment variables of the docker exec (-e NAME takes the
    value from this process), never on a command line, and are removed from this process again afterwards.
#>

Set-StrictMode -Version Latest

$script:AdfsSignInToolbox = 'jim-ldap-toolbox'
$script:AdfsSignInClientDirectory = '/tmp/jim-sign-in'
$script:AdfsSignInBinDirectory = '/tmp/jim-sign-in-bin'

function Invoke-AdfsSignInDocker {
    # The one place these helpers run docker, so the tests replace it without needing docker installed. Returns
    # every line it printed, as text; $LASTEXITCODE is docker's.
    param([string[]]$Arguments)
    return @(& docker @Arguments 2>&1 | ForEach-Object { "$_" })
}

function ConvertFrom-AdfsSignInCheckOutput {
    <#
    .SYNOPSIS
        Reads what Invoke-AdfsSignInCheck.ps1 printed: the JSON after its ADFS-SIGN-IN-RESULT marker. Throws when there is
        none, naming the last lines printed.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Line
    )

    $marked = @($Line | Where-Object { "$_" -like 'ADFS-SIGN-IN-RESULT *' }) | Select-Object -Last 1
    if (-not $marked) {
        $tail = @($Line | Where-Object { "$_".Trim() } | Select-Object -Last 5) -join ' | '
        throw "The sign-in check printed no result. Last output: $tail"
    }
    return ($marked.Substring('ADFS-SIGN-IN-RESULT '.Length) | ConvertFrom-Json)
}

function Initialize-AdfsSignInToolbox {
    <#
    .SYNOPSIS
        Copies the sign-in client, the JIM PowerShell module and jim.web's certificate into the toolbox, and has it
        trust that certificate and the AD FS server's (Initialize-AdfsSignInClient.ps1).

    .PARAMETER RepositoryRoot
        The JIM checkout.

    .PARAMETER FederationCertificateName
        The AD FS server's certificate file in the toolbox's /certs (test/integration/ad-lab-certs), such as
        adfs-primary.pem.

    .PARAMETER JimCertificatePath
        jim.web's certificate on this machine (test/integration/results/jim-web-tls/tls.crt).
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Prepares a throwaway test container.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$FederationCertificateName,
        [Parameter(Mandatory)][string]$JimCertificatePath
    )

    $toolbox = $script:AdfsSignInToolbox
    $client = $script:AdfsSignInClientDirectory
    $utils = Join-Path $RepositoryRoot 'test' 'integration' 'utils'

    function Invoke-Docker {
        param([string[]]$Arguments)
        $output = Invoke-AdfsSignInDocker -Arguments $Arguments
        if ($LASTEXITCODE -ne 0) { throw "docker $($Arguments[0]) failed: $($output -join ' ')" }
        return $output
    }

    $null = Invoke-Docker @('exec', '-u', 'root', $toolbox, 'sh', '-c', "rm -rf '$client' '$($script:AdfsSignInBinDirectory)' && mkdir -p '$client' && chmod 755 '$client'")
    foreach ($file in 'AdfsSignInClient.psm1', 'Invoke-AdfsSignInCheck.ps1', 'Initialize-AdfsSignInClient.ps1', 'Complete-AdfsBrowserSignIn.ps1') {
        $null = Invoke-Docker @('cp', (Join-Path $utils $file), "$($toolbox):$client/$file")
    }
    $null = Invoke-Docker @('cp', (Join-Path $RepositoryRoot 'src' 'JIM.PowerShell'), "$($toolbox):$client/JIM")
    $null = Invoke-Docker @('cp', $JimCertificatePath, "$($toolbox):$client/jim-web.pem")
    $null = Invoke-Docker @('exec', '-u', 'root', $toolbox, 'chmod', '-R', 'a+rX', $client)

    $output = Invoke-Docker @('exec', $toolbox, 'pwsh', '-NoProfile', '-NonInteractive', '-File', "$client/Initialize-AdfsSignInClient.ps1",
        '-ClientDirectory', $client, '-CertificatePath', "/certs/$FederationCertificateName,$client/jim-web.pem", '-BinDirectory', $script:AdfsSignInBinDirectory)
    return $output
}

function Invoke-AdfsSignInCheck {
    <#
    .SYNOPSIS
        Runs one sign-in check in the toolbox (Invoke-AdfsSignInCheck.ps1) as a person, and returns its result.

    .PARAMETER Check
        Portal or ConnectJim.

    .PARAMETER JimUrl
        JIM's address as the toolbox reaches it (Get-ActiveDirectoryLabJimUrl).

    .PARAMETER UserName
        The person's sign-in name (their user principal name).

    .PARAMETER Password
        Their password. It reaches the container through the docker exec's environment only.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'A throwaway lab account''s password, handed to the container through its environment.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Portal', 'ConnectJim')][string]$Check,
        [Parameter(Mandatory)][string]$JimUrl,
        [Parameter(Mandatory)][string]$UserName,
        [Parameter(Mandatory)][string]$Password
    )

    $client = $script:AdfsSignInClientDirectory
    try {
        $env:JIM_SIGN_IN_USERNAME = $UserName
        $env:JIM_SIGN_IN_PASSWORD = $Password
        $output = Invoke-AdfsSignInDocker -Arguments @('exec', '-e', 'JIM_SIGN_IN_USERNAME', '-e', 'JIM_SIGN_IN_PASSWORD', $script:AdfsSignInToolbox,
            'pwsh', '-NoProfile', '-NonInteractive', '-File', "$client/Invoke-AdfsSignInCheck.ps1", '-Check', $Check, '-JimUrl', $JimUrl,
            '-ClientDirectory', $client, '-BinDirectory', $script:AdfsSignInBinDirectory)
    }
    finally {
        Remove-Item -Path Env:JIM_SIGN_IN_USERNAME, Env:JIM_SIGN_IN_PASSWORD -ErrorAction SilentlyContinue
    }
    return (ConvertFrom-AdfsSignInCheckOutput -Line $output)
}
