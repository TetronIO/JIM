# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# The Ansible scenario, dot-sourced by Invoke-LabCell.ps1 once the VM is up and the bundle checked: JIM deployed by
# Deploying on RHEL with Ansible's own playbook, rootful or (with the page's Rootless changes) rootless, run on the VM
# itself, air-gapped, then checked as the page and the RHEL acceptance plan say. See README.md, "What an Ansible cell
# checks".

$page = 'deploying-with-ansible.md'
$work = Join-Path $OutputPath 'jim-ansible'
$playDirectory = '/root/jim-ansible'
$tlsFolder = Join-Path $OutputPath 'ansible-tls'

#region Ansible and the RHEL System Roles, from the distribution's own packages

# The cell's VM has no route to them, so a VM of the same image on the online network fetches them, with everything
# they need that the image lacks, for the cell's VM to install; then it goes.
$helperVm = "$vm-ansible"
Write-CellLog "creating $helperVm, to fetch Ansible's packages"
New-LabVm -Name $helperVm -Image $matrix.Images[$cell.Image].Image -Profile @($lab.BaseProfile, $lab.Networks['online'].Profile) `
    -MemoryGiB 2
Invoke-LabVm -Name $helperVm -Command @('bash', '-ec',
    'dnf -y -q install --downloadonly --downloaddir /root/ansible ansible-core rhel-system-roles && tar -C /root -czf /root/ansible.tar.gz ansible') `
    -AllowFailure -LogPath (Join-Path $OutputPath 'ansible-install.log') | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Ansible's packages could not be fetched; see ansible-install.log"
}
$packages = Join-Path $OutputPath 'ansible.tar.gz'
Copy-LabFile -Source "${helperVm}:/root/ansible.tar.gz" -Destination $packages
Remove-LabVm -Name $helperVm
$helperVm = $null
Copy-LabFile -Source $packages -Destination "${vm}:/root/ansible.tar.gz"
Remove-Item $packages
Invoke-LabVm -Name $vm -Command @('bash', '-ec', "tar -C /root -xzf /root/ansible.tar.gz && dnf -y -q install --disablerepo='*' /root/ansible/*.rpm") `
    -AllowFailure -LogPath (Join-Path $OutputPath 'ansible-install.log') | Out-Null
$installed = $LASTEXITCODE -eq 0
Add-Check "Ansible and the RHEL System Roles installed from $($cell.Os)'s packages" $installed
if (-not $installed) {
    throw 'stopped: Ansible did not install'
}

#endregion

#region Before you begin: JIM's images, files, certificate and secrets

# The images, into root's Podman (rootless, the jim account's), as Installing on Podman by Hand loads them. For a
# rootless JIM that needs the account, with lingering, first, as that page's rootless section creates it.
if ($rootless) {
    $account = Get-DocumentedCommand -Page 'podman-by-hand.md' -Heading 'Rootless, by hand'
    $created = Invoke-Documented -Rootful -Command $account -Title 'creating the jim account'
    Add-Check 'the jim account created, as Installing on Podman by Hand does' ($created -eq 0) "exit $created"
}
$load = (Get-DocumentedCommand -Page 'podman-by-hand.md' -Heading 'Install JIM' -Step 'Load the images') -split "`n" |
    Where-Object { $_ -match 'podman load' } | Select-Object -First 1
if (-not $load) {
    throw 'Installing on Podman by Hand no longer loads the images with podman load'
}
$loaded = Invoke-Documented -Command $load -WorkingDirectory $bundleFolder -Title 'loading the images'
Add-Check "JIM's images loaded from the bundle, as Installing on Podman by Hand does" ($loaded -eq 0) "exit $loaded"
if ($loaded -ne 0) {
    throw 'stopped: the images did not load'
}

