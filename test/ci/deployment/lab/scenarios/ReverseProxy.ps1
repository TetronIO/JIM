# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# The ReverseProxy scenario, dot-sourced by Invoke-LabCell.ps1 once the VM is up and the bundle checked: JIM behind
# nginx, set up from TLS and Reverse Proxy's own example, either re-encrypting to JIM from a VM of its own (Proxy =
# 'Remote', the recommended way) or forwarding plain HTTP to JIM on the JIM host's loopback interface (Proxy = 'Local',
# Docker only). Every check is made through the proxy, from the lab host, as a user's browser would. See README.md,
# "What a ReverseProxy cell checks".

$installPath = '/opt/jim'
$guide = 'tls-and-reverse-proxy.md'
$local = $cell.Proxy -eq 'Local'
# The name the proxy connects to JIM at, beside the one users type, as the guide's example has it.
$jimName = 'jim-app.' + ($hostName -replace '^[^.]+\.', '')
$apiKey = 'jim_ak_' + [System.Security.Cryptography.RandomNumberGenerator]::GetHexString(64, $true)
# Every request the checks make comes from the lab host, which is the cell network's gateway: the address JIM must
# record for a client once it trusts the proxy.
$clientAddress = $network.Gateway
$jimCaPath = $caPath
$proxyCaPath = Join-Path $OutputPath 'proxy-ca.crt'

#region nginx, its certificate and its configuration

# nginx from Ubuntu's own packages, as a customer installs it. On the air-gapped network it has no route to them, so a
# VM on the online network fetches it: for a proxy of its own, that VM becomes the proxy and then moves onto the
# cell's network, as an ExternalDatabase cell's server does; for a proxy on the JIM host, it fetches the packages for
# the JIM VM to install, and goes.
$proxyVm = "$vm-proxy"
Write-CellLog "creating $proxyVm, to bring nginx"
New-LabVm -Name $proxyVm -Image $lab.DatabaseServer.Image -Profile @($lab.BaseProfile, $lab.Networks['online'].Profile) `
    -MemoryGiB $cell.ProxyMemoryGiB
$fetch = if ($local) {
    'apt-get update -q && apt-get install -y -q --download-only nginx && mkdir -p /root/nginx && cp /var/cache/apt/archives/*.deb /root/nginx/ && tar -C /root -czf /root/nginx.tar.gz nginx'
}
else {
    'apt-get update -q && apt-get install -y -q nginx'
}
Invoke-LabVm -Name $proxyVm -Command @('bash', '-ec', "export DEBIAN_FRONTEND=noninteractive; $fetch") -AllowFailure `
    -LogPath (Join-Path $OutputPath 'nginx-install.log') | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'nginx could not be fetched from Ubuntu''s packages; see nginx-install.log'
}
if ($local) {
    $packages = Join-Path $OutputPath 'nginx.tar.gz'
    Copy-LabFile -Source "${proxyVm}:/root/nginx.tar.gz" -Destination $packages
    Remove-LabVm -Name $proxyVm
    $proxyVm = $null
    Copy-LabFile -Source $packages -Destination "${vm}:/root/nginx.tar.gz"
    Remove-Item $packages
    Invoke-LabVm -Name $vm -Command @('bash', '-ec', 'tar -C /root -xzf /root/nginx.tar.gz && DEBIAN_FRONTEND=noninteractive dpkg -i /root/nginx/*.deb') `
        -AllowFailure -LogPath (Join-Path $OutputPath 'nginx-install.log') | Out-Null
    $installedNginx = $LASTEXITCODE -eq 0
    $nginxVm = $vm
    $proxyAddress = $address
}
else {
    Set-LabVmProfile -Name $proxyVm -Profile @($lab.BaseProfile, $network.Profile)
    $installedNginx = $true
    $nginxVm = $proxyVm
    $proxyAddress = Get-LabVmAddress -Name $proxyVm -Gateway $network.Gateway
    Write-CellLog "the proxy is at $proxyAddress"
}
Add-Check "nginx installed from Ubuntu's packages $(if ($local) { 'on the JIM host' } else { 'on a server of its own' })" $installedNginx
if (-not $installedNginx) {
    throw 'stopped: nginx did not install'
}

function New-ProxyCertificate {
    # The proxy's own certificate for the name users type, from a certificate authority made for the cell, as an
    # organisation's own CA would issue it. Only the CA's certificate is kept; the keys go once they are in place.
    $work = Join-Path $OutputPath 'proxy-tls'
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $extensions = Join-Path $work 'server.ext'
    "subjectAltName=DNS:$hostName`nbasicConstraints=critical,CA:FALSE`nkeyUsage=critical,digitalSignature,keyEncipherment`nextendedKeyUsage=serverAuth" |
        Set-Content $extensions
    $commands = @(
        ,@('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', "$work/ca.key", '-out', $proxyCaPath, '-days', '2',
            '-subj', '/CN=JIM deployment lab proxy CA', '-addext', 'basicConstraints=critical,CA:TRUE',
            '-addext', 'keyUsage=critical,keyCertSign,cRLSign')
        ,@('req', '-newkey', 'rsa:2048', '-nodes', '-keyout', "$work/server.key", '-out', "$work/server.csr", '-subj', "/CN=$hostName")
        ,@('x509', '-req', '-in', "$work/server.csr", '-CA', $proxyCaPath, '-CAkey', "$work/ca.key", '-CAcreateserial',
            '-out', "$work/server.crt", '-days', '2', '-extfile', $extensions)
    )
    foreach ($arguments in $commands) {
        $output = $null | & openssl @arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "openssl $($arguments[0]) failed: $output"
        }
    }
    Remove-Item "$work/ca.key"
    $work
}

