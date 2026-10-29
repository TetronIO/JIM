# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Initial administrator provisioning: an internal Metaverse Object is provisioned the same way whether the unscoped
    export rule or the object came first

.DESCRIPTION
    The acceptance test for #2615. A Metaverse Object created outside synchronisation, here the initial administrator
    JIM creates at the first portal sign-in, must be reviewed against every export Synchronisation Rule by the next
    synchronisation of any Connected System, Delta Synchronisation included. Before #2615 the outcome depended on
    timing: an object created before the rule was provisioned (saving the rule flagged it), one created after it never
    was.

    The scenario signs the initial administrator in to the portal itself, through the bundled Keycloak over plain HTTP
    with no browser (KeycloakSignIn-Helpers.ps1; Scenario 031 does the same through AD FS), and runs two orders:

      ObjectFirst  The administrator signs in, and then an unscoped provisioning User export rule is created whose
                   mappings the administrator's sign-in attributes satisfy.
      RuleFirst    The rule exists, and then the administrator signs in.

    In each, a Delta Synchronisation of a Connected System other than the export rule's target, one that holds no people
    and so has nothing to import, is the run that has to provision the administrator. It must stage one Pending Export
    Create for them, queued by that run's review of the Metaverse Object (an Export Review item), and the export
    must write one row to the target file. All = both orders, and the two must agree: the same attribute values in the
    Pending Export, the same row in the file.

    The initial administrator is created once per database, so the second order needs a fresh one. A factory reset that
    includes administrators (Reset-JIMSystem -IncludeAdministrators, the REST API's POST /api/v1/system/reset) removes
    them, and signing in afterwards creates the initial administrator again. Each order therefore starts with that
    reset and builds its own configuration (Setup-Scenario-033.ps1), as Scenarios 010 and 011 start theirs.

    The User export rule here is deliberately unscoped, the one case the rest of the harness avoids: its other User
    export rules are scoped to the people their source projected so that an administrator who signs in during a run is
    never provisioned. Unscoped provisioning of Groups is covered by Scenarios 008, 028 and 029.

    The two Connected Systems are File Connector systems (HR: import only, no rows; directory: export only), so the
    scenario needs no directory container and runs once however many directory types a run asks for.

.PARAMETER Step
    ObjectFirst, RuleFirst, or All (default). Each order stands on its own; only All compares them.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200). The bundled Keycloak realm allows
    http://localhost:5200/signin-oidc as the portal's redirect, so no other address can sign in.

.PARAMETER ApiKey
    API key for authentication