# The playbook's folder, as Before you begin lists it: the release's Podman files, jim-config.yaml filled in, and
# JIM's certificate, from a certificate authority made for the cell as an organisation's would issue it.
$files = Join-Path $work 'files'
New-Item -ItemType Directory -Path $files -Force | Out-Null
$release = Join-Path $OutputPath 'release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
& tar -xzf $BundlePath -C $release --wildcards '*/podman/*' --strip-components=1
if ($LASTEXITCODE -ne 0) {
    throw "Could not take the Podman files out of $bundleName"
}
foreach ($file in @('jim.yaml', 'jim-database.yaml', 'quadlet/jim.network')) {
    Copy-Item (Join-Path $release 'podman' $file) $files
}
$config = ConvertTo-LabPodmanConfig -Config (Get-Content -Raw (Join-Path $release 'podman' 'jim-config.yaml')) -Setting @{
    JIM_SSO_AUTHORITY = $authority; JIM_SSO_CLIENT_ID = 'jim-web'; JIM_SSO_API_SCOPE = 'jim-api'
    JIM_SSO_INITIAL_ADMIN = '00000000-0000-0000-0000-000000000001'
}
Set-Content -LiteralPath (Join-Path $files 'jim-config.yaml') -Value $config -NoNewline
Remove-Item -Recurse $release
$issued = New-LabCertificate -Folder $tlsFolder -CaPath $caPath -Name $hostName -IpAddress $address
Copy-Item $issued.Certificate (Join-Path $files 'tls.crt')

# The secrets, in Ansible Vault, under the names the playbook reads; the vault is encrypted on the VM.
$vaultPassword = [System.Security.Cryptography.RandomNumberGenerator]::GetHexString(32, $true)
$databasePassword = [System.Security.Cryptography.RandomNumberGenerator]::GetHexString(32, $true)
$keyLines = (Get-Content $issued.Key | ForEach-Object { "  $_" }) -join "`n"
$vaultFolder = Join-Path $work 'group_vars' 'jim_servers'
New-Item -ItemType Directory -Path $vaultFolder -Force | Out-Null
"jim_db_password: `"$databasePassword`"`njim_sso_secret: `"jim-dev-secret`"`njim_tls_key: |`n$keyLines`n" |
    Set-Content (Join-Path $vaultFolder 'vault.yml') -NoNewline
Set-Content (Join-Path $work 'vault-pass') $vaultPassword
Remove-Item -Recurse $tlsFolder

# The playbook, as the page gives it, with its Rootless changes for a rootless JIM; run on the VM itself.
$playbook = Get-DocumentedCommand -Page $page -Heading 'The Playbook'
$rootlessChanges = if ($rootless) { Get-DocumentedCommand -Page $page -Heading 'Rootless' }
Set-Content (Join-Path $work 'jim.yml') (ConvertTo-LabAnsiblePlaybook -Playbook $playbook -Rootless $rootlessChanges -AirGapped)
Set-Content (Join-Path $work 'inventory') "[jim_servers]`nlocalhost ansible_connection=local"

