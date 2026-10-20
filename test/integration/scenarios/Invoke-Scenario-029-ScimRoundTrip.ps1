# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    JIM provisioning its own SCIM endpoint: the SCIM 2.0 Client Connector against the SCIM 2.0 Service Provider

.DESCRIPTION
    The acceptance test for JIM's SCIM 2.0 Service Provider (#124). One JIM provisions its own SCIM endpoint with
    the shipped SCIM 2.0 Client Connector, over HTTPS on a real socket exactly as between two JIM instances, and
    imports what arrives with the SCIM 2.0 Service Provider Connector through a Schedule. An identity provider's
    requests are sent to the same endpoint directly, as identity providers send them.

      Provision          People and a group provisioned through the client: discovery and the schema it flattens,
                         POST, the group's members added by PATCH, and a confirming Full Import paging through the
                         endpoint four at a time.
      Inbound            The Schedule's Delta Import and Delta Synchronisation bring what arrived into the
                         Metaverse: every person and the group joined, the group with its members.
      Update             A surname change exported as a PATCH carrying If-Match, confirmed by the client's Delta
                         Import (its meta.lastModified filter), and brought in by the Schedule.
      Clear              A department cleared in HR, exported as a value-less replace; the store holds none.
      IdentityProvider   What identity providers send: a lower-case scheme, a bare key, a trailing slash, unknown
                         query parameters, the empty credential probe, a capitalised op, Booleans as strings, a
                         pushed password (refused, and stored nowhere), a duplicate userName (409 uniqueness). A
                         pushed Job Title reaches the Metaverse through the Schedule.
      Conflict           The client exports over a change the identity provider made since the client last read
                         it: the endpoint refuses it (412), the client reports a concurrency conflict, and an
                         import then lets the change through.
      Deprovision        A leaver deleted from the endpoint; the client's Full Import finds it gone, and the
                         Schedule's Delta Import reads the deletion.
      Restore            The Service Provider's Connector Space cleared and rebuilt from the store by a Full
                         Import, with nothing re-sent.
      Summary            Each key's SCIM request summary Activity counts what it did.

.PARAMETER Step
    Which step to run (default: All)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER UserCount
    How many people the HR CSV holds (default: 12).

.PARAMETER Template
    Accepted for runner compatibility. This scenario's data is its own CSV.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. This scenario has no directory target.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario Scenario-029-ScimRoundTrip
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Provision", "Inbound", "Update", "Clear", "IdentityProvider", "Conflict", "Deprovision", "Restore", "Summary", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [ValidateRange(6, 200)]
    [int]$UserCount = 12,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Accepted for runner compatibility; this scenario seeds its own data and has no directory target.
$null = $DirectoryConfig
$null = $SkipPopulate
$null = $Template
$null = $WaitSeconds

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/Scenario-029-Data.ps1"

Write-TestSection "Scenario 029: JIM provisioning its own SCIM endpoint"
Write-Host "Step:   $Step" -ForegroundColor Gray
Write-Host "People: $UserCount" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "JIM provisioning its own SCIM endpoint"
    Steps = @()
    Success = $false
}

function Add-StepResult {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $testResults.Steps += @{ Name = $Name; Passed = $Passed; Detail = $Detail }
}

# The HR data every step changes and rewrites, so each step starts from what the step before left.
$people = [System.Collections.Generic.List[object]]::new()
foreach ($person in (New-Scenario029People -Count $UserCount)) { $people.Add($person) }
$group = New-Scenario029Group

function Write-HrCsv {
    Write-Scenario029HrCsv -People $people.ToArray() -Group $group
}

function Get-Person {
    param([int]$Number)
    return $people | Where-Object { $_.AccountName -eq (Get-Scenario029AccountName $Number) }
}

function Invoke-RunProfile {
    param([int]$ConnectedSystemId, [string]$RunProfileName, [string]$Label, [switch]$AllowErrors)
    $result = Start-JIMRunProfile -ConnectedSystemId $ConnectedSystemId -RunProfileName $RunProfileName -Wait -PassThru
    if (-not $AllowErrors) {
        Assert-ActivitySuccess -ActivityId $result.activityId -Name $Label
    }
    return $result.activityId
}

