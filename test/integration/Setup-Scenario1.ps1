<#
.SYNOPSIS
    Configure JIM for Scenario 1: HR to Enterprise Directory

.DESCRIPTION
    Sets up Connected Systems and Sync Rules for HR to Active Directory provisioning.
    This script creates:
    - CSV Connected System (HR source)
    - LDAP Connected System (Samba AD target)
    - Sync Rules for attribute mappings
    - Run Profiles for synchronisation

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200 for host access)

.PARAMETER ApiKey
    API key for authentication (if not provided, will attempt to create one)

.PARAMETER Template
    Data scale template (Micro, Small, Medium, Large, XLarge, XXLarge)

.EXAMPLE
    ./Setup-Scenario1.ps1 -JIMUrl "http://localhost:5200" -ApiKey "jim_abc123..."

.EXAMPLE
    ./Setup-Scenario1.ps1 -Template Small

.NOTES
    This script requires the JIM PowerShell module and assumes:
    - JIM is running and accessible
    - Samba AD container is running on jim-network
    - CSV files exist at /var/connector-files/test-data/hr-users.csv
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = "http://localhost:5200",

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [ValidateSet("Micro", "Small", "Medium", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Scenario 1 Setup: HR to Enterprise Directory"

# Step 1: Import JIM PowerShell module
Write-TestStep "Step 1" "Importing JIM PowerShell module"

$modulePath = "$PSScriptRoot/../../JIM.PowerShell/JIM/JIM.psd1"
if (-not (Test-Path $modulePath)) {
    throw "JIM PowerShell module not found at: $modulePath"
}

# Remove any existing module to ensure fresh import with latest cmdlets
Remove-Module JIM -Force -ErrorAction SilentlyContinue

Import-Module $modulePath -Force -ErrorAction Stop
Write-Host "  ✓ JIM PowerShell module imported" -ForegroundColor Green

# Verify Get-JIMConnectorDefinition is available
if (-not (Get-Command Get-JIMConnectorDefinition -ErrorAction SilentlyContinue)) {
    throw "Get-JIMConnectorDefinition cmdlet not found. Module may not have loaded correctly."
}

# Step 2: Connect to JIM
Write-TestStep "Step 2" "Connecting to JIM at $JIMUrl"

if (-not $ApiKey) {
    Write-Host "  No API key provided - this scenario requires an existing API key" -ForegroundColor Yellow
    Write-Host "  Create an API key via JIM web UI: Admin > API Keys" -ForegroundColor Yellow
    throw "API key required for authentication"
}

try {
    Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null
    Write-Host "  ✓ Connected to JIM" -ForegroundColor Green
}
catch {
    Write-Host "  ✗ Failed to connect to JIM: $_" -ForegroundColor Red
    Write-Host "  Ensure JIM is running and accessible at $JIMUrl" -ForegroundColor Yellow
    throw
}

# Step 3: Get connector definitions
Write-TestStep "Step 3" "Getting connector definitions"

try {
    # Get all connector definitions
    $connectorDefs = Get-JIMConnectorDefinition

    $csvConnector = $connectorDefs | Where-Object { $_.name -eq "JIM File Connector" }
    $ldapConnector = $connectorDefs | Where-Object { $_.name -eq "JIM LDAP Connector" }

    if (-not $csvConnector) {
        throw "JIM File Connector definition not found"
    }

    if (-not $ldapConnector) {
        throw "JIM LDAP Connector definition not found"
    }

    Write-Host "  ✓ Found File connector (ID: $($csvConnector.id))" -ForegroundColor Green
    Write-Host "  ✓ Found LDAP connector (ID: $($ldapConnector.id))" -ForegroundColor Green
}
catch {
    Write-Host "  ✗ Failed to get connector definitions: $_" -ForegroundColor Red
    throw
}

# Step 4: Create CSV Connected System (HR source)
Write-TestStep "Step 4" "Creating CSV Connected System"

try {
    # Check if already exists
    $existingSystems = Get-JIMConnectedSystem
    $csvSystem = $existingSystems | Where-Object { $_.name -eq "HR CSV Source" }

    if ($csvSystem) {
        Write-Host "  Connected System 'HR CSV Source' already exists (ID: $($csvSystem.id))" -ForegroundColor Yellow
    }
    else {
        $csvSystem = New-JIMConnectedSystem `
            -Name "HR CSV Source" `
            -Description "HR system CSV export for integration testing" `
            -ConnectorDefinitionId $csvConnector.id `
            -PassThru

        Write-Host "  ✓ Created CSV Connected System (ID: $($csvSystem.id))" -ForegroundColor Green
    }

    # Configure CSV settings
    # Get settings from the connector definition to find setting IDs
    $csvConnectorFull = Get-JIMConnectorDefinition -Id $csvConnector.id
    $filePathSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "File Path" }
    $delimiterSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "Delimiter" }
    $objectTypeSetting = $csvConnectorFull.settings | Where-Object { $_.name -eq "Object Type" }

    $settingValues = @{}
    if ($filePathSetting) {
        $settingValues[$filePathSetting.id] = @{ stringValue = "/var/connector-files/test-data/hr-users.csv" }
    }
    if ($delimiterSetting) {
        $settingValues[$delimiterSetting.id] = @{ stringValue = "," }
    }
    if ($objectTypeSetting) {
        $settingValues[$objectTypeSetting.id] = @{ stringValue = "person" }
    }

    if ($settingValues.Count -gt 0) {
        Set-JIMConnectedSystem -Id $csvSystem.id -SettingValues $settingValues | Out-Null
        Write-Host "  ✓ Configured CSV settings" -ForegroundColor Green
    }
}
catch {
    Write-Host "  ✗ Failed to create/configure CSV Connected System: $_" -ForegroundColor Red
    throw
}

