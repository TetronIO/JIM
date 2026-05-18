# Testing Reference

> Detailed testing patterns for JIM. See root `CLAUDE.md` for build/test requirements.

## Test-Driven Development (TDD)

JIM requires TDD. The workflow is **Red → Green → Refactor**:

1. **Write the test first**: before any implementation
2. **Run it, confirm it fails (Red)**: a test that cannot fail is not a useful test
3. **Implement the minimum code to pass (Green)**
4. **Run again, confirm it passes**
5. **Refactor**: clean up without breaking the test

**For bug fixes:**
- Write a test that reproduces the bug → it must fail before your fix
- Implement the fix → run the test → it must now pass
- This proves correctness and prevents regression

**NEVER** write the implementation first and then write a test to match it. The test must fail before the fix to be meaningful.

## Test Structure

- Use NUnit with `[Test]` attribute
- Async tests: `public async Task TestNameAsync()`
- Use `Assert.That()` syntax
- Mock with Moq: `Mock<DbSet<T>>`
- Test naming: `MethodName_Scenario_ExpectedResult`

**Common Test Pattern:**
```csharp
[Test]
public async Task GetObjectAsync_WithValidId_ReturnsObject()
{
    // Arrange
    var expectedObject = new MetaverseObject { Id = Guid.NewGuid() };

    // Act
    var result = await _server.GetObjectAsync(expectedObject.Id);

    // Assert
    Assert.That(result, Is.Not.Null);
    Assert.That(result.Id, Is.EqualTo(expectedObject.Id));
}
```

## Debugging Failing Tests

- Claude Code cannot interactively debug with breakpoints like an IDE
- To diagnose issues, add temporary `Console.WriteLine()` statements to trace execution and inspect variable values
- Test output appears in the test results under "Standard Output Messages"
- **IMPORTANT**: Remove all debug statements before committing

## Inspecting Docker Container Binaries

When checking whether compiled code is present in a running Docker container (e.g., verifying a fix was included in the image), **do NOT use `strings`**; it is not installed in the runtime container images and silently returns no output, leading to false conclusions.

Use this approach instead:
```bash
docker compose exec jim.worker bash -c 'cat /app/JIM.PostgresData.dll | tr -d "\0" | grep -o "MethodName"'
```

## EF Core In-Memory Database Limitation

- Unit and workflow tests use EF Core's in-memory database which **auto-tracks navigation properties**
- This MASKS bugs where `.Include()` statements are missing from repository queries
- **Integration tests are the ONLY reliable way to verify navigation property loading**
- When modifying repository queries, ALWAYS run integration tests to verify `.Include()` chains are correct
- Add defensive null checks with logging for navigation properties to catch missing `.Include()` at runtime
- See `docs/TESTING_STRATEGY.md` for full details and real-world example (Drift Detection bug January 2026)

## Resource Usage Diagnostics

### Per-test memory snapshots (unit + workflow tests)

Every test project references `JIM.TestSupport`, which registers an assembly-level NUnit `TestActionAttribute` that snapshots process memory before and after each test. It's **opt-in** via an environment variable and a no-op otherwise, so normal runs incur no overhead.

To capture:
```bash
export JIM_TEST_MEMORY_LOG=/tmp/jim-test-memory.csv
dotnet test JIM.sln                          # or a specific test project
```

The CSV is **line-flushed** (`FileOptions.WriteThrough` + explicit flush after each row), so if a run OOMs you still have the last completed row on disk. Columns: `timestamp_utc, assembly, test, phase, managed_mb, working_set_mb, gen0_count, gen1_count, gen2_count`.

Useful for:
- Finding individual tests that leak memory (`managed_mb` delta between `before` and `after` rows)
- Correlating Gen2 collections with memory spikes
- Identifying which assembly dominates `dotnet test JIM.sln` peak RAM (compare `working_set_mb` across assemblies)

### Container stats during integration runs

`Run-IntegrationTests.ps1` automatically starts a `docker stats` sampler in a background job for every scenario invocation. Output goes to `test/integration/results/docker-stats-<scenario>-<template>-<timestamp>.csv`, one row per container per 2s. Covers `jim.worker`, `jim.web`, `jim.scheduler`, `jim.database`, directory containers, etc. Combine with the in-process `LogPageMemoryDiagnostics` lines in the worker log (set `-LogLevel Debug`) for the managed-heap view.

## Test Data Generation

**Change History UI Test Data:**

For testing the Change History UI (CSO and MVO change timelines), use the SQL seed script rather than workflow tests for faster iteration:

