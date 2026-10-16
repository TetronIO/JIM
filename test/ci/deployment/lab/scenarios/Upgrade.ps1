# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# The Upgrade scenario, dot-sourced by Invoke-LabCell.ps1 once the VM is up and the bundle checked: install the
# previous release with its own installer, give it real data, upgrade to the bundle under test by the Upgrading guide,
# check, reboot and check again, then roll back by the guide and check the previous release is back with the data it
# had. Every step the guide gives a command for runs that command, read from the documentation (Get-DocumentedCommand);
# see README.md, "What an Upgrade cell checks".

$previousVersion = Get-BundleVersion $PreviousBundlePath
$newVersion = Get-BundleVersion $BundlePath
$previousFolder = Copy-Bundle $PreviousBundlePath
$fromSixteenOrEarlier = Test-LabVersionAtMost -Version $previousVersion -Limit '0.16.0'
$installPath = '/opt/jim'
$tab = if ($cell.Runtime -eq 'docker') { 'Docker' } else { 'Podman' }
$apiKey = 'jim_ak_' + [System.Security.Cryptography.RandomNumberGenerator]::GetHexString(64, $true)

# The JIM PowerShell module of each release, from its bundle, for the data checks.
$modules = @{ $previousVersion = (Get-BundleModule $PreviousBundlePath); $newVersion = (Get-BundleModule $BundlePath) }

function Invoke-LabData {
    # Seeds or verifies the upgrade data from the lab host, with the module of the release JIM runs; returns whether
    # it passed. Each run is its own PowerShell, as two releases' modules cannot share one.
    param([string]$Mode, [string]$Version, [string]$Tag)
    $trust = Join-Path $OutputPath 'trust.pem'
    Get-Content /etc/ssl/certs/ca-certificates.crt, $caPath | Set-Content $trust
    $dataLog = Join-Path $OutputPath "data-$Tag.log"
    $env:SSL_CERT_FILE = $trust
    $env:NO_COLOR = '1'
    try {
        $null | & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $labRoot 'Invoke-LabUpgradeData.ps1') -Mode $Mode `
            -Url "https://$address" -ApiKey $apiKey -LdapHost $network.Gateway -LdapPort $lab.Directory.Port `
            -LdapBindDn $lab.Directory.BindDn -LdapPassword $lab.Directory.Password -LdapSuffix $lab.Directory.Suffix `
            -ModulePath $modules[$Version] -OutFile (Join-Path $OutputPath "data-$Tag.json") *> $dataLog
        $LASTEXITCODE -eq 0
    }
    finally {
        Remove-Item Env:\SSL_CERT_FILE, Env:\NO_COLOR -ErrorAction SilentlyContinue
    }
}

function Get-SizedSharedBuffers {
    # What the installer would size shared_buffers to on this VM, in MB.
    $memoryKiB = [long]((Invoke-LabVm -Name $vm -Command @('awk', '/^MemTotal:/ { print $2 }', '/proc/meminfo')) | Select-Object -First 1)
    ConvertTo-LabMegabytes (Get-LabPostgresMemory -MemoryMB ([int][math]::Floor($memoryKiB / 1024))).JIM_DB_SHARED_BUFFERS
}

function Test-OwnComposeFileCheck {
    param([string]$When)
    if ($cell.Runtime -ne 'docker') {
        return
    }
    $label = Invoke-LabVm -Name $vm -AllowFailure -Command @('docker', 'inspect', '--format', '{{index .Config.Labels "io.jimlab.own-compose-file"}}', 'jim.web')
    Add-Check "${When}: the administrator's own docker-compose.local.yml is still in use" ("$label" -eq 'kept') "label '$label'"
}

function Test-RunningRelease {
    # What the upgrade guide's verification asks, plus a sign-in and the database's memory.
    param([string]$When, [string]$Version, [switch]$Sizing)
    if (-not (Test-ReadyCheck $When)) {
        return
    }
    Test-VersionCheck $When $Version
    $tag = $When -replace '\W+', '-'
    $verified = Invoke-LabData -Mode Verify -Version $Version -Tag $tag
    Add-Check "${When}: the data is all there, and the stored credential still decrypts (a Full Import binds with it)" `
        $verified $(if (-not $verified) { "see data-$tag.log" })
    Test-SignInCheck $When
    Test-OwnComposeFileCheck $When
    if ($Sizing) {
        $expected = Get-SizedSharedBuffers
        $actual = Invoke-Guest -Action Sql -Sql 'SHOW shared_buffers;'
        $actualMB = try { ConvertTo-LabMegabytes "$actual" } catch { -1 }
        Add-Check "${When}: PostgreSQL's shared_buffers sized to the host, as the installer sizes it" ($actualMB -eq $expected) `
            "$actual, expected ${expected}MB"
    }
}

function Add-AppArmorSignalRule {
    # The signal rule from Firewall, SELinux, AppArmor and NetworkManager, without the network rules the previous
    # release's installer already added.
    $block = Get-DocumentedCommand -Page 'podman.md' -Heading 'Firewall, SELinux, AppArmor and NetworkManager' -Block 2
    $signalRule = ($block -split "`n" | Where-Object { $_ -match 'abstractions/base\.d' }) -join "`n"
    if ($signalRule -notmatch 'signal peer=') {
        throw "Firewall, SELinux, AppArmor and NetworkManager no longer gives the AppArmor signal rule:`n$block"
    }
    (Invoke-Documented -Command $signalRule -Title 'the AppArmor signal rule' -Rootful) -eq 0
}

