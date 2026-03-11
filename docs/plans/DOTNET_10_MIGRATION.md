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

1. **Swashbuckle vs built-in OpenAPI + Scalar** — ASP.NET Core 10 has native OpenAPI 3.1 document generation. Microsoft recommends `Microsoft.AspNetCore.OpenApi` + [Scalar](https://github.com/scalar/scalar) for the interactive UI. Swashbuckle 10.1.5 still works but is no longer the platform default. Options:
   - **Option A:** Upgrade Swashbuckle to 10.1.5 (minimal effort, works fine)
   - **Option B:** Migrate to built-in OpenAPI + Scalar (aligns with platform direction, removes a dependency)

2. **Asp.Versioning.Mvc** — only 10.0.0-preview.1 available. The current 8.1.1 (targeting net8.0) *may* work on .NET 10 but is not officially supported. Options:
   - **Option A:** Use the preview package (risk: instability)
   - **Option B:** Keep 8.1.1 and test compatibility (risk: unsupported)
   - **Option C:** Wait for stable 10.x release before migrating (safest)

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
- Swashbuckle.AspNetCore → 10.1.5 (or replace — see audit decision)

## Phase 4: Docker Infrastructure

**CRITICAL: .NET 10 Docker images are Ubuntu-based, not Debian.**

Microsoft dropped Debian-based images for .NET 10. All base images now use Ubuntu 24.04 (Noble).

- [ ] `src/JIM.Web/Dockerfile` — `sdk:10.0-noble` + `aspnet:10.0-noble` (with new SHA256 digests)
- [ ] `src/JIM.Worker/Dockerfile` — `sdk:10.0-noble` + `runtime:10.0-noble` (with new SHA256 digests)
- [ ] `src/JIM.Scheduler/Dockerfile` — `sdk:10.0-noble` + `runtime:10.0-noble` (with new SHA256 digests)
- [ ] `.devcontainer/Dockerfile` — update to .NET 10 devcontainer base image
- [ ] **Re-verify all pinned `apt` package versions** — package names and versions may differ between Debian Bookworm and Ubuntu Noble (especially `libldap` and `cifs-utils`)

## Phase 5: CI/CD Workflows

- [ ] `.github/workflows/dotnet-build-and-test.yml` — `dotnet-version: 10.0.x`
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
| **WebHostBuilder/IWebHost marked obsolete** | Check if JIM uses these | Expect compiler warnings if so; migrate to `WebApplicationBuilder`. |
| **Razor runtime compilation obsolete** | Check if JIM uses this | If used, plan removal. |

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

| Feature | Benefit for JIM | Effort |
|---------|----------------|--------|
| **`[PersistentState]` (Blazor)** | Eliminates manual state serialisation, fixes double-render, preserves state across circuit reconnection | Medium — refactor affected pages |
| **Named query filters (EF Core 10)** | Multiple toggleable filters per entity — useful for soft-delete, scoping, admin bypass | Medium — design filter strategy |
| **`ExecuteUpdateAsync` with regular lambdas** | Conditional batch updates in sync operations without expression trees | Low — simplify existing code |
| **LeftJoin LINQ operator** | Replace verbose `GroupJoin`/`SelectMany`/`DefaultIfEmpty` patterns | Low — find and replace patterns |
| **EF Core SQL log PII redaction** | Automatic — aligns with JIM's security requirements for government/defence deployments | None — automatic |

### Medium Value — Consider Post-Migration

| Feature | Benefit for JIM | Effort |
|---------|----------------|--------|
| **`field` keyword (C# 14)** | Eliminate backing field boilerplate for validated properties in domain models | Low — gradual adoption |
| **Extension members (C# 14)** | Extension properties for DTOs/models in API layer | Low — gradual adoption |
| **Null-conditional assignment (C# 14)** | Reduce null-check boilerplate in sync operations | Low — gradual adoption |
| **Complex types with value semantics (EF Core 10)** | Could eliminate the EF Core identity conflict issue documented in `CROSS_PAGE_REFERENCE_IDENTITY_CONFLICT.md` | High — requires data model changes |
| **Built-in passkey/WebAuthn auth** | Phishing-resistant auth without third-party dependencies; aligns with air-gapped requirement | Medium — new auth feature |
| **JSON column improvements (EF Core 10)** | Better support for complex types mapped to JSONB, including `ExecuteUpdateAsync` on JSON columns | Medium — evaluate for structured metadata |
| **Circuit state persistence (Blazor)** | Users resume sessions after disconnection without losing work | Low — mostly automatic |
| **CSP-compliant reconnection modal (Blazor)** | New `ReconnectModal` respecting Content Security Policy — important for secure deployments | Low |
| **UUIDv7 support (PostgreSQL 18)** | Time-sortable GUIDs via `Guid.CreateVersion7()` — better index performance for new entities | Low — use for new tables |

### Low Value / Future

| Feature | Notes |
|---------|-------|
| Server-Sent Events | Alternative to SignalR for simple streaming scenarios |
| OpenAPI YAML output | `app.MapOpenApi("/openapi/{documentName}.yaml")` |
| XML doc comments in OpenAPI | Automatic population of API descriptions from XML comments |
| Virtual generated columns (PG 18) | Computed-on-read columns — useful for computed identity attributes |

---

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Asp.Versioning.Mvc no stable 10.x release | Medium | High | Current 8.1.1 may work on .NET 10; preview available; wait for stable release |
| MudBlazor 9 breaking changes extensive | Medium | High | Review migration guide thoroughly; budget time for UI fixes |
| Humanizer.Core 3 namespace changes | Low | Low | Well-documented migration; limited use in JIM |
| Docker apt package versions differ on Ubuntu | Medium | Medium | Test package availability on Noble; update pinned versions |
| Npgsql `ObjectDisposedException` bug ([#3699](https://github.com/npgsql/efcore.pg/issues/3699)) | Medium | Medium | Monitor for fix in Npgsql 10.0.1+; workaround may exist |
| LDAP strict parsing breaks with non-standard directories | Low | High | Thorough testing against all target directory types |
| BackgroundService startup ordering changes | Low | Medium | Audit Worker/Scheduler `ExecuteAsync` implementations |
| Configuration null binding changes | Low | Medium | Audit `appsettings.json` for null values |

## Dependencies

- .NET 10 SDK available in devcontainer base image
- .NET 10 Docker base images (Ubuntu Noble) published on MCR with SHA256 digests
- Asp.Versioning.Mvc stable 10.x release (or confirmation that 8.1.1 works on .NET 10)
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
