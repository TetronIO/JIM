# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Makes the LDAP toolbox container ready to sign in to JIM through AD FS (#2441), as an administrator's workstation
    is: it trusts the AD FS server's and jim.web's certificates, and has a "browser" for Connect-JIM to open.

.DESCRIPTION
    Runs inside the toolbox (docker exec), as its unprivileged user, from the folder the scenario copied the sign-in
    client into. It:

      1. adds each certificate to the user's trusted root store, which .NET on Linux trusts for every HTTPS connection
         it makes: Invoke-WebRequest and Connect-JIM alike;
      2. writes an xdg-open into <BinDirectory>, which Connect-JIM finds first on PATH (Invoke-AdfsSignInCheck.ps1 puts
         it there) and runs with the AD FS authorisation URL, as it would run the desktop's browser. It hands the URL to
         Complete-AdfsBrowserSignIn.ps1, which signs in with forms and delivers the code to the module's loopback
         listener, and logs to <BinDirectory>/browser.log.

    Idempotent: a certificate already trusted is left alone, and the opener is rewritten.

.PARAMETER ClientDirectory
    Where the scenario copied the sign-in client (this script, its module and the JIM PowerShell module).

.PARAMETER CertificatePath
    The PEM certificates to trust: the AD FS server's and jim.web's.

.PARAMETER BinDirectory
    Where to write the opener. A folder the container's user can write.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'One progress line per action, for the scenario log.')]
param(
    [Parameter(Mandatory)][string]$ClientDirectory,
    [Parameter(Mandatory)][string[]]$CertificatePath,
    [Parameter(Mandatory)][string]$BinDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# pwsh -File passes "a.pem,b.pem" as one string.
$CertificatePath = @($CertificatePath | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$store = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
$store.Open('ReadWrite')
try {
    foreach ($path in $CertificatePath) {
        # The constructor reads a certificate-only PEM; CreateFromPemFile expects the private key in the file too.
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($path)
        if ($store.Certificates.Find('FindByThumbprint', $certificate.Thumbprint, $false).Count -eq 0) {
            $store.Add($certificate)
            Write-Host "  Trusted $($certificate.Subject) ($($certificate.Thumbprint))"
        }
        else {
            Write-Host "  Already trusted: $($certificate.Subject)"
        }
    }
}
finally {
    $store.Close()
}

$null = New-Item -ItemType Directory -Path $BinDirectory -Force
$opener = Join-Path $BinDirectory 'xdg-open'
$browserScript = Join-Path $ClientDirectory 'Complete-AdfsBrowserSignIn.ps1'
$log = Join-Path $BinDirectory 'browser.log'
[System.IO.File]::WriteAllText($opener, "#!/bin/sh`nexec pwsh -NoProfile -NonInteractive -File '$browserScript' `"`$1`" >>'$log' 2>&1`n")
& chmod +x $opener
if ($LASTEXITCODE -ne 0) { throw "Could not make $opener executable." }
Write-Host "  Browser for Connect-JIM: $opener"