function Install-ProxyConfig {
    # The guide's example, with the lab's names, on the proxy: its certificate and key, the CA it checks JIM's
    # certificate by (re-encrypting only), and the configuration, which nginx must accept.
    param([string]$Config)
    $files = Get-LabProxyFile -Config $Config
    $tls = New-ProxyCertificate
    Copy-LabFile -Source "$tls/server.crt" -Destination "${nginxVm}:$($files.Certificate)"
    Copy-LabFile -Source "$tls/server.key" -Destination "${nginxVm}:$($files.Key)"
    Remove-Item -Recurse $tls
    if ($files.TrustedCa) {
        Copy-LabFile -Source $jimCaPath -Destination "${nginxVm}:$($files.TrustedCa)"
    }
    $configPath = Join-Path $OutputPath 'nginx-jim.conf'
    $Config | Set-Content $configPath
    Copy-LabFile -Source $configPath -Destination "${nginxVm}:/etc/nginx/conf.d/jim.conf"
    Invoke-LabVm -Name $nginxVm -Command @('bash', '-ec', "chmod 600 '$($files.Key)' && nginx -t && systemctl reload-or-restart nginx") `
        -AllowFailure -LogPath (Join-Path $OutputPath 'nginx-config.log') | Out-Null
    $accepted = $LASTEXITCODE -eq 0
    Add-Check "nginx runs the guide's example configuration (nginx-jim.conf)" $accepted
    if (-not $accepted) {
        throw 'stopped: nginx refused the configuration; see nginx-config.log'
    }
}

#endregion

#region What is checked through the proxy

function Invoke-ProxyRequest {
    # A request through the proxy, at the name users type, trusting only the proxy's CA; returns the HTTP status and
    # the body.
    param([string]$Path, [string[]]$Argument = @())
    $body = New-TemporaryFile
    try {
        $status = $null | & curl -sS --max-time 15 --noproxy $hostName --cacert $proxyCaPath --resolve "${hostName}:443:$proxyAddress" `
            -o $body.FullName -w '%{http_code}' @Argument "https://$hostName$Path" 2>$null
        [pscustomobject]@{ Status = "$status"; Body = (Get-Content -Raw $body.FullName) }
    }
    finally {
        Remove-Item $body.FullName -ErrorAction SilentlyContinue
    }
}

function Invoke-PasswordRequest {
    # A REST request carrying a password, for a Metaverse Object that does not exist: once JIM accepts the transport
    # it answers 404; while it cannot confirm the connection is encrypted, 403 with its refusal.
    Invoke-ProxyRequest -Path "/api/v1/metaverse/objects/$([guid]::NewGuid())/password" -Argument @(
        '-X', 'POST', '-H', "Authorization: Bearer $apiKey", '-H', 'Content-Type: application/json',
        '--data', '{"password":"Lab-Only-Password-1"}')
}

