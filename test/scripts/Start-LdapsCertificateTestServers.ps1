# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Stands up the LDAPS directory servers needed by the RequiresLdaps test category.

.DESCRIPTION
    LDAPS certificate validation is performed by the platform LDAP client, not by JIM, so it can only be verified
    against a real directory server presenting a real certificate. This script creates the certificates and starts
    three OpenLDAP containers with TLS enabled:

      * A server whose issuing CA is added to this machine's trust store, so it validates without JIM's help. Used to
        prove that adding certificates to the JIM certificate store never weakens what already worked.
      * A server whose issuing CA is trusted by nobody, standing in for a customer's internal PKI. Used to prove the
        JIM certificate store is honoured, and that an unknown issuer is rejected without it.
      * A server presenting an expired certificate issued by that same CA. Used to prove trusting an issuer does not
        amount to waiving the validity period.

    It then prints the environment variables that LdapsCertificateValidationTests reads.

    Requires Docker, OpenSSL, and root (it writes hosts entries and adds a CA to the machine trust store).

.PARAMETER Stop
    Removes the containers, hosts entries and trusted CA, and deletes the working directory.

.PARAMETER WorkingDirectory
    Where certificates are generated. Defaults to a jim-ldaps-test directory under the system temporary path.

.EXAMPLE
    sudo pwsh ./test/scripts/Start-LdapsCertificateTestServers.ps1

.EXAMPLE
    sudo pwsh ./test/scripts/Start-LdapsCertificateTestServers.ps1 -Stop
#>
[CmdletBinding()]
param(
    [switch]$Stop,
    [string]$WorkingDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'jim-ldaps-test')
)

$ErrorActionPreference = 'Stop'

# Matches the image used by the integration test OpenLDAP container (test/integration/docker/openldap).
$image = 'bitnamilegacy/openldap:latest'

$servers = @(
    @{ Name = 'jim-ldaps-system-trusted'; Port = 3636; Hostname = 'ldap-sys.local'; Ca = 'caA' }
    @{ Name = 'jim-ldaps-jim-store';      Port = 4636; Hostname = 'ldap-jim.local'; Ca = 'caB' }
    @{ Name = 'jim-ldaps-expired';        Port = 5636; Hostname = 'ldap-old.local'; Ca = 'caB' }
)

$systemTrustPath = '/usr/local/share/ca-certificates/jim-ldaps-test-ca-a.crt'
$hostsFile = '/etc/hosts'
$bindDn = 'cn=admin,dc=example,dc=org'
$bindPassword = 'adminpassword'

function Assert-Prerequisites {
    foreach ($tool in @('docker', 'openssl')) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "$tool is required but was not found on PATH."
        }
    }

    if ($IsWindows) {
        throw 'These servers exercise the Linux platform LDAP client that JIM containers use; run this on Linux.'
    }
}

function Remove-TestServers {
    foreach ($server in $servers) {
        docker rm -f $server.Name 2>$null | Out-Null
    }

    if (Test-Path $systemTrustPath) {
        Remove-Item $systemTrustPath -Force
        & update-ca-certificates --fresh 2>$null | Out-Null
        Write-Host 'Removed the test CA from the machine trust store.'
    }

    $hostnames = $servers.Hostname
    $retainedLines = Get-Content $hostsFile | Where-Object {
        $line = $_
        -not ($hostnames | Where-Object { $line -match [regex]::Escape($_) })
    }
    Set-Content -Path $hostsFile -Value $retainedLines

    if (Test-Path $WorkingDirectory) {
        Remove-Item $WorkingDirectory -Recurse -Force
    }

    Write-Host 'LDAPS test servers stopped and cleaned up.'
}

function Invoke-OpenSsl {
    param([string[]]$Arguments)

    $output = & openssl @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "openssl $($Arguments -join ' ') failed: $output"
    }
}

