# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 040: the Microsoft Entra ID Connector reading a tenant of up to a million objects, from a stand-in
    Microsoft Graph service

.DESCRIPTION
    Measures JIM reading a large tenant through the Microsoft Entra ID Connector (#3053; Phase 5 of
    engineering/plans/doing/ENTRA_ID_CONNECTOR_INTEGRATION_SCENARIOS.md), at every template up to Scale1m80Groups.

    The tenant is the stand-in Graph service (test/JIM.TestGraphService, the simulated tenant the unit suite drives in
    process), which Run-IntegrationTests.ps1 starts for this scenario only. On the test network it answers for
    login.microsoftonline.com and graph.microsoft.com, and its certificate is added to JIM's Trusted Certificates, so
    JIM runs unchanged. It measures reading only: the Connector writes no faster than Graph's 20 writes a second per
    application whatever answers it, so writing at scale is Scenario 039's, against the real tenant.

    The stand-in is seeded in process with Scenario 039's population for the template (a million users cannot be
    created through Graph in any time worth waiting), then:

      1  Full Import      A Full Import and Full Synchronisation project every user and group (Scenario 037's
                          configuration); a sample of people is read back from the Metaverse.
      2  Delta Import     The stand-in retitles a tenth of the people, moves a tenth between teams and deletes a
                          hundredth; a Delta Import and Delta Synchronisation bring the Metaverse in line.
      3  Delta integrity  A Delta Import with nothing changed reads nothing.
      4  Full Import      A Full Import and Full Synchronisation of the whole tenant again, which change nothing.

    The steps' timings are written to results/scenario-040-<template>-<time>.json and printed. It needs no
    credentials and no directory, and runs only when asked for by number.

.PARAMETER Template
    The size: Get-TemplateScale's users, groups and average memberships.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario 40 -Template Scale1m80Groups -DirectoryType OpenLDAP
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing progress output, coloured in the repository style.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Step, Domain, WaitSeconds, SkipPopulate and DirectoryConfig are accepted for the runner, which passes the same parameters to every scenario.')]
param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Domain,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

$null = $DirectoryConfig
$null = $SkipPopulate
$null = $WaitSeconds

. "$PSScriptRoot/../utils/Test-Helpers.ps1"

if (-not $ApiKey) { throw "API key required for authentication." }
if (-not $Domain) { $Domain = "standin.example" }

# The stand-in's harness controls, published on the host's loopback only.
$standIn = "https://localhost:$($env:GRAPH_SERVICE_PORT ?? '5443')"
$prefix = "jimit-$([Guid]::NewGuid().ToString('N').Substring(0, 6))-"
$scale = Get-TemplateScale -Template $Template
$entraSystemName = "Entra ID (Scenario 040)"
$resultsFolder = Join-Path $PSScriptRoot '..' 'results'

$testResults = @{
    Scenario = "Entra ID at scale, stand-in"
    Steps = @()
    Success = $false
}
$timings = [System.Collections.Generic.List[object]]::new()

function Add-StepResult {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $testResults.Steps += @{ Name = $Name; Passed = $Passed; Detail = $Detail }
}

# The stand-in serves a certificate made at its start; the harness talks to it on loopback, so it skips the check JIM
# itself makes against the same certificate in its Trusted Certificates.
function Invoke-StandIn {
    param([ValidateSet('Get', 'Post')][string]$Method = 'Get', [Parameter(Mandatory)][string]$Path, [object]$Body)
    $request = @{ Method = $Method; Uri = "$standIn$Path"; SkipCertificateCheck = $true; ErrorAction = 'Stop' }
    if ($PSBoundParameters.ContainsKey('Body')) { $request.Body = ConvertTo-Json -InputObject $Body -Compress; $request.ContentType = 'application/json' }
    return Invoke-RestMethod @request
}

function Add-Timing {
    param([string]$Label, [System.Diagnostics.Stopwatch]$Watch, [int]$Objects = 0)
    $seconds = [Math]::Round($Watch.Elapsed.TotalSeconds, 1)
    $rate = if ($Objects -gt 0 -and $seconds -gt 0) { [Math]::Round($Objects / $seconds, 1) } else { $null }
    $timings.Add([PSCustomObject]@{ Step = $Label; Seconds = $seconds; Objects = $Objects; PerSecond = $rate })
    Write-Host ("    {0}: {1}s{2}" -f $Label, $seconds, $(if ($rate) { ", $rate objects a second" } else { '' })) -ForegroundColor DarkGray
}

