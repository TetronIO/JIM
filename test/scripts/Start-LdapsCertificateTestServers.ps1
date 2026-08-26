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
        prove that adding certificates to the JIM certificate store never weakens what already worked. This server
        also publishes its unencrypted LDAP port, for the unencrypted-connection tests.
      * A server whose issuing CA is trusted by nobody, standing in for a customer's internal PKI. Used to prove the
        JIM certificate store is honoured, and that an unknown issuer is rejected without it.
      * A server presenting an expired certificate issued by that same CA. Used to prove trusting an issuer does not
        amount to waiving the validity period.

    It then prints the environment variables that LdapsCertificateValidationTests, ServerCertificateProbeTests and
    the Samba AD / unencrypted-connection fixture read.

    Requires Docker, OpenSSL, and root (it writes hosts entries and adds a CA to the machine trust store).

.PARAMETER Stop
    Removes the containers, hosts entries and trusted CA, and deletes the working directory. Always cleans up the
    Samba AD container and its hosts entry too, whether or not -IncludeSambaAd was passed on the run being stopped,
    so a stale Samba AD container from an earlier run is never left behind.

.PARAMETER IncludeSambaAd
    Also stands up a Samba AD domain controller, covering the AD-family directory type alongside OpenLDAP. First
    boot provisions a domain from scratch, which takes several minutes; the script waits and prints progress.

.PARAMETER WorkingDirectory
    Where certificates are generated. Defaults to a jim-ldaps-test directory under the system temporary path.

.EXAMPLE
    sudo pwsh ./test/scripts/Start-LdapsCertificateTestServers.ps1

.EXAMPLE
    sudo pwsh ./test/scripts/Start-LdapsCertificateTestServers.ps1 -IncludeSambaAd

.EXAMPLE
    sudo pwsh ./test/scripts/Start-LdapsCertificateTestServers.ps1 -Stop
#>
[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$IncludeSambaAd,
    [string]$WorkingDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'jim-ldaps-test')
)

$ErrorActionPreference = 'Stop'

# Matches the image used by the integration test OpenLDAP container (test/integration/docker/openldap).
$image = 'bitnamilegacy/openldap:latest'

# Host ports are not fixed: Docker publishes each container port to an ephemeral, loopback-bound
# host port (Port is filled in after the container starts). Fixed ports collide on the self-hosted
# runner host, where a second runner service can be running another job's containers at the same
# moment; the fixtures read the actual ports from the environment variables this script exports.
$servers = @(
    @{ Name = 'jim-ldaps-system-trusted'; Port = $null; Hostname = 'ldap-sys.local'; Ca = 'caA' }
    @{ Name = 'jim-ldaps-jim-store';      Port = $null; Hostname = 'ldap-jim.local'; Ca = 'caB' }
    @{ Name = 'jim-ldaps-expired';        Port = $null; Hostname = 'ldap-old.local'; Ca = 'caB' }
)

$systemTrustPath = '/usr/local/share/ca-certificates/jim-ldaps-test-ca-a.crt'
$hostsFile = '/etc/hosts'
$bindDn = 'cn=admin,dc=example,dc=org'
$bindPassword = 'adminpassword'

# The system-trusted OpenLDAP server also carries the unencrypted-connection coverage: bitnami's openldap image
# serves plain LDAP on container port 1389 alongside LDAPS on 1636, so no extra container is needed for it.
# Host port assigned by Docker after the container starts, like the LDAPS ports above.
$plainLdapHostPort = $null
$plainLdapContainerPort = 1389

# Samba AD, added by -IncludeSambaAd, covers the AD-family directory type alongside OpenLDAP.
$sambaImage = 'diegogslomp/samba-ad-dc:latest'
$sambaContainerName = 'jim-ldaps-samba-ad'
$sambaHostname = 'dc1.ldapstest.local'
$sambaShortName = 'dc1'
$sambaRealm = 'LDAPSTEST.LOCAL'
$sambaDomain = 'LDAPSTEST'
$sambaAdminPassword = 'Test@123!JIM'
$sambaAdminDn = 'CN=Administrator,CN=Users,DC=ldapstest,DC=local'
# Host ports assigned by Docker after the container starts; docker restart preserves the mapping.
$sambaLdapsPort = $null
$sambaLdapPort = $null
$sambaCaPath = Join-Path $WorkingDirectory 'samba-ca.pem'

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

    # Always removed, whether or not -IncludeSambaAd was passed on this run: a stale Samba AD container from an
    # earlier -IncludeSambaAd run must never survive a plain -Stop.
    docker rm -f $sambaContainerName 2>$null | Out-Null

    if (Test-Path $systemTrustPath) {
        Remove-Item $systemTrustPath -Force
        & update-ca-certificates --fresh 2>$null | Out-Null
        Write-Host 'Removed the test CA from the machine trust store.'
    }

    $hostnames = $servers.Hostname + $sambaHostname
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