function New-Certificates {
    New-Item -ItemType Directory -Path $WorkingDirectory -Force | Out-Null
    Push-Location $WorkingDirectory
    try {
        foreach ($ca in @('caA', 'caB')) {
            Invoke-OpenSsl @('req', '-x509', '-newkey', 'rsa:2048', '-keyout', "$ca.key", '-out', "$ca.crt", '-days', '5', '-nodes', '-subj', "/CN=JIM LDAPS Test CA $($ca.Substring(2))")
        }

        # Valid certificates, one per issuing CA.
        foreach ($pair in @(@{ Name = 'sys'; Ca = 'caA'; Hostname = 'ldap-sys.local' }, @{ Name = 'jim'; Ca = 'caB'; Hostname = 'ldap-jim.local' })) {
            Set-Content -Path "$($pair.Name).ext" -Value @(
                "subjectAltName=DNS:$($pair.Hostname)"
                'extendedKeyUsage=serverAuth'
            )
            Invoke-OpenSsl @('req', '-newkey', 'rsa:2048', '-keyout', "$($pair.Name).key", '-out', "$($pair.Name).csr", '-nodes', '-subj', "/CN=$($pair.Hostname)")
            Invoke-OpenSsl @('x509', '-req', '-in', "$($pair.Name).csr", '-CA', "$($pair.Ca).crt", '-CAkey', "$($pair.Ca).key", '-CAcreateserial', '-out', "$($pair.Name).crt", '-days', '5', '-extfile', "$($pair.Name).ext")
        }

        # An expired certificate needs explicit dates, which only the openssl ca command accepts.
        New-Item -ItemType Directory -Path (Join-Path $WorkingDirectory 'ca/newcerts') -Force | Out-Null
        # The index must be genuinely empty; a file holding just a newline fails to parse ("Problem with index file").
        [System.IO.File]::WriteAllText((Join-Path $WorkingDirectory 'ca/index.txt'), '')
        Set-Content -Path 'ca/serial' -Value '1000'
        Set-Content -Path 'expired-ca.cnf' -Value @(
            '[ ca ]'
            'default_ca = CA_default'
            '[ CA_default ]'
            'dir = ./ca'
            'database = $dir/index.txt'
            'new_certs_dir = $dir/newcerts'
            'serial = $dir/serial'
            'certificate = ./caB.crt'
            'private_key = ./caB.key'
            'default_md = sha256'
            'policy = policy_any'
            'copy_extensions = copy'
            'unique_subject = no'
            '[ policy_any ]'
            'commonName = supplied'
            '[ v3_srv ]'
            'subjectAltName = DNS:ldap-old.local'
            'extendedKeyUsage = serverAuth'
        )
        Invoke-OpenSsl @('req', '-newkey', 'rsa:2048', '-keyout', 'old.key', '-out', 'old.csr', '-nodes', '-subj', '/CN=ldap-old.local')
        Invoke-OpenSsl @('ca', '-config', 'expired-ca.cnf', '-batch', '-in', 'old.csr', '-out', 'old.crt', '-startdate', '20250101000000Z', '-enddate', '20250201000000Z', '-extensions', 'v3_srv', '-notext')

        # The containers read these as a non-root user.
        Get-ChildItem -Path $WorkingDirectory -Filter '*.crt' | ForEach-Object { & chmod 644 $_.FullName }
        Get-ChildItem -Path $WorkingDirectory -Filter '*.key' | ForEach-Object { & chmod 644 $_.FullName }
    }
    finally {
        Pop-Location
    }
}

function Start-TestServers {
    $certificateNames = @{
        'jim-ldaps-system-trusted' = 'sys'
        'jim-ldaps-jim-store'      = 'jim'
        'jim-ldaps-expired'        = 'old'
    }

    foreach ($server in $servers) {
        $certificateName = $certificateNames[$server.Name]
        $serverDirectory = Join-Path $WorkingDirectory $server.Name
        New-Item -ItemType Directory -Path $serverDirectory -Force | Out-Null
        foreach ($file in @("$certificateName.crt", "$certificateName.key", "$($server.Ca).crt")) {
            Copy-Item (Join-Path $WorkingDirectory $file) $serverDirectory -Force
        }

        docker rm -f $server.Name 2>$null | Out-Null
        docker run -d --name $server.Name `
            -p "$($server.Port):1636" `
            -e LDAP_ADMIN_USERNAME=admin `
            -e LDAP_ADMIN_PASSWORD=$bindPassword `
            -e LDAP_ROOT=dc=example,dc=org `
            -e LDAP_ENABLE_TLS=yes `
            -e LDAP_TLS_CERT_FILE=/certs/$certificateName.crt `
            -e LDAP_TLS_KEY_FILE=/certs/$certificateName.key `
            -e LDAP_TLS_CA_FILE=/certs/$($server.Ca).crt `
            -v "${serverDirectory}:/certs:ro" `
            $image | Out-Null

        Write-Host "Started $($server.Name) on port $($server.Port) as $($server.Hostname)."
    }
}

function Set-HostEntries {
    $hosts = Get-Content $hostsFile
    foreach ($server in $servers) {
        if (-not ($hosts | Where-Object { $_ -match [regex]::Escape($server.Hostname) })) {
            Add-Content -Path $hostsFile -Value "127.0.0.1 $($server.Hostname)"
        }
    }
}

function Add-CaToMachineTrustStore {
    Copy-Item (Join-Path $WorkingDirectory 'caA.crt') $systemTrustPath -Force
    & update-ca-certificates 2>$null | Out-Null
    Write-Host 'Added the first test CA to the machine trust store so one server validates without JIM.'
}

Assert-Prerequisites

if ($Stop) {
    Remove-TestServers
    return
}

if (Test-Path $WorkingDirectory) {
    Remove-Item $WorkingDirectory -Recurse -Force
}

New-Certificates
Start-TestServers
Set-HostEntries
Add-CaToMachineTrustStore

Write-Host ''
Write-Host 'Waiting for the directory servers to accept connections...'
Start-Sleep -Seconds 16

Write-Host ''
Write-Host 'Set these, then run: dotnet test test/JIM.Worker.Tests/ --filter "Category=RequiresLdaps"'
Write-Host ''
$environmentVariables = [ordered]@{
    JIM_TEST_LDAPS_HOST                 = 'ldap-jim.local'
    JIM_TEST_LDAPS_PORT                 = '4636'
    JIM_TEST_LDAPS_USERNAME             = $bindDn
    JIM_TEST_LDAPS_PASSWORD             = $bindPassword
    JIM_TEST_LDAPS_CA_PATH              = (Join-Path $WorkingDirectory 'caB.crt')
    JIM_TEST_LDAPS_MISMATCH_HOST        = '127.0.0.1'
    JIM_TEST_LDAPS_EXPIRED_HOST         = 'ldap-old.local'
    JIM_TEST_LDAPS_EXPIRED_PORT         = '5636'
    JIM_TEST_LDAPS_SYSTEM_TRUSTED_HOST  = 'ldap-sys.local'
    JIM_TEST_LDAPS_SYSTEM_TRUSTED_PORT  = '3636'
}

foreach ($variable in $environmentVariables.GetEnumerator()) {
    Write-Host "export $($variable.Key)=$($variable.Value)"
}
