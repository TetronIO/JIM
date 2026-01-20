# MVO Deletion Rules - Manual Integration Testing Guide

This guide provides step-by-step instructions for manually testing the MVO Deletion Rules and Deprovisioning functionality.

## Prerequisites

- JIM instance running with Docker stack (`jim-stack`)
- PowerShell module loaded: `Import-Module JIM.PowerShell/JIM/JIM.psd1`
- API key configured: `Connect-JIM -Url http://localhost:5200 -ApiKey "your-key"`
- At least one connected system configured (e.g., CSV + LDAP)

## Test Scenarios

### Scenario 1: Configure Deletion Rules

**Purpose**: Verify deletion rule configuration via API and PowerShell

**Steps**:
1. Get the User object type:
   ```powershell
   $userType = Get-JIMMetaverseObjectType -Name "User"
   ```

2. Configure deletion rules:
   ```powershell
   # Option A: Delete when ALL connectors disconnect
   Set-JIMMetaverseObjectType -Id $userType.id `
       -DeletionRule "WhenLastConnectorDisconnected" `
       -DeletionGracePeriodDays 7

   # Option B: Delete when authoritative source disconnects (recommended for Leaver scenarios)
   $hrSystem = Get-JIMConnectedSystem -Name "HR System"
   Set-JIMMetaverseObjectType -Id $userType.id `
       -DeletionRule "WhenAuthoritativeSourceDisconnected" `
       -DeletionGracePeriodDays 7 `
       -DeletionTriggerConnectedSystemIds @($hrSystem.id)
   ```

3. Verify configuration:
   ```powershell
   $userType = Get-JIMMetaverseObjectType -Name "User"
   Write-Host "Deletion Rule: $($userType.deletionRule)"
   Write-Host "Grace Period: $($userType.deletionGracePeriodDays) days"
   Write-Host "Authoritative Sources: $($userType.deletionTriggerConnectedSystemIds -join ', ')"
   ```

**Expected Result**:
- Deletion rule set to chosen option
- Grace period set to 7 days
- For `WhenAuthoritativeSourceDisconnected`: Authoritative source IDs listed

---

### Scenario 2: Leaver with Grace Period

**Purpose**: Verify that when a user leaves (CSO deleted), the MVO enters pending deletion state

**Steps**:
1. Provision a test user through JIM (ensure user exists in HR system and synced to directory)

2. Note the MVO ID:
   ```powershell
   $mvos = Get-JIMMetaverseObject -ObjectType "User" -SearchQuery "test.user"
   $mvoId = $mvos.items[0].id
   ```

3. Remove user from source system (e.g., CSV file or HR database)

4. Run synchronisation:
   ```powershell
   # Import from source (marks CSO as obsolete)
   Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1

   # Full Sync (processes obsolete CSO, disconnects from MVO)
   Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 2
   ```

5. Check MVO status:
   ```powershell
   Invoke-RestMethod -Uri "http://localhost:5200/api/v1/metaverse/objects/$mvoId" `
       -Headers @{ "X-API-Key" = $apiKey }
   ```

**Expected Result**:
- MVO still exists
- `lastConnectorDisconnectedDate` is set to current timestamp
- MVO will be deleted after grace period (7 days in this example)
- Worker housekeeping will clean up after grace period expires

---

### Scenario 3: Reconnection (Rehire before Grace Period)

**Purpose**: Verify that reconnecting a CSO before grace period clears pending deletion

**Steps**:
1. Follow Scenario 2 to create a pending deletion MVO

2. Wait a few minutes (but less than grace period)

3. Re-add the user to source system with same identifier

4. Run synchronisation:
   ```powershell
   # Import from source
   Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1

   # Full Sync (rejoins CSO to MVO)
   Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 2
   ```