function Test-WebSocketCheck {
    # The portal's live connection, as a browser opens it: negotiate, then ask to switch to a WebSocket. JIM agrees
    # with 101 only if the proxy passed the Upgrade and Connection headers through; without them the portal falls back
    # to long polling.
    $negotiate = Invoke-ProxyRequest -Path '/_blazor/negotiate?negotiateVersion=1' -Argument @('-X', 'POST', '-H', 'Content-Length: 0')
    $token = try { ($negotiate.Body | ConvertFrom-Json).connectionToken } catch { $null }
    if (-not $token) {
        Add-Check 'through the proxy: the portal''s live connection is a WebSocket' $false "negotiate answered HTTP $($negotiate.Status)"
        return
    }
    $key = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
    $status = $null | & curl -sS --http1.1 --max-time 5 --noproxy $hostName --cacert $proxyCaPath --resolve "${hostName}:443:$proxyAddress" `
        -o /dev/null -w '%{http_code}' -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' `
        -H "Sec-WebSocket-Key: $key" "https://$hostName/_blazor?id=$([uri]::EscapeDataString($token))" 2>$null
    Add-Check 'through the proxy: the portal''s live connection is a WebSocket' ("$status" -eq '101') "HTTP $status, not 101 Switching Protocols"
}

function Test-ClientAddressCheck {
    # The audit log's record of the sign-in just made: the lab host's address, not the proxy's.
    $recorded = Invoke-Guest -Action Sql -Sql 'SELECT "ClientIpAddress" FROM "Activities" WHERE "TargetName" = ''Interactive sign-in succeeded'' ORDER BY "Created" DESC LIMIT 1;'
    Add-Check "through the proxy: the security audit log records the client's address ($clientAddress), not the proxy's" `
        ("$recorded" -eq $clientAddress) "recorded '$recorded'"
}

function Test-TrustedProxyLogCheck {
    $line = Invoke-Guest -Action WebLog | Where-Object { $_ -match 'Trusting forwarded headers from' } | Select-Object -Last 1
    Add-Check 'jim.web logs that it trusts 1 proxy address, as the guide says it will' `
        ("$line" -match 'Trusting forwarded headers from 1 known proxy address\(es\)') $(if ($line) { "$line" } else { 'no such line' })
}

function Test-ThroughProxyCheck {
    # Everything a user and an administrator rely on, through the proxy, once JIM trusts it.
    $address = $proxyAddress
    $caPath = $proxyCaPath
    if (-not (Test-ReadyCheck 'through the proxy' -Trusting "the proxy's CA")) {
        throw 'stopped: JIM is not ready through the proxy'
    }
    Test-SignInCheck 'through the proxy'
    Test-ClientAddressCheck
    Test-WebSocketCheck
    Test-TrustedProxyLogCheck
}

#endregion

