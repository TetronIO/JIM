# Integration Testing Framework

| | |
|---|---|
| **Status** | **In Progress** |
| **Phase 1 Target** | MVP Validation |
| **Phase 2 Target** | Post-MVP (after Database Connector #170) |
| **Related Issue** | [#173](https://github.com/TetronIO/JIM/issues/173) |

---

## Table of Contents

1. [Test Lifecycle Quick Reference](#test-lifecycle-quick-reference)
2. [Overview](#overview)
3. [Architecture](#architecture)
4. [Data Scale Templates](#data-scale-templates)
5. [Test Scenarios](#test-scenarios)
6. [Setup & Configuration](#setup--configuration)
7. [Running Tests Locally](#running-tests-locally)
8. [CI/CD Integration](#cicd-integration)
9. [Writing New Scenarios](#writing-new-scenarios)
10. [Troubleshooting](#troubleshooting)

---

## Test Lifecycle Quick Reference

Integration tests require a complete environment reset between runs to ensure repeatable, idempotent results. This includes resetting **both** external systems (Samba AD, databases) **and** JIM itself (metaverse, configuration).

### DevContainer / Local Development

For developers running tests locally in a DevContainer or development environment:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         LOCAL DEVELOPMENT LIFECYCLE                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  1. STAND UP                     2. POPULATE                                │
│  ┌─────────────────────────┐     ┌─────────────────────────┐                │
│  │ # Start external systems│     │ # Populate test data    │                │
│  │ docker compose -f       │     │ ./Populate-SambaAD.ps1  │                │
│  │   docker-compose.       │ ──▶ │   -Template Small       │                │
│  │   integration-tests.yml │     │ ./Generate-TestCSV.ps1  │                │
│  │   up -d                 │     │   -Template Small       │                │
│  └─────────────────────────┘     └─────────────────────────┘                │
│                                            │                                │
│                                            ▼                                │
│  3. CONFIGURE JIM                4. EXECUTE TESTS                           │
│  ┌─────────────────────────┐     ┌─────────────────────────┐                │
│  │ # Configure via API     │     │ # Run scenario steps    │                │
│  │ ./Setup-Scenario1.ps1   │ ──▶ │ ./Invoke-Scenario1...   │                │
│  │   -ApiKey $key          │     │   -Step All             │                │
│  │                         │     │   -Template Small       │                │
│  └─────────────────────────┘     └─────────────────────────┘                │
│                                            │                                │
│                                            ▼                                │
│  5. RESET (for next run)                                                    │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ # Reset BOTH external systems AND JIM database                      │    │
│  │ docker compose -f docker-compose.integration-tests.yml down -v      │    │
│  │ docker compose -f docker-compose.yml down -v  # Reset JIM's DB      │    │
│  │                                                                     │    │
│  │ # Then stand up fresh for next test run                             │    │
│  │ docker compose -f docker-compose.yml up -d    # JIM stack           │    │
│  │ docker compose -f docker-compose.integration-tests.yml up -d        │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

**Key Commands:**

| Step | Command | Purpose |
|------|---------|---------|
| Stand up external systems | `docker compose -f docker-compose.integration-tests.yml up -d` | Start Samba AD, databases |
| Stand up JIM | `jim-stack` or `docker compose up -d` | Start JIM services |
| Populate test data | `./Populate-SambaAD.ps1 -Template Small` | Create users/groups in external systems |
| Configure JIM | `./Setup-Scenario1.ps1` | Create Connected Systems, Sync Rules |
| Run tests | `./Invoke-Scenario1-HRToDirectory.ps1 -Step All` | Execute test scenario |
| Reset external systems | `docker compose -f docker-compose.integration-tests.yml down -v` | Remove external system data |
| Reset JIM | `docker compose -f docker-compose.yml down -v` | Remove JIM database (metaverse, config) |

### CI/CD Pipeline

For automated testing in GitHub Actions:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                            CI/CD PIPELINE LIFECYCLE                          │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌──────────────────────────────────────────────────────────────────────┐   │
│  │ WORKFLOW TRIGGER (Manual via workflow_dispatch)                      │   │
│  │ - Select Template: Micro / Small / Medium / Large / XLarge / XXLarge │   │
│  │ - Select Phase: 1 (MVP) or 2 (Post-MVP)                              │   │
│  └──────────────────────────────────────────────────────────────────────┘   │
│                                     │                                       │
│                                     ▼                                       │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐              │
│  │ 1. STAND UP     │  │ 2. BUILD JIM    │  │ 3. CONFIGURE    │              │
│  │ - JIM stack     │─▶│ - dotnet build  │─▶│ - Setup scripts │              │
│  │ - External sys  │  │ - Wait ready    │  │ - Populate data │              │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘              │
│                                                    │                        │
│                                                    ▼                        │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐              │
│  │ 6. TEAR DOWN    │  │ 5. COLLECT      │  │ 4. EXECUTE      │              │
│  │ (always runs)   │◀─│ - Test results  │◀─│ - Run scenarios │              │
│  │ - down -v ALL   │  │ - Upload artefacts│ │ - Validate      │              │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘              │
│         │                                                                   │
│         ▼                                                                   │
│  ┌──────────────────────────────────────────────────────────────────────┐   │
│  │ CLEAN STATE: Runner is fresh for next workflow run                   │   │
│  │ No persistent volumes = automatic reset                              │   │
│  └──────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

**CI/CD Characteristics:**

| Aspect | Behaviour |
|--------|-----------|
| **Trigger** | Manual only (`workflow_dispatch`) - not on every commit |
| **Isolation** | Fresh GitHub runner = clean state guaranteed |
| **Reset** | `docker compose down -v` in `always()` step ensures cleanup even on failure |
| **Idempotency** | Each run is fully independent; no state persists between runs |
| **Timeout** | 2 hours maximum to prevent runaway costs |

### Why Reset JIM's Database?

Integration tests create real data in JIM:

- **Metaverse Objects** - Identity records from imports
- **Connected System Objects** - Links to external systems
- **Sync Rules** - Attribute flow configurations
- **Run Profiles** - Execution schedules
- **Activity History** - Sync operation logs

Without resetting JIM, subsequent test runs would:
- Fail join rules (objects already exist)
- Have incorrect object counts
- Accumulate stale configuration
- Produce non-deterministic results

**The `-v` flag removes Docker volumes**, which contain:
- JIM's PostgreSQL database (all metaverse data, configuration)
- External system data (Samba AD, SQL Server, etc.)

This guarantees a clean slate for each test run.

---

## Overview

The Integration Testing Framework provides end-to-end validation of JIM's synchronisation capabilities against real connected systems running in Docker containers. This enables:

- **MVP Validation**: Prove JIM works in real-world scenarios
- **Regression Prevention**: Catch breaking changes before production
- **Performance Baselines**: Establish and monitor performance characteristics
- **Connector Validation**: Verify all connectors function correctly
- **Release Confidence**: Automated testing before each release

### Key Principles

- **Realistic Systems**: Test against actual Samba AD, SQL Server, Oracle, etc., not mocks
- **Idempotent**: Complete stand-up/tear-down for repeatable testing
- **Scalable**: Template-based data sets from 3 to 1M objects
- **Phased**: Phase 1 (MVP) uses LDAP/CSV; Phase 2 adds databases
- **Opt-In**: Manual trigger only, not automatic on every commit

### Step-Based Execution Model

Each scenario script supports a `-Step` parameter that controls which test case to execute. This is essential because JIM needs time to complete its import/sync/export cycle between test steps.

**Why step-based?**
1. **JIM Processing Time**: After modifying source data, JIM must import, synchronise, and export before verification
2. **Developer Control**: Run individual steps for debugging or testing specific functionality
3. **CI/CD Automation**: The `-Step All` option runs all steps sequentially with appropriate waits
4. **Realistic Testing**: Simulates real-world ILM operations where changes happen over time

**Common parameters across all scenario scripts**:
- `-Step <StepName>` - Execute a specific test step (or `All` for full sequence)
- `-Template <Size>` - Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)
- `-WaitSeconds <N>` - Override default wait time between steps (default: 60)
- `-TriggerRunProfile` - Automatically trigger JIM Run Profile after data changes

---

## Architecture

### Container Stack

All external systems run as Docker containers defined in `docker-compose.integration-tests.yml`:

```
┌────────────────────────────────────────────────────────────────┐
│                       Integration Test Stack                   │
├────────────────────────────────────────────────────────────────┤
│                                                                │
│  Phase 1 (MVP):                                                │
│  ┌──────────────────┐  ┌──────────────────┐  ┌──────────────┐  │
│  │ Samba AD Primary │  │ Samba AD Source  │  │ Samba AD     │  │
│  │ (Scenarios 1&3)  │  │ (Scenario 2)     │  │ Target       │  │
│  │ Port: 389/636    │  │ Port: 10389/636  │  │ Port: 11389  │  │
│  └──────────────────┘  └──────────────────┘  └──────────────┘  │
│                                                                │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ CSV Files (mounted volume at /connector-files)           │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                │
│  Phase 2 (Post-MVP):                                           │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐          │
│  │ SQL Server   │  │ Oracle XE    │  │ PostgreSQL   │          │
│  │ (HRIS A)     │  │ (HRIS B)     │  │ (Target)     │          │
│  │ Port: 1433   │  │ Port: 1521   │  │ Port: 5433   │          │
│  └──────────────┘  └──────────────┘  └──────────────┘          │
│                                                                │
│  ┌──────────────┐  ┌──────────────┐                            │
│  │ OpenLDAP     │  │ MySQL        │                            │
│  │ Port: 12389  │  │ Port: 3306   │                            │
│  └──────────────┘  └──────────────┘                            │
└────────────────────────────────────────────────────────────────┘
```

### Test Flow

```
1. Stand Up Systems
   └─> docker compose up (selected services based on phase/scenario)

2. Populate Test Data
   └─> PowerShell scripts generate realistic data
       ├─> Populate-SambaAD.ps1 -Template Medium
       ├─> Generate-TestCSV.ps1 -Template Medium
       └─> Populate-SqlServer.ps1 -Template Medium (Phase 2)

3. Configure JIM
   └─> PowerShell module creates Connected Systems, Sync Rules, Run Profiles
       ├─> Connect-JIM -ApiKey $env:JIM_API_KEY
       ├─> New-JIMConnectedSystem (HR CSV, Samba AD)
       ├─> New-JIMSyncRule (attribute flows)
       └─> New-JIMRunProfile (import, sync, export steps)

4. Execute Scenarios
   └─> Run scenario scripts
       ├─> Invoke-Scenario1-HRToDirectory.ps1
       ├─> Invoke-Scenario2-DirectorySync.ps1
       └─> Invoke-Scenario3-GALSYNC.ps1

5. Validate Results
   └─> Assertions check expected outcomes
       ├─> User provisioned correctly?
       ├─> Attributes flowed as configured?
       └─> Performance within thresholds?

6. Tear Down
   └─> docker compose down -v (complete cleanup)
```

---

## Data Scale Templates

Choose the appropriate template based on test goals:

| Template   | Users     | Groups  | Avg Memberships | Total Objects | Use Case                          | Est. Time  |
|------------|-----------|---------|-----------------|---------------|-----------------------------------|------------|
| **Nano**   | 3         | 1       | 1               | 4             | Fast dev iteration, debugging     | < 1 min    |
| **Micro**  | 10        | 3       | 3               | 13            | Quick smoke tests, development    | < 2 min    |
| **Small**  | 100       | 20      | 5               | 120           | Small business, unit tests        | < 5 min    |
| **Medium** | 1,000     | 100     | 8               | 1,100         | Medium enterprise, CI/CD          | < 15 min   |
| **Large**  | 10,000    | 500     | 10              | 10,500        | Large enterprise, baselines       | < 45 min   |
| **XLarge** | 100,000   | 2,000   | 12              | 102,000       | Very large enterprise, stress     | 2-3 hours  |
| **XXLarge**| 1,000,000 | 10,000  | 15              | 1,010,000     | Global enterprise, scale limits   | 8+ hours   |

### Data Characteristics

All templates generate realistic enterprise data following normal distribution patterns:

- **Job Titles**: Hierarchy (many Individual Contributors, few C-level)
- **Departments**: Realistic structure (IT, HR, Sales, Finance, Operations, Marketing)
- **Group Memberships**: Normal distribution (most users 5-15 groups, power users 30+)
- **Attributes**: Valid names, email patterns, phone numbers, addresses
- **Organisational Structure**: Tree hierarchy with realistic spans of control

---

## Test Scenarios

### Phase 1 (MVP) - LDAP & CSV

#### Scenario 1: HR to Enterprise Directory

**Purpose**: Validate the most common ILM use case - provisioning users from HR system to Active Directory.

**Systems**:
- Source: CSV (HR system)
- Target: Samba AD Primary

**Test Steps** (executed sequentially):

| Step | Test Case | Description |
|------|-----------|-------------|
| 1 | **Joiner** | User added to HR CSV → provisioned to AD with correct attributes and group memberships |
| 2 | **Mover** | User department/title changed in CSV → attributes and groups updated in AD |
| 3 | **Leaver** | User removed from CSV → deprovisioned from AD (respecting deletion rules) |
| 4 | **Reconnection** | User re-added to CSV within grace period → scheduled deletion cancelled |

**Script**: `test/integration/scenarios/Invoke-Scenario1-HRToDirectory.ps1`

**Execution Model**:

Each test step is triggered via a `-Step` parameter. This allows JIM to complete its import/sync/export cycle between steps:

```powershell
# Step 1: Joiner - Add user to HR CSV, trigger JIM sync, verify in AD
./Invoke-Scenario1-HRToDirectory.ps1 -Step Joiner -Template Small

# ... wait for JIM to process (or trigger Run Profile manually) ...

# Step 2: Mover - Modify user in CSV, trigger JIM sync, verify changes in AD
./Invoke-Scenario1-HRToDirectory.ps1 -Step Mover -Template Small

# Step 3: Leaver - Remove user from CSV, trigger JIM sync, verify deprovisioned
./Invoke-Scenario1-HRToDirectory.ps1 -Step Leaver -Template Small

# Step 4: Reconnection - Re-add user before grace period expires, verify not deleted
./Invoke-Scenario1-HRToDirectory.ps1 -Step Reconnection -Template Small

# Run all steps sequentially (waits for JIM between each)
./Invoke-Scenario1-HRToDirectory.ps1 -Step All -Template Small
```

**Step Details**:

| Parameter | Action | Verification |
|-----------|--------|--------------|
| `-Step Joiner` | Creates test user(s) in HR CSV | User exists in AD with correct attributes |
| `-Step Mover` | Modifies department/title in CSV | AD attributes updated, group memberships changed |
| `-Step Leaver` | Removes user from HR CSV | User disabled/deleted in AD per deletion rules |
| `-Step Reconnection` | Re-adds user to CSV | Scheduled deletion cancelled, user remains active |
| `-Step All` | Runs all steps sequentially | Full lifecycle validated |

The `-Step All` option includes built-in waits and JIM Run Profile triggers between steps to automate the full test cycle.

---

#### Scenario 2: Directory to Directory Synchronisation

**Purpose**: Validate bidirectional synchronisation between two directory services.

**Systems**:
- Source: Samba AD Source
- Target: Samba AD Target

**Test Steps** (executed sequentially):

| Step | Test Case | Description |
|------|-----------|-------------|
| 1 | **Provision** | User created in Source AD → provisioned to Target AD |
| 2 | **ForwardSync** | Attributes changed in Source AD → flow to Target AD |
| 3 | **ReverseSync** | Different attributes changed in Target AD → flow back to Source AD |
| 4 | **Conflict** | Simultaneous changes to same user → conflict resolution applied |

**Script**: `test/integration/scenarios/Invoke-Scenario2-DirectorySync.ps1`

**Execution Model**:

```powershell
# Individual steps
./Invoke-Scenario2-DirectorySync.ps1 -Step Provision -Template Small
./Invoke-Scenario2-DirectorySync.ps1 -Step ForwardSync -Template Small
./Invoke-Scenario2-DirectorySync.ps1 -Step ReverseSync -Template Small
./Invoke-Scenario2-DirectorySync.ps1 -Step Conflict -Template Small

# Run all steps sequentially
./Invoke-Scenario2-DirectorySync.ps1 -Step All -Template Small
```

---

#### Scenario 3: GALSYNC (Global Address List Synchronisation)

**Purpose**: Validate exporting directory users to CSV for distribution/reporting.

**Systems**:
- Source: Samba AD Primary
- Target: CSV (GAL export)

**Test Steps** (executed sequentially):

| Step | Test Case | Description |
|------|-----------|-------------|
| 1 | **Export** | Users in AD → exported to CSV with selected attributes only |
| 2 | **Update** | User attributes modified in AD → CSV updated |
| 3 | **Delete** | User deleted in AD → removed from CSV |

**Script**: `test/integration/scenarios/Invoke-Scenario3-GALSYNC.ps1`

**Execution Model**:

```powershell
# Individual steps
./Invoke-Scenario3-GALSYNC.ps1 -Step Export -Template Small
./Invoke-Scenario3-GALSYNC.ps1 -Step Update -Template Small
./Invoke-Scenario3-GALSYNC.ps1 -Step Delete -Template Small

# Run all steps sequentially
./Invoke-Scenario3-GALSYNC.ps1 -Step All -Template Small
```

---

### Phase 2 (Post-MVP) - Databases

#### Scenario 4: Multi-Source Aggregation

**Purpose**: Validate multiple database sources feeding the metaverse with join rules and attribute precedence.

**Systems**:
- Source 1: SQL Server (HRIS System A - Business Unit A)
- Source 2: Oracle Database (HRIS System B - Business Unit B)
- Target 1: Samba AD Primary
- Target 2: CSV (Reporting)

**Test Steps** (executed sequentially):

| Step | Test Case | Description |
|------|-----------|-------------|
| 1 | **InitialLoad** | Both sources → metaverse → both targets |
| 2 | **JoinRules** | Matching employeeID across sources → single metaverse object |
| 3 | **Precedence** | SQL Server authoritative for email/phone, Oracle for department/title |
| 4 | **DataTypes** | VARCHAR, NVARCHAR, DATE, DATETIME, INT, BIT → correct mapping |

**Script**: `test/integration/scenarios/Invoke-Scenario4-MultiSourceAggregation.ps1`

**Execution Model**:

```powershell
# Individual steps
./Invoke-Scenario4-MultiSourceAggregation.ps1 -Step InitialLoad -Template Small
./Invoke-Scenario4-MultiSourceAggregation.ps1 -Step JoinRules -Template Small
./Invoke-Scenario4-MultiSourceAggregation.ps1 -Step Precedence -Template Small
./Invoke-Scenario4-MultiSourceAggregation.ps1 -Step DataTypes -Template Small

# Run all steps sequentially
./Invoke-Scenario4-MultiSourceAggregation.ps1 -Step All -Template Small
```

---

#### Scenario 5: Database Source/Target

**Purpose**: Validate database connector import/export capabilities.

**Systems**:
- Source: SQL Server
- Target: PostgreSQL

**Test Steps** (executed sequentially):

| Step | Test Case | Description |
|------|-----------|-------------|
| 1 | **Import** | Import users from SQL Server |
| 2 | **Export** | Export users to PostgreSQL |
| 3 | **DataTypes** | Data type handling (text, numeric, date, boolean) |
| 4 | **MultiValue** | Multi-valued attributes (if supported) |

**Script**: `test/integration/scenarios/Invoke-Scenario5-DatabaseSourceTarget.ps1`

**Execution Model**:

```powershell
# Individual steps
./Invoke-Scenario5-DatabaseSourceTarget.ps1 -Step Import -Template Small
./Invoke-Scenario5-DatabaseSourceTarget.ps1 -Step Export -Template Small
./Invoke-Scenario5-DatabaseSourceTarget.ps1 -Step DataTypes -Template Small
./Invoke-Scenario5-DatabaseSourceTarget.ps1 -Step MultiValue -Template Small

# Run all steps sequentially
./Invoke-Scenario5-DatabaseSourceTarget.ps1 -Step All -Template Small
```

---

#### Scenario 6: Performance Baselines

**Purpose**: Establish performance characteristics at various scales.

**Systems**: All (depending on scenario)

**Test Coverage**:
1. Run each scenario at each template scale
2. Measure import, sync, export times
3. Measure memory consumption
4. Identify bottlenecks
5. Establish acceptable thresholds

**Script**: `test/integration/scenarios/Invoke-Scenario6-Performance.ps1`

---

## Setup & Configuration

### Prerequisites

- Docker and Docker Compose v2
- PowerShell 7+ (cross-platform)
- JIM built and ready (`dotnet build JIM.sln`)
- 8GB+ RAM available for containers
- 32GB+ disk space for larger templates

### Initial Setup

1. **Clone Repository** (if not already):
   ```bash
   git clone https://github.com/TetronIO/JIM.git
   cd JIM
   ```

2. **Verify Docker**:
   ```bash
   docker --version
   docker compose version
   ```

3. **Review Compose Configuration**:
   ```bash
   cat docker-compose.integration-tests.yml
   ```

### JIM Configuration via PowerShell Module

Integration tests require JIM to be configured with Connected Systems, Sync Rules, and Run Profiles. This is automated using the **JIM PowerShell Module** ([#176](https://github.com/TetronIO/JIM/issues/176)) with **API Key Authentication** ([#175](https://github.com/TetronIO/JIM/issues/175)).

#### Why PowerShell Module?

- **Maintainable**: Uses supported JIM APIs, not direct database manipulation
- **Testable**: The module itself is tested, increasing confidence
- **Reusable**: Same cmdlets used for production automation
- **Documented**: Clear, readable configuration scripts

#### Authentication for Non-Interactive Testing

Tests authenticate using API keys (X-API-Key header), avoiding the need for SSO/browser interaction:

```powershell
# API key stored as environment variable or GitHub secret
Connect-JIM -ApiKey $env:JIM_API_KEY -BaseUrl "http://localhost:5203"
```

#### Example: Configure JIM for Scenario 1

```powershell
# test/integration/Setup-Scenario1.ps1

param(
    [string]$ApiKey = $env:JIM_API_KEY,
    [string]$BaseUrl = "http://localhost:5203"
)

Import-Module JIM.PowerShell

# Connect to JIM
Connect-JIM -ApiKey $ApiKey -BaseUrl $BaseUrl

# Create HR CSV Connected System (Source)
$hrSystem = New-JIMConnectedSystem -Name "HR CSV" `
    -ConnectorType "CSV" `
    -Configuration @{
        FilePath = "/connector-files/hr-users.csv"
        Delimiter = ","
        HasHeader = $true
        AnchorAttribute = "employeeId"
    }

# Create Samba AD Connected System (Target)
$adSystem = New-JIMConnectedSystem -Name "Samba AD Primary" `
    -ConnectorType "LDAP" `
    -Configuration @{
        Server = "samba-ad-primary"
        Port = 389
        BaseDN = "DC=testdomain,DC=local"
        BindDN = "CN=Administrator,CN=Users,DC=testdomain,DC=local"
        BindPassword = "Test@123!"
        UserContainer = "OU=Users,DC=testdomain,DC=local"
    }

# Create Inbound Sync Rule (HR -> Metaverse)
New-JIMSyncRule -Name "HR Users Inbound" `
    -ConnectedSystemId $hrSystem.Id `
    -Direction Inbound `
    -ObjectType "user" `
    -MetaverseObjectType "person" `
    -JoinRules @(
        @{ CSAttribute = "employeeId"; MVAttribute = "employeeId" }
    ) `
    -AttributeFlows @(
        @{ Source = "employeeId"; Target = "employeeId"; Type = "Direct" }
        @{ Source = "firstName"; Target = "givenName"; Type = "Direct" }
        @{ Source = "lastName"; Target = "sn"; Type = "Direct" }
        @{ Source = "email"; Target = "mail"; Type = "Direct" }
        @{ Source = "department"; Target = "department"; Type = "Direct" }
        @{ Source = "title"; Target = "title"; Type = "Direct" }
    )

# Create Outbound Sync Rule (Metaverse -> AD)
New-JIMSyncRule -Name "AD Users Outbound" `
    -ConnectedSystemId $adSystem.Id `
    -Direction Outbound `
    -ObjectType "user" `
    -MetaverseObjectType "person" `
    -AttributeFlows @(
        @{ Source = "givenName"; Target = "givenName"; Type = "Direct" }
        @{ Source = "sn"; Target = "sn"; Type = "Direct" }
        @{ Source = "mail"; Target = "mail"; Type = "Direct" }
        @{ Source = "department"; Target = "department"; Type = "Direct" }
        @{ Source = "title"; Target = "title"; Type = "Direct" }
        @{ Source = "employeeId"; Target = "employeeNumber"; Type = "Direct" }
    )

# Create Run Profile
New-JIMRunProfile -Name "HR to AD Full Sync" `
    -Steps @(
        @{ ConnectedSystemId = $hrSystem.Id; Type = "FullImport" }
        @{ Type = "FullSynchronisation" }
        @{ ConnectedSystemId = $adSystem.Id; Type = "Export" }
    )

Write-Host "Scenario 1 configuration complete" -ForegroundColor Green
```

#### Dependencies

| Dependency | Issue | Status | Notes |
|------------|-------|--------|-------|
| API Key Authentication | [#175](https://github.com/TetronIO/JIM/issues/175) | **✅ Complete** | API key authentication fully functional for all endpoints |
| PowerShell Module | [#176](https://github.com/TetronIO/JIM/issues/176) | **✅ Complete** | Core cmdlets implemented and tested |

#### API Key Authentication Status (Issue #175)

**✅ RESOLVED** - API key authentication is now fully functional for both GET and POST/PUT/DELETE operations.

**Completed:**
- ✅ Created 3 connector definition API endpoints (GET list, GET by ID, GET by name)
- ✅ Created `Get-JIMConnectorDefinition` PowerShell cmdlet
- ✅ Added cmdlet to module manifest exports
- ✅ OIDC redirect suppressed for API requests (returns 401 JSON instead of 302)
- ✅ Added detailed logging to API key authentication handler
- ✅ **Fixed**: API key handler now invoked for `[Authorize]` protected endpoints
- ✅ **Fixed**: DbContext threading issues in authentication handler
- ✅ **Fixed**: Write operations (POST/PUT/DELETE) now work with API key auth
- ✅ **Fixed**: Null initiatedBy handling for API key authentication
- ✅ Build succeeds, all 395 unit tests pass

**Root Cause & Fix:**
The issue was that ASP.NET Core's authentication pipeline only runs the DefaultScheme (Cookie) by default. Other schemes are only tried when explicitly requested. The fix was to add `ForwardDefaultSelector` to Cookie authentication options, which conditionally forwards to API Key authentication when the `X-API-Key` header is present.

**Technical Details:**
- Added `ForwardDefaultSelector` in Program.cs Cookie options
- Changed `ApiKeyAuthenticationHandler` to inject `IServiceProvider` instead of `JimApplication`
- Create new DI scope for each database operation to prevent DbContext threading issues
- Separate scope for background usage tracking task
- Allow null `initiatedBy` for API key auth in SynchronisationController
- Use appropriate constructors for worker tasks when user context unavailable

#### PowerShell Module Status (Issue #176)

**✅ Core cmdlets implemented and tested.**

**Completed Cmdlets:**
- ✅ `Connect-JIM` - API key authentication
- ✅ `Get-JIMConnectorDefinition` - List and retrieve connector definitions
- ✅ `Get-JIMConnectedSystem` / `New-JIMConnectedSystem` / `Set-JIMConnectedSystem` - Manage connected systems
- ✅ `Get-JIMRunProfile` / `New-JIMRunProfile` / `Start-JIMRunProfile` - Manage and execute run profiles
- ✅ `Get-JIMSyncRule` / `New-JIMSyncRule` - Manage sync rules

**Fixes Applied:**
- ✅ Fixed pagination handling for empty arrays (Get-JIMConnectedSystem, Get-JIMSyncRule)
- ✅ Fixed parameter types (Get-JIMConnectorDefinition -Id uses int, not Guid)
- ✅ Fixed JSON serialization of hashtable keys (Set-JIMConnectedSystem)

**Remaining Work:**
- Sync rules require object type IDs from imported connector schema (needs schema import cmdlet)

---

## Running Tests Locally

### Quick Start - Single Scenario

```powershell
# Stand up Phase 1 systems
docker compose -f docker-compose.integration-tests.yml up -d

# Wait for systems to be ready (AD takes ~2 minutes to initialise)
Start-Sleep -Seconds 120

# Populate with Small template
./test/integration/Populate-SambaAD.ps1 -Template Small -Instance Primary
./test/integration/Generate-TestCSV.ps1 -Template Small

# Run Scenario 1
./test/integration/scenarios/Invoke-Scenario1-HRToDirectory.ps1 -Template Small

# Tear down when complete
docker compose -f docker-compose.integration-tests.yml down -v
```

### Running Specific Scenario

#### Scenario 2 (Directory Sync)

Scenario 2 requires two AD instances, so use the `scenario2` profile:

```powershell
# Stand up Scenario 2 systems (Source and Target AD)
docker compose -f docker-compose.integration-tests.yml --profile scenario2 up -d

# Wait for systems
Start-Sleep -Seconds 180

# Populate both AD instances
./test/integration/Populate-SambaAD.ps1 -Template Medium -Instance Source
./test/integration/Populate-SambaAD.ps1 -Template Medium -Instance Target

# Run scenario
./test/integration/scenarios/Invoke-Scenario2-DirectorySync.ps1 -Template Medium

# Tear down
docker compose -f docker-compose.integration-tests.yml --profile scenario2 down -v
```

### Running All Phase 1 Scenarios

```powershell
# Use master script
./test/integration/Invoke-IntegrationTests.ps1 -Template Medium -Phase 1
```

This script will:
1. Stand up all Phase 1 systems
2. Populate data
3. Run all Phase 1 scenarios
4. Collect results to `test/integration/results/`
5. Tear down systems
6. Display summary report

### Phase 2 (Database Scenarios)

Phase 2 requires the Database Connector (#170) to be complete:

```powershell
# Stand up Phase 2 systems
docker compose -f docker-compose.integration-tests.yml --profile phase2 up -d

# Wait for databases (Oracle can take 5-10 minutes)
./test/integration/Wait-SystemsReady.ps1 -Phase 2

# Populate databases
./test/integration/Populate-SqlServer.ps1 -Template Medium
./test/integration/Populate-Oracle.ps1 -Template Medium
./test/integration/Populate-PostgreSQL.ps1 -Template Medium

# Run Phase 2 scenarios
./test/integration/Invoke-IntegrationTests.ps1 -Template Medium -Phase 2

# Tear down
docker compose -f docker-compose.integration-tests.yml --profile phase2 down -v
```

---

## CI/CD Integration

Integration tests run manually via GitHub Actions `workflow_dispatch` to avoid excessive resource consumption.

### Triggering a Test Run

1. Navigate to **Actions** tab in GitHub
2. Select **Integration Tests** workflow
3. Click **Run workflow**
4. Choose:
   - **Template**: Data scale (Micro to XXLarge)
   - **Phase**: 1 (MVP) or 2 (Post-MVP)
5. Click **Run workflow**

### Workflow Configuration

See `.github/workflows/integration-tests.yml` for complete workflow definition.

**Key Features**:
- Manual trigger only (`workflow_dispatch`)
- Configurable template and phase
- Complete stand-up/tear-down for idempotency
- Artefact upload for test results
- Timeout protection (2 hours max)

**When to Run**:
- Before creating a release
- After major connector changes
- After sync engine modifications
- When validating performance improvements
- Before merging large PRs

**Not Recommended**:
- On every commit (too expensive)
- On every PR (use unit tests instead)
- During development (use local testing)

---

## Writing New Scenarios

### Scenario Script Template

```powershell
<#
.SYNOPSIS
    Test Scenario X: [Brief Description]

.DESCRIPTION
    [Detailed description of what this scenario validates]

.PARAMETER Template
    Data scale template (Micro, Small, Medium, Large, XLarge, XXLarge)

.EXAMPLE
    ./Invoke-ScenarioX-Name.ps1 -Template Medium
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Micro", "Small", "Medium", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import test utilities
. "$PSScriptRoot/../utils/Test-Helpers.ps1"

Write-Host "Starting Scenario X: [Name]" -ForegroundColor Cyan
Write-Host "Template: $Template" -ForegroundColor Gray

# Test Step 1
Write-Host "`n[Step 1] Description of step..." -ForegroundColor Yellow
# ... perform action ...
Assert-Condition -Condition $result -Message "Expected outcome"

# Test Step 2
Write-Host "`n[Step 2] Description of step..." -ForegroundColor Yellow
# ... perform action ...
Assert-Condition -Condition $result -Message "Expected outcome"

# Summary
Write-Host "`n✓ Scenario X completed successfully" -ForegroundColor Green
```

### Testing Utilities

Create reusable helpers in `test/integration/utils/`:

**`Test-Helpers.ps1`**:
```powershell
function Assert-Condition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        Write-Host "✗ FAILED: $Message" -ForegroundColor Red
        throw "Assertion failed: $Message"
    }

    Write-Host "✓ PASSED: $Message" -ForegroundColor Green
}

function Wait-ForSync {
    param(
        [int]$TimeoutSeconds = 300
    )

    # Poll JIM API for activity completion
    # ... implementation ...
}

function Get-ADUser {
    param(
        [string]$SamAccountName,
        [string]$Server = "localhost",
        [int]$Port = 389
    )

    # LDAP query to get user
    # ... implementation ...
}
```

### Best Practices

1. **Idempotent**: Script should be runnable multiple times safely
2. **Clear Output**: Use colour-coded Write-Host for test steps
3. **Error Handling**: Fail fast with descriptive error messages
4. **Cleanup**: Leave systems in known state after test
5. **Documentation**: Clear parameter descriptions and examples
6. **Assertions**: Use helper functions for consistent assertion messages

---

## Troubleshooting

### Container Issues

**Samba AD won't start**:
```powershell
# Check logs
docker logs samba-ad-primary

# Common issue: Port already in use
netstat -an | Select-String "389"

# Solution: Stop conflicting service or change port mapping
```

**Oracle takes forever to start**:
```powershell
# Oracle XE first start can take 10-15 minutes
docker logs -f oracle-hris-b

# Wait for: "DATABASE IS READY TO USE!"
```

**PostgreSQL connection refused**:
```powershell
# Check if container is running
docker ps | Select-String "postgres"

# Check connection
Test-NetConnection -ComputerName localhost -Port 5433
```

### Data Population Issues

**Populate script fails with "Access Denied"**:
```powershell
# Ensure Samba AD is fully initialised
Start-Sleep -Seconds 120

# Verify admin credentials
docker exec samba-ad-primary samba-tool user list
```

**CSV files not found**:
```powershell
# Verify volume mount
docker volume inspect test-csv-data

# Check file exists
Get-ChildItem /connector-files/test-data/
```

### Scenario Failures

**User not provisioned**:
1. Check JIM logs: `docker logs jim.web`
2. Verify Connected System configured correctly
3. Verify Sync Rule attribute flows
4. Check Run Profile executed successfully
5. Review Activity history in JIM UI

**Attribute flow incorrect**:
1. Review Sync Rule configuration
2. Check attribute precedence rules
3. Verify source attribute populated
4. Check for transformation functions

**Performance degradation**:
1. Check database query performance
2. Review Activity execution times
3. Check container resource limits
4. Monitor memory consumption
5. Check for n+1 query patterns

### Getting Help

- **GitHub Issues**: [Create an issue](https://github.com/TetronIO/JIM/issues/new) with `integration-testing` label
- **Logs**: Always include relevant logs from containers and JIM services
- **Template**: Specify which template size was being used
- **Environment**: Note if running locally or in CI/CD

---

## Appendix

### File Structure

```
JIM/
├── docker-compose.integration-tests.yml                    # Container definitions
├── test/
│   ├── JIM.Api.Tests/                                      # API unit tests
│   ├── JIM.Models.Tests/                                   # Model unit tests
│   └── integration/
│       ├── Invoke-IntegrationTests.ps1                     # Master test runner
│       ├── Populate-SambaAD.ps1                            # AD population
│       ├── Generate-TestCSV.ps1                            # CSV generation
│       ├── Populate-SqlServer.ps1                          # SQL Server population (Phase 2)
│       ├── Populate-Oracle.ps1                             # Oracle population (Phase 2)
│       ├── Populate-PostgreSQL.ps1                         # PostgreSQL setup (Phase 2)
│       ├── Wait-SystemsReady.ps1                           # Health check script
│       ├── scenarios/
│       │   ├── Invoke-Scenario1-HRToDirectory.ps1
│       │   ├── Invoke-Scenario2-DirectorySync.ps1
│       │   ├── Invoke-Scenario3-GALSYNC.ps1
│       │   ├── Invoke-Scenario4-MultiSourceAggregation.ps1  # Phase 2
│       │   ├── Invoke-Scenario5-DatabaseSourceTarget.ps1    # Phase 2
│       │   └── Invoke-Scenario6-Performance.ps1             # Phase 2
│       ├── utils/
│       │   ├── Test-Helpers.ps1                             # Common test utilities
│       │   ├── LDAP-Helpers.ps1                             # LDAP query functions
│       │   └── Database-Helpers.ps1                         # Database query functions
│       └── results/                                         # Test results output
└── docs/
    └── INTEGRATION_TESTING.md                               # This document
```

### Container Port Reference

| Service              | Container Port | Host Port | Protocol |
|----------------------|----------------|-----------|----------|
| Samba AD Primary     | 389            | 389       | LDAP     |
| Samba AD Primary     | 636            | 636       | LDAPS    |
| Samba AD Source      | 389            | 10389     | LDAP     |
| Samba AD Source      | 636            | 10636     | LDAPS    |
| Samba AD Target      | 389            | 11389     | LDAP     |
| Samba AD Target      | 636            | 11636     | LDAPS    |
| SQL Server           | 1433           | 1433      | TCP      |
| Oracle XE            | 1521           | 1521      | TCP      |
| PostgreSQL (Test)    | 5432           | 5433      | TCP      |
| MySQL                | 3306           | 3306      | TCP      |
| OpenLDAP             | 389            | 12389     | LDAP     |
| OpenLDAP             | 636            | 12636     | LDAPS    |

### Environment Variables

| Variable                    | Default            | Description                          |
|-----------------------------|--------------------|--------------------------------------|
| `INTEGRATION_TEST_TEMPLATE` | `Medium`           | Default template for test runs       |
| `INTEGRATION_TEST_TIMEOUT`  | `7200` (2 hours)   | Maximum test execution time          |
| `SAMBA_ADMIN_PASSWORD`      | `Test@123!`        | Samba AD admin password              |
| `SQL_SA_PASSWORD`           | `Test@123!`        | SQL Server SA password               |
| `ORACLE_ADMIN_PASSWORD`     | `Test@123!`        | Oracle SYS/SYSTEM password           |
| `POSTGRES_PASSWORD`         | `Test@123!`        | PostgreSQL admin password            |

---

## Current Progress & Known Issues

### Phase 1 Status (as of 2025-12-16) - ✅ COMPLETE

| Component | Status | Notes |
|-----------|--------|-------|
| Infrastructure | ✅ Complete | Samba AD, CSV file mounting, volume orchestration |
| API Endpoints | ✅ Complete | Schema management, sync rules, mappings, run profiles |
| PowerShell Module | ✅ Complete | All cmdlets for Scenario 1 |
| Setup-Scenario1.ps1 | ✅ Complete | Automated JIM configuration working |
| Invoke-Scenario1 | ✅ Complete | All 4 tests passing (Joiner, Mover, Leaver, Reconnection) |
| Scenario 2 & 3 | ⏳ Pending | Placeholder scripts exist |
| GitHub Actions | ⏳ Pending | CI/CD workflow not yet created |

### Completed Fixes (This Session)

1. **API routing fix** - `CreatedAtAction` failed with API versioning. Changed to explicit `Created()` with URL path.

2. **PowerShell enum serialisation** - `New-JIMMetaverseAttribute` and `Set-JIMMetaverseAttribute` were sending string enum values ("Text") but API expected integers. Added mapping dictionaries.

3. **CSV path fix** - Integration test wrote to wrong path. Fixed to use `test/test-data/` which is mounted to JIM containers.

4. **PowerShell array count** - Fixed `$testResults.Steps.Count` to `@($testResults.Steps).Count` for empty arrays.

5. **Hashtable property access** - Fixed strict mode error accessing `.Error` property on hashtables without it.

6. **CSV generation** - Added `userPrincipalName` and `dn` columns to generated CSV files.

7. **Nano template** - Added smallest template (3 users) for fast iteration during development.

8. **File connector change detection** - `.Include()` calls were missing in repository methods that retrieve CSOs. Added eager loading for `AttributeValues` and `Attributes` navigation properties.

9. **Test data reset** - Added CSV test data reset and AD user cleanup at start of each test run for repeatable execution.

### Previously Fixed Issues

1. **DisplayNameOrId bug** - `ConnectedSystemObject.DisplayNameOrId` was using `SingleOrDefault` which threw "Sequence contains more than one matching element" when duplicate attribute values existed. Fixed by changing to `FirstOrDefault`.

2. **MetaverseAttribute.MetaverseObjectTypes null** - When updating attribute associations, the collection wasn't loaded. Added `GetMetaverseAttributeWithObjectTypesAsync` method to include navigation property.

### Quick Start for New Session

```powershell
# 1. Start the full Docker stack (JIM + external systems)
jim-stack
docker compose -f docker-compose.integration-tests.yml up -d

# 2. Wait for systems to be ready
pwsh test/integration/Wait-SystemsReady.ps1

# 3. Set up infrastructure API key (if not already done)
pwsh test/integration/Setup-InfrastructureApiKey.ps1

# 4. Run Scenario 1 setup (creates connected systems, sync rules, mappings)
pwsh test/integration/Setup-Scenario1.ps1 -ApiKey "jim_ak_xxx" -Template Micro

# 5. Run the full test scenario
pwsh test/integration/scenarios/Invoke-Scenario1-HRToDirectory.ps1 -ApiKey "jim_ak_xxx" -Template Micro

# 6. Check logs if tests fail
docker logs jim.worker --tail 100
docker logs jim.web --tail 100
```

### Files Modified in This Branch

**New Files:**
- `test/JIM.Api.Tests/SynchronisationControllerSchemaTests.cs` - 14 tests
- `test/JIM.Api.Tests/MetaverseControllerAttributeTests.cs` - 18 tests
- `test/JIM.Api.Tests/SynchronisationControllerMappingTests.cs` - 11 tests

**Bug Fixes:**
- `JIM.Application/Servers/ConnectedSystemServer.cs` - Activity.ConnectedSystemId for UPDATE operations
- `JIM.Web/Controllers/Api/MetaverseController.cs` - Collection initialisation, eager loading for updates
- `JIM.Models/Staging/ConnectedSystemObject.cs` - DisplayNameOrId FirstOrDefault fix
- `JIM.Data/Repositories/IMetaverseRepository.cs` - Added GetMetaverseAttributeWithObjectTypesAsync
- `JIM.PostgresData/Repositories/MetaverseRepository.cs` - Implemented GetMetaverseAttributeWithObjectTypesAsync
- `JIM.PostgresData/Repositories/ConnectedSystemRepository.cs` - Added .Include() for AttributeValues in CSO retrieval methods

**Integration Test Improvements:**
- `test/integration/Setup-Scenario1.ps1` - Fixed API response property names (metaverseObjectTypes)
- `test/integration/scenarios/Invoke-Scenario1-HRToDirectory.ps1` - Added CSV reset and AD cleanup for repeatable tests

### Next Steps

1. ~~**Debug sync engine export**~~ - ✅ Fixed! Users now provisioned to AD successfully
2. ~~**Fix file connector change detection**~~ - ✅ Fixed! All Scenario 1 tests now passing
3. **Complete Scenarios 2 & 3** - Directory-to-Directory sync and GALSYNC (blocked - see below)
4. **Create GitHub Actions workflow** - Automate integration tests in CI/CD

### Blocking Issue: LDAP Partition Management API Missing

**Status**: 🚧 BLOCKED

**Symptom**: Scenario 2 (Directory-to-Directory) setup completes but imports return no objects.

**Root Cause**: The LDAP connector requires partitions and containers to be selected for import. The connector iterates over `ConnectedSystem.Partitions.Where(p => p.Selected)` and then selected containers within each partition. Currently:
- There is no API endpoint to list, select, or configure partitions
- Schema import creates partitions but doesn't select them by default
- The PowerShell module has no cmdlets for partition management

**Impact**:
- Scenario 2 (Directory-to-Directory): ❌ BLOCKED - LDAP-to-LDAP sync cannot work without partition selection
- Scenario 3 (GALSYNC): ❌ BLOCKED - LDAP import to CSV export needs partition selection

**Fix Required**: Implement partition management API endpoints:
1. `GET /api/v1/synchronisation/connected-systems/{id}/partitions` - List partitions
2. `PUT /api/v1/synchronisation/connected-systems/{id}/partitions/{partitionId}` - Update partition (select/deselect)
3. `PUT /api/v1/synchronisation/connected-systems/{id}/partitions/{partitionId}/containers/{containerId}` - Update container selection

**Files Created (Ready for Use Once API Available)**:
- `test/integration/Setup-Scenario2.ps1` - JIM configuration for directory sync
- `test/integration/scenarios/Invoke-Scenario2-DirectorySync.ps1` - Test execution script

### Resolved Issue: File Connector Not Detecting Attribute Changes

**Status**: ✅ RESOLVED (2025-12-16)

**Symptom**: The Mover test was failing - when `bob.smith1` department changed from "HR" to "IT" in the CSV, the change was not exported to AD.

**Root Cause**: Repository methods that retrieve CSOs for comparison were missing `.Include()` calls for navigation properties. When the sync engine compared incoming attribute values with existing CSO values, the existing values were null because they weren't eagerly loaded.

**Fix Applied**: Added `.Include(cso => cso.AttributeValues).ThenInclude(av => av.Attribute)` to these methods in `JIM.PostgresData/Repositories/ConnectedSystemRepository.cs`:
- `GetConnectedSystemObjectByAnchorAsync`
- `GetConnectedSystemObjectByDnAsync`
- `FindExistingConnectedSystemObjectAsync`

**Test Results After Fix**:
- Joiner test: ✅ PASSES (new user creation works)
- Mover test: ✅ PASSES (attribute updates now detected and exported)
- Leaver test: ✅ PASSES (user flagged for deletion - actual deletion is policy-based)
- Reconnection test: ✅ PASSES (user preservation works)

---

## Version History

| Version | Date       | Changes                                         |
|---------|------------|-------------------------------------------------|
| 1.8     | 2025-12-16 | Added Scenario 2 scripts (Setup-Scenario2.ps1, Invoke-Scenario2-DirectorySync.ps1). Documented blocking issue - LDAP partition management API needed. |
| 1.7     | 2025-12-16 | **Phase 1 Complete!** All Scenario 1 tests passing. Fixed file connector change detection (missing .Include() calls). Added test data reset and AD cleanup for repeatable tests. |
| 1.6     | 2025-12-16 | Ran full Scenario 1 tests, documented file connector change detection issue |
| 1.5     | 2025-12-16 | Scenario 1 Joiner test passing, added Nano template, multiple bug fixes |
| 1.4     | 2025-12-16 | Added Current Progress section, known issues, quick start guide |
| 1.3     | 2025-12-13 | Added Test Lifecycle Quick Reference section for DevContainer and CI/CD |
| 1.2     | 2025-12-09 | Added JIM configuration section, step-based execution, dependencies |
| 1.1     | 2025-12-08 | Updated file paths to use existing test/ folder |
| 1.0     | 2025-12-08 | Initial version - Phase 1 & 2 specification     |

---

## Related Documentation

- [MVP Definition](MVP_DEFINITION.md) - Overall project status
- [Developer Guide](DEVELOPER_GUIDE.md) - Development setup and architecture
- [GitHub Issue #173](https://github.com/TetronIO/JIM/issues/173) - Integration Testing Framework tracking issue
- [GitHub Issue #170](https://github.com/TetronIO/JIM/issues/170) - SQL Database Connector (Phase 2 dependency)
- [GitHub Issue #175](https://github.com/TetronIO/JIM/issues/175) - API Key Authentication (required for non-interactive testing)
- [GitHub Issue #176](https://github.com/TetronIO/JIM/issues/176) - PowerShell Module (required for JIM configuration)