function Invoke-HrImportAndSync {
    param([string]$Label)
    Invoke-RunProfile -ConnectedSystemId $hrSystem.id -RunProfileName "Full Import" -Label "HR Full Import ($Label)" | Out-Null
    Invoke-RunProfile -ConnectedSystemId $hrSystem.id -RunProfileName "Full Synchronisation" -Label "HR Full Synchronisation ($Label)" | Out-Null
}

# Exports through the client, then reads the endpoint back. A change that references something the same run creates
# (a member whose account is new) is sent once that exists, so this goes round until nothing is left, and fails if a
# round makes no progress: a Pending Export a confirming import cannot confirm is a value the endpoint does not hold.
function Invoke-ClientExportAndConfirm {
    param([string]$Label, [string]$ConfirmWith = "Full Import", [int]$MaximumRounds = 4)
    $remaining = -1
    for ($round = 1; $round -le $MaximumRounds; $round++) {
        $exportActivityId = Invoke-RunProfile -ConnectedSystemId $clientSystem.id -RunProfileName "Export" -Label "Client Export ($Label, round $round)"
        Assert-ExportSuccess -ActivityId $exportActivityId -Name "Client Export ($Label, round $round)"
        Invoke-RunProfile -ConnectedSystemId $clientSystem.id -RunProfileName $ConfirmWith -Label "Client $ConfirmWith ($Label, round $round)" | Out-Null
        Invoke-RunProfile -ConnectedSystemId $clientSystem.id -RunProfileName "Full Synchronisation" -Label "Client Full Synchronisation ($Label, round $round)" | Out-Null
        $pending = @(Get-JIMPendingExport -ConnectedSystemId $clientSystem.id -All)
        if ($pending.Count -eq 0) { return }
        if ($pending.Count -eq $remaining) {
            $summary = ($pending | Select-Object -First 5 | ForEach-Object { "$($_.changeType) $($_.status)" }) -join "; "
            throw "Client export ($Label) stopped making progress with $($pending.Count) Pending Export(s) left: $summary"
        }
        $remaining = $pending.Count
    }
    throw "Client export ($Label) still had Pending Exports after $MaximumRounds rounds."
}

# The Schedule an administrator would run on a timer: the Service Provider's Delta Import, then Delta Synchronisation.
function Invoke-InboundSchedule {
    param([string]$Label)
    $execution = Start-JIMSchedule -Id $schedule.id -Wait -PassThru -Timeout ([TimeSpan]::FromMinutes(10))
    if ($execution.status -ne "Complete") {
        throw "The inbound Schedule ($Label) finished as '$($execution.status)', not Complete."
    }
}

function Get-LiveObjects {
    param([int]$ConnectedSystemId, [int]$ObjectTypeId)
    return @(Get-JIMConnectedSystemObject -ConnectedSystemId $ConnectedSystemId -ObjectTypeId $ObjectTypeId -All |
        Where-Object { $_.status -ne "Obsolete" })
}

function Get-ObjectValues {
    param([int]$ConnectedSystemId, $Object, [string]$AttributeName)
    return @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $ConnectedSystemId -CsoId $Object.id -AttributeName $AttributeName -All)
}

function Find-Object {
    param([int]$ConnectedSystemId, [int]$ObjectTypeId, [string]$AttributeName, [string]$Value)
    foreach ($object in (Get-LiveObjects -ConnectedSystemId $ConnectedSystemId -ObjectTypeId $ObjectTypeId)) {
        $values = Get-ObjectValues -ConnectedSystemId $ConnectedSystemId -Object $object -AttributeName $AttributeName
        if ($values | Where-Object { $_.stringValue -ieq $Value }) { return $object }
    }
    return $null
}

function Get-SingleString {
    param([int]$ConnectedSystemId, $Object, [string]$AttributeName)
    $values = @(Get-ObjectValues -ConnectedSystemId $ConnectedSystemId -Object $Object -AttributeName $AttributeName)
    if ($values.Count -eq 0) { return $null }
    return $values[0].stringValue
}

function Get-MetaverseUser {
    param([string]$AccountName)
    $header = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Account Name" -AttributeValue $AccountName) | Select-Object -First 1
    if (-not $header) { throw "No Metaverse User has the Account Name '$AccountName'." }
    return Get-JIMMetaverseObject -Id $header.id
}