# 1. The previous release, installed with its own installer, as a customer installed it.
if ($cell.Runtime -eq 'podman' -and -not $rootless -and -not $ubuntu -and $fromSixteenOrEarlier) {
    # v0.16.0's rootful pods on AlmaLinux are unplugged by NetworkManager now and then, so that JIM never becomes
    # ready (#2009, fixed after it). A customer who met it applied the documented setting, which the upgrade's note
    # applies again later; done here first, the cell does not fail on the previous release's defect by chance.
    $networkManager = Invoke-Documented -Rootful -Title "NetworkManager left out of Podman's interfaces, for $previousVersion (#2009)" `
        -Command (Get-DocumentedCommand -Page 'podman.md' -Heading 'Firewall, SELinux, AppArmor and NetworkManager')
    if ($networkManager -ne 0) {
        throw 'Could not tell NetworkManager to leave Podman''s interfaces alone'
    }
}
$installed =(Invoke-Setup -Folder $previousFolder -Settings (New-SetupSettings @{ JIM_INFRASTRUCTURE_API_KEY = $apiKey }) `
        -Log "install-$previousVersion.log").ExitCode -eq 0
Add-Check "$previousVersion installed with its own installer" $installed $(if (-not $installed) { "see install-$previousVersion.log" })
if (-not $installed) {
    throw "stopped: $previousVersion did not install"
}
Copy-LabFile -Source "${vm}:$installPath/tls/ca.crt" -Destination $caPath

