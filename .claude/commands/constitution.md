# JIM Constitution (Spec-Kit)

> **Note**: This file is part of Claude's spec-kit system for slash commands.
>
> For day-to-day development reference: **`CLAUDE.md`** (repository root)
>
> For comprehensive architecture documentation: **`docs/DEVELOPER_GUIDE.md`**

## Quick Reference

**Project**: Junctional Identity Manager (JIM) - Enterprise identity management system

**Stack**: .NET 9.0, EF Core, PostgreSQL, Blazor Server, MudBlazor

### Core Principles

- **Metaverse Pattern**: All identity operations flow through central metaverse
- **Layered Architecture**: Presentation → Application → Domain → Data
- **Async/Await**: All I/O operations must be async
- **Dependency Injection**: Constructor injection for all services
- **Repository Pattern**: Never access DbContext directly from application layer
- **En-GB Conventions**: British English spellings (authorisation, synchronisation)

### Before Committing

**IMPORTANT**: YOU MUST build and test locally before committing:

```bash
dotnet build JIM.sln
dotnet test JIM.sln
```

### Common Commands

```bash
# Build & Test
dotnet build JIM.sln
dotnet test JIM.sln
dotnet test --filter "FullyQualifiedName~TestName"

# Database
dotnet ef migrations add [Name] --project JIM.PostgresData
dotnet ef database update --project JIM.PostgresData

# Docker
docker compose up -d
docker compose logs [service]
```

### Key Locations

- API endpoints: `JIM.Api/Controllers/`
- UI pages: `JIM.Web/Pages/`
- Business logic: `JIM.Application/Servers/`
- Models: `JIM.Models/`
- Tests: `JIM.Worker.Tests/`

## Full Documentation

See **`docs/DEVELOPER_GUIDE.md`** for:
- Complete architecture patterns
- Detailed coding conventions
- Security requirements
- Testing expectations
- Common development tasks
- Troubleshooting guide
