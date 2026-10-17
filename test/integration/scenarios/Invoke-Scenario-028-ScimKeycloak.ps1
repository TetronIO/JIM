# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    SCIM 2.0 Client Connector against Keycloak's SCIM API (provision, large membership, Keycloak delta, deprovision, protected accounts)

.DESCRIPTION
    Drives the SCIM 2.0 Client Connector through JIM against a real Keycloak (the keycloak-scim container), so a
    Keycloak release that changes the behaviours JIM works around (#2094) fails a run rather than a customer (#2349).

    Each step asserts through JIM: Pending Exports confirmed by an import that reads Keycloak back, and the values
    imported Connected System Objects hold.

      Provision        People and two groups provisioned. Keycloak refuses a group created with members, so JIM
                       creates each group then adds its members; the large group's 110 adds travel as one operation.
                       Keycloak returns a group's members only when asked for them by name, and its schema leaves
                       out what is inside members and emails; the confirming import reads every member back.
                       userName and email are lowercased by the export, as Keycloak stores them.
      DeltaImport      Keycloak does not move a resource's last-modified date for most changes, so JIM recognises it
                       and reads everything, saying why on the Activity.
      LargeMembership  A change of over 100 members to the large group, which Keycloak's 100-operation PATCH cap would
                       refuse in one request.
      Disable          A leaver disabled (active=false).
      Delete           A leaver moved out of scope and deleted.
      Protected        An account holding an administrative role, which Keycloak refuses every SCIM change to, fails
                       its export as Access Denied with a message saying to scope it out.

.PARAMETER Step
    Which step to run (default: All)

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER UserCount
    How many people the HR CSV holds (default: 120). Must exceed the large group's 110 members.

.PARAMETER CertificatePath
    Keycloak's certificate. When omitted, it is copied out of the keycloak-scim container.

.PARAMETER Template
    Accepted for runner compatibility. This scenario's data is its own CSV.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility. This scenario has no directory target.

.EXAMPLE
    ./Run-IntegrationTests.ps1 -Scenario Scenario-028-ScimKeycloak
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Provision", "DeltaImport", "LargeMembership", "Disable", "Delete", "Protected", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [ValidateRange(110, 1000)]
    [int]$UserCount = 120,

    [Parameter(Mandatory=$false)]
    [string]$CertificatePath,

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
. "$PSScriptRoot/../utils/Scenario-028-Data.ps1"

$keycloakContainer = "keycloak-scim"

Write-TestSection "Scenario 028: SCIM 2.0 Client Connector against Keycloak"
Write-Host "Step:   $Step" -ForegroundColor Gray
Write-Host "People: $UserCount" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "SCIM 2.0 Client Connector against Keycloak"
    Steps = @()
    Success = $false
}

function Add-StepResult {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $testResults.Steps += @{ Name = $Name; Passed = $Passed; Detail = $Detail }
}

# The HR data every step changes and rewrites. Kept here so each step starts from what the step before left.
$people = [System.Collections.Generic.List[object]]::new()
foreach ($person in (New-Scenario028People -Count $UserCount)) { $people.Add($person) }
$groups = New-Scenario028Groups -UserCount $UserCount

function Write-HrCsv {
    Write-Scenario028HrCsv -People $people.ToArray() -Groups $groups
}

function Invoke-HrImportAndSync {
    param([string]$Label)

    $result = Start-JIMRunProfile -ConnectedSystemId $hrSystem.id -RunProfileName "Full Import" -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $result.activityId -Name "HR Full Import ($Label)"

    $result = Start-JIMRunProfile -ConnectedSystemId $hrSystem.id -RunProfileName "Full Synchronisation" -Wait -PassThru
    Assert-ActivitySuccess -ActivityId $result.activityId -Name "HR Full Synchronisation ($Label)"
}