# Runs a Run Profile to completion, timed, and returns its Activity's statistics.
function Invoke-TimedRun {
    param([Parameter(Mandatory)][string]$Label, [Parameter(Mandatory)][string]$RunProfile, [int]$Objects = 0)
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $result = Start-JIMRunProfile -ConnectedSystemId $entraSystem.id -RunProfileName $RunProfile -Wait -PassThru
    $watch.Stop()
    Assert-ActivitySuccess -ActivityId $result.activityId -Name $Label
    Add-Timing -Label $Label -Watch $watch -Objects $Objects
    return Get-JIMActivityStats -ActivityId $result.activityId
}

# Scenario 039's names for person $Index (Seed in the stand-in makes the same).
$width = [Math]::Max(5, "$($scale.Users)".Length)
function Get-AccountName { param([int]$Index) "${prefix}u$("$Index".PadLeft($width, '0'))" }

function Get-PersonMismatch {
    param([Parameter(Mandatory)][int]$Index, [Parameter(Mandatory)][string]$JobTitle)
    $accountName = Get-AccountName -Index $Index
    $found = @(Get-JIMMetaverseObject -AttributeName "Account Name" -AttributeValue $accountName -ErrorAction SilentlyContinue)
    if ($found.Count -ne 1) { return "$accountName is $($found.Count) Metaverse Object(s), expected 1" }
    $object = Get-JIMMetaverseObject -Id $found[0].id
    $values = @{}
    foreach ($value in @($object.attributeValues)) { $values[$value.attributeName] = $value.stringValue }
    $expected = [ordered]@{ 'Display Name' = "${prefix}Scale User $("$Index".PadLeft($width, '0'))"; 'Job Title' = $JobTitle; 'User Principal Name' = "$accountName@$Domain" }
    return @(foreach ($name in $expected.Keys) {
        if ("$($values[$name])" -ne "$($expected[$name])") { "$accountName '$name' is '$($values[$name])', expected '$($expected[$name])'" }
    })
}

function Assert-MetaverseCount {
    param([Parameter(Mandatory)][string]$ObjectTypeName, [Parameter(Mandatory)][int]$Expected)
    $count = Get-JIMMetaverseObject -ObjectTypeName $ObjectTypeName -Count
    Assert-Equal -Actual $count -Expected $Expected -Message "$ObjectTypeName Metaverse Objects"
}

# --- The run -----------------------------------------------------------------------------------------------------------

Write-TestSection "Scenario 040: Entra ID at scale, stand-in ($Template)"
Write-Host "Run prefix: $prefix" -ForegroundColor Gray
Write-Host "Size:       $($scale.Users) users, $($scale.Groups) groups, $($scale.AvgMemberships) memberships each on average" -ForegroundColor Gray
Write-Host ""