# A request to the endpoint as an identity provider sends it: straight to JIM's own address, with the key it was
# given, however the identity provider formats the header.
function Invoke-ScimRequest {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [string]$Authorization = "Bearer $identityProviderKey",
        [object]$Body
    )
    $parameters = @{
        Uri                  = "$JIMUrl/scim/v2/$serviceProviderId$Path"
        Method               = $Method
        Headers              = @{ Authorization = $Authorization }
        SkipHttpErrorCheck   = $true
        SkipHeaderValidation = $true
    }
    if ($null -ne $Body) {
        $parameters.Body = ($Body | ConvertTo-Json -Depth 10 -Compress)
        $parameters.ContentType = "application/scim+json"
    }
    $response = Invoke-WebRequest @parameters
    # PowerShell treats application/scim+json as binary, so the body arrives as bytes rather than text.
    $content = if ($response.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
    $json = if ($content) { $content | ConvertFrom-Json -Depth 20 } else { $null }
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Json = $json }
}

function Get-StoredUser {
    param([string]$UserName)
    $filter = [uri]::EscapeDataString('userName eq "' + $UserName + '"')
    $response = Invoke-ScimRequest -Method GET -Path "/Users?filter=$filter"
    if ($response.Status -ne 200) { throw "Listing Users by userName answered $($response.Status)." }
    if ($response.Json.totalResults -eq 0) { return $null }
    return $response.Json.Resources[0]
}