5. Check MVO status:
   ```powershell
   $mvo = Invoke-RestMethod -Uri "http://localhost:5200/api/v1/metaverse/objects/$mvoId" `
       -Headers @{ "X-API-Key" = $apiKey }

   Write-Host "LastConnectorDisconnectedDate: $($mvo.lastConnectorDisconnectedDate)"
   ```

**Expected Result**:
- `lastConnectorDisconnectedDate` is cleared (null)
- MVO is no longer pending deletion
- User is fully reconnected and active

---

### Scenario 4: Internal Origin Protection

**Purpose**: Verify that MVOs with `Origin=Internal` are never auto-deleted

MVOs can have two origins:
- `Projected` (0): Created by projecting a CSO from a connected system - subject to deletion rules
- `Internal` (1): Created directly in JIM (e.g., service accounts, test identities) - protected from automatic deletion

**Steps**:
1. Query the database to find any Internal MVOs:
   ```sql
   SELECT "Id", "DisplayName", "Origin", "LastConnectorDisconnectedDate"
   FROM "MetaverseObjects"
   WHERE "Origin" = 1;  -- Internal
   ```

2. If no Internal MVOs exist, create one for testing via the API or database:
   ```sql
   -- For testing purposes only
   INSERT INTO "MetaverseObjects" ("Id", "TypeId", "Origin", "Created")
   SELECT gen_random_uuid(), "Id", 1, NOW()
   FROM "MetaverseObjectTypes"
   WHERE "Name" = 'User'
   LIMIT 1;
   ```

3. Verify housekeeping skips Internal MVOs:
   ```sql
   -- This query (used by housekeeping) excludes Internal MVOs
   SELECT COUNT(*) FROM "MetaverseObjects"
   WHERE "Origin" = 0  -- Only Projected
       AND "LastConnectorDisconnectedDate" IS NOT NULL;
   ```

**Expected Result**:
- Internal MVOs (`Origin = 1`) are never returned by `GetMetaverseObjectsEligibleForDeletionAsync`
- Internal MVOs persist indefinitely, even with no CSO connections
- Only Projected MVOs (`Origin = 0`) are evaluated for deletion

**Note**: Admin users who log in via SSO/OIDC are regular Projected MVOs created from the IDP. They are not Internal MVOs. Role assignments (Admin, Operator, etc.) do not affect deletion rules - only the `Origin` field matters.

---

### Scenario 5: Out-of-Scope Deprovisioning (Export)

**Purpose**: Verify OutboundDeprovisionAction when MVO falls out of export rule scope

**Setup**:
1. Create an export sync rule with scoping criteria (e.g., "Department = IT")
2. Configure `OutboundDeprovisionAction`:
   ```powershell
   # Via API or database update
   # Set sync rule's OutboundDeprovisionAction to "Disconnect" or "Delete"
   ```

**Steps**:
1. Provision a user that matches export scope (Department = IT)

2. Verify user exported to target system

3. Change user attribute so they fall out of scope (Department = "Finance")

4. Run synchronisation

5. Check behaviour based on action:
   - **Disconnect**: CSO disconnected from MVO, but remains in target system
   - **Delete**: Pending export created to delete CSO from target system

**Expected Result**:
- For `OutboundDeprovisionAction.Disconnect`: CSO in target system orphaned but not deleted
- For `OutboundDeprovisionAction.Delete`: CSO deleted from target system
- If MVO has no other connectors and is Projected origin, `LastConnectorDisconnectedDate` is set

---

### Scenario 5b: Authoritative Source Deletion (Multi-Source)

**Purpose**: Verify that `WhenAuthoritativeSourceDisconnected` triggers deletion only when authoritative source disconnects

This is the core **Leaver scenario** for multi-source identity management. When HR (authoritative source) removes an employee, the MVO should be marked for deletion even if other CSOs (e.g., Training, Target AD) remain connected.

**Setup**:
1. Configure multiple connected systems:
   - **HR System** (authoritative source) - imports employee data
   - **Training System** (secondary source) - imports training completion data
   - **Target AD** (target) - receives provisioned users

2. Configure User object type with authoritative source deletion:
   ```powershell
   $hrSystem = Get-JIMConnectedSystem -Name "HR System"
   $userType = Get-JIMMetaverseObjectType -Name "User"

   Set-JIMMetaverseObjectType -Id $userType.id `
       -DeletionRule "WhenAuthoritativeSourceDisconnected" `
       -DeletionGracePeriodDays 0 `
       -DeletionTriggerConnectedSystemIds @($hrSystem.id)
   ```

