# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Stands up the Microsoft SQL Server needed by the RequiresSqlTls test category.

.DESCRIPTION
    The JIM SQL Connector's certificate trust can only be verified against a real SQL Server presenting a real
    certificate: the driver makes the trust decision, and SQL Server only offers its certificate inside a TDS
    PRELOGIN exchange, which no unit test reaches. This script generates a certificate authority trusted by nobody
    and a server certificate it issues for 'localhost', then starts one SQL Server container presenting it.

    One server covers every row: 'localhost' is the name the certificate carries, '127.0.0.1' reaches the same
    server by a name it does not, and encryption is left to the client (forceencryption = 0) so the same server
    also takes the unencrypted connection. Nothing is written outside the working directory and Docker, so unlike
    the LDAPS fixture this needs no root: no hosts entries, and no change to the machine trust store.

    Certificates are generated on every run and expire after five days, so nothing with a fixed expiry is ever
    committed. It then prints the environment variables that SqlServerTlsCertificateValidationTests reads.

    Requires Docker and OpenSSL.

.PARAMETER Stop
    Removes the container and deletes the working directory.

.PARAMETER WorkingDirectory
    Where certificates are generated. Defaults to a jim-sqltls-test directory under the system temporary path.

.EXAMPLE
    pwsh ./test/scripts/Start-SqlServerTlsTestServer.ps1

.EXAMPLE
    pwsh ./test/scripts/Start-SqlServerTlsTestServer.ps1 -Stop
#>
[CmdletBinding()]
param(
    [switch]$Stop,
    [string]$WorkingDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'jim-sqltls-test')
)

$ErrorActionPreference = 'Stop'

# Matches the image the integration lab's SQL Server container uses (test/integration/docker).
$image = 'mcr.microsoft.com/mssql/server:2022-latest'
$containerName = 'jim-sqltls'
$hostname = 'localhost'
$mismatchedHostname = '127.0.0.1'
$readyTimeoutSeconds = 180

function Assert-Prerequisites {
    foreach ($tool in @('docker', 'openssl')) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "$tool is required but was not found on PATH."
        }
    }
}

function Remove-TestServer {
    docker rm -f $containerName 2>$null | Out-Null

    if (Test-Path $WorkingDirectory) {
        Remove-Item $WorkingDirectory -Recurse -Force
    }

    Write-Host 'SQL Server TLS test server stopped and cleaned up.'
}

function Invoke-OpenSsl {
    param([string[]]$Arguments)

    $output = & openssl @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "openssl $($Arguments -join ' ') failed: $output"
    }
}

function New-SaPassword {
    <#
    .SYNOPSIS
        A fresh password per run, meeting SQL Server's complexity policy (upper, lower, digit and symbol).
    #>
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    return 'Jim!' + [System.Convert]::ToHexString($bytes).ToLowerInvariant()
}

function New-Certificates {
    New-Item -ItemType Directory -Path (Join-Path $WorkingDirectory 'server') -Force | Out-Null
    Push-Location $WorkingDirectory
    try {
        Invoke-OpenSsl @('req', '-x509', '-newkey', 'rsa:2048', '-keyout', 'ca.key', '-out', 'ca.crt', '-days', '5', '-nodes', '-subj', '/CN=JIM SQL TLS Test CA')

        Set-Content -Path 'server.ext' -Value @(
            "subjectAltName=DNS:$hostname"
            'extendedKeyUsage=serverAuth'
        )
        Invoke-OpenSsl @('req', '-newkey', 'rsa:2048', '-keyout', 'server/server.key', '-out', 'server.csr', '-nodes', '-subj', "/CN=$hostname")
        Invoke-OpenSsl @('x509', '-req', '-in', 'server.csr', '-CA', 'ca.crt', '-CAkey', 'ca.key', '-CAcreateserial', '-out', 'server/server.crt', '-days', '5', '-extfile', 'server.ext')

        # forceencryption = 0 leaves the choice to the client, which is what lets one server take both the
        # encrypted and the unencrypted rows. TLS 1.2 is the floor JIM's security baseline sets.
        Set-Content -Path 'server/mssql.conf' -Value @(
            '[network]'
            'tlscert = /certs/server.crt'
            'tlskey = /certs/server.key'
            'tlsprotocols = 1.2'
            'forceencryption = 0'
        )

        # The container runs as the unprivileged mssql user, which has to be able to read all three.
        Get-ChildItem -Path (Join-Path $WorkingDirectory 'server') | ForEach-Object { & chmod 644 $_.FullName }
    }
    finally {
        Pop-Location
    }
}