.PARAMETER KeycloakUrl
    Where Keycloak is reachable from this machine (default: http://localhost:8180, the port the stack publishes it on).
    JIM redirects the sign-in to the address Keycloak advertises (http://localhost:8181, bridged to it only where the
    runner has socat), which is rewritten to this.

.PARAMETER Template
    Accepted for runner compatibility. This scenario's data is its own two files.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. This scenario has no directory.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario Scenario-033-InitialAdministratorProvisioning -DirectoryType OpenLDAP
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("ObjectFirst", "RuleFirst", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$KeycloakUrl = "http://localhost:8180",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

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

# Accepted for runner compatibility; this scenario seeds its own data and has no directory target.
$null = $DirectoryConfig
$null = $SkipPopulate
$null = $Template
$null = $WaitSeconds

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/Scenario-033-Data.ps1"
. "$PSScriptRoot/../utils/KeycloakSignIn-Helpers.ps1"

Write-TestSection "Scenario 033: initial administrator provisioning"
Write-Host "Step: $Step" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Initial administrator provisioning"
    Steps = @()
    Success = $false
}

function Add-StepResult {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $testResults.Steps += @{ Name = $Name; Passed = $Passed; Detail = $Detail }
}

function Confirm-Check {
    # A named check that stops the scenario when it fails: each later check builds on the one before.
    param([string]$Name, [bool]$Passed, [string]$Detail = "")
    if (-not $Passed) {
        Write-Host "  FAIL $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "       $Detail" -ForegroundColor Yellow }
        throw "Check failed: $Name. $Detail"
    }
    Add-StepResult -Name $Name -Passed $true -Detail $Detail
    Write-Host "  OK $Name" -ForegroundColor Green
}

function Invoke-RunProfile {
    param([int]$ConnectedSystemId, [string]$RunProfileName, [string]$Label)
    $result = Start-JIMRunProfile -ConnectedSystemId $ConnectedSystemId -RunProfileName $RunProfileName -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $result.ActivityId -Name $Label
    return $result.ActivityId
}

function Find-AdministratorMetaverseObject {
    # The Metaverse Objects of type User holding the initial administrator's Subject Identifier: none, or the one.
    param([string]$Subject)
    return @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Subject Identifier" -AttributeValue $Subject -ErrorAction SilentlyContinue)
}

function Get-AttributeText {
    param($MetaverseObject, [string]$AttributeName)
    $value = @($MetaverseObject.AttributeValues | Where-Object { $_.AttributeName -eq $AttributeName }) | Select-Object -First 1
    if ($value) { return [string]$value.StringValue } else { return $null }
}

function ConvertTo-ChangeLines {
    # A Pending Export's attribute changes as comparable text, one line per change, in a fixed order.
    param($PendingExportDetail)
    return @($PendingExportDetail.AttributeChanges | ForEach-Object { "$($_.AttributeName)|$($_.ChangeType)|$($_.StringValue)" } | Sort-Object)
}

# One order, start to finish. Returns what the comparison needs.
function Invoke-Order {
    param([ValidateSet("ObjectFirst", "RuleFirst")][string]$Order)

    $ruleFirst = ($Order -eq "RuleFirst")
    Write-TestSection "Order: $(if ($ruleFirst) { 'the unscoped export rule exists, then the administrator signs in' } else { 'the administrator signs in, then the unscoped export rule is created' })"

    # --- A fresh start: no administrator, none of the scenario's configuration ---
    Write-TestStep "$Order.1" "Factory reset, administrators included, so the administrator is created afresh"
    Reset-JIMSystem -Force -IncludeAdministrators -AcknowledgeAdministratorLockout | Out-Null
    Confirm-Check "$Order`: no Metaverse Object holds the initial administrator's Subject Identifier after the reset" `
        (@(Find-AdministratorMetaverseObject -Subject $administrator.Subject).Count -eq 0)

    Write-TestStep "$Order.2" "Configuring the two File Connector systems$(if ($ruleFirst) { ' and the unscoped export rule' })"
    $setupOutput = & "$PSScriptRoot/../Setup-Scenario-033.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -OmitExportRule:(-not $ruleFirst)
    $setup = @($setupOutput | Where-Object { $_ -is [hashtable] }) | Select-Object -Last 1
    if (-not $setup) { throw "Setup-Scenario-033.ps1 returned no configuration." }
    # The setup script connects afresh; connect again in case it left a different connection behind.
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
    $hrId = $setup.HrSystemId
    $directoryId = $setup.DirectorySystemId

    Write-TestStep "$Order.3" "Baseline: a Full Import and Full Synchronisation of the HR file, before anyone has signed in"
    Invoke-RunProfile -ConnectedSystemId $hrId -RunProfileName "Full Import" -Label "HR Full Import ($Order)" | Out-Null
    Invoke-RunProfile -ConnectedSystemId $hrId -RunProfileName "Full Synchronisation" -Label "HR Full Synchronisation ($Order)" | Out-Null
    Confirm-Check "$Order`: the HR system holds no Connected System Objects, so a synchronisation of it has nothing of its own to process" `
        ((Get-JIMConnectedSystemObject -ConnectedSystemId $hrId -Count) -eq 0)
    Confirm-Check "$Order`: the directory file holds no rows" `
        (@(Get-Scenario033ConnectorFileRows -Path $script:Scenario033DirectoryCsvPath -Volume $volume).Count -eq 0)

    # --- The initial administrator signs in to the portal ---
    Write-TestStep "$Order.4" "The initial administrator signs in to the portal through Keycloak"
    $browser = Invoke-KeycloakPortalSignIn -JimUrl $JIMUrl -UserName $administrator.UserName -Password $administrator.Password -KeycloakUrl $KeycloakUrl
    try {
        $userInfo = Get-KeycloakPortalUserInfo -Browser $browser -JimUrl $JIMUrl
        $administratorApiStatus = Get-KeycloakPortalAdministratorApiStatus -Browser $browser -JimUrl $JIMUrl
    }
    finally {
        Close-SignInBrowser -Browser $browser
    }
    Confirm-Check "$Order`: JIM signs them in as an Administrator with a Metaverse Object of their own" `
        ($userInfo.authorised -and $userInfo.isAdministrator -and $userInfo.metaverseObjectId -and $administratorApiStatus -eq 200) `
        "userinfo: $($userInfo | ConvertTo-Json -Compress); Administrator API: $administratorApiStatus"
    $administratorMvoId = [guid]$userInfo.metaverseObjectId
    $administratorMvo = Get-JIMMetaverseObject -Id $administratorMvoId

    Confirm-Check "$Order`: the Metaverse Object is internal, created outside synchronisation, and carries the Subject Identifier" `
        ("$($administratorMvo.Origin)" -eq "Internal" -and (Get-AttributeText $administratorMvo "Subject Identifier") -eq $administrator.Subject) `
        "Origin: $($administratorMvo.Origin)"
    $missing = @($script:Scenario033ExportFlows.Keys | Where-Object { -not (Get-AttributeText $administratorMvo $_) })
    Confirm-Check "$Order`: the sign-in populated every attribute the export rule maps, so the object satisfies its mappings" `
        ($missing.Count -eq 0) "Missing: $($missing -join ', ')"

    # --- The unscoped provisioning export rule ---
    Write-TestStep "$Order.5" "$(if ($ruleFirst) { 'The unscoped export rule was in place before the administrator existed' } else { 'Creating the unscoped provisioning User export rule, now that the administrator exists' })"
    if (-not $ruleFirst) {
        Confirm-Check "$Order`: with no export rule yet, nothing is waiting to be exported to the directory" `
            ((Get-JIMPendingExport -ConnectedSystemId $directoryId -Count) -eq 0)
        $rule = Add-Scenario033ExportRule -DirectorySystemId $directoryId
    }
    else {
        $rule = Get-JIMSyncRule | Where-Object { $_.name -eq $script:Scenario033ExportRuleName } | Select-Object -First 1
    }
    Confirm-Check "$Order`: the export rule provisions every User to the directory and has no scoping criteria" `
        ($rule -and $rule.Direction -eq "Export" -and $rule.ProvisionToConnectedSystem -and $rule.MetaverseObjectTypeName -eq "User" -and
        @(Get-JIMScopingCriteria -SyncRuleId $rule.Id).Count -eq 0) `
        "Rule $($rule.Id): direction $($rule.Direction), provisioning $($rule.ProvisionToConnectedSystem), scoping groups $(@(Get-JIMScopingCriteria -SyncRuleId $rule.Id).Count)"
    Confirm-Check "$Order`: nothing is exported until a synchronisation runs: no Pending Export and no row yet" `
        ((Get-JIMPendingExport -ConnectedSystemId $directoryId -Count) -eq 0 -and
        @(Get-Scenario033ConnectorFileRows -Path $script:Scenario033DirectoryCsvPath -Volume $volume).Count -eq 0)

    # --- The Delta Synchronisation of the other Connected System ---
    Write-TestStep "$Order.6" "Delta Synchronisation of the HR system, which holds no people and is not where the administrator is provisioned"
    $deltaActivityId = Invoke-RunProfile -ConnectedSystemId $hrId -RunProfileName "Delta Synchronisation" -Label "HR Delta Synchronisation ($Order)"

    $pendingExports = @(Get-JIMPendingExport -ConnectedSystemId $directoryId -All)
    Confirm-Check "$Order`: the administrator is provisioned: one Pending Export Create, sourced from their Metaverse Object" `
        ($pendingExports.Count -eq 1 -and "$($pendingExports[0].ChangeType)" -eq "Create" -and "$($pendingExports[0].SourceMetaverseObjectId)" -eq "$administratorMvoId") `
        "$($pendingExports.Count) Pending Export(s): $(($pendingExports | ForEach-Object { "$($_.ChangeType) from $($_.SourceMetaverseObjectId)" }) -join '; ')"
    $pendingExportId = $pendingExports[0].Id

    $queuedBy = @(Get-JIMPendingExport -Id $pendingExportId -QueuedBy)
    $review = $queuedBy | Where-Object { "$($_.ActivityId)" -eq "$deltaActivityId" } | Select-Object -First 1
    Confirm-Check "$Order`: it was queued by the HR Delta Synchronisation's review of the administrator (an Export Review item)" `
        ($queuedBy.Count -eq 1 -and $review -and $review.ConnectedSystemId -eq $hrId -and "$($review.RunType)" -eq "DeltaSynchronisation" -and
        "$($review.ObjectChangeType)" -eq "ExportScopeReview" -and "$($review.MetaverseObjectId)" -eq "$administratorMvoId") `
        "Queued by: $(($queuedBy | ForEach-Object { "$($_.RunType) of Connected System $($_.ConnectedSystemId), $($_.ObjectChangeType), Activity $($_.ActivityId)" }) -join '; ') (the Delta Synchronisation was Activity $deltaActivityId)"

    $detail = Get-JIMPendingExport -Id $pendingExportId
    $changes = ConvertTo-ChangeLines -PendingExportDetail $detail
    $stagedValues = @($detail.AttributeChanges | ForEach-Object { "$($_.AttributeName)|$($_.StringValue)" } | Sort-Object)
    $expectedValues = @($script:Scenario033ExportFlows.GetEnumerator() | ForEach-Object {
            "$($_.Value)|$(Get-AttributeText $administratorMvo $_.Key)"
        } | Sort-Object)
    Confirm-Check "$Order`: its attribute changes are the administrator's own values, one per mapping" `
        (($stagedValues -join "`n") -ceq ($expectedValues -join "`n")) `
        "Staged: $($changes -join ', ')"

    # --- The export ---
    Write-TestStep "$Order.7" "Export to the directory file"
    $exportActivityId = Invoke-RunProfile -ConnectedSystemId $directoryId -RunProfileName "Export" -Label "Directory Export ($Order)"
    Assert-ExportSuccess -ActivityId $exportActivityId -Name "Directory Export ($Order)"
    $rows = @(Get-Scenario033ConnectorFileRows -Path $script:Scenario033DirectoryCsvPath -Volume $volume)
    Confirm-Check "$Order`: the directory file holds one row, the administrator's, and nothing is left pending" `
        ($rows.Count -eq 1 -and (Get-JIMPendingExport -ConnectedSystemId $directoryId -Count) -eq 0) `
        "$($rows.Count) row(s)"
    $row = $rows[0]
    $mismatches = @($script:Scenario033ExportFlows.GetEnumerator() | Where-Object { $row.($_.Value) -cne (Get-AttributeText $administratorMvo $_.Key) } |
            ForEach-Object { "$($_.Value)='$($row.($_.Value))'" })
    Confirm-Check "$Order`: the row carries the administrator's values" ($mismatches.Count -eq 0) "Differs: $($mismatches -join ', ')"

    return @{
        Order = $Order
        AdministratorMvoId = $administratorMvoId
        Changes = $changes
        Row = $row
    }
}

try {
    if (-not $ApiKey) { throw "API key required for authentication" }
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    Write-TestStep "Setup" "Who the stack names as its initial administrator, and where Keycloak is"
    $lane = Get-IntegrationLane
    $volume = $lane.ConnectorFilesVolume
    $realmFile = Join-Path $PSScriptRoot ".." ".." ".." ".devcontainer" "keycloak" "jim-realm.json"
    $administrator = Get-Scenario033InitialAdministrator -WebContainer $lane.WebContainer -RealmFile $realmFile
    if (([uri]$JIMUrl).GetLeftPart([UriPartial]::Authority) -ne "http://localhost:5200") {
        throw "The bundled Keycloak realm allows http://localhost:5200/signin-oidc as the portal's redirect, so the sign-in cannot be driven against $JIMUrl."
    }
    Write-Host "  OK JIM_SSO_INITIAL_ADMIN is $($administrator.Subject), the development realm's '$($administrator.UserName)'; Keycloak at $KeycloakUrl" -ForegroundColor Green

    $outcomes = @{}
    foreach ($order in @("ObjectFirst", "RuleFirst")) {
        if ($Step -in @($order, "All")) { $outcomes[$order] = Invoke-Order -Order $order }
    }

    if ($Step -eq "All") {
        Write-TestSection "Comparison: the same provisioning in both orders"
        $first = $outcomes["ObjectFirst"]
        $second = $outcomes["RuleFirst"]

        Confirm-Check "The two orders used two different Metaverse Objects, so the second administrator was created afresh" `
            ("$($first.AdministratorMvoId)" -ne "$($second.AdministratorMvoId)") `
            "$($first.AdministratorMvoId) and $($second.AdministratorMvoId)"
        Confirm-Check "The Pending Export staged for the administrator is identical in both orders" `
            (($first.Changes -join "`n") -ceq ($second.Changes -join "`n")) `
            "ObjectFirst: $($first.Changes -join ', ') | RuleFirst: $($second.Changes -join ', ')"
        $columns = @($script:Scenario033ExportFlows.Values)
        $differing = @($columns | Where-Object { $first.Row.$_ -cne $second.Row.$_ })
        Confirm-Check "The row the export wrote to the directory file is identical in both orders" ($differing.Count -eq 0) "Differs: $($differing -join ', ')"
    }

    $testResults.Success = $true
}
catch {
    Write-Host ""
    Write-Host "Scenario 033 failed: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    Add-StepResult -Name "Failure" -Passed $false -Detail "$_"
    $testResults.Success = $false
}

Write-Host ""
Write-TestSection "Scenario 033 Results"
foreach ($result in $testResults.Steps) {
    $colour = if ($result.Passed) { "Green" } else { "Red" }
    $mark = if ($result.Passed) { "PASS" } else { "FAIL" }
    Write-Host "  [$mark] $($result.Name)" -ForegroundColor $colour
    if (-not $result.Passed -and $result.Detail) { Write-Host "         $($result.Detail)" -ForegroundColor Yellow }
}

return $testResults
