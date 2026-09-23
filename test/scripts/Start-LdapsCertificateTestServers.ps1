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
    the Samba AD / 389 Directory Server / unencrypted-connection fixtures read.

    -Include389 adds two 389 Directory Server containers presenting the same certificates (one the valid
    JIM-store-only certificate, one the expired certificate), so the second directory family is covered on the
    same rows without generating anything new.

    Requires Docker, OpenSSL, and root (it writes hosts entries and adds a CA to the machine trust store).

.PARAMETER Stop
    Removes the containers, hosts entries and trusted CA, and deletes the working directory. Always cleans up the
    Samba AD container and its hosts entry too, whether or not -IncludeSambaAd was passed on the run being stopped,
    so a stale Samba AD container from an earlier run is never left behind. The two 389 Directory Server containers
    are removed unconditionally in the same way, whether or not -Include389 was passed.

.PARAMETER IncludeSambaAd
    Also stands up a Samba AD domain controller, covering the AD-family directory type alongside OpenLDAP. First
    boot provisions a domain from scratch, which takes several minutes; the script waits and prints progress.

.PARAMETER Include389
    Also stands up two 389 Directory Server containers (one presenting the valid JIM-store-only certificate, one
    presenting the expired certificate), covering the second RFC 4512 directory family alongside OpenLDAP. They
    reuse the certificates this script already generates and come up in a few seconds.

.PARAMETER WorkingDirectory
    Where certificates are generated. Defaults to a jim-ldaps-test directory under the system temporary path.

.EXAMPLE
    sudo pwsh ./test/scripts/Start-LdapsCertificateTestServers.ps1

.EXAMPLE
    sudo pwsh ./test/scripts/Start-LdapsCertificateTestServers.ps1 -IncludeSambaAd

.EXAMPLE
    sudo pwsh ./test/scripts/Start-LdapsCertificateTestServers.ps1 -IncludeSambaAd -Include389

.EXAMPLE
    sudo pwsh ./test/scripts/Start-LdapsCertificateTestServers.ps1 -Stop
#>
[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$IncludeSambaAd,
    [switch]$Include389,
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

# 389 Directory Server, added by -Include389, covers the second RFC 4512 directory family alongside OpenLDAP.
# Matches the base image of the integration lab's 389 Directory Server container (test/integration/docker/dirsrv).
# Both containers present certificates this script already generates: the certificate names a host, both hosts
# already resolve to loopback via the OpenLDAP hosts entries, so a 389 server presenting the same certificate on
# its own port is exactly the row wanted, and no new certificates or hosts entries are needed. There is no
# system-trusted 389 row: additive trust is a property of the client, already proven by the OpenLDAP row.
$dirsrvImage = '389ds/dirsrv:3.1'
$dirsrvContainerName = 'jim-ldaps-389'
$dirsrvExpiredContainerName = 'jim-ldaps-389-expired'
$dirsrvBindDn = 'cn=Directory Manager'
$dirsrvBindPassword = 'Test@123!JIM'
# The image serves plain LDAP on container port 3389 alongside LDAPS on 3636.
$dirsrvLdapsContainerPort = 3636
$dirsrvPlainContainerPort = 3389
# Host ports assigned by Docker after the containers start.
$dirsrvLdapsPort = $null
$dirsrvPlainPort = $null
$dirsrvExpiredLdapsPort = $null

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

    # Same rule for the 389 Directory Server pair: a stale container from an earlier -Include389 run must never
    # survive a plain -Stop.
    docker rm -f $dirsrvContainerName 2>$null | Out-Null
    docker rm -f $dirsrvExpiredContainerName 2>$null | Out-Null

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

    # Read the published ports only AFTER the restart above: an ephemeral '::<port>' mapping is re-assigned
    # to a NEW random host port on every container restart, so a port captured at docker run time is stale
    # by here and every connection to it fails with "the LDAP server is unavailable".
    $script:sambaLdapsPort = Get-PublishedHostPort -ContainerName $sambaContainerName -ContainerPort 636
    $script:sambaLdapPort = Get-PublishedHostPort -ContainerName $sambaContainerName -ContainerPort 389

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

function Wait-ForDirsrvReady {
    <#
    .SYNOPSIS
        Polls a 389 Directory Server container until its own healthcheck (dscontainer -H) passes AND a simple bind
        as cn=Directory Manager succeeds, which proves DS_DM_PASSWORD has been applied and the listener answers.
    #>
    param(
        [string]$ContainerName,
        [int]$TimeoutSeconds,
        [string]$Description
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastReport = Get-Date
    while ((Get-Date) -lt $deadline) {
        docker exec $ContainerName /usr/lib/dirsrv/dscontainer -H 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            # The image carries the OpenLDAP client tools, so bind over the container's own plain port.
            docker exec $ContainerName ldapwhoami -x -H "ldap://localhost:${dirsrvPlainContainerPort}" -D $dirsrvBindDn -w $dirsrvBindPassword 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) {
                return $true
            }
        }

        if (((Get-Date) - $lastReport).TotalSeconds -ge 30) {
            Write-Host "  Still waiting for $Description... ($([int]($deadline - (Get-Date)).TotalSeconds)s remaining)"
            $lastReport = Get-Date
        }

        Start-Sleep -Seconds 5
    }

    return $false
}

