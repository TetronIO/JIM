# JIM Integration Testing

End-to-end integration tests for JIM against real external systems running in Docker containers.

## Quick Start

**Recommended: Single command to run everything**

```powershell
# From anywhere in the repo - runs full lifecycle
# Use Micro for Codespaces/CI, Small for local development
pwsh test/integration/Invoke-IntegrationTests.ps1 -Template Micro

# With debugging (keeps containers running after tests)
pwsh test/integration/Invoke-IntegrationTests.ps1 -Template Micro -SkipTearDown

# For minimal testing (3 users)
pwsh test/integration/Invoke-IntegrationTests.ps1 -Template Nano
```

This will automatically:
1. Reset any existing environment (clean slate)
2. Start Samba AD and JIM containers
3. Wait for systems to be healthy
4. Create infrastructure API key
5. Populate test data (users, groups, CSV files)
6. Run Scenario 1 (HR to Enterprise Directory)
7. Report results
8. Tear down containers and volumes

## Reset Environment

To reset JIM to a clean state (e.g., between test runs):

```powershell
# From repo root - tears down everything
./Reset-JIM.ps1

# Reset and restart immediately
./Reset-JIM.ps1 -Restart

# Skip confirmation (for CI/CD)
./Reset-JIM.ps1 -SkipConfirmation
```

## Manual Step-by-Step Testing

If you prefer more control:

```powershell
# 1. Reset environment
./Reset-JIM.ps1 -Restart

# 2. Set up infrastructure API key
pwsh test/integration/Setup-InfrastructureApiKey.ps1

# 3. Populate test data
pwsh test/integration/Populate-SambaAD.ps1 -Template Small -Instance Primary
pwsh test/integration/Generate-TestCSV.ps1 -Template Small

# 4. Run scenarios only (skips setup)
pwsh test/integration/Invoke-IntegrationTests.ps1 -ScenariosOnly

# 5. Clean up when done
./Reset-JIM.ps1
```

## Implementation Status

### Phase 1 Infrastructure
- **Docker Compose Configuration** - External systems containerised
- **Data Population Scripts** - Generate realistic test data
- **Health Checks** - Wait for systems to be ready
- **Test Utilities** - Helper functions for assertions and LDAP queries
- **Lifecycle Management** - Stand up, populate, reset, tear down
- **Infrastructure API Key** - Automatic setup for automation

### Phase 1 Scenarios
- **Scenario 1: HR to Enterprise Directory** - Joiner, Mover, Leaver, Reconnection
- **Scenario 2: Directory to Directory Sync** - Placeholder (not implemented)
- **Scenario 3: GALSYNC** - Placeholder (not implemented)

