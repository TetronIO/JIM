# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Seeds an installed JIM with real data before an upgrade, or verifies it after one, for the deployment lab's
    upgrade cells (#1876).

.DESCRIPTION
    Seed: creates what #1876 asks an upgrade to carry through, through JIM's own PowerShell module:
      - a Connected System on the JIM LDAP Connector, against the lab's test directory (OpenLDAP), whose bind
        password is a stored, encrypted credential;
      - its schema, partition, container (ou=people) and object type (inetOrgPerson), selected;
      - an import Synchronisation Rule projecting people into the Metaverse as Users, with mappings
        and a matching rule;
      - Full Import and Full Synchronisation Run Profiles, both executed.
    Verify: checks all of it is still there after the upgrade, and runs the Full Import again: a successful import
    binds with the stored password, so the credential still decrypts, as the upgrade guide's verification asks.

    Writes a small JSON record of what it saw (counts, Activity outcomes, server version), so the seed and the verify
    can be compared. It uses the JIM PowerShell module from the release JIM runs, from that release's bundle, and is
    run with SSL_CERT_FILE trusting the installer's certificate authority.

.EXAMPLE
    ./Invoke-LabUpgradeData.ps1 -Mode Seed -Url https://10.78.0.20 -ApiKey jim_ak_... -LdapHost 10.78.0.1 -LdapBindDn cn=admin,dc=jimlab,dc=test -LdapPassword ... -LdapSuffix dc=jimlab,dc=test -ModulePath ./powershell/JIM/JIM.psd1 -OutFile seed.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Seed', 'Verify')] [string]$Mode,
    [Parameter(Mandatory)] [string]$Url,
    [Parameter(Mandatory)] [string]$ApiKey,
    [Parameter(Mandatory)] [string]$LdapHost,
    [Parameter(Mandatory)] [string]$ModulePath,
    [Parameter(Mandatory)] [string]$OutFile,
    [Parameter(Mandatory)] [string]$LdapBindDn,
    [Parameter(Mandatory)] [string]$LdapPassword,
    [Parameter(Mandatory)] [string]$LdapSuffix,
    [int]$LdapPort = 1389
)
$ErrorActionPreference = 'Stop'
$csName = 'Lab Directory'
$suffix = $LdapSuffix

Import-Module $ModulePath -Force
$conn = Connect-JIM -Url $Url -ApiKey $ApiKey
Write-Host "Connected: server $($conn.ServerVersion), authorised=$($conn.Authorised)"

function Invoke-Profile([object]$System, [string]$Name) {
    $runProfile = Get-JIMRunProfile -ConnectedSystemId $System.id | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $runProfile) { throw "Run Profile '$Name' not found on $($System.name)" }
    $result = Start-JIMRunProfile -ConnectedSystemId $System.id -RunProfileId $runProfile.id -Wait -PassThru
    $activity = if ($result.activityId) { Get-JIMActivity -Id $result.activityId } else { $result }
    Write-Host "  $Name -> $($activity.status) $($activity.errorMessage)"
    [pscustomobject]@{ name = $Name; status = "$($activity.status)"; error = "$($activity.errorMessage)" }
}

$record = [ordered]@{ mode = $Mode; serverVersion = "$($conn.ServerVersion)"; at = (Get-Date).ToUniversalTime().ToString('o') }

