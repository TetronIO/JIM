# Integration Testing Framework

> **Status**: Planned
> **Phase 1 Target**: MVP Validation
> **Phase 2 Target**: Post-MVP (after Database Connector #170)
> **Related Issue**: [#173](https://github.com/TetronIO/JIM/issues/173)

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Data Scale Templates](#data-scale-templates)
4. [Test Scenarios](#test-scenarios)
5. [Setup & Configuration](#setup--configuration)
6. [Running Tests Locally](#running-tests-locally)
7. [CI/CD Integration](#cicd-integration)
8. [Writing New Scenarios](#writing-new-scenarios)
9. [Troubleshooting](#troubleshooting)

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
- **Scalable**: Template-based data sets from 10 to 1M objects
- **Phased**: Phase 1 (MVP) uses LDAP/CSV; Phase 2 adds databases
- **Opt-In**: Manual trigger only, not automatic on every commit

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
   └─> Create Connected Systems, Sync Rules, Run Profiles
       (via API or manual configuration)

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

**Test Coverage**:
1. **New Hire (Joiner)**: User in CSV → provisioned to AD with correct attributes and group memberships
2. **Leaver**: User removed from CSV → deprovisioned from AD (respecting deletion rules)
3. **Mover**: User department/title changed in CSV → attributes and groups updated in AD
4. **Reconnection**: User removed then re-added within grace period → deletion cancelled

**Script**: `tests/integration/scenarios/Invoke-Scenario1-HRToDirectory.ps1`

---

#### Scenario 2: Directory to Directory Synchronisation

**Purpose**: Validate bidirectional synchronisation between two directory services.

**Systems**:
- Source: Samba AD Source
- Target: Samba AD Target

**Test Coverage**:
1. User created in Source AD → provisioned to Target AD
2. Attributes changed in Source AD → flow to Target AD
3. Different attributes changed in Target AD → flow back to Source AD
4. Simultaneous changes to same user → conflict resolution applied

**Script**: `tests/integration/scenarios/Invoke-Scenario2-DirectorySync.ps1`

---

#### Scenario 3: GALSYNC (Global Address List Synchronisation)

**Purpose**: Validate exporting directory users to CSV for distribution/reporting.

**Systems**:
- Source: Samba AD Primary
- Target: CSV (GAL export)

**Test Coverage**:
1. Users in AD → exported to CSV with selected attributes only
2. User attributes modified in AD → CSV updated
3. User deleted in AD → removed from CSV

**Script**: `tests/integration/scenarios/Invoke-Scenario3-GALSYNC.ps1`

---

### Phase 2 (Post-MVP) - Databases

#### Scenario 4: Multi-Source Aggregation

**Purpose**: Validate multiple database sources feeding the metaverse with join rules and attribute precedence.

**Systems**:
- Source 1: SQL Server (HRIS System A - Business Unit A)
- Source 2: Oracle Database (HRIS System B - Business Unit B)
- Target 1: Samba AD Primary
- Target 2: CSV (Reporting)

**Test Coverage**:
1. **Initial Load**: Both sources → metaverse → both targets
2. **Join Rules**: Matching employeeID across sources → single metaverse object
3. **Attribute Precedence**: SQL Server authoritative for email/phone, Oracle for department/title
4. **Data Types**: VARCHAR, NVARCHAR, DATE, DATETIME, INT, BIT → correct mapping

**Script**: `tests/integration/scenarios/Invoke-Scenario4-MultiSourceAggregation.ps1`

---

#### Scenario 5: Database Source/Target

**Purpose**: Validate database connector import/export capabilities.

**Systems**:
- Source: SQL Server
- Target: PostgreSQL

**Test Coverage**:
1. Import users from SQL Server
2. Export users to PostgreSQL
3. Data type handling (text, numeric, date, boolean)
4. Multi-valued attributes (if supported)

**Script**: `tests/integration/scenarios/Invoke-Scenario5-DatabaseSourceTarget.ps1`

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

**Script**: `tests/integration/scenarios/Invoke-Scenario6-Performance.ps1`

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

---

## Running Tests Locally

### Quick Start - Single Scenario

```powershell
# Stand up Phase 1 systems
docker compose -f docker-compose.integration-tests.yml up -d

# Wait for systems to be ready (AD takes ~2 minutes to initialise)
Start-Sleep -Seconds 120

# Populate with Small template
./tests/integration/Populate-SambaAD.ps1 -Template Small -Instance Primary
./tests/integration/Generate-TestCSV.ps1 -Template Small

# Run Scenario 1
./tests/integration/scenarios/Invoke-Scenario1-HRToDirectory.ps1 -Template Small

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
./tests/integration/Populate-SambaAD.ps1 -Template Medium -Instance Source
./tests/integration/Populate-SambaAD.ps1 -Template Medium -Instance Target

# Run scenario
./tests/integration/scenarios/Invoke-Scenario2-DirectorySync.ps1 -Template Medium

# Tear down
docker compose -f docker-compose.integration-tests.yml --profile scenario2 down -v
```

### Running All Phase 1 Scenarios

```powershell
# Use master script
./tests/integration/Invoke-IntegrationTests.ps1 -Template Medium -Phase 1
```

This script will:
1. Stand up all Phase 1 systems
2. Populate data
3. Run all Phase 1 scenarios
4. Collect results to `tests/integration/results/`
5. Tear down systems
6. Display summary report

### Phase 2 (Database Scenarios)

Phase 2 requires the Database Connector (#170) to be complete:

```powershell
# Stand up Phase 2 systems
docker compose -f docker-compose.integration-tests.yml --profile phase2 up -d

# Wait for databases (Oracle can take 5-10 minutes)
./tests/integration/Wait-SystemsReady.ps1 -Phase 2

# Populate databases
./tests/integration/Populate-SqlServer.ps1 -Template Medium
./tests/integration/Populate-Oracle.ps1 -Template Medium
./tests/integration/Populate-PostgreSQL.ps1 -Template Medium

# Run Phase 2 scenarios
./tests/integration/Invoke-IntegrationTests.ps1 -Template Medium -Phase 2

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

Create reusable helpers in `tests/integration/utils/`:

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
├── tests/
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

## Version History

| Version | Date       | Changes                                      |
|---------|------------|----------------------------------------------|
| 1.0     | 2025-12-08 | Initial version - Phase 1 & 2 specification |

---

## Related Documentation

- [MVP Definition](MVP_DEFINITION.md) - Overall project status
- [Developer Guide](DEVELOPER_GUIDE.md) - Development setup and architecture
- [GitHub Issue #173](https://github.com/TetronIO/JIM/issues/173) - Integration Testing Framework tracking issue
- [GitHub Issue #170](https://github.com/TetronIO/JIM/issues/170) - SQL Database Connector (Phase 2 dependency)
