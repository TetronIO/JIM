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
      - A generated export mapping on the LDAP target's "preferredLanguage" attribute (Random token,
        Digits format, length 6): export-mode generation, keyed on the Connected System Object, the
        Metaverse untouched. preferredLanguage is part of RFC 4519's organizationalPerson (which both
        inetOrgPerson and Active Directory's user class descend from) and is not used by any other
        Scenario 1 mapping, so it is genuinely spare on both directory types this scenario supports.
      - The IDs the Brownfield test step needs to add an import Attribute Flow from the directory
        (its account name attribute to Account Name) at higher priority than the generated flow.
        That step creates the directory import Synchronisation Rule itself, so Tests 1 to 6 run
        against Scenario 1's shape unchanged.

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
    $accountNameAttr = $mvAttributes | Where-Object { $_.name -eq "Account Name" }

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

    # Step 5: Select a spare LDAP attribute and create a generated EXPORT mapping on it.
    Write-TestStep "Step 5" "Creating the generated export mapping (preferredLanguage)"

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

    $preferredLanguageAttr = $ldapUserType.attributes | Where-Object { $_.name -eq 'preferredLanguage' }
    if (-not $preferredLanguageAttr) {
        throw "Setup failed: LDAP attribute 'preferredLanguage' not found in the schema for '$ldapSystemName' " +
              "(object type '$ldapUserObjectClass'). preferredLanguage is part of RFC 4519's organizationalPerson " +
              "and is expected on both OpenLDAP and Active Directory; if the schema genuinely lacks it, " +
              "pick a different spare attribute here and in Invoke-Scenario23-UniqueValueGeneration.ps1's " +
              "Export mode assertions."
    }
    if (-not $preferredLanguageAttr.selected) {
        Set-JIMConnectedSystemAttribute -ConnectedSystemId $ldapSystem.id -ObjectTypeId $ldapUserType.id -AttributeId $preferredLanguageAttr.id -Selected $true | Out-Null
        Write-Host "  ✓ Selected LDAP attribute 'preferredLanguage'" -ForegroundColor Green
    }

    $exportRuleName = "$ldapSystemName Export Users"
    $exportRule = @(Get-JIMSyncRule) | Where-Object { $_.name -eq $exportRuleName } | Select-Object -First 1
    if (-not $exportRule) {
        throw "Setup failed: export Synchronisation Rule '$exportRuleName' not found; Setup-Scenario1.ps1 should have created it."
    }

    $existingExportMappings = Get-JIMSyncRuleMapping -SyncRuleId $exportRule.id
    $preferredLanguageMapping = $existingExportMappings | Where-Object { $_.targetConnectedSystemAttributeId -eq $preferredLanguageAttr.id }
    if (-not $preferredLanguageMapping) {
        # Export-mode generation: the assignment is keyed on the Connected System Object, the
        # Metaverse never touched. Random Digits: no base expression needed.
        $preferredLanguageMapping = New-JIMSyncRuleMapping -SyncRuleId $exportRule.id `
            -TargetConnectedSystemAttributeId $preferredLanguageAttr.id `
            -Generate -TokenKind Random -RandomFormat Digits -RandomLength 6
        Write-Host "  ✓ Generated export mapping created on preferredLanguage (Random, Digits, 6 characters, ID: $($preferredLanguageMapping.id))" -ForegroundColor Green
    }
    else {
        Write-Host "  preferredLanguage export mapping already exists (ID: $($preferredLanguageMapping.id))" -ForegroundColor Gray
    }

    # Step 6: Resolve what the Brownfield test step needs: the directory's account name attribute
    # (uid on an RFC directory, sAMAccountName on Active Directory) and the generated Account Name
    # mapping on the HR import rule, so it can put a directory flow ahead of it in Attribute Priority.
    Write-TestStep "Step 6" "Resolving the directory account name attribute and the generated Account Name mapping"

    $accountNameLdapAttrName = $DirectoryConfig.UserNameAttr
    $accountNameLdapAttr = $ldapUserType.attributes | Where-Object { $_.name -eq $accountNameLdapAttrName }
    if (-not $accountNameLdapAttr) {
        throw "Setup failed: LDAP attribute '$accountNameLdapAttrName' not found on '$ldapSystemName' (object type '$ldapUserObjectClass')."
    }
    $accountNameMapping = @(Get-JIMSyncRuleMapping -SyncRuleId $importRule.id) | Where-Object { $_.targetMetaverseAttributeId -eq $accountNameAttr.id } | Select-Object -First 1
    if (-not $accountNameMapping) {
        throw "Setup failed: no Account Name mapping on '$importRuleName'; Setup-Scenario1.ps1 -GenerateAccountName should have created it."
    }
    $mvUserTypeId = $mvUserType.id
    Write-Host "  ✓ Directory account name attribute: $accountNameLdapAttrName (ID: $($accountNameLdapAttr.id)); generated Account Name mapping ID: $($accountNameMapping.id)" -ForegroundColor Green
}
finally {
    Disconnect-JIM -ErrorAction SilentlyContinue
    Remove-Module JIM -Force -ErrorAction SilentlyContinue
}

# Summary
Write-TestSection "Scenario 23 Setup Complete"
Write-Host "Directory:              $($DirectoryConfig.ConnectedSystemName) ($($DirectoryConfig.DirectoryType))" -ForegroundColor Cyan
Write-Host "Account Name:           Generated (OnlyIfTaken, Number)" -ForegroundColor Cyan
Write-Host "Staff Number:           Generated (Sequence, EMP-NNNNNN from 1000)" -ForegroundColor Cyan
Write-Host "Badge Code:             Generated (Random, Hex, 8 characters)" -ForegroundColor Cyan
Write-Host "Call Sign:              Generated, disabled (OnlyIfTaken \"CALLSIGN\", AttemptLimit 1)" -ForegroundColor Cyan
Write-Host "preferredLanguage:      Generated (export mode) (Random, Digits, 6 characters)" -ForegroundColor Cyan
Write-Host ""

# Return Scenario 1's configuration plus the ids this scenario's assertions need.
$config.ImportRuleId = $importRule.id
$config.ExportRuleId = $exportRule.id
$config.EmployeeNumberMvAttributeId = $employeeNumberAttr.id
$config.BadgeCodeMvAttributeId = $badgeCodeAttr.id
$config.CallSignMvAttributeId = $callSignAttr.id
$config.AccountNameMvAttributeId = $accountNameAttr.id
$config.AccountNameMappingId = $accountNameMapping.id
$config.AccountNameLdapAttributeId = $accountNameLdapAttr.id
$config.LdapUserTypeId = $ldapUserType.id
$config.MvUserTypeId = $mvUserTypeId
$config.EmployeeNumberMappingId = $employeeNumberMapping.id
$config.BadgeCodeMappingId = $badgeCodeMapping.id
$config.CallSignMappingId = $callSignMapping.id
$config.PreferredLanguageMappingId = $preferredLanguageMapping.id
$config.PreferredLanguageAttributeId = $preferredLanguageAttr.id
return $config