function Get-PublishedHostPort {
    <#
    .SYNOPSIS
        Returns the ephemeral host port Docker published for a container port, from 'docker port'.
    #>
    param(
        [string]$ContainerName,
        [int]$ContainerPort
    )

    $mapping = docker port $ContainerName "$ContainerPort/tcp" 2>$null | Select-Object -First 1
    if (-not $mapping -or $mapping -notmatch ':(\d+)\s*$') {
        throw "Could not determine the published host port for ${ContainerName}:${ContainerPort}. Is the container running?"
    }

    return [int]$Matches[1]
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

        # Only the system-trusted server also publishes its unencrypted LDAP port, for the plain-connection tests;
        # the other two servers exist solely to exercise LDAPS certificate validation. '127.0.0.1::<port>' has
        # Docker choose a free host port, bound to loopback only (the test hostnames all resolve to 127.0.0.1).
        $portArguments = @('-p', '127.0.0.1::1636')
        if ($server.Name -eq 'jim-ldaps-system-trusted') {
            $portArguments += @('-p', "127.0.0.1::${plainLdapContainerPort}")
        }

        docker run -d --name $server.Name @portArguments `
            -e LDAP_ADMIN_USERNAME=admin `
            -e LDAP_ADMIN_PASSWORD=$bindPassword `
            -e LDAP_ROOT=dc=example,dc=org `
            -e LDAP_ENABLE_TLS=yes `
            -e LDAP_TLS_CERT_FILE=/certs/$certificateName.crt `
            -e LDAP_TLS_KEY_FILE=/certs/$certificateName.key `
            -e LDAP_TLS_CA_FILE=/certs/$($server.Ca).crt `
            -v "${serverDirectory}:/certs:ro" `
            $image | Out-Null

        $server.Port = Get-PublishedHostPort -ContainerName $server.Name -ContainerPort 1636
        if ($server.Name -eq 'jim-ldaps-system-trusted') {
            $script:plainLdapHostPort = Get-PublishedHostPort -ContainerName $server.Name -ContainerPort $plainLdapContainerPort
        }

        Write-Host "Started $($server.Name) on port $($server.Port) as $($server.Hostname)."
    }
}

function Wait-ForSmbReady {
    <#
    .SYNOPSIS
        Polls a Samba AD container until its SMB listener answers, which is how the container's own healthcheck
        determines readiness. LDAP/LDAPS come up alongside SMB, so this doubles as "the directory is ready".
    #>
    param(
        [string]$ContainerName,
        [int]$TimeoutSeconds,
        [string]$Description
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastReport = Get-Date
    while ((Get-Date) -lt $deadline) {
        docker exec $ContainerName smbclient -L localhost -U% -N 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            return $true
        }

        if (((Get-Date) - $lastReport).TotalSeconds -ge 30) {
            Write-Host "  Still waiting for $Description... ($([int]($deadline - (Get-Date)).TotalSeconds)s remaining)"
            $lastReport = Get-Date
        }

        Start-Sleep -Seconds 5
    }

    return $false
}

function Repair-SambaInterfaceBinding {
    <#
    .SYNOPSIS
        Fixes the "interfaces = lo eth0@ifNN" binding that Samba AD writes at provisioning time.
    .DESCRIPTION
        The veth peer index in that string is captured once, at provisioning, and stops matching reality on the
        very next container start in this kind of environment (the index is a host-wide, ever-incrementing
        counter). When it stops matching, TCP connections to the LDAPS listener are accepted and then reset during
        the TLS handshake, while plain LDAP and SMB continue to work, because only the TLS-serving listener enforces
        it strictly. Stripping the "@ifNN" suffix, leaving a plain interface name, avoids the mismatch entirely; see
        "Running Samba AD in the cloud sandbox" in test/CLAUDE.md.
    #>
    param([string]$ContainerName)

    docker exec $ContainerName sed -i 's/interfaces = lo eth0@if[0-9]*/interfaces = lo eth0/' /usr/local/samba/etc/smb.conf | Out-Null
}

function Start-SambaAdServer {
    Write-Host ''
    Write-Host "Starting Samba AD ($sambaContainerName)..."
    docker rm -f $sambaContainerName 2>$null | Out-Null

    docker run -d --privileged --name $sambaContainerName --hostname $sambaShortName `
        -e REALM=$sambaRealm `
        -e DOMAIN=$sambaDomain `
        -e ADMIN_PASS=$sambaAdminPassword `
        -e DNS_FORWARDER=8.8.8.8 `
        -p '127.0.0.1::636' `
        -p '127.0.0.1::389' `
        $sambaImage | Out-Null

    $script:sambaLdapsPort = Get-PublishedHostPort -ContainerName $sambaContainerName -ContainerPort 636
    $script:sambaLdapPort = Get-PublishedHostPort -ContainerName $sambaContainerName -ContainerPort 389

    # First boot provisions a full AD forest from scratch, which routinely takes several minutes. The container's
    # own healthcheck probes SMB (see the Dockerfile this image is modelled on), which comes up once provisioning
    # has finished, so polling for it doubles as a provisioning-complete signal; do not trust "docker ps" health
    # status alone, since a container can report unhealthy for a while before the first successful probe lands.
    Write-Host '  Waiting for domain provisioning to complete (can take several minutes on first boot)...'
    if (-not (Wait-ForSmbReady -ContainerName $sambaContainerName -TimeoutSeconds 900 -Description 'Samba AD provisioning')) {
        throw "Samba AD ($sambaContainerName) did not become ready within 900 seconds. Check 'docker logs $sambaContainerName'."
    }
    Write-Host '  Domain provisioned.'

    # The interfaces mismatch can already be present on first boot in this kind of environment, so fix it before
    # relying on LDAPS at all, not only after the restart below.
    Repair-SambaInterfaceBinding -ContainerName $sambaContainerName

    # Regenerate the TLS certificate with deterministic, explicit SANs. Samba autogenerates its own certificate at
    # first boot, but it only names the domain, not the host names JIM connects by (mirrors
    # test/integration/docker/samba-ad-prebuilt/post-provision.sh).
    Write-Host '  Regenerating the TLS certificate with explicit SANs...'
    $certificateScript = @"
set -e
cd /usr/local/samba/private/tls
openssl req -x509 -nodes -days 3650 -newkey rsa:2048 -keyout key.pem -out cert.pem -subj '/CN=$sambaHostname/O=JIM LDAPS Test' -addext 'subjectAltName=DNS:$sambaHostname,DNS:$sambaShortName' 2>/dev/null
cp cert.pem ca.pem
chmod 600 key.pem
"@
    # .ps1 files check out with CRLF endings (.gitattributes), and bash chokes on the carriage returns, so strip
    # them before handing the script over (same treatment as the runner's own docker exec bash blocks).
    docker exec $sambaContainerName bash -c ($certificateScript -replace "`r", '')

    # Wire the regenerated certificate into smb.conf and disable the strong-auth requirement that otherwise refuses
    # every simple bind over unencrypted LDAP (mirrors post-provision.sh's TLS configuration block).
    $smbConfScript = @'
set -e
if ! grep -q "tls enabled" /usr/local/samba/etc/smb.conf; then
    sed -i "/\[global\]/a \\
tls enabled = yes\\n\\
tls keyfile = /usr/local/samba/private/tls/key.pem\\n\\
tls certfile = /usr/local/samba/private/tls/cert.pem\\n\\
tls cafile = /usr/local/samba/private/tls/ca.pem\\
" /usr/local/samba/etc/smb.conf
fi
if grep -qi "^\s*ldap server require strong auth" /usr/local/samba/etc/smb.conf; then
    sed -i "s/^\(\s*\)ldap server require strong auth.*/\1ldap server require strong auth = no/" /usr/local/samba/etc/smb.conf
else
    sed -i "/\[global\]/a ldap server require strong auth = no" /usr/local/samba/etc/smb.conf
fi
'@
    docker exec $sambaContainerName bash -c ($smbConfScript -replace "`r", '')

    Write-Host '  Restarting to pick up the certificate and smb.conf changes...'
    docker restart $sambaContainerName | Out-Null

    if (-not (Wait-ForSmbReady -ContainerName $sambaContainerName -TimeoutSeconds 180 -Description 'Samba AD restart')) {
        throw "Samba AD ($sambaContainerName) did not become ready again after restart. Check 'docker logs $sambaContainerName'."
    }

    # The veth peer index can change again across the restart, so re-check the binding rather than assuming the
    # first fix still holds.
    Repair-SambaInterfaceBinding -ContainerName $sambaContainerName

    docker cp "${sambaContainerName}:/usr/local/samba/private/tls/ca.pem" $sambaCaPath | Out-Null
    if (-not (Test-Path $sambaCaPath)) {
        throw "Failed to copy the Samba AD CA certificate out of $sambaContainerName."
    }

    Write-Host "Started $sambaContainerName (LDAPS on $sambaLdapsPort, LDAP on $sambaLdapPort) as $sambaHostname."
}