try {
    if ($Step -eq "All") {
        Write-TestStep "Setup" "Configuring JIM to provision its own SCIM endpoint"
        & "$PSScriptRoot/../Setup-Scenario-029.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -UserCount $UserCount
    }

    Write-TestStep "Step 0" "Connecting to JIM and resolving the Connected Systems"
    if (-not $ApiKey) { throw "API key required for authentication" }
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $systems = @(Get-JIMConnectedSystem)
    $hrSystem = $systems | Where-Object { $_.name -eq $script:Scenario029HrSystemName }
    $clientSystem = $systems | Where-Object { $_.name -eq $script:Scenario029ClientSystemName }
    $serviceProviderSystem = $systems | Where-Object { $_.name -eq $script:Scenario029ServiceProviderSystemName }
    if (-not $hrSystem -or -not $clientSystem -or -not $serviceProviderSystem) {
        throw "The scenario's Connected Systems were not found. Run Setup-Scenario-029.ps1 first."
    }
    if (-not (Test-Path $script:Scenario029KeyFile)) { throw "The scenario's keys were not found. Run Setup-Scenario-029.ps1 first." }
    $keys = Get-Content $script:Scenario029KeyFile | ConvertFrom-Json
    $identityProviderKey = $keys.identityProvider
    $serviceProviderId = $serviceProviderSystem.id
    $schedule = Get-JIMSchedule | Where-Object { $_.name -eq $script:Scenario029ScheduleName }
    if (-not $schedule) { throw "The inbound Schedule was not found. Run Setup-Scenario-029.ps1 first." }

    $clientTypes = Get-JIMConnectedSystem -Id $clientSystem.id -ObjectTypes
    $clientUserTypeId = ($clientTypes | Where-Object { $_.name -eq "User" }).id
    $clientGroupTypeId = ($clientTypes | Where-Object { $_.name -eq "Group" }).id
    $serviceProviderTypes = Get-JIMConnectedSystem -Id $serviceProviderId -ObjectTypes
    $serviceProviderUserTypeId = ($serviceProviderTypes | Where-Object { $_.name -eq "User" }).id
    $serviceProviderGroupTypeId = ($serviceProviderTypes | Where-Object { $_.name -eq "Group" }).id
    Write-Host "  OK HR ($($hrSystem.id)), Client ($($clientSystem.id)), Service Provider ($serviceProviderId)" -ForegroundColor Green

    # --- Provision ---
    if ($Step -in @("Provision", "All")) {
        Write-TestStep "Provision" "Provisioning $UserCount people and a group to the endpoint through the client"
        Write-HrCsv
        Invoke-HrImportAndSync -Label "baseline"
        Invoke-ClientExportAndConfirm -Label "provision"

        $clientUsers = Get-LiveObjects -ConnectedSystemId $clientSystem.id -ObjectTypeId $clientUserTypeId
        Assert-Equal -Actual $clientUsers.Count -Expected $UserCount -Message "Users the client read back from the endpoint"
        $summary = Get-JIMScimStoreSummary -ConnectedSystemId $serviceProviderId
        $storedUsers = ($summary.resourceCounts | Where-Object { $_.resourceTypeName -eq "User" }).count
        $storedGroups = ($summary.resourceCounts | Where-Object { $_.resourceTypeName -eq "Group" }).count
        Assert-Equal -Actual $storedUsers -Expected $UserCount -Message "Users the endpoint's store holds"
        Assert-Equal -Actual $storedGroups -Expected 1 -Message "Groups the endpoint's store holds"

        $clientGroup = Find-Object -ConnectedSystemId $clientSystem.id -ObjectTypeId $clientGroupTypeId -AttributeName "displayName" -Value $group.DisplayName
        if (-not $clientGroup) { throw "The client read back no group '$($group.DisplayName)'." }
        $memberCount = (Get-ObjectValues -ConnectedSystemId $clientSystem.id -Object $clientGroup -AttributeName "members").Count
        Assert-Equal -Actual $memberCount -Expected $group.Members.Count -Message "Members the client read back on the group"

        $stored = Get-StoredUser -UserName (Get-Scenario029AccountName 1)
        if (-not $stored) { throw "The endpoint holds no User '$(Get-Scenario029AccountName 1)'." }
        Assert-Equal -Actual $stored.name.familyName -Expected (Get-Person 1).LastName -Message "familyName as the endpoint holds it"
        Assert-Condition -Condition ($null -ne $stored.meta.version) -Message "The endpoint versions what it holds (ETag)"

        Add-StepResult -Name "Provision" -Passed $true -Detail "$UserCount people and 1 group provisioned and read back over HTTPS"
    }

    # --- Inbound ---
    if ($Step -in @("Inbound", "All")) {
        Write-TestStep "Inbound" "Bringing what arrived into the Metaverse through the Schedule"
        Invoke-InboundSchedule -Label "inbound"

        $inboundUsers = Get-LiveObjects -ConnectedSystemId $serviceProviderId -ObjectTypeId $serviceProviderUserTypeId
        Assert-Equal -Actual $inboundUsers.Count -Expected $UserCount -Message "Users the Service Provider imported"
        $mvUser = Get-MetaverseUser -AccountName (Get-Scenario029AccountName 1)
        $joined = @($mvUser.connectedSystemObjects | Where-Object { $_.connectedSystemId -eq $serviceProviderId })
        Assert-Equal -Actual $joined.Count -Expected 1 -Message "The Service Provider's User joined to the person's Metaverse Object"

        $inboundGroup = Find-Object -ConnectedSystemId $serviceProviderId -ObjectTypeId $serviceProviderGroupTypeId -AttributeName "displayName" -Value $group.DisplayName
        if (-not $inboundGroup) { throw "The Service Provider imported no group '$($group.DisplayName)'." }
        $inboundMembers = (Get-ObjectValues -ConnectedSystemId $serviceProviderId -Object $inboundGroup -AttributeName "members").Count
        Assert-Equal -Actual $inboundMembers -Expected $group.Members.Count -Message "Members on the imported group"

        Add-StepResult -Name "Inbound" -Passed $true -Detail "Scheduled Delta Import and Delta Synchronisation joined $UserCount people and the group"
    }

    # --- Update ---
    if ($Step -in @("Update", "All")) {
        Write-TestStep "Update" "A surname change, exported as PATCH with If-Match and confirmed by a Delta Import"
        (Get-Person 1).LastName = "Renamed01"
        Write-HrCsv
        Invoke-HrImportAndSync -Label "update"
        Invoke-ClientExportAndConfirm -Label "update" -ConfirmWith "Delta Import"

        $clientUser = Find-Object -ConnectedSystemId $clientSystem.id -ObjectTypeId $clientUserTypeId -AttributeName "userName" -Value (Get-Scenario029AccountName 1)
        Assert-Equal -Actual (Get-SingleString -ConnectedSystemId $clientSystem.id -Object $clientUser -AttributeName "name.familyName") `
            -Expected "Renamed01" -Message "familyName the client's Delta Import read back"

        Invoke-InboundSchedule -Label "update"
        $inboundUser = Find-Object -ConnectedSystemId $serviceProviderId -ObjectTypeId $serviceProviderUserTypeId -AttributeName "userName" -Value (Get-Scenario029AccountName 1)
        Assert-Equal -Actual (Get-SingleString -ConnectedSystemId $serviceProviderId -Object $inboundUser -AttributeName "name.familyName") `
            -Expected "Renamed01" -Message "familyName the Service Provider's Delta Import read"

        Add-StepResult -Name "Update" -Passed $true -Detail "PATCH with If-Match, client Delta Import and scheduled Delta Import agree"
    }

    # --- Clear ---
    if ($Step -in @("Clear", "All")) {
        Write-TestStep "Clear" "A department cleared in HR, exported as a value-less replace"
        (Get-Person 2).Department = ""
        Write-HrCsv
        Invoke-HrImportAndSync -Label "clear"
        Invoke-ClientExportAndConfirm -Label "clear"

        $stored = Get-StoredUser -UserName (Get-Scenario029AccountName 2)
        $enterprise = $stored.PSObject.Properties["urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"]
        $department = if ($enterprise) { $enterprise.Value.PSObject.Properties["department"] } else { $null }
        Assert-Condition -Condition ($null -eq $department) -Message "The endpoint holds no department for the person"

        Add-StepResult -Name "Clear" -Passed $true -Detail "A cleared value reaches the endpoint as none"
    }

    # --- IdentityProvider ---
    if ($Step -in @("IdentityProvider", "All")) {
        Write-TestStep "IdentityProvider" "What identity providers send, straight to the endpoint"

        $lowerScheme = Invoke-ScimRequest -Method GET -Path "/ServiceProviderConfig" -Authorization "bearer $identityProviderKey"
        Assert-Equal -Actual $lowerScheme.Status -Expected 200 -Message "A lower-case 'bearer' scheme is accepted"
        $bareKey = Invoke-ScimRequest -Method GET -Path "/ServiceProviderConfig" -Authorization $identityProviderKey
        Assert-Equal -Actual $bareKey.Status -Expected 200 -Message "A bare key without the scheme is accepted"
        $trailingSlash = Invoke-ScimRequest -Method GET -Path "/Users/?count=1"
        Assert-Equal -Actual $trailingSlash.Status -Expected 200 -Message "A trailing slash on the endpoint is accepted"
        $probe = Invoke-ScimRequest -Method GET -Path "/Users?filter=$([uri]::EscapeDataString('userName eq "probe-7c1e9a"'))&excludedAttributes=members&unknownParameter=1"
        Assert-Equal -Actual $probe.Status -Expected 200 -Message "The credential probe, with an unknown query parameter, answers 200"
        Assert-Equal -Actual ([int]$probe.Json.totalResults) -Expected 0 -Message "The credential probe finds nothing"

        $person3 = Get-StoredUser -UserName (Get-Scenario029AccountName 3)
        $patch = Invoke-ScimRequest -Method PATCH -Path "/Users/$($person3.id)" -Body @{
            schemas    = @("urn:ietf:params:scim:api:messages:2.0:PatchOp")
            Operations = @(
                @{ op = "Replace"; path = "title"; value = "Principal Engineer" },
                @{ op = "replace"; value = @{ active = "True" } }
            )
        }
        Assert-Equal -Actual $patch.Status -Expected 200 -Message "A capitalised op and a Boolean sent as a string are accepted"
        Assert-Equal -Actual $patch.Json.title -Expected "Principal Engineer" -Message "The pushed title as the endpoint returns it"
        Assert-Condition -Condition ($patch.Json.active -is [bool] -and $patch.Json.active) -Message "active stored as a Boolean"

        $passwordUserName = "rt.pushed-password"
        $withPassword = Invoke-ScimRequest -Method POST -Path "/Users" -Body @{
            schemas  = @("urn:ietf:params:scim:schemas:core:2.0:User")
            userName = $passwordUserName
            password = "Pushed-Secret-029!"
        }
        Assert-Equal -Actual $withPassword.Status -Expected 400 -Message "A pushed password is refused"
        Assert-Condition -Condition ($withPassword.Json.detail -notmatch "Pushed-Secret") -Message "The refusal does not repeat the password"
        Assert-Condition -Condition ($null -eq (Get-StoredUser -UserName $passwordUserName)) -Message "Nothing was stored for the refused request"

        $duplicate = Invoke-ScimRequest -Method POST -Path "/Users" -Body @{
            schemas  = @("urn:ietf:params:scim:schemas:core:2.0:User")
            userName = (Get-Scenario029AccountName 1)
        }
        Assert-Equal -Actual $duplicate.Status -Expected 409 -Message "A duplicate userName is refused"
        Assert-Equal -Actual $duplicate.Json.scimType -Expected "uniqueness" -Message "The refusal's scimType"

        Invoke-InboundSchedule -Label "identity provider"
        $mvUser = Get-MetaverseUser -AccountName (Get-Scenario029AccountName 3)
        $jobTitle = @($mvUser.attributeValues | Where-Object { $_.attributeName -eq "Job Title" })
        Assert-Equal -Actual $jobTitle.Count -Expected 1 -Message "The person has a Job Title in the Metaverse"
        Assert-Equal -Actual $jobTitle[0].stringValue -Expected "Principal Engineer" -Message "The Job Title an identity provider pushed"

        Add-StepResult -Name "IdentityProvider" -Passed $true -Detail "Leniency through the real pipeline; a password refused; a pushed value reached the Metaverse"
    }

    # --- Conflict ---
    if ($Step -in @("Conflict", "All")) {
        Write-TestStep "Conflict" "The client exports over a change the identity provider made since the client read it"
        # The identity provider changes the person; the client still holds the version it read before.
        $person4 = Get-StoredUser -UserName (Get-Scenario029AccountName 4)
        $pushed = Invoke-ScimRequest -Method PATCH -Path "/Users/$($person4.id)" -Body @{
            schemas    = @("urn:ietf:params:scim:api:messages:2.0:PatchOp")
            Operations = @(@{ op = "replace"; path = "nickName"; value = "Pushed by the identity provider" })
        }
        Assert-Equal -Actual $pushed.Status -Expected 200 -Message "The identity provider's change is accepted"

        (Get-Person 4).LastName = "Renamed04"
        Write-HrCsv
        Invoke-HrImportAndSync -Label "conflict"
        $exportActivityId = Invoke-RunProfile -ConnectedSystemId $clientSystem.id -RunProfileName "Export" -Label "Client Export (conflict)" -AllowErrors
        $failed = @(Get-JIMActivity -Id $exportActivityId -ExecutionItems | Where-Object { $_.errorType -and $_.errorType -ne "NotSet" })
        Assert-Equal -Actual $failed.Count -Expected 1 -Message "Exports refused by the endpoint"
        Assert-Equal -Actual $failed[0].errorType -Expected "ConcurrencyConflict" -Message "The refusal is classified as a concurrency conflict"

        # An import reads the identity provider's change; the surname then goes through.
        Invoke-RunProfile -ConnectedSystemId $clientSystem.id -RunProfileName "Full Import" -Label "Client Full Import (conflict)" | Out-Null
        Invoke-RunProfile -ConnectedSystemId $clientSystem.id -RunProfileName "Full Synchronisation" -Label "Client Full Synchronisation (conflict)" | Out-Null
        Invoke-ClientExportAndConfirm -Label "after conflict"
        $stored = Get-StoredUser -UserName (Get-Scenario029AccountName 4)
        Assert-Equal -Actual $stored.name.familyName -Expected "Renamed04" -Message "The surname the client exported after the conflict"
        Assert-Equal -Actual $stored.nickName -Expected "Pushed by the identity provider" -Message "The identity provider's change survived"

        Add-StepResult -Name "Conflict" -Passed $true -Detail "412 reported as a concurrency conflict; both changes kept after an import"
    }

    # --- Deprovision ---
    if ($Step -in @("Deprovision", "All")) {
        Write-TestStep "Deprovision" "A leaver deleted from the endpoint, found gone by the client's Full Import"
        $leaver = Get-Person $UserCount
        $leaver.Active = $false
        Write-HrCsv
        Invoke-HrImportAndSync -Label "deprovision"
        Invoke-ClientExportAndConfirm -Label "deprovision"

        Assert-Condition -Condition ($null -eq (Get-StoredUser -UserName $leaver.AccountName)) -Message "The endpoint no longer returns the leaver"
        $clientUsers = Get-LiveObjects -ConnectedSystemId $clientSystem.id -ObjectTypeId $clientUserTypeId
        Assert-Equal -Actual $clientUsers.Count -Expected ($UserCount - 1) -Message "Users the client's Full Import finds"

        Invoke-InboundSchedule -Label "deprovision"
        $inboundLeaver = Find-Object -ConnectedSystemId $serviceProviderId -ObjectTypeId $serviceProviderUserTypeId -AttributeName "userName" -Value $leaver.AccountName
        Assert-Condition -Condition ($null -eq $inboundLeaver) -Message "The Service Provider's Delta Import read the deletion"

        Add-StepResult -Name "Deprovision" -Passed $true -Detail "DELETE, client Full Import and scheduled Delta Import agree the leaver is gone"
    }

    # --- Restore ---
    if ($Step -in @("Restore", "All")) {
        Write-TestStep "Restore" "Clearing the Service Provider's Connector Space and rebuilding it from the store"
        $before = (Get-LiveObjects -ConnectedSystemId $serviceProviderId -ObjectTypeId $serviceProviderUserTypeId).Count
        Clear-JIMConnectedSystem -Id $serviceProviderId -Wait -Force | Out-Null
        Assert-Equal -Actual (Get-LiveObjects -ConnectedSystemId $serviceProviderId -ObjectTypeId $serviceProviderUserTypeId).Count -Expected 0 `
            -Message "The Connector Space is empty after clearing"
        Invoke-RunProfile -ConnectedSystemId $serviceProviderId -RunProfileName "Full Import" -Label "Service Provider Full Import (restore)" | Out-Null
        Invoke-RunProfile -ConnectedSystemId $serviceProviderId -RunProfileName "Full Synchronisation" -Label "Service Provider Full Synchronisation (restore)" | Out-Null
        Assert-Equal -Actual (Get-LiveObjects -ConnectedSystemId $serviceProviderId -ObjectTypeId $serviceProviderUserTypeId).Count -Expected $before `
            -Message "Users rebuilt from the store, with nothing re-sent"
        $mvUser = Get-MetaverseUser -AccountName (Get-Scenario029AccountName 3)
        $jobTitle = @($mvUser.attributeValues | Where-Object { $_.attributeName -eq "Job Title" })
        Assert-Equal -Actual $jobTitle[0].stringValue -Expected "Principal Engineer" -Message "The pushed Job Title is back from the store"

        Add-StepResult -Name "Restore" -Passed $true -Detail "A Full Import rebuilt $before Users from the store"
    }

    # --- Summary ---
    if ($Step -in @("Summary", "All")) {
        Write-TestStep "Summary" "Each key's SCIM request summary"
        # The counts are flushed about every ten seconds.
        Start-Sleep -Seconds 15
        $summaries = @(Get-JIMActivity -Operation Receive -PageSize 100)
        $clientSummaries = @($summaries | Where-Object { $_.initiatedByName -eq $script:Scenario029ClientKeyName })
        $identityProviderSummaries = @($summaries | Where-Object { $_.initiatedByName -eq $script:Scenario029IdentityProviderKeyName })
        $clientCreated = ($clientSummaries | ForEach-Object { $_.scimRequests.created } | Measure-Object -Sum).Sum
        $clientDeleted = ($clientSummaries | ForEach-Object { $_.scimRequests.deleted } | Measure-Object -Sum).Sum
        $identityProviderRejected = ($identityProviderSummaries | ForEach-Object { $_.scimRequests.rejected } | Measure-Object -Sum).Sum
        Assert-Equal -Actual $clientCreated -Expected ($UserCount + 1) -Message "Creates counted for the client's key"
        Assert-Equal -Actual $clientDeleted -Expected 1 -Message "Deletes counted for the client's key"
        Assert-Equal -Actual $identityProviderRejected -Expected 2 -Message "Rejections counted for the identity provider's key (the password and the duplicate)"

        Add-StepResult -Name "Summary" -Passed $true -Detail "Creates, deletes and rejections counted per key"
    }

    $testResults.Success = $true
}
catch {
    Write-Host ""
    Write-Host "Scenario 029 failed: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    Add-StepResult -Name "Failure" -Passed $false -Detail "$_"
    $testResults.Success = $false
}

Write-Host ""
Write-TestSection "Scenario 029 Results"
foreach ($result in $testResults.Steps) {
    $colour = if ($result.Passed) { "Green" } else { "Red" }
    $mark = if ($result.Passed) { "PASS" } else { "FAIL" }
    Write-Host "  [$mark] $($result.Name): $($result.Detail)" -ForegroundColor $colour
}

return $testResults