# Step 5: Create LDAP Connected System (Samba AD target)
Write-TestStep "Step 5" "Creating LDAP Connected System"

try {
    $ldapSystem = $existingSystems | Where-Object { $_.name -eq "Samba AD Primary" }

    if ($ldapSystem) {
        Write-Host "  Connected System 'Samba AD Primary' already exists (ID: $($ldapSystem.id))" -ForegroundColor Yellow
    }
    else {
        $ldapSystem = New-JIMConnectedSystem `
            -Name "Samba AD Primary" `
            -Description "Samba Active Directory for integration testing" `
            -ConnectorDefinitionId $ldapConnector.id `
            -PassThru

        Write-Host "  ✓ Created LDAP Connected System (ID: $($ldapSystem.id))" -ForegroundColor Green
    }

    # Configure LDAP settings
    # Note: Using LDAPS (port 636) with Simple authentication over TLS
    # This satisfies AD's strong authentication requirement
    $ldapConnectorFull = Get-JIMConnectorDefinition -Id $ldapConnector.id

    # Find setting IDs by name (using actual setting names from connector definition)
    $hostSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Host" }
    $portSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Port" }
    $usernameSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Username" }
    $passwordSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Password" }
    $useSSLSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Use Secure Connection (LDAPS)?" }
    $certValidationSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Certificate Validation" }
    $connectionTimeoutSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Connection Timeout" }
    $authTypeSetting = $ldapConnectorFull.settings | Where-Object { $_.name -eq "Authentication Type" }

    $ldapSettings = @{}
    if ($hostSetting) {
        $ldapSettings[$hostSetting.id] = @{ stringValue = "samba-ad-primary" }
    }
    if ($portSetting) {
        # Use LDAPS port 636 for encrypted connection
        $ldapSettings[$portSetting.id] = @{ intValue = 636 }
    }
    if ($usernameSetting) {
        # DN format for Simple bind
        $ldapSettings[$usernameSetting.id] = @{ stringValue = "CN=Administrator,CN=Users,DC=testdomain,DC=local" }
    }
    if ($passwordSetting) {
        # Password setting uses stringValue - API stores it encrypted based on setting type
        $ldapSettings[$passwordSetting.id] = @{ stringValue = "Test@123!" }
    }
    if ($useSSLSetting) {
        # Enable LDAPS for encrypted connection
        $ldapSettings[$useSSLSetting.id] = @{ checkboxValue = $true }
    }
    if ($certValidationSetting) {
        # Skip cert validation for self-signed test certificates
        $ldapSettings[$certValidationSetting.id] = @{ stringValue = "Skip Validation (Not Recommended)" }
    }
    if ($connectionTimeoutSetting) {
        $ldapSettings[$connectionTimeoutSetting.id] = @{ intValue = 30 }
    }
    if ($authTypeSetting) {
        # Simple authentication over TLS satisfies AD strong auth requirement
        $ldapSettings[$authTypeSetting.id] = @{ stringValue = "Simple" }
    }

    if ($ldapSettings.Count -gt 0) {
        Set-JIMConnectedSystem -Id $ldapSystem.id -SettingValues $ldapSettings | Out-Null
        Write-Host "  ✓ Configured LDAP settings" -ForegroundColor Green
    }
}
catch {
    Write-Host "  ✗ Failed to create/configure LDAP Connected System: $_" -ForegroundColor Red
    throw
}