### Phase 2 (Planned)
- Database connector testing (requires [#170](https://github.com/TetronIO/JIM/issues/170))
- SQL Server, Oracle, PostgreSQL, MySQL connectors

## Available Scripts

| Script | Location | Purpose |
|--------|----------|---------|
| `Reset-JIM.ps1` | Repo root | Reset environment (tear down containers and volumes) |
| `Invoke-IntegrationTests.ps1` | test/integration/ | Master script - full test lifecycle |
| `Setup-InfrastructureApiKey.ps1` | test/integration/ | Create API key for automation |
| `Wait-SystemsReady.ps1` | test/integration/ | Wait for containers to be healthy |
| `Populate-SambaAD.ps1` | test/integration/ | Create users/groups in Samba AD |
| `Generate-TestCSV.ps1` | test/integration/ | Generate HR CSV files |
| `Setup-Scenario1.ps1` | test/integration/ | Configure JIM for Scenario 1 |
| `Invoke-Scenario1-HRToIdentityDirectory.ps1` | test/integration/scenarios/ | Run Scenario 1 tests (Joiner, Mover, Leaver, Reconnection) |

## Data Scale Templates

| Template | Users | Groups | Avg Memberships | Use Case |
|----------|-------|--------|-----------------|----------|
| **Nano** | 3 | 1 | 1 | Minimal testing, debugging |
| **Micro** | 10 | 3 | 3 | Quick smoke tests, Codespaces |
| **Small** | 100 | 20 | 5 | Small business, local development |
| **Medium** | 1,000 | 100 | 8 | Medium enterprise, CI/CD |
| **MediumLarge** | 5,000 | 250 | 9 | Growing enterprise |
| **Large** | 10,000 | 500 | 10 | Large enterprise, baselines |
| **XLarge** | 100,000 | 2,000 | 12 | Very large enterprise |
| **XXLarge** | 1,000,000 | 10,000 | 15 | Global enterprise, performance testing |

> **Note**: For GitHub Codespaces or resource-constrained environments, use **Nano** or **Micro** templates.

### Test Data Quality

Test user data is generated using reference CSV files with British naming conventions:
- **~1,000 female first names** (`Firstnames-f.csv`)
- **~1,000 male first names** (`Firstnames-m.csv`)
- **~500 British surnames** (`Lastnames.csv`)

Names are distributed using a prime-based algorithm to ensure realistic diversity across all template sizes:
- Templates up to ~994,000 users have unique display names
- Larger templates automatically append numeric suffixes: `(2)`, `(3)`, etc.
- All generated data is deterministic (same template always produces same names) for test repeatability

## External Systems

### Phase 1 (Available Now)

- **Samba AD Primary** - Port 389 (LDAP), 636 (LDAPS)
- **Samba AD Source** - Port 10389 (LDAP) - Profile: scenario2
- **Samba AD Target** - Port 11389 (LDAP) - Profile: scenario2

### Phase 2 (Planned)

- **SQL Server** - Port 1433 - Profile: phase2
- **Oracle XE** - Port 1521 - Profile: phase2
- **PostgreSQL** - Port 5433 - Profile: phase2
- **MySQL** - Port 3306 - Profile: phase2
- **OpenLDAP** - Port 12389 - Profile: phase2

## Test Lifecycle

```
┌─────────────────────────────────────────────────────────────────┐
│                    Full Test Lifecycle                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Step 0: Reset Environment                                      │
│  ├─ Tear down JIM containers and volumes                        │
│  └─ Tear down external system containers and volumes            │
│                                                                 │
│  Step 1: Stand Up Systems                                       │
│  ├─ Start external systems (Samba AD)                           │
│  └─ Start JIM (web, worker, scheduler, database)                │
│                                                                 │
│  Step 2: Wait for Ready                                         │
│  ├─ Check Samba AD health                                       │
│  ├─ Check JIM health (http://localhost:5200)                    │
│  └─ Set up infrastructure API key                               │
│                                                                 │
│  Step 3: Populate Test Data                                     │
│  ├─ Create users/groups in Samba AD                             │
│  └─ Generate CSV files                                          │
│                                                                 │
│  Step 4: Run Scenarios                                          │
│  ├─ Scenario 1: HR to Enterprise Directory                      │
│  │   ├─ Configure JIM (Connected Systems, Sync Rules)           │
│  │   ├─ Test: Joiner (new hire provisioning)                    │
│  │   ├─ Test: Mover (attribute updates)                         │
│  │   ├─ Test: Leaver (deprovisioning)                           │
│  │   └─ Test: Reconnection (deletion cancellation)              │
│  ├─ Scenario 2: Directory Sync (placeholder)                    │
│  └─ Scenario 3: GALSYNC (placeholder)                           │
│                                                                 │
│  Step 5: Collect Results                                        │
│  └─ Save to test/integration/results/                           │
│                                                                 │
│  Step 6: Tear Down (unless -SkipTearDown)                       │
│  ├─ Stop and remove external system containers                  │
│  └─ Stop and remove JIM containers                              │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

## Scenario 1: HR to Enterprise Directory

This scenario tests the complete Identity Lifecycle Management (ILM) pattern:

### What Gets Tested

1. **Setup** - Automatically configures JIM with:
   - CSV Connected System (HR source at `/connector-files/hr-users.csv`)
   - LDAP Connected System (Samba AD at `samba-ad-primary:389`)
   - Sync Rules (employeeId, firstName, lastName, email, department, etc.)
   - Object Matching Rules (for CSO auto-join on provisioning)
   - Run Profiles (CSV Import, LDAP Export)

2. **Joiner Test** - New hire provisioning:
   - Adds `test.joiner` to CSV
   - Triggers CSV import → Full Sync → LDAP export
   - Validates user created in Samba AD with correct attributes

3. **Mover Tests** - User lifecycle changes:
   - **3a. Attribute Change**: Updates job title (non-DN attribute)
   - **3b. Rename**: Changes display name (triggers DN rename in AD)
   - **3c. OU Move**: Changes department (triggers OU move in AD via DN change)
   - Each triggers delta sync sequence and validates changes in AD

4. **Leaver Test** - Deprovisioning:
   - Removes user from CSV (simulating termination)
   - Triggers delta sync sequence
   - Validates CSO disconnected and MVO enters grace period
   - User remains in AD during grace period (configurable: 7 days default)

5. **Reconnection Test** - Rehire within grace period:
   - Creates `test.reconnect`, provisions to AD
   - Removes from CSV (simulating termination/quit)
   - Restores to CSV within grace period (simulating rehire)
   - Validates user preserved in AD (reconnection successful, not re-provisioned)

### Running Individual Test Steps

You can run specific tests from Scenario 1:

```powershell
# Run all tests (default)
pwsh test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step All -Template Small -ApiKey $env:JIM_API_KEY

# Run only Joiner test
pwsh test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step Joiner -Template Micro -ApiKey $env:JIM_API_KEY

# Run only Mover tests (all three variants: attribute, rename, OU move)
pwsh test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step Mover -Template Small -ApiKey $env:JIM_API_KEY

# Run only Leaver test
pwsh test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step Leaver -Template Small -ApiKey $env:JIM_API_KEY

# Run only Reconnection test
pwsh test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step Reconnection -Template Small -ApiKey $env:JIM_API_KEY

# Continue on error (run all tests even if some fail)
pwsh test/integration/scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1 -Step All -Template Small -ApiKey $env:JIM_API_KEY -ContinueOnError
```

## Troubleshooting

### "API key required for authentication"

The infrastructure API key wasn't set up. Either:
- Run the full test suite (it creates the key automatically)
- Run `pwsh test/integration/Setup-InfrastructureApiKey.ps1` manually

### "Failed to start containers"

Docker Compose couldn't start containers. Check:
- Docker is running: `docker ps`
- No port conflicts: `docker compose ps`
- Container logs: `docker logs samba-ad-primary` or `docker logs jim.web`

### "Systems not ready within timeout"

Containers started but didn't become healthy. Check:
- Container health: `docker inspect --format='{{.State.Health.Status}}' samba-ad-primary`
- JIM logs: `docker logs jim.web`
- Database connectivity: `docker logs jim.database`

### Tests fail but containers are running

If using `-SkipTearDown`, containers remain running. To reset:
```powershell
./Reset-JIM.ps1
```

## Documentation

- **Complete Guide**: [docs/INTEGRATION_TESTING.md](../../docs/INTEGRATION_TESTING.md)
- **Issue Tracking**: [#173](https://github.com/TetronIO/JIM/issues/173)
- **Dependencies**:
  - [#175 - API Key Authentication](https://github.com/TetronIO/JIM/issues/175) - Complete
  - [#176 - PowerShell Module](https://github.com/TetronIO/JIM/issues/176) - Complete
  - [#170 - Database Connector](https://github.com/TetronIO/JIM/issues/170) - Phase 2
