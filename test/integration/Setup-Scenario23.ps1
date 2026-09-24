# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Setup for Scenario 23: Unique Value Generation

.DESCRIPTION
    Configures JIM to generate the identifiers an HR feed would not carry in a real deployment
    (Unique Value Generation, #242, release 1). The provisioning substrate is Scenario 1's,
    composed exactly like Scenario 22's, with two differences that are this scenario's whole point:

      - Setup-Scenario1.ps1 -GenerateAccountName replaces the ordinary samAccountName -> Account
        Name import mapping with a generated one (OnlyIfTaken, Number suffix), and derives Email
        from the names.
      - The HR CSV is expected to have been generated with Generate-TestCSV.ps1
        -OmitItOwnedAttributes (Invoke-Scenario23-UniqueValueGeneration.ps1 does this before calling
        this script), so samAccountName/email/userPrincipalName are genuinely absent, the shape this
        feature exists to make representative.

    On top of that substrate, this script adds three further generated mappings, each exercising a
    release-1 capability Account Name alone does not cover:

      - "Staff Number" (new Metaverse attribute, import mode): Sequence token, prefix "EMP-" in
        the base expression, start 1000, increment 1, fixed width 6 (so the counter's zero-padding
        is visible from the first issued value: "EMP-001000", not "EMP-100000"). Raising and
        lowering its Sequence Start, and Start again, are exercised against this mapping.
      - "Badge Code" (new Metaverse attribute, import mode): Random token, Hex format, length 8, no
        base expression.
      - "Call Sign" (new Metaverse attribute, import mode): OnlyIfTaken against the constant base
        expression "CALLSIGN" with an attempt limit of 1, created DISABLED. Every object in the
        population shares the one candidate; with only one attempt allowed, the first object to
        claim it succeeds and every other object's sole attempt finds it already taken and is
        exhausted. Left disabled so it has no effect until the Failure test step enables it
        on-demand against the whole existing population in one synchronisation.
      - A generated export mapping on the LDAP target's "postalCode" attribute (Random token,
        Digits format, length 6): export-mode generation, keyed on the Connected System Object, the
        Metaverse untouched. postalCode is part of RFC 4519's organizationalPerson (which both
        inetOrgPerson and Active Directory's user class descend from) and is not used by any other
        Scenario 1 mapping, so it is genuinely spare on both directory types this scenario supports.
      - "Locker Code" (new Metaverse attribute, import mode): an ordinary (non-generated) export
        mapping flows it to the LDAP target's "physicalDeliveryOfficeName" attribute (also RFC 4519
        organizationalPerson, also spare, and a real Active Directory attribute), and a generated
        import mapping (OnlyIfTaken against the constant base "LOCKER") is created DISABLED. This
        pair exists solely for the Adopt before generate test step: enabling the import mapping only
        after a brownfield Connected System Object is already joined and already holds a
        physicalDeliveryOfficeName value is what makes an import-mode adoption reproducible at all
        (see that step's own comments for why the ordering matters).

    Supports OpenLDAP (default) and Samba AD; refuses 389 Directory Server, since the SambaAD-only
    target-side collision test needs a real directory-wide unique-value constraint a CSV target
    cannot produce, and OpenLDAP already covers the RFC-directory shape.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication

.PARAMETER Template
    Data scale template, passed through to Setup-Scenario1.ps1. Micro is the default: this scenario
    asserts against individually-identifiable objects (per-base-value sets, specific new joiners), so
    a small, fast population is the right size for development; the runner can still be asked for a
    larger one.

.PARAMETER DirectoryConfig
    Directory configuration hashtable. Defaults to Get-DirectoryConfig -DirectoryType OpenLDAP if not
    supplied. Only OpenLDAP and SambaAD are accepted; 389 Directory Server is refused.

.PARAMETER ExportConcurrency
    LDAP Connector export concurrency, passed through to Setup-Scenario1.ps1

.PARAMETER MaxExportParallelism
    Connected System export parallelism, passed through to Setup-Scenario1.ps1

.EXAMPLE
    ./Setup-Scenario23.ps1 -ApiKey "jim_..." -Template Micro

.EXAMPLE
    ./Setup-Scenario23.ps1 -ApiKey "jim_..." -DirectoryConfig (Get-DirectoryConfig -DirectoryType SambaAD -Instance Primary)
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
    [int]$MaxExportParallelism = 1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ConfirmPreference = 'None'

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

# Default to OpenLDAP Primary: an empty target directory is the whole point of this scenario (every
# Account Name and Staff Number is generated, not sourced), and OpenLDAP is the faster of the two
# supported directories to stand up. SambaAD is fully supported too (pass -DirectoryConfig
# explicitly), and is required for the SambaAD-only target-side collision test step.
if (-not $DirectoryConfig) {
    $DirectoryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Primary
}

if ($DirectoryConfig.DirectoryType -notin @("OpenLDAP", "SambaAD")) {
    throw "Scenario 23 supports OpenLDAP and Samba AD only. 389 Directory Server was requested " +
          "($($DirectoryConfig.ConnectedSystemName)): the SambaAD-only target-side collision step needs a " +
          "real directory-wide unique-value constraint a CSV target cannot produce, and OpenLDAP already " +
          "covers the RFC-directory shape, so a third directory adds nothing this scenario needs. Use " +
          "-DirectoryType OpenLDAP or -DirectoryType SambaAD."
}

Write-TestSection "Scenario 23 Setup: Unique Value Generation ($($DirectoryConfig.ConnectedSystemName))"

# Step 1: Build the provisioning substrate (HR CSV, generated Account Name -> OpenLDAP/Samba AD)
Write-TestStep "Step 1" "Running Setup-Scenario1.ps1 -GenerateAccountName for the provisioning substrate"

$setupScript = "$PSScriptRoot/Setup-Scenario1.ps1"
if (-not (Test-Path $setupScript)) {
    throw "Setup script not found at: $setupScript"
}

$setupParams = @{
    JIMUrl = $JIMUrl
    ApiKey = $ApiKey
    Template = $Template
    DirectoryConfig = $DirectoryConfig
    GenerateAccountName = $true
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

Write-Host "  ✓ Provisioning substrate configured (Account Name is generated)" -ForegroundColor Green

# Step 2: Connect to JIM for the additional generated mappings
Write-TestStep "Step 2" "Connecting to JIM"

$modulePath = "$PSScriptRoot/../../src/JIM.PowerShell/JIM.psd1"
Remove-Module JIM -Force -ErrorAction SilentlyContinue
Import-Module $modulePath -Force -ErrorAction Stop
Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
Write-Host "  ✓ Connected to $JIMUrl" -ForegroundColor Green

try {
    # Step 3: Create the extra Metaverse attributes this scenario's mappings target. Following the
    # pattern Setup-Scenario1.ps1 itself uses for the Training attributes.
    Write-TestStep "Step 3" "Creating the Metaverse attributes for Sequence, Random and Failure tests"

    $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1
    if (-not $mvUserType) {
        throw "Setup failed: Metaverse object type 'User' not found."
    }

    $extraMvAttributes = @(
        @{ Name = "Staff Number"; Type = "Text"; Plurality = "SingleValued" }  # Sequence token
        @{ Name = "Badge Code";      Type = "Text"; Plurality = "SingleValued" }  # Random token
        @{ Name = "Call Sign";       Type = "Text"; Plurality = "SingleValued" }  # OnlyIfTaken, Failure test
        @{ Name = "Locker Code";     Type = "Text"; Plurality = "SingleValued" }  # OnlyIfTaken, Adopt before generate test
    )

    $mvAttributes = Get-JIMMetaverseAttribute
    foreach ($attrDef in $extraMvAttributes) {
        $existingAttr = $mvAttributes | Where-Object { $_.name -eq $attrDef.Name }
        if (-not $existingAttr) {
            $newAttr = New-JIMMetaverseAttribute `
                -Name $attrDef.Name `
                -Type $attrDef.Type `
                -AttributePlurality $attrDef.Plurality `
                -ObjectTypeIds @($mvUserType.id)
            Write-Host "  ✓ Created MV attribute: $($attrDef.Name) (ID: $($newAttr.id))" -ForegroundColor Green
            $mvAttributes = @($mvAttributes) + $newAttr
        }
        else {
            Write-Host "  MV attribute '$($attrDef.Name)' already exists (ID: $($existingAttr.id))" -ForegroundColor Gray
        }
    }

    $employeeNumberAttr = $mvAttributes | Where-Object { $_.name -eq "Staff Number" }
    $badgeCodeAttr = $mvAttributes | Where-Object { $_.name -eq "Badge Code" }
    $callSignAttr = $mvAttributes | Where-Object { $_.name -eq "Call Sign" }
    $lockerCodeAttr = $mvAttributes | Where-Object { $_.name -eq "Locker Code" }

    # Step 4: Create the three additional generated IMPORT mappings on the HR import rule.
    Write-TestStep "Step 4" "Creating the Sequence, Random and Failure-test generated import mappings"

    $importRuleName = "HR CSV Import Users"
    $importRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq $importRuleName } | Select-Object -First 1
    if (-not $importRule) {
        throw "Setup failed: import Synchronisation Rule '$importRuleName' not found; Setup-Scenario1.ps1 should have created it."
    }

    $existingImportMappings = Get-JIMSyncRuleMapping -SyncRuleId $importRule.id

    $employeeNumberMapping = $existingImportMappings | Where-Object { $_.targetMetaverseAttributeId -eq $employeeNumberAttr.id }
    if (-not $employeeNumberMapping) {
        # Sequence: prefix "EMP-" in the base expression, start 1000, fixed width 6, so padding is
        # visible from the first issued value ("EMP-001000") rather than coincidentally already six
        # digits wide.
        $employeeNumberMapping = New-JIMSyncRuleMapping -SyncRuleId $importRule.id `
            -TargetMetaverseAttributeId $employeeNumberAttr.id `
            -Expression '"EMP-"' `
            -Generate -TokenKind Sequence -SequenceStart 1000 -SequenceIncrement 1 -FixedWidth 6
        Write-Host "  ✓ Generated Staff Number mapping created (Sequence, EMP-NNNNNN from 1000, ID: $($employeeNumberMapping.id))" -ForegroundColor Green
    }
    else {
        Write-Host "  Staff Number mapping already exists (ID: $($employeeNumberMapping.id))" -ForegroundColor Gray
    }

    $badgeCodeMapping = $existingImportMappings | Where-Object { $_.targetMetaverseAttributeId -eq $badgeCodeAttr.id }
    if (-not $badgeCodeMapping) {
        $badgeCodeMapping = New-JIMSyncRuleMapping -SyncRuleId $importRule.id `
            -TargetMetaverseAttributeId $badgeCodeAttr.id `
            -Generate -TokenKind Random -RandomFormat Hex -RandomLength 8
        Write-Host "  ✓ Generated Badge Code mapping created (Random, Hex, 8 characters, ID: $($badgeCodeMapping.id))" -ForegroundColor Green
    }
    else {
        Write-Host "  Badge Code mapping already exists (ID: $($badgeCodeMapping.id))" -ForegroundColor Gray
    }

    $callSignMapping = $existingImportMappings | Where-Object { $_.targetMetaverseAttributeId -eq $callSignAttr.id }
    if (-not $callSignMapping) {
        # Deliberately created DISABLED: every object shares the one candidate "CALLSIGN" and the
        # attempt limit is 1, so enabling this against an existing population immediately exhausts
        # every object but the first to claim it. Left off until the Failure test step turns it on.
        $callSignMapping = New-JIMSyncRuleMapping -SyncRuleId $importRule.id `
            -TargetMetaverseAttributeId $callSignAttr.id `
            -Expression '"CALLSIGN"' `
            -Generate -TokenKind OnlyIfTaken -AttemptLimit 1 -Enabled $false
        Write-Host "  ✓ Generated Call Sign mapping created, disabled (OnlyIfTaken \"CALLSIGN\", AttemptLimit 1, ID: $($callSignMapping.id))" -ForegroundColor Green
    }
    else {
        Write-Host "  Call Sign mapping already exists (ID: $($callSignMapping.id))" -ForegroundColor Gray
    }

    $lockerCodeMapping = $existingImportMappings | Where-Object { $_.targetMetaverseAttributeId -eq $lockerCodeAttr.id }
    if (-not $lockerCodeMapping) {
        # Deliberately created DISABLED, and stays that way until the Adopt before generate test step
        # turns it on. Unique Value Generation's adopt-before-generate check only ever sees a joined
        # Connected System Object that was ALREADY joined (committed in an earlier, separate
        # synchronisation) by the time this mapping first resolves for an object (release 1's
        # adopt-existing lookup is a database read of already-persisted joins; see
        # GeneratedValueParticipation.FindAdoptableValueAsync and its callers in
        # SyncTaskProcessorBase.ResolvePendingGeneratedValuesAsync). Since HR is the only system that
        # projects a Metaverse Object, that object's very first synchronisation pass is also this
        # mapping's first (and, being sticky thereafter, only) chance to resolve; a brownfield join
        # cannot be committed before an object exists to join to. Creating this mapping disabled and
        # enabling it only once a brownfield Locker Code CSO is already joined is what makes the
        # reproduction sound rather than a race against the engine's own ordering.
        $lockerCodeMapping = New-JIMSyncRuleMapping -SyncRuleId $importRule.id `
            -TargetMetaverseAttributeId $lockerCodeAttr.id `
            -Expression '"LOCKER"' `
            -Generate -TokenKind OnlyIfTaken -AttemptLimit 1000 -Enabled $false
        Write-Host "  ✓ Generated Locker Code mapping created, disabled (OnlyIfTaken \"LOCKER\", ID: $($lockerCodeMapping.id))" -ForegroundColor Green
    }
    else {
        Write-Host "  Locker Code mapping already exists (ID: $($lockerCodeMapping.id))" -ForegroundColor Gray
    }

    # Step 5: Select a spare LDAP attribute and create a generated EXPORT mapping on it.
    Write-TestStep "Step 5" "Creating the generated export mapping (postalCode)"

    $ldapSystemName = $DirectoryConfig.ConnectedSystemName
    $ldapSystem = @(Get-JIMConnectedSystem) | Where-Object { $_.name -eq $ldapSystemName } | Select-Object -First 1
    if (-not $ldapSystem) {
        throw "Setup failed: Connected System '$ldapSystemName' not found."
    }

    $ldapUserObjectClass = $DirectoryConfig.UserObjectClass
    $ldapObjectTypes = Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes
    $ldapUserType = $ldapObjectTypes | Where-Object { $_.name -eq $ldapUserObjectClass } | Select-Object -First 1
    if (-not $ldapUserType) {
        throw "Setup failed: LDAP object type '$ldapUserObjectClass' not found on '$ldapSystemName'."
    }

    $postalCodeAttr = $ldapUserType.attributes | Where-Object { $_.name -eq 'postalCode' }
    if (-not $postalCodeAttr) {
        throw "Setup failed: LDAP attribute 'postalCode' not found in the schema for '$ldapSystemName' " +
              "(object type '$ldapUserObjectClass'). postalCode is part of RFC 4519's organizationalPerson " +
              "and is expected on both OpenLDAP and Active Directory; if the schema genuinely lacks it, " +
              "pick a different spare attribute here and in Invoke-Scenario23-UniqueValueGeneration.ps1's " +
              "Export mode assertions."
    }
    if (-not $postalCodeAttr.selected) {
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $ldapSystem.id -ObjectTypeId $ldapUserType.id -AttributeId $postalCodeAttr.id -Selected $true | Out-Null
        Write-Host "  ✓ Selected LDAP attribute 'postalCode'" -ForegroundColor Green
    }

    $exportRuleName = "$ldapSystemName Export Users"
    $exportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq $exportRuleName } | Select-Object -First 1
    if (-not $exportRule) {
        throw "Setup failed: export Synchronisation Rule '$exportRuleName' not found; Setup-Scenario1.ps1 should have created it."
    }

    $existingExportMappings = Get-JIMSyncRuleMapping -SyncRuleId $exportRule.id
    $postalCodeMapping = $existingExportMappings | Where-Object { $_.targetConnectedSystemAttributeId -eq $postalCodeAttr.id }
    if (-not $postalCodeMapping) {
        # Export-mode generation: the assignment is keyed on the Connected System Object, the
        # Metaverse never touched. Random Digits: no base expression needed.
        $postalCodeMapping = New-JIMSyncRuleMapping -SyncRuleId $exportRule.id `
            -TargetConnectedSystemAttributeId $postalCodeAttr.id `
            -Generate -TokenKind Random -RandomFormat Digits -RandomLength 6
        Write-Host "  ✓ Generated export mapping created on postalCode (Random, Digits, 6 characters, ID: $($postalCodeMapping.id))" -ForegroundColor Green
    }
    else {
        Write-Host "  postalCode export mapping already exists (ID: $($postalCodeMapping.id))" -ForegroundColor Gray
    }

    # Step 6: Select physicalDeliveryOfficeName and create the ORDINARY (non-generated) export
    # mapping the Adopt before generate test's Locker Code pair needs. Also RFC 4519
    # organizationalPerson, and a genuine Active Directory attribute ("Office"), spare on both.
    Write-TestStep "Step 6" "Creating the ordinary export mapping for Locker Code (physicalDeliveryOfficeName)"

    $officeAttr = $ldapUserType.attributes | Where-Object { $_.name -eq 'physicalDeliveryOfficeName' }
    if (-not $officeAttr) {
        throw "Setup failed: LDAP attribute 'physicalDeliveryOfficeName' not found in the schema for " +
              "'$ldapSystemName' (object type '$ldapUserObjectClass'). It is part of RFC 4519's " +
              "organizationalPerson and is expected on both OpenLDAP and Active Directory; if the schema " +
              "genuinely lacks it, pick a different spare attribute here and in " +
              "Invoke-Scenario23-UniqueValueGeneration.ps1's Adopt before generate step."
    }
    if (-not $officeAttr.selected) {
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $ldapSystem.id -ObjectTypeId $ldapUserType.id -AttributeId $officeAttr.id -Selected $true | Out-Null
        Write-Host "  ✓ Selected LDAP attribute 'physicalDeliveryOfficeName'" -ForegroundColor Green
    }

    $officeMapping = $existingExportMappings | Where-Object { $_.targetConnectedSystemAttributeId -eq $officeAttr.id }
    if (-not $officeMapping) {
        $officeMapping = New-JIMSyncRuleMapping -SyncRuleId $exportRule.id `
            -TargetConnectedSystemAttributeId $officeAttr.id `
            -SourceMetaverseAttributeId $lockerCodeAttr.id
        Write-Host "  ✓ Ordinary export mapping created: Locker Code -> physicalDeliveryOfficeName (ID: $($officeMapping.id))" -ForegroundColor Green
    }
    else {
        Write-Host "  Locker Code -> physicalDeliveryOfficeName mapping already exists (ID: $($officeMapping.id))" -ForegroundColor Gray
    }
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

# Summary
Write-TestSection "Scenario 23 Setup Complete"
Write-Host "Directory:              $($DirectoryConfig.ConnectedSystemName) ($($DirectoryConfig.DirectoryType))" -ForegroundColor Cyan
Write-Host "Account Name:           Generated (OnlyIfTaken, Number)" -ForegroundColor Cyan
Write-Host "Staff Number:        Generated (Sequence, EMP-NNNNNN from 1000)" -ForegroundColor Cyan
Write-Host "Badge Code:             Generated (Random, Hex, 8 characters)" -ForegroundColor Cyan
Write-Host "Call Sign:              Generated, disabled (OnlyIfTaken \"CALLSIGN\", AttemptLimit 1)" -ForegroundColor Cyan
Write-Host "Locker Code:            Generated, disabled (OnlyIfTaken \"LOCKER\"); ordinary export -> physicalDeliveryOfficeName" -ForegroundColor Cyan
Write-Host "postalCode (export):    Generated (Random, Digits, 6 characters)" -ForegroundColor Cyan
Write-Host ""

# Return Scenario 1's configuration plus the ids this scenario's assertions need.
$config.ImportRuleId = $importRule.id
$config.ExportRuleId = $exportRule.id
$config.EmployeeNumberMvAttributeId = $employeeNumberAttr.id
$config.BadgeCodeMvAttributeId = $badgeCodeAttr.id
$config.CallSignMvAttributeId = $callSignAttr.id
$config.LockerCodeMvAttributeId = $lockerCodeAttr.id
$config.EmployeeNumberMappingId = $employeeNumberMapping.id
$config.BadgeCodeMappingId = $badgeCodeMapping.id
$config.CallSignMappingId = $callSignMapping.id
$config.LockerCodeMappingId = $lockerCodeMapping.id
$config.PostalCodeMappingId = $postalCodeMapping.id
$config.PostalCodeAttributeId = $postalCodeAttr.id
$config.OfficeAttributeId = $officeAttr.id
return $config