function Set-SambaHostEntry {
    $hosts = Get-Content $hostsFile
    if (-not ($hosts | Where-Object { $_ -match [regex]::Escape($sambaHostname) })) {
        Add-Content -Path $hostsFile -Value "127.0.0.1 $sambaHostname"
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

if ($IncludeSambaAd) {
    Start-SambaAdServer
    Set-SambaHostEntry
}

Write-Host ''
Write-Host 'Waiting for the directory servers to accept connections...'
Start-Sleep -Seconds 16

Write-Host ''
Write-Host 'Set these, then run: dotnet test test/JIM.Worker.Tests/ --filter "Category=RequiresLdaps"'
Write-Host ''
# Ports come from the running containers (Docker assigns them; see the $servers comment).
$serversByName = @{}
foreach ($server in $servers) {
    $serversByName[$server.Name] = $server
}

$environmentVariables = [ordered]@{
    JIM_TEST_LDAPS_HOST                 = 'ldap-jim.local'
    JIM_TEST_LDAPS_PORT                 = "$($serversByName['jim-ldaps-jim-store'].Port)"
    JIM_TEST_LDAPS_USERNAME             = $bindDn
    JIM_TEST_LDAPS_PASSWORD             = $bindPassword
    JIM_TEST_LDAPS_CA_PATH              = (Join-Path $WorkingDirectory 'caB.crt')
    JIM_TEST_LDAPS_MISMATCH_HOST        = '127.0.0.1'
    JIM_TEST_LDAPS_EXPIRED_HOST         = 'ldap-old.local'
    JIM_TEST_LDAPS_EXPIRED_PORT         = "$($serversByName['jim-ldaps-expired'].Port)"
    JIM_TEST_LDAPS_SYSTEM_TRUSTED_HOST  = 'ldap-sys.local'
    JIM_TEST_LDAPS_SYSTEM_TRUSTED_PORT  = "$($serversByName['jim-ldaps-system-trusted'].Port)"
    JIM_TEST_LDAP_PLAIN_HOST            = 'ldap-sys.local'
    JIM_TEST_LDAP_PLAIN_PORT            = "$plainLdapHostPort"
}

if ($IncludeSambaAd) {
    $environmentVariables['JIM_TEST_LDAPS_SAMBA_HOST']         = $sambaHostname
    $environmentVariables['JIM_TEST_LDAPS_SAMBA_PORT']         = "$sambaLdapsPort"
    $environmentVariables['JIM_TEST_LDAPS_SAMBA_PLAIN_PORT']   = "$sambaLdapPort"
    $environmentVariables['JIM_TEST_LDAPS_SAMBA_USERNAME']     = $sambaAdminDn
    $environmentVariables['JIM_TEST_LDAPS_SAMBA_PASSWORD']     = $sambaAdminPassword
    $environmentVariables['JIM_TEST_LDAPS_SAMBA_CA_PATH']      = $sambaCaPath
    $environmentVariables['JIM_TEST_LDAPS_SAMBA_MISMATCH_HOST'] = '127.0.0.1'
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