# Exports to Keycloak, then reads it back. A change that references something not yet created (a member whose
# account the same run creates) is sent once that exists, so this goes round until nothing is left, and fails if a
# round makes no progress: a Pending Export the confirming import cannot confirm is a value Keycloak does not hold.
function Invoke-KeycloakExportAndConfirm {
    param([string]$Label, [int]$MaximumRounds = 4)

    $remaining = -1
    for ($round = 1; $round -le $MaximumRounds; $round++) {
        $result = Start-JIMRunProfile -ConnectedSystemId $keycloakSystem.id -RunProfileName "Export" -Wait -PassThru
        Assert-ExportSuccess -ActivityId $result.activityId -Name "Keycloak Export ($Label, round $round)"

        $result = Start-JIMRunProfile -ConnectedSystemId $keycloakSystem.id -RunProfileName "Full Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Keycloak Confirming Full Import ($Label, round $round)"

        $result = Start-JIMRunProfile -ConnectedSystemId $keycloakSystem.id -RunProfileName "Full Synchronisation" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Keycloak Full Synchronisation ($Label, round $round)"

        $pending = @(Get-JIMPendingExport -ConnectedSystemId $keycloakSystem.id -All)
        if ($pending.Count -eq 0) { return }
        if ($pending.Count -eq $remaining) {
            $summary = ($pending | Select-Object -First 5 | ForEach-Object { "$($_.changeType) $($_.status)" }) -join "; "
            throw "Keycloak export ($Label) stopped making progress with $($pending.Count) Pending Export(s) left: $summary"
        }
        $remaining = $pending.Count
    }

    throw "Keycloak export ($Label) still had Pending Exports after $MaximumRounds rounds."
}

function Get-KeycloakObjects {
    param([int]$ObjectTypeId)
    return @(Get-JIMConnectedSystemObject -ConnectedSystemId $keycloakSystem.id -ObjectTypeId $ObjectTypeId -All |
        Where-Object { $_.status -ne "Obsolete" })
}

function Get-KeycloakValues {
    param($Object, [string]$AttributeName)
    return @(Get-JIMConnectedSystemObjectAttributeValue -ConnectedSystemId $keycloakSystem.id -CsoId $Object.id -AttributeName $AttributeName -All)
}

# The imported Keycloak object whose single-valued attribute holds $Value, compared without regard to case.
function Find-KeycloakObject {
    param([int]$ObjectTypeId, [string]$AttributeName, [string]$Value)
    foreach ($object in (Get-KeycloakObjects -ObjectTypeId $ObjectTypeId)) {
        $values = Get-KeycloakValues -Object $object -AttributeName $AttributeName
        if ($values | Where-Object { $_.stringValue -ieq $Value }) { return $object }
    }
    return $null
}

function Get-GroupMemberCount {
    param([string]$DisplayName)
    $group = Find-KeycloakObject -ObjectTypeId $groupTypeId -AttributeName "displayName" -Value $DisplayName
    if (-not $group) { throw "Keycloak holds no group '$DisplayName' after the confirming import." }
    return (Get-KeycloakValues -Object $group -AttributeName "members").Count
}