try {
    Write-TestStep "Setup" "Trusting the stand-in, creating the Entra ID Connected System and configuring JIM"

    $summary = Invoke-StandIn -Path '/_stand-in/summary'
    if ($summary.users -ne 0 -or $summary.groups -ne 0) { throw "The stand-in already holds $($summary.users) user(s) and $($summary.groups) group(s); it must start empty." }

    Remove-Module JIM -Force -ErrorAction SilentlyContinue
    Import-Module "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1" -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $certificatePath = Join-Path ([System.IO.Path]::GetTempPath()) "graph-service-$prefix.pem"
    docker cp graph-service:/certificates/graph-service.pem $certificatePath 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not copy the certificate out of the graph-service container. Run-IntegrationTests.ps1 starts it for Scenario 040." }
    Add-JIMCertificate -Name "Stand-in Microsoft Graph ($prefix)" -CertificateData ([System.IO.File]::ReadAllBytes($certificatePath)) `
        -Notes "Self-signed certificate the stand-in Microsoft Graph service made for this run." | Out-Null
    Remove-Item $certificatePath -Force -ErrorAction SilentlyContinue

    $connector = @(Get-JIMConnectorDefinition) | Where-Object { $_.name -eq 'JIM Microsoft Entra ID Connector' }
    if (-not $connector) { throw "The JIM Microsoft Entra ID Connector is not registered." }
    $connectorDetail = Get-JIMConnectorDefinition -Id $connector.id
    $settingId = @{}
    foreach ($setting in @($connectorDetail.settings)) { $settingId[$setting.name] = $setting.id }
    $entraSystem = New-JIMConnectedSystem -Name $entraSystemName -Description "The stand-in Microsoft Graph service" -ConnectorDefinitionId $connector.id -PassThru
    # Any tenant and app: the stand-in signs any client in. A client secret keeps the run to what it measures; the
    # certificate sign-in is Scenarios 036 and 037's.
    Set-JIMConnectedSystem -Id $entraSystem.id -SettingValues @{
        $settingId['Tenant ID'] = @{ stringValue = [Guid]::NewGuid().ToString() }
        $settingId['Client ID'] = @{ stringValue = [Guid]::NewGuid().ToString() }
        $settingId['Authentication Method'] = @{ stringValue = 'Client Secret' }
        $settingId['Client Secret'] = @{ stringValue = [Guid]::NewGuid().ToString('N') }
    } | Out-Null

    & "$PSScriptRoot/../Setup-Scenario-037.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -ConnectedSystemId $entraSystem.id -Prefix $prefix
    $entraSystem = Get-JIMConnectedSystem -Id $entraSystem.id
    Write-Host "  OK '$entraSystemName' reads the stand-in, with Scenario 037's rules" -ForegroundColor Green

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $seeded = Invoke-StandIn -Method Post -Path '/_stand-in/seed' -Body @{
        prefix = $prefix; domain = $Domain; users = $scale.Users; groups = $scale.Groups; averageMemberships = $scale.AvgMemberships }
    Add-Timing -Label "Stand-in seed" -Watch $watch -Objects ($seeded.users + $seeded.groups)
    Write-Host "  OK The stand-in holds $($seeded.users) users, $($seeded.groups) groups and $($seeded.memberships) memberships" -ForegroundColor Green
    $total = $seeded.users + $seeded.groups
    $sample = @(for ($i = 0; $i -lt $scale.Users; $i += [Math]::Max(1, [Math]::Floor($scale.Users / 20))) { $i })

    # --- 1: Full Import ---
    Write-TestStep "FullImport" "A Full Import and Full Synchronisation of $total objects"

    $null = Invoke-TimedRun -Label "Entra ID Full Import" -RunProfile "Full Import" -Objects $total
    $null = Invoke-TimedRun -Label "Entra ID Full Synchronisation" -RunProfile "Full Synchronisation" -Objects $total
    Assert-MetaverseCount -ObjectTypeName User -Expected $seeded.users
    Assert-MetaverseCount -ObjectTypeName Group -Expected $seeded.groups
    $mismatches = @(foreach ($index in $sample) { Get-PersonMismatch -Index $index -JobTitle 'Analyst' })
    Assert-Equal -Actual $mismatches.Count -Expected 0 -Message "The sample of people ($(($mismatches | Select-Object -First 5) -join '; '))"

    Add-StepResult -Name "FullImport" -Passed $true -Detail "$($seeded.users) users and $($seeded.groups) groups projected; $($sample.Count) people read back"
    Write-Host "  OK Everyone projected, and a sample read back" -ForegroundColor Green

    # --- 2: Delta Import ---
    $changed = Invoke-StandIn -Method Post -Path '/_stand-in/changes'
    Write-TestStep "DeltaImport" "$($changed.retitled) new job titles, $($changed.moved) moves between teams and $($changed.deleted) deletions"

    $stats = Invoke-TimedRun -Label "Entra ID Delta Import" -RunProfile "Delta Import" -Objects ($changed.retitled + $changed.moved + $changed.deleted)
    Write-Host "    Read $([int]$stats.totalCsoUpdates) update(s) and $([int]$stats.totalCsoDeletes) deletion(s)" -ForegroundColor DarkGray
    $null = Invoke-TimedRun -Label "Entra ID Delta Synchronisation" -RunProfile "Delta Synchronisation" -Objects ($changed.retitled + $changed.moved + $changed.deleted)
    Assert-MetaverseCount -ObjectTypeName User -Expected ($seeded.users - $changed.deleted)
    $retitledSample = @(for ($i = 7; $i -lt $scale.Users -and $i -lt 2000; $i += 100) { $i })
    $mismatches = @(foreach ($index in $retitledSample) { Get-PersonMismatch -Index $index -JobTitle 'Senior Analyst' })
    Assert-Equal -Actual $mismatches.Count -Expected 0 -Message "The retitled people ($(($mismatches | Select-Object -First 5) -join '; '))"
    $deletedSample = @(for ($i = 5; $i -lt $scale.Users -and $i -lt 2000; $i += 100) { Get-AccountName -Index $i })
    $survivors = @($deletedSample | Where-Object { @(Get-JIMMetaverseObject -AttributeName "Account Name" -AttributeValue $_ -ErrorAction SilentlyContinue).Count -gt 0 })
    Assert-Equal -Actual $survivors.Count -Expected 0 -Message "Deleted people left in the Metaverse ($($survivors -join ', '))"

    Add-StepResult -Name "DeltaImport" -Passed $true -Detail "$($changed.retitled) retitled, $($changed.moved) moved and $($changed.deleted) deleted, confirmed in the Metaverse"
    Write-Host "  OK The Metaverse is in line with the tenant" -ForegroundColor Green

    # --- 3: Delta integrity ---
    Write-TestStep "DeltaIntegrity" "A Delta Import with nothing changed reads nothing"

    $stats = Invoke-TimedRun -Label "Entra ID Delta Import (nothing changed)" -RunProfile "Delta Import"
    $read = [int]$stats.totalCsoAdds + [int]$stats.totalCsoUpdates + [int]$stats.totalCsoDeletes
    Assert-Equal -Actual $read -Expected 0 -Message "Objects a Delta Import read with nothing changed"

    Add-StepResult -Name "DeltaIntegrity" -Passed $true -Detail "Nothing read"
    Write-Host "  OK Nothing to read when nothing changed" -ForegroundColor Green

    # --- 4: Full Import ---
    $remaining = $total - $changed.deleted
    Write-TestStep "FullImportAgain" "A Full Import and Full Synchronisation of the whole tenant change nothing"

    $stats = Invoke-TimedRun -Label "Entra ID Full Import (everything again)" -RunProfile "Full Import" -Objects $remaining
    $read = [int]$stats.totalCsoAdds + [int]$stats.totalCsoUpdates + [int]$stats.totalCsoDeletes
    Assert-Equal -Actual $read -Expected 0 -Message "Objects a Full Import changed with nothing changed"
    $null = Invoke-TimedRun -Label "Entra ID Full Synchronisation (everything again)" -RunProfile "Full Synchronisation" -Objects $remaining
    Assert-MetaverseCount -ObjectTypeName User -Expected ($seeded.users - $changed.deleted)

    Add-StepResult -Name "FullImportAgain" -Passed $true -Detail "Nothing changed"
    Write-Host "  OK Nothing changed" -ForegroundColor Green

    $testResults.Success = $true
}
catch {
    $testResults.Success = $false
    Write-Failure "Scenario 040 failed: $_"
}
finally {
    $null = New-Item -ItemType Directory -Path $resultsFolder -Force
    $resultsFile = Join-Path $resultsFolder "scenario-040-$Template-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')).json"
    [PSCustomObject]@{
        Template = $Template; Users = $scale.Users; Groups = $scale.Groups; Succeeded = $testResults.Success; Steps = $timings
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $resultsFile
    Write-Host ""
    $timings | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host "  Timings written to $resultsFile" -ForegroundColor Gray
    if (Get-Module JIM) { Disconnect-JIM -ErrorAction SilentlyContinue }
}

Write-TestSection "Scenario 040 $(if ($testResults.Success) { 'Complete' } else { 'Failed' })"
foreach ($stepResult in $testResults.Steps) {
    Write-Host "  [PASS] $($stepResult.Name): $($stepResult.Detail)" -ForegroundColor Green
}

return $testResults
