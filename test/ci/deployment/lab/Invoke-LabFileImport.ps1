# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Imports a CSV from a host folder mounted into JIM, through a File Connector Connected System, for the deployment
    lab's FileConnector cells (#2738).

.DESCRIPTION
    Through JIM's own PowerShell module, from the release JIM runs: a Connected System on the JIM File Connector
    reading -FilePath (a path under /connector-files, which the cell has mounted from a host folder), its schema
    imported, the person object type and its attributes selected with employeeId as the external ID, and a Full
    Import Run Profile, executed. Writes what it saw (the Activity's outcome) to -OutFile, and fails unless the import
    completed: a container that may not read the folder (its owner, or its SELinux label) fails the import, or the
    schema discovery before it. Run with SSL_CERT_FILE trusting the installer's certificate authority.

.EXAMPLE
    ./Invoke-LabFileImport.ps1 -Url https://10.78.0.20 -ApiKey jim_ak_... -ModulePath ./powershell/JIM/JIM.psd1 -FilePath /connector-files/hr-input/people.csv -OutFile import.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Url,
    [Parameter(Mandatory)] [string]$ApiKey,
    [Parameter(Mandatory)] [string]$ModulePath,
    [Parameter(Mandatory)] [string]$FilePath,
    [Parameter(Mandatory)] [string]$OutFile
)
$ErrorActionPreference = 'Stop'

Import-Module $ModulePath -Force
$conn = Connect-JIM -Url $Url -ApiKey $ApiKey
Write-Host "Connected: server $($conn.ServerVersion), authorised=$($conn.Authorised)"

$connector = Get-JIMConnectorDefinition | Where-Object { $_.name -eq 'JIM File Connector' } | Select-Object -First 1
if (-not $connector) { throw 'No JIM File Connector definition' }
$connectorFull = Get-JIMConnectorDefinition -Id $connector.id
$setting = { param($n) ($connectorFull.settings | Where-Object { $_.name -eq $n }).id }

$system = New-JIMConnectedSystem -Name 'Lab HR Extracts' -Description 'deployment lab host folder import (#2738)' `
    -ConnectorDefinitionId $connector.id -PassThru
Set-JIMConnectedSystem -Id $system.id -SettingValues @{
    (& $setting 'File Path')   = @{ stringValue = $FilePath }
    (& $setting 'Delimiter')   = @{ stringValue = ',' }
    (& $setting 'Object Type') = @{ stringValue = 'person' }
} | Out-Null
Write-Host "  Connected System saved, reading $FilePath"

Import-JIMConnectedSystemSchema -Id $system.id | Out-Null
$person = Get-JIMConnectedSystem -Id $system.id -ObjectTypes | Where-Object { $_.name -eq 'person' } | Select-Object -First 1
if (-not $person) { throw "The schema read from $FilePath has no person object type" }
Set-JIMConnectedSystemObjectType -ConnectedSystemId $system.id -ObjectTypeId $person.id -Selected $true | Out-Null
$updates = @{}
foreach ($attribute in $person.attributes) {
    $updates[$attribute.id] = @{ selected = $true; isExternalId = ($attribute.name -eq 'employeeId') }
}
Set-JIMConnectedSystemAttribute -ConnectedSystemId $system.id -ObjectTypeId $person.id -AttributeUpdates $updates | Out-Null
Write-Host "  Schema imported: person, $($updates.Count) attributes, employeeId the external ID"

New-JIMRunProfile -Name 'Full Import' -ConnectedSystemId $system.id -RunType FullImport | Out-Null
$runProfile = Get-JIMRunProfile -ConnectedSystemId $system.id | Where-Object { $_.name -eq 'Full Import' } | Select-Object -First 1
$result = Start-JIMRunProfile -ConnectedSystemId $system.id -RunProfileId $runProfile.id -Wait -PassThru
$activity = if ($result.activityId) { Get-JIMActivity -Id $result.activityId } else { $result }
Write-Host "  Full Import -> $($activity.status) $($activity.errorMessage)"

$record = [ordered]@{
    connectedSystemId = $system.id
    status = "$($activity.status)"
    error = "$($activity.errorMessage)"
}
$record | ConvertTo-Json | Set-Content $OutFile
$record | ConvertTo-Json
if ("$($activity.status)" -notin 'Complete', 'CompleteWithWarning', '2', '3') {
    Write-Error 'The Full Import did not complete'
    exit 1
}
