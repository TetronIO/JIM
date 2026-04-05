---
title: Contributing
---

# Contributing

JIM is a source-available project. This page covers the guidelines and expectations for contributors.

## Getting Started

1. Set up your [development environment](dev-environment.md)
2. Familiarise yourself with the [architecture](architecture.md)
3. Review the code style and conventions below
4. Pick an issue or propose a change

## Code Style

### Language and Spelling

Use **British English** (en-GB) throughout the codebase: code comments, log messages, UI text, and documentation.

| Correct | Incorrect |
|---------|-----------|
| synchronisation | synchronization |
| authorisation | authorization |
| behaviour | behavior |
| colour | color |
| initialise | initialize |

### C# Conventions

- **Async/await** for all I/O operations. Suffix async methods with `Async`.
- **Constructor injection** for all dependencies. Avoid the service locator pattern.
- **One class per file**. File names match class names exactly.
- **Nullable reference types** are enabled. Use `string?` for optional values and `string` with a default for required values.
- **Descriptive names:** avoid abbreviations. Use `ConnectedSystemServer` not `CSSrv`.
- All models and POCOs belong in `src/JIM.Models/`, never inline in service files.
- Use `DateTime.UtcNow`, never `DateTime.Now`.

### Error Handling

```csharp
try
{
    await ProcessSync();
}
catch (Exception ex)
{
    Log.Error(ex, "Synchronisation failed for {SystemId}", systemId);
    throw; // Re-throw unless explicitly handled
}
```

## Git Workflow

1. **Always work on a feature branch**, never commit directly to `main`
2. Use descriptive branch names: `feature/description`
3. Write clear, descriptive commit messages with issue references where applicable
4. **Build and test before every commit** of .NET code
5. Push to your feature branch and create a pull request to `main`

### Pull Request Expectations

- All builds must pass with zero errors and zero warnings
- All tests must pass
- New functionality must include corresponding tests (following [TDD](testing.md))
- Code must follow the style conventions described on this page
- British English throughout

## Build and Test Before Committing

Every commit of .NET code must be preceded by a successful build and test run.

```bash
# During development: targeted builds
dotnet build test/JIM.Worker.Tests/
dotnet test test/JIM.Worker.Tests/

# Final pre-PR check: full solution
dotnet build JIM.sln
dotnet test JIM.sln
```

The following changes do **not** require a build or test run:

- Scripts (`.ps1`, `.sh`)
- Static assets (CSS, JS, images)
- Documentation (`.md` files)
- Configuration files (`.env.example`, `docker-compose.yml`, `.gitignore`)
- CI/CD workflows (`.github/workflows/`)

## Security Requirements

JIM is deployed in high-trust environments (healthcare, financial services, government). All contributions must meet these security expectations.

### Authentication and Authorisation

- Use `[Authorize]` on all API controllers; deny by default
- No local authentication; SSO/OIDC is required for all deployments
- Use claims-based authorisation for role-based access control

### Input Validation

- Validate all user input at system boundaries (API controllers, Blazor form submissions)
- Use parameterised queries (EF Core default); never bypass with unparameterised raw SQL
- Use DTOs with data annotations for request validation

### Log Injection Prevention

Wrap user-controlled `string?` values with `LogSanitiser.Sanitise()` from `JIM.Utilities` before passing them to any logger call. This prevents log injection attacks (CWE-117).

```csharp
// Correct: sanitised before logging
_logger.LogInformation("Search query: {Search}", LogSanitiser.Sanitise(request.Search));

// Safe: non-string types do not need wrapping
_logger.LogInformation("Page: {Page}, Id: {Id}", page, objectId);
```

Integers, GUIDs, enums, and `DateTime` values are inherently safe and do not need wrapping.

### Secrets

- Never hardcode secrets, credentials, or connection strings in source code
- Never log secrets, tokens, or personal data
- Use environment variables for all secrets (configured via `.env`)

## Dependency Governance

Before adding any new NuGet package or third-party dependency:

1. **Notify the maintainers:** state the need and conduct a suitability analysis
2. **Research:** licence compatibility, maintainer reputation, maintenance status, known vulnerabilities
3. **Present findings:** include a comparison table if alternatives exist
4. **Await approval** before adding the dependency

**Preference order:**

1. Microsoft-maintained packages
2. Established corporate-backed packages
3. .NET Foundation projects
4. Well-maintained open-source with identifiable maintainers

## Licence

JIM is source-available with a commercial licence required for production use. See the repository root for full licence terms.
