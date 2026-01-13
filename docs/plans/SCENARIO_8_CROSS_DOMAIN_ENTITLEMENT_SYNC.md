# Scenario 8: Cross-domain Entitlement Synchronisation

| | |
|---|---|
| **Status** | **In Progress** |
| **Milestone** | MVP |
| **Related Issue** | [#173](https://github.com/TetronIO/JIM/issues/173) |
| **Dependencies** | Source/Target AD containers (samba-ad-source, samba-ad-target) |

---

## Implementation Progress

### Completed ✅

- **Phase 1: Test Data Infrastructure** - Complete
  - `Populate-SambaAD-Scenario8.ps1` - Populates users and groups in Source AD
  - `Setup-Scenario8.ps1` - Configures JIM with Connected Systems, sync rules, and attribute mappings
  - `Invoke-Scenario8-CrossDomainEntitlementSync.ps1` - Test scenario runner
  - `Test-GroupHelpers.ps1` - Group helper functions for test data generation
  - OU structure creation for both Source (Corp) and Target (CorpManaged)
  - Support for Nano through XXLarge templates

- **Phase 2: JIM Configuration** - Complete
  - Source LDAP Connected System (Quantum Dynamics APAC)
  - Target LDAP Connected System (Quantum Dynamics EMEA)
  - User and Group object type selection with required attributes
  - Import/Export sync rules for both users and groups
  - Attribute flow mappings including DN expressions
  - Matching rules (sAMAccountName → Account Name)
  - Run profiles (Full Import, Full Sync, Export)

- **Phase 3: ImportToMV Step** - Complete
  - Successfully imports users and groups from Source AD
  - Projects objects to Metaverse
  - Users and Groups visible in Metaverse with correct attributes

- **Phase 4: InitialSync Step** - Complete
  - Fixed reference attribute resolution (member attribute now resolves to DN instead of MVO GUID)
  - Exports with reference attributes are now properly deferred until referenced objects exist
  - Users and groups successfully provisioned to Target AD
  - Confirming import working - pending exports confirmed and cleared
  - **Bugs Fixed**:
    - `ExportExecutionServer`: Resolve references to secondary external ID (DN) for LDAP systems
    - `PendingExportReconciliationService`: Process `ExportNotImported` status and `ExportedNotConfirmed` attribute changes during confirming import
  - Related: Created issue #287 for pending export visibility improvements

### In Progress 🔄

- **Remaining Test Steps**
  - ForwardSync (membership changes)
  - DetectDrift (drift detection)
  - ReassertState (state reassertion)
  - NewGroup (new group provisioning)
  - DeleteGroup (group deletion)

---

## Overview

Scenario 8 validates synchronising entitlement groups (security groups, distribution groups, mail-enabled groups) between two Active Directory domains, where one domain is authoritative for groups. Unlike Scenarios 6-7 which require Internal MVOs, this scenario syncs groups between external systems through the metaverse.

### Business Value

- **Multi-domain enterprises** often need consistent group structures across domains
- **Merger/acquisition scenarios** require group replication between forests
- **Disaster recovery** benefits from group synchronisation to secondary domains
- **Compliance** requirements may mandate group consistency across environments

---

## Architecture

### Data Flow

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                        Scenario 8: Group Sync Flow                           │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  Quantum Dynamics APAC (Source)           Quantum Dynamics EMEA (Target)     │
│  ┌──────────────────────────────┐          ┌──────────────────────────────┐  │
│  │ OU=Entitlements,OU=Corp│          │ OU=Entitlements,OU=CorpManaged│  │
│  │                              │          │                              │  │
│  │  ┌─────────────────────┐     │          │  ┌─────────────────────┐     │  │
│  │  │ Company-Subatomic   │     │          │  │ Company-Subatomic   │     │  │
│  │  │ Members:            │     │          │  │ Members:            │     │  │
│  │  │  - CN=John,OU=...   │─────┼──────────┼─▶│  - CN=John,OU=...   │     │  │
│  │  │  - CN=Jane,OU=...   │     │          │  │  - CN=Jane,OU=...   │     │  │
│  │  └─────────────────────┘     │          │  └─────────────────────┘     │  │
│  │                              │          │                              │  │
│  │  ┌─────────────────────┐     │          │  ┌─────────────────────┐     │  │
│  │  │ Dept-Engineering    │     │          │  │ Dept-Engineering    │     │  │
│  │  │ (Universal Security)│─────┼──────────┼─▶│ (Universal Security)│     │  │
│  │  └─────────────────────┘     │          │  └─────────────────────┘     │  │
│  │                              │          │                              │  │
│  └──────────────────────────────┘          └──────────────────────────────┘  │
│               │                                        ▲                     │
│               │ Import                                 │ Export              │
│               ▼                                        │                     │
│  ┌────────────────────────────────────────────────────────────────────┐      │
│  │                         JIM Metaverse                              │      │
│  │  ┌─────────────────────────────────────────────────────────────┐   │      │
│  │  │ Group MVO: Company-Subatomic                                │   │      │
│  │  │   Account Name: Company-Subatomic                           │   │      │
│  │  │   Group Type: Universal Security                            │   │      │
│  │  │   Members: [ref:User-John-MVO, ref:User-Jane-MVO]           │   │      │
│  │  └─────────────────────────────────────────────────────────────┘   │      │
│  └────────────────────────────────────────────────────────────────────┘      │
│                                                                              │
└──────────────────────────────────────────────────────────────────────────────┘
```

### Reference Resolution

The member attribute contains DN references that must be transformed between domains:

```
Source AD member:  CN=John Smith,OU=Users,OU=Corp,DC=sourcedomain,DC=local
                              ↓
                    Import (CSO reference)
                              ↓
Metaverse member:  Reference to User MVO (John Smith)
                              ↓
                    Export (DN calculation)
                              ↓
Target AD member:  CN=John Smith,OU=Users,OU=CorpManaged,DC=targetdomain,DC=local
```

**Prerequisites**: Users must be synced between domains first so that:
1. User MVOs exist in the metaverse
2. User CSOs exist in the target system
3. Export can calculate correct target DNs from MVO→CSO mappings

> **Note**: Scenario 8 is self-contained and will populate and sync its own users as part of the test setup, before syncing groups.

---

## Systems

| System | Role | Container | Domain |
|--------|------|-----------|--------|
| Quantum Dynamics APAC | Source (Authoritative) | `samba-ad-source` | `DC=sourcedomain,DC=local` |
| Quantum Dynamics EMEA | Target (Replica) | `samba-ad-target` | `DC=targetdomain,DC=local` |

### Container Structure

**Source AD:**
```
DC=sourcedomain,DC=local
└── OU=Corp
    ├── OU=Users
    └── OU=Entitlements
```

**Target AD:**
```
DC=targetdomain,DC=local
└── OU=CorpManaged
    ├── OU=Users
    └── OU=Entitlements
```

---

## AD Group Attributes

### Core Attributes (Required)

| Attribute | Type | Description | Sync Direction |
|-----------|------|-------------|----------------|
| `objectGUID` | GUID | Immutable identifier (External ID/Anchor) | Import only |
| `sAMAccountName` | Text | Pre-Windows 2000 name (matching attribute) | Bidirectional |
| `cn` | Text | Common Name (RDN component) | Bidirectional |
| `distinguishedName` | Text | Full DN path (Secondary External ID) | Export: Expression |
| `displayName` | Text | Human-readable group name | Bidirectional |
| `description` | Text | Group description | Bidirectional |
| `groupType` | Integer | Type and scope flags (see below) | Bidirectional |
| `member` | Multi-valued DN | Group membership references | Bidirectional |
| `managedBy` | DN | Group owner/manager reference | Bidirectional |

### Mail-Enabled Attributes (For Mail-Enabled Groups)

| Attribute | Type | Description | Sync Direction |
|-----------|------|-------------|----------------|
| `mail` | Text | Primary email address | Bidirectional |
| `mailNickname` | Text | Exchange alias (required for mail-enabled) | Bidirectional |
| `proxyAddresses` | Multi-valued Text | SMTP addresses (primary + aliases) | Bidirectional |

> **Samba AD Note**: Samba AD supports `mail` attribute natively. The `mailNickname` and `proxyAddresses` attributes may require schema extension or may be available depending on Samba version. The population script will test attribute availability and skip unsupported attributes gracefully.

### groupType Flag Values

The `groupType` attribute is a bitmask combining type and scope:

| Flag | Hex | Decimal | Description |
|------|-----|---------|-------------|
| Global Scope | 0x00000002 | 2 | Members from same domain only |
| Domain Local Scope | 0x00000004 | 4 | Can include members from any domain |
| Universal Scope | 0x00000008 | 8 | Can include members from any domain in forest |
| Security Enabled | 0x80000000 | -2147483648 | Security group (can assign permissions) |

**Common Combinations:**

| Group Type | Decimal Value | Hex Value |
|------------|---------------|-----------|
| Global Security | -2147483646 | 0x80000002 |
| Domain Local Security | -2147483644 | 0x80000004 |
| Universal Security | -2147483640 | 0x80000008 |
| Global Distribution | 2 | 0x00000002 |
| Domain Local Distribution | 4 | 0x00000004 |
| Universal Distribution | 8 | 0x00000008 |

---

## Test Data Model

### Group Naming Convention

Groups follow a hierarchical naming model for realistic enterprise representation:

| Category | Pattern | Example | Purpose |
|----------|---------|---------|---------|
| Company | `Company-{Name}` | `Company-Subatomic` | Top-level organisational groups |
| Department | `Dept-{Name}` | `Dept-Engineering` | Functional team groups |
| Location | `Location-{City}` | `Location-Sydney` | Geographic groups |
| Project | `Project-{Name}` | `Project-Phoenix` | Dynamic project teams (scalable) |

### Template Scaling

| Template | Companies | Departments | Locations | Projects | Total Groups | Users |
|----------|-----------|-------------|-----------|----------|--------------|-------|
| Nano | 1 | 2 | 1 | 2 | 6 | 3 |
| Micro | 2 | 3 | 2 | 5 | 12 | 10 |
| Small | 3 | 5 | 3 | 20 | 31 | 100 |
| Medium | 5 | 8 | 5 | 100 | 118 | 1,000 |
| MediumLarge | 5 | 10 | 8 | 250 | 273 | 5,000 |
| Large | 8 | 12 | 10 | 500 | 530 | 10,000 |
| XLarge | 10 | 15 | 15 | 2,000 | 2,040 | 100,000 |
| XXLarge | 15 | 20 | 20 | 10,000 | 10,055 | 1,000,000 |

### Group Type and Mail Enablement Distribution

To test all group types and mail enablement states, the population script creates a realistic enterprise mix. In real environments:
- Security groups are more common than distribution groups
- Universal scope is required for mail-enabled security groups
- Mail-enabled groups require `mail` and `mailNickname` attributes
- Not all security groups need mail enablement

#### Group Type Matrix

| Scope | Security (Not Mail-Enabled) | Security (Mail-Enabled) | Distribution |
|-------|----------------------------|-------------------------|--------------|
| Universal | ✓ Common | ✓ Common | ✓ Common |
| Global | ✓ Very Common | ✗ Not Supported | ✓ Rare |
| Domain Local | ✓ Common | ✗ Not Supported | ✓ Rare |

> **Note**: Only Universal security groups can be mail-enabled in Exchange. Global and Domain Local security groups must be converted to Universal scope before mail-enabling.

#### Distribution by Category

Different group categories have different typical configurations:

| Category | Typical Type | Typical Scope | Mail-Enabled | Rationale |
|----------|--------------|---------------|--------------|-----------|
| Company | Security | Universal | Yes | Company-wide access + announcements |
| Department | Security | Universal | Yes | Team access + team emails |
| Location | Security | Universal | Sometimes | Building access + local announcements |
| Project | Security | Universal | Sometimes | Project resources + collaboration |

#### Detailed Distribution (Percentage of Total Groups)

| Group Type | Scope | Mail-Enabled | Percentage | Example Use Case |
|------------|-------|--------------|------------|------------------|
| Security | Universal | Yes | 30% | `Dept-Engineering` - Team access + email |
| Security | Universal | No | 25% | `App-CRM-Users` - Application access only |
| Security | Global | No | 15% | `Server-Admins` - Single-domain resource access |
| Security | Domain Local | No | 5% | `FS-Share-Read` - File share permissions |
| Distribution | Universal | Yes | 20% | `DL-AllStaff` - Announcement lists |
| Distribution | Global | Yes | 5% | `DL-Marketing-News` - Legacy distribution |

#### Mail-Enabled Group Attributes

When a group is mail-enabled, these additional attributes are populated:

| Attribute | Required | Example Value |
|-----------|----------|---------------|
| `mail` | Yes | `dept-engineering@sourcedomain.local` |
| `mailNickname` | Yes | `Dept-Engineering` |
| `proxyAddresses` | Optional | `SMTP:dept-engineering@sourcedomain.local` |

#### Naming Convention for Mail-Enabled Groups

Mail-enabled groups follow a pattern that indicates their email address:

| Category | Group Name | Mail Address |
|----------|------------|--------------|
| Company | `Company-Subatomic` | `company-subatomic@domain.local` |
| Department | `Dept-Engineering` | `dept-engineering@domain.local` |
| Location | `Location-Sydney` | `location-sydney@domain.local` |
| Project | `Project-Phoenix` | `project-phoenix@domain.local` |

#### Group Ownership (managedBy) Distribution

The `managedBy` attribute is a single-valued DN reference to the user who manages the group. In enterprise environments, not all groups have explicit owners - many are managed by IT/system accounts or have no owner set.

**Distribution by Category:**

| Category | Has managedBy | Rationale |
|----------|---------------|-----------|
| Company | 100% | Always owned by executive or HR lead |
| Department | 100% | Owned by department head/manager |
| Location | 80% | Usually owned by site manager |
| Project | 60% | Project groups often have a project lead, but some are ad-hoc |

**Overall Distribution**: ~70% of groups will have `managedBy` set

**Owner Assignment Logic:**
- Company groups: Assigned to a random user with "Director" or "Manager" title
- Department groups: Assigned to a user in the same department with "Manager" title
- Location groups: Assigned to a random user at that location with seniority
- Project groups: Assigned to a random project member (simulating project lead)

This tests the single-valued DN reference attribute sync, which uses the same reference resolution mechanism as the multi-valued `member` attribute but with different cardinality.

### Reference Data

**Company Names:**
- Subatomic, NexusDynamics, OrbitalSystems, QuantumBridge, StellarLogistics
- VortexTech, CatalystCorp, HorizonIndustries, PulsarEnterprises, NovaNetworks

**Department Names:**
- Engineering, Finance, Human Resources, Information Technology, Legal
- Marketing, Operations, Procurement, Research & Development, Sales
- Customer Support, Quality Assurance

**Location Names:**
- Sydney, Melbourne, London, Manchester, New York, San Francisco
- Tokyo, Singapore, Berlin, Paris, Toronto, Dubai

**Project Name Generator:**
- Combines adjectives + nouns for unlimited variety
- Examples: Phoenix, Titan, Mercury, Apollo, Voyager, Nebula, Quantum, Fusion
- Adjectives: Agile, Digital, Global, Smart, Cloud, Next, Ultra, Hyper
- Nouns: Platform, Gateway, Engine, Hub, Core, Bridge, Matrix, Vector

---

## Test Steps

### Step 1: InitialSync

**Purpose**: Validate initial import of groups from Source AD and provisioning to Target AD.

**Preconditions**:
- Users populated in Source AD and synced to Target AD (done by Scenario 8 setup)
- Groups populated in Source AD with membership
- JIM configured with user and group sync rules

**Actions**:
1. Trigger Full Import on Source AD
2. Trigger Full Sync
3. Trigger Export on Target AD
4. Trigger Confirming Import on Target AD

**Validations**:
- [ ] All groups from Source `OU=Entitlements` exist in Target `OU=Entitlements`
- [ ] Group attributes match (displayName, description, groupType)
- [ ] Member DNs correctly transformed to target domain
- [ ] Member count matches between source and target groups
- [ ] All group types synced correctly (security, distribution, various scopes)

### Step 2: ForwardSync

**Purpose**: Validate that membership changes in Source AD flow to Target AD.

**Actions**:
1. Add 2 users to an existing group in Source AD
2. Remove 1 user from a different group in Source AD
3. Trigger Full Import on Source AD
4. Trigger Full Sync
5. Trigger Export on Target AD

**Validations**:
- [ ] Added members appear in Target AD group
- [ ] Removed member no longer in Target AD group
- [ ] Other group memberships unchanged
- [ ] Activity shows expected changes

### Step 3: DetectDrift

**Purpose**: Validate that JIM detects unauthorised changes made directly in Target AD.

**Actions**:
1. Directly add a user to a group in Target AD (bypassing JIM)
2. Directly remove a user from a different group in Target AD
3. Trigger Full Import on Target AD
4. Check CSO status and drift detection

**Validations**:
- [ ] JIM detects the added member as drift
- [ ] JIM detects the removed member as drift
- [ ] CSO attribute values show discrepancy from expected state

### Step 4: ReassertState

**Purpose**: Validate that JIM reasserts Source AD membership to Target AD.

**Actions**:
1. Trigger Full Sync (after DetectDrift step)
2. Trigger Export on Target AD
3. Trigger Confirming Import on Target AD

**Validations**:
- [ ] Unauthorised member additions removed from Target AD
- [ ] Unauthorised member removals restored in Target AD
- [ ] Target AD groups match Source AD groups exactly
- [ ] Activity shows corrective exports

### Step 5: NewGroup

**Purpose**: Validate that new groups created in Source AD are provisioned to Target AD.

**Actions**:
1. Create new group `Project-Scenario8Test` in Source AD with members
2. Trigger Full Import on Source AD
3. Trigger Full Sync
4. Trigger Export on Target AD

**Validations**:
- [ ] New group exists in Target AD
- [ ] Group attributes correct (displayName, description, groupType)
- [ ] Members correctly synced with target domain DNs
- [ ] Group in correct OU (`OU=Entitlements,OU=CorpManaged`)

### Step 6: DeleteGroup

**Purpose**: Validate that groups deleted from Source AD are deleted from Target AD.

**Actions**:
1. Delete a group from Source AD
2. Trigger Full Import on Source AD
3. Trigger Full Sync
4. Wait for deletion rules (if grace period configured)
5. Trigger Export on Target AD

**Validations**:
- [ ] Group marked for deletion in JIM (if grace period)
- [ ] Group deleted from Target AD (after grace period or immediately)
- [ ] Members not affected (users still exist)
- [ ] Activity shows deletion

---

## Implementation

### Phase 1: Test Data Infrastructure

#### 1.1 Group Helper Functions

**File**: `test/integration/utils/Test-GroupHelpers.ps1`

```powershell
function Get-GroupTypeFlags {
    param(
        [ValidateSet("Security", "Distribution")]
        [string]$Type = "Security",

        [ValidateSet("Global", "DomainLocal", "Universal")]
        [string]$Scope = "Universal"
    )
    # Returns integer groupType value
}

function New-TestGroup {
    param(
        [string]$Category,      # Company, Department, Location, Project
        [string]$Name,
        [string]$Type,          # Security, Distribution
        [string]$Scope,         # Global, DomainLocal, Universal
        [string]$Description,
        [string]$Mail,          # Optional
        [string]$MailNickname   # Optional
    )
    # Returns group object with all properties
}

function Get-ProjectNames {
    param([int]$Count)
    # Generates unique project names using adjective + noun combinations
}

function Get-Scenario8GroupScale {
    param([string]$Template)
    # Returns group counts for each category based on template
}
```

#### 1.2 Group Population Script

**File**: `test/integration/Populate-SambaAD-Scenario8.ps1`

- Reuses `Test-Helpers.ps1` for template scales
- Creates `OU=SourceCorp` and `OU=Entitlements` structure
- Generates groups based on template scale
- Assigns members from users created in the same population run
- Supports `-Instance Source` parameter (groups only created in source)

### Phase 2: JIM Configuration

#### 2.1 Setup Script

**File**: `test/integration/Setup-Scenario8.ps1`

**Steps**:
1. Import JIM PowerShell module
2. Connect to JIM API
3. Create/verify Source and Target Connected Systems
4. Import schema if not already done
5. Select "group" object type for both systems
6. Select required group attributes (see attribute list above)
7. Set `objectGUID` as External ID for groups
8. Create Import Sync Rule: "APAC AD Import Groups"
9. Create Export Sync Rule: "EMEA AD Export Groups"
10. Configure attribute flow mappings
11. Configure DN expression for target placement
12. Create/update Run Profiles to include group object type

#### 2.2 Attribute Flow Mappings

**Import (Source → Metaverse):**

| Source Attribute | Metaverse Attribute | Flow Type |
|------------------|---------------------|-----------|
| sAMAccountName | Account Name | Direct |
| displayName | Display Name | Direct |
| cn | Common Name | Direct |
| description | Description | Direct |
| groupType | Group Type | Direct |
| member | Members | Direct (Reference) |
| managedBy | Managed By | Direct (Reference) |
| mail | Email | Direct |
| mailNickname | Mail Nickname | Direct |

**Export (Metaverse → Target):**

| Metaverse Attribute | Target Attribute | Flow Type |
|---------------------|------------------|-----------|
| Account Name | sAMAccountName | Direct |
| Display Name | displayName | Direct |
| Display Name | cn | Direct |
| Description | description | Direct |
| Group Type | groupType | Direct |
| Members | member | Direct (Reference) |
| Managed By | managedBy | Direct (Reference) |
| Email | mail | Direct |
| Mail Nickname | mailNickname | Direct |
| (Expression) | distinguishedName | Expression |

**DN Expression:**
```
"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Entitlements,OU=CorpManaged,DC=targetdomain,DC=local"
```

### Phase 3: Test Script

**File**: `test/integration/scenarios/Invoke-Scenario8-CrossDomainEntitlementSync.ps1`

**Parameters**:
- `-Template` - Data scale (Nano, Micro, Small, etc.)
- `-Step` - Test step (InitialSync, ForwardSync, DetectDrift, ReassertState, NewGroup, DeleteGroup, All)
- `-ApiKey` - JIM API key
- `-WaitSeconds` - Override default wait time between operations
- `-TriggerRunProfile` - Automatically trigger sync operations

---

## Metaverse Schema Requirements

### Group Object Type

Verify the `Group` metaverse object type exists with these attributes:

| Attribute | Type | Required | Notes |
|-----------|------|----------|-------|
| Account Name | Text | Yes | sAMAccountName mapping |
| Display Name | Text | Yes | displayName mapping |
| Common Name | Text | No | cn mapping |
| Description | Text | No | description mapping |
| Group Type | Number | Yes | groupType flags |
| Members | Reference (Multi) | Yes | member DN references |
| Managed By | Reference | No | managedBy DN reference (single-valued) |
| Email | Text | No | mail mapping |
| Mail Nickname | Text | No | mailNickname mapping |

If attributes are missing, the setup script should create them.

---

## Dependencies

### Required Before Scenario 8

1. **AD Containers** - Source and Target AD containers running (`samba-ad-source`, `samba-ad-target`)
2. **Group Object Type** - Metaverse must have Group object type with required attributes

### Self-Contained Setup

Scenario 8 is fully self-contained and will:
1. Create OUs in both Source and Target AD (`OU=Corp`, `OU=CorpManaged`)
2. Populate users in Source AD
3. Configure JIM with Connected Systems, sync rules for both users and groups
4. Sync users between domains (prerequisite for group member resolution)
5. Then sync groups with membership

### Shared Components (DRY Principle)

| Component | Shared With | Location |
|-----------|-------------|----------|
| Template scale definitions | All scenarios | `Test-Helpers.ps1` |
| Name data (first/last names) | Scenario 1 | `test-data/*.csv` |
| AD container configuration | All AD scenarios | `docker-compose.yml` |
| LDAP helper functions | All AD scenarios | `Test-Helpers.ps1` |

---

## Success Criteria

### Functional

- [ ] All 6 test steps pass with Nano template
- [ ] All test steps pass with Small template
- [ ] All group types sync correctly (Universal/Global Security/Distribution)
- [ ] Member references resolve correctly across domains
- [ ] Drift detection and reassertion work correctly
- [ ] Group creation and deletion sync correctly

### Performance

| Template | Expected Duration | Max Duration |
|----------|-------------------|--------------|
| Nano | < 30 seconds | 1 minute |
| Small | < 2 minutes | 5 minutes |
| Medium | < 5 minutes | 15 minutes |

### Quality

- [ ] All new code has unit tests where applicable
- [ ] PowerShell scripts follow existing patterns
- [ ] Documentation updated in INTEGRATION_TESTING.md
- [ ] No regressions in existing scenarios

---

## Risks and Mitigations

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| Member reference resolution fails | High | Medium | Debug worker reference translation; add logging |
| Samba AD doesn't support all group attributes | Medium | Low | Test attribute availability; gracefully handle missing attributes |
| Large member lists cause performance issues | Medium | Medium | Test with progressively larger templates; optimise if needed |
| Group type flags not preserved correctly | Medium | Low | Explicit testing of all group type combinations |

---

## Open Questions

1. **Multi-valued attribute handling**: Does JIM correctly handle the multi-valued `member` attribute for large groups (1000+ members)?

2. **Circular references**: If Group A contains Group B as a member, and we sync both, does the order of operations matter? (Likely handled by reference resolution in worker)

3. **Mail attributes in Samba AD**: Initial testing needed to determine which mail attributes (`mail`, `mailNickname`, `proxyAddresses`) are supported. The population script will gracefully skip unsupported attributes.

4. **Nested groups**: Should we test nested group membership (groups containing other groups as members)? This is common in enterprise AD for role hierarchies.

5. **Domain Local groups**: Domain Local groups cannot have members from other domains. Should we skip syncing Domain Local groups, or convert them to Universal scope in the target?

---

## Decisions Made

1. **Self-contained**: Scenario 8 is fully self-contained. It populates its own users and groups, configures JIM, syncs users first, then syncs groups. Reuses shared helper functions (DRY principle).

2. **Reference translation**: The worker handles reference translation via the metaverse - no additional code changes expected (debugging may be required).

3. **Deletion behaviour**: Follows deletion rules configuration - same as other object types.

4. **Group type distribution**: Includes realistic mix of security/distribution groups with various scopes and mail-enablement states (see detailed distribution table above).

---

## Future Enhancements

- **Bidirectional sync**: Allow changes in either domain to flow to the other (requires conflict resolution)
- **Selective sync**: Filter which groups sync based on naming pattern or OU
- **Exchange attributes**: Full mail-enabled group support with `msExch*` attributes (msExchRecipientDisplayType, msExchRecipientTypeDetails)
- **Co-managers**: Support for `msExchCoManagedByLink` attribute for multiple group managers (requires Exchange schema)

---

## References

- [Active Directory Group Types and Scopes](https://theitbros.com/active-directory-groups/) - Comprehensive guide to AD group types
- [Manage mail-enabled security groups in Exchange Online](https://learn.microsoft.com/en-us/exchange/recipients-in-exchange-online/manage-mail-enabled-security-groups) - Microsoft documentation
- [msExchRecipientDisplayType and msExchRecipientTypeDetails Values](https://www.mistercloudtech.com/2016/05/18/reference-to-msexchrecipientdisplaytype-msexchrecipienttypedetails-and-msexchremoterecipienttype-values/) - Exchange attribute reference
- [INTEGRATION_TESTING.md](../INTEGRATION_TESTING.md) - Integration testing framework documentation
- [Scenario 2: Cross-domain Sync](../INTEGRATION_TESTING.md#scenario-2-person-entity---cross-domain-synchronisation) - User sync between domains
