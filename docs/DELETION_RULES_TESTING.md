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
   Set-JIMMetaverseObjectType -Id $userType.id `
       -DeletionRule "WhenLastConnectorDisconnected" `
       -DeletionGracePeriodDays 7
   ```

3. Verify configuration:
   ```powershell
   $userType = Get-JIMMetaverseObjectType -Name "User"
   Write-Host "Deletion Rule: $($userType.deletionRule)"
   Write-Host "Grace Period: $($userType.deletionGracePeriodDays) days"
   ```

**Expected Result**:
- Deletion rule set to `WhenLastConnectorDisconnected`
- Grace period set to 7 days

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

### Scenario 4: Admin Account Protection

**Purpose**: Verify that internal accounts (Origin=Internal) are never auto-deleted

**Steps**:
1. Get the admin MVO:
   ```powershell
   $adminMvos = Get-JIMMetaverseObject -ObjectType "User" -SearchQuery "admin"
   ```

2. Check the database directly to verify Origin field:
   ```sql
   SELECT "Id", "DisplayName", "Origin", "LastConnectorDisconnectedDate"
   FROM "MetaverseObjects"
   WHERE "DisplayName" LIKE '%admin%';
   ```

**Expected Result**:
- Admin account has `Origin = 1` (Internal)
- Admin account will never be returned by `GetMetaverseObjectsEligibleForDeletionAsync`
- Admin account persists even with no CSO connections

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
    (SELECT COUNT(*) FROM "ConnectedSystemObjects" WHERE "MetaverseObjectId" = mo."Id") AS "ConnectorCount"
FROM "MetaverseObjects" mo
JOIN "MetaverseObjectTypes" mot ON mo."TypeId" = mot."Id"
WHERE mo."LastConnectorDisconnectedDate" IS NOT NULL
    AND mo."Origin" = 0  -- Projected
    AND mot."DeletionRule" = 1;  -- WhenLastConnectorDisconnected
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

## Integration Test Script

The automated integration test can be run with:
```powershell
./test/integration/scenarios/Invoke-Scenario4-DeletionRules.ps1 `
    -Step All `
    -Template Nano `
    -JIMUrl "http://localhost:5200" `
    -ApiKey "your-api-key"
```

This tests all scenarios automatically in sequence.