function Start-DirsrvServers {
    Write-Host ''
    Write-Host "Starting 389 Directory Server ($dirsrvContainerName, $dirsrvExpiredContainerName)..."

    # On every start dscontainer imports /data/tls/server.key, /data/tls/server.crt and every /data/tls/ca/*.crt
    # into its NSS database in place of its self-signed certificate, so a host directory bind-mounted at /data/tls
    # is all that is needed to have the server present one of the certificates generated above. An expired
    # certificate is imported and served as-is, which is what the expired row relies on.
    $dirsrvServers = @(
        @{ Name = $dirsrvContainerName;        CertificateName = 'jim'; Ca = 'caB'; PublishPlainPort = $true }
        @{ Name = $dirsrvExpiredContainerName; CertificateName = 'old'; Ca = 'caB'; PublishPlainPort = $false }
    )

    # Start both first, then wait for both: they come up in parallel.
    foreach ($server in $dirsrvServers) {
        $tlsDirectory = Join-Path $WorkingDirectory $server.Name 'tls'
        $caDirectory = Join-Path $tlsDirectory 'ca'
        New-Item -ItemType Directory -Path $caDirectory -Force | Out-Null
        Copy-Item (Join-Path $WorkingDirectory "$($server.CertificateName).key") (Join-Path $tlsDirectory 'server.key') -Force
        Copy-Item (Join-Path $WorkingDirectory "$($server.CertificateName).crt") (Join-Path $tlsDirectory 'server.crt') -Force
        Copy-Item (Join-Path $WorkingDirectory "$($server.Ca).crt") (Join-Path $caDirectory "$($server.Ca).crt") -Force
        foreach ($file in @((Join-Path $tlsDirectory 'server.key'), (Join-Path $tlsDirectory 'server.crt'), (Join-Path $caDirectory "$($server.Ca).crt"))) {
            & chmod 644 $file
        }

        docker rm -f $server.Name 2>$null | Out-Null

        # Only the valid-certificate server also publishes its unencrypted LDAP port, for the plain-connection
        # row; the expired server exists solely to exercise LDAPS certificate validation. Same '127.0.0.1::<port>'
        # ephemeral, loopback-bound publishing as the OpenLDAP servers.
        $portArguments = @('-p', "127.0.0.1::${dirsrvLdapsContainerPort}")
        if ($server.PublishPlainPort) {
            $portArguments += @('-p', "127.0.0.1::${dirsrvPlainContainerPort}")
        }

        docker run -d --name $server.Name @portArguments `
            -e DS_DM_PASSWORD=$dirsrvBindPassword `
            -v "${tlsDirectory}:/data/tls" `
            $dirsrvImage | Out-Null
    }

    foreach ($server in $dirsrvServers) {
        Write-Host "  Waiting for $($server.Name) to become ready..."
        if (-not (Wait-ForDirsrvReady -ContainerName $server.Name -TimeoutSeconds 300 -Description "$($server.Name) start-up")) {
            throw "389 Directory Server ($($server.Name)) did not become ready within 300 seconds. Check 'docker logs $($server.Name)'."
        }
    }

    # No restart is involved, so the published ports are stable and can be read straight after readiness.
    $script:dirsrvLdapsPort = Get-PublishedHostPort -ContainerName $dirsrvContainerName -ContainerPort $dirsrvLdapsContainerPort
    $script:dirsrvPlainPort = Get-PublishedHostPort -ContainerName $dirsrvContainerName -ContainerPort $dirsrvPlainContainerPort
    $script:dirsrvExpiredLdapsPort = Get-PublishedHostPort -ContainerName $dirsrvExpiredContainerName -ContainerPort $dirsrvLdapsContainerPort

    Write-Host "Started $dirsrvContainerName (LDAPS on $dirsrvLdapsPort, LDAP on $dirsrvPlainPort) as ldap-jim.local."
    Write-Host "Started $dirsrvExpiredContainerName (LDAPS on $dirsrvExpiredLdapsPort) as ldap-old.local."
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

# No hosts entries to add: ldap-jim.local and ldap-old.local already exist for the OpenLDAP servers.
if ($Include389) {
    Start-DirsrvServers
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

if ($Include389) {
    $environmentVariables['JIM_TEST_LDAPS_389_HOST']          = 'ldap-jim.local'
    $environmentVariables['JIM_TEST_LDAPS_389_PORT']          = "$dirsrvLdapsPort"
    $environmentVariables['JIM_TEST_LDAPS_389_PLAIN_PORT']    = "$dirsrvPlainPort"
    $environmentVariables['JIM_TEST_LDAPS_389_USERNAME']      = $dirsrvBindDn
    $environmentVariables['JIM_TEST_LDAPS_389_PASSWORD']      = $dirsrvBindPassword
    $environmentVariables['JIM_TEST_LDAPS_389_CA_PATH']       = (Join-Path $WorkingDirectory 'caB.crt')
    $environmentVariables['JIM_TEST_LDAPS_389_MISMATCH_HOST'] = '127.0.0.1'
    $environmentVariables['JIM_TEST_LDAPS_389_EXPIRED_HOST']  = 'ldap-old.local'
    $environmentVariables['JIM_TEST_LDAPS_389_EXPIRED_PORT']  = "$dirsrvExpiredLdapsPort"
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