```bash
# Run against your development/test database
docker compose exec jim.database psql -U jim -d jim_test -f /workspaces/JIM/test/data/seed-change-history.sql
```

**Maintaining the SQL Script:**

The SQL script at `test/data/seed-change-history.sql` generates realistic change history data for UI testing. **If the database schema changes** (e.g., new columns, renamed tables, changed relationships for MetaverseObjectChanges, ConnectedSystemObjectChanges, or related tables), you MUST regenerate this script:

1. **When to regenerate:**
   - Migrations added/changed for MetaverseObjectChanges, MetaverseObjectChangeAttributes, MetaverseObjectChangeAttributeValues tables
   - Migrations added/changed for ConnectedSystemObjectChanges and related tables
   - New enum values for ObjectChangeType, ValueChangeType, or ChangeInitiatorType
   - Changes to MetaverseObject, MetaverseAttribute, or navigation property structures

2. **How to regenerate:**
   - Read the current `test/data/seed-change-history.sql` to understand the data scenario
   - Review recent migrations in `src/JIM.PostgresData/Migrations/` to understand schema changes
   - Rewrite the SQL script to match the new schema while preserving the same realistic test scenario:
     - Alice (Person): 5-7 changes including promotions, department moves, email updates, salary changes
     - Bob (Person): 7-9 changes including manager reference changes (add/remove/re-add Alice as manager)
     - Engineers Group: 4-5 changes including name changes and member additions/removals (Alice, Bob)
     - Platform Team Group: 1-3 changes including description updates
   - Test the script works by running it against a fresh test database
   - Document any schema-specific requirements in comments within the SQL file

3. **Script design principles:**
   - Self-contained: Creates MVOs and attributes if they don't exist
   - Idempotent where possible: Check for existing data before inserting
   - Realistic enterprise scenarios: Job titles, departments, salaries, dates that make sense
   - Covers all attribute types: Text, Number, LongNumber, DateTime, Boolean, Reference
   - Tests edge cases: Reference attributes being added/removed multiple times
   - Output URLs at end: Print MVO IDs so user can immediately navigate to test pages

4. **Alternative - Workflow Tests:**
   If you prefer writing C# workflow tests instead of SQL, see `/workspaces/JIM/test/JIM.Workflow.Tests/ChangeHistoryScenarioTests.cs` for a starting point (incomplete as of Jan 2026). Workflow tests are slower to run but type-safe and easier to maintain if you understand the WorkflowTestHarness API.

## Integration Testing

**IMPORTANT: The correct way to run integration tests is NOT by directly invoking scenario scripts.**

Instead, use the main integration test runner which handles setup, environment management, and teardown:

```powershell
# From repository root, run in PowerShell (not bash/zsh)
cd /workspaces/JIM

# Interactive menu - select scenario with arrow keys
./test/integration/Run-IntegrationTests.ps1

# Run ALL scenarios sequentially (full regression)
./test/integration/Run-IntegrationTests.ps1 -Scenario All -Template Small

# Run a specific scenario directly
./test/integration/Run-IntegrationTests.ps1 -Scenario Scenario1-HRToIdentityDirectory

# Run with a specific template size (Nano, Micro, Small, Medium, Large, Scale100k50Groups, Scale200k55Groups, Scale500k65Groups, Scale750k70Groups, Scale1m80Groups; or long-tail / OpenLDAP-only Scale100k5kGroups, Scale200k10kGroups, Scale500k25kGroups, Scale750k40kGroups, Scale1m60kGroups)
./test/integration/Run-IntegrationTests.ps1 -Template Small

# Run against OpenLDAP instead of Samba AD
./test/integration/Run-IntegrationTests.ps1 -Scenario All -Template Small -DirectoryType OpenLDAP

# Run against BOTH directory types (full cross-directory regression)
./test/integration/Run-IntegrationTests.ps1 -Scenario All -Template Small -DirectoryType All

# Run against BOTH directory types with different template sizes per directory
./test/integration/Run-IntegrationTests.ps1 -Scenario All -DirectoryType All -TemplateSambaAD Medium -TemplateOpenLDAP Scale100k50Groups

# Run only a specific test step (Joiner, Mover, Leaver, Reconnection, etc.)
./test/integration/Run-IntegrationTests.ps1 -Step Joiner

# Skip reset for faster re-runs (keeps existing environment)
./test/integration/Run-IntegrationTests.ps1 -SkipReset

# Skip rebuild (use existing Docker images)
./test/integration/Run-IntegrationTests.ps1 -SkipReset -SkipBuild

# Setup only - configure environment without running tests (for demos, manual exploration)
./test/integration/Run-IntegrationTests.ps1 -Scenario Scenario1-HRToIdentityDirectory -SetupOnly

# Set log level (overrides .env for this run, restores afterwards)
./test/integration/Run-IntegrationTests.ps1 -LogLevel Warning

# Disable change tracking (reduces database writes for large tests)
./test/integration/Run-IntegrationTests.ps1 -DisableChangeTracking

# Large-scale test with reduced logging and no change tracking
./test/integration/Run-IntegrationTests.ps1 -Template Large -LogLevel Warning -DisableChangeTracking
```