# Step 6: Import Schemas
Write-TestStep "Step 6" "Importing Connected System Schemas"

try {
    # Import CSV schema
    Write-Host "  Importing CSV schema..." -ForegroundColor Gray
    $csvSystemUpdated = Import-JIMConnectedSystemSchema -Id $csvSystem.id -PassThru
    $csvObjectTypes = Get-JIMConnectedSystem -Id $csvSystem.id -ObjectTypes
    Write-Host "  ✓ CSV schema imported ($($csvObjectTypes.Count) object types)" -ForegroundColor Green
}
catch {
    Write-Host "  ✗ Failed to import CSV schema: $_" -ForegroundColor Red
    Write-Host "    Ensure connected system is properly configured before importing schema" -ForegroundColor Yellow
    throw
}

try {
    # Import LDAP schema
    Write-Host "  Importing LDAP schema..." -ForegroundColor Gray
    $ldapSystemUpdated = Import-JIMConnectedSystemSchema -Id $ldapSystem.id -PassThru
    $ldapObjectTypes = Get-JIMConnectedSystem -Id $ldapSystem.id -ObjectTypes
    Write-Host "  ✓ LDAP schema imported ($($ldapObjectTypes.Count) object types)" -ForegroundColor Green
}
catch {
    Write-Host "  ⚠ LDAP schema import failed: $_" -ForegroundColor Yellow
    Write-Host "    LDAP export sync rules will be skipped. Continuing with CSV import only." -ForegroundColor Yellow
    $ldapObjectTypes = @()
}

# Step 6b: Create Sync Rules
Write-TestStep "Step 6b" "Creating Sync Rules"

