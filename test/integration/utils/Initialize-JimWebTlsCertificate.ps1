# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Mints the self-signed certificate jim.web serves HTTPS with in the integration harness.

.DESCRIPTION
    test/integration/docker/jim-web-https.override.yml has jim.web serve HTTPS on 8443 on the Docker network,
    beside its usual HTTP on 8080, so that the SCIM Client Connector, which refuses plain HTTP to anything but
    a loopback address, can call JIM's own SCIM endpoint (Scenario 029). Kestrel reads the pair this writes;
    the scenario trusts the certificate in JIM's certificate store, so validation stays on.

    The certificate names jim.web, the service name every lane's worker reaches its own web container by, and
    localhost. A certificate still valid for a day is kept rather than replaced: the runner mints it once,
    before any -Parallel lane starts, and the lanes then find it and leave it alone, so no lane ever sees the
    file it is serving change underneath it.

    Self-signed is enough: the certificate exists only for the length of a run, inside a test network, and it
    is trusted explicitly by thumbprint rather than through any authority.

.PARAMETER Directory
    Where to write tls.crt and tls.key. Created if missing.

.PARAMETER ValidityDays
    How long a newly minted certificate is valid for. The default covers any run; tests pass 0 to mint one
    that is already due for replacement.
#>
function Initialize-JimWebTlsCertificate {
    [CmdletBinding()]
    [OutputType([void])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory,

        [Parameter(Mandatory = $false)]
        [ValidateRange(0, 3650)]
        [int]$ValidityDays = 30
    )

    $certificatePath = Join-Path $Directory 'tls.crt'
    $keyPath = Join-Path $Directory 'tls.key'

    if ((Test-Path $certificatePath) -and (Test-Path $keyPath)) {
        $existing = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
        try {
            if ($existing.NotAfter.ToUniversalTime() -gt [DateTime]::UtcNow.AddDays(1)) {
                return
            }
        }
        finally {
            $existing.Dispose()
        }
    }

    New-Item -ItemType Directory -Path $Directory -Force | Out-Null

    $key = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=jim.web', $key,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

        $names = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $names.AddDnsName('jim.web')
        $names.AddDnsName('localhost')
        $request.CertificateExtensions.Add($names.Build())
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $usage = [System.Security.Cryptography.OidCollection]::new()
        $usage.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1')) | Out-Null
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usage, $false))

        $now = [DateTimeOffset]::UtcNow
        $certificate = $request.CreateSelfSigned($now.AddMinutes(-5), $now.AddDays($ValidityDays))
        try {
            Set-Content -Path $certificatePath -Value $certificate.ExportCertificatePem() -NoNewline
            Set-Content -Path $keyPath -Value $key.ExportPkcs8PrivateKeyPem() -NoNewline
        }
        finally {
            $certificate.Dispose()
        }
    }
    finally {
        $key.Dispose()
    }

    # jim.web runs as UID 1654, which owns neither file on a Linux host: a bind mount keeps the host owner. The
    # key protects nothing outside a disposable test network, so it is made world-readable.
    if (-not $IsWindows) {
        $readable = [System.IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead'
        [System.IO.File]::SetUnixFileMode($certificatePath, $readable)
        [System.IO.File]::SetUnixFileMode($keyPath, $readable)
        [System.IO.File]::SetUnixFileMode($Directory,
            [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute')
    }
}
