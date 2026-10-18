# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# The InstallerCases scenario, dot-sourced by Invoke-LabCell.ps1 once the VM is up and the bundle checked: what the
# installer does with settings it must refuse, with a run stopped partway, and when it is run again over an
# installation. Whatever it refuses, or is stopped from doing, must leave the host exactly as it was. See README.md,
# "What an InstallerCases cell checks".

$installPath = '/opt/jim'
$apiKey = 'jim_ak_' + [System.Security.Cryptography.RandomNumberGenerator]::GetHexString(64, $true)
$memoryKiB = [long]((Invoke-LabVm -Name $vm -Command @('awk', '/^MemTotal:/ { print $2 }', '/proc/meminfo')) | Select-Object -First 1)
$memoryMB = [int][math]::Floor($memoryKiB / 1024)
# More than the host has: PostgreSQL would not start.
$impossible = "$([int][math]::Ceiling($memoryMB / 1024) + 64)GB"

function Get-InstallFolderContent {
    "$(Invoke-LabVm -Name $vm -AllowFailure -Command @('sh', '-c', "ls -A $installPath 2>/dev/null"))".Trim()
}

function Get-ServedCertificate {
    # The serial number of the certificate JIM serves, as the lab host sees it.
    $served = $null | & bash -c 'openssl s_client -connect "$1:443" -servername "$2" < /dev/null 2>/dev/null | openssl x509 -noout -serial' `
        served $address $hostName
    "$served".Trim()
}

function Get-InstalledCertificate {
    "$(Invoke-LabVm -Name $vm -Command @('openssl', 'x509', '-in', "$installPath/tls/tls.crt", '-noout', '-serial'))".Trim()
}

# 1. Settings refused before anything is written: a malformed infrastructure API key, and a shared_buffers the host
# cannot give.
$badKey = Invoke-Setup -Folder $bundleFolder -Settings (New-SetupSettings @{ JIM_INFRASTRUCTURE_API_KEY = 'not-a-jim-key' }) `
    -Log 'setup-bad-key.log'
$left = Get-InstallFolderContent
Add-Check 'a malformed JIM_INFRASTRUCTURE_API_KEY refused before anything is written' ($badKey.ExitCode -ne 0 -and -not $left) `
    "exit $($badKey.ExitCode); $installPath holds '$left'; $(Get-LabSetupMessage -Line $badKey.Output -Level ERR | Select-Object -First 1)"

$tooBig = Invoke-Setup -Folder $bundleFolder -Settings (New-SetupSettings @{ JIM_DB_SHARED_BUFFERS = $impossible }) -Log 'setup-too-big.log'
$left = Get-InstallFolderContent
Add-Check "JIM_DB_SHARED_BUFFERS=$impossible refused on a first install, leaving nothing behind" ($tooBig.ExitCode -ne 0 -and -not $left) `
    "exit $($tooBig.ExitCode); $installPath holds '$left'; $(Get-LabSetupMessage -Line $tooBig.Output -Level ERR | Select-Object -First 1)"

# 2. The install that follows, with an infrastructure API key.
$settings = New-SetupSettings @{ JIM_INFRASTRUCTURE_API_KEY = $apiKey }
$installed = (Invoke-Setup -Folder $bundleFolder -Settings $settings -Log 'setup-install.log').ExitCode -eq 0
Add-Check 'the next install works' $installed
if (-not $installed) {
    throw 'stopped: JIM did not install'
}
Copy-LabFile -Source "${vm}:$installPath/tls/ca.crt" -Destination $caPath
if (-not (Test-ReadyCheck 'installed')) {
    throw 'stopped: JIM is not ready'
}
Test-SignInCheck 'installed'
$password = "$(Invoke-Guest -Action DatabasePassword)"
Add-Check 'the database password is generated, not left as the template''s placeholder' ($password.Length -ge 16 -and $password -notmatch 'your_secure_password') `
    "$($password.Length) characters"

$status = $null | & curl -sS -o /dev/null -w '%{http_code}' --max-time 10 --noproxy $hostName --cacert $caPath `
    --resolve "${hostName}:443:$address" -H "Authorization: Bearer $apiKey" "https://$hostName/api/v1/connected-systems" 2>$null
Add-Check 'JIM accepts the infrastructure API key given to the installer' ("$status" -eq '200') "HTTP $status"
$module = Get-BundleModule $BundlePath
$trust = Join-Path $OutputPath 'trust.pem'
Get-Content /etc/ssl/certs/ca-certificates.crt, $caPath | Set-Content $trust
$env:SSL_CERT_FILE = $trust
try {
    $rejected = $null | & (Get-Process -Id $PID).Path -NoProfile -Command {
        param($ModulePath, $Url)
        Import-Module $ModulePath
        try {
            $connection = Connect-JIM -Url $Url -ApiKey ('jim_ak_' + '0' * 40)
            "connected=$($connection.Connected) authorised=$($connection.Authorised)"
        }
        catch {
            "refused: $($_.Exception.Message)"
        }
    } -args $module, "https://$address" 2>&1
}
finally {
    Remove-Item Env:\SSL_CERT_FILE -ErrorAction SilentlyContinue
}
Add-Check 'Connect-JIM reports a key JIM rejects' ("$rejected" -match '^refused|authorised=False') "$rejected"

# 3. Run again with no answers: the installer asks before overwriting the installation, and takes no for an answer.
Invoke-Guest -Action Sql -Sql "CREATE SCHEMA IF NOT EXISTS jimlab; CREATE TABLE IF NOT EXISTS jimlab.marker (cell text); INSERT INTO jimlab.marker VALUES ('$CellName');" | Out-Null
$before = Get-Fingerprint
$unanswered = Invoke-Setup -Folder $bundleFolder -Settings $settings -Log 'setup-rerun-unanswered.log'
$aborted = Get-LabSetupMessage -Line $unanswered.Output -Level INFO | Where-Object { $_ -match '^Aborting' } | Select-Object -First 1
Add-Check 'run again with no answer, the installer leaves the installation alone' ([bool]$aborted) "exit $($unanswered.ExitCode); $aborted"
Test-UnchangedCheck 'run again with no answer: every file and container as it was' $before

# 4. Run again, answering yes: a new certificate, served at once (#1956), with the database, its password and the
# certificate authority kept.
$servedBefore = Get-ServedCertificate
$caBefore = Invoke-LabVm -Name $vm -Command @('sha256sum', "$installPath/tls/ca.crt")
$rerun = Invoke-Setup -Folder $bundleFolder -Settings $settings -Answer 'y', 'y', 'y', 'y' -Log 'setup-rerun.log'
Add-Check 'run again, answering yes, the installer reinstalls' ($rerun.ExitCode -eq 0) "exit $($rerun.ExitCode)"
if (Test-ReadyCheck 'reinstalled') {
    $installedCertificate = Get-InstalledCertificate
    $served = Get-ServedCertificate
    Add-Check 'reinstalled: JIM serves the certificate the run issued (#1956)' ($served -eq $installedCertificate -and $served -ne $servedBefore) `
        "served $served, installed $installedCertificate, before $servedBefore"
    $caAfter = Invoke-LabVm -Name $vm -Command @('sha256sum', "$installPath/tls/ca.crt")
    Add-Check 'reinstalled: the certificate authority kept' ("$caAfter" -eq "$caBefore")
    $marker = Invoke-Guest -Action Sql -Sql 'SELECT cell FROM jimlab.marker;'
    Add-Check 'reinstalled: the database kept, with its data and password' ("$marker" -eq $CellName -and "$(Invoke-Guest -Action DatabasePassword)" -eq $password) `
        "found '$marker'"
    Test-SignInCheck 'reinstalled'
}

# 5. shared_buffers given: more than half the host's memory warns; more than it has is refused, changing nothing.
$generous = "$([int][math]::Floor($memoryMB * 0.6))MB"
$warned = Invoke-Setup -Folder $bundleFolder -Settings (New-SetupSettings @{ JIM_INFRASTRUCTURE_API_KEY = $apiKey; JIM_DB_SHARED_BUFFERS = $generous }) `
    -Answer 'y', 'y', 'y', 'y' -Log 'setup-generous.log'
$warning = Get-LabSetupMessage -Line $warned.Output -Level WARN | Where-Object { $_ -match 'JIM_DB_SHARED_BUFFERS' } | Select-Object -First 1
Add-Check "JIM_DB_SHARED_BUFFERS=$generous, over half of $memoryMB MB, warns and is used" ($warned.ExitCode -eq 0 -and $warning) `
    "exit $($warned.ExitCode); $warning"
if (Test-ReadyCheck "with shared_buffers $generous") {
    $actual = Invoke-Guest -Action Sql -Sql 'SHOW shared_buffers;'
    Add-Check "PostgreSQL runs with the shared_buffers given" ((ConvertTo-LabMegabytes "$actual") -eq (ConvertTo-LabMegabytes $generous)) "$actual"
}

$before = Get-Fingerprint
$refused = Invoke-Setup -Folder $bundleFolder -Settings (New-SetupSettings @{ JIM_INFRASTRUCTURE_API_KEY = $apiKey; JIM_DB_SHARED_BUFFERS = $impossible }) `
    -Answer 'y', 'y', 'y', 'y' -Log 'setup-rerun-too-big.log'
Add-Check "JIM_DB_SHARED_BUFFERS=$impossible refused on a reinstall" ($refused.ExitCode -ne 0) `
    "exit $($refused.ExitCode); $(Get-LabSetupMessage -Line $refused.Output -Level ERR | Select-Object -First 1)"
Test-UnchangedCheck 'the refused reinstall left every file and container as it was, with nothing set aside (#1994)' $before
Add-Check 'after the refused reinstall, JIM starts as documented' ((Invoke-DocumentedStart) -eq 0 -and (Wait-Ready) -ge 0)

# 6. Ctrl+C at a question: the run stops and puts everything back. The installer runs in a session of its own, asked
# to overwrite the installation (yes), then left waiting at the next question, which a value left out of its settings
# makes it ask; then the whole session is sent SIGINT, as Ctrl+C sends it to the terminal's foreground processes.
$before = Get-Fingerprint
$interrupt = @'
set -u
fifo=$(mktemp -u) && mkfifo "$fifo"
exec 7<>"$fifo"
# A shell without job control starts a background command with SIGINT ignored, which would pass to the installer and
# stop it reacting to Ctrl+C at all, so SIGINT is given back its default first.
setsid env --default-signal=INT JIM_SSO_AUTHORITY="$1" JIM_SETUP_RUNTIME="$2" JIM_SETUP_PODMAN_ROOTLESS="$3" ./setup.sh < "$fifo" > /root/setup-interrupted.log 2>&1 &
pid=$!
printf 'y\n' >&7
size=-1
for _ in $(seq 1 60); do
    sleep 2
    now=$(wc -c < /root/setup-interrupted.log)
    [ "$now" -gt 0 ] && [ "$now" -eq "$size" ] && break
    size=$now
done
# The installer's own process group, which setsid made it the leader of.
group=$(ps -o pgid= -p "$pid" | tr -d ' ')
kill -INT -- "-${group:-$pid}"
for _ in $(seq 1 30); do kill -0 "$pid" 2>/dev/null || break; sleep 1; done
wait "$pid"; code=$?
exec 7>&-
rm -f "$fifo"
echo "setup.sh stopped, exit $code, at: $(tail -c 200 /root/setup-interrupted.log | tr '\n' ' ')"
'@
$stopped = Invoke-LabVm -Name $vm -WorkingDirectory $bundleFolder -AllowFailure -Command @('bash', '-c', $interrupt, 'interrupt',
    $authority, $cell.Runtime, "$($rootless.ToString().ToLower())")
Write-CellLog "$stopped"
Copy-LabFile -Source "${vm}:/root/setup-interrupted.log" -Destination (Join-Path $OutputPath 'setup-interrupted.log')
Test-UnchangedCheck 'Ctrl+C at a question put every file and container back' $before
Add-Check 'after Ctrl+C, JIM starts as documented' ((Invoke-DocumentedStart) -eq 0 -and (Wait-Ready) -ge 0)

# 7. Looking after the certificate, as TLS and Reverse Proxy says: renewed from the same certificate authority, created
# again for new names (a new authority), and replaced by an organisation's own certificate, which the installer then
# refuses to renew. Last in the cell, as from here the authority the cell's checks trust changes.
$tlsPage = 'tls-and-reverse-proxy.md'

function Get-ServedNames {
    $names = $null | & bash -c 'openssl s_client -connect "$1:443" -servername "$2" < /dev/null 2>/dev/null | openssl x509 -noout -ext subjectAltName' `
        names $address $hostName
    "$($names | Select-Object -Skip 1)".Trim()
}

function Test-TrustedBy {
    # Whether a TLS client trusting only this authority accepts the certificate JIM serves.
    param([string]$Authority)
    $null | & curl -sS -o /dev/null --max-time 10 --noproxy $hostName --cacert $Authority --resolve "${hostName}:443:$address" `
        "https://$hostName/api/v1/health/ready" 2>$null
    $LASTEXITCODE -eq 0
}

# 7a. Renewed, as the page says for a certificate the installer created.
$servedBefore = Get-ServedCertificate
$caBefore = Invoke-LabVm -Name $vm -Command @('sha256sum', "$installPath/tls/ca.crt")
$namesBefore = Invoke-LabVm -Name $vm -Command @('cat', "$installPath/tls/names")
$renew = Get-DocumentedCommand -Page $tlsPage -Heading 'Renewing the Certificate' -Block 2
if ($renew -notmatch '--renew-certificate') {
    throw "Renewing the Certificate no longer gives setup.sh --renew-certificate:`n$renew"
}
Add-Check '--renew-certificate, as documented, works' ((Invoke-Documented -Command $renew -Title 'the certificate renewal' -Rootful) -eq 0)
if (Test-ReadyCheck 'renewed') {
    $installedCertificate = Get-InstalledCertificate
    $served = Get-ServedCertificate
    Add-Check 'renewed: JIM serves the new certificate' ($served -eq $installedCertificate -and $served -ne $servedBefore) `
        "served $served, installed $installedCertificate, before $servedBefore"
    $caAfter = Invoke-LabVm -Name $vm -Command @('sha256sum', "$installPath/tls/ca.crt")
    $namesAfter = Invoke-LabVm -Name $vm -Command @('cat', "$installPath/tls/names")
    Add-Check 'renewed: from the same certificate authority, for the same names' ("$caAfter" -eq "$caBefore" -and "$namesAfter" -eq "$namesBefore") `
        "names '$namesAfter', before '$namesBefore'"
    Invoke-LabVm -Name $vm -AllowFailure -Command @('openssl', 'x509', '-in', "$installPath/tls/tls.crt", '-noout', '-checkend', "$(360 * 86400)") | Out-Null
    Add-Check 'renewed: the new certificate lasts a year' ($LASTEXITCODE -eq 0)
}

# 7b. Created again for one more name: a new certificate authority, which the installer says to distribute again.
$extraName = "alt.$($lab.HostName -replace '^[^.]+\.', '')"
$oldAuthority = Join-Path $OutputPath 'ca-before-new-names.crt'
Copy-Item $caPath $oldAuthority
$renamed = Invoke-Setup -Folder $installPath -Argument '--certificate' -Log 'setup-certificate-names.log' `
    -Settings @{ JIM_SETUP_TLS_MODE = 'generate'; JIM_SETUP_TLS_NAMES = "$hostName,$address,$extraName" }
$distribute = Get-LabSetupMessage -Line $renamed.Output -Level WARN | Where-Object { $_ -match 'ca\.crt' } | Select-Object -First 1
Add-Check "--certificate for new names works, and says to distribute the new certificate authority" ($renamed.ExitCode -eq 0 -and $distribute) `
    "exit $($renamed.ExitCode); $distribute"
Copy-LabFile -Source "${vm}:$installPath/tls/ca.crt" -Destination $caPath
if (Test-ReadyCheck 'with new names') {
    $names = Get-ServedNames
    Add-Check "with new names: JIM serves a certificate for $extraName too" ($names -match "DNS:$([regex]::Escape($extraName))\b") "$names"
    Add-Check 'with new names: the old certificate authority no longer vouches for JIM' (-not (Test-TrustedBy $oldAuthority))
}

# 7c. Replaced by an organisation's certificate: a root, an issuing CA under it, and a server certificate the issuing
# CA signs, given as the certificate followed by the issuing CA's, as the installer asks. The cell trusts the root
# alone from here, so a ready JIM proves it serves the whole chain.
$organisation = Join-Path $OutputPath 'organisation'
New-Item -ItemType Directory -Path $organisation -Force | Out-Null
$issue = @'
set -euo pipefail
cd "$1"
openssl req -x509 -newkey rsa:3072 -nodes -keyout root.key -out root.crt -days 30 -subj '/CN=JIM lab organisation root' \
    -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,keyCertSign,cRLSign'
openssl req -newkey rsa:3072 -nodes -keyout issuing.key -out issuing.csr -subj '/CN=JIM lab organisation issuing CA'
openssl x509 -req -in issuing.csr -CA root.crt -CAkey root.key -CAcreateserial -out issuing.crt -days 30 \
    -extfile <(printf 'basicConstraints=critical,CA:TRUE,pathlen:0\nkeyUsage=critical,keyCertSign,cRLSign\n')
openssl req -newkey rsa:2048 -nodes -keyout server.key -out server.csr -subj "/CN=$2"
openssl x509 -req -in server.csr -CA issuing.crt -CAkey issuing.key -CAcreateserial -out server.crt -days 90 \
    -extfile <(printf 'basicConstraints=CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectAltName=DNS:%s,IP:%s\n' "$2" "$3")
cat server.crt issuing.crt > chain.crt
openssl x509 -in server.crt -noout -serial
'@
$organisationSerial = "$($null | & bash -c $issue 'issue' $organisation $hostName $address 2>$null | Select-Object -Last 1)".Trim()
Invoke-LabVm -Name $vm -Command @('mkdir', '-p', '/root/organisation') | Out-Null
Copy-LabFile -Source (Join-Path $organisation 'chain.crt') -Destination "${vm}:/root/organisation/jim.crt"
Copy-LabFile -Source (Join-Path $organisation 'server.key') -Destination "${vm}:/root/organisation/jim.key"
$replaced = Invoke-Setup -Folder $installPath -Argument '--certificate' -Log 'setup-certificate-provided.log' -Settings @{
    JIM_SETUP_TLS_MODE = 'provided'; JIM_SETUP_TLS_CERT_FILE = '/root/organisation/jim.crt'; JIM_SETUP_TLS_KEY_FILE = '/root/organisation/jim.key'
}
Add-Check "--certificate with the organisation's certificate works" ($replaced.ExitCode -eq 0) "exit $($replaced.ExitCode)"
Copy-Item (Join-Path $organisation 'root.crt') $caPath -Force
if (Test-ReadyCheck "with the organisation's certificate" -Trusting "the organisation's root CA") {
    $served = Get-ServedCertificate
    Add-Check "JIM serves the organisation's certificate, with its issuing CA" ($served -eq $organisationSerial) "served $served, issued $organisationSerial"
}

# 7d. Asked to renew the organisation's certificate, the installer refuses, points to --certificate, and changes nothing.
$before = Get-Fingerprint
$refusedRenewal = Invoke-Setup -Folder $installPath -Argument '--renew-certificate' -Log 'setup-renew-provided.log'
$pointer = Get-LabSetupMessage -Line $refusedRenewal.Output -Level ERR | Select-Object -First 1
Add-Check "--renew-certificate refused for the organisation's certificate, pointing to --certificate" `
    ($refusedRenewal.ExitCode -ne 0 -and $pointer -match '--certificate') "exit $($refusedRenewal.ExitCode); $pointer"
Test-UnchangedCheck 'the refused renewal left every file and container as it was' $before

Test-SignInCheck 'at the end'