if ($cell.Runtime -eq 'docker') {
    if ($fromSixteenOrEarlier) {
        # v0.16.0's installer left JIM_INFRASTRUCTURE_API_KEY out of .env on Docker (#1950, fixed after it).
        Invoke-LabVm -Name $vm -Environment @{ KEY = $apiKey } -Command @('sh', '-ec',
            "sed -i 's|^#[[:space:]]*JIM_INFRASTRUCTURE_API_KEY=.*|JIM_INFRASTRUCTURE_API_KEY=$apiKey|' $installPath/.env; grep -q '^JIM_INFRASTRUCTURE_API_KEY=' $installPath/.env || echo ""JIM_INFRASTRUCTURE_API_KEY=`$KEY"" >> $installPath/.env") | Out-Null
        Write-CellLog "lab workaround for $previousVersion (#1950): the API key written to .env by hand"
    }
    # An administrator's own compose file beside the installer's, started once, as the upgrade guide asks: the
    # upgrade must keep starting JIM with it.
    $ownFile = Join-Path $OutputPath 'docker-compose.local.yml'
    "services:`n  jim.web:`n    labels:`n      io.jimlab.own-compose-file: `"kept`"" | Set-Content $ownFile
    Copy-LabFile -Source $ownFile -Destination "${vm}:$installPath/docker-compose.local.yml"
    $ownStarted = Invoke-Documented -WorkingDirectory $installPath -Title 'JIM with an own compose file' -Command `
        'docker compose -f docker-compose.yml -f docker-compose.production.yml -f docker-compose.local.yml --profile with-db up -d --pull never'
    Add-Check "$previousVersion started with the administrator's own docker-compose.local.yml" ($ownStarted -eq 0)
}
elseif ($ubuntu -and -not $rootless -and $fromSixteenOrEarlier) {
    # v0.16.0 on Ubuntu's rootful Podman crashes over and over without the AppArmor signal rule (#1953), which can
    # break the data set-up. The upgrade guide's note adds the rule and restarts the server; done here before the data,
    # it is the same change, made earlier.
    $added = Add-AppArmorSignalRule
    Restart-LabVm -Name $vm
    Add-Check "$previousVersion on Ubuntu rootful: the AppArmor signal rule added and the server restarted, before the data (#1953)" $added
}