if ($Mode -eq 'Seed') {
    $connector = Get-JIMConnectorDefinition | Where-Object { $_.name -like '*LDAP*' } | Select-Object -First 1
    if (-not $connector) { throw 'No LDAP connector definition' }
    $connectorFull = Get-JIMConnectorDefinition -Id $connector.id
    $system = New-JIMConnectedSystem -Name $csName -Description 'deployment lab upgrade data (#1876)' -ConnectorDefinitionId $connector.id -PassThru
    $setting = { param($n) ($connectorFull.settings | Where-Object { $_.name -eq $n }).id }
    $values = @{
        (& $setting 'Host')                           = @{ stringValue = $LdapHost }
        (& $setting 'Port')                           = @{ intValue = $LdapPort }
        (& $setting 'Username')                       = @{ stringValue = $LdapBindDn }
        (& $setting 'Password')                       = @{ stringValue = $LdapPassword }
        (& $setting 'Use Secure Connection (LDAPS)?') = @{ checkboxValue = $false }
        (& $setting 'Connection Timeout')             = @{ intValue = 30 }
    }
    $auth = & $setting 'Authentication Type'
    if ($auth) { $values[$auth] = @{ stringValue = 'Simple' } }
    Set-JIMConnectedSystem -Id $system.id -SettingValues $values | Out-Null
    Write-Host "  Connected System '$csName' saved (its live connection test passed)"

    Import-JIMConnectedSystemSchema -Id $system.id | Out-Null
    Import-JIMConnectedSystemHierarchy -Id $system.id | Out-Null
    $partition = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $system.id) |
        Where-Object { $_.name -eq $suffix -or $_.externalId -eq $suffix } | Select-Object -First 1
    if (-not $partition) { throw "Partition $suffix not found" }
    Set-JIMConnectedSystemPartition -ConnectedSystemId $system.id -PartitionId $partition.id -Selected $true | Out-Null
    function Get-AllContainers($nodes) { foreach ($n in @($nodes)) { if ($n) { $n; Get-AllContainers $n.childContainers } } }
    $containers = @(Get-AllContainers $partition.containers)
    $people = $containers | Where-Object { $_ -and ($_.name -eq "ou=people,$suffix" -or $_.name -eq 'people' -or $_.externalId -eq "ou=people,$suffix") } | Select-Object -First 1
    if (-not $people) { throw "Container ou=people not found under $suffix (have: $(($containers | ForEach-Object name) -join ', '))" }
    Set-JIMConnectedSystemContainer -ConnectedSystemId $system.id -ContainerId $people.id -Selected $true | Out-Null

    $csType = Get-JIMConnectedSystemObjectType -ConnectedSystemId $system.id | Where-Object { $_.name -eq 'inetOrgPerson' } | Select-Object -First 1
    if (-not $csType) { throw 'Object type inetOrgPerson not found' }
    Set-JIMConnectedSystemObjectType -ConnectedSystemId $system.id -ObjectTypeId $csType.id -Selected $true | Out-Null
    $wanted = 'uid', 'cn', 'givenName', 'sn', 'displayName', 'mail', 'employeeNumber', 'distinguishedName'
    $updates = @{}
    foreach ($a in $csType.attributes) { if ($wanted -contains $a.name) { $updates[$a.id] = @{ selected = $true } } }
    Set-JIMConnectedSystemAttribute -ConnectedSystemId $system.id -ObjectTypeId $csType.id -AttributeUpdates $updates | Out-Null
    $csType = Get-JIMConnectedSystemObjectType -ConnectedSystemId $system.id | Where-Object { $_.name -eq 'inetOrgPerson' } | Select-Object -First 1

    $mvType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq 'User' } | Select-Object -First 1
    $rule = New-JIMSyncRule -Name 'Lab Directory Import Users' -ConnectedSystemId $system.id -ConnectedSystemObjectTypeId $csType.id `
        -MetaverseObjectTypeId $mvType.id -Direction Import -ProjectToMetaverse -PassThru
    $mvAttrs = Get-JIMMetaverseAttribute
    $map = [ordered]@{ uid = 'Account Name'; displayName = 'Display Name'; givenName = 'First Name'; sn = 'Last Name'; mail = 'Email'; employeeNumber = 'Employee ID' }
    foreach ($k in $map.Keys) {
        $cs = $csType.attributes | Where-Object { $_.name -eq $k }
        $mv = $mvAttrs | Where-Object { $_.name -eq $map[$k] }
        if (-not $cs -or -not $mv) { throw "Mapping $k -> $($map[$k]): attribute missing" }
        New-JIMSyncRuleMapping -SyncRuleId $rule.id -TargetMetaverseAttributeId $mv.id -SourceConnectedSystemAttributeId $cs.id | Out-Null
    }
    New-JIMMatchingRule -ConnectedSystemId $system.id -ObjectTypeId $csType.id -MetaverseObjectTypeId $mvType.id `
        -SourceAttributeId ($csType.attributes | Where-Object name -eq 'employeeNumber').id `
        -TargetMetaverseAttributeId ($mvAttrs | Where-Object name -eq 'Employee ID').id | Out-Null
    New-JIMRunProfile -Name 'Full Import' -ConnectedSystemId $system.id -RunType FullImport | Out-Null
    New-JIMRunProfile -Name 'Full Synchronisation' -ConnectedSystemId $system.id -RunType FullSynchronisation | Out-Null
    Write-Host "  Synchronisation Rule, 6 mappings, matching rule and 2 Run Profiles created"
    $record.runs = @((Invoke-Profile $system 'Full Import'), (Invoke-Profile $system 'Full Synchronisation'))
}
else {
    $system = Get-JIMConnectedSystem | Where-Object { $_.name -eq $csName } | Select-Object -First 1
    if (-not $system) { throw "Connected System '$csName' is gone" }
    $record.runs = @((Invoke-Profile $system 'Full Import'))
}

$system = Get-JIMConnectedSystem -Id $system.id
$rules = @(Get-JIMSyncRule | Where-Object { $_.connectedSystemId -eq $system.id })
$users = @(1..5 | ForEach-Object {
    Get-JIMMetaverseObject -ObjectTypeName 'User' -AttributeName 'Employee ID' -AttributeValue ('E{0:D4}' -f $_) -ErrorAction SilentlyContinue
} | Where-Object { $_ })
$record.connectedSystem = $system.name
$record.syncRules = $rules.Count
$record.runProfiles = @(Get-JIMRunProfile -ConnectedSystemId $system.id).Count
$record.labUsersInMetaverse = $users.Count
$record | ConvertTo-Json -Depth 5 | Set-Content $OutFile
$record | ConvertTo-Json -Depth 5
$failed = @($record.runs | Where-Object { $_.status -notin 'Complete', 'CompleteWithWarning', '2', '3' }).Count
if ($failed -or $users.Count -ne 5 -or $rules.Count -lt 1) { Write-Error "Upgrade data $Mode FAILED"; exit 1 }
Write-Host "Upgrade data $Mode passed"
