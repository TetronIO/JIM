# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Setup for Scenario 17: Initial Password Provisioning

.DESCRIPTION
    Configures JIM to set an Initial Password on every account its outbound Synchronisation Rule
    provisions into Samba AD, so that Scenario 17 can prove the account holder can actually use it.

    The provisioning substrate is Scenario 1's: HR CSV source, Samba AD target, an Export
    Synchronisation Rule that provisions Users. This script composes Setup-Scenario1.ps1 rather than
    rebuilding it, the same way Scenario 6 does, and then turns on the one thing Scenario 1 does not
    configure: the Initial Password.

    The password source is Static, which is the only source whose value the test can know. A generated
    password is never persisted and never returned, by design, so a test cannot bind with one; what
    Static gives up in realism it buys back in being provable end to end. The delivery path being
    exercised (stage a Pending Initial Password on a Create export, then set it through the Connector's
    password channel) is the same one the generated sources use.

    Expiry behaviour is RequireChangeAtNextSignIn and the account is enabled once the password lands,
    which together are the configuration an administrator would choose for a new starter, and the two
    the scenario then holds the directory to.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Data scale template, passed through to Setup-Scenario1.ps1

.PARAMETER DirectoryConfig
    Directory configuration hashtable from Get-DirectoryConfig

.PARAMETER ExportConcurrency
    LDAP Connector export concurrency, passed through to Setup-Scenario1.ps1

.PARAMETER MaxExportParallelism
    Connected System export parallelism, passed through to Setup-Scenario1.ps1

.PARAMETER ExpiryBehaviour
    What the directory should do with the Initial Password once it holds it. Defaults to
    RequireChangeAtNextSignIn, which is what Scenario 17 asserts against and what an administrator would choose
    for a new starter. Scenario 20 overrides it, because it needs accounts whose Initial Password signs in
    cleanly: it is proving that a *synchronised* password replaced that one, and a must-change account answers
    both the old and the new password with the same LDAP result code.

.EXAMPLE
    ./Setup-Scenario17.ps1 -ApiKey "jim_..." -Template Micro
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$true)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [string]$Template = "Micro",

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig,

    [Parameter(Mandatory=$false)]
    [int]$ExportConcurrency = 1,

    [Parameter(Mandatory=$false)]
    [int]$MaxExportParallelism = 1,

    [Parameter(Mandatory=$false)]
    [ValidateSet("RequireChangeAtNextSignIn", "ExpiresAccordingToTargetPolicy", "NeverExpires")]
    [string]$ExpiryBehaviour = "RequireChangeAtNextSignIn"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

# Default to SambaAD Primary if no config provided
if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary
}

# Active Directory is the only directory type this scenario can assert against: "must change at next
# sign-in" is an Active Directory behaviour, and JIM reports it as a downgrade everywhere else (see
# LdapConnectorPassword.BuildNonActiveDirectoryResult). Failing here is better than running a scenario
# whose central assertion is inapplicable.
if ($DirectoryConfig.UserObjectClass -ne "user") {
    throw "This setup requires Samba AD: it provisions accounts and enables them as the Initial Password lands, " +
          "which is an Active Directory operation with no portable equivalent on " +
          "$($DirectoryConfig.ConnectedSystemName). An account left disabled there cannot be signed in as, so " +
          "nothing built on this substrate can assert anything about the password."
}

<#
    The one password every account this rule provisions will carry.

    Chosen to satisfy a stock Active Directory complexity rule on its own merits (upper, lower, digit
    and symbol, well past the eight-character minimum) even though the test domain is provisioned with
    NOCOMPLEXITY=true. A password that only passes because complexity is switched off would make the
    scenario prove less than it appears to: the point is to drive the same path a real deployment does.

    It deliberately shares no token with the account names the HR template generates, because Active
    Directory refuses a password containing the sAMAccountName or a three-character-or-longer piece of
    the display name, and a scenario that tripped that would fail for a reason nobody was testing.
#>
$staticPassword = 'Chalkstream-7-Vault!'

Write-TestSection "Scenario 17 Setup: Initial Password Provisioning"