if (-not (Test-ReadyCheck $previousVersion)) {
    throw "stopped: $previousVersion is not ready"
}
Test-VersionCheck $previousVersion $previousVersion
$seeded = Invoke-LabData -Mode Seed -Version $previousVersion -Tag 'seed'
Add-Check "$previousVersion given data: an LDAP Connected System with a stored credential, a Synchronisation Rule, Run Profiles run, 5 people" `
    $seeded $(if (-not $seeded) { 'see data-seed.log' })
if (-not $seeded) {
    throw 'stopped: the data could not be set up'
}
Test-SignInCheck $previousVersion

if ($cell.Runtime -eq 'docker') {
    # The installer's refusals, each of which must leave the installation exactly as it was, still running.
    $before = Get-Fingerprint
    $declined = Invoke-Setup -Folder $bundleFolder -Argument '--upgrade' -Answer 'n' -Log 'upgrade-declined.log'
    $cancelled = Get-LabSetupMessage -Line $declined.Output -Level INFO | Where-Object { $_ -match 'cancelled' } | Select-Object -First 1
    Add-Check 'an upgrade cancelled when the administrator has no backup' ([bool]$cancelled) "exit $($declined.ExitCode); $cancelled"
    Test-UnchangedCheck 'the declined upgrade left every file and container as it was' $before

    # A bundle damaged in transit (an image cut short) fails partway through, after the files are replaced.
    $broken = "$bundleFolder-broken"
    Invoke-LabVm -Name $vm -Command @('sh', '-ec', "cp -a $bundleFolder $broken && truncate -s 10M $broken/docker-images/jim-web.tar") | Out-Null
    $partway = Invoke-Setup -Folder $broken -Argument '--upgrade' -Settings @{ JIM_SETUP_BACKUP_CONFIRMED = 'true' } -Log 'upgrade-partway.log'
    Add-Check 'an upgrade from a damaged bundle fails' ($partway.ExitCode -ne 0) "exit $($partway.ExitCode)"
    $difference = @(Compare-LabFingerprint -Before $before -After (Get-Fingerprint))
    $changed = @($difference | Where-Object { $_ -like '- *' })
    Add-Check 'the failed upgrade put every file back, and JIM runs as it did' ($changed.Count -eq 0) "$($changed | Select-Object -First 4)"
    # Copies it made on the way, such as .previous files, would replace those an earlier upgrade left for rolling back.
    $added = @($difference | Where-Object { $_ -like '+ *' })
    Add-Check 'the failed upgrade left nothing of its own behind' ($added.Count -eq 0) "$($added | Select-Object -First 4)"
    if (Test-ReadyCheck 'after the failed upgrade') {
        Test-VersionCheck 'after the failed upgrade' $previousVersion
    }
    Invoke-LabVm -Name $vm -Command @('rm', '-rf', $broken) | Out-Null

    # An edit to a compose file the upgrade replaces would be lost, so it stops the upgrade; a release before the
    # installer recorded its compose files cannot tell, and says so instead.
    Invoke-LabVm -Name $vm -AllowFailure -Command @('test', '-f', "$installPath/compose-files.sha256") | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Invoke-LabVm -Name $vm -Command @('sh', '-ec', "cp -p $installPath/docker-compose.yml /root/docker-compose.yml.kept && echo '# edited by hand' >> $installPath/docker-compose.yml") | Out-Null
        $edited = Get-Fingerprint
        $refused = Invoke-Setup -Folder $bundleFolder -Argument '--upgrade' -Settings @{ JIM_SETUP_BACKUP_CONFIRMED = 'true' } -Log 'upgrade-edited.log'
        Add-Check 'an upgrade refused over a compose file edited by hand' ($refused.ExitCode -ne 0) "$(Get-LabSetupMessage -Line $refused.Output -Level ERR | Select-Object -First 1)"
        Test-UnchangedCheck 'the refused upgrade left every file and container as it was' $edited
        Invoke-LabVm -Name $vm -Command @('cp', '-p', '/root/docker-compose.yml.kept', "$installPath/docker-compose.yml") | Out-Null
    }
    else {
        Write-CellLog "$previousVersion keeps no record of its compose files as installed, so an edited one cannot be refused"
    }
}

# 2. Before upgrading: stop JIM, leaving the database running, and back up the database and the encryption keys by
# Backup & Disaster Recovery.
$stop = if ($cell.Runtime -eq 'docker') {
    Get-DocumentedCommand -Page 'upgrading.md' -Heading 'Upgrading a connected deployment' -Step 'Stop the services'
}
else {
    Get-DocumentedCommand -Page 'upgrading.md' -Heading 'Upgrading on Podman' -Step 'Stop JIM'
}
Add-Check 'JIM stopped, as the upgrading guide says' ((Invoke-Documented -Command $stop -WorkingDirectory $installPath -Title 'stopping JIM') -eq 0)
Invoke-LabVm -Name $vm -Command @('mkdir', '-p', '/root/backup') | Out-Null
$backups = @(
    @{ Title = 'the database backup'; Command = (Get-DocumentedCommand -Page 'backup-recovery.md' -Heading '1. Back up the database' -Tab $tab) }
    @{ Title = 'the encryption key backup'; Command = (Get-DocumentedCommand -Page 'backup-recovery.md' -Heading '2. Back up the encryption keys' -Tab $tab) }
    @{ Title = 'the key backup''s check'; Command = (Get-DocumentedCommand -Page 'backup-recovery.md' -Heading '2. Back up the encryption keys') }
)
foreach ($backup in $backups) {
    Add-Check "$($backup.Title), as documented" ((Invoke-Documented -Command $backup.Command -WorkingDirectory /root/backup -Title $backup.Title) -eq 0)
}

# 3. The upgrade, by the Upgrading guide.
if ($cell.Runtime -eq 'docker') {
    # Run as root already: without sudo, which would drop the backup confirmation given in advance.
    $upgrade = (Get-DocumentedCommand -Page 'upgrading.md' -Heading 'Upgrading with the installer' -Tab 'Air-gapped') -replace '(?m)^sudo\s+', ''
    $upgradeCommand = "export JIM_SETUP_BACKUP_CONFIRMED=true`n$upgrade"
    $upgraded = Invoke-Documented -Command $upgradeCommand -WorkingDirectory $bundleFolder -Title 'the upgrade, with the installer'
    Add-Check "upgraded to $newVersion with setup.sh --upgrade" ($upgraded -eq 0)
}
else {
    $page = 'upgrading.md'
    $heading = 'Upgrading on Podman'
    Add-Check '2. the current pod files kept, for rolling back' ((Invoke-Documented -Title 'keeping the pod files' -WorkingDirectory $installPath `
                -Command (Get-DocumentedCommand -Page $page -Heading $heading -Step 'Keep the current pod files')) -eq 0)
    Add-Check "3. $newVersion's images loaded and its files put in place, air-gapped" ((Invoke-Documented -Title 'the new release''s files and images' `
                -WorkingDirectory $bundleFolder -Command (Get-DocumentedCommand -Page $page -Heading $heading `
                    -Step 'Put the new release''s files in place, and its images.' -Block 2)) -eq 0)
    $restartServer = $false
    if ($fromSixteenOrEarlier) {
        # The guide's notes for upgrading from v0.16.0 or earlier.
        $memoryKiB = [long]((Invoke-LabVm -Name $vm -Command @('awk', '/^MemTotal:/ { print $2 }', '/proc/meminfo')) | Select-Object -First 1)
        $sized = Get-LabPostgresMemory -MemoryMB ([int][math]::Floor($memoryKiB / 1024))
        $lines = ($sized.Keys | Where-Object { $_ -ne 'JIM_DB_SHM_SIZE' } | ForEach-Object { "  ${_}: `"$($sized[$_])`"" }) -join "`n"
        Invoke-LabVm -Name $vm -Command @('sh', '-ec', "printf '%s\n' `"`$0`" >> $installPath/jim-config.yaml", $lines) | Out-Null
        Add-Check "3. (from $previousVersion) PostgreSQL's memory added to jim-config.yaml, sized as the installer would ($($sized.JIM_DB_SHARED_BUFFERS))" ($LASTEXITCODE -eq 0)
        if (-not $rootless -and -not $ubuntu) {
            # firewalld and NetworkManager, on RHEL and its derivatives (#2009).
            $netavark = Invoke-Documented -Rootful -Title 'netavark-firewalld-reload.service (Firewall, SELinux, AppArmor and NetworkManager)' `
                -Command 'systemctl enable --now netavark-firewalld-reload.service'
            Add-Check "3. (from $previousVersion) netavark-firewalld-reload.service enabled" ($netavark -eq 0)
            $networkManager = Invoke-Documented -Rootful -Title 'NetworkManager left out of Podman''s interfaces' `
                -Command (Get-DocumentedCommand -Page 'podman.md' -Heading 'Firewall, SELinux, AppArmor and NetworkManager')
            Add-Check "3. (from $previousVersion) NetworkManager told to leave Podman's interfaces alone" ($networkManager -eq 0)
        }
        if (-not $rootless -and $ubuntu) {
            Add-Check "3. (from $previousVersion) the AppArmor signal rule in place" (Add-AppArmorSignalRule)
            $restartServer = $true
        }
    }
    if ($restartServer) {
        # The note: restart the server instead of the two services, for Podman to load the rule.
        Restart-LabVm -Name $vm
        Add-Check '4. the server restarted instead of the two services, as the note for Ubuntu says' $true
    }
    else {
        Add-Check "4. JIM started on $newVersion" ((Invoke-Documented -Title 'starting JIM' -WorkingDirectory $installPath `
                    -Command (Get-DocumentedCommand -Page $page -Heading $heading -Step 'Start JIM')) -eq 0)
    }
}

# 4. Verified, then again after a reboot.
Test-RunningRelease 'upgraded' $newVersion -Sizing
if ($cell.Runtime -eq 'docker') {
    # Running the installer again, or the previous release's, changes nothing.
    $before = Get-Fingerprint
    $same = Invoke-Setup -Folder $bundleFolder -Argument '--upgrade' -Settings @{ JIM_SETUP_BACKUP_CONFIRMED = 'true' } -Log 'upgrade-same.log'
    Add-Check "the same upgrade again says there is nothing to upgrade" ($same.ExitCode -eq 0 -and
        (Get-LabSetupMessage -Line $same.Output -Level OK) -match 'nothing to upgrade') "exit $($same.ExitCode)"
    $older = Invoke-Setup -Folder $previousFolder -Argument '--upgrade' -Settings @{ JIM_SETUP_BACKUP_CONFIRMED = 'true' } -Log 'upgrade-downgrade.log'
    Add-Check "$previousVersion's installer refuses to downgrade" ($older.ExitCode -ne 0) "exit $($older.ExitCode)"
    Test-UnchangedCheck 'both left every file and container as it was' $before
}
Write-CellLog 'rebooting the VM'
Restart-LabVm -Name $vm
Test-RunningRelease 'upgraded, after a reboot' $newVersion -Sizing

# 5. Rolled back by the guide: the previous release, on the database and keys it had before the upgrade.
Invoke-Guest -Action Sql -Sql 'CREATE SCHEMA IF NOT EXISTS jimlab; CREATE TABLE IF NOT EXISTS jimlab.after_upgrade (written timestamptz DEFAULT now()); INSERT INTO jimlab.after_upgrade DEFAULT VALUES;' | Out-Null
$restoreKeys = Get-DocumentedCommand -Page 'backup-recovery.md' -Heading 'Restoring' -Tab $tab
$restoreDatabase = Get-DocumentedCommand -Page 'backup-recovery.md' -Heading 'Restoring' -Step 'Restore the database' -Tab $tab
if ($cell.Runtime -eq 'docker') {
    $page = 'upgrading.md'
    Add-Check 'rollback 1: services stopped' ((Invoke-Documented -Title 'stopping the services' -WorkingDirectory $installPath `
                -Command (Get-DocumentedCommand -Page $page -Heading 'Rolling back' -Step 'Stop the services:')) -eq 0)
    Add-Check 'rollback 2: encryption keys restored, as documented' ((Invoke-Documented -Title 'the key restore' -WorkingDirectory /root/backup -Command $restoreKeys) -eq 0)
    Add-Check 'rollback 2: database restored into an empty one, as documented' ((Invoke-Documented -Title 'the database restore' -WorkingDirectory /root/backup -Command $restoreDatabase) -eq 0)
    Add-Check 'rollback 3: the installer''s .previous files put back' ((Invoke-Documented -Title 'the .previous files' `
                -Command (Get-DocumentedCommand -Page $page -Heading 'Rolling back' -Step 'Point `JIM_VERSION` back' -Block 2)) -eq 0)
    # With every compose file JIM was started with, the administrator's own included, as the step says.
    $start = Get-DocumentedCommand -Page $page -Heading 'Rolling back' -Step 'Start the services'
    $startWithOwnFile = $start -replace '-f docker-compose\.production\.yml', '-f docker-compose.production.yml -f docker-compose.local.yml'
    if ($startWithOwnFile -eq $start) {
        throw 'Rolling back''s start command no longer names docker-compose.production.yml, to add the own compose file beside'
    }
    Add-Check 'rollback 4: started with every compose file' ((Invoke-Documented -Title 'starting the services' -Command $startWithOwnFile) -eq 0)
}
else {
    # On Podman the guide gives the rollback in words: stop JIM, restore the backup, put the .previous pod files back,
    # and start JIM.
    Add-Check 'rollback: JIM stopped' ((Invoke-Documented -Title 'stopping JIM' -WorkingDirectory $installPath `
                -Command (Get-DocumentedCommand -Page 'upgrading.md' -Heading 'Upgrading on Podman' -Step 'Stop JIM')) -eq 0)
    Add-Check 'rollback: encryption keys restored, as documented' ((Invoke-Documented -Title 'the key restore' -WorkingDirectory /root/backup -Command $restoreKeys) -eq 0)
    Add-Check 'rollback: database restored into an empty one, as documented' ((Invoke-Documented -Title 'the database restore' -WorkingDirectory /root/backup -Command $restoreDatabase) -eq 0)
    Add-Check 'rollback: the .previous pod files put back' ((Invoke-Documented -Title 'the .previous pod files' -WorkingDirectory $installPath `
                -Command 'cp jim.yaml.previous jim.yaml && cp jim-database.yaml.previous jim-database.yaml') -eq 0)
    Add-Check 'rollback: JIM started' ((Invoke-Documented -Title 'starting JIM' -Command 'systemctl start jim.service') -eq 0)
}
Test-RunningRelease 'rolled back' $previousVersion
$left = Invoke-Guest -Action Sql -Sql "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'jimlab' AND table_name = 'after_upgrade';"
Add-Check 'rolled back: what was written after the upgrade is gone' ("$left" -eq '0') "found $left"