try {
    # Get the "user" object type from both systems (common in identity management)
    # For CSV, it might be "Person" or similar; for LDAP it's typically "user"
    $csvUserType = $csvObjectTypes | Where-Object { $_.name -match "^(user|person|record)$" } | Select-Object -First 1
    $ldapUserType = $ldapObjectTypes | Where-Object { $_.name -eq "user" } | Select-Object -First 1

    # Get the Metaverse "User" object type
    $mvUserType = Get-JIMMetaverseObjectType | Where-Object { $_.name -eq "User" } | Select-Object -First 1

    if (-not $csvUserType) {
        Write-Host "  ⚠ No user/person object type found in CSV schema. Available types:" -ForegroundColor Yellow
        $csvObjectTypes | ForEach-Object { Write-Host "    - $($_.name)" -ForegroundColor Gray }
        Write-Host "  Skipping sync rule creation" -ForegroundColor Yellow
    }
    elseif (-not $ldapUserType) {
        Write-Host "  ⚠ No 'user' object type found in LDAP schema. Available types:" -ForegroundColor Yellow
        $ldapObjectTypes | ForEach-Object { Write-Host "    - $($_.name)" -ForegroundColor Gray }
        Write-Host "  Skipping sync rule creation" -ForegroundColor Yellow
    }
    elseif (-not $mvUserType) {
        Write-Host "  ⚠ No 'User' object type found in Metaverse" -ForegroundColor Yellow
        Write-Host "  Skipping sync rule creation" -ForegroundColor Yellow
    }
    else {
        Write-Host "  Found object types:" -ForegroundColor Gray
        Write-Host "    CSV: $($csvUserType.name) (ID: $($csvUserType.id))" -ForegroundColor Gray
        Write-Host "    LDAP: $($ldapUserType.name) (ID: $($ldapUserType.id))" -ForegroundColor Gray
        Write-Host "    Metaverse: $($mvUserType.name) (ID: $($mvUserType.id))" -ForegroundColor Gray

        # Mark the required object types as selected (required for import/export to work)
        $selectObjectTypesSql = @"
UPDATE "ConnectedSystemObjectTypes"
SET "Selected" = true
WHERE "Id" IN ($($csvUserType.id), $($ldapUserType.id));
"@
        $selectObjectTypesSql | docker compose -f /workspaces/JIM/docker-compose.yml -f /workspaces/JIM/docker-compose.override.codespaces.yml exec -T jim.database psql -U jim -d jim > $null
        Write-Host "  ✓ Selected CSV 'person' and LDAP 'User' object types" -ForegroundColor Green

        # Mark employeeId as the External ID (anchor) for CSV object type
        $setExternalIdSql = @"
UPDATE "ConnectedSystemAttributes"
SET "IsExternalId" = true
WHERE "ConnectedSystemObjectTypeId" = $($csvUserType.id) AND "Name" = 'employeeId';
"@
        $setExternalIdSql | docker compose -f /workspaces/JIM/docker-compose.yml -f /workspaces/JIM/docker-compose.override.codespaces.yml exec -T jim.database psql -U jim -d jim > $null
        Write-Host "  ✓ Set 'employeeId' as External ID for CSV object type" -ForegroundColor Green

        # Create Import sync rule (CSV -> Metaverse)
        $existingRules = Get-JIMSyncRule
        $importRuleName = "HR CSV Import Users"
        $importRule = $existingRules | Where-Object { $_.name -eq $importRuleName }

        if (-not $importRule) {
            $importRule = New-JIMSyncRule `
                -Name $importRuleName `
                -ConnectedSystemId $csvSystem.id `
                -ConnectedSystemObjectTypeId $csvUserType.id `
                -MetaverseObjectTypeId $mvUserType.id `
                -Direction Import `
                -ProjectToMetaverse `
                -PassThru
            Write-Host "  ✓ Created import sync rule: $importRuleName" -ForegroundColor Green
        }
        else {
            Write-Host "  Import sync rule '$importRuleName' already exists" -ForegroundColor Gray
        }

        # Create Export sync rule (Metaverse -> LDAP)
        $exportRuleName = "Samba AD Export Users"
        $exportRule = $existingRules | Where-Object { $_.name -eq $exportRuleName }

        if (-not $exportRule) {
            $exportRule = New-JIMSyncRule `
                -Name $exportRuleName `
                -ConnectedSystemId $ldapSystem.id `
                -ConnectedSystemObjectTypeId $ldapUserType.id `
                -MetaverseObjectTypeId $mvUserType.id `
                -Direction Export `
                -ProvisionToConnectedSystem `
                -PassThru
            Write-Host "  ✓ Created export sync rule: $exportRuleName" -ForegroundColor Green
        }
        else {
            Write-Host "  Export sync rule '$exportRuleName' already exists" -ForegroundColor Gray
        }
    }
}
catch {
    Write-Host "  ✗ Failed to create sync rules: $_" -ForegroundColor Red
    Write-Host "    Error details: $($_.Exception.Message)" -ForegroundColor Red
    # Continue - sync rules can be created manually if needed
}

# Step 6c: Configure Attribute Flow Mappings
Write-TestStep "Step 6c" "Configuring Attribute Flow Mappings"

