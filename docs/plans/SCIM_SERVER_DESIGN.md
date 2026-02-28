# SCIM 2.0 Server Design Document

- **Status:** Planned
> **Issue**: #124
> **Related Issues**: #123 (Event-Based Sync), #121 (Outbound Sync)
> **Last Updated**: 2025-12-03

## Overview

This document describes how JIM can act as a **SCIM 2.0 Server**, enabling external identity providers (Okta, Azure AD, OneLogin, etc.) to push identity changes directly to JIM. This transforms JIM into a **SCIM Hub** - aggregating identities from multiple SCIM sources and synchronising them to target systems.

### What is SCIM?

SCIM (System for Cross-domain Identity Management) is a standard protocol (RFC 7643/7644) for automating user provisioning. It defines:
- **Resources**: User, Group, and extensible schemas
- **Operations**: Create, Read, Update, Delete, Patch, Bulk
- **Discovery**: Schema and service provider configuration endpoints

### Why SCIM Server for JIM?

1. **Event-Based Inbound** - Eliminates polling; changes pushed in real-time
2. **Enterprise Integration** - Major IdPs (Okta, Azure AD, Ping) support SCIM provisioning
3. **Standards Compliance** - Well-defined spec reduces integration effort
4. **JIM as Hub** - Aggregate from multiple SCIM sources into unified metaverse

---

## Table of Contents

