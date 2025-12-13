# JIM Integration Testing

End-to-end integration tests for JIM against real external systems running in Docker containers.

## Quick Start

```powershell
# Stand up systems, populate data, and run tests
./Invoke-IntegrationTests.ps1 -Template Small -Phase 1

# Or step by step:
docker compose -f ../../docker-compose.integration-tests.yml up -d
./Wait-SystemsReady.ps1 -Phase 1
./Populate-SambaAD.ps1 -Template Small -Instance Primary
./Generate-TestCSV.ps1 -Template Small

# Tear down when done
docker compose -f ../../docker-compose.integration-tests.yml down -v
```

## Implementation Status

### ✅ Complete (Phase 1 Infrastructure)

- **Docker Compose Configuration** - External systems containerised
- **Data Population Scripts** - Generate realistic test data
- **Health Checks** - Wait for systems to be ready
- **Test Utilities** - Helper functions for assertions and LDAP queries
- **Lifecycle Management** - Stand up, populate, tear down

### ⏳ Pending (Phase 1 Scenarios)

Scenario test scripts require:

1. **API Key Authentication** ([#175](https://github.com/TetronIO/JIM/issues/175))
2. **PowerShell Module** ([#176](https://github.com/TetronIO/JIM/issues/176))

Once these are implemented, the scenario scripts will be completed:

- `scenarios/Invoke-Scenario1-HRToDirectory.ps1` - HR to AD provisioning
- `scenarios/Invoke-Scenario2-DirectorySync.ps1` - Directory to directory sync
- `scenarios/Invoke-Scenario3-GALSYNC.ps1` - AD to CSV export

### 📋 Planned (Phase 2)

Phase 2 adds database connector testing (requires [#170](https://github.com/TetronIO/JIM/issues/170)):

- SQL Server, Oracle, PostgreSQL, MySQL connectors
- Multi-source aggregation scenarios
- Database source/target scenarios

## Available Scripts

| Script | Purpose |
|--------|---------|
| `Invoke-IntegrationTests.ps1` | Master script - orchestrates full test lifecycle |
| `Wait-SystemsReady.ps1` | Wait for containers to be healthy |
| `Populate-SambaAD.ps1` | Create users/groups in Samba AD |
| `Generate-TestCSV.ps1` | Generate HR CSV files |
| `Setup-Scenario1.ps1` | Configure JIM for Scenario 1 (placeholder) |
| `scenarios/Invoke-Scenario*.ps1` | Test scenario scripts (placeholders) |

## Data Scale Templates

| Template | Users | Groups | Use Case |
|----------|-------|--------|----------|
| **Micro** | 10 | 3 | Quick smoke tests |
| **Small** | 100 | 20 | Small business, unit tests |
| **Medium** | 1,000 | 100 | Medium enterprise, CI/CD |
| **Large** | 10,000 | 500 | Large enterprise, baselines |
| **XLarge** | 100,000 | 2,000 | Very large enterprise |
| **XXLarge** | 1,000,000 | 10,000 | Global enterprise |

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

See [docs/INTEGRATION_TESTING.md](../../docs/INTEGRATION_TESTING.md) for detailed lifecycle information.

### DevContainer / Local

1. **Stand up**: `docker compose -f ../../docker-compose.integration-tests.yml up -d`
2. **Populate**: Run populate scripts
3. **Configure JIM**: Setup Connected Systems and Sync Rules (manual or via API)
4. **Execute**: Run scenario scripts
5. **Reset**: `docker compose down -v` (both test systems AND JIM)

### CI/CD

Manual trigger via GitHub Actions workflow (see `.github/workflows/integration-tests.yml` when implemented).

## Current Limitations

1. **No Automated Scenarios** - Require API Key Auth and PowerShell Module
2. **Manual JIM Configuration** - Must configure Connected Systems via UI
3. **No CI/CD Workflow** - GitHub Actions workflow not yet created

## Next Steps

To complete Phase 1:

1. Implement API Key Authentication ([#175](https://github.com/TetronIO/JIM/issues/175))
2. Implement PowerShell Module ([#176](https://github.com/TetronIO/JIM/issues/176))
3. Complete scenario test scripts with actual assertions
4. Create GitHub Actions workflow for CI/CD
5. Document manual testing procedures until automation is ready

## Manual Testing (Validated)

The Phase 1 infrastructure has been validated end-to-end. You can now manually test JIM integration:

### 1. Start Test Environment

```powershell
cd test/integration
./Invoke-IntegrationTests.ps1 -Template Small -Phase 1 -SkipTearDown
```

This will:
- ✅ Start Samba AD container and wait for healthy status
- ✅ Create 100 test users in AD (`bob.smith1` through `kevin.smith100`)
- ✅ Create 20 test groups (`Group-HR-1`, `Group-Sales-2`, etc.)
- ✅ Add 196 group memberships
- ✅ Generate CSV files at `test-data/hr-users.csv` and `departments.csv`
- ✅ Copy CSV files to container volume at `/connector-files/`

### 2. Configure JIM Connected Systems

Via JIM web UI (http://localhost:5200 or https://localhost:7000):

**CSV Source Connector:**
- Type: CSV
- File Path: `/connector-files/hr-users.csv` (accessible from JIM.Worker container)
- Delimiter: `,`
- Has Headers: `true`

**Samba AD Target Connector:**
- Type: LDAP
- Server: `samba-ad-primary` (container hostname on jim-network)
- Port: `389`
- Base DN: `dc=testdomain,dc=local`
- Bind DN: `cn=Administrator,cn=Users,dc=testdomain,dc=local`
- Password: `Test@123!`

### 3. Create Sync Rules

Map attributes between CSV and AD:
- `employeeId` → `employeeNumber`
- `firstName` → `givenName`
- `lastName` → `sn`
- `email` → `mail`
- `department` → `department`
- `samAccountName` → `sAMAccountName`
- `displayName` → `displayName`

### 4. Execute Sync and Validate

1. Trigger Run Profile manually via JIM UI
2. View sync results in web UI

Validate using LDAP queries:
```bash
# Count users (should be 100 + default accounts)
docker exec samba-ad-primary samba-tool user list | wc -l

# List test groups
docker exec samba-ad-primary samba-tool group list | grep "^Group-"

# View specific user
docker exec samba-ad-primary samba-tool user show bob.smith1
```

### 5. Test Update Scenarios

Modify CSV data:
```powershell
# Edit test-data/hr-users.csv - change department for bob.smith1
# Copy updated file to container
docker cp test-data/hr-users.csv samba-ad-primary:/connector-files/
```

Re-run sync and validate changes propagated.

### 6. Reset Environment

```bash
# Stop and remove all containers and volumes for fresh start
docker compose -f ../../docker-compose.integration-tests.yml down -v
```

### Validated Scale Templates

| Template | Users | Groups | Memberships | Duration |
|----------|-------|--------|-------------|----------|
| **Micro** | 10 | 3 | 3 | ~20s |
| **Small** | 100 | 20 | 196 | ~2m 20s |

Larger templates (Medium, Large, XLarge, XXLarge) should work but execution time increases significantly.

## Documentation

- **Complete Guide**: [docs/INTEGRATION_TESTING.md](../../docs/INTEGRATION_TESTING.md)
- **Issue Tracking**: [#173](https://github.com/TetronIO/JIM/issues/173)
- **Dependencies**:
  - [#175 - API Key Authentication](https://github.com/TetronIO/JIM/issues/175)
  - [#176 - PowerShell Module](https://github.com/TetronIO/JIM/issues/176)
  - [#170 - Database Connector](https://github.com/TetronIO/JIM/issues/170) (Phase 2)