try {
    if ($importRule -and $exportRule) {
        Write-Host "  Configuring attribute mappings via database..." -ForegroundColor Gray

        # Get attribute IDs for mapping
        $importMappings = @(
            @{ CsAttr = "employeeId";    MvAttr = "Employee ID";    CsId = $null; MvId = $null }
            @{ CsAttr = "firstName";     MvAttr = "First Name";     CsId = $null; MvId = $null }
            @{ CsAttr = "lastName";      MvAttr = "Last Name";      CsId = $null; MvId = $null }
            @{ CsAttr = "displayName";   MvAttr = "Display Name";   CsId = $null; MvId = $null }
            @{ CsAttr = "email";         MvAttr = "Email";          CsId = $null; MvId = $null }
            @{ CsAttr = "title";         MvAttr = "Job Title";      CsId = $null; MvId = $null }
            @{ CsAttr = "department";    MvAttr = "Department";     CsId = $null; MvId = $null }
            @{ CsAttr = "samAccountName"; MvAttr = "Account Name";  CsId = $null; MvId = $null }
        )

        $exportMappings = @(
            @{ MvAttr = "Account Name";  LdapAttr = "sAMAccountName"; MvId = $null; LdapId = $null }
            @{ MvAttr = "First Name";    LdapAttr = "givenName";      MvId = $null; LdapId = $null }
            @{ MvAttr = "Last Name";     LdapAttr = "sn";             MvId = $null; LdapId = $null }
            @{ MvAttr = "Display Name";  LdapAttr = "displayName";    MvId = $null; LdapId = $null }
            @{ MvAttr = "Display Name";  LdapAttr = "cn";             MvId = $null; LdapId = $null }
            @{ MvAttr = "Email";         LdapAttr = "mail";           MvId = $null; LdapId = $null }
            @{ MvAttr = "Job Title";     LdapAttr = "title";          MvId = $null; LdapId = $null }
            @{ MvAttr = "Department";    LdapAttr = "department";     MvId = $null; LdapId = $null }
        )

        # SQL to add import mappings (CSV → Metaverse)
        $importSql = @"
-- Import attribute flow mappings (CSV → Metaverse)
DO `$`$
DECLARE
    mapping_id INT;
    csv_attr_id INT;
    mv_attr_id INT;
BEGIN
"@

        foreach ($mapping in $importMappings) {
            $importSql += @"

    -- Map CSV.$($mapping.CsAttr) → Metaverse.$($mapping.MvAttr)
    SELECT ca."Id" INTO csv_attr_id
    FROM "ConnectedSystemAttributes" ca
    WHERE ca."ConnectedSystemObjectTypeId" = $($csvUserType.id) AND ca."Name" = '$($mapping.CsAttr)';

    SELECT ma."Id" INTO mv_attr_id
    FROM "MetaverseAttributes" ma
    WHERE ma."Name" = '$($mapping.MvAttr)';

    IF csv_attr_id IS NOT NULL AND mv_attr_id IS NOT NULL THEN
        -- Check if mapping already exists
        IF NOT EXISTS (
            SELECT 1 FROM "SyncRuleMappings" srm
            JOIN "SyncRuleMappingSources" srms ON srm."Id" = srms."SyncRuleMappingId"
            WHERE srm."SyncRuleId" = $($importRule.id)
              AND srm."TargetMetaverseAttributeId" = mv_attr_id
              AND srms."ConnectedSystemAttributeId" = csv_attr_id
        ) THEN
            INSERT INTO "SyncRuleMappings" ("Created", "SyncRuleId", "TargetMetaverseAttributeId")
            VALUES (NOW(), $($importRule.id), mv_attr_id)
            RETURNING "Id" INTO mapping_id;

            INSERT INTO "SyncRuleMappingSources" ("Order", "ConnectedSystemAttributeId", "SyncRuleMappingId")
            VALUES (0, csv_attr_id, mapping_id);
        END IF;
    END IF;
"@
        }

        $importSql += @"

END `$`$;
"@

        # SQL to add export mappings (Metaverse → LDAP)
        $exportSql = @"
-- Export attribute flow mappings (Metaverse → LDAP)
DO `$`$
DECLARE
    mapping_id INT;
    ldap_attr_id INT;
    mv_attr_id INT;
BEGIN
"@

        foreach ($mapping in $exportMappings) {
            $exportSql += @"

    -- Map Metaverse.$($mapping.MvAttr) → LDAP.$($mapping.LdapAttr)
    SELECT ca."Id" INTO ldap_attr_id
    FROM "ConnectedSystemAttributes" ca
    WHERE ca."ConnectedSystemObjectTypeId" = $($ldapUserType.id) AND ca."Name" = '$($mapping.LdapAttr)';

    SELECT ma."Id" INTO mv_attr_id
    FROM "MetaverseAttributes" ma
    WHERE ma."Name" = '$($mapping.MvAttr)';

    IF ldap_attr_id IS NOT NULL AND mv_attr_id IS NOT NULL THEN
        -- Check if mapping already exists
        IF NOT EXISTS (
            SELECT 1 FROM "SyncRuleMappings" srm
            JOIN "SyncRuleMappingSources" srms ON srm."Id" = srms."SyncRuleMappingId"
            WHERE srm."SyncRuleId" = $($exportRule.id)
              AND srm."TargetConnectedSystemAttributeId" = ldap_attr_id
              AND srms."MetaverseAttributeId" = mv_attr_id
        ) THEN
            INSERT INTO "SyncRuleMappings" ("Created", "SyncRuleId", "TargetConnectedSystemAttributeId")
            VALUES (NOW(), $($exportRule.id), ldap_attr_id)
            RETURNING "Id" INTO mapping_id;

            INSERT INTO "SyncRuleMappingSources" ("Order", "MetaverseAttributeId", "SyncRuleMappingId")
            VALUES (0, mv_attr_id, mapping_id);
        END IF;
    END IF;
"@
        }

        $exportSql += @"

END `$`$;
"@

        # Execute SQL via docker exec
        $importSql | docker compose -f /workspaces/JIM/docker-compose.yml -f /workspaces/JIM/docker-compose.override.codespaces.yml exec -T jim.database psql -U jim -d jim > $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ Import attribute mappings configured" -ForegroundColor Green
        }
        else {
            Write-Host "  ⚠ Failed to configure import mappings" -ForegroundColor Yellow
        }

        $exportSql | docker compose -f /workspaces/JIM/docker-compose.yml -f /workspaces/JIM/docker-compose.override.codespaces.yml exec -T jim.database psql -U jim -d jim > $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ Export attribute mappings configured" -ForegroundColor Green
        }
        else {
            Write-Host "  ⚠ Failed to configure export mappings" -ForegroundColor Yellow
        }

        # Add object matching rule for CSV object type (how to match CSOs to existing MVOs during import)
        Write-Host "  Configuring object matching rule..." -ForegroundColor Gray

        # Use SQL to create matching rule since the API endpoint may not return attributes with object types
        $matchingRuleSql = @"