Copy-LabFile -Source $work -Destination "${vm}:/root/" -Recurse
Invoke-LabVm -Name $vm -Command @('bash', '-ec', 'chmod 600 vault-pass && ansible-vault encrypt --vault-password-file vault-pass group_vars/jim_servers/vault.yml') `
    -WorkingDirectory $playDirectory -AllowFailure -LogPath (Join-Path $OutputPath 'ansible-vault.log') | Out-Null
$encrypted = $LASTEXITCODE -eq 0
# What stays on the lab host for inspection: the playbook and its files, not the secrets.
Remove-Item (Join-Path $work 'vault-pass'), (Join-Path $vaultFolder 'vault.yml')
if (-not $encrypted) {
    throw 'the vault could not be encrypted; see ansible-vault.log'
}

#endregion

#region Run the play, and Check it worked

# An air-gapped server with no mirror of its own: Podman and firewalld are installed, and the repositories it cannot
# reach are disabled, as Before you begin says.
Invoke-LabVm -Name $vm -Command @('bash', '-ec', "sed -i 's/^enabled=1/enabled=0/' /etc/yum.repos.d/*.repo") | Out-Null

$runLine = ((Get-Content -Raw (Join-Path $docs $page)) | Select-String '`(ansible-playbook [^`]+)`').Matches
if (-not $runLine) {
    throw "$page no longer gives an ansible-playbook command"
}
$run = ConvertTo-LabAnsibleCommand -Command $runLine[0].Groups[1].Value -Inventory 'inventory' -Playbook 'jim' -VaultPasswordFile 'vault-pass'

function Invoke-Play {
    # Runs the play as the page says, and returns its exit code and its recap for this host.
    param([string]$Log)
    Write-CellLog "running the play ($Log): $run"
    $output = Invoke-LabVm -Name $vm -Command @('bash', '-ec', $run) -WorkingDirectory $playDirectory `
        -Environment @{ ANSIBLE_NOCOLOR = '1'; ANSIBLE_FORCE_COLOR = '0' } -AllowFailure -LogPath (Join-Path $OutputPath $Log)
    $exit = $LASTEXITCODE
    $recap = @($output | Where-Object { $_ -match '^localhost\s+:\s+ok=' }) | Select-Object -Last 1
    [pscustomobject]@{ ExitCode = $exit; Recap = "$recap" }
}

$play = Invoke-Play -Log 'ansible-play.log'
Add-Check "the page's playbook ran$(if ($rootless) { ', with its Rootless changes,' }) to the end (ansible-play.log)" `
    ($play.ExitCode -eq 0 -and $play.Recap -match 'failed=0') $(if ($play.Recap) { $play.Recap.Trim() } else { "exit $($play.ExitCode)" })
if ($play.ExitCode -ne 0) {
    throw 'stopped: the play failed'
}

# Check it worked: JIM prepares its database on first start, and is ready when its health check passes.
if (Test-ReadyCheck 'deployed' -Trusting "the organisation's CA") {
    # Polled, as the page says to wait for it.
    $deadline = (Get-Date).AddSeconds(120)
    do {
        $healthy = Invoke-Guest -Action Healthy
        if ("$healthy" -eq 'True') { break }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    Add-Check "deployed: every JIM container passes its own health check, as Check it worked says" ("$healthy" -eq 'True') "$healthy"
    Test-SignInCheck 'deployed'
}
else {
    throw 'stopped: JIM did not become ready'
}

#endregion

#region What the RHEL acceptance plan checks besides

# Before the reboot below clears the journal's boot; every RHEL cell checks again as it ends (Invoke-LabCell.ps1).
Test-SelinuxCheck 'deployed'

if (-not $rootless) {
    $enabled = Invoke-LabVm -Name $vm -Command @('systemctl', 'is-enabled', 'netavark-firewalld-reload.service') -AllowFailure
    Add-Check 'deployed: the play enabled netavark-firewalld-reload.service' ("$enabled" -eq 'enabled') "$enabled"
    Invoke-LabVm -Name $vm -Command @('firewall-cmd', '--reload') | Out-Null
    if (Test-ReadyCheck 'after a firewalld reload' -Trusting "the organisation's CA") {
        Test-SignInCheck 'after a firewalld reload'
    }
}

# Run again, as the page says to after a change: with nothing changed, the play must still succeed and leave JIM up.
$again = Invoke-Play -Log 'ansible-play-again.log'
Add-Check 'the play runs again cleanly, as after a change (ansible-play-again.log)' `
    ($again.ExitCode -eq 0 -and $again.Recap -match 'failed=0') $(if ($again.Recap) { $again.Recap.Trim() } else { "exit $($again.ExitCode)" })
Test-ReadyCheck 'after the play ran again' -Trusting "the organisation's CA" | Out-Null

# A restart as Operating JIM gives it, then a host reboot, with data kept through both.
Invoke-Guest -Action Sql -Sql "CREATE SCHEMA IF NOT EXISTS jimlab; CREATE TABLE IF NOT EXISTS jimlab.marker (cell text); INSERT INTO jimlab.marker VALUES ('$CellName');" | Out-Null
# As Operating JIM gives it, from root's home: there is no /opt/jim, which only the installer makes.
$restart = (Get-DocumentedCommand -Page 'podman.md' -Heading 'Operating JIM') -split "`n" |
    Where-Object { $_ -match 'systemctl restart jim\.service' } | Select-Object -First 1
if (-not $restart) {
    throw 'Operating JIM no longer gives systemctl restart jim.service'
}
$restarted = Invoke-Documented -Command $restart -Title 'restarting JIM'
Add-Check 'restarted as Operating JIM says' ($restarted -eq 0) "exit $restarted"
if (Test-ReadyCheck 'after a restart' -Trusting "the organisation's CA") {
    $marker = Invoke-Guest -Action Sql -Sql 'SELECT cell FROM jimlab.marker;'
    Add-Check 'after a restart: JIM kept its data' ("$marker" -eq $CellName) "found '$marker'"
}

Write-CellLog 'rebooting the VM'
Restart-LabVm -Name $vm
if (Test-ReadyCheck 'after a reboot' -Trusting "the organisation's CA") {
    $marker = Invoke-Guest -Action Sql -Sql 'SELECT cell FROM jimlab.marker;'
    Add-Check "after a reboot: JIM started by itself$(if ($rootless) { ', through the jim account''s lingering' }), with its data" ("$marker" -eq $CellName) "found '$marker'"
    Test-SignInCheck 'after a reboot'
}

#endregion
