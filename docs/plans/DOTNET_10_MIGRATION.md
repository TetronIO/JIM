# .NET 10 Migration

- **Status:** Planned
- **GitHub Issue:** [#174](https://github.com/TetronIO/JIM/issues/174)
- **Created:** 2026-03-11

## Overview

Migrate JIM from .NET 9.0 (Standard Term Support, end-of-support November 2026) to .NET 10.0 (Long-Term Support, end-of-support November 2028). .NET 10.0.4 is the current GA release.

## Motivation

- **.NET 10 is LTS** — 3-year support window (until November 2028) vs .NET 9's 18-month STS ending November 2026
- **Performance improvements** — each .NET release brings measurable throughput and memory gains
- **EF Core 10** — query optimisations, simplified `ExecuteUpdateAsync`, named query filters, LeftJoin
- **C# 14** — `field` keyword, extension members, null-conditional assignment
- **Blazor** — `[PersistentState]` attribute, circuit state persistence, CSP-compliant reconnection
- **Security** — automatic PII redaction in EF Core logs, built-in passkey/WebAuthn support

## Current State

All 14 projects target `net9.0`. No `global.json` exists. Docker images pin .NET 9.0 Debian-based base image digests.

---

## Phase 1: Package Compatibility Audit ✅

All 31 NuGet packages have been audited. Every package has a .NET 10-compatible version available.

### Microsoft Packages — All Clear

All have 10.x releases available. Upgrade alongside the framework target change.

| Package | Current | Target | Notes |
|---------|---------|--------|-------|
| Microsoft.EntityFrameworkCore | 9.0.13 | **10.0.4** | |
| Microsoft.EntityFrameworkCore.Design | 9.0.13 | **10.0.3** | |
| Microsoft.EntityFrameworkCore.Relational | 9.0.13 | **10.0.3** | |
| Microsoft.EntityFrameworkCore.Tools | 9.0.13 | **10.0.3** | |
| Microsoft.EntityFrameworkCore.InMemory | 9.0.13 | **10.0.3** | Test projects (3) |
| Microsoft.AspNetCore.Authentication.JwtBearer | 9.0.13 | **10.0.3** | |
| Microsoft.AspNetCore.Authentication.OpenIdConnect | 9.0.13 | **10.0.3** | |
| Microsoft.AspNetCore.DataProtection | 9.0.13 | **10.0.3** | |
| Microsoft.AspNetCore.DataProtection.Extensions | 9.0.13 | **10.0.1** | Lags behind other ASP.NET packages |
| Microsoft.Extensions.Hosting | 9.0.13 | **10.0.3** | |
| System.DirectoryServices.Protocols | 9.0.13 | **10.0.3** | LDAP parsing is stricter — see Breaking Changes |
| Npgsql.EntityFrameworkCore.PostgreSQL | 9.0.4 | **10.0.0** | Known `ObjectDisposedException` bug in `dotnet ef database update` ([#3699](https://github.com/npgsql/efcore.pg/issues/3699)) — monitor for patch |

### Third-Party Packages — Decisions Required

| Package | Current | Latest | .NET 10? | Action |
|---------|---------|--------|----------|--------|
| MudBlazor | 8.15.0 | **9.1.0** | Yes (net10.0) | **Major upgrade** — review migration guide for breaking changes |
| Swashbuckle.AspNetCore | 9.0.6 | **10.1.5** | Yes | **Decision:** keep Swashbuckle or migrate to built-in OpenAPI + Scalar (see below) |
| DynamicExpresso.Core | 2.19.3 | 2.19.3 | Yes (netstandard2.0) | No change needed |
| Asp.Versioning.Mvc | 8.1.1 | 8.1.1 | **Preview only** (10.0.0-preview.1) | **Potential blocker** — wait for stable 10.x release |
| Asp.Versioning.Mvc.ApiExplorer | 8.1.1 | 8.1.1 | **Preview only** (10.0.0-preview.1) | Same as above |
| Humanizer.Core | 2.14.1 | **3.0.10** | Yes | **Major upgrade** — breaking namespace/API changes, review [migration guide](https://github.com/Humanizr/Humanizer/blob/main/docs/migration-v3.md) |
| CsvHelper | 33.1.0 | 33.1.0 | Yes (netstandard2.0) | No change needed |
| NCrontab | 3.4.0 | 3.4.0 | Yes (netstandard1.0) | No change needed |
| DNParser | 1.3.5 | 1.3.4 | Yes (netstandard1.1) | No change needed |
| MockQueryable.EntityFrameworkCore | 9.0.0 | **10.0.2** | Yes (net10.0 only) | Upgrade with migration |
| MockQueryable.Moq | 9.0.0 | **10.0.2** | Yes (net10.0 only) | Upgrade with migration |
| EntityFramework (EF6) | 6.5.1 | 6.5.1 | Yes (net8.0 target) | No change — maintenance mode, test-only dependency |

### Test/Build Packages — All Clear

| Package | Current | Latest | Action |
|---------|---------|--------|--------|
| Microsoft.NET.Test.Sdk | 18.3.0 | 18.3.0 | Already latest |
| NUnit | 4.5.1 | 4.5.1 | Already latest |
| NUnit3TestAdapter | 5.2.0 | **6.1.0** | Upgrade (drops .NET Core 3.x support — fine for JIM) |
| NUnit.Analyzers | 4.12.0 | 4.12.0 | Already latest |
| Moq | 4.20.72 | 4.20.72 | Already latest |
| coverlet.collector | 6.0.4 | **8.0.0** | Upgrade (min .NET 8.0 SDK — fine for JIM) |
| Serilog | 4.3.1 | 4.3.1 | Already latest (netstandard2.0) |
| Serilog.AspNetCore | 9.0.0 | **10.0.0** | Upgrade |
| Serilog.Sinks.Console | 6.1.1 | 6.1.1 | Already latest |
| Serilog.Sinks.File | 7.0.0 | 7.0.0 | Already latest |
| Serilog.Formatting.Compact | 3.0.0 | 3.0.0 | Already latest |
| Azure.Containers.Tools.Targets | 1.22.1/1.23.0 | 1.23.0 | Standardise on 1.23.0 |

### Audit Decisions Needed

1. **Swashbuckle vs built-in OpenAPI + Scalar** — **Decision: migrate to `Microsoft.AspNetCore.OpenApi` + `Scalar.AspNetCore`.** Removes Swashbuckle from the dependency tree (supply chain reduction for government/defence deployments), aligns with Microsoft's platform direction (Swashbuckle removed from all .NET 9+ templates), and XML doc comment support is automatic in .NET 10 via a compile-time source generator. Scalar provides a modern API reference UI with native dark mode, full-text search, auto-generated code snippets, and OAuth2/PKCE/API key auth support. Both UIs can coexist during transition for zero-risk evaluation. Migration effort is small to moderate — mainly translating OAuth2 + API key security definitions from Swashbuckle's `AddSecurityDefinition`/`AddSecurityRequirement` to OpenAPI document transformers.

2. **Asp.Versioning.Mvc** — **Decision: use 10.0.0-preview.1.** Accept the preview risk. This is a Microsoft-maintained package under `dotnet/aspnet-api-versioning` and a stable release is expected soon.

3. **MudBlazor 8 to 9** — major version with breaking changes. Must review the MudBlazor 9 migration guide before upgrading.

4. **Humanizer.Core 2 to 3** — breaking namespace changes (all APIs moved to root `Humanizer` namespace), removed `FormatWith` and obsolete `ToMetric` overloads. Review [migration guide](https://github.com/Humanizr/Humanizer/blob/main/docs/migration-v3.md).

---

## Phase 2: Framework Target Update

Update all 14 project files from `net9.0` to `net10.0`:

**Source projects:**
- [ ] `src/JIM.Data/JIM.Data.csproj`
- [ ] `src/JIM.Models/JIM.Models.csproj`
- [ ] `src/JIM.PostgresData/JIM.PostgresData.csproj`
- [ ] `src/JIM.Application/JIM.Application.csproj`
- [ ] `src/JIM.Connectors/JIM.Connectors.csproj`
- [ ] `src/JIM.Utilities/JIM.Utilities.csproj`
- [ ] `src/JIM.Web/JIM.Web.csproj`
- [ ] `src/JIM.Worker/JIM.Worker.csproj`
- [ ] `src/JIM.Scheduler/JIM.Scheduler.csproj`

**Test projects:**
- [ ] `test/JIM.Models.Tests/JIM.Models.Tests.csproj`
- [ ] `test/JIM.Utilities.Tests/JIM.Utilities.Tests.csproj`
- [ ] `test/JIM.Web.Api.Tests/JIM.Web.Api.Tests.csproj`
- [ ] `test/JIM.Worker.Tests/JIM.Worker.Tests.csproj`
- [ ] `test/JIM.Workflow.Tests/JIM.Workflow.Tests.csproj`

## Phase 3: NuGet Package Updates

Update all packages as per Phase 1 audit tables. Key upgrades:

- Microsoft.EntityFrameworkCore.* → 10.x
- Microsoft.AspNetCore.* → 10.x
- Microsoft.Extensions.* → 10.x
- Npgsql.EntityFrameworkCore.PostgreSQL → 10.0.0
- MudBlazor → 9.1.0
- Humanizer.Core → 3.0.10
- MockQueryable.* → 10.0.2
- Serilog.AspNetCore → 10.0.0
- NUnit3TestAdapter → 6.1.0
- coverlet.collector → 8.0.0
- **Remove** Swashbuckle.AspNetCore; **add** Microsoft.AspNetCore.OpenApi + Scalar.AspNetCore
- Asp.Versioning.Mvc / Asp.Versioning.Mvc.ApiExplorer → 10.0.0-preview.1

## Phase 4: Docker Infrastructure

**CRITICAL: .NET 10 Docker images are Ubuntu-based, not Debian.**

Microsoft dropped Debian-based images for .NET 10. All base images now use Ubuntu 24.04 (Noble).

- [ ] `src/JIM.Web/Dockerfile` — `sdk:10.0-noble` + `aspnet:10.0-noble` (with new SHA256 digests)
- [ ] `src/JIM.Worker/Dockerfile` — `sdk:10.0-noble` + `runtime:10.0-noble` (with new SHA256 digests)
- [ ] `src/JIM.Scheduler/Dockerfile` — `sdk:10.0-noble` + `runtime:10.0-noble` (with new SHA256 digests)
- [ ] `.devcontainer/Dockerfile` — update to .NET 10 devcontainer base image

#### Pinned `apt` Package Changes (Debian Bookworm → Ubuntu Noble) ✅ Audited

| Package (Bookworm) | Package (Noble) | Name Changed? | New Version |
|---------------------|-----------------|---------------|-------------|
| `libldap-common=2.5.13+dfsg-5` | `libldap-common` | No | `2.6.7+dfsg-1~exp1ubuntu8` |
| `libldap-2.5-0=2.5.13+dfsg-5` | **`libldap2`** | **Yes — renamed** | `2.6.7+dfsg-1~exp1ubuntu8` |
| `cifs-utils=2:7.0-2` | `cifs-utils` | No | `2:7.0-2build1` |
| `iputils-ping` (unpinned) | `iputils-ping` | No | Available |
| `curl` (unpinned) | `curl` | No | Available |

**Key actions:**
- **Rename** `libldap-2.5-0` → `libldap2` in Web and Worker Dockerfiles
- **Update version pins** for all three functional packages to Noble versions
- OpenLDAP moves from 2.5.x to 2.6.x — verify LDAP connector compatibility with the newer library

#### Image Variant Selection ✅ Audited

.NET 10 offers **chiseled** images — stripped-down Ubuntu images with no shell, no package manager, non-root by default (UID 1654), and ~50% smaller than full images. Ideal for security-sensitive deployments.

| Variant | Size (aspnet) | Shell? | apt? | Non-root? | Globalisation? |
|---------|--------------|--------|------|-----------|---------------|
| `noble` (full) | ~220 MB | Yes | Yes | No | Yes |
| `noble-chiseled` | ~110 MB | No | No | Yes | No |
| `noble-chiseled-extra` | ~130 MB | No | No | Yes | Yes |

**Constraint:** JIM.Web and JIM.Worker need `apt-get install` for `libldap` and `cifs-utils`. Chiseled images have no package manager, so they cannot be used for these services.

**Recommended per-service:**

| Service | Image Variant | Rationale |
|---------|--------------|-----------|
| **JIM.Web** | `aspnet:10.0-noble` + `USER app` | Needs libldap + cifs-utils via apt; add non-root user manually |
| **JIM.Worker** | `runtime:10.0-noble` + `USER app` | Needs libldap via apt; add non-root user manually |
| **JIM.Scheduler** | `runtime:10.0-noble-chiseled-extra` | No native dependencies; maximum security, non-root by default, ~50% smaller |

**Security improvement over current state:**
- All three services will run as non-root (currently they run as root)
- Scheduler gets chiseled image — no shell, no package manager, dramatically reduced CVE surface
- Web and Worker get non-root via explicit `USER app` directive

**Known issue:** [dotnet/runtime#123676](https://github.com/dotnet/runtime/issues/123676) — `System.DirectoryServices.Protocols` fails to load `libldap-2.5.so.0` on Ubuntu Noble with .NET 10. Monitor for fix; may need symlink workaround (`libldap-2.5.so.0` → `libldap.so.2`).

## Phase 5: CI/CD Workflows

- [ ] `.github/workflows/ci.yml` — `dotnet-version: 10.0.x`
- [ ] `.github/workflows/release.yml` — `dotnet-version: 10.0.x`

## Phase 6: Add `global.json`

Add a `global.json` to pin the SDK version:

```json
{
  "sdk": {
    "version": "10.0.400",
    "rollForward": "latestFeature"
  }
}
```

## Phase 7: Build, Test, and Validate

- [ ] `dotnet build JIM.sln` — zero errors
- [ ] `dotnet test JIM.sln` — all tests pass
- [ ] Docker builds succeed for all three services
- [ ] Devcontainer builds and runs correctly
- [ ] Integration tests pass (`Run-IntegrationTests.ps1`)
- [ ] LDAP connectivity tested against target directory servers

## Phase 8: Documentation Updates

- [ ] `CLAUDE.md` — update .NET version references
- [ ] `README.md` — update prerequisites, badge
- [ ] `docs/DEVELOPER_GUIDE.md` — update framework references
- [ ] `.devcontainer/README.md` — update SDK version reference

---

## Breaking Changes Requiring Attention

### Critical — Requires Code Changes or Verification

| Change | Impact on JIM | Action |
|--------|--------------|--------|
| **Docker: Debian dropped, Ubuntu only** | All Dockerfiles use Debian Bookworm | Migrate to Ubuntu Noble base images; re-verify `apt` package availability |
| **BackgroundService.ExecuteAsync fully async** | Worker and Scheduler use `BackgroundService` | Audit all `ExecuteAsync` implementations — code before first `await` no longer blocks other services from starting |
| **LDAP DirectoryControl parsing stricter** | JIM uses `System.DirectoryServices.Protocols` | BER/ASN.1 parsing is now managed code with strict validation; test against all target directories |
| **Configuration null values preserved** | `appsettings.json` null values were previously converted to `""` | Audit configuration files for `null` values; review binding code for null-safety |
| **Npgsql: date/time type mapping changed** | JIM uses `DateTime` (not `DateOnly`/`TimeOnly`) | Low risk — JIM uses `timestamp with time zone`, not `date`/`time`. Verify no raw SQL uses these types. |

### Medium — Behavioural Changes

| Change | Impact on JIM | Action |
|--------|--------------|--------|
| **Cookie auth: API endpoints return 401/403 instead of redirect** | JIM's API uses `[ApiController]` | **Beneficial** — proper status codes for API. Verify no client code depends on redirect. |
| **EF Core: parameterised collections use multiple params** | Sync operations use `.Contains()` with ID batches | May improve PostgreSQL query planning. Monitor performance. Can revert per-query with `EF.Parameter()`. |
| **EF Core: Application Name injected into connection string** | Could affect connection pooling | Explicitly set `Application Name` in PostgreSQL connection strings. |
| **System.Text.Json property name conflict checking** | Could surface hidden DTO serialisation bugs | Build and test — any conflicts surface as runtime exceptions. |
| ~~**WebHostBuilder/IWebHost marked obsolete**~~ | **Not affected** — JIM already uses `WebApplication.CreateBuilder()` (Web) and `Host.CreateDefaultBuilder()` (Worker, Scheduler) | No action needed |
| ~~**Razor runtime compilation obsolete**~~ | **Not affected** — JIM uses compile-time Razor compilation only; no `RuntimeCompilation` package or `AddRazorRuntimeCompilation()` calls | No action needed |

### Low — Informational

| Change | Notes |
|--------|-------|
| OpenSSL 1.1.1+ required on Unix | Ubuntu Noble ships OpenSSL 3.x — no action for Docker |
| SIGTERM handler removed from runtime | JIM uses generic host which registers its own handler — no action |
| C# 14 `Span<T>` overload resolution changes | Build will surface any ambiguities |
| `AsyncEnumerable` now in core libraries | Check for `System.Linq.Async` NuGet references — may conflict |
| EF Core inlined constants redacted from logs | **Beneficial** for JIM's security posture |
| EF Core split query ordering fix | **Beneficial** — fixes potential non-deterministic data corruption |

---

## Opportunities Worth Considering

### High Value — Consider During Migration

#### 1. `[PersistentState]` attribute (Blazor) — Effort: Medium

Eliminates the double-render problem across the Blazor UI. Currently, every page that loads data in `OnInitializedAsync` fetches that data twice during prerendering — once on the server, then again when the circuit connects.

**Concrete impact in JIM:**
- `ConnectedSystemList.razor` (line 102) — loads connected system headers; would avoid a redundant `GetConnectedSystemHeadersAsync()` call on every page load
- `ActivityDetail.razor` (lines 576–597) — loads activity, connected system header, metaverse object header, and execution stats in sequence; all four queries currently run twice
- `ConnectedSystemObjectList.razor` (lines 330–352) — loads connected system header + object types; double-fetched on navigation
- `ConnectorList.razor` (line 56) — loads connector definitions; simple but still double-rendered
- `UserPreferenceService.cs` — already has try-catch workarounds for "JS interop not available during prerendering" (12+ catch blocks); `[PersistentState]` would eliminate this entire pattern

Also provides **circuit state persistence** — users can resume sessions after disconnection without losing work, which is valuable for long-running identity management operations.

#### 2. Named query filters (EF Core 10) — Effort: Medium

Multiple independently toggleable query filters per entity. JIM currently has **no `HasQueryFilter` calls** and relies on manually repeated WHERE clauses.

**Concrete duplication in JIM today:**
- `ConnectedSystemRepository.cs` (lines 2219 and 2264) — the **identical** three-way PendingExport status filter (`Pending || Exported || ExportNotConfirmed`) is duplicated verbatim
- `TaskingRepository.cs` (lines 97 and 107) — `WorkerTaskStatus.Queued` filter duplicated across `GetNextWorkerTaskAsync` and `GetNextWorkerTasksToProcessAsync`
- `TaskingRepository.cs` (lines 241, 251, 270, 280) — `WaitingForPreviousStep` status filter repeated four times
- `ActivitiesRepository.cs` (lines 150 and 281) — activity status filtering duplicated

Named query filters would centralise these in `OnModelCreating` and allow selective bypass (e.g., admin views that need to see all statuses).

#### 3. `ExecuteUpdateAsync` with regular lambdas (EF Core 10) — Effort: Low

EF Core 10 allows regular C# logic (if/else, loops) inside `ExecuteUpdateAsync` instead of expression trees. JIM has extensive batch update operations that would benefit.

**Concrete impact in JIM:**
- `TaskingRepository.cs` (lines 326–343) — `UpdateWorkerTasksAsProcessingAsync` currently loops through tasks one by one, fetching each from the database, updating properties, then saving. Could become a single `ExecuteUpdateAsync` call
- `TaskingRepository.cs` (lines 242, 364) — already uses `ExecuteUpdateAsync` with fixed expressions; conditional logic would allow combining status transitions into one call
- `ConnectedSystemRepository.cs` (lines 2414–2478) — complex PendingExport batch update with try/catch fallback between raw SQL and entity-by-entity EF tracking. Simplified lambdas could reduce the need for raw SQL fallback paths
- The extensive raw SQL bulk operations (lines 3893–4214) with `BulkInsert*RawAsync` / `BulkUpdate*RawAsync` — while these are already optimal for performance, the simplified `ExecuteUpdateAsync` could replace the simpler raw SQL paths

#### 4. LeftJoin LINQ operator (EF Core 10) — Effort: Low

Replaces the verbose `join...into` + `DefaultIfEmpty()` pattern with a clean `LeftJoin` method.

**Concrete impact in JIM:**
- `ConnectedSystemRepository.cs` (lines 565–569) — the `GetConnectedSystemObjectsHeaderAsync` method uses the verbose pattern to left-join PendingExports with ConnectedSystemObjects. Would become a single `LeftJoin()` call

Only one LINQ instance found (other left joins use raw SQL), but this establishes a cleaner pattern for future queries.

#### 5. EF Core SQL log PII redaction — Effort: None (automatic)

EF Core 10 automatically redacts inlined constant values from SQL logs by default. JIM currently controls sensitive data logging via the `JIM_DB_LOG_SENSITIVE_INFO` environment variable and keeps EF Core command logging at Warning level (`Program.cs` line 476, `appsettings.json` line 10). The automatic redaction adds a safety net — even if someone enables Debug logging, PII won't leak into SQL log output.

**Directly relevant** for security-conscious customers where log data exfiltration is a compliance concern.

### Medium Value — Consider Post-Migration

#### 6. Null-conditional assignment (C# 14) — Effort: Low, gradual

C# 14 allows `x?.Property = value;` instead of `if (x != null) { x.Property = value; }`.

**Concrete patterns in JIM:**
- `TaskingServer.cs` (lines 399–405) — fetches a schedule execution, checks for null, then updates Status and CompletedAt. Repeated at lines 420–423 for CurrentStepIndex
- `SeedingServer.cs` (lines 495–539) — the same `if (result != null) list.Add(result)` pattern is repeated ~15 times sequentially for built-in example data sets
- `SearchServer.cs` (lines 28–29 and 40–41) — null-check-then-assign for predefined search post-processing
- `AuditHelper.cs` (lines 20–24 and 57–62) — `SetCreated` and `SetUpdated` methods check `if (user != null)` before assigning audit properties

#### 7. `field` keyword (C# 14) — Effort: Low, gradual

Eliminates explicit backing field declarations for properties that need validation or notification in setters. JIM currently has a few instances (e.g., `PostgresDataRepository.cs` with `_disposed` backing field) but the main value is establishing the pattern for future domain model properties that need setter validation.

#### 8. Complex types with value semantics (EF Core 10) — Effort: High

EF Core 10's complex types use value semantics, avoiding the reference identity issues that plague owned entities. This directly relates to the **cross-page reference EF identity conflict** documented in `docs/notes/CROSS_PAGE_REFERENCE_IDENTITY_CONFLICT.md` — the bug where `Update`/`UpdateRange`/`TrackGraph` all traverse the object graph and hit shared entities after `ClearChangeTracker()`.

Complex types with value semantics would eliminate this entire class of bugs. However, this requires significant data model changes and migration planning — a separate initiative rather than part of the .NET 10 migration itself.

#### 9. Built-in passkey/WebAuthn auth — Effort: Medium, separate initiative

JIM currently delegates all user authentication to the OIDC identity provider (including any MFA the IdP implements). API clients use JWT Bearer tokens or API keys. JIM has no built-in MFA.

Passkey support would primarily benefit **direct JIM authentication** if that's ever needed (e.g., local admin accounts in air-gapped environments without an IdP). For now, this is a future consideration rather than a migration task.

#### 10. CSP-compliant reconnection modal (Blazor) — Effort: Low

New `ReconnectModal` component respects Content Security Policy headers. JIM deploys in security-conscious environments where CSP is enforced — this is a straightforward swap that improves compatibility.

#### 11. UUIDv7 support (PostgreSQL 18) — Effort: Low-Medium

`Guid.CreateVersion7()` generates time-sortable GUIDs where the first 48 bits encode a millisecond timestamp. Unlike random UUIDv4 (`Guid.NewGuid()`), sequential UUIDs insert into B-tree indexes in order, avoiding page splits and index fragmentation.

**Scale in JIM:** 28 entity types use GUID primary keys, all generated via `Guid.NewGuid()` (fully random). No sequential or COMB GUID patterns exist today.

**Where this matters most — high-volume tables:**

| Table | Volume | Why it matters |
|-------|--------|---------------|
| `ConnectedSystemObjectAttributeValues` | 10,000s+ per sync | Bulk-inserted during import; random GUIDs cause scattered index writes across pages |
| `MetaverseObjectAttributeValues` | 10,000s+ per sync | Created during projection; same scattered write pattern |
| `ActivityRunProfileExecutionItemSyncOutcomes` | 1,000s per sync | Bulk-inserted with tree structure in `ActivitiesRepository` |
| `PendingExportAttributeValueChanges` | 100s–1,000s per export eval | Batch-created during export evaluation |
| `MetaverseObjectChanges` / `ConnectedSystemObjectChanges` | 100s per sync | Audit trail entries written sequentially but indexed randomly |

**Concrete benefit:** When JIM runs a full import of 50,000 objects with 20 attributes each, that's ~1,000,000 attribute value rows inserted. With random UUIDv4, each insert lands at a random position in the B-tree index, causing page splits and cache thrashing. With UUIDv7, inserts are append-mostly (new rows go to the end of the index), which is dramatically faster for bulk operations and reduces WAL (write-ahead log) volume.

**Scope of change:** Replace `Guid.NewGuid()` with `Guid.CreateVersion7()` at GUID generation points — primarily in `ActivitiesRepository` (bulk inserts), `ExportEvaluationServer`, `DriftDetectionService`, and the EF Core `ValueGeneratedOnAdd` default. Existing data is unaffected (UUIDv4 and UUIDv7 coexist in the same column). Could be adopted incrementally, starting with the highest-volume tables.

### Low Value / Future

| Feature | Notes |
|---------|-------|
| Extension members (C# 14) | Extension properties for DTOs in `src/JIM.Web/Extensions/Api/` — useful but not urgent |
| Server-Sent Events | Alternative to SignalR for simple streaming scenarios |
| OpenAPI YAML output | `app.MapOpenApi("/openapi/{documentName}.yaml")` |
| Virtual generated columns (PG 18) | Computed-on-read columns — useful for computed identity attributes |

> **Note:** XML doc comments in OpenAPI are now **automatic** with `Microsoft.AspNetCore.OpenApi` in .NET 10 — the source generator processes `<summary>`, `<param>`, `<returns>`, `<response>` etc. at compile time with zero runtime overhead. JIM.Web already has `GenerateDocumentationFile` enabled.

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Asp.Versioning.Mvc using preview package | Low | Low | Using 10.0.0-preview.1; Microsoft-maintained, stable release expected soon; can pin to stable when available |
| MudBlazor 9 breaking changes extensive | Medium | High | Review migration guide thoroughly; budget time for UI fixes |
| Humanizer.Core 3 namespace changes | Low | Low | Well-documented migration; limited use in JIM |
| Docker apt package versions differ on Ubuntu | Medium | Medium | Audited — `libldap-2.5-0` renamed to `libldap2`, versions updated (see Phase 4) |
| libldap loading failure on Noble ([#123676](https://github.com/dotnet/runtime/issues/123676)) | Medium | High | Monitor for fix; may need symlink `libldap-2.5.so.0` → `libldap.so.2` |
| Npgsql `ObjectDisposedException` bug ([#3699](https://github.com/npgsql/efcore.pg/issues/3699)) | Medium | Medium | Monitor for fix in Npgsql 10.0.1+; workaround may exist |
| LDAP strict parsing breaks with non-standard directories | Low | High | Thorough testing against all target directory types |
| BackgroundService startup ordering changes | Low | Medium | Audit Worker/Scheduler `ExecuteAsync` implementations |
| Configuration null binding changes | Low | Medium | Audit `appsettings.json` for null values |

## Dependencies

- .NET 10 SDK available in devcontainer base image
- .NET 10 Docker base images (Ubuntu Noble) published on MCR with SHA256 digests
- Asp.Versioning.Mvc 10.0.0-preview.1 (accepted; upgrade to stable when released)
- MudBlazor 9 migration guide reviewed
- Npgsql.EntityFrameworkCore.PostgreSQL patch for known `ObjectDisposedException` bug

## Success Criteria

- All 14 projects target `net10.0`
- Full solution builds with zero errors and no new warnings from breaking changes
- All unit tests pass
- All integration tests pass
- Docker images build and run correctly on Ubuntu Noble base
- CI/CD pipelines pass
- LDAP connectivity verified against target directories
- No performance regression in sync operations
