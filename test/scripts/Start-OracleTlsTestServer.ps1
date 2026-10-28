# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Stands up the Oracle Database needed by the RequiresOracleTls test category.

.DESCRIPTION
    The JIM SQL Connector's Oracle TCPS certificate trust can only be verified against a real Oracle listener
    presenting a real certificate: ODP.NET makes the trust decision itself, from an Oracle wallet. This script
    generates a root certificate authority trusted by nobody, an intermediate authority under it, and a server
    certificate the intermediate issues for 'localhost'. It then starts one Oracle Database Free container and adds
    a TCPS listener presenting that certificate, from a server wallet built with the image's own orapki.

    One server covers every row: 'localhost' is the name the certificate carries, '127.0.0.1' reaches the same
    server by a name it does not, and the chain has a root, an intermediate and the server's own certificate, so
    the tests can trust each of them in turn. Nothing is written outside the working directory and Docker, so it
    needs no root: no hosts entries, and no change to the machine trust store.

    Certificates and passwords are generated on every run, and the certificates expire after five days, so nothing
    with a fixed expiry is ever committed. It then prints the environment variables that
    OracleTlsCertificateValidationTests reads.

    Requires Docker and OpenSSL. The Oracle image is large (13.6GB unpacked) and its first start creates the
    database, which takes minutes rather than seconds.

.PARAMETER Stop
    Removes the container and deletes the working directory.

.PARAMETER WorkingDirectory
    Where certificates are generated. Defaults to a jim-oracletls-test directory under the system temporary path.

.EXAMPLE
    pwsh ./test/scripts/Start-OracleTlsTestServer.ps1

.EXAMPLE
    pwsh ./test/scripts/Start-OracleTlsTestServer.ps1 -Stop
#>

[CmdletBinding()]
param(
    [switch]$Stop,

    [string]$WorkingDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'jim-oracletls-test')
)

$ErrorActionPreference = 'Stop'

# Matches the image the integration lab's Oracle container uses (test/integration/docker).
$image = 'container-registry.oracle.com/database/free:23.9.0.0'
$containerName = 'jim-oracletls'
$hostname = 'localhost'
$mismatchedHostname = '127.0.0.1'
$serviceName = 'FREEPDB1'
$username = 'JIMTLS'
$readyTimeoutSeconds = 900

# Inside the container. The listener and Oracle Net files are symbolic links into the database volume, which is
# the container's own here, so nothing outlives the container.
$walletDirectory = '/opt/oracle/jim-tls-wallet'
$networkAdminDirectory = '/opt/oracle/oradata/dbconfig/FREE'

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
    Write-Host 'Oracle TLS test server stopped and cleaned up.'
}

function Invoke-OpenSsl {
    param([string[]]$Arguments)

    $output = & openssl @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "openssl $($Arguments -join ' ') failed: $output"
    }
}

function Invoke-InContainer {
    <#
    .SYNOPSIS
        Runs a shell script in the container as the oracle user, failing loudly on any error.
    #>
    param([string]$Script)

    # .gitattributes checks *.ps1 out with CRLF, so every line of a here-string ends in a CR, which bash reads as
    # part of the line ("/dev/null\r: Permission denied"). Strip it rather than rely on how this file was checked out.
    $output = docker exec $containerName bash -c ("set -e`n$Script" -replace "`r", '') 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Command in $containerName failed: $($output | Out-String)"
    }
    return $output
}

function New-Password {
    <#
    .SYNOPSIS
        A fresh password per run. Oracle wants it to start with a letter; the rest is hex.
    #>
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    return 'Jim' + [System.Convert]::ToHexString($bytes).ToLowerInvariant()
}

