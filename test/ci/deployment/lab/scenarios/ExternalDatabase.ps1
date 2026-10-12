# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# The ExternalDatabase scenario, dot-sourced by Invoke-LabCell.ps1 once the VM is up and the bundle checked: JIM
# installed against a PostgreSQL server of the customer's own, prepared as Before You Install says, on a VM of its own
# on the cell's network. See README.md, "What an ExternalDatabase cell checks".

$installPath = '/opt/jim'
$server = $lab.DatabaseServer
$postgres = $server.PostgresVersion
# Invoke-LabCell.ps1 sends JIM's database queries to this VM, and deletes it with the cell's.
$databaseVm = "$vm-db"
$databasePassword = [System.Security.Cryptography.RandomNumberGenerator]::GetHexString(32, $true)
$subnet = ($network.Gateway -replace '\.\d+$', '.0') + '/24'

# 1. The database server, as a customer's would be: PostgreSQL from the PostgreSQL project's packages, listening on the
# network and accepting JIM's user from the cell's; JIM's user and database created as the documentation says.
Write-CellLog "creating the database server $databaseVm, with PostgreSQL $postgres"
New-LabVm -Name $databaseVm -Image $server.Image -Profile @($lab.BaseProfile, $lab.Networks['online'].Profile) `
    -MemoryGiB $cell.DatabaseMemoryGiB
$prepare = @"
export DEBIAN_FRONTEND=noninteractive
apt-get install -y -q postgresql-common
/usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y
apt-get install -y -q postgresql-$postgres
pg_conftool $postgres main set listen_addresses '*'
echo 'host jim jim $subnet scram-sha-256' >> /etc/postgresql/$postgres/main/pg_hba.conf
systemctl restart postgresql@$postgres-main
"@
Invoke-LabVm -Name $databaseVm -Command @('bash', '-ec', $prepare) -LogPath (Join-Path $OutputPath 'database-server.log') | Out-Null

$documented = Get-DocumentedCommand -Page 'deployment.md' -Heading 'Before You Install'
if ($documented -notmatch 'your_secure_password') {
    throw "Before You Install no longer gives JIM's user a password to replace:`n$documented"
}
Write-CellLog "running Before You Install's SQL as postgres, with its password replaced:"
$documented -split "`n" | ForEach-Object { Write-CellLog "    $_" }
$sqlPath = Join-Path $OutputPath 'database.sql'
$documented.Replace('your_secure_password', $databasePassword) | Set-Content $sqlPath
Copy-LabFile -Source $sqlPath -Destination "${databaseVm}:/tmp/jim.sql"
Remove-Item $sqlPath
Invoke-LabVm -Name $databaseVm -AllowFailure -LogPath (Join-Path $OutputPath 'database-server.log') -Command @('sh', '-c',
    'chmod 644 /tmp/jim.sql && runuser -u postgres -- psql -v ON_ERROR_STOP=1 -f /tmp/jim.sql; code=$?; rm -f /tmp/jim.sql; exit $code') | Out-Null
$prepared = $LASTEXITCODE -eq 0
Add-Check "PostgreSQL $postgres prepared on a server of its own, as Before You Install says" $prepared
if (-not $prepared) {
    throw 'stopped: the database server could not be prepared'
}
if ($cell.Network -ne 'online') {
    Set-LabVmProfile -Name $databaseVm -Profile @($lab.BaseProfile, $network.Profile)
}
$databaseAddress = Get-LabVmAddress -Name $databaseVm -Gateway $network.Gateway
Write-CellLog "the database server is at $databaseAddress"

# 2. JIM, installed against it.
$settings = New-SetupSettings @{
    JIM_SETUP_DB_MODE = 'external'; JIM_DB_HOSTNAME = $databaseAddress; JIM_DB_NAME = 'jim'; JIM_DB_USERNAME = 'jim'
    JIM_DB_PASSWORD = $databasePassword
}
$installed = (Invoke-Setup -Folder $bundleFolder -Settings $settings -Log 'setup-install.log').ExitCode -eq 0
Add-Check 'installed with setup.sh against the external database' $installed
if (-not $installed) {
    throw 'stopped: JIM did not install'
}
Copy-LabFile -Source "${vm}:$installPath/tls/ca.crt" -Destination $caPath

function Test-NoBundledDatabaseCheck {
    param([string]$When)
    $databases = @(Get-Fingerprint | Where-Object { $_ -match '^jim[.-]database' })
    Add-Check "${When}: no bundled database runs" ($databases.Count -eq 0) "$databases"
}

Test-NoBundledDatabaseCheck 'installed'
$tables = Invoke-Guest -Action Sql -Sql "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tableowner = 'jim';"
Add-Check "installed: JIM created its tables in the external database" ([int]"$tables" -gt 0) "$tables tables"
if (-not (Test-ReadyCheck 'installed')) {
    throw 'stopped: JIM is not ready'
}
Test-SignInCheck 'installed'

# 3. A host reboot: JIM starts by itself, against its database.
Invoke-Guest -Action Sql -Sql "CREATE SCHEMA IF NOT EXISTS jimlab; CREATE TABLE IF NOT EXISTS jimlab.marker (cell text); INSERT INTO jimlab.marker VALUES ('$CellName');" | Out-Null
Write-CellLog 'rebooting the VM'
Restart-LabVm -Name $vm
if (Test-ReadyCheck 'after a reboot') {
    $marker = Invoke-Guest -Action Sql -Sql 'SELECT cell FROM jimlab.marker;'
    Add-Check 'after a reboot: JIM started by itself, with its data' ("$marker" -eq $CellName) "found '$marker'"
    Test-SignInCheck 'after a reboot'
}

# 4. Started again as the documentation says for a server of the customer's own: no bundled database comes with it.
Add-Check 'JIM started again as documented, for a database of its own' ((Invoke-DocumentedStart) -eq 0 -and (Wait-Ready) -ge 0)
Test-NoBundledDatabaseCheck 'started again'