if (-not $local) {
    # 1. JIM, with a certificate for both names, as the guide says, and trusting the proxy's address.
    $settings = New-SetupSettings @{
        JIM_SETUP_TLS_NAMES = "$hostName,$jimName,$address"; JIM_TRUSTED_PROXIES = $proxyAddress; JIM_INFRASTRUCTURE_API_KEY = $apiKey
    }
    $installed = (Invoke-Setup -Folder $bundleFolder -Settings $settings -Log 'setup-install.log').ExitCode -eq 0
    Add-Check 'installed with setup.sh, with a certificate for the proxy''s name and the proxy trusted' $installed
    if (-not $installed) {
        throw 'stopped: JIM did not install'
    }
    Copy-LabFile -Source "${vm}:$installPath/tls/ca.crt" -Destination $jimCaPath
    if (-not (Test-ReadyCheck 'installed')) {
        throw 'stopped: JIM is not ready'
    }

    # 2. nginx re-encrypting to JIM at the second name, which the proxy resolves to the JIM server.
    Invoke-LabVm -Name $proxyVm -Command @('sh', '-c', "echo '$address $jimName' >> /etc/hosts") | Out-Null
    $example = Get-DocumentedCommand -Page $guide -Heading 'nginx Example'
    Install-ProxyConfig (ConvertTo-LabProxyConfig -Config $example -UserName $hostName -JimName $jimName)

    # 3. Through the proxy.
    Test-ThroughProxyCheck
}
else {
    # 1. JIM, installed as usual, serving HTTPS on port 443 until it moves behind the proxy.
    $settings = New-SetupSettings @{ JIM_INFRASTRUCTURE_API_KEY = $apiKey }
    $installed = (Invoke-Setup -Folder $bundleFolder -Settings $settings -Log 'setup-install.log').ExitCode -eq 0
    Add-Check 'installed with setup.sh' $installed
    if (-not $installed) {
        throw 'stopped: JIM did not install'
    }
    Copy-LabFile -Source "${vm}:$installPath/tls/ca.crt" -Destination $jimCaPath
    if (-not (Test-ReadyCheck 'installed')) {
        throw 'stopped: JIM is not ready'
    }

    # 2. JIM moved onto plain HTTP on the loopback interface, as Plain HTTP for a Proxy on the Same Host says: its
    # docker-compose.local-proxy.yml, added after the other two files in the command that starts JIM.
    $override = Join-Path $OutputPath 'docker-compose.local-proxy.yml'
    Get-DocumentedCommand -Page $guide -Heading 'Plain HTTP for a Proxy on the Same Host' | Set-Content $override
    Copy-LabFile -Source $override -Destination "${vm}:$installPath/docker-compose.local-proxy.yml"
    $start = Get-DocumentedCommand -Page 'deployment.md' -Heading 'By Hand' -Block 3
    if ($start -notmatch '-f docker-compose\.production\.yml') {
        throw "Installing by hand no longer starts JIM with -f docker-compose.production.yml:`n$start"
    }
    $start = $start -replace '-f docker-compose\.production\.yml', '$0 -f docker-compose.local-proxy.yml'
    $moved = (Invoke-Documented -Command $start -WorkingDirectory $installPath -Title 'starting JIM with docker-compose.local-proxy.yml') -eq 0
    Add-Check 'JIM started again with docker-compose.local-proxy.yml' $moved
    if (-not $moved) {
        throw 'stopped: JIM did not start with docker-compose.local-proxy.yml'
    }
    $null | & curl -sS --max-time 5 -o /dev/null "http://${address}:8080/" 2>$null
    Add-Check 'JIM''s plain HTTP port, 8080, cannot be reached from another machine' ($LASTEXITCODE -ne 0)

    # 3. nginx on the JIM host, forwarding plain HTTP to JIM as the guide says to point it.
    $example = Get-DocumentedCommand -Page $guide -Heading 'nginx Example'
    Install-ProxyConfig (ConvertTo-LabProxyConfig -Config $example -UserName $hostName -PlainHttp 'http://localhost:8080')

    # 4. Before JIM trusts the proxy, the guide says sign-in fails, and so does a REST request carrying a password.
    # nginx now holds port 443 on the JIM host, so the checks reach JIM at the same address, trusting the proxy's CA.
    $caPath = $proxyCaPath
    if (-not (Test-ReadyCheck 'through the proxy, not yet trusted' -Trusting "the proxy's CA")) {
        throw 'stopped: JIM is not ready through the proxy'
    }
    $signIn = Invoke-LabSignIn -Address $address -HostName $hostName -CaPath $caPath `
        -User $lab.IdentityProvider.User -Password $lab.IdentityProvider.Password
    Add-Check 'without JIM_TRUSTED_PROXIES: sign-in fails on the http:// callback, as the guide says' `
        (-not $signIn.Passed -and $signIn.Detail -match 'redirect URI') $(if ($signIn.Passed) { 'signed in' } else { $signIn.Detail })
    $refused = Invoke-PasswordRequest
    Add-Check 'without JIM_TRUSTED_PROXIES: a REST request carrying a password is refused, as the guide says' `
        ($refused.Status -eq '403' -and $refused.Body -match 'JIM_TRUSTED_PROXIES') "HTTP $($refused.Status)"

    # 5. JIM trusts the jim-network gateway, found as the guide says, set in .env, and started again.
    $find = Get-DocumentedCommand -Page $guide -Heading 'Trusting the Reverse Proxy'
    $gateway = "$(Invoke-LabVm -Name $vm -Command @('bash', '-ec', $find) -WorkingDirectory $installPath)".Trim()
    $found = $gateway -match '^\d+\.\d+\.\d+\.\d+$'
    Add-Check 'the jim-network gateway found as the guide says' $found "'$gateway'"
    if (-not $found) {
        throw 'stopped: no jim-network gateway to trust'
    }
    Invoke-LabVm -Name $vm -WorkingDirectory $installPath -Command @('bash', '-ec',
        "sed -i '/^JIM_TRUSTED_PROXIES=/d' .env && echo 'JIM_TRUSTED_PROXIES=$gateway' >> .env") | Out-Null
    $restarted = (Invoke-Documented -Command $start -WorkingDirectory $installPath -Title 'starting JIM with JIM_TRUSTED_PROXIES set') -eq 0
    Add-Check "JIM started again, trusting $gateway" $restarted
    if (-not $restarted) {
        throw 'stopped: JIM did not start again'
    }

    # 6. Through the proxy, and the password request accepted.
    Test-ThroughProxyCheck
    $accepted = Invoke-PasswordRequest
    Add-Check 'through the proxy: a REST request carrying a password is accepted' ($accepted.Status -eq '404') `
        "HTTP $($accepted.Status), expected 404 for an object that does not exist"
}
