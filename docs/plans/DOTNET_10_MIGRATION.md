# .NET 10 Migration

- **Status:** Planned
- **GitHub Issue:** [#174](https://github.com/TetronIO/JIM/issues/174)
- **Created:** 2026-03-11

## Overview

Migrate JIM from .NET 9.0 (Standard Term Support, end-of-support November 2026) to .NET 10.0 (Long-Term Support, end-of-support November 2028). .NET 10.0.4 is the current GA release.

## Motivation

- **.NET 10 is LTS** — 3-year support window (until November 2028) vs .NET 9's 18-month STS ending November 2026
- **Performance improvements** — each .NET release brings measurable throughput and memory gains
- **EF Core 10** — query optimisations, new features for data access
- **C# 14** — language improvements for cleaner code
- **Security** — latest security patches and hardening

## Current State

All 14 projects target `net9.0`. No `global.json` exists. Docker images pin .NET 9.0 base image digests.

## Migration Scope

### Phase 1: Package Compatibility Audit

Before changing any framework targets, verify all NuGet dependencies have .NET 10-compatible releases.

#### Microsoft Packages (upgrade to 10.x)

| Package | Current Version | Target | Projects |
|---------|----------------|--------|----------|
| Microsoft.EntityFrameworkCore | 9.0.13 | 10.x | JIM.Models |
| Microsoft.EntityFrameworkCore.Design | 9.0.13 | 10.x | JIM.PostgresData, JIM.Web |
| Microsoft.EntityFrameworkCore.Relational | 9.0.13 | 10.x | JIM.PostgresData |
| Microsoft.EntityFrameworkCore.Tools | 9.0.13 | 10.x | JIM.PostgresData |
| Microsoft.EntityFrameworkCore.InMemory | 9.0.13 | 10.x | Test projects (3) |
| Microsoft.AspNetCore.Authentication.JwtBearer | 9.0.13 | 10.x | JIM.Web |
| Microsoft.AspNetCore.Authentication.OpenIdConnect | 9.0.13 | 10.x | JIM.Web |
| Microsoft.AspNetCore.DataProtection | 9.0.13 | 10.x | JIM.Application |
| Microsoft.AspNetCore.DataProtection.Extensions | 9.0.13 | 10.x | JIM.Application, JIM.Scheduler, JIM.Worker |
| Microsoft.Extensions.Hosting | 9.0.13 | 10.x | JIM.Scheduler, JIM.Worker |
| System.DirectoryServices.Protocols | 9.0.13 | 10.x | JIM.Connectors |
| Npgsql.EntityFrameworkCore.PostgreSQL | 9.0.4 | 10.x | JIM.PostgresData |

#### Third-Party Packages (verify compatibility)

| Package | Current Version | .NET 10 Compatible? | Notes |
|---------|----------------|---------------------|-------|
| MudBlazor | 8.15.0 | TBC | Check release notes |
| Serilog | 4.3.1 | TBC | netstandard2.0 target — likely compatible |
| Serilog.AspNetCore | 9.0.0 | TBC | May need update |
| Serilog.Sinks.Console | 6.1.1 | TBC | Likely compatible |
| Serilog.Sinks.File | 7.0.0 | TBC | Likely compatible |
| Serilog.Formatting.Compact | 3.0.0 | TBC | Likely compatible |
| DynamicExpresso.Core | 2.19.3 | TBC | Check .NET 10 support |
| Swashbuckle.AspNetCore | 9.0.6 | TBC | Check — may be replaced by built-in OpenAPI |
| Asp.Versioning.Mvc | 8.1.1 | TBC | Check for 10.x release |
| Asp.Versioning.Mvc.ApiExplorer | 8.1.1 | TBC | Check for 10.x release |
| Humanizer.Core | 2.14.1 | TBC | netstandard2.0 — likely compatible |
| CsvHelper | 33.1.0 | TBC | Likely compatible |
| NCrontab | 3.4.0 | TBC | netstandard1.0 — compatible |
| DNParser | 1.3.5 | TBC | Check compatibility |
| MockQueryable.EntityFrameworkCore | 9.0.0 | TBC | May need 10.x version |
| MockQueryable.Moq | 9.0.0 | TBC | May need 10.x version |
| EntityFramework (EF6) | 6.5.1 | TBC | Used in JIM.Worker.Tests only — check |

#### Test/Build Packages (verify compatibility)

| Package | Current Version | Notes |
|---------|----------------|-------|
| Microsoft.NET.Test.Sdk | 18.3.0 | Likely compatible |
| NUnit | 4.5.1 | Likely compatible |
| NUnit3TestAdapter | 5.2.0 | Likely compatible |
| NUnit.Analyzers | 4.12.0 | Likely compatible |
| Moq | 4.20.72 | Likely compatible |
| coverlet.collector | 6.0.4 | Likely compatible |
| Microsoft.VisualStudio.Azure.Containers.Tools.Targets | 1.22.1 / 1.23.0 | Likely compatible |

### Phase 2: Framework Target Update

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

### Phase 3: NuGet Package Updates

Update all Microsoft packages to their 10.x equivalents and update third-party packages as needed.

### Phase 4: Docker Infrastructure

Update all Dockerfiles with .NET 10 base images and new SHA256 digests:

- [ ] `src/JIM.Web/Dockerfile` — `sdk:10.0` + `aspnet:10.0` (with new digests)
- [ ] `src/JIM.Worker/Dockerfile` — `sdk:10.0` + `runtime:10.0` (with new digests)
- [ ] `src/JIM.Scheduler/Dockerfile` — `sdk:10.0` + `runtime:10.0` (with new digests)
- [ ] `.devcontainer/Dockerfile` — `dotnet:1-10.0-bookworm` (or newer Debian base)
- [ ] Update pinned `apt` package versions if base image changes Debian version

### Phase 5: CI/CD Workflows

- [ ] `.github/workflows/dotnet-build-and-test.yml` — `dotnet-version: 10.0.x`
- [ ] `.github/workflows/release.yml` — `dotnet-version: 10.0.x`

### Phase 6: Add `global.json`

Add a `global.json` to pin the SDK version and prevent accidental use of an older SDK:

```json
{
  "sdk": {
    "version": "10.0.x",
    "rollForward": "latestFeature"
  }
}
```

### Phase 7: Build, Test, and Validate

- [ ] `dotnet build JIM.sln` — zero errors
- [ ] `dotnet test JIM.sln` — all tests pass
- [ ] Docker builds succeed for all three services
- [ ] Devcontainer builds and runs correctly
- [ ] Integration tests pass (`Run-IntegrationTests.ps1`)

### Phase 8: Review Breaking Changes and Opportunities

Review Microsoft's .NET 10 and EF Core 10 release notes for:

- **Breaking changes** — deprecated APIs, behavioural changes
- **EF Core 10 features** — query improvements, bulk operations, JSON columns
- **ASP.NET Core 10** — Blazor enhancements, OpenAPI improvements, auth changes
- **C# 14 features** — field keyword, extension types, other language improvements

> Note: Adopting new features is optional and can be done incrementally after the migration.

### Phase 9: Documentation Updates

- [ ] `CLAUDE.md` — update .NET version references
- [ ] `README.md` — update prerequisites, badge
- [ ] `docs/DEVELOPER_GUIDE.md` — update framework references
- [ ] `.devcontainer/README.md` — update SDK version reference

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Third-party package not yet .NET 10 compatible | Medium | High | Audit in Phase 1 before starting; find alternatives if blocked |
| EF Core 10 behavioural changes break queries | Low | High | Run full test suite + integration tests; review EF Core 10 breaking changes list |
| Docker base image not available for target architecture | Low | Medium | Check MCR availability before starting |
| Swashbuckle deprecation | Medium | Medium | ASP.NET Core has built-in OpenAPI — may need to migrate |
| MockQueryable not updated for EF Core 10 | Medium | Low | Can fork or replace with manual mocks if needed |

## Dependencies

- .NET 10 SDK available in devcontainer base image
- .NET 10 Docker base images published on MCR with SHA256 digests
- EF Core 10.x and Npgsql.EntityFrameworkCore.PostgreSQL 10.x released
- All critical third-party packages compatible

## Success Criteria

- All projects target `net10.0`
- Full solution builds with zero errors
- All unit tests pass
- All integration tests pass
- Docker images build and run correctly
- CI/CD pipelines pass
- No performance regression