try {
    if ($Step -eq "All") {
        Write-TestStep "Setup" "Configuring Keycloak and JIM for the scenario"

        if (-not $CertificatePath) {
            # The container mints its certificate before Keycloak starts listening, and the runner waits for
            # Keycloak to report ready, so the file is there; the retry covers a scenario run on its own.
            $CertificatePath = Join-Path ([System.IO.Path]::GetTempPath()) "keycloak-scim.pem"
            $copied = $false
            for ($attempt = 0; $attempt -lt 12; $attempt++) {
                docker cp "${keycloakContainer}:/tmp/tls/keycloak-scim.pem" $CertificatePath 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0 -and (Test-Path $CertificatePath)) { $copied = $true; break }
                Start-Sleep -Seconds 5
            }
            if (-not $copied) {
                throw "Could not copy the certificate out of the $keycloakContainer container. Is it running? (Run-IntegrationTests.ps1 starts it with the 'keycloak-scim' compose profile.)"
            }
            Write-Host "  OK Copied Keycloak's certificate from the $keycloakContainer container" -ForegroundColor Green
        }

        & "$PSScriptRoot/../Setup-Scenario-028.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -KeycloakContainer $keycloakContainer `
            -CertificatePath $CertificatePath -UserCount $UserCount
    }

    Write-TestStep "Step 0" "Connecting to JIM and resolving the Connected Systems"

    if (-not $ApiKey) { throw "API key required for authentication" }

    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $systems = @(Get-JIMConnectedSystem)
    $hrSystem = $systems | Where-Object { $_.name -eq $script:Scenario028HrSystemName }
    $keycloakSystem = $systems | Where-Object { $_.name -eq $script:Scenario028KeycloakSystemName }
    if (-not $hrSystem -or -not $keycloakSystem) { throw "The scenario's Connected Systems were not found. Run Setup-Scenario-028.ps1 first." }

    $objectTypes = Get-JIMConnectedSystem -Id $keycloakSystem.id -ObjectTypes
    $userTypeId = ($objectTypes | Where-Object { $_.name -eq "User" }).id
    $groupTypeId = ($objectTypes | Where-Object { $_.name -eq "Group" }).id
    $smallGroup = $groups | Where-Object { $_.AccountName -eq "kc-small" }
    $largeGroup = $groups | Where-Object { $_.AccountName -eq "kc-large" }

    Write-Host "  OK HR source (ID: $($hrSystem.id)), Keycloak (ID: $($keycloakSystem.id))" -ForegroundColor Green

    # --- Provision people and groups ---
    if ($Step -in @("Provision", "All")) {
        Write-TestStep "Provision" "Provisioning $UserCount people and $($groups.Count) groups to Keycloak"

        Write-HrCsv
        Invoke-HrImportAndSync -Label "baseline"
        Invoke-KeycloakExportAndConfirm -Label "provision"

        $users = Get-KeycloakObjects -ObjectTypeId $userTypeId
        # The protected account Keycloak already held is imported too, and is not one of the scenario's people.
        $provisioned = @($users | Where-Object { $_.displayName -ne $script:Scenario028ProtectedUserName })
        Assert-Condition -Condition ($provisioned.Count -ge $UserCount) `
            -Message "People provisioned to Keycloak and read back (found $($provisioned.Count) users, expected at least $UserCount)"

        # Keycloak lowercased what it was sent; the export sent it lowercased already, which is why it confirmed.
        $sample = Find-KeycloakObject -ObjectTypeId $userTypeId -AttributeName "userName" -Value (Get-Scenario028AccountName 1)
        if (-not $sample) { throw "Keycloak holds no user '$(Get-Scenario028AccountName 1)' after the confirming import." }
        $sampleUserName = (Get-KeycloakValues -Object $sample -AttributeName "userName")[0].stringValue
        $sampleEmail = (Get-KeycloakValues -Object $sample -AttributeName "emails.work")[0].stringValue
        Assert-Equal -Actual $sampleUserName -Expected (Get-Scenario028AccountName 1).ToLowerInvariant() -Message "userName as Keycloak holds it"
        Assert-Equal -Actual $sampleEmail -Expected "$(Get-Scenario028AccountName 1)@Example.COM".ToLowerInvariant() -Message "Email as Keycloak holds it"

        Assert-Equal -Actual (Get-GroupMemberCount -DisplayName $smallGroup.DisplayName) -Expected $smallGroup.Members.Count `
            -Message "Members of '$($smallGroup.DisplayName)' read back from Keycloak"
        Assert-Equal -Actual (Get-GroupMemberCount -DisplayName $largeGroup.DisplayName) -Expected $largeGroup.Members.Count `
            -Message "Members of '$($largeGroup.DisplayName)' read back from Keycloak"

        Add-StepResult -Name "Provision" -Passed $true -Detail "$($provisioned.Count) users; groups of $($smallGroup.Members.Count) and $($largeGroup.Members.Count) members"
        Write-Host "  OK People and groups provisioned and confirmed" -ForegroundColor Green
    }

    # --- Delta Import reads everything from Keycloak ---
    if ($Step -in @("DeltaImport", "All")) {
        Write-TestStep "DeltaImport" "Delta Import from Keycloak reads every resource and says why"

        $result = Start-JIMRunProfile -ConnectedSystemId $keycloakSystem.id -RunProfileName "Delta Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Keycloak Delta Import" -AllowedWarningMessagePattern '.*Keycloak.*'
        $activity = Get-JIMActivity -Id $result.activityId
        Assert-Condition -Condition ("$($activity.warningMessage)" -match 'Keycloak') `
            -Message "The Delta Import's Activity says Keycloak was read in full (warning: '$($activity.warningMessage)')"

        Add-StepResult -Name "DeltaImport" -Passed $true -Detail "Read in full with the Keycloak warning"
        Write-Host "  OK Delta Import read Keycloak in full and said why" -ForegroundColor Green
    }

    # --- A membership change of more than 100 ---
    if ($Step -in @("LargeMembership", "All")) {
        # Removing 105 members is 105 operations, each naming its member, so the change has to be split.
        $newMembers = @(106..$UserCount | ForEach-Object { Get-Scenario028AccountName $_ })
        Write-TestStep "LargeMembership" "Changing '$($largeGroup.DisplayName)' from $($largeGroup.Members.Count) members to $($newMembers.Count)"

        $largeGroup.Members = $newMembers
        Write-HrCsv
        Invoke-HrImportAndSync -Label "large membership change"
        Invoke-KeycloakExportAndConfirm -Label "large membership change"

        Assert-Equal -Actual (Get-GroupMemberCount -DisplayName $largeGroup.DisplayName) -Expected $newMembers.Count `
            -Message "Members of '$($largeGroup.DisplayName)' read back from Keycloak after the change"

        Add-StepResult -Name "LargeMembership" -Passed $true -Detail "Membership changed to $($newMembers.Count) and confirmed"
        Write-Host "  OK A membership change of over 100 operations was applied and confirmed" -ForegroundColor Green
    }

    # --- A leaver disabled ---
    if ($Step -in @("Disable", "All")) {
        $leaver = $people[2]
        Write-TestStep "Disable" "Disabling $($leaver.AccountName)"

        $leaver.Active = $false
        Write-HrCsv
        Invoke-HrImportAndSync -Label "disable"
        Invoke-KeycloakExportAndConfirm -Label "disable"

        $object = Find-KeycloakObject -ObjectTypeId $userTypeId -AttributeName "userName" -Value $leaver.AccountName
        if (-not $object) { throw "Keycloak holds no user '$($leaver.AccountName)' after the confirming import." }
        $active = (Get-KeycloakValues -Object $object -AttributeName "active")[0].boolValue
        Assert-Equal -Actual "$active" -Expected "False" -Message "active as Keycloak holds it for $($leaver.AccountName)"

        Add-StepResult -Name "Disable" -Passed $true -Detail "$($leaver.AccountName) disabled"
        Write-Host "  OK $($leaver.AccountName) disabled in Keycloak" -ForegroundColor Green
    }

    # --- A leaver deleted ---
    if ($Step -in @("Delete", "All")) {
        $leaver = $people[3]
        Write-TestStep "Delete" "Deleting $($leaver.AccountName) by moving them out of scope"

        $leaver.Department = "Alumni"
        Write-HrCsv
        Invoke-HrImportAndSync -Label "delete"
        Invoke-KeycloakExportAndConfirm -Label "delete"

        $object = Find-KeycloakObject -ObjectTypeId $userTypeId -AttributeName "userName" -Value $leaver.AccountName
        Assert-Condition -Condition ($null -eq $object) -Message "$($leaver.AccountName) is gone from Keycloak"

        Add-StepResult -Name "Delete" -Passed $true -Detail "$($leaver.AccountName) deleted"
        Write-Host "  OK $($leaver.AccountName) deleted from Keycloak" -ForegroundColor Green
    }

    # --- An account Keycloak protects ---
    if ($Step -in @("Protected", "All")) {
        $protectedName = $script:Scenario028ProtectedUserName
        Write-TestStep "Protected" "Exporting to $protectedName, an account Keycloak protects"

        # JIM finds the account Keycloak already holds and joins it rather than creating another, then has changes
        # for it that Keycloak will not take.
        $people.Add([pscustomobject]@{
            AccountName = $protectedName
            FirstName   = "Kc"
            LastName    = "Protected"
            Email       = "$protectedName@example.com"
            Department  = $script:Scenario028InScopeDepartment
            Active      = $true
        })
        Write-HrCsv
        $result = Start-JIMRunProfile -ConnectedSystemId $keycloakSystem.id -RunProfileName "Full Import" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Keycloak Full Import (protected)"
        Invoke-HrImportAndSync -Label "protected"
        $result = Start-JIMRunProfile -ConnectedSystemId $keycloakSystem.id -RunProfileName "Full Synchronisation" -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $result.activityId -Name "Keycloak Full Synchronisation (protected)"

        $result = Start-JIMRunProfile -ConnectedSystemId $keycloakSystem.id -RunProfileName "Export" -Wait -PassThru
        $refused = @(Get-JIMActivity -Id $result.activityId -ExecutionItems | Where-Object { $_.errorType -and $_.errorType -ne 'NotSet' })
        Assert-Equal -Actual $refused.Count -Expected 1 -Message "Exports Keycloak refused"
        Assert-Equal -Actual "$($refused[0].errorType)" -Expected "AccessDenied" -Message "How the refusal is classified"

        $item = Get-JIMActivityExecutionItem -Id $refused[0].id
        Assert-Condition -Condition ("$($item.errorMessage)" -match 'scope those objects out of the Synchronisation Rule') `
            -Message "The refusal tells the administrator to scope the account out (message: '$($item.errorMessage)')"

        Add-StepResult -Name "Protected" -Passed $true -Detail "Export to $protectedName refused as Access Denied"
        Write-Host "  OK Keycloak's refusal reported as Access Denied, with the remedy" -ForegroundColor Green
    }

    $testResults.Success = $true
    Write-TestSection "Scenario 028 Complete"
    foreach ($stepResult in $testResults.Steps) {
        Write-Host "  [PASS] $($stepResult.Name): $($stepResult.Detail)" -ForegroundColor Green
    }
}
catch {
    $testResults.Success = $false
    Write-Failure "Scenario 028 failed: $_"
    throw
}

return $testResults