function New-Certificates {
    param([string]$WalletPassword)

    New-Item -ItemType Directory -Path $WorkingDirectory -Force | Out-Null
    Push-Location $WorkingDirectory
    try {
        Set-Content -Path 'ca.ext' -Value @(
            'basicConstraints=critical,CA:TRUE'
            'keyUsage=critical,keyCertSign,cRLSign'
        )
        Invoke-OpenSsl @('req', '-x509', '-newkey', 'rsa:2048', '-keyout', 'root.key', '-out', 'root.crt', '-days', '5', '-nodes',
            '-subj', '/CN=JIM Oracle TLS Test Root CA', '-addext', 'basicConstraints=critical,CA:TRUE', '-addext', 'keyUsage=critical,keyCertSign,cRLSign')

        Invoke-OpenSsl @('req', '-newkey', 'rsa:2048', '-keyout', 'intermediate.key', '-out', 'intermediate.csr', '-nodes', '-subj', '/CN=JIM Oracle TLS Test Intermediate CA')
        Invoke-OpenSsl @('x509', '-req', '-in', 'intermediate.csr', '-CA', 'root.crt', '-CAkey', 'root.key', '-CAcreateserial', '-out', 'intermediate.crt', '-days', '5', '-extfile', 'ca.ext')

        Set-Content -Path 'server.ext' -Value @(
            "subjectAltName=DNS:$hostname"
            'extendedKeyUsage=serverAuth'
            'keyUsage=critical,digitalSignature,keyEncipherment'
        )
        Invoke-OpenSsl @('req', '-newkey', 'rsa:2048', '-keyout', 'server.key', '-out', 'server.csr', '-nodes', '-subj', "/CN=$hostname")
        Invoke-OpenSsl @('x509', '-req', '-in', 'server.csr', '-CA', 'intermediate.crt', '-CAkey', 'intermediate.key', '-CAcreateserial', '-out', 'server.crt', '-days', '5', '-extfile', 'server.ext')

        # The server wallet takes the key, its certificate and the chain above it in one PKCS#12 import.
        Set-Content -Path 'chain.crt' -Value ((Get-Content 'intermediate.crt' -Raw) + (Get-Content 'root.crt' -Raw)) -NoNewline
        Invoke-OpenSsl @('pkcs12', '-export', '-inkey', 'server.key', '-in', 'server.crt', '-certfile', 'chain.crt', '-out', 'server.p12', '-passout', "pass:$WalletPassword")
    }
    finally {
        Pop-Location
    }
}

function Wait-ForDatabase {
    $deadline = (Get-Date).AddSeconds($readyTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        # The image's own status script, which fails until the pluggable database is open READ WRITE.
        docker exec $containerName /opt/oracle/checkDBStatus.sh 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            return
        }

        Start-Sleep -Seconds 10
    }

    throw "Oracle Database ($containerName) was not ready within $readyTimeoutSeconds seconds. Check 'docker logs $containerName'."
}

function Enable-Tcps {
    param([string]$WalletPassword, [string]$UserPassword)

    docker cp (Join-Path $WorkingDirectory 'server.p12') "${containerName}:/tmp/server.p12" | Out-Null
    docker exec -u root $containerName chown oracle /tmp/server.p12 | Out-Null

    $wallet = "(SOURCE = (METHOD = FILE)(METHOD_DATA = (DIRECTORY = $walletDirectory)))"
    Invoke-InContainer @"
rm -rf $walletDirectory && mkdir -p $walletDirectory
orapki wallet create -wallet $walletDirectory -pwd '$WalletPassword' -auto_login > /dev/null
orapki wallet import_pkcs12 -wallet $walletDirectory -pwd '$WalletPassword' -pkcs12file /tmp/server.p12 -pkcs12pwd '$WalletPassword' > /dev/null
rm -f /tmp/server.p12
cd $networkAdminDirectory
cat > listener.ora <<'EOF'
DEFAULT_SERVICE_LISTENER = FREE
LISTENER =
  (DESCRIPTION_LIST =
    (DESCRIPTION =
      (ADDRESS = (PROTOCOL = TCP)(HOST = 0.0.0.0)(PORT = 1521))
      (ADDRESS = (PROTOCOL = TCPS)(HOST = 0.0.0.0)(PORT = 2484))
      (ADDRESS = (PROTOCOL = IPC)(KEY = EXTPROC1521))
    )
  )
WALLET_LOCATION = $wallet
SSL_CLIENT_AUTHENTICATION = FALSE
EOF
cat >> sqlnet.ora <<'EOF'
WALLET_LOCATION = $wallet
SSL_CLIENT_AUTHENTICATION = FALSE
EOF
lsnrctl stop > /dev/null 2>&1 || true
lsnrctl start > /dev/null
sqlplus -s / as sysdba <<'EOF'
WHENEVER SQLERROR EXIT FAILURE
ALTER SYSTEM REGISTER;
ALTER SESSION SET CONTAINER = $serviceName;
CREATE USER $username IDENTIFIED BY "$UserPassword";
GRANT CREATE SESSION TO $username;
EXIT
EOF
"@ | Out-Null

    # Service registration with the restarted listener is asynchronous; wait until it serves the PDB over TCPS.
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        $status = docker exec $containerName lsnrctl status 2>&1 | Out-String
        if ($status -match '\(PROTOCOL=tcps\)' -and $status -match ('Service "' + $serviceName.ToLowerInvariant() + '"')) {
            return
        }
        Start-Sleep -Seconds 3
    }

    throw "The TCPS listener in $containerName did not come up serving $serviceName. Check 'docker exec $containerName lsnrctl status'."
}