-- Object matching rule: employeeId → Employee ID
DO `$`$
DECLARE
    csv_attr_id INT;
    mv_attr_id INT;
    rule_id INT;
BEGIN
    -- Get CSV employeeId attribute
    SELECT ca."Id" INTO csv_attr_id
    FROM "ConnectedSystemAttributes" ca
    WHERE ca."ConnectedSystemObjectTypeId" = $($csvUserType.id) AND ca."Name" = 'employeeId';

    -- Get MV Employee ID attribute
    SELECT ma."Id" INTO mv_attr_id
    FROM "MetaverseAttributes" ma
    WHERE ma."Name" = 'Employee ID';

    IF csv_attr_id IS NOT NULL AND mv_attr_id IS NOT NULL THEN
        -- Check if matching rule already exists
        IF NOT EXISTS (
            SELECT 1 FROM "ObjectMatchingRules" omr
            JOIN "ObjectMatchingRuleSources" omrs ON omr."Id" = omrs."ObjectMatchingRuleId"
            WHERE omr."ConnectedSystemObjectTypeId" = $($csvUserType.id)
              AND omr."TargetMetaverseAttributeId" = mv_attr_id
              AND omrs."ConnectedSystemAttributeId" = csv_attr_id
        ) THEN
            INSERT INTO "ObjectMatchingRules" ("Created", "Order", "ConnectedSystemObjectTypeId", "TargetMetaverseAttributeId")
            VALUES (NOW(), 0, $($csvUserType.id), mv_attr_id)
            RETURNING "Id" INTO rule_id;

            INSERT INTO "ObjectMatchingRuleSources" ("Order", "ConnectedSystemAttributeId", "ObjectMatchingRuleId")
            VALUES (0, csv_attr_id, rule_id);

            RAISE NOTICE 'Created matching rule for employeeId → Employee ID';
        ELSE
            RAISE NOTICE 'Matching rule already exists';
        END IF;
    ELSE
        RAISE NOTICE 'Could not find required attributes (csv_attr_id=%, mv_attr_id=%)', csv_attr_id, mv_attr_id;
    END IF;
