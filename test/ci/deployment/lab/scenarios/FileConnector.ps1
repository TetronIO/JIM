# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# The FileConnector scenario, dot-sourced by Invoke-LabCell.ps1 once the VM is up and the bundle checked: a host folder
# mounted into JIM for the File Connector, as the File Connector guide's On Podman says, on a host with SELinux
# enforcing, then imported from through a Connected System. See README.md, "What a FileConnector cell checks".

$installPath = '/opt/jim'
$connectorPage = Join-Path '..' 'connectors' 'jim-file-connector.md'
$apiKey = 'jim_ak_' + [System.Security.Cryptography.RandomNumberGenerator]::GetHexString(64, $true)
$people = 3

# 1. JIM, installed with setup.sh, with an API key for the import run from the lab host.
$installed = (Invoke-Setup -Folder $bundleFolder -Settings (New-SetupSettings @{ JIM_INFRASTRUCTURE_API_KEY = $apiKey }) `
    -Log 'setup-install.log').ExitCode -eq 0
Add-Check "installed with setup.sh$(if ($rootless) { ' --rootless' })" $installed
if (-not $installed) {
    throw 'stopped: JIM did not install'
}
Copy-LabFile -Source "${vm}:$installPath/tls/ca.crt" -Destination $caPath
if (-not (Test-ReadyCheck 'installed')) {
    throw 'stopped: JIM is not ready'
}

# 2. The host folder, as the guide's On Podman says: the folder in its pod file snippet, holding a CSV as an HR system
# would leave it (written by root, as such a job usually runs), handed to the container's user, and labelled.
$podFile = "$installPath/jim.yaml"
$pod = (Invoke-LabVm -Name $vm -Command @('cat', $podFile)) -join "`n"
$mounted = ConvertTo-LabPodWithHostFolder -Pod $pod -Snippet (Get-DocumentedCommand -Page $connectorPage -Heading 'On Podman' -Block 1)
$csv = "employeeId,firstName,lastName,email`n" + ((1..$people | ForEach-Object { "F$_,Lab,Person$_,person$_@jimlab.test" }) -join "`n")
Invoke-LabVm -Name $vm -Command @('bash', '-ec', "mkdir -p '$($mounted.HostPath)' && printf '%s\n' '$csv' > '$($mounted.HostPath)/people.csv'") | Out-Null
if ($rootless) {
    # Ownership, when rootless: from within the jim account's user namespace, as the guide gives it.
    $owned = Invoke-Documented -Command (Get-DocumentedCommand -Page $connectorPage -Heading 'On Podman' -Block 2) -Title 'the folder handed to the rootless container user'
}
else {
    # A rootful JIM needs the folder owned by UID 1654, as on Docker (Permissions on bind-mounted paths).
    $owned = Invoke-Documented -Rootful -Command "chown 1654:1654 '$($mounted.HostPath)'" -Title 'the folder handed to UID 1654'
}
Add-Check "the folder $($mounted.HostPath) handed to JIM's container user, as the guide says" ($owned -eq 0) "exit $owned"
$labelled = Invoke-Documented -Rootful -Command (Get-DocumentedCommand -Page $connectorPage -Heading 'On Podman' -Block 3) -Title 'the folder labelled for containers'
Add-Check 'the folder labelled container_file_t, as the guide says' ($labelled -eq 0) "exit $labelled"

# 3. Mounted in the pod file, then JIM restarted, as the guide says.
$edited = Join-Path $OutputPath 'jim.yaml'
$mounted.Pod | Set-Content $edited
Copy-LabFile -Source $edited -Destination "${vm}:$podFile"
$restart = (Get-DocumentedCommand -Page 'podman.md' -Heading 'Operating JIM') -split "`n" |
    Where-Object { $_ -match 'systemctl restart jim\.service' } | Select-Object -First 1
if (-not $restart) {
    throw 'Operating JIM no longer gives systemctl restart jim.service'
}
$restarted = Invoke-Documented -Command $restart -Title 'restarting JIM with the folder mounted'
Add-Check "restarted with the folder mounted at $($mounted.MountPath)" ($restarted -eq 0) "exit $restarted"
if (-not (Test-ReadyCheck 'with the folder mounted')) {
    throw 'stopped: JIM is not ready with the folder mounted'
}

# 4. A File Connector Connected System reading the CSV in the folder, and a Full Import, through JIM's own PowerShell
# module, from the lab host.
$trust = Join-Path $OutputPath 'trust.pem'
Get-Content /etc/ssl/certs/ca-certificates.crt, $caPath | Set-Content $trust
$env:SSL_CERT_FILE = $trust
$env:NO_COLOR = '1'
try {
    $null | & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $labRoot 'Invoke-LabFileImport.ps1') `
        -Url "https://$address" -ApiKey $apiKey -ModulePath (Get-BundleModule $BundlePath) `
        -FilePath "$($mounted.MountPath)/people.csv" -OutFile (Join-Path $OutputPath 'file-import.json') *> (Join-Path $OutputPath 'file-import.log')
    $imported = $LASTEXITCODE -eq 0
}
finally {
    Remove-Item Env:\SSL_CERT_FILE, Env:\NO_COLOR -ErrorAction SilentlyContinue
}
Add-Check 'a File Connector Full Import of the folder''s CSV completes (file-import.log)' $imported
if ($imported) {
    $count = Invoke-Guest -Action Sql -Sql 'SELECT count(*) FROM "ConnectedSystemObjects";'
    Add-Check "every person in the CSV imported ($people)" ("$count" -eq "$people") "found $count"
}