function Get-PublishedHostPort {
    $mapping = docker port $containerName '2484/tcp' 2>$null | Select-Object -First 1
    if (-not $mapping -or $mapping -notmatch ':(\d+)\s*$') {
        throw "Could not determine the published host port for ${containerName}:2484. Is the container running?"
    }

    return [int]$Matches[1]
}

Assert-Prerequisites

if ($Stop) {
    Remove-TestServer
    return
}

docker rm -f $containerName 2>$null | Out-Null
$walletPassword = New-Password
$userPassword = New-Password
$sysPassword = New-Password

# Disposable and loopback-only, but still credentials: keep them out of the Actions log.
if ($env:GITHUB_ACTIONS) {
    foreach ($secret in @($walletPassword, $userPassword, $sysPassword)) {
        Write-Host "::add-mask::$secret"
    }
}

New-Certificates -WalletPassword $walletPassword

# '127.0.0.1::2484' has Docker choose a free host port, bound to loopback only, as the SQL Server lane does.
# Oracle keeps its SGA in System V shared memory, and Docker's 64MB default /dev/shm is far too small for it.
docker run -d --name $containerName -p '127.0.0.1::2484' --shm-size 2g `
    -e "ORACLE_PWD=$sysPassword" `
    -e "ORACLE_PDB=$serviceName" `
    $image | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "docker run failed for $containerName."
}

Write-Host "Waiting for Oracle Database ($containerName) to open its pluggable database..."
Wait-ForDatabase
Write-Host 'Adding the TCPS listener...'
Enable-Tcps -WalletPassword $walletPassword -UserPassword $userPassword
$port = Get-PublishedHostPort
Write-Host "Started $containerName with TCPS on port $port, presenting a certificate for '$hostname'."
Write-Host ''

$environmentVariables = [ordered]@{
    JIM_TEST_ORACLETLS_HOST                    = $hostname
    JIM_TEST_ORACLETLS_PORT                    = "$port"
    JIM_TEST_ORACLETLS_SERVICE_NAME            = $serviceName
    JIM_TEST_ORACLETLS_USERNAME                = $username
    JIM_TEST_ORACLETLS_PASSWORD                = $userPassword
    JIM_TEST_ORACLETLS_ROOT_CA_PATH            = (Join-Path $WorkingDirectory 'root.crt')
    JIM_TEST_ORACLETLS_INTERMEDIATE_CA_PATH    = (Join-Path $WorkingDirectory 'intermediate.crt')
    JIM_TEST_ORACLETLS_SERVER_CERTIFICATE_PATH = (Join-Path $WorkingDirectory 'server.crt')
    JIM_TEST_ORACLETLS_MISMATCH_HOST           = $mismatchedHostname
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
