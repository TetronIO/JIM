# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Test Scenario 19: Auxiliary Classes (merge, import, export class convergence, discovery)

.DESCRIPTION
    Validates auxiliary object class support on RFC 4512 directories (#492) end to end against
    the JIM-owned jimBadgeHolder auxiliary class and the DIT Content Rule on jimPerson that the
    OpenLDAP test image's schema defines (docker/openldap/scripts/01-add-second-suffix.sh).

    Topology (configured by Setup-Scenario19.ps1, seeded by Populate-OpenLDAP-Scenario19.ps1),
    following Scenario 14's two-suffix shape: "Scenario 19 Source" (dc=yellowstone,dc=local)
    imports and projects; "Scenario 19 Target" (dc=glitterband,dc=local) exports. Six users
    share Employee ID across both suffixes so each pair joins to one Metaverse Object. Source
    indices 0-2 carry jimBadgeHolder with jimBadgeNumber (the class's MUST) and jimBadgeColour;
    index 1 (Boris) lists the auxiliary class before the structural one in objectClass. Target
    entries carry no auxiliary class at all.

    Tests, in dependency order (each -Step value runs every step up to and including itself,
    because the later steps build on the earlier steps' configuration and state):

    1. Merge - Get-JIMConnectedSystemAuxiliaryClass lists jimBadgeHolder for jimPerson and
       marks it permitted by the Connected System (the DIT Content Rule read live from the
       directory) and suggested. Set-JIMConnectedSystemAuxiliaryClass merges it (both systems),
       Import-JIMConnectedSystemSchema refreshes, and the contributed attributes
       (jimBadgeNumber required, jimBadgeColour, jimBadgeIssued) appear on jimPerson carrying
       'jimBadgeHolder' in their ClassName. The badge attribute flow mappings are then created:
       import (Source) jimBadgeNumber/jimBadgeColour -> Badge Number/Badge Colour, export
       (Target) the same pair outbound.

    2. Import - Full Import (Source) reads exactly six objects: an entry carrying both
       jimPerson and jimBadgeHolder produces exactly ONE Connected System Object whichever
       order the directory serves its objectClass values in (Boris's entry lists the auxiliary
       class first). Full Synchronisation projects; the badge values are asserted on the
       Metaverse Objects. Target then imports and joins, and each Employee ID resolves to
       exactly one Metaverse Object (no duplicate projection).

    3. DeltaConvergence - the Export (Target) writes each badge carrier's values to the
       Glitterband counterpart that lacks the class, and the class arrives in the same modify:
       read back over ldapsearch, Amber's Target entry now carries objectClass jimBadgeHolder
       alongside jimPerson, with jimBadgeNumber and jimBadgeColour present. A Full Import
       (Target) then confirms the exports.

    4. MustEnforcement - roomNumber -> Badge Colour is added as a second-priority contributor
       (Source), giving Dora (S19-3, no badge) a Badge Colour with no Badge Number. The export
       that would first flow her colour must add jimBadgeHolder, whose MUST (jimBadgeNumber)
       neither the export nor her Target entry can satisfy, so it is REFUSED before being sent,
       and the Run Profile Execution Item names the missing attribute. Her Target entry is
       asserted untouched over ldapsearch. The step then removes the roomNumber mapping and
       re-synchronises both systems so the refused Pending Export is withdrawn and later steps
       start clean.

    5. CarrierProvisioning - Gina (S19-6), a badge carrier, is added to Yellowstone only.
       jimBadgeHolder is selected as its own Object Type on Target with 'account' as its
       Structural Carrier Class (the classic account + posixAccount pairing: the fixture's
       jimBadgeHolder MAYs uid, which satisfies account's MUST and the uid= RDN). A dedicated
       provisioning export rule, scoped to Gina's Employee ID, creates her Target entry; read
       back over ldapsearch it carries BOTH classes (account structural carrier plus
       jimBadgeHolder) with her badge attributes.

    6. Discovery - a FullScan discovery run (Source) reads all seven entries and reports
       jimBadgeHolder observed on exactly the four carrier entries (Amber, Boris, Clara, Gina),
       named against the jimPerson structural type. A QuickSample run with a sample size of two
       then reads at most two entries per Object Type and reports a count bounded by it.

.PARAMETER Step
    How far to run (Merge, Import, DeltaConvergence, MustEnforcement, CarrierProvisioning,
    Discovery, All). Steps are cumulative: each value runs every step up to and including
    itself, because later steps depend on the configuration and state the earlier ones
    establish. All is equivalent to Discovery.

.PARAMETER Template
    Accepted for runner compatibility. This scenario seeds its own small, fixed, deterministic
    user set (see Populate-OpenLDAP-Scenario19.ps1) and ignores the template.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER WaitSeconds
    Seconds to wait between steps for JIM processing (default: 0)

.PARAMETER SkipPopulate
    Skip re-seeding OpenLDAP. Scenario 19 is excluded from OpenLDAP snapshot handling in
    Run-IntegrationTests.ps1 (its dataset is small and bespoke), so the runner never sets this
    automatically; it exists for manual re-runs against an already-populated environment.

.PARAMETER DirectoryConfig
    Directory-specific configuration hashtable from Get-DirectoryConfig. Must be OpenLDAP.

.EXAMPLE
    ./Invoke-Scenario19-AuxiliaryClasses.ps1 -Step All -ApiKey "jim_..."

.EXAMPLE
    ./Invoke-Scenario19-AuxiliaryClasses.ps1 -Step Import -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Merge", "Import", "DeltaConvergence", "MustEnforcement", "CarrierProvisioning", "Discovery", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers. LDAP-Helpers supplies the read side (Invoke-LDAPSearch / Expand-LDIFFoldedLine):
# the class-convergence and refusal assertions have to observe the directory's ACTUAL state,
# which only an independent LDAP read can show.
. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/LDAP-Helpers.ps1"

if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source
}
if ($DirectoryConfig.UserObjectClass -ne "inetOrgPerson") {
    throw "Scenario 19 (Auxiliary Classes) is OpenLDAP only. Run-IntegrationTests.ps1 should have rejected this combination before this script was invoked."
}

$sourceSystemName = "Scenario 19 Source"
$targetSystemName = "Scenario 19 Target"

# Re-derive Source (Yellowstone) and Target (Glitterband) directory configuration independently
# of whichever single OpenLDAP instance was passed in, mirroring Setup-Scenario19.ps1: the
# CarrierProvisioning step mutates Yellowstone directly (ldapadd) and every export assertion
# reads Glitterband, so both suffixes' bind credentials are needed.
$sourceLdapConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source
$targetLdapConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Target
$sourceLdapUri = "ldap://localhost:$($sourceLdapConfig.Port)"

function Invoke-Scenario19LdapAdd {
    <#
    .SYNOPSIS
        Runs an LDIF payload through ldapadd against a Scenario 19 OpenLDAP suffix, following
        the established temp-file + docker-exec pattern (Populate-OpenLDAP-Scenario19.ps1).
    #>
    param(
        [Parameter(Mandatory=$true)] [string]$ContainerName,
        [Parameter(Mandatory=$true)] [string]$LdapUri,
        [Parameter(Mandatory=$true)] [string]$BindDN,
        [Parameter(Mandatory=$true)] [string]$BindPassword,
        [Parameter(Mandatory=$true)] [string]$Ldif
    )

    $ldifPath = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $ldifPath -Value $Ldif -NoNewline
    try {
        $result = bash -c "cat '$ldifPath' | docker exec -i $ContainerName ldapadd -x -H '$LdapUri' -D '$BindDN' -w '$BindPassword' -c" 2>&1
        if ($LASTEXITCODE -ne 0 -and "$result" -notmatch "already exists") {
            throw "ldapadd failed (exit code $LASTEXITCODE): $result"
        }
    }
    finally {
        Remove-Item -Path $ldifPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-Scenario19LdapAttributeValues {
    <#
    .SYNOPSIS
        Reads EVERY value of an attribute straight from a Scenario 19 OpenLDAP suffix, bypassing
        JIM.

    .DESCRIPTION
        The convergence and refusal assertions must observe the Connected System's ACTUAL state,
        not JIM's view of it: a Connected System Object mirrors whatever the last import saw, so
        asserting against it would only prove JIM remembers staging a Pending Export, never that
        the directory itself changed (or stayed unchanged). Multi-valued reading matters here
        because objectClass is the attribute under test.

        Returns an empty array when the entry carries no value for the attribute. Throws when
        the entry itself is missing, so an absent entry cannot pass an "attribute absent"
        assertion by accident; use -AllowMissingEntry to get an empty array for that case
        instead (the provisioning step's precondition needs it).
    #>
    param(
        [Parameter(Mandatory=$true)] [hashtable]$LdapConfig,
        [Parameter(Mandatory=$true)] [string]$Uid,
        [Parameter(Mandatory=$true)] [string]$AttributeName,
        [switch]$AllowMissingEntry
    )

    $raw = Invoke-LDAPSearch -ContainerName $LdapConfig.ContainerName -Server "localhost" -Port $LdapConfig.Port `
        -BaseDN $LdapConfig.UserContainer -BindDN $LdapConfig.BindDN -BindPassword $LdapConfig.BindPassword `
        -Filter "(uid=$Uid)" -Attributes @($AttributeName)
    if ($null -eq $raw) {
        throw "ldapsearch for uid=$Uid under $($LdapConfig.UserContainer) returned nothing; the directory is unreachable."
    }

    $rawText = $raw -join "`n"
    if ($rawText -notmatch "(?m)^dn:") {
        if ($AllowMissingEntry) { return @() }
        throw "No entry found for uid=$Uid under $($LdapConfig.UserContainer)."
    }

    $values = @()
    foreach ($line in (Expand-LDIFFoldedLine -RawLdif $rawText)) {
        if ($line.StartsWith("${AttributeName}:: ", [System.StringComparison]::OrdinalIgnoreCase)) {
            $values += [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($line.Substring($AttributeName.Length + 3)))
        }
        elseif ($line.StartsWith("${AttributeName}: ", [System.StringComparison]::OrdinalIgnoreCase)) {
            $values += $line.Substring($AttributeName.Length + 2)
        }
    }

    return $values
}

Write-TestSection "Scenario 19: Auxiliary Classes"
Write-Host "Step:     $Step (steps are cumulative)" -ForegroundColor Gray
Write-Host "Template: $Template (ignored - fixed six-user dataset)" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Auxiliary Classes"
    Template = $Template
    Steps = @()
    Success = $false
}

# Cumulative dispatch: each step builds on the previous one's configuration and state, so a
# named -Step runs everything up to and including itself.
$stepOrder = @("Merge", "Import", "DeltaConvergence", "MustEnforcement", "CarrierProvisioning", "Discovery")
$lastStepIndex = if ($Step -eq "All") { $stepOrder.Count - 1 } else { $stepOrder.IndexOf($Step) }

try {
    # ========================================================================
    # Step 0: Setup and Verification
    # ========================================================================
    Write-TestSection "Step 0: Setup and Verification"

    if (-not $ApiKey) {
        throw "API key required for authentication"
    }

    Write-Host "Waiting for OpenLDAP to be healthy..." -ForegroundColor Gray
    $maxWaitSeconds = 120
    $elapsed = 0
    $interval = 5
    $containerStatus = ""
    while ($elapsed -lt $maxWaitSeconds) {
        $containerStatus = docker inspect --format='{{.State.Health.Status}}' $DirectoryConfig.ContainerName 2>&1
        if ($containerStatus -eq "healthy") { break }
        Start-Sleep -Seconds $interval
        $elapsed += $interval
    }
    if ($containerStatus -ne "healthy") {
        throw "$($DirectoryConfig.ContainerName) container did not become healthy within ${maxWaitSeconds}s (status: $containerStatus)"
    }
    Write-Host "  OK OpenLDAP is healthy" -ForegroundColor Green

    if (-not $SkipPopulate) {
        Write-Host "Populating test data (both suffixes)..." -ForegroundColor Gray
        & "$PSScriptRoot/../Populate-OpenLDAP-Scenario19.ps1"
        Write-Host "  OK Test data populated" -ForegroundColor Green
    }
    else {
        Write-Host "  Using pre-populated data - skipping population" -ForegroundColor Green
    }

    Write-Host "Running Scenario 19 setup..." -ForegroundColor Gray
    & "$PSScriptRoot/../Setup-Scenario19.ps1" -JIMUrl $JIMUrl -ApiKey $ApiKey -Template $Template -DirectoryConfig $DirectoryConfig
    Write-Host "  OK JIM configured for Scenario 19" -ForegroundColor Green

    # Re-import module for a live connection after Setup-Scenario19.ps1 ran in a separate invocation.
    $modulePath = "$PSScriptRoot/../../../src/JIM.PowerShell/JIM.psd1"
    Import-Module $modulePath -Force -ErrorAction Stop
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

    $connectedSystems = Get-JIMConnectedSystem
    $sourceSystem = $connectedSystems | Where-Object { $_.name -eq $sourceSystemName }
    $targetSystem = $connectedSystems | Where-Object { $_.name -eq $targetSystemName }
    if (-not $sourceSystem -or -not $targetSystem) {
        throw "Connected Systems not found. Ensure Setup-Scenario19.ps1 completed successfully."
    }

    $sourceProfiles = Get-JIMRunProfile -ConnectedSystemId $sourceSystem.id
    $targetProfiles = Get-JIMRunProfile -ConnectedSystemId $targetSystem.id
    $sourceFullImport = $sourceProfiles | Where-Object { $_.name -eq "Full Import" }
    $sourceFullSync = $sourceProfiles | Where-Object { $_.name -eq "Full Synchronisation" }
    $targetFullImport = $targetProfiles | Where-Object { $_.name -eq "Full Import" }
    $targetFullSync = $targetProfiles | Where-Object { $_.name -eq "Full Synchronisation" }
    $targetExport = $targetProfiles | Where-Object { $_.name -eq "Export" }
    if (-not $sourceFullImport -or -not $sourceFullSync -or -not $targetFullImport -or -not $targetFullSync -or -not $targetExport) {
        throw "Required Run Profiles not found. Ensure Setup-Scenario19.ps1 completed successfully."
    }

    $sourceImportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq "$sourceSystemName Import Users" } | Select-Object -First 1
    $targetExportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq "$targetSystemName Export Users" } | Select-Object -First 1
    if (-not $sourceImportRule -or -not $targetExportRule) {
        throw "Required Sync Rules not found. Ensure Setup-Scenario19.ps1 completed successfully."
    }

    $mvAttributes = @(Get-JIMMetaverseAttribute)
    $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
    $mvBadgeNumber = $mvAttributes | Where-Object { $_.name -eq "Badge Number" }
    $mvBadgeColour = $mvAttributes | Where-Object { $_.name -eq "Badge Colour" }
    if (-not $mvBadgeNumber -or -not $mvBadgeColour) {
        throw "Badge Metaverse attributes not found. Ensure Setup-Scenario19.ps1 completed successfully."
    }

    function Get-Scenario19UserType {
        param($System)
        $objectTypes = @(Get-JIMConnectedSystem -Id $System.id -ObjectTypes)
        return $objectTypes | Where-Object { $_.name -eq "jimPerson" }
    }

    function Get-Scenario19MvoId {
        param([string]$EmployeeId)
        $mvo = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue $EmployeeId -PageSize 5) | Select-Object -First 1
        if (-not $mvo) {
            throw "No Metaverse Object found with Employee ID '$EmployeeId'."
        }
        return $mvo.id
    }

    # ========================================================================
    # Test 1: Merge
    # ========================================================================
    if ($lastStepIndex -ge 0) {
        Write-TestSection "Test 1: Merge (Set-JIMConnectedSystemAuxiliaryClass + schema refresh)"
        $mergeSuccess = $true
        $mergeNotes = @()

        foreach ($side in @(
            @{ Label = $sourceSystemName; System = $sourceSystem; AssertSuggestion = $true }
            @{ Label = $targetSystemName; System = $targetSystem; AssertSuggestion = $false }
        )) {
            $userType = Get-Scenario19UserType -System $side.System

            Write-Host "Listing auxiliary classes on offer for '$($side.Label)' jimPerson..." -ForegroundColor Gray
            $offers = @(Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId $side.System.id -ObjectTypeId $userType.id)
            $badgeOffer = $offers | Where-Object { $_.name -eq "jimBadgeHolder" }
            Assert-Condition -Condition ($null -ne $badgeOffer) `
                -Message "'$($side.Label)': jimBadgeHolder is offered as an auxiliary class for jimPerson"

            if ($side.AssertSuggestion) {
                # The DIT Content Rule on jimPerson names jimBadgeHolder, and the schema import
                # read it live from the directory: this is the only estate fixture exercising
                # the RFC 4512 dITContentRules path end to end.
                Assert-Condition -Condition ([bool]$badgeOffer.permittedByTheConnectedSystem) `
                    -Message "'$($side.Label)': jimBadgeHolder is permitted by the Connected System (DIT Content Rule)"
                Assert-Condition -Condition ([bool]$badgeOffer.isSuggested) `
                    -Message "'$($side.Label)': jimBadgeHolder is suggested"
                Assert-Condition -Condition (-not [bool]$badgeOffer.merged) `
                    -Message "'$($side.Label)': jimBadgeHolder is not yet merged"
            }

            Write-Host "Merging jimBadgeHolder into '$($side.Label)' jimPerson and refreshing the schema..." -ForegroundColor Gray
            Set-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId $side.System.id -ObjectTypeId $userType.id `
                -AuxiliaryClassObjectTypeId @($badgeOffer.objectTypeId) -Confirm:$false | Out-Null
            Import-JIMConnectedSystemSchema -Id $side.System.id | Out-Null

            # The merged class's attributes join the Object Type at the refresh, carrying their
            # class in ClassName so their provenance is visible.
            $userType = Get-Scenario19UserType -System $side.System
            foreach ($expected in @(
                @{ Name = "jimBadgeNumber"; Required = $true }
                @{ Name = "jimBadgeColour"; Required = $false }
                @{ Name = "jimBadgeIssued"; Required = $false }
            )) {
                $attr = $userType.attributes | Where-Object { $_.name -eq $expected.Name }
                Assert-Condition -Condition ($null -ne $attr) `
                    -Message "'$($side.Label)': contributed attribute '$($expected.Name)' appears on jimPerson after the refresh"
                Assert-Equal -Expected "jimBadgeHolder" -Actual $attr.className `
                    -Message "'$($side.Label)': '$($expected.Name)' carries jimBadgeHolder in its ClassName"
                if ($expected.Required) {
                    Assert-Condition -Condition ([bool]$attr.required) `
                        -Message "'$($side.Label)': '$($expected.Name)' is marked required (the class's MUST)"
                }
            }

            $mergedReadBack = @(Get-JIMConnectedSystemAuxiliaryClass -ConnectedSystemId $side.System.id -ObjectTypeId $userType.id -MergedOnly)
            Assert-Condition -Condition (@($mergedReadBack | Where-Object { $_.name -eq "jimBadgeHolder" }).Count -eq 1) `
                -Message "'$($side.Label)': jimBadgeHolder reads back as merged"

            # Select the contributed attributes so they import and export.
            $attrUpdates = @{}
            foreach ($attrName in @("jimBadgeNumber", "jimBadgeColour")) {
                $attr = $userType.attributes | Where-Object { $_.name -eq $attrName }
                $attrUpdates[$attr.id] = @{ selected = $true }
            }
            Set-JIMConnectedSystemAttribute -ConnectedSystemId $side.System.id -ObjectTypeId $userType.id -AttributeUpdates $attrUpdates | Out-Null
            Write-Host "  OK '$($side.Label)': jimBadgeNumber + jimBadgeColour selected" -ForegroundColor Green
        }

        # Badge attribute flow mappings, now the attributes exist on both schemas.
        Write-Host "Creating badge attribute flow mappings..." -ForegroundColor Gray
        $sourceUserType = Get-Scenario19UserType -System $sourceSystem
        foreach ($mapping in @(
            @{ LdapAttr = "jimBadgeNumber"; MvAttr = $mvBadgeNumber }
            @{ LdapAttr = "jimBadgeColour"; MvAttr = $mvBadgeColour }
        )) {
            $csAttr = $sourceUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
            New-JIMSyncRuleMapping -SyncRuleId $sourceImportRule.id `
                -TargetMetaverseAttributeId $mapping.MvAttr.id `
                -SourceConnectedSystemAttributeId $csAttr.id | Out-Null
        }
        $targetUserType = Get-Scenario19UserType -System $targetSystem
        foreach ($mapping in @(
            @{ LdapAttr = "jimBadgeNumber"; MvAttr = $mvBadgeNumber }
            @{ LdapAttr = "jimBadgeColour"; MvAttr = $mvBadgeColour }
        )) {
            $csAttr = $targetUserType.attributes | Where-Object { $_.name -eq $mapping.LdapAttr }
            New-JIMSyncRuleMapping -SyncRuleId $targetExportRule.id `
                -TargetConnectedSystemAttributeId $csAttr.id `
                -SourceMetaverseAttributeId $mapping.MvAttr.id | Out-Null
        }
        Write-Host "  OK Badge mappings created (import on Source, export on Target)" -ForegroundColor Green

        $testResults.Steps += @{ Name = "Merge"; Success = $mergeSuccess; Note = ($mergeNotes -join "; ") }
    }

    # ========================================================================
    # Test 2: Import
    # ========================================================================
    if ($lastStepIndex -ge 1) {
        Write-TestSection "Test 2: Import (one CSO per entry; auxiliary values arrive)"

        Write-Host "Running Full Import (Source)..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullImport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Source)"
        # Exactly six: an entry carrying jimPerson AND jimBadgeHolder imports as ONE Connected
        # System Object, whichever order the directory serves the classes in (Boris lists the
        # auxiliary class first). Seven or more here means an entry produced a CSO per class.
        Assert-ImportedObjectCount -ActivityId $importResult.activityId -Expected 6 -Name "Full Import (Source)"

        if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

        Write-Host "Running Full Synchronisation (Source)..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Source)"

        # The auxiliary attributes' values arrived: both ordinary-order (Amber) and
        # auxiliary-class-first (Boris) entries carry their badge values.
        $amberMvoId = Get-Scenario19MvoId -EmployeeId "S19-0"
        $borisMvoId = Get-Scenario19MvoId -EmployeeId "S19-1"
        Assert-MvoAttributeValue -MvoId $amberMvoId -AttributeName "Badge Number" -ExpectedValue "B19-0" -Name "Amber's Badge Number imported"
        Assert-MvoAttributeValue -MvoId $amberMvoId -AttributeName "Badge Colour" -ExpectedValue "Blue" -Name "Amber's Badge Colour imported"
        Assert-MvoAttributeValue -MvoId $borisMvoId -AttributeName "Badge Number" -ExpectedValue "B19-1" -Name "Boris's Badge Number imported (auxiliary class listed first)"
        Assert-MvoAttributeValue -MvoId $borisMvoId -AttributeName "Badge Colour" -ExpectedValue "Green" -Name "Boris's Badge Colour imported (auxiliary class listed first)"

        Write-Host "Running Full Import (Target)..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullImport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Target)"
        Assert-ImportedObjectCount -ActivityId $importResult.activityId -Expected 6 -Name "Full Import (Target)"

        Write-Host "Running Full Synchronisation (Target)..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Target)"

        # Joins, not duplicate projections: each Employee ID resolves to exactly one Metaverse
        # Object after both systems have synchronised.
        foreach ($i in 0..5) {
            $matches19 = @(Get-JIMMetaverseObject -ObjectTypeName "User" -AttributeName "Employee ID" -AttributeValue "S19-$i" -PageSize 5)
            Assert-Equal -Expected 1 -Actual $matches19.Count -Message "Employee ID S19-$i resolves to exactly one Metaverse Object"
        }

        $testResults.Steps += @{ Name = "Import"; Success = $true }
    }

    # ========================================================================
    # Test 3: DeltaConvergence
    # ========================================================================
    if ($lastStepIndex -ge 2) {
        Write-TestSection "Test 3: DeltaConvergence (the class arrives in the same modify as its first attribute)"

        # Precondition: Amber's Target entry does not carry the class or its attributes yet.
        $amberClassesBefore = @(Get-Scenario19LdapAttributeValues -LdapConfig $targetLdapConfig -Uid "amber19" -AttributeName "objectClass")
        Assert-Condition -Condition ($amberClassesBefore -notcontains "jimBadgeHolder") `
            -Message "Amber's Target entry does not yet carry jimBadgeHolder"

        Write-Host "Running Export (Target)..." -ForegroundColor Gray
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetExport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "Export (Target)"

        # The class and its attributes arrived together: one export run, and the entry that
        # lacked the class now carries it alongside the structural class, with the badge values.
        $amberClassesAfter = @(Get-Scenario19LdapAttributeValues -LdapConfig $targetLdapConfig -Uid "amber19" -AttributeName "objectClass")
        Assert-Condition -Condition ($amberClassesAfter -contains "jimBadgeHolder") `
            -Message "Amber's Target entry now carries jimBadgeHolder"
        Assert-Condition -Condition ($amberClassesAfter -contains "jimPerson") `
            -Message "Amber's Target entry still carries its structural class jimPerson"
        $amberNumber = @(Get-Scenario19LdapAttributeValues -LdapConfig $targetLdapConfig -Uid "amber19" -AttributeName "jimBadgeNumber")
        $amberColour = @(Get-Scenario19LdapAttributeValues -LdapConfig $targetLdapConfig -Uid "amber19" -AttributeName "jimBadgeColour")
        Assert-Equal -Expected "B19-0" -Actual ($amberNumber -join ",") -Message "Amber's Target jimBadgeNumber was written"
        Assert-Equal -Expected "Blue" -Actual ($amberColour -join ",") -Message "Amber's Target jimBadgeColour was written"

        # The other carriers converged in the same run.
        foreach ($carrier in @(@{ Uid = "boris19"; Number = "B19-1" }, @{ Uid = "clara19"; Number = "B19-2" })) {
            $classes = @(Get-Scenario19LdapAttributeValues -LdapConfig $targetLdapConfig -Uid $carrier.Uid -AttributeName "objectClass")
            Assert-Condition -Condition ($classes -contains "jimBadgeHolder") `
                -Message "$($carrier.Uid)'s Target entry now carries jimBadgeHolder"
        }

        # A non-carrier stays untouched: no badge values means no class add.
        $elenaClasses = @(Get-Scenario19LdapAttributeValues -LdapConfig $targetLdapConfig -Uid "elena19" -AttributeName "objectClass")
        Assert-Condition -Condition ($elenaClasses -notcontains "jimBadgeHolder") `
            -Message "Elena's Target entry (no badge) was not given the class"

        # Confirm the exports so the Target Connected System Objects mirror the new state.
        Write-Host "Running Full Import (Target) to confirm the exports..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullImport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Target) after convergence"

        $testResults.Steps += @{ Name = "DeltaConvergence"; Success = $true }
    }

    # ========================================================================
    # Test 4: MustEnforcement
    # ========================================================================
    if ($lastStepIndex -ge 3) {
        Write-TestSection "Test 4: MustEnforcement (an export that cannot satisfy the class's MUST is refused)"

        # Give Dora (S19-3, no badge) a Badge Colour without a Badge Number: roomNumber becomes
        # a second-priority contributor to Badge Colour. Priority is explicit because two
        # mappings now feed one attribute; the carriers keep winning with jimBadgeColour.
        Write-Host "Adding roomNumber -> Badge Colour (priority 2) on the Source import rule..." -ForegroundColor Gray
        $sourceUserType = Get-Scenario19UserType -System $sourceSystem
        $roomNumberAttr = $sourceUserType.attributes | Where-Object { $_.name -eq "roomNumber" }
        $roomNumberMapping = New-JIMSyncRuleMapping -SyncRuleId $sourceImportRule.id `
            -TargetMetaverseAttributeId $mvBadgeColour.id `
            -SourceConnectedSystemAttributeId $roomNumberAttr.id
        $badgeColourMappings = @(Get-JIMSyncRuleMapping -SyncRuleId $sourceImportRule.id) |
            Where-Object { $_.targetMetaverseAttributeId -eq $mvBadgeColour.id }
        $jimBadgeColourMapping = $badgeColourMappings | Where-Object { $_.id -ne $roomNumberMapping.id } | Select-Object -First 1
        Set-JIMMetaverseAttributePriority -AttributeId $mvBadgeColour.id -ObjectTypeId $mvUserType.id `
            -MappingId @($jimBadgeColourMapping.id, $roomNumberMapping.id) | Out-Null
        Write-Host "  OK roomNumber mapping created at priority 2" -ForegroundColor Green

        Write-Host "Running Full Synchronisation (Source)..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Source) with roomNumber mapping"

        $doraMvoId = Get-Scenario19MvoId -EmployeeId "S19-3"
        Assert-MvoAttributeValue -MvoId $doraMvoId -AttributeName "Badge Colour" -ExpectedValue "Yellow" -Name "Dora's Badge Colour arrived from roomNumber"
        Assert-MvoAttributeValue -MvoId $doraMvoId -AttributeName "Badge Number" -ExpectNoValue -Name "Dora has no Badge Number"

        Write-Host "Running Full Synchronisation (Target) to stage Dora's export..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Target) staging Dora's export"

        # The export must be refused BEFORE being sent: writing the colour first-flows a class
        # whose MUST (jimBadgeNumber) neither this export nor Dora's Target entry can satisfy.
        # Sending it anyway would have OpenLDAP reject the change in its own terms; refusing it
        # names the attribute an administrator has to flow. Assert-ActivitySuccess is
        # deliberately NOT used: the refusal is the expected outcome, reported per-object.
        Write-Host "Running Export (Target), expecting Dora's export to be refused..." -ForegroundColor Gray
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetExport.id -Wait -PassThru
        $executionItems = @(Get-JIMActivity -Id $exportResult.activityId -ExecutionItems)
        $refusals = @($executionItems | Where-Object {
            $itemError = $_.PSObject.Properties['errorMessage']?.Value
            $itemError -and $itemError -match 'jimBadgeNumber'
        })
        Assert-Equal -Expected 1 -Actual $refusals.Count -Message "Exactly one export was refused naming jimBadgeNumber"
        Assert-Condition -Condition ($refusals[0].errorMessage -match 'jimBadgeHolder') `
            -Message "The refusal names the class being added (jimBadgeHolder)"
        Write-Host "    Refusal: $($refusals[0].errorMessage)" -ForegroundColor Gray

        # The directory was never touched: the refusal happened in JIM, not at OpenLDAP.
        $doraClasses = @(Get-Scenario19LdapAttributeValues -LdapConfig $targetLdapConfig -Uid "dora19" -AttributeName "objectClass")
        Assert-Condition -Condition ($doraClasses -notcontains "jimBadgeHolder") `
            -Message "Dora's Target entry was not given the class"
        $doraColour = @(Get-Scenario19LdapAttributeValues -LdapConfig $targetLdapConfig -Uid "dora19" -AttributeName "jimBadgeColour")
        Assert-Equal -Expected 0 -Actual $doraColour.Count -Message "Dora's Target entry was not given the colour"

        # Restore: remove the roomNumber mapping and re-synchronise both systems, so Dora's
        # Badge Colour is recalled and the refused Pending Export is withdrawn; later steps then
        # run their exports clean rather than re-tripping this refusal.
        Write-Host "Removing the roomNumber mapping and re-synchronising..." -ForegroundColor Gray
        # Shrink the priority list back to the surviving contributor first, so the mapping being
        # removed is no longer referenced by an Attribute Priority entry.
        Set-JIMMetaverseAttributePriority -AttributeId $mvBadgeColour.id -ObjectTypeId $mvUserType.id `
            -MappingId @($jimBadgeColourMapping.id) | Out-Null
        Remove-JIMSyncRuleMapping -SyncRuleId $sourceImportRule.id -MappingId $roomNumberMapping.id -Confirm:$false | Out-Null
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Source) after mapping removal"
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Target) after mapping removal"
        Assert-MvoAttributeValue -MvoId $doraMvoId -AttributeName "Badge Colour" -ExpectNoValue -Name "Dora's Badge Colour recalled after mapping removal"

        $testResults.Steps += @{ Name = "MustEnforcement"; Success = $true }
    }

    # ========================================================================
    # Test 5: CarrierProvisioning
    # ========================================================================
    if ($lastStepIndex -ge 4) {
        Write-TestSection "Test 5: CarrierProvisioning (an auxiliary-typed object is created as carrier + class)"

        # Gina (S19-6) exists in Yellowstone only, so she is the one Metaverse Object with no
        # Target presence to provision.
        Write-Host "Adding Gina (S19-6, badge carrier) to the Source suffix..." -ForegroundColor Gray
        $ginaLdif = @"
dn: uid=gina19,$($sourceLdapConfig.UserContainer)
objectClass: jimPerson
objectClass: jimBadgeHolder
uid: gina19
cn: Gina Grant (S19)
sn: Grant
givenName: Gina
displayName: Gina Grant (S19)
mail: gina19@yellowstone.local
employeeNumber: S19-6
jimBadgeNumber: B19-6
jimBadgeColour: Gold
userPassword: Test@123!

"@
        Invoke-Scenario19LdapAdd -ContainerName $sourceLdapConfig.ContainerName -LdapUri $sourceLdapUri `
            -BindDN $sourceLdapConfig.BindDN -BindPassword $sourceLdapConfig.BindPassword -Ldif $ginaLdif

        Write-Host "Running Full Import + Full Synchronisation (Source)..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullImport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Source) with Gina present"
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $sourceSystem.id -RunProfileId $sourceFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Source) with Gina present"
        $ginaMvoId = Get-Scenario19MvoId -EmployeeId "S19-6"
        Assert-MvoAttributeValue -MvoId $ginaMvoId -AttributeName "Badge Number" -ExpectedValue "B19-6" -Name "Gina's Badge Number imported"

        # Select jimBadgeHolder as its own Object Type on Target and name its Structural Carrier
        # Class: an entry carries exactly one structural class, so JIM has to be told what to
        # write alongside the auxiliary one. 'account' is the classic carrier here (the
        # fixture's jimBadgeHolder MAYs uid, satisfying account's MUST and the uid= RDN).
        Write-Host "Selecting jimBadgeHolder as an Object Type on Target with 'account' as carrier..." -ForegroundColor Gray
        $targetObjectTypes = @(Get-JIMConnectedSystem -Id $targetSystem.id -ObjectTypes)
        $targetAuxType = $targetObjectTypes | Where-Object { $_.name -eq "jimBadgeHolder" }
        $targetAccountType = $targetObjectTypes | Where-Object { $_.name -eq "account" }
        if (-not $targetAuxType) { throw "'jimBadgeHolder' object type not found in the Target schema." }
        if (-not $targetAccountType) { throw "'account' object type not found in the Target schema (cosine should supply it)." }

        Set-JIMConnectedSystemObjectType -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetAuxType.id -Selected $true | Out-Null
        $auxAttrUpdates = @{}
        foreach ($attrName in @("uid", "jimBadgeNumber", "jimBadgeColour", "distinguishedName", "entryUUID")) {
            $attr = $targetAuxType.attributes | Where-Object { $_.name -eq $attrName }
            if (-not $attr) { throw "'$attrName' attribute not found on the Target jimBadgeHolder object type." }
            $auxAttrUpdates[$attr.id] = @{ selected = $true }
        }
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetAuxType.id -AttributeUpdates $auxAttrUpdates | Out-Null
        Set-JIMConnectedSystemStructuralCarrierClass -ConnectedSystemId $targetSystem.id -ObjectTypeId $targetAuxType.id `
            -StructuralCarrierObjectTypeId $targetAccountType.id -Confirm:$false | Out-Null

        $targetAuxType = @(Get-JIMConnectedSystem -Id $targetSystem.id -ObjectTypes) | Where-Object { $_.name -eq "jimBadgeHolder" }
        Assert-Equal -Expected $targetAccountType.id -Actual $targetAuxType.structuralCarrierObjectTypeId `
            -Message "jimBadgeHolder's Structural Carrier Class reads back as 'account'"

        # The provisioning export rule, scoped to Gina alone so the step's blast radius is one
        # object however the engine treats Metaverse Objects already joined to a Target CSO of
        # another type.
        Write-Host "Creating the provisioning export rule (scoped to Gina)..." -ForegroundColor Gray
        $provisionRule = New-JIMSyncRule `
            -Name "$targetSystemName Provision Badges" `
            -ConnectedSystemId $targetSystem.id `
            -ConnectedSystemObjectTypeId $targetAuxType.id `
            -MetaverseObjectTypeId $mvUserType.id `
            -Direction Export `
            -ProvisionToConnectedSystem `
            -PassThru
        $scopeGroup = New-JIMScopingCriteriaGroup -SyncRuleId $provisionRule.id -Type All -PassThru
        $mvEmployeeIdAttr = $mvAttributes | Where-Object { $_.name -eq "Employee ID" }
        New-JIMScopingCriterion -SyncRuleId $provisionRule.id -GroupId $scopeGroup.id `
            -MetaverseAttributeId $mvEmployeeIdAttr.id -ComparisonType Equals -StringValue "S19-6" | Out-Null

        $mvAccountNameAttr = $mvAttributes | Where-Object { $_.name -eq "Account Name" }
        $auxUidAttr = $targetAuxType.attributes | Where-Object { $_.name -eq "uid" }
        $auxDnAttr = $targetAuxType.attributes | Where-Object { $_.name -eq "distinguishedName" }
        $auxNumberAttr = $targetAuxType.attributes | Where-Object { $_.name -eq "jimBadgeNumber" }
        $auxColourAttr = $targetAuxType.attributes | Where-Object { $_.name -eq "jimBadgeColour" }
        New-JIMSyncRuleMapping -SyncRuleId $provisionRule.id -TargetConnectedSystemAttributeId $auxDnAttr.id `
            -Expression ('"uid=" + mv["Account Name"] + ",' + $targetLdapConfig.UserContainer + '"') | Out-Null
        New-JIMSyncRuleMapping -SyncRuleId $provisionRule.id -TargetConnectedSystemAttributeId $auxUidAttr.id `
            -SourceMetaverseAttributeId $mvAccountNameAttr.id | Out-Null
        New-JIMSyncRuleMapping -SyncRuleId $provisionRule.id -TargetConnectedSystemAttributeId $auxNumberAttr.id `
            -SourceMetaverseAttributeId $mvBadgeNumber.id | Out-Null
        New-JIMSyncRuleMapping -SyncRuleId $provisionRule.id -TargetConnectedSystemAttributeId $auxColourAttr.id `
            -SourceMetaverseAttributeId $mvBadgeColour.id | Out-Null
        Write-Host "  OK Provisioning export rule created with DN expression + uid + badge mappings" -ForegroundColor Green

        # Precondition, then provision.
        $ginaBefore = @(Get-Scenario19LdapAttributeValues -LdapConfig $targetLdapConfig -Uid "gina19" -AttributeName "objectClass" -AllowMissingEntry)
        Assert-Equal -Expected 0 -Actual $ginaBefore.Count -Message "Gina has no Target entry before provisioning"

        Write-Host "Running Full Synchronisation (Target) + Export (Target)..." -ForegroundColor Gray
        $syncResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullSync.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Full Synchronisation (Target) staging Gina's provisioning"
        $exportResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetExport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $exportResult.activityId -Name "Export (Target) provisioning Gina"

        # The created entry exists as the carrier class PLUS the auxiliary class, with the badge
        # values, read back over ldapsearch.
        $ginaClasses = @(Get-Scenario19LdapAttributeValues -LdapConfig $targetLdapConfig -Uid "gina19" -AttributeName "objectClass")
        Assert-Condition -Condition ($ginaClasses -contains "account") `
            -Message "Gina's provisioned Target entry carries the structural carrier class 'account'"
        Assert-Condition -Condition ($ginaClasses -contains "jimBadgeHolder") `
            -Message "Gina's provisioned Target entry carries 'jimBadgeHolder'"
        $ginaNumber = @(Get-Scenario19LdapAttributeValues -LdapConfig $targetLdapConfig -Uid "gina19" -AttributeName "jimBadgeNumber")
        Assert-Equal -Expected "B19-6" -Actual ($ginaNumber -join ",") -Message "Gina's provisioned entry carries her badge number"

        # Confirm the export.
        Write-Host "Running Full Import (Target) to confirm the provisioning..." -ForegroundColor Gray
        $importResult = Start-JIMRunProfile -ConnectedSystemId $targetSystem.id -RunProfileId $targetFullImport.id -Wait -PassThru
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Full Import (Target) after provisioning"

        $testResults.Steps += @{ Name = "CarrierProvisioning"; Success = $true }
    }

    # ========================================================================
    # Test 6: Discovery
    # ========================================================================
    if ($lastStepIndex -ge 5) {
        Write-TestSection "Test 6: Discovery (full scan, then a bounded quick sample)"

        function Wait-Scenario19DiscoveryComplete {
            param([string]$Label)
            $completed = Wait-ForCondition -TimeoutSeconds 180 -IntervalSeconds 5 -Description "$Label discovery run to complete" -Condition {
                $run = Get-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId $sourceSystem.id
                $run -and $run.status -eq "Complete"
            }
            if (-not $completed) {
                $run = Get-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId $sourceSystem.id
                throw "$Label discovery run did not complete in time (status: $($run.status); error: $($run.errorMessage))."
            }
            return Get-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId $sourceSystem.id
        }

        Write-Host "Starting a FullScan discovery run (Source)..." -ForegroundColor Gray
        Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId $sourceSystem.id -Scope FullScan -Confirm:$false | Out-Null
        $fullRun = Wait-Scenario19DiscoveryComplete -Label "FullScan"

        # Seven jimPerson entries in scope (the seeded six plus Gina); four carry the class
        # (Amber, Boris, Clara, Gina).
        Assert-Equal -Expected 7 -Actual $fullRun.entriesRead -Message "FullScan read every in-scope entry"
        $fullResults = @($fullRun.results)
        $badgeResult = $fullResults | Where-Object { $_.auxiliaryClassName -eq "jimBadgeHolder" }
        Assert-Condition -Condition ($null -ne $badgeResult) -Message "FullScan observed jimBadgeHolder in use"
        Assert-Equal -Expected 4 -Actual $badgeResult.entryCount -Message "FullScan counted the four carrier entries"

        # A quick sample reads at most the sample size per Object Type, so both what it read and
        # what it observed are bounded by it.
        Write-Host "Starting a QuickSample discovery run (Source, sample size 2)..." -ForegroundColor Gray
        Start-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId $sourceSystem.id -Scope QuickSample -SampleSizePerObjectType 2 -Confirm:$false | Out-Null
        $sampleRun = Wait-Scenario19DiscoveryComplete -Label "QuickSample"

        Assert-Condition -Condition ($sampleRun.entriesRead -le 2) `
            -Message "QuickSample read at most the sample size ($($sampleRun.entriesRead) of 2)"
        $sampleResults = @($sampleRun.results)
        foreach ($result in $sampleResults) {
            Assert-Condition -Condition ($result.entryCount -le 2) `
                -Message "QuickSample count for '$($result.auxiliaryClassName)' is bounded by the sample size ($($result.entryCount) of 2)"
        }

        $testResults.Steps += @{ Name = "Discovery"; Success = $true }
    }

    # Calculate overall success
    $failedSteps = @($testResults.Steps | Where-Object { $_.Success -eq $false })
    $testResults.Success = ($failedSteps.Count -eq 0)
}
catch {
    Write-Host ""
    Write-Host "FAIL Test failed with error:" -ForegroundColor Red
    Write-Host "  $_" -ForegroundColor Red
    Write-Host ""
    if (@($testResults.Steps | Where-Object { $_.Success -eq $false }).Count -eq 0) {
        $testResults.Steps += @{ Name = "Setup"; Success = $false; Error = $_.ToString() }
    }
}

# ========================================================================
# Summary
# ========================================================================
Write-TestSection "Test Results Summary"

$passedCount = @($testResults.Steps | Where-Object { $_.Success -eq $true }).Count
$failedCount = @($testResults.Steps | Where-Object { $_.Success -eq $false }).Count
$totalCount = @($testResults.Steps).Count

Write-Host "Scenario: $($testResults.Scenario)" -ForegroundColor Cyan
Write-Host ""

foreach ($testStep in $testResults.Steps) {
    $icon = if ($testStep.Success) { "OK" } else { "FAIL" }
    $color = if ($testStep.Success) { "Green" } else { "Red" }

    Write-Host "  $icon $($testStep.Name)" -ForegroundColor $color

    if ($testStep.ContainsKey('Note') -and $testStep.Note) {
        Write-Host "    $($testStep.Note)" -ForegroundColor Gray
    }
    if (-not $testStep.Success -and $testStep.ContainsKey('Error') -and $testStep.Error) {
        Write-Host "    Error: $($testStep.Error)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Results: $passedCount passed, $failedCount failed (of $totalCount tests)" -ForegroundColor $(if ($failedCount -eq 0) { "Green" } else { "Red" })

if ($testResults.Success) {
    Write-Host ""
    Write-Host "OK All Scenario 19 tests passed!" -ForegroundColor Green
    exit 0
}
else {
    Write-Host ""
    Write-Host "FAIL Some Scenario 19 tests failed" -ForegroundColor Red
    exit 1
}