1. [Architecture](#architecture)
2. [SCIM as a Connected System](#scim-as-a-connected-system)
3. [API Endpoints](#api-endpoints)
4. [Authentication](#authentication)
5. [Request Processing Flow](#request-processing-flow)
6. [Schema Mapping](#schema-mapping)
7. [Design Questions](#design-questions)
   - [Q1: SCIM Resource IDs](#q1-how-do-we-handle-scim-resource-ids)
   - [Q2: Filtering and Pagination](#q2-how-do-we-handle-scim-filtering-and-pagination)
   - [Q3: Response Data Source](#q3-should-scim-responses-include-mvo-data-or-cso-data)
   - [Q4: Groups with Members](#q4-how-do-we-handle-scim-groups-with-members)
   - [Q5: Inter-Object Dependencies](#q5-how-does-scim-handle-inter-object-dependencies)
   - [Q6: Bulk Operations](#q6-do-we-need-scim-bulk-operations)
8. [Implementation Approach](#implementation-approach)
9. [Edge Cases & Challenges](#edge-cases--challenges)

---

## Architecture

### SCIM Server is Not a Traditional Connector

Traditional JIM connectors **pull** data from systems. A SCIM server **receives** data pushed by external systems. However, it still fits the Connected System model:

```
┌──────────────────────────────────────────────────────────────────────────┐
│                    JIM INBOUND DATA SOURCES                              │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  ┌─────────────────────┐          ┌─────────────────────┐                │
│  │  Traditional        │          │  SCIM Server        │                │
│  │  Connectors         │          │  (Push-Based)       │                │
│  │                     │          │                     │                │
│  │  - LDAP             │          │  POST /scim/Users   │                │
│  │  - CSV              │          │  PATCH /scim/Users  │                │
│  │  - SQL              │          │  DELETE /scim/Users │                │
│  │                     │          │                     │                │
│  │  JIM initiates      │          │  External system    │                │
│  │  connection         │          │  initiates request  │                │
│  └──────────┬──────────┘          └──────────┬──────────┘                │
│             │                                │                           │
│             │    ┌───────────────────────────┘                           │
│             │    │                                                       │
│             ▼    ▼                                                       │
│  ┌──────────────────────────────────────────────────────────────────┐    │
│  │                                                                  │    │
│  │                    Unified Inbound Sync Engine                   │    │
│  │                                                                  │    │
│  │   StagingObject -> Join/Project -> MVO Update -> Pending Exports │    │
│  │                                                                  │    │
│  └──────────────────────────────────────────────────────────────────┘    │
│                                                                          │
└──────────────────────────────────────────────────────────────────────────┘
```

### Key Architectural Principle: Reuse the Sync Engine

SCIM requests are processed through the **same inbound sync engine** as scheduled imports:

| Data Source | Creates | Processed By |
|-------------|---------|--------------|
| LDAP Full Import | `StagingObject` | `SyncEngine.ProcessInboundObjectAsync()` |
| CSV Delta Import | `StagingObject` | `SyncEngine.ProcessInboundObjectAsync()` |
| **SCIM POST /Users** | `StagingObject` | `SyncEngine.ProcessInboundObjectAsync()` |
| **SCIM PATCH /Users/{id}** | `StagingObject` | `SyncEngine.ProcessInboundObjectAsync()` |

This means:
- Same join/project logic
- Same attribute flow rules
- Same pending export creation
- Same audit trail

---

## SCIM as a Connected System

Each SCIM source is configured as a **Connected System** with a special connector type:

### Connected System Configuration

```
Connected System: "Okta HR"
├── Connector Type: SCIM Server
├── Settings:
│   ├── Authentication Type: Bearer Token
│   ├── Token Value: [encrypted]
│   ├── Allowed IP Ranges: [optional]
│   └── SCIM Endpoint Path: /scim/v2/okta-hr  (auto-generated from system ID)
│
├── Schema: (Fixed SCIM 2.0 schema)
│   ├── User
│   │   ├── userName (string, required)
│   │   ├── displayName (string)
│   │   ├── name.givenName (string)
│   │   ├── name.familyName (string)
│   │   ├── emails[].value (multi-valued string)
│   │   ├── active (boolean)
│   │   └── externalId (string)
│   │
│   └── Group
│       ├── displayName (string, required)
│       ├── members[].value (multi-valued reference)
│       └── externalId (string)
│
└── Sync Rules:
    ├── "SCIM User -> MV Person" (Import)
    └── "SCIM Group -> MV Group" (Import)
```

### The ScimServerConnector Class

```csharp
public class ScimServerConnector : IConnector, IConnectorCapabilities, IConnectorSchema, IConnectorSettings
{
    public string Name => "SCIM 2.0 Server";
    public string Description => "Receives identity data pushed from external SCIM clients (IdPs)";

    // Capability flags - note: no Import interfaces!
    public bool SupportsFullImport => false;   // We don't pull
    public bool SupportsDeltaImport => false;  // We don't pull
    public bool SupportsExport => false;       // Could be true for SCIM client mode (future)
    public bool SupportsPartitions => false;
    public bool SupportsPartitionContainers => false;
    public bool SupportsSecondaryExternalId => true;
    public bool SupportsUserSelectedExternalId => false;
    public bool SupportsUserSelectedAttributeTypes => false;

    // Settings for authentication configuration
    public List<ConnectorSetting> GetSettings() => new()
    {
        new() { Name = "Authentication", Type = ConnectedSystemSettingType.Heading },
        new() { Name = "Auth Type", Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = new() { "Bearer Token", "OAuth 2.0" } },
        new() { Name = "Bearer Token", Type = ConnectedSystemSettingType.StringEncrypted,
                Description = "Token that SCIM clients must provide" },
        new() { Name = "Security", Type = ConnectedSystemSettingType.Heading },
        new() { Name = "Allowed IP Ranges", Type = ConnectedSystemSettingType.String,
                Description = "Optional: Comma-separated CIDR ranges (e.g., 10.0.0.0/8)" },
    };

    // Fixed SCIM 2.0 schema
    public Task<ConnectorSchema> GetSchemaAsync(...) => Task.FromResult(GetScimSchema());

    private ConnectorSchema GetScimSchema()
    {
        return new ConnectorSchema
        {
            ObjectTypes = new List<ConnectorSchemaObjectType>
            {
                CreateScimUserObjectType(),
                CreateScimGroupObjectType()
            }
        };
    }
}
```

---

## API Endpoints

### URL Structure

```
/scim/v2/{systemId}/...
```

Where `{systemId}` is the GUID of the Connected System. This allows multiple SCIM sources, each with their own authentication and sync rules.

### Required SCIM Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/scim/v2/{systemId}/ServiceProviderConfig` | GET | SCIM capabilities |
| `/scim/v2/{systemId}/Schemas` | GET | Available schemas |
| `/scim/v2/{systemId}/ResourceTypes` | GET | Resource type definitions |
| `/scim/v2/{systemId}/Users` | GET | List/search users |
| `/scim/v2/{systemId}/Users` | POST | Create user |
| `/scim/v2/{systemId}/Users/{id}` | GET | Get user |
| `/scim/v2/{systemId}/Users/{id}` | PUT | Replace user |
| `/scim/v2/{systemId}/Users/{id}` | PATCH | Update user |
| `/scim/v2/{systemId}/Users/{id}` | DELETE | Delete user |
| `/scim/v2/{systemId}/Groups` | GET | List/search groups |
| `/scim/v2/{systemId}/Groups` | POST | Create group |
| `/scim/v2/{systemId}/Groups/{id}` | GET | Get group |
| `/scim/v2/{systemId}/Groups/{id}` | PUT | Replace group |
| `/scim/v2/{systemId}/Groups/{id}` | PATCH | Update group |
| `/scim/v2/{systemId}/Groups/{id}` | DELETE | Delete group |

### Controller Structure

```csharp
[Route("scim/v2/{systemId}")]
[ApiController]
public class ScimController : ControllerBase
{
    private readonly IRepository _repository;
    private readonly ISyncEngine _syncEngine;
    private readonly IScimAuthenticator _authenticator;

    [HttpGet("ServiceProviderConfig")]
    public IActionResult GetServiceProviderConfig(Guid systemId) { ... }

    [HttpGet("Schemas")]
    public IActionResult GetSchemas(Guid systemId) { ... }

    [HttpPost("Users")]
    public async Task<IActionResult> CreateUser(
        Guid systemId,
        [FromBody] ScimUser user) { ... }

    [HttpPatch("Users/{userId}")]
    public async Task<IActionResult> PatchUser(
        Guid systemId,
        Guid userId,
        [FromBody] ScimPatchRequest patch) { ... }

    // ... etc
}
```

---

## Authentication

Each SCIM Connected System has its own authentication configuration:

### Option 1: Bearer Token (Simple)

```http
POST /scim/v2/{systemId}/Users
Authorization: Bearer <token-configured-in-connected-system>
Content-Type: application/scim+json
```

### Option 2: OAuth 2.0 (Enterprise)

For IdPs that use OAuth for SCIM:
- JIM exposes token endpoint or validates tokens from IdP
- More complex but more secure

### Authentication Flow

```csharp
public class ScimAuthenticator : IScimAuthenticator
{
    public async Task<bool> AuthenticateAsync(
        Guid systemId,
        HttpRequest request)
    {
        var system = await _repository.ConnectedSystems.GetAsync(systemId);
        if (system?.ConnectorType != "ScimServer")
            return false;

        var authType = system.GetSettingValue("Auth Type");
        var expectedToken = system.GetSettingValue("Bearer Token");

        // Extract Authorization header
        var authHeader = request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader))
            return false;

        if (authType == "Bearer Token")
        {
            return authHeader.StartsWith("Bearer ") &&
                   authHeader.Substring(7) == expectedToken;
        }

        // OAuth validation would go here
        return false;
    }
}
```

---

## Request Processing Flow

### SCIM POST /Users -> MVO Creation

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    SCIM USER CREATION FLOW                              │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  1. HTTP Request Received                                               │
│     POST /scim/v2/{systemId}/Users                                      │
│     Authorization: Bearer xxx                                           │
│     Body: { "userName": "jsmith", "displayName": "John Smith", ... }    │
│                                                                         │
│  2. Load Connected System                                               │
│     ┌─────────────────────────────────────────────────────────────┐     │
│     │ system = repository.ConnectedSystems.Get(systemId)          │     │
│     │ if (system.ConnectorType != "ScimServer") return 404        │     │
│     └─────────────────────────────────────────────────────────────┘     │
│                                                                         │
│  3. Authenticate Request                                                │
│     ┌─────────────────────────────────────────────────────────────┐     │
│     │ if (!authenticator.Authenticate(system, request))           │     │
│     │     return 401 Unauthorized                                 │     │
│     └─────────────────────────────────────────────────────────────┘     │
│                                                                         │
│  4. Map SCIM User -> StagingObject                                      │
│     ┌─────────────────────────────────────────────────────────────┐     │
│     │ stagingObject = new StagingObject                           │     │
│     │ {                                                           │     │
│     │     ExternalId = scimUser.Id ?? Guid.NewGuid(),             │     │
│     │     ObjectType = "User",                                    │     │
│     │     Attributes = MapScimAttributes(scimUser)                │     │
│     │ }                                                           │     │
│     └─────────────────────────────────────────────────────────────┘     │
│                                                                         │
│  5. Process Through Inbound Sync Engine                                 │
│     ┌─────────────────────────────────────────────────────────────┐     │
│     │ // Same code path as scheduled import!                      │     │
│     │ result = await syncEngine.ProcessInboundObjectAsync(        │     │
│     │     connectedSystem: system,                                │     │
│     │     stagingObject: stagingObject,                           │     │
│     │     syncRules: system.SyncRules.Where(r => r.IsImport)      │     │
│     │ )                                                           │     │
│     └─────────────────────────────────────────────────────────────┘     │
│                                                                         │
│  6. Sync Engine Does:                                                   │
│     ├── Find/create CSO for this external ID                            │
│     ├── Evaluate join rules -> find/create MVO                          │
│     ├── Apply attribute flow rules                                      │
│     ├── Update MVO attributes                                           │
│     └── Create Pending Exports (per Option A decision)                  │
│                                                                         │
│  7. Return SCIM Response                                                │
│     ┌─────────────────────────────────────────────────────────────┐     │
│     │ return Created(                                             │     │
│     │     $"/scim/v2/{systemId}/Users/{cso.ExternalId}",          │     │
│     │     MapToScimUser(cso)                                      │     │
│     │ )                                                           │     │
│     └─────────────────────────────────────────────────────────────┘     │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### SCIM PATCH /Users/{id} -> MVO Update

```csharp
[HttpPatch("Users/{userId}")]
public async Task<IActionResult> PatchUser(
    Guid systemId,
    string userId,
    [FromBody] ScimPatchRequest patch)
{
    // 1-3. Same auth flow as above

    // 4. Find existing CSO
    var cso = await _repository.ConnectedSystemObjects
        .GetByExternalIdAsync(systemId, userId);
    if (cso == null)
        return NotFound(ScimError.NotFound(userId));

    // 5. Apply SCIM patch operations to create updated StagingObject
    var stagingObject = ApplyPatchOperations(cso, patch);

    // 6. Process through sync engine (same as create)
    var result = await _syncEngine.ProcessInboundObjectAsync(
        connectedSystem: system,
        stagingObject: stagingObject,
        syncRules: system.SyncRules.Where(r => r.IsImport)
    );

    // 7. Return updated SCIM user
    return Ok(MapToScimUser(result.Cso));
}
```

### SCIM DELETE /Users/{id} -> CSO Disconnection

```csharp
[HttpDelete("Users/{userId}")]
public async Task<IActionResult> DeleteUser(Guid systemId, string userId)
{
    // 1-3. Same auth flow

    // 4. Find existing CSO
    var cso = await _repository.ConnectedSystemObjects
        .GetByExternalIdAsync(systemId, userId);
    if (cso == null)
        return NotFound(ScimError.NotFound(userId));

    // 5. Mark CSO as deleted/disconnected
    // This triggers MVO deletion rules if configured
    await _syncEngine.ProcessInboundDeletionAsync(system, cso);

    // 6. Return success
    return NoContent();
}
```

---

## Schema Mapping

### SCIM User to JIM Attributes

| SCIM Attribute | JIM Staging Attribute | Notes |
|----------------|----------------------|-------|
| `id` | `ExternalId` | SCIM resource ID |
| `externalId` | `ExternalId` (secondary) | Optional client-provided ID |
| `userName` | `userName` | Required, unique |
| `displayName` | `displayName` | |
| `name.givenName` | `givenName` | |
| `name.familyName` | `familyName` | |
| `name.formatted` | `formattedName` | |
| `emails[0].value` | `email` | Primary email |
| `emails` | `emails` (MVA) | All emails as multi-valued |
| `phoneNumbers[0].value` | `phoneNumber` | Primary phone |
| `active` | `active` | Boolean |
| `title` | `title` | Job title |
| `department` | `department` | |
| `manager.value` | `manager` | Reference to another user |

### Sync Rule Example

Admin configures sync rules to map SCIM attributes to MVO:

```
Sync Rule: "SCIM User to MV Person"
├── Source: Connected System "Okta HR", Object Type "User"
├── Target: Metaverse Object Type "Person"
├── Direction: Import
├── Join Rules:
│   └── SCIM.userName = MV.Username
└── Attribute Flow:
    ├── SCIM.displayName -> MV.Display Name
    ├── SCIM.givenName -> MV.First Name
    ├── SCIM.familyName -> MV.Last Name
    ├── SCIM.email -> MV.Email Address
    ├── SCIM.department -> MV.Department
    └── SCIM.active -> MV.Account Enabled
```

---

## Design Questions

### Q1: How do we handle SCIM resource IDs?

**Options:**

A) **JIM generates IDs** - Return JIM's CSO ID as SCIM `id`
   - Pros: Consistent, JIM controls IDs
   - Cons: Client's `externalId` becomes secondary

B) **Client provides IDs** - Use client's `externalId` as primary
   - Pros: Client maintains control
   - Cons: May conflict across systems

C) **Hybrid** - Accept client `id` if provided, generate if not
   - Pros: Flexible
   - Cons: Complex

**Recommendation**: Option A - JIM generates SCIM `id` (maps to CSO external ID), client's `externalId` stored as secondary attribute.

---

### Q2: How do we handle SCIM filtering and pagination?

SCIM GET endpoints support filtering:
```
GET /scim/v2/{systemId}/Users?filter=userName eq "jsmith"
```

**Options:**

A) **Full SCIM filter support** - Parse and translate to JIM queries
   - Pros: Full spec compliance
   - Cons: Complex implementation

B) **Limited filter support** - Support common filters only
   - Pros: Simpler, covers 90% of use cases
   - Cons: Not fully compliant

C) **No filtering** - Return all resources
   - Pros: Simplest
   - Cons: Won't work for large directories

**Recommendation**: Option B for MVP - support `userName eq`, `externalId eq`, and `id eq` filters.

---

### Q3: Should SCIM responses include MVO data or CSO data?

When returning a SCIM User, what's the source?

**Options:**

A) **Return CSO data** - What was pushed to this system
   - Pros: Simple, idempotent
   - Cons: Doesn't show merged data from other sources

B) **Return MVO data** - Merged identity from all sources
   - Pros: Shows complete identity
   - Cons: May confuse client ("I sent X, why does it say Y?")

C) **Configurable** - System setting to choose behavior
   - Pros: Flexible
   - Cons: More complex

**Recommendation**: Option A - Return CSO data. SCIM clients expect to see what they pushed.

---

### Q4: How do we handle SCIM Groups with members?

SCIM Groups include member references. When a group is pushed:
- Members may not exist yet in JIM
- Members may reference users from other systems

**Options:**

A) **Strict** - Reject if any member not found
B) **Lenient** - Accept group, ignore unknown members
C) **Deferred** - Store references, resolve during sync

**Recommendation**: Option C - Store member references as-is, resolve during attribute flow to MVO.

---

### Q5: How does SCIM handle inter-object dependencies?

SCIM is fundamentally **single-object-at-a-time** for mutations. The spec states:

> "The SCIM protocol does not define any ordering guarantees for bulk operations"
> — RFC 7644, Section 3.7

**Who handles ordering?**

| Scenario | Responsibility | How It Works |
|----------|---------------|--------------|
| **Single-object requests** | SCIM Client | Client creates dependencies first (e.g., Bob before Alice with manager=Bob) |
| **Bulk requests** | SCIM Server (JIM) | Server can topologically sort using `bulkId` references |

**For single-object requests (MVP):**

```csharp
[HttpPost("Users")]
public async Task<IActionResult> CreateUser(Guid systemId, ScimUser user)
{
    if (user.Manager?.Value != null)
    {
        // Look up referenced CSO in THIS connected system
        var managerCso = await _repository.ConnectedSystemObjects
            .GetByExternalIdAsync(systemId, user.Manager.Value);

        if (managerCso == null)
        {
            // SCIM spec: Return 400 Bad Request
            return BadRequest(new ScimError
            {
                Status = 400,
                ScimType = "invalidValue",
                Detail = $"Manager '{user.Manager.Value}' not found"
            });
        }
    }
    // Continue processing...
}
```

This is correct per SCIM spec - clients are expected to create dependencies first.

---

### Q6: Do we need SCIM Bulk operations?

**Standard SCIM (most IdPs):**
- Single-object operations (POST, PATCH, DELETE one at a time)
- Client handles ordering
- Optional Bulk endpoint (rarely used by clients)

**Entra ID Inbound Provisioning API:**
- Bulk-first approach with SCIM payloads
- Uses `bulkId` for intra-batch references
- Server handles dependency ordering

```json
{
    "schemas": ["urn:ietf:params:scim:api:messages:2.0:BulkRequest"],
    "Operations": [
        {
            "method": "POST",
            "path": "/Users",
            "bulkId": "user-bob",
            "data": { "userName": "bob", ... }
        },
        {
            "method": "POST",
            "path": "/Users",
            "bulkId": "user-alice",
            "data": {
                "userName": "alice",
                "manager": { "value": "bulkId:user-bob" }
            }
        }
    ]
}
```

**Recommendation:**

| Phase | Scope | Rationale |
|-------|-------|-----------|
| **MVP** | Single-object only | Most IdPs (Okta, OneLogin, Ping) use single-object |
| **Post-MVP** | Add Bulk endpoint | Required for Entra ID API-driven provisioning |

**Bulk implementation approach (post-MVP):**

```csharp
[HttpPost("Bulk")]
public async Task<IActionResult> BulkOperation(Guid systemId, ScimBulkRequest request)
{
    // 1. Parse all operations
    var operations = request.Operations;

    // 2. Build dependency graph from bulkId references
    var graph = BuildBulkIdDependencyGraph(operations);

    // 3. Topological sort - dependencies first
    var sorted = TopologicalSort(graph);

    // 4. Execute in order, tracking bulkId -> SCIM ID
    var bulkIdToScimId = new Dictionary<string, string>();
    var results = new List<ScimBulkOperationResult>();

    foreach (var op in sorted)
    {
        ReplaceBulkIdReferences(op, bulkIdToScimId);
        var result = await ExecuteOperationAsync(systemId, op);

        if (op.BulkId != null)
            bulkIdToScimId[op.BulkId] = result.ScimId;

        results.Add(result);
    }

    return Ok(new ScimBulkResponse { Operations = results });
}
```

---

## Implementation Approach

### Phase 1: Core Infrastructure
1. Create `ScimServerConnector` class
2. Add SCIM schema definition (User, Group)
3. Implement basic authentication (Bearer token)
4. Create `ScimController` with discovery endpoints

### Phase 2: User Operations
5. Implement POST /Users (create)
6. Implement GET /Users/{id} (read)
7. Implement PATCH /Users/{id} (update)
8. Implement DELETE /Users/{id} (delete)
9. Implement GET /Users (list with basic filtering)

### Phase 3: Group Operations
10. Implement POST /Groups
11. Implement GET /Groups/{id}
12. Implement PATCH /Groups/{id} (including member updates)
13. Implement DELETE /Groups/{id}
14. Implement GET /Groups

### Phase 4: Polish
15. Enhanced filtering support
16. Pagination
17. Bulk operations (optional)
18. OAuth 2.0 authentication (optional)
19. IP allowlist enforcement

---

## Edge Cases & Challenges

### 1. Concurrent Updates

Multiple SCIM requests for the same user arrive simultaneously.

**Solution**: Use optimistic concurrency on CSO. Return SCIM 409 Conflict if version mismatch.

### 2. Reference Resolution

SCIM manager reference points to user that doesn't exist yet.

**Solution**: Store raw reference value. Resolve to MVO reference during attribute flow (deferred resolution).

### 3. Schema Extensions

Client sends custom SCIM schema extensions.

**Solution**: Store in CSO as custom attributes. Admin can map via sync rules.

### 4. Large Payloads

Bulk operations or groups with thousands of members.

**Solution**: Streaming processing, database batching, configurable limits.

### 5. Rate Limiting

Client floods JIM with requests.

**Solution**: Per-system rate limiting configuration. Return 429 Too Many Requests.

### 6. Partial Failures

PATCH operation has 5 operations, 2 fail.

**Solution**: SCIM spec allows partial success. Return 200 with error details per RFC 7644.

---

## SCIM Compliance Checklist

### Required (MVP)
- [ ] ServiceProviderConfig endpoint
- [ ] Schemas endpoint
- [ ] ResourceTypes endpoint
- [ ] User CRUD operations
- [ ] Group CRUD operations
- [ ] Bearer token authentication
- [ ] SCIM error responses (RFC 7644 Section 3.12)
- [ ] ETag support for concurrency

### Optional (Post-MVP)
- [ ] SCIM filtering (full grammar)
- [ ] Pagination with cursors
- [ ] Bulk operations
- [ ] OAuth 2.0 authentication
- [ ] Password management
- [ ] Schema extensions

---

## Testing Strategy

### Unit Tests
- SCIM JSON parsing/serialization
- Attribute mapping logic
- Authentication validation

### Integration Tests
- Full request flow: SCIM request -> CSO -> MVO -> Pending Export
- Sync rule application
- Error handling

### Compliance Tests
- Use existing SCIM compliance test suites
- Test with real IdPs (Okta, Azure AD) in sandbox

---

## References

- [RFC 7643 - SCIM Core Schema](https://tools.ietf.org/html/rfc7643)
- [RFC 7644 - SCIM Protocol](https://tools.ietf.org/html/rfc7644)
- [SCIM 2.0 Tutorial](http://www.simplecloud.info/)
- Issue #123: Event-Based Synchronisation Support
- Issue #121: Outbound Sync
- [OUTBOUND_SYNC_DESIGN.md](OUTBOUND_SYNC_DESIGN.md) - Event-Based Sync Roadmap
