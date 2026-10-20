# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Initialize-JimWebTlsCertificate.ps1.

.DESCRIPTION
    The harness serves jim.web over HTTPS on the Docker network so the SCIM Client Connector, which refuses
    plain HTTP to anything but a loopback address, can call JIM's own SCIM endpoint (Scenario 029). The
    certificate must name the address the worker calls (jim.web), load as a PEM pair Kestrel accepts, and be
    minted once per run rather than per lane, since the lanes share it.
#>

BeforeAll {
    . "$PSScriptRoot/Initialize-JimWebTlsCertificate.ps1"
}

Describe 'Initialize-JimWebTlsCertificate' {
    BeforeEach {
        $script:Directory = Join-Path ([System.IO.Path]::GetTempPath()) "jim-web-tls-$([guid]::NewGuid())"
    }

    AfterEach {
        Remove-Item -Path $script:Directory -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'writes a PEM certificate and key that load as a pair' {
        Initialize-JimWebTlsCertificate -Directory $script:Directory

        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile(
            (Join-Path $script:Directory 'tls.crt'), (Join-Path $script:Directory 'tls.key'))
        $certificate.HasPrivateKey | Should -BeTrue
    }

    It 'names jim.web and localhost' {
        Initialize-JimWebTlsCertificate -Directory $script:Directory

        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $script:Directory 'tls.crt'))
        $certificate.MatchesHostname('jim.web') | Should -BeTrue
        $certificate.MatchesHostname('localhost') | Should -BeTrue
    }

    It 'keeps a certificate that is still valid, so lanes sharing it never see it change' {
        Initialize-JimWebTlsCertificate -Directory $script:Directory
        $first = Get-FileHash (Join-Path $script:Directory 'tls.crt')

        Initialize-JimWebTlsCertificate -Directory $script:Directory

        (Get-FileHash (Join-Path $script:Directory 'tls.crt')).Hash | Should -Be $first.Hash
    }

    It 'replaces a certificate that is about to expire' {
        Initialize-JimWebTlsCertificate -Directory $script:Directory -ValidityDays 0
        $first = Get-FileHash (Join-Path $script:Directory 'tls.crt')

        Initialize-JimWebTlsCertificate -Directory $script:Directory

        (Get-FileHash (Join-Path $script:Directory 'tls.crt')).Hash | Should -Not -Be $first.Hash
    }

    It 'leaves the key readable by the container user' -Skip:($IsWindows) {
        Initialize-JimWebTlsCertificate -Directory $script:Directory

        (Get-Item (Join-Path $script:Directory 'tls.key')).UnixFileMode.HasFlag([System.IO.UnixFileMode]::OtherRead) | Should -BeTrue
    }
}