**What the runner does automatically:**
1. Resets environment (stops containers, removes volumes)
2. Rebuilds and starts JIM stack + Samba AD
3. Waits for all services to be ready
4. Creates infrastructure API key
5. Generates test data (CSV, Samba AD users)
6. Configures JIM with connected systems and sync rules
7. Runs the scenario
8. Tears down all containers

**For detailed integration testing guide, see:** [`docs/INTEGRATION_TESTING.md`](docs/INTEGRATION_TESTING.md)

**Metrics streaming** (optional): Set `JIM_BENCH_API_URL` and `JIM_BENCH_API_KEY` environment variables to stream performance metrics to the JIM-Bench ingestion API during test runs. When set, the runner automatically:
- Captures a host fingerprint at the start of the run
- Streams diagnostic log lines to the API in the background (no post-run processing overhead)
- Submits a final summary with pass/fail, duration, and host profile
- Prints a Grafana dashboard URL for the run

When not set, tests run normally with local-only results. See `test/integration/README.md` for full details.

**CRITICAL: Always use default runner behaviour, no `-SkipReset` or `-SkipBuild` flags.**
These flags are for human developer iteration only. Claude must not use them because:
- `-SkipBuild` can run stale container images that don't reflect the current code, masking real bugs
- `-SkipReset` carries over state from previous runs, producing results that are not reproducible
- Integration tests must always prove the code works from a clean state with freshly built containers

**Common templates by data size:**
- **Nano**: 3 users, 1 group (~10 sec) - Fast dev iteration
- **Micro**: 10 users, 3 groups (~30 sec) - Quick smoke tests
- **Small**: 100 users, 20 groups (~2 min) - Small business scenarios
- **Medium**: 1,000 users, 100 groups (~2 min) - Medium enterprise
- **Large**: 10,000 users, 500 groups (~15 min) - Large enterprise
- **Scale100k50Groups**: 100,000 users, 50 groups - Requires 20+ GB host RAM (OOM-killed on 16 GB machines; a 16 GB Codespace is not sufficient)
- **Scale100k5kGroups**: 100,000 users, ~5,027 groups (realistic long-tail shape) - OpenLDAP only, Scenario 8 only. Hard-fails if combined with `-DirectoryType SambaAD` or any non-Scenario-8 scenario. Same memory requirements as Scale100k50Groups.
- **Scale200k10kGroups / Scale500k25kGroups / Scale750k40kGroups / Scale1m60kGroups**: long-tail templates extending the Scale100k5kGroups model to 200k/500k/750k/1m users. OpenLDAP only, Scenario 8 only — same hard-fail behaviour as Scale100k5kGroups. RAM requirements scale roughly with user count (28+ / 40+ / 48+ / 64+ GB recommended). The 1m60k tier also needs the OpenLDAP accesslog `olcDbMaxSize` raised proportionally; see the section below.

**OpenLDAP accesslog MDB map size (IMPORTANT for large templates):**

The OpenLDAP accesslog database uses an MDB storage engine with a fixed maximum map size (`olcDbMaxSize`). When the map is full, OpenLDAP **silently stops recording changes**; delta imports will find zero modifications and sync changes will be lost. There is no error message; the writes just stop.

The map size is configured in `test/integration/docker/openldap/scripts/01-add-second-suffix.sh`. Current setting: **8 GB** (sufficient for Scale100k50Groups / 100K objects with large group membership operations, and tested against Scale100k5kGroups at ~5,000 groups / ~1M memberships). For higher-tier long-tail templates (Scale200k10kGroups, Scale500k25kGroups, Scale750k40kGroups, Scale1m60kGroups) or templates beyond Scale100k50Groups (e.g. Scale1m80Groups / 1M objects), increase the accesslog `olcDbMaxSize` proportionally (estimate ~10 MB per 1,000 objects for the initial population, plus additional capacity for sync cycles and group membership writes). Scale1m60kGroups (~1.06M objects, ~10M memberships) needs at least 32 GB; raise it before running.

## CSV cache