# Step 1: Build the provisioning substrate (HR CSV -> Metaverse -> Samba AD)
Write-TestStep "Step 1" "Running Setup-Scenario1.ps1 for the provisioning substrate"

$setupScript = "$PSScriptRoot/Setup-Scenario1.ps1"
if (-not (Test-Path $setupScript)) {
    throw "Setup script not found at: $setupScript"
}

$setupParams = @{
    JIMUrl = $JIMUrl
    ApiKey = $ApiKey
    Template = $Template
    DirectoryConfig = $DirectoryConfig
}
if ($PSBoundParameters.ContainsKey('ExportConcurrency')) {
    $setupParams.ExportConcurrency = $ExportConcurrency
}
if ($PSBoundParameters.ContainsKey('MaxExportParallelism')) {
    $setupParams.MaxExportParallelism = $MaxExportParallelism
}

$config = & $setupScript @setupParams
if (-not $config) {
    throw "Setup-Scenario1.ps1 returned no configuration"
}

Write-Host "  ✓ Provisioning substrate configured" -ForegroundColor Green

# Step 2: Connect to JIM for the Initial Password configuration
Write-TestStep "Step 2" "Connecting to JIM"

$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  ✓ Connected to $JIMUrl" -ForegroundColor Green

try {
    # Step 2b: Make sure the directory's Partition and Containers were actually enumerated
    #
    # Two observed behaviours make Setup-Scenario1's single hierarchy import unreliable against Samba AD, and
    # both surface far from their cause, as an export refused several steps later with "selected partition(s)
    # contain no enumerated containers":
    #
    #   1. The FIRST hierarchy import against a newly created Connected System fails with "An operation error
    #      occurred. 00002020: Operation unavailable without authentication". A second, identical import
    #      succeeds. Reproduced on every clean run of this scenario. Setup-Scenario1 catches the failure,
    #      warns, and carries on, so the Connected System is left with no Partitions at all.
    #   2. The import enumerates Containers only for a Partition already marked as selected, and
    #      Setup-Scenario1 selects the Partition *after* importing, so even a successful first import returns
    #      the Partition alone.
    #
    # This step recovers from both by driving the sequence to completion: import, select the Partition,
    # import again for the Containers, select the one accounts are provisioned into. Every stage is checked,
    # and a failure throws here rather than being carried into the run.
    Write-TestStep "Step 2b" "Verifying the directory Partition and Containers were enumerated"

    $ldapSystem = @(Get-JIMConnectedSystem) |
        Where-Object { $_.name -eq $DirectoryConfig.ConnectedSystemName } | Select-Object -First 1
    if (-not $ldapSystem) {
        throw "Could not find the Connected System '$($DirectoryConfig.ConnectedSystemName)'."
    }

    function Invoke-HierarchyImport {
        param([int]$ConnectedSystemId, [int]$Attempts = 3)
        for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
            try {
                Import-JIMConnectedSystemHierarchy -Id $ConnectedSystemId -ErrorAction Stop | Out-Null
                return
            }
            catch {
                if ($attempt -eq $Attempts) { throw }
                Write-Host "    Hierarchy import attempt $attempt failed, retrying: $($_.Exception.Message)" -ForegroundColor DarkYellow
                Start-Sleep -Seconds 2
            }
        }
    }

    function Get-SelectedPartition {
        param([int]$ConnectedSystemId)
        return @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ConnectedSystemId) |
            Where-Object { $_.selected } | Select-Object -First 1
    }

    function Find-ContainerByName {
        param($Containers, [string]$Name)
        foreach ($container in $Containers) {
            if ($container.name -eq $Name) { return $container }
            if ($container.childContainers) {
                $found = Find-ContainerByName -Containers $container.childContainers -Name $Name
                if ($found) { return $found }
            }
        }
        return $null
    }

    # Select the Partition, importing the hierarchy first where nothing was discovered.
    $selectedPartition = Get-SelectedPartition -ConnectedSystemId $ldapSystem.id
    if (-not $selectedPartition) {
        Write-Host "  No Partition is selected; importing the hierarchy..." -ForegroundColor Yellow
        Invoke-HierarchyImport -ConnectedSystemId $ldapSystem.id

        $partition = @(Get-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id) |
            Where-Object { $_.name -eq $DirectoryConfig.BaseDN -or $_.externalId -eq $DirectoryConfig.BaseDN } |
            Select-Object -First 1
        if (-not $partition) {
            throw "The hierarchy import discovered no Partition matching '$($DirectoryConfig.BaseDN)' on " +
                  "'$($DirectoryConfig.ConnectedSystemName)'. Nothing can be imported or exported without one."
        }

        Set-JIMConnectedSystemPartition -ConnectedSystemId $ldapSystem.id -PartitionId $partition.id -Selected $true | Out-Null
        $selectedPartition = Get-SelectedPartition -ConnectedSystemId $ldapSystem.id
        Write-Host "  ✓ Selected Partition '$($selectedPartition.name)'" -ForegroundColor Green
    }

    # Enumerate the Containers, which only happens for a Partition that is already selected.
    if (-not $selectedPartition.containers -or $selectedPartition.containers.Count -eq 0) {
        Write-Host "  Partition '$($selectedPartition.name)' has no enumerated Containers; re-importing the hierarchy..." -ForegroundColor Yellow
        Invoke-HierarchyImport -ConnectedSystemId $ldapSystem.id
        $selectedPartition = Get-SelectedPartition -ConnectedSystemId $ldapSystem.id

        if (-not $selectedPartition.containers -or $selectedPartition.containers.Count -eq 0) {
            throw "The Partition '$($selectedPartition.name)' still reports no Containers after a second " +
                  "hierarchy import. Exports cannot run without them; check the Connected System's connectivity."
        }
    }

    # Select the Container accounts are provisioned into.
    $targetContainerName = if ($DirectoryConfig.UserContainer -match "^[Oo][Uu]=([^,]+)") { $matches[1] } else { "Corp" }
    $targetContainer = Find-ContainerByName -Containers $selectedPartition.containers -Name $targetContainerName
    if (-not $targetContainer) {
        throw "Could not find the Container '$targetContainerName' under '$($selectedPartition.name)'. " +
              "Accounts are provisioned into $($DirectoryConfig.UserContainer), which must exist in the directory."
    }

    if (-not $targetContainer.selected) {
        Set-JIMConnectedSystemContainer -ConnectedSystemId $ldapSystem.id -ContainerId $targetContainer.id -Selected $true | Out-Null
    }
    Write-Host "  ✓ Partition '$($selectedPartition.name)' carries $($selectedPartition.containers.Count) Container(s); '$targetContainerName' is selected" -ForegroundColor Green

    # Step 2c: Let the Initial Password be the only writer of the account's enabled state
    #
    # Setup-Scenario1 exports userAccountControl through an expression, to exercise integer attribute flow.
    # The Initial Password's EnableAccount option writes the same attribute, so leaving both in place gives
    # one attribute two writers: the export asserts the flow's value, the password channel then writes its
    # own, and the confirming import reports "We exported a change, but did not get confirmation of it ...
    # 1 attribute(s): userAccountControl. Will attempt to reassert the change on the next export run."
    #
    # Removing the mapping here is what an administrator provisioning through the Initial Password should do:
    # the account's enabled state belongs to whichever mechanism sets the password, because Active Directory
    # will not enable an account that does not already hold a policy-compliant one.
    Write-TestStep "Step 2c" "Removing the userAccountControl Attribute Flow so the Initial Password owns the enabled state"

    $exportRuleName = "$($DirectoryConfig.ConnectedSystemName) Export Users"
    $exportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq $exportRuleName } | Select-Object -First 1

    if (-not $exportRule) {
        throw "Could not find the Export Synchronisation Rule '$exportRuleName'. Setup-Scenario1.ps1 " +
              "creates it; if that has been renamed, this scenario needs updating to match."
    }

    $accountControlMapping = @(Get-JIMSyncRuleMapping -SyncRuleId $exportRule.id) |
        Where-Object { $_.TargetConnectedSystemAttributeName -eq 'userAccountControl' } | Select-Object -First 1

    if ($accountControlMapping) {
        try {
            Remove-JIMSyncRuleMapping -SyncRuleId $exportRule.id -MappingId $accountControlMapping.Id -Force -ErrorAction Stop | Out-Null
        }
        catch {
            # Deleting a Synchronisation Rule Mapping currently fails with an Entity Framework tracking
            # conflict on SyncRuleMappingSource. Naming it here saves the next person diagnosing an opaque
            # HTTP 400 from the setup script; there is no way to configure around it, because the mapping has
            # to go for the Initial Password to own the account's enabled state, and EnableAccount cannot
            # simply be switched off (false means "disable the account", not "leave it alone").
            throw "Could not remove the userAccountControl Attribute Flow from '$exportRuleName', so this " +
                  "scenario cannot run: the Initial Password's EnableAccount option and that flow would both " +
                  "write the same attribute, and the confirming import would report the export as " +
                  "unconfirmed. Underlying error: $($_.Exception.Message)"
        }
        Write-Host "  ✓ Removed the userAccountControl Attribute Flow (mapping $($accountControlMapping.Id))" -ForegroundColor Green
    }
    else {
        Write-Host "  No userAccountControl Attribute Flow to remove" -ForegroundColor Gray
    }

    # Step 3: Enable the Initial Password on the Export Synchronisation Rule
    Write-TestStep "Step 3" "Enabling the Initial Password on the Export Synchronisation Rule"

    Write-Host "  Export Synchronisation Rule '$exportRuleName' (ID: $($exportRule.id))" -ForegroundColor Gray

    $securePassword = ConvertTo-SecureString -String $staticPassword -AsPlainText -Force

    Set-JIMSyncRuleInitialPassword `
        -Id $exportRule.id `
        -Enable `
        -Source Static `
        -StaticPassword $securePassword `
        -ExpiryBehaviour $ExpiryBehaviour `
        -EnableAccount $true `
        -ChangeReason "Integration test substrate: provisioned accounts carry a known, usable Initial Password" | Out-Null

    Write-Host "  ✓ Initial Password enabled (Source: Static, Expiry: $ExpiryBehaviour, account enabled)" -ForegroundColor Green

    # Step 4: Read the configuration back
    # The password itself is write-only and is never returned, so what is verified here is that the
    # rule is switched on and carrying the settings the scenario's assertions depend on. A rule that
    # silently kept its previous settings would otherwise surface as a puzzling directory failure
    # several minutes later.
    Write-TestStep "Step 4" "Verifying the stored Initial Password configuration"

    $storedConfig = Get-JIMSyncRuleInitialPassword -Id $exportRule.id

    Assert-Condition -Condition ($storedConfig.enabled -eq $true) `
        -Message "Initial Password is enabled on '$exportRuleName'"
    Assert-Equal -Actual $storedConfig.source -Expected "Static" `
        -Message "Initial Password source is Static"
    Assert-Equal -Actual $storedConfig.expiryBehaviour -Expected $ExpiryBehaviour `
        -Message "Expiry behaviour is $ExpiryBehaviour"
    Assert-Condition -Condition ($storedConfig.enableAccount -eq $true) `
        -Message "The account is enabled once the password is set"
    Assert-Condition -Condition ($storedConfig.staticPasswordSet -eq $true) `
        -Message "A static password is stored (the value itself is never returned)"
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

# Summary
Write-TestSection "Scenario 17 Setup Complete"
Write-Host "Export Synchronisation Rule: $exportRuleName (ID: $($exportRule.id))" -ForegroundColor Cyan
Write-Host "Initial Password source:     Static" -ForegroundColor Cyan
Write-Host "Expiry behaviour:            $ExpiryBehaviour" -ForegroundColor Cyan
Write-Host ""

# Return Scenario 1's configuration, plus what the assertions need to reach the directory as the
# account holder. The static password travels in clear here because the scenario has to bind with it;
# it is a throwaway credential in a throwaway container, in the same class as the test domain's own
# administrator password in Get-DirectoryConfig.
$config.ExportSyncRuleId = $exportRule.id
$config.InitialPassword = $staticPassword
return $config