function Get-PublishedHostPort {
    $mapping = docker port $containerName '1433/tcp' 2>$null | Select-Object -First 1
    if (-not $mapping -or $mapping -notmatch ':(\d+)\s*$') {
        throw "Could not determine the published host port for ${containerName}:1433. Is the container running?"
    }

    return [int]$Matches[1]
}

function Wait-ForSqlServer {
    param([string]$SaPassword)

    $deadline = (Get-Date).AddSeconds($readyTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        # -C trusts the server certificate for this readiness probe only; it runs inside the container and has
        # nothing to do with what the tests validate.
        docker exec $containerName /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $SaPassword -Q 'SELECT 1' 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            return
        }

        Start-Sleep -Seconds 3
    }

    throw "SQL Server ($containerName) did not accept connections within $readyTimeoutSeconds seconds. Check 'docker logs $containerName'."
}

function Assert-CertificateLoaded {
    # SQL Server falls back to a self-signed certificate of its own when it cannot load the configured one, and
    # every test would then fail on a name mismatch that has nothing to do with JIM. Say so plainly instead.
    $logs = docker logs $containerName 2>&1 | Out-String
    if ($logs -notmatch "Certificate File:'/certs/server.crt'.*successfully loaded for encryption") {
        throw "SQL Server ($containerName) did not load the generated certificate. Check 'docker logs $containerName'."
    }
}

Assert-Prerequisites

if ($Stop) {
    Remove-TestServer
    return
}

docker rm -f $containerName 2>$null | Out-Null
New-Certificates
$saPassword = New-SaPassword

# Disposable and loopback-only, but still a credential: keep it out of the Actions log.
if ($env:GITHUB_ACTIONS) {
    Write-Host "::add-mask::$saPassword"
}

# '127.0.0.1::1433' has Docker choose a free host port, bound to loopback only. A fixed port collides on the
# self-hosted runner host, where a second runner service can be running another job's containers at the same time.
docker run -d --name $containerName -p '127.0.0.1::1433' `
    -e ACCEPT_EULA=Y `
    -e "MSSQL_SA_PASSWORD=$saPassword" `
    -v "$(Join-Path $WorkingDirectory 'server'):/certs:ro" `
    -v "$(Join-Path $WorkingDirectory 'server/mssql.conf'):/var/opt/mssql/mssql.conf:ro" `
    $image | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "docker run failed for $containerName."
}

Write-Host "Waiting for SQL Server ($containerName) to accept connections..."
Wait-ForSqlServer -SaPassword $saPassword
Assert-CertificateLoaded
$port = Get-PublishedHostPort
Write-Host "Started $containerName on port $port, presenting a certificate for '$hostname'."
Write-Host ''

$environmentVariables = [ordered]@{
    JIM_TEST_SQLTLS_HOST                    = $hostname
    JIM_TEST_SQLTLS_PORT                    = "$port"
    JIM_TEST_SQLTLS_USERNAME                = 'sa'
    JIM_TEST_SQLTLS_PASSWORD                = $saPassword
    JIM_TEST_SQLTLS_CA_PATH                 = (Join-Path $WorkingDirectory 'ca.crt')
    JIM_TEST_SQLTLS_SERVER_CERTIFICATE_PATH = (Join-Path $WorkingDirectory 'server/server.crt')
    JIM_TEST_SQLTLS_MISMATCH_HOST           = $mismatchedHostname
}

foreach ($variable in $environmentVariables.GetEnumerator()) {
    Write-Host "export $($variable.Key)=$($variable.Value)"
}

# Lets a CI workflow step consume these without a human copying and pasting the printout above.
if ($env:GITHUB_ENV) {
    foreach ($variable in $environmentVariables.GetEnumerator()) {
        Add-Content -Path $env:GITHUB_ENV -Value "$($variable.Key)=$($variable.Value)"
    }
    Write-Host ''
    Write-Host "Also appended to `$env:GITHUB_ENV ($($env:GITHUB_ENV))."
}
