# Integration Testing Framework

| | |
|---|---|
| **Status** | **In Progress** |
| **Phase 1 Target** | MVP Validation |
| **Phase 2 Target** | Post-MVP (after Database Connector #170) |
| **Related Issue** | [#173](https://github.com/TetronIO/JIM/issues/173) |

---

## ⚡ Quick Start

**First time running integration tests?** Use the single-command runner:

```powershell
# IMPORTANT: Run these commands inside PowerShell, not bash/zsh!
pwsh

# From the repository root (/workspaces/JIM)
cd /workspaces/JIM

# Run the complete test suite with a single command
./test/integration/Run-IntegrationTests.ps1
```

This single script handles everything:
1. ✅ Builds custom Samba AD image if not present (first run only, ~30 seconds)
2. ✅ Resets the environment (stops containers, removes volumes)
3. ✅ Rebuilds and starts JIM stack + Samba AD
4. ✅ Waits for all services to be ready
5. ✅ Creates an infrastructure API key
6. ✅ Runs the test scenario

**Common Options:**

```powershell
# Run with default settings (Scenario1, Nano template, all steps)
./test/integration/Run-IntegrationTests.ps1

# Run a specific scenario
./test/integration/Run-IntegrationTests.ps1 -Scenario "Scenario1-HRToIdentityDirectory"   # HR CSV → AD provisioning
./test/integration/Run-IntegrationTests.ps1 -Scenario "Scenario2-CrossDomainSync"         # APAC AD → EMEA AD sync
./test/integration/Run-IntegrationTests.ps1 -Scenario "Scenario4-DeletionRules"           # Deletion rules testing
./test/integration/Run-IntegrationTests.ps1 -Scenario "Scenario5-MatchingRules"           # Matching rules testing
./test/integration/Run-IntegrationTests.ps1 -Scenario "Scenario8-CrossDomainEntitlementSync"  # Group sync between domains

# Run with a specific template size
./test/integration/Run-IntegrationTests.ps1 -Template Nano
./test/integration/Run-IntegrationTests.ps1 -Template Micro
./test/integration/Run-IntegrationTests.ps1 -Template Small
./test/integration/Run-IntegrationTests.ps1 -Template Medium
./test/integration/Run-IntegrationTests.ps1 -Template MediumLarge
./test/integration/Run-IntegrationTests.ps1 -Template Large
./test/integration/Run-IntegrationTests.ps1 -Template XLarge
./test/integration/Run-IntegrationTests.ps1 -Template XXLarge

# Run only a specific test step (steps vary by scenario)
./test/integration/Run-IntegrationTests.ps1 -Step Joiner                          # Scenario 1: Joiner, Mover, Leaver, Reconnection
./test/integration/Run-IntegrationTests.ps1 -Scenario "Scenario2-CrossDomainSync" -Step Provision  # Scenario 2: Provision, ForwardSync, ReverseSync
./test/integration/Run-IntegrationTests.ps1 -Scenario "Scenario8-CrossDomainEntitlementSync" -Step InitialSync  # Scenario 8: InitialSync, ForwardSync, DetectDrift, ReassertState, NewGroup, DeleteGroup

# Combine scenario, template, and step
./test/integration/Run-IntegrationTests.ps1 -Scenario "Scenario2-CrossDomainSync" -Template Small -Step All

# Skip reset for faster re-runs (keeps existing environment)
./test/integration/Run-IntegrationTests.ps1 -SkipReset

# Skip rebuild (use existing Docker images)
./test/integration/Run-IntegrationTests.ps1 -SkipReset -SkipBuild
```

**Available Scenarios (`-Scenario` parameter):**

| Scenario | Description | Containers Used |
|----------|-------------|-----------------|
| `Scenario1-HRToIdentityDirectory` | HR CSV → Subatomic AD provisioning (Joiner/Mover/Leaver) | samba-ad-primary |
| `Scenario2-CrossDomainSync` | Quantum Dynamics APAC → EMEA directory sync | samba-ad-source, samba-ad-target |
| `Scenario4-DeletionRules` | Deletion rules and grace period testing | samba-ad-primary |
| `Scenario5-MatchingRules` | Object matching rules testing | samba-ad-primary |
| `Scenario8-CrossDomainEntitlementSync` | Group synchronisation between APAC and EMEA domains | samba-ad-source, samba-ad-target |

**Available Templates (`-Template` parameter):**

Choose a template based on your testing goals:

- **Nano** (default): 3 users, 1 group - **< 10 sec** - Fast dev iteration and debugging
- **Micro**: 10 users, 3 groups - **< 30 sec** - Quick smoke tests and development
- **Small**: 100 users, 20 groups - **< 2 min** - Small business scenarios, unit testing
- **Medium**: 1,000 users, 100 groups - **< 2 min** - Medium enterprise, CI/CD pipelines
- **MediumLarge**: 5,000 users, 250 groups - **< 5 min** - Large medium enterprise, performance validation
- **Large**: 10,000 users, 500 groups - **< 15 min** - Large enterprise, performance baselines
- **XLarge**: 100,000 users, 2,000 groups - **< 2 hours** - Very large enterprise, stress testing
- **XXLarge**: 1,000,000 users, 10,000 groups - **TBD** - Global enterprise, scale limits

See [Data Scale Templates](#data-scale-templates) for detailed template specifications.

**Alternative: Manual step-by-step (for debugging or more control)**

```powershell
# 1. Start complete test environment (JIM stack + Samba AD + readiness check)
./test/integration/Start-IntegrationTestEnvironment.ps1

# 2. Create Infrastructure API Key
./test/integration/Setup-InfrastructureApiKey.ps1

# 3. Run Scenario 1
./test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1 -Template Nano -ApiKey (Get-Content test/integration/.api-key)
```

**Helper Scripts:**
- `Run-IntegrationTests.ps1` - **Single-command test runner (recommended!)**
- `Start-IntegrationTestEnvironment.ps1` - Starts JIM + Samba AD (used by runner)
- `Wait-SambaReady.ps1` - Checks if Samba AD is ready
- `Setup-InfrastructureApiKey.ps1` - Creates API key for testing

---

## Table of Contents

1. [Quick Start](#-quick-start)
2. [Test Lifecycle Quick Reference](#test-lifecycle-quick-reference)
3. [Overview](#overview)
4. [Architecture](#architecture)
5. [Data Scale Templates](#data-scale-templates)
6. [Test Scenarios](#test-scenarios)
7. [Setup & Configuration](#setup--configuration)
8. [Running Tests Locally](#running-tests-locally)
9. [CI/CD Integration](#cicd-integration)
10. [Writing New Scenarios](#writing-new-scenarios)
11. [Troubleshooting](#troubleshooting)
12. [Known Issues & TODOs](#known-issues--todos)

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
| Run tests | `./Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step All` | Execute test scenario |
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
│  │ Subatomic AD     │  │ Quantum Dynamics │  │ Quantum      │  │
│  │ (Scenarios 1&3)  │  │ APAC (Scen. 2)   │  │ Dynamics     │  │
│  │ Port: 389/636    │  │ Port: 10389/636  │  │ EMEA: 11389  │  │
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
       ├─> Invoke-Scenario1-HRToIdentityDirectory.ps1
       ├─> Invoke-Scenario2-CrossDomainSync.ps1
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
| **Nano**   | 3         | 1       | 1               | 4             | Fast dev iteration, debugging     | < 10 sec   |
| **Micro**  | 10        | 3       | 3               | 13            | Quick smoke tests, development    | < 30 sec   |
| **Small**  | 100       | 20      | 5               | 120           | Small business, unit tests        | < 2 min    |
| **Medium** | 1,000     | 100     | 8               | 1,100         | Medium enterprise, CI/CD          | < 2 min    |
| **MediumLarge** | 5,000 | 250     | 9               | 5,250         | Large medium enterprise, validation | < 5 min  |
| **Large**  | 10,000    | 500     | 10              | 10,500        | Large enterprise, baselines       | < 15 min   |
| **XLarge** | 100,000   | 2,000   | 12              | 102,000       | Very large enterprise, stress     | < 2 hours  |
| **XXLarge**| 1,000,000 | 10,000  | 15              | 1,010,000     | Global enterprise, scale limits   | TBD        |

### Data Characteristics

All templates generate realistic enterprise data following normal distribution patterns:

- **Job Titles**: Hierarchy (many Individual Contributors, few C-level)
- **Departments**: Realistic structure (IT, HR, Sales, Finance, Operations, Marketing)
- **Group Memberships**: Normal distribution (most users 5-15 groups, power users 30+)
- **Attributes**: Valid names, email patterns, phone numbers, addresses
- **Organisational Structure**: Tree hierarchy with realistic spans of control

---

## Test Scenarios

### Phase 1 (MVP) - Person Entity Scenarios (LDAP & CSV)

#### Scenario 1: Person Entity - HR to Identity Directory

**Purpose**: Validate the most common ILM use case - provisioning users from HR system to Active Directory.

**Systems**:
- Source: CSV (HR system)
- Target: Subatomic AD

**Test Data**:
- HR CSV includes Company attribute: "Subatomic" for employees, partner companies for contractors
- Partner companies: Nexus Dynamics, Orbital Systems, Quantum Bridge, Stellar Logistics, Vertex Solutions

**Test Steps** (executed sequentially):

| Step | Test Case | Description |
|------|-----------|-------------|
| 1 | **Joiner** | User added to HR CSV → provisioned to AD with correct attributes and group memberships |
| 2a | **Mover** | User title changed in CSV → attribute updated in AD (no DN impact) |
| 2b | **Mover-Rename** | User name changed in CSV → DN renamed in AD (same container) |
| 2c | **Mover-Move** | User department changed in CSV (Admin→Finance) → DN recalculated with new OU, LDAP move operation executed |
| 3 | **Leaver** | User removed from CSV → deprovisioned from AD (respecting deletion rules) |
| 4 | **Reconnection** | User re-added to CSV within grace period → scheduled deletion cancelled |

**Script**: `test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1`

**Execution Model**:

Each test step is triggered via a `-Step` parameter. This allows JIM to complete its import/sync/export cycle between steps:

```powershell
# Step 1: Joiner - Add user to HR CSV, trigger JIM sync, verify in AD
./Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step Joiner -Template Small

# Step 2a: Mover - Modify user attributes in CSV, verify changes in AD
./Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step Mover -Template Small

# Step 2b: Mover-Rename - Change user name, verify DN rename in AD
./Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step Mover-Rename -Template Small

# Step 2c: Mover-Move - Change display name, verify LDAP move operation
./Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step Mover-Move -Template Small

# Step 3: Leaver - Remove user from CSV, verify deprovisioned in AD
./Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step Leaver -Template Small

# Step 4: Reconnection - Re-add user before grace period, verify preserved
./Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step Reconnection -Template Small

# Run all steps sequentially (waits for JIM between each)
./Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step All -Template Small
```

**Step Details**:

| Parameter | Action | Verification |
|-----------|--------|--------------|
| `-Step Joiner` | Creates test user(s) in HR CSV | User exists in AD with correct attributes |
| `-Step Mover` | Modifies title in CSV | Title attribute updated in AD (no DN change) |
| `-Step Mover-Rename` | Changes user name in CSV | DN renamed in AD (CN component changed) |
| `-Step Mover-Move` | Changes department (Admin→Finance) | User moved from OU=Admin to OU=Finance via LDAP move operation |
| `-Step Leaver` | Removes user from HR CSV | User disabled/deleted in AD per deletion rules |
| `-Step Reconnection` | Re-adds user to CSV | Scheduled deletion cancelled, user remains active |
| `-Step All` | Runs all steps sequentially | Full lifecycle validated |

The `-Step All` option includes built-in waits and JIM Run Profile triggers between steps to automate the full test cycle.

---

#### Scenario 2: Person Entity - Cross-domain Synchronisation

**Purpose**: Validate unidirectional synchronisation of person entities between two directory services.

**Systems**:
- Source: Quantum Dynamics APAC (authoritative)
- Target: Quantum Dynamics EMEA

**Test Steps** (executed sequentially):

| Step | Test Case | Description |
|------|-----------|-------------|
| 1 | **Provision** | User created in Source AD → provisioned to Target AD |
| 2 | **ForwardSync** | Attributes changed in Source AD → flow to Target AD |
| 3 | **DetectDrift** | Attributes manually changed in Target AD → JIM detects drift |
| 4 | **ReassertState** | JIM reasserts expected state from Source AD to Target AD |

**Script**: `test/integration/scenarios/Invoke-Scenario2-CrossDomainSync.ps1`

**Execution Model**:

```powershell
# Individual steps
./Invoke-Scenario2-CrossDomainSync.ps1 -Step Provision -Template Small
./Invoke-Scenario2-CrossDomainSync.ps1 -Step ForwardSync -Template Small
./Invoke-Scenario2-CrossDomainSync.ps1 -Step DetectDrift -Template Small
./Invoke-Scenario2-CrossDomainSync.ps1 -Step ReassertState -Template Small

# Run all steps sequentially
./Invoke-Scenario2-CrossDomainSync.ps1 -Step All -Template Small
```

---

#### Scenario 3: Person Entity - GALSYNC (Global Address List Synchronisation)

**Purpose**: Validate exporting directory users to CSV for distribution/reporting.

**Systems**:
- Source: Subatomic AD
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

#### Scenario 4: MVO Deletion Rules and Deprovisioning

**Purpose**: Validate MVO deletion rules including grace periods, reconnection, admin protection, and scope filter changes.

**Systems**:
- Source: CSV (HR system)
- Target: Subatomic AD

**Test Steps** (executed sequentially):

| Step | Test Case | Description |
|------|-----------|-------------|
| 1 | **LeaverGracePeriod** | User removed from CSV → MVO enters grace period, not immediately deleted |
| 2 | **Reconnection** | User re-added within grace period → MVO preserved, grace period cleared |
| 3 | **SourceDeletion** | Authoritative source record deleted → triggers MVO deletion rule processing |
| 4 | **AdminProtection** | Admin accounts with Origin=Internal → protected from auto-deletion |
| 5 | **InboundScopeFilter** | Scoping criteria on import sync rule → filters CSOs by department |
| 6 | **OutboundScopeFilter** | Scoping criteria on export sync rule → filters MVOs for export |

**Script**: `test/integration/scenarios/Invoke-Scenario4-DeletionRules.ps1`

**Execution Model**:

```powershell
# Individual steps
./Invoke-Scenario4-DeletionRules.ps1 -Step LeaverGracePeriod -Template Small
./Invoke-Scenario4-DeletionRules.ps1 -Step Reconnection -Template Small
./Invoke-Scenario4-DeletionRules.ps1 -Step SourceDeletion -Template Small
./Invoke-Scenario4-DeletionRules.ps1 -Step AdminProtection -Template Small
./Invoke-Scenario4-DeletionRules.ps1 -Step InboundScopeFilter -Template Small
./Invoke-Scenario4-DeletionRules.ps1 -Step OutboundScopeFilter -Template Small

# Run all steps sequentially
./Invoke-Scenario4-DeletionRules.ps1 -Step All -Template Small
```

---

#### Scenario 5: Matching Rules and Join Logic

**Purpose**: Validate matching rules for joining CSOs to existing MVOs based on configurable criteria.

**Systems**:
- Source: CSV (HR system) with `hrId` (GUID) as external ID
- Target: Subatomic AD

**Test Steps** (executed sequentially):

| Step | Test Case | Description | Status |
|------|-----------|-------------|--------|
| 1 | **Projection** | New CSO with unique employeeId → projects to new MVO | ✅ Passing |
| 2 | **Join** | CSO with matching employeeId → joins existing MVO (no duplicate created) | ✅ Passing |
| 3 | **DuplicatePrevention** | Two CSV rows with same hrId → BOTH rejected with `DuplicateObject` error | ✅ Passing |
| 4 | **MultipleRules** | First rule doesn't match → falls back to secondary matching rule | ⏳ Run separately |
| 5 | **JoinConflict** | Two CSOs with different hrIds but same employeeId → `CouldNotJoinDueToExistingJoin` error | ✅ Passing |

**Script**: `test/integration/scenarios/Invoke-Scenario5-MatchingRules.ps1`

**Key Design**:
- Uses `hrId` (GUID format) as the CSV external ID instead of `employeeId`
- This separates the external ID (for CSO identity) from the matching attribute (employeeId)
- Enables testing both import deduplication (same hrId) and sync join conflict (same employeeId, different hrId)

**Same-Batch Import Deduplication** (Fixed in Issue #280):
When two CSV rows with identical external IDs are processed in the same import batch, JIM detects the duplicate and rejects BOTH objects with a `DuplicateObject` error. This "error both" approach ensures no "random winner" based on file order - the data owner must fix the source data.

**Execution Model**:

```powershell
# Run passing tests (Projection, Join, JoinConflict)
./Invoke-Scenario5-MatchingRules.ps1 -Step All -Template Small

# Individual steps
./Invoke-Scenario5-MatchingRules.ps1 -Step Projection -Template Small
./Invoke-Scenario5-MatchingRules.ps1 -Step Join -Template Small
./Invoke-Scenario5-MatchingRules.ps1 -Step JoinConflict -Template Small

# Test duplicate detection (now runs in All mode)
./Invoke-Scenario5-MatchingRules.ps1 -Step DuplicatePrevention -Template Small

# Test multiple matching rules (complex test - run separately)
./Invoke-Scenario5-MatchingRules.ps1 -Step MultipleRules -Template Small
```

---

### Phase 1 (MVP) - Entitlement Management Scenarios

These scenarios test group management capabilities - a core ILM function where the system manages group memberships based on identity attributes.

#### Scenario 6: Entitlement Management - JIM to AD ⏸️ DEFERRED

> **Status**: ⏸️ **DEFERRED** - This scenario requires proper design and implementation of Internally-managed MVOs (Metaverse Objects created within JIM rather than imported from a Connected System). Deferred until Internal MVO support is designed and implemented.

**Purpose**: Validate JIM as the authoritative source for entitlement groups, provisioning them to AD with membership derived from person attributes.

**Concept**: JIM creates and manages role-based groups (e.g., department groups, company groups, job title groups). Group membership is calculated from person attributes in the metaverse. Groups are provisioned to AD, and JIM detects and corrects any unauthorised changes made directly in AD.

**Systems**:
- Source: JIM Metaverse (groups created via JIM API, not imported from a Connected System)
- Target: Subatomic AD (OU=Entitlements,OU=Groups,OU=Corp,DC=subatomic,DC=local)

**Group Types Created**:
- **Department Groups**: `Dept-{Department}` (e.g., `Dept-Finance`, `Dept-Information Technology`)
- **Company Groups**: `Company-{Company}` (e.g., `Company-Subatomic`, `Company-Nexus Dynamics`)
- **Job Title Groups**: `Role-{Title}` (e.g., `Role-Manager`, `Role-Analyst`)

**Test Steps** (executed sequentially):

| Step | Test Case | Description |
|------|-----------|-------------|
| 1 | **CreateGroups** | Groups created in JIM via API → provisioned to AD with calculated membership |
| 2 | **UpdateMembership** | User department changes in HR → membership updated (removed from old group, added to new) |
| 3 | **DetectDrift** | Admin manually adds/removes member in AD → JIM detects drift on next sync |
| 4 | **ReassertState** | JIM reasserts expected membership, overwriting unauthorised AD changes |
| 5 | **DeleteGroup** | Group deleted from JIM MV → group deleted from AD |
| 6 | **DeleteMember** | User deleted from JIM MV → user removed from all group memberships in AD |

**Script**: `test/integration/scenarios/Invoke-Scenario6-EntitlementJIMToAD.ps1`

**Execution Model**:

```powershell
# Individual steps
./Invoke-Scenario6-EntitlementJIMToAD.ps1 -Step CreateGroups -Template Small
./Invoke-Scenario6-EntitlementJIMToAD.ps1 -Step UpdateMembership -Template Small
./Invoke-Scenario6-EntitlementJIMToAD.ps1 -Step DetectDrift -Template Small
./Invoke-Scenario6-EntitlementJIMToAD.ps1 -Step ReassertState -Template Small
./Invoke-Scenario6-EntitlementJIMToAD.ps1 -Step DeleteGroup -Template Small
./Invoke-Scenario6-EntitlementJIMToAD.ps1 -Step DeleteMember -Template Small

# Run all steps sequentially
./Invoke-Scenario6-EntitlementJIMToAD.ps1 -Step All -Template Small
```

---

#### Scenario 7: Entitlement Management - Convert AD Group Authority to JIM ⏸️ DEFERRED

> **Status**: ⏸️ **DEFERRED** - This scenario requires proper design and implementation of Internally-managed MVOs. After import, groups would need to be marked as JIM-authoritative (Internal origin), which requires the same Internal MVO support as Scenario 6. Deferred until Internal MVO support is designed and implemented.

**Purpose**: Validate importing existing AD groups into JIM and converting authority so JIM becomes the authoritative source. After conversion, any changes made directly in AD are overwritten by JIM.

**Concept**: Organisations often have existing groups in AD that were created manually or by other tools. This scenario tests bringing those groups under JIM management, making JIM authoritative for their membership.

**Systems**:
- Source: Subatomic AD (existing groups in OU=Legacy Groups,OU=Groups,OU=Corp)
- Target: JIM Metaverse (becomes authoritative after import)
- Export Target: Subatomic AD (same groups, now JIM-managed)

**Test Steps** (executed sequentially):

| Step | Test Case | Description |
|------|-----------|-------------|
| 1 | **ImportGroups** | Existing AD groups imported into JIM metaverse with current membership |
| 2 | **ConvertAuthority** | Groups marked as JIM-authoritative (export sync rule enabled) |
| 3 | **UpdateViaJIM** | Membership changed via JIM API → changes exported to AD |
| 4 | **DetectDrift** | Admin manually modifies group in AD → JIM detects drift |
| 5 | **ReassertState** | JIM overwrites AD changes, reasserting JIM-managed membership |

**Script**: `test/integration/scenarios/Invoke-Scenario7-ConvertADGroupAuthority.ps1`

**Execution Model**:

```powershell
# Individual steps
./Invoke-Scenario7-ConvertADGroupAuthority.ps1 -Step ImportGroups -Template Small
./Invoke-Scenario7-ConvertADGroupAuthority.ps1 -Step ConvertAuthority -Template Small
./Invoke-Scenario7-ConvertADGroupAuthority.ps1 -Step UpdateViaJIM -Template Small
./Invoke-Scenario7-ConvertADGroupAuthority.ps1 -Step DetectDrift -Template Small
./Invoke-Scenario7-ConvertADGroupAuthority.ps1 -Step ReassertState -Template Small

# Run all steps sequentially
./Invoke-Scenario7-ConvertADGroupAuthority.ps1 -Step All -Template Small
```

---

#### Scenario 8: Entitlement Management - Cross-domain Entitlement Synchronisation

**Purpose**: Validate synchronising entitlement groups between two AD domains, with one domain authoritative for groups.

**Concept**: In multi-domain environments, groups may need to be replicated across domains. This scenario tests importing groups from AD1 (authoritative) and exporting them to AD2, ensuring AD2 groups mirror AD1.

**Systems**:
- Source: Quantum Dynamics APAC (OU=Entitlements,OU=SourceCorp - authoritative for groups)
- Target: Quantum Dynamics EMEA (OU=Entitlements,OU=TargetCorp - replica of source groups)

**Important**: Each domain uses dedicated OUs to avoid conflicts with other scenarios.

**Test Steps** (executed sequentially):

| Step | Test Case | Description |
|------|-----------|-------------|
| 1 | **InitialSync** | Groups and membership imported from AD1 → provisioned to AD2 |
| 2 | **ForwardSync** | Group membership changed in AD1 → changes flow to AD2 |
| 3 | **DetectDrift** | Admin manually modifies group in AD2 → JIM detects drift |
| 4 | **ReassertState** | JIM reasserts AD1 membership to AD2, overwriting AD2 changes |
| 5 | **NewGroup** | New group created in AD1 → provisioned to AD2 |
| 6 | **DeleteGroup** | Group deleted from AD1 → deleted from AD2 |

**Script**: `test/integration/scenarios/Invoke-Scenario8-CrossDomainEntitlementSync.ps1`

**Execution Model**:

```powershell
# Individual steps
./Invoke-Scenario8-CrossDomainEntitlementSync.ps1 -Step InitialSync -Template Small
./Invoke-Scenario8-CrossDomainEntitlementSync.ps1 -Step ForwardSync -Template Small
./Invoke-Scenario8-CrossDomainEntitlementSync.ps1 -Step DetectDrift -Template Small
./Invoke-Scenario8-CrossDomainEntitlementSync.ps1 -Step ReassertState -Template Small
./Invoke-Scenario8-CrossDomainEntitlementSync.ps1 -Step NewGroup -Template Small
./Invoke-Scenario8-CrossDomainEntitlementSync.ps1 -Step DeleteGroup -Template Small

# Run all steps sequentially
./Invoke-Scenario8-CrossDomainEntitlementSync.ps1 -Step All -Template Small
```

---

### Phase 2 (Post-MVP) - Database Scenarios

#### Scenario 9: Multi-Source Aggregation

**Purpose**: Validate multiple database sources feeding the metaverse with join rules and attribute precedence.

**Systems**:
- Source 1: SQL Server (HRIS System A - Business Unit A)
- Source 2: Oracle Database (HRIS System B - Business Unit B)
- Target 1: Subatomic AD
- Target 2: CSV (Reporting)

**Test Steps** (executed sequentially):

| Step | Test Case | Description |
|------|-----------|-------------|
| 1 | **InitialLoad** | Both sources → metaverse → both targets |
| 2 | **JoinRules** | Matching employeeID across sources → single metaverse object |
| 3 | **Precedence** | SQL Server authoritative for email/phone, Oracle for department/title |
| 4 | **DataTypes** | VARCHAR, NVARCHAR, DATE, DATETIME, INT, BIT → correct mapping |

**Script**: `test/integration/scenarios/Invoke-Scenario9-MultiSourceAggregation.ps1`

**Execution Model**:

```powershell
# Individual steps
./Invoke-Scenario9-MultiSourceAggregation.ps1 -Step InitialLoad -Template Small
./Invoke-Scenario9-MultiSourceAggregation.ps1 -Step JoinRules -Template Small
./Invoke-Scenario9-MultiSourceAggregation.ps1 -Step Precedence -Template Small
./Invoke-Scenario9-MultiSourceAggregation.ps1 -Step DataTypes -Template Small

# Run all steps sequentially
./Invoke-Scenario9-MultiSourceAggregation.ps1 -Step All -Template Small
```

---

#### Scenario 10: Database Source/Target

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

**Script**: `test/integration/scenarios/Invoke-Scenario10-DatabaseSourceTarget.ps1`

**Execution Model**:

```powershell
# Individual steps
./Invoke-Scenario10-DatabaseSourceTarget.ps1 -Step Import -Template Small
./Invoke-Scenario10-DatabaseSourceTarget.ps1 -Step Export -Template Small
./Invoke-Scenario10-DatabaseSourceTarget.ps1 -Step DataTypes -Template Small
./Invoke-Scenario10-DatabaseSourceTarget.ps1 -Step MultiValue -Template Small

# Run all steps sequentially
./Invoke-Scenario10-DatabaseSourceTarget.ps1 -Step All -Template Small
```

---

#### Scenario 11: Performance Baselines

**Purpose**: Establish performance characteristics at various scales.

**Systems**: All (depending on scenario)

**Test Coverage**:
1. Run each scenario at each template scale
2. Measure import, sync, export times
3. Measure memory consumption
4. Identify bottlenecks
5. Establish acceptable thresholds

**Script**: `test/integration/scenarios/Invoke-Scenario9-Performance.ps1`

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
$adSystem = New-JIMConnectedSystem -Name "Subatomic AD" `
    -ConnectorType "LDAP" `
    -Configuration @{
        Server = "samba-ad-primary"
        Port = 389
        BaseDN = "DC=subatomic,DC=local"
        BindDN = "CN=Administrator,CN=Users,DC=subatomic,DC=local"
        BindPassword = "Test@123!"
        UserContainer = "OU=Users,OU=Corp,DC=subatomic,DC=local"
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
./test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1 -Template Small

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
./test/integration/scenarios/Invoke-Scenario2-CrossDomainSync.ps1 -Template Medium

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
7. **Selective Attribute Selection**: Only select attributes that are actually needed for sync flows (see below)

### Development Guidelines

#### No Direct SQL for JIM Configuration

**CRITICAL REQUIREMENT**: Integration test scripts must **NEVER** use direct SQL queries to configure JIM or manipulate its database for test setup/teardown. All JIM configuration must be performed through:

1. **REST API** (`/api/v1/...` endpoints)
2. **PowerShell Module** (`JIM.PowerShell` cmdlets)

**Rationale**:
- Direct SQL bypasses business logic, validation, and audit trails
- SQL-based workarounds create technical debt and hide API gaps
- Tests should validate the same interfaces that users and administrators use
- API/PowerShell gaps discovered during testing are valuable feedback

**When API/PowerShell Gaps Are Identified**:

If a test scenario requires functionality not exposed via API or PowerShell:

1. **STOP** - Do not work around with SQL
2. **Document** - Record the missing functionality in the test documentation
3. **Ask** - Consult with the user/team on how to proceed
4. **Assume** - The default assumption is that we will implement the missing API/PowerShell functionality

**Example of Blocked Scenario**:

Scenario 2 (Directory-to-Directory) was initially blocked because no API existed for partition selection. Following this process:
1. Documented the blocking issue in this document
2. Created GitHub issue #191 for the required API endpoints
3. Implemented the missing API (partition management, hierarchy import)
4. Updated test scripts to use the new API

The scenario is now blocked by a different issue (LDAP connector object type matching bug) which was discovered through integration testing - exactly the kind of issue these tests are designed to find.

**Acceptable SQL Usage**:
- **External systems only**: Querying Samba AD, test databases, or other external systems being tested
- **Verification queries**: Read-only queries to verify JIM's internal state (not for setup/teardown)
- **Emergency debugging**: Temporary diagnostic queries during development (never committed)

#### Selective Attribute Selection

**BEST PRACTICE**: Integration test setup scripts should only select the attributes that are actually needed for sync flows, rather than selecting all available attributes from the imported schema.

**Rationale** (see [GitHub Issue #227](https://github.com/TetronIO/JIM/issues/227)):
- More representative of real-world ILM configurations
- Reduces unnecessary attribute metadata being loaded and stored
- Improves test performance by processing fewer attributes
- Better simulates how production administrators configure systems

**Implementation**:

Instead of selecting all attributes:
```powershell
# ❌ Don't do this - selects all attributes
$ldapAttrUpdates = @{}
foreach ($attr in $ldapUserType.attributes) {
    $ldapAttrUpdates[$attr.id] = @{ selected = $true }
}
```

Select only the attributes needed for your sync rules:
```powershell
# ✅ Do this - select only required attributes
$requiredLdapAttributes = @(
    'sAMAccountName',     # Account Name - required anchor
    'givenName',          # First Name
    'sn',                 # Last Name (surname)
    'displayName',        # Display Name
    'cn',                 # Common Name
    'mail',               # Email
    'userPrincipalName',  # UPN
    'title',              # Job Title
    'department',         # Department
    'distinguishedName'   # DN - required for LDAP provisioning
)

$ldapAttrUpdates = @{}
foreach ($attr in $ldapUserType.attributes) {
    if ($requiredLdapAttributes -contains $attr.name) {
        $ldapAttrUpdates[$attr.id] = @{ selected = $true }
    }
}
```

**Current Implementation**:
- **Scenario 1 (HR to AD)**: Selects 10 LDAP attributes for export (sAMAccountName, givenName, sn, displayName, cn, mail, userPrincipalName, title, department, distinguishedName)
- **Scenario 2 (Directory Sync)**: Selects 11 LDAP attributes for bidirectional sync (adds telephoneNumber for Phone attribute)

---

## Performance Diagnostics

JIM includes built-in performance diagnostics that automatically measure and log operation timings during sync operations. This is invaluable for identifying performance bottlenecks during integration testing.

### Performance Optimization Results

Following extensive optimization work (see [GitHub Issue #190](https://github.com/TetronIO/JIM/issues/190)), JIM now delivers exceptional synchronisation performance:

**Actual Performance (December 2024):**
- **Medium dataset (~1,000 users)**: 78 seconds total (import + sync + export)
  - FullSync: 34.5s (8 runs, avg 4.3s)
  - FullImport: 13.8s (8 runs, avg 1.7s)
  - Export: 30.1s (7 runs, avg 4.3s)
- **Performance improvement**: 192x faster than original baseline (4 users/minute → ~768 users/minute)

**Key Optimizations:**
1. Eliminated O(N×M) query complexity through pre-loaded caching
2. Batch database operations for Metaverse Objects
3. Query optimization with `.AsSplitQuery()` to prevent cartesian explosion
4. Pre-loaded lookup dictionaries for export rules and Connected System Objects

**Production Readiness:**
- ✅ All acceptance criteria exceeded (100 users < 60s, 1000 users < 5min)
- ✅ 98.8% improvement in FullSync operation time
- ✅ Production-ready for large-scale identity synchronisation workloads

### Automatic Enablement

Performance diagnostics are automatically enabled in:
- **Worker Service**: 100ms slow operation threshold (logs warnings for operations exceeding this)
- **Unit Tests**: 50ms slow operation threshold (via `GlobalTestSetup.cs` in test projects)

### Viewing Performance Metrics

**During Docker Stack Testing:**

```bash
# View worker logs with timing information
docker logs jim.worker 2>&1 | grep -i "DiagnosticListener"

# Follow logs in real-time during sync operations
docker logs -f jim.worker 2>&1 | grep -i "DiagnosticListener"

# View all worker logs (includes timing in context)
docker logs jim.worker --tail 200
```

**During Unit/Integration Tests:**

```powershell
# Run tests with verbose output to see timing info
dotnet test JIM.Worker.Tests --verbosity normal

# Run specific test with detailed output
dotnet test JIM.Worker.Tests --filter "FullyQualifiedName~SyncImport" --verbosity detailed
```

### Understanding Diagnostic Output

Diagnostic output appears in logs like:

```
DiagnosticListener: FullImport completed in 1234.56ms
DiagnosticListener: ImportPage completed in 45.23ms [pageNumber=1]
DiagnosticListener: ProcessImportObjects completed in 892.10ms [objectCount=100]
```

Operations exceeding the slow threshold are logged at **Warning** level for easy identification.

### Instrumented Operations

The following sync operations are instrumented:

| Operation | Description |
|-----------|-------------|
| `FullImport` | Complete import from a connected system |
| `CallBasedImport` | Import using connector API calls |
| `FileBasedImport` | Import from file-based connectors |
| `ImportPage` | Single page of paginated import |
| `ReadFile` | File connector file read |
| `ProcessImportObjects` | Processing imported objects |
| `ProcessDeletions` | Processing object deletions |
| `ResolveReferences` | Resolving object references |
| `PersistConnectedSystemObjects` | Database persistence |
| `FullSync` | Full synchronisation cycle |
| `LoadSyncRules` | Loading sync rules |
| `LoadObjectTypes` | Loading object types |
| `ProcessConnectedSystemObjects` | Processing CSOs during sync |
| `Export` | Export to connected system |
| `ExecuteExports` | Executing pending exports |

### Adding Custom Instrumentation

When adding new operations that should be timed:

```csharp
using JIM.Application.Diagnostics;

using var span = Diagnostics.Sync.StartSpan("MyNewOperation");
span.SetTag("relevantContext", contextValue);

try
{
    await PerformOperationAsync();
    span.SetSuccess();
}
catch (Exception ex)
{
    span.SetError(ex);
    throw;
}
```

### Path to OpenTelemetry

The diagnostics infrastructure uses `System.Diagnostics.ActivitySource` (the .NET OpenTelemetry API). To export telemetry to external systems like Jaeger, Zipkin, or Azure Monitor, add OpenTelemetry exporters without any instrumentation code changes. See [GitHub Issue #212](https://github.com/TetronIO/JIM/issues/212) for .NET Aspire evaluation.

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
│   ├── JIM.Web.Api.Tests/                                  # API unit tests
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
│       │   ├── Invoke-Scenario1-HRToIdentityDirectory.ps1
│       │   ├── Invoke-Scenario2-CrossDomainSync.ps1
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
| Subatomic AD     | 389            | 389       | LDAP     |
| Subatomic AD     | 636            | 636       | LDAPS    |
| Quantum Dynamics APAC | 389            | 10389     | LDAP     |
| Quantum Dynamics APAC | 636            | 10636     | LDAPS    |
| Quantum Dynamics EMEA | 389            | 11389     | LDAP     |
| Quantum Dynamics EMEA | 636            | 11636     | LDAPS    |
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

### Phase 1 Status (as of 2026-01-15) - ✅ COMPLETE

| Component | Status | Notes |
|-----------|--------|-------|
| Infrastructure | ✅ Complete | Samba AD, CSV file mounting, volume orchestration |
| API Endpoints | ✅ Complete | Schema management, sync rules, mappings, run profiles |
| PowerShell Module | ✅ Complete | All cmdlets for Scenario 1 |
| Setup-Scenario1.ps1 | ✅ Complete | Automated JIM configuration with deletion rules |
| Invoke-Scenario1 | ✅ Complete | All 6 tests passing (Joiner, Mover, Mover-Rename, Mover-Move, Leaver, Reconnection) |
| Scenario 2 | ✅ Complete | All 4 tests passing (Provision, ForwardSync, TargetImport, Conflict). Test 3 fixed to validate unidirectional sync. |
| Scenario 3 | ⏳ Pending | Placeholder script exists |
| Scenario 4 | ✅ Complete | Deletion rules - all tests passing |
| Scenario 5 | ✅ Complete | Matching rules - 4/5 tests passing, 1 run separately (MultipleRules requires specific setup) |
| Scenarios 6-7 | ⏸️ Deferred | Requires Internal MVO design (JIM-authoritative objects) |
| Scenario 8 | 🔄 In Progress | InitialSync & ForwardSync complete. Remaining: DetectDrift, ReassertState, NewGroup, DeleteGroup |
| Scenarios 9-11 | ⏳ Post-MVP | Database scenarios |
| GitHub Actions | ⏳ Pending | CI/CD workflow not yet created |

### Completed Fixes (2026-01-15)

1. **Scenario 2 TargetImport test** - Fixed test logic to validate unidirectional sync behaviour. Test now correctly expects that objects created directly in Target AD should NOT project to Metaverse (because Target import rule has `ProjectToMetaverse=false`). This is the intended design where Source is authoritative.

2. **Import summary statistics** - Fixed misleading error count in `ProcessImportObjectsAsync` summary. RPEIs with `ErrorType = NotSet` were incorrectly counted as errors. Now correctly treats both `null` and `NotSet` as successful.

### Completed Fixes (2026-01-13)

1. **Scenario 8 reference attribute resolution** - Fixed export of `member` and `managedBy` attributes to LDAP systems. References are now resolved to the secondary external ID (Distinguished Name) instead of the primary external ID (objectGUID). This ensures LDAP syntax compliance for DN-type attributes.

2. **Scenario 8 confirming import reconciliation** - Fixed `PendingExportReconciliationService` to process pending exports with `ExportNotImported` status and attribute changes with `ExportedNotConfirmed` status. Previously, these were skipped during confirming imports, leaving exports permanently unconfirmed.

3. **Issue #287 created** - Documented need for pending export visibility improvements (sync should surface unconfirmed exports, confirming import should report confirmation stats).

### Completed Fixes (2026-01-10)

1. **Scenario 5 hrId external ID** - Changed CSV external ID from `employeeId` to `hrId` (GUID format) to properly test both import deduplication and sync join conflicts. This separates the CSO identity (hrId) from the matching attribute (employeeId).

2. **Test 5 JoinConflict** - Added new test that verifies `CouldNotJoinDueToExistingJoin` error when two CSOs with different external IDs (hrId) but the same matching attribute (employeeId) try to join the same MVO.

3. **Known limitation documented** - Same-batch import deduplication bug (Issue #280) was discovered. Test 3 was skipped in "All" mode pending fix.

4. **Terminology standardisation** - Replaced "Connector Space Object" with "Connected System Object" in error messages and comments throughout the codebase.

### Completed Fixes (2025-12-21)

1. **DN column removal from CSV** - Removed hardcoded DN column from CSV test data generation across all scenario files. DN is now calculated dynamically by export sync rule expressions (`"CN=" + SamAccountName + ",OU=" + Department + ",..."`).

2. **Deletion rules configuration** - Added Step 6d to `Setup-Scenario1.ps1` to configure deletion rules on the User metaverse object type with `WhenLastConnectorDisconnected` rule and 7-day grace period.

3. **Reconnection test fix** - Fixed test user property overrides. `New-TestUser -Index 8888` generates deterministic values (Ian, Jones, etc.), but test was only overriding EmployeeId and SamAccountName. Added complete property overrides for Email, FirstName, LastName, Department, and Title.

4. **Leaver test expectations** - Updated Leaver test to expect user to still exist in AD during grace period with clear messaging about 7-day grace period behaviour.

5. **Test isolation** - Documented that full environment reset (`docker compose down -v`) is required between test runs to avoid orphaned Metaverse Objects from previous runs causing matching failures.

### Previously Fixed (2025-12-16)

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
pwsh test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1 -ApiKey "jim_ak_xxx" -Template Micro

# 6. Check logs if tests fail
docker logs jim.worker --tail 100
docker logs jim.web --tail 100
```

### Files Modified in This Branch

**New Files:**
- `test/JIM.Web.Api.Tests/SynchronisationControllerSchemaTests.cs` - 14 tests
- `test/JIM.Web.Api.Tests/MetaverseControllerAttributeTests.cs` - 18 tests
- `test/JIM.Web.Api.Tests/SynchronisationControllerMappingTests.cs` - 11 tests

**Bug Fixes:**
- `JIM.Application/Servers/ConnectedSystemServer.cs` - Activity.ConnectedSystemId for UPDATE operations
- `JIM.Web/Controllers/Api/MetaverseController.cs` - Collection initialisation, eager loading for updates
- `JIM.Models/Staging/ConnectedSystemObject.cs` - DisplayNameOrId FirstOrDefault fix
- `JIM.Data/Repositories/IMetaverseRepository.cs` - Added GetMetaverseAttributeWithObjectTypesAsync
- `JIM.PostgresData/Repositories/MetaverseRepository.cs` - Implemented GetMetaverseAttributeWithObjectTypesAsync
- `JIM.PostgresData/Repositories/ConnectedSystemRepository.cs` - Added .Include() for AttributeValues in CSO retrieval methods

**Integration Test Improvements:**
- `test/integration/Setup-Scenario1.ps1` - Fixed API response property names (metaverseObjectTypes)
- `test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1` - Added CSV reset and AD cleanup for repeatable tests

### Next Steps

1. ~~**Debug sync engine export**~~ - ✅ Fixed! Users now provisioned to AD successfully
2. ~~**Fix file connector change detection**~~ - ✅ Fixed! All Scenario 1 tests now passing
3. ~~**Fix Scenario 2 duplicate attribute bug**~~ - ✅ Fixed! PR #279 - uses objectGUID as external ID
4. **Test and validate Scenario 2** - Run full cross-domain sync tests
5. **Complete Scenario 3** - GALSYNC (AD to CSV export)
6. **Create GitHub Actions workflow** - Automate integration tests in CI/CD

### Scenario 2 Status: Ready for Testing

**Status**: 🔧 Ready for Testing

**Previous Issue**: Scenario 2 (Directory-to-Directory) was failing with error: `Sequence contains more than one matching element`

**Root Cause & Fix (PR #279)**: The error was caused by duplicate CSO attribute values being created:

1. **Export storing value with wrong type**: `ExportExecutionServer.UpdateCsoAfterSuccessfulExportAsync` was blindly trying to parse external ID as GUID and storing in `GuidValue`, regardless of the attribute's actual data type. Fixed by looking up the attribute type before storing.

2. **Incorrect external ID configuration**: Setup-Scenario2.ps1 was using `sAMAccountName` (Text type) as the external ID. This is incorrect because `sAMAccountName` can change (user renames). Fixed by changing to `objectGUID` which is immutable and the correct AD anchor.

**Files Available**:
- `test/integration/Setup-Scenario2.ps1` - JIM configuration for directory sync
- `test/integration/scenarios/Invoke-Scenario2-CrossDomainSync.ps1` - Test execution script

**To Run Scenario 2**:
```powershell
./test/integration/Run-IntegrationTests.ps1 -Scenario "Scenario2-CrossDomainSync" -Template Nano
```

### Resolved Issue: LDAP Partition Management API Missing

**Status**: ✅ RESOLVED (2025-12-16)

**Original Symptom**: Scenario 2 setup completed but imports returned no objects because partitions were not selected.

**Fix Applied**: Implemented partition and container management API:
- `GET /api/v1/synchronisation/connected-systems/{id}/partitions` - List partitions
- `PUT /api/v1/synchronisation/connected-systems/{id}/partitions/{partitionId}` - Update partition selection
- `PUT /api/v1/synchronisation/connected-systems/{id}/containers/{containerId}` - Update container selection
- `POST /api/v1/synchronisation/connected-systems/{id}/import-hierarchy` - Import hierarchy from LDAP

**PowerShell Cmdlets Added**:
- `Get-JIMConnectedSystemPartition` - List partitions
- `Set-JIMConnectedSystemPartition` - Update partition selection
- `Set-JIMConnectedSystemContainer` - Update container selection
- `Import-JIMConnectedSystemHierarchy` - Import hierarchy from connector

**Commit**: 9d7445f - feat(api): Add hierarchy import endpoint and PowerShell cmdlet

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
| 2.3     | 2026-01-10 | Fixed Issue #280: Same-batch import deduplication. When duplicate external IDs detected in same batch, BOTH objects rejected with `DuplicateObject` error. Added unit tests. Test 3 now runs in All mode. |
| 2.2     | 2026-01-10 | Scenario 5 matching rules complete. Added hrId (GUID) as external ID, Test 5 JoinConflict verifies CouldNotJoinDueToExistingJoin error. Documented same-batch import deduplication limitation (#280). |
| 2.1     | 2026-01-10 | Scenario 2 blocker fixed (PR #279). Export now stores external ID with correct data type. Setup-Scenario2.ps1 updated to use objectGUID as external ID instead of sAMAccountName. |
| 2.0     | 2025-12-21 | All 6 Scenario 1 tests passing. Fixed DN column removal (now expression-calculated), deletion rules configuration, Reconnection test property overrides, and Leaver test expectations for grace period. |
| 1.9     | 2025-12-16 | Resolved partition API blocking issue. Added partition/container management API and PowerShell cmdlets. Discovered LDAP connector object type matching bug (new blocker). |
| 1.8     | 2025-12-16 | Added Scenario 2 scripts (Setup-Scenario2.ps1, Invoke-Scenario2-CrossDomainSync.ps1). Documented blocking issue - LDAP partition management API needed. |
| 1.7     | 2025-12-16 | **Phase 1 Complete!** All Scenario 1 tests passing. Fixed file connector change detection (missing .Include() calls). Added test data reset and AD cleanup for repeatable tests. |
| 1.6     | 2025-12-16 | Ran full Scenario 1 tests, documented file connector change detection issue |
| 1.5     | 2025-12-16 | Scenario 1 Joiner test passing, added Nano template, multiple bug fixes |
| 1.4     | 2025-12-16 | Added Current Progress section, known issues, quick start guide |
| 1.3     | 2025-12-13 | Added Test Lifecycle Quick Reference section for DevContainer and CI/CD |
| 1.2     | 2025-12-09 | Added JIM configuration section, step-based execution, dependencies |
| 1.1     | 2025-12-08 | Updated file paths to use existing test/ folder |
| 1.0     | 2025-12-08 | Initial version - Phase 1 & 2 specification     |

---

## Known Issues & TODOs

### Infrastructure Improvements Needed

#### 1. Progress Bars for Test Execution
**Priority: Medium | Status: ✅ Complete**

Progress bar helper functions implemented in `Test-Helpers.ps1`:
- `Write-ProgressBar`: Visual progress bar with percentage, elapsed time, and ETA
- `Complete-ProgressBar`: Complete progress bar with success/failure indicator
- `Start-TimedOperation`: Start tracking a timed operation
- `Update-OperationProgress`: Update progress for a timed operation
- `Complete-TimedOperation`: Complete operation and show final duration
- `Write-Spinner`: Spinner animation for indeterminate progress

**Example output:**
```
[████████████████████░░░░] 80% | Elapsed: 8.2s | ETA: 2.1s
```

See [#196](https://github.com/TetronIO/JIM/issues/196) for implementation details.

#### 2. Docker Compose Files for Test Infrastructure
**Priority: High | Status: ✅ Complete**

`docker-compose.integration-tests.yml` exists in project root with:
- ✅ Samba AD (Primary instance) - for Scenario 1
- ✅ Samba AD (Source & Target instances) - for Scenario 2 (profile: scenario2)
- ✅ Test data volumes for CSV files
- ✅ Profile-based service selection
- ✅ Phase 2 services (SQL Server, Oracle, PostgreSQL, MySQL, OpenLDAP)

#### 3. Stand-up Performance Optimization
**Priority: Medium | Status: ✅ Complete**

**Implemented Optimisations:**
- ✅ **Pre-built Samba AD images**: Domain provisioning is done at image build time, reducing startup from ~5 minutes to ~30 seconds
- ✅ **Multi-architecture support**: Uses `diegogslomp/samba-ad-dc` base image which provides native ARM64 support (no Rosetta emulation on Apple Silicon Macs)
- ✅ **Automatic architecture detection**: Docker automatically pulls the correct image variant for AMD64 or ARM64

**Base Image Details:**
- Base image: `diegogslomp/samba-ad-dc:latest` (supports linux/amd64 and linux/arm64)
- Pre-built images: `ghcr.io/tetronio/jim-samba-ad:{primary,source,target}`
- Samba binaries location: `/usr/local/samba/bin/`

**Automatic Image Building:**
The `Run-IntegrationTests.ps1` script automatically builds the custom Samba AD image if it doesn't exist locally. This happens on first run and takes ~30 seconds. Subsequent runs use the cached image for fast startup (~10 seconds).

**To manually rebuild pre-built images (required after base image updates):**
```powershell
pwsh test/integration/docker/samba-ad-prebuilt/Build-SambaImages.ps1 -Images All
```

**Baseline Timings:**
- JIM stack cold start: ~19s
- JIM stack warm start (after code change): ~19s (measured)
- Samba AD cold start (pre-built image): ~30s
- Samba AD cold start (standard image): ~5 minutes
- CSV file generation (Nano): <1s (measured)

#### 4. Test Data Reset Automation
**Priority: Low | Status: Partial**

**Current State:**
- CSV reset works (`Generate-TestCSV.ps1`)
- AD cleanup attempts but often fails (users don't exist)
- JIM database reset requires full stack down/up

**Improvements Needed:**
- Silent cleanup (suppress "user not found" warnings)
- Faster JIM reset without full stack restart
- Automated metaverse purge API endpoint for testing

### Documentation Improvements

#### 5. Quick Start Consolidation
**Priority: High | Status: Done (2025-12-17)**

✅ Added Quick Start section to top of INTEGRATION_TESTING.md with:
- Step-by-step first-run instructions
- Prerequisites checklist
- Current limitations clearly stated
- Direct commands to copy-paste

---

## Workflow Tests vs Integration Tests

For many debugging and development scenarios, **Workflow Tests** provide a faster alternative to full integration tests:

| Aspect | Workflow Tests | Integration Tests |
|--------|---------------|-------------------|
| **Execution time** | Seconds | Minutes to hours |
| **External dependencies** | None (in-memory DB, mock connectors) | Real systems (Samba AD, databases) |
| **State inspection** | Snapshots after each step | Limited to logs and queries |
| **Debugging** | Easy - run in IDE with breakpoints | Complex - Docker containers |
| **Scale testing** | 1000+ objects in seconds | Depends on external system performance |
| **Use case** | Logic debugging, regression testing | End-to-end validation, production readiness |

**When to use Workflow Tests:**
- Debugging sync logic issues (e.g., issue #234)
- Testing multi-step scenarios (import → sync → export → confirming import)
- Validating state transitions (CSO status changes, PendingExport reconciliation)
- Fast iteration during development

**When to use Integration Tests:**
- Validating real connector behaviour (LDAP, CSV, database)
- Testing with real external systems
- Pre-release validation
- Performance baseline measurement

See [Developer Guide - Workflow Tests](DEVELOPER_GUIDE.md#workflow-tests) for details on writing and running workflow tests.

---

## Related Documentation

- [MVP Definition](MVP_DEFINITION.md) - Overall project status
- [Developer Guide](DEVELOPER_GUIDE.md) - Development setup and architecture
- [GitHub Issue #173](https://github.com/TetronIO/JIM/issues/173) - Integration Testing Framework tracking issue
- [GitHub Issue #170](https://github.com/TetronIO/JIM/issues/170) - SQL Database Connector (Phase 2 dependency)
- [GitHub Issue #175](https://github.com/TetronIO/JIM/issues/175) - API Key Authentication (required for non-interactive testing)
- [GitHub Issue #176](https://github.com/TetronIO/JIM/issues/176) - PowerShell Module (required for JIM configuration)
- [GitHub Issue #238](https://github.com/TetronIO/JIM/issues/238) - Workflow Test Framework