**Steps**:
1. Ensure a user exists with CSOs from all three systems joined to one MVO:
   ```powershell
   $mvos = Search-JIMMetaverseObject -ObjectTypeName "User" `
       -FilterAttribute "Employee ID" -FilterValue "EMP001" -FilterOperator Equals
   $mvoId = $mvos.results[0].id

   # Verify all CSOs are connected
   $mvo = Get-JIMMetaverseObject -Id $mvoId
   Write-Host "Connected CSOs: $($mvo.connectedSystemObjects.Count)"
   ```

2. **Test Part 1**: Delete from Training System (non-authoritative)
   - Remove user from Training source
   - Run Training import and sync
   ```powershell
   Start-JIMRunProfile -ConnectedSystemId $trainingSystem.id -RunProfileName "Delta Import"
   Start-JIMRunProfile -ConnectedSystemId $trainingSystem.id -RunProfileName "Delta Sync"
   ```

3. Check MVO status:
   ```powershell
   $mvo = Get-JIMMetaverseObject -Id $mvoId
   Write-Host "LastConnectorDisconnectedDate: $($mvo.lastConnectorDisconnectedDate)"
   ```

   **Expected**: `LastConnectorDisconnectedDate` should be **null** (not marked for deletion)

4. **Test Part 2**: Delete from HR System (authoritative)
   - Remove user from HR source
   - Run HR import and sync
   ```powershell
   Start-JIMRunProfile -ConnectedSystemId $hrSystem.id -RunProfileName "Delta Import"
   Start-JIMRunProfile -ConnectedSystemId $hrSystem.id -RunProfileName "Delta Sync"
   ```

5. Check MVO status:
   ```powershell
   $mvo = Get-JIMMetaverseObject -Id $mvoId
   Write-Host "LastConnectorDisconnectedDate: $($mvo.lastConnectorDisconnectedDate)"
   ```

   **Expected**: `LastConnectorDisconnectedDate` should be **set** (marked for deletion)

**Expected Result**:
- Training CSO deletion: MVO NOT marked for deletion (non-authoritative source)
- HR CSO deletion: MVO IS marked for deletion (authoritative source disconnected)
- Target AD CSO remains connected (will be deprovisioned when housekeeping deletes MVO)

---

### Scenario 6: Out-of-Scope Deprovisioning (Import)

**Purpose**: Verify InboundOutOfScopeAction when CSO becomes obsolete

**Setup**:
1. Configure import sync rule `InboundOutOfScopeAction`:
   ```sql
   UPDATE "SyncRules"
   SET "InboundOutOfScopeAction" = 1  -- 0 = RemainJoined, 1 = Disconnect
   WHERE "Id" = <sync-rule-id>;
   ```

**Steps**:
1. Provision a user from source system

2. Mark CSO as obsolete (delete from source, or move out of OU/container)

3. Run import sync

4. Check behaviour:
   - **RemainJoined**: CSO deleted but MVO remains (no deletion rule triggered)
   - **Disconnect**: CSO disconnected, MVO deletion rule applies

**Expected Result**:
- For `InboundOutOfScopeAction.RemainJoined`: MVO preserved even after CSO deletion ("always managed")
- For `InboundOutOfScopeAction.Disconnect`: MVO enters pending deletion if no other connectors

---

### Scenario 7: Worker Housekeeping (Automatic Cleanup)

**Purpose**: Verify worker automatically deletes orphaned MVOs after grace period

**Steps**:
1. Create test MVOs with `LastConnectorDisconnectedDate` set to past date:
   ```sql
   UPDATE "MetaverseObjects"
   SET "LastConnectorDisconnectedDate" = NOW() - INTERVAL '8 days',
       "Origin" = 0  -- Projected
   WHERE "Id" = '<test-mvo-id>';
   ```

2. Ensure MVO has no CSOs:
   ```sql
   SELECT COUNT(*) FROM "ConnectedSystemObjects"
   WHERE "MetaverseObjectId" = '<test-mvo-id>';
   ```

3. Wait for worker housekeeping cycle (runs every 60 seconds during idle time)

4. Check worker logs:
   ```bash
   docker logs jim.worker | grep "PerformHousekeepingAsync"
   ```

5. Verify MVO deleted:
   ```sql
   SELECT * FROM "MetaverseObjects" WHERE "Id" = '<test-mvo-id>';
   ```

**Expected Result**:
- Worker log shows: "Found X orphaned MVOs eligible for deletion"
- Worker log shows: "Successfully deleted orphaned MVO {id}"
- MVO no longer exists in database

---

## Verification Queries

### Check MVOs Pending Deletion
```sql
SELECT
    mo."Id",
    mo."DisplayName",
    mo."Origin",
    mo."LastConnectorDisconnectedDate",
    mo."LastConnectorDisconnectedDate" + (mot."DeletionGracePeriodDays" || ' days')::INTERVAL AS "DeletionEligibleDate",
    mot."DeletionRule",
    mot."DeletionGracePeriodDays",
    mot."DeletionTriggerConnectedSystemIds",
    (SELECT COUNT(*) FROM "ConnectedSystemObjects" WHERE "MetaverseObjectId" = mo."Id") AS "ConnectorCount"