The three large, deterministic HR CSVs (`hr-users.csv`, `departments.csv`, `training-records.csv`) are cached by `test/integration/Get-OrGenerate-TestCSV.ps1`. At Scale100k50Groups the cache turns ~100 s of CSV generation into a sub-second tar extraction.

**Callers that use the cache (templated by the caller, potentially large):**
- `Invoke-IntegrationTests.ps1` (main runner, Step 3 populate)
- `Run-IntegrationTests.ps1` (scenario orchestrator)
- `scenarios/Invoke-Scenario1-HRToIdentityDirectory.ps1` (scenario-internal reset to baseline)
- `scenarios/Invoke-Scenario7-ClearConnectedSystemObjects.ps1` (scenario-internal seed)

**Callers that deliberately bypass the cache (call `Generate-TestCSV.ps1` directly):**
- `scenarios/Invoke-Scenario4-DeletionRules.ps1` — hard-coded `Nano` template then overlays scenario-specific HR/Training CSVs from `scenarios/data/`. Caching would save negligible time (3 users) and the overlay would immediately replace the wrapper's output.
- `scenarios/Invoke-Scenario5-MatchingRules.ps1` — hard-coded `Nano`, same overlay pattern as Scenario 4.
- `scenarios/Invoke-Scenario6-SchedulerService.ps1` — hard-coded `Micro` (10 users); too small for caching to be worth the wrapper complexity.

When adding a new scenario, ask: does it use `-Template $Template` with a potentially large template, and does it consume the generator's pristine output without further file-level tampering? If both yes, call `Get-OrGenerate-TestCSV.ps1`. Otherwise stick with `Generate-TestCSV.ps1` directly.

**Cache location:** `test/integration/test-data/.cache/csv-<template>-<hash16>.tar` (gitignored).

**Cache key inputs:** whole-file SHA256 over `Generate-TestCSV.ps1` + `utils/Test-Helpers.ps1`, plus the template name and PowerShell major version. Editing either script invalidates every template's cache entry. This is intentional (matches the Samba snapshot precedent in `Build-SambaSnapshots.ps1`).

**Determinism requirement:** the CSVs MUST be byte-identical across runs, otherwise the cache silently serves stale data. The two places this is enforced:
- `Generate-TestCSV.ps1` uses `$script:TrainingEpoch` (a fixed `2026-01-01` UTC date) instead of `Get-Date` for `completionDate`.
- `utils/Test-Helpers.ps1 > New-TestUser` uses a fixed `2030-01-01` UTC epoch instead of `Get-Date` for `AccountExpires`.
Do NOT reintroduce `Get-Date` into these code paths. If you add a new deterministic date field, derive it from a fixed epoch too.

**Not cached:** `cross-domain-users.csv`. It is a header-only export target that the File connector appends to during the test. The wrapper regenerates it fresh on every run (including cache hits) to avoid leaking state between runs.

**Docker seeding runs every time.** The cache archive holds file contents only; the `jim-connector-files-volume` state is not cached. Both cache-hit and cache-miss paths call `Write-FilesToConnectorVolume` (plural) to copy the four CSVs into the volume via a single rootless `docker run --user 1654:1654 busybox` that mounts the volume and the source directory. Files land owned by UID 1654 directly (no `chown` or `jim.worker` exec required), which is both faster and better aligned with the read-only rootfs hardening. Wall-clock is ~1 s for a full Scale100k50Groups-sized payload (~70 MB across four files). The legacy per-file `Write-FileToConnectorVolume` still exists as a thin wrapper for single-file callers.

**Flags:**
- `-IgnoreCache` — force regeneration and overwrite any existing cache entry
- `-NoCache` — generate as normal but neither read nor write the cache
- `-CachePath <dir>` — override the default `<OutputPath>/.cache` location

**Verifying the cache:** `test/integration/Test-CsvCache.ps1` is a standalone acceptance-test script that proves generator determinism, cache round-trip byte-identity, key sensitivity, and invalidation on script edits. Runs in a few seconds against the `Nano` template without needing Docker or `jim.worker`. Run it whenever you touch `Generate-TestCSV.ps1`, `utils/Test-Helpers.ps1 > New-TestUser`, or the cache wrapper.

**GitHub Actions wiring** (for the future pre-release workflow; out of scope for this iteration):

```yaml
- uses: actions/cache@v4
  with:
    path: test/integration/test-data/.cache
    key:  csv-${{ matrix.template }}-${{ hashFiles('test/integration/Generate-TestCSV.ps1','test/integration/utils/Test-Helpers.ps1') }}
    restore-keys: |
      csv-${{ matrix.template }}-
- run: ./test/integration/Get-OrGenerate-TestCSV.ps1 -Template ${{ matrix.template }}
```