END `$`$;
"@

        $matchingRuleSql | docker compose -f /workspaces/JIM/docker-compose.yml -f /workspaces/JIM/docker-compose.override.codespaces.yml exec -T jim.database psql -U jim -d jim > $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ Object matching rule configured (employeeId → Employee ID)" -ForegroundColor Green
        }
        else {
            Write-Host "  ⚠ Could not configure object matching rule" -ForegroundColor Yellow
        }
    }
    else {
        Write-Host "  ⚠ Sync rules not found, skipping attribute mappings" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "  ✗ Failed to configure attribute mappings: $_" -ForegroundColor Red
    Write-Host "  Error details: $($_.Exception.Message)" -ForegroundColor Red
    # Continue - mappings can be configured manually if needed
}

# Step 7: Create Run Profiles
Write-TestStep "Step 7" "Creating Run Profiles"

try {
    # Get existing run profiles for each connected system
    $csvProfiles = Get-JIMRunProfile -ConnectedSystemId $csvSystem.id
    $ldapProfiles = Get-JIMRunProfile -ConnectedSystemId $ldapSystem.id

    # Full Import from CSV - FilePath is required for file-based connectors
    $csvFilePath = "/var/connector-files/test-data/hr-users.csv"
    $csvImportProfile = $csvProfiles | Where-Object { $_.name -eq "HR CSV - Full Import" }
    if (-not $csvImportProfile) {
        $csvImportProfile = New-JIMRunProfile `
            -Name "HR CSV - Full Import" `
            -ConnectedSystemId $csvSystem.id `
            -RunType "FullImport" `
            -FilePath $csvFilePath `
            -PassThru
        Write-Host "  ✓ Created 'HR CSV - Full Import' run profile" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'HR CSV - Full Import' already exists" -ForegroundColor Gray
    }

    # Export to LDAP (Note: 'Export' is the correct RunType, not 'FullExport')
    $ldapExportProfile = $ldapProfiles | Where-Object { $_.name -eq "Samba AD - Export" }
    if (-not $ldapExportProfile) {
        $ldapExportProfile = New-JIMRunProfile `
            -Name "Samba AD - Export" `
            -ConnectedSystemId $ldapSystem.id `
            -RunType "Export" `
            -PassThru
        Write-Host "  ✓ Created 'Samba AD - Export' run profile" -ForegroundColor Green
    }
    else {
        Write-Host "  Run profile 'Samba AD - Export' already exists" -ForegroundColor Gray
    }
}
catch {
    Write-Host "  ✗ Failed to create run profiles: $_" -ForegroundColor Red
    Write-Host "  Error details: $($_.Exception.Message)" -ForegroundColor Red
    # Continue - run profiles might need manual configuration
}

# Summary
Write-TestSection "Setup Complete"
Write-Host "Template:        $Template" -ForegroundColor Cyan
Write-Host "CSV System ID:   $($csvSystem.id)" -ForegroundColor Cyan
Write-Host "LDAP System ID:  $($ldapSystem.id)" -ForegroundColor Cyan
Write-Host ""
Write-Host "✓ Scenario 1 setup complete" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Run: ./scenarios/Invoke-Scenario1-HRToDirectory.ps1 -Template $Template" -ForegroundColor Gray
Write-Host "  2. Or manually trigger run profiles via JIM UI" -ForegroundColor Gray
Write-Host ""

# Return configuration for use by scenario scripts
return @{
    CSVSystemId = $csvSystem.id
    LDAPSystemId = $ldapSystem.id
    CSVImportProfileId = $csvImportProfile.id
    LDAPExportProfileId = $ldapExportProfile.id
}