FROM "MetaverseObjects" mo
JOIN "MetaverseObjectTypes" mot ON mo."TypeId" = mot."Id"
WHERE mo."LastConnectorDisconnectedDate" IS NOT NULL
    AND mo."Origin" = 0;  -- Projected
-- Note: DeletionRule values: 0=Manual, 1=WhenLastConnectorDisconnected, 2=WhenAuthoritativeSourceDisconnected
```

### Check MVOs Eligible for Immediate Deletion
```sql
SELECT
    mo."Id",
    mo."DisplayName",
    mo."LastConnectorDisconnectedDate",
    mot."DeletionGracePeriodDays"
FROM "MetaverseObjects" mo
JOIN "MetaverseObjectTypes" mot ON mo."TypeId" = mot."Id"
WHERE mo."Origin" = 0  -- Projected
    AND mot."DeletionRule" = 1  -- WhenLastConnectorDisconnected
    AND mo."LastConnectorDisconnectedDate" IS NOT NULL
    AND (mot."DeletionGracePeriodDays" IS NULL
         OR mot."DeletionGracePeriodDays" = 0
         OR mo."LastConnectorDisconnectedDate" + (mot."DeletionGracePeriodDays" || ' days')::INTERVAL <= NOW())
    AND NOT EXISTS (
        SELECT 1 FROM "ConnectedSystemObjects"
        WHERE "MetaverseObjectId" = mo."Id"
    );
```

### Check Sync Rule Deprovisioning Settings
```sql
SELECT
    sr."Id",
    sr."Name",
    sr."Direction",
    sr."OutboundDeprovisionAction",
    sr."InboundOutOfScopeAction",
    cs."Name" AS "ConnectedSystem"
FROM "SyncRules" sr
JOIN "ConnectedSystems" cs ON sr."ConnectedSystemId" = cs."Id"
WHERE sr."Enabled" = true;
```

## Troubleshooting

### MVO Not Deleted After Grace Period

**Check**:
1. Verify worker is running: `docker ps | grep jim.worker`
2. Check worker logs: `docker logs jim.worker | tail -n 100`
3. Verify MVO has no CSOs: `SELECT COUNT(*) FROM "ConnectedSystemObjects" WHERE "MetaverseObjectId" = '<id>'`
4. Check MVO Origin: Must be `Projected` (0), not `Internal` (1)
5. Check grace period calculation: `LastConnectorDisconnectedDate + GracePeriodDays <= NOW()`

### CSO Not Disconnected on Obsoletion

**Check**:
1. Verify sync rule `InboundOutOfScopeAction` setting
2. Check import sync ran after CSO marked obsolete
3. Review full sync logs for deprovisioning messages

### Export Deprovisioning Not Working

**Check**:
1. Verify export sync rule has scoping criteria configured
2. Check MVO attribute changes triggered out-of-scope evaluation
3. Verify `OutboundDeprovisionAction` is configured on sync rule
4. Review logs: `EvaluateOutOfScopeExportsAsync` messages

## Integration Test Scripts

### Scenario 4: Basic Deletion Rules
The automated integration test for basic deletion scenarios:
```powershell
./test/integration/scenarios/Invoke-Scenario4-DeletionRules.ps1 `
    -Step All `
    -Template Nano `
    -JIMUrl "http://localhost:5200" `
    -ApiKey "your-api-key"
```

### Scenario 8: Cross-Domain with Authoritative Source Deletion
Tests `WhenAuthoritativeSourceDisconnected` in a cross-domain AD sync:
```powershell
./test/integration/Run-IntegrationTests.ps1 -Scenario Scenario8-CrossDomainEntitlementSync
```

This scenario:
- Configures Source AD (APAC) as the authoritative source for Groups
- Uses `WhenAuthoritativeSourceDisconnected` deletion rule
- Tests DeleteGroup step: deleting a group from Source AD marks the MVO for deletion
- Validates MVO `LastConnectorDisconnectedDate` is set after sync
- Housekeeping deprovisions the group from Target AD (EMEA)

See [INTEGRATION_TESTING.md](INTEGRATION_TESTING.md) for full details on running integration tests.
