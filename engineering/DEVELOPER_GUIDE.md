# JIM Developer Guide

> **Project**: Junctional Identity Manager (JIM)
> **Purpose**: Enterprise-grade identity management system for synchronisation, governance, and domain migrations
> **License**: Source-available, commercial license required for production use

## Overview

JIM is a .NET-based Identity Management (IDM) system implementing the metaverse pattern for centralised identity governance. It synchronises identities across heterogeneous systems (Active Directory, OpenLDAP, 389 DS, and other RFC 4512-compliant directories, files, databases, etc.) with bi-directional attribute flows, provisioning rules, and compliance tracking.

## Architecture Principles

### 1. Layered Architecture
- **Presentation Layer**: JIM.Web (Blazor Server with integrated REST API at `/api/`)
- **Application Layer**: JIM.Application (business logic, domain servers)
- **Domain Layer**: JIM.Models (entities, DTOs, interfaces)
- **Data Layer**: JIM.Data (abstractions), JIM.PostgresData (implementation)
- **Integration Layer**: JIM.Connectors (external systems)

**Rule**: Respect layer boundaries. Upper layers depend on lower layers, never vice versa.

### 2. Metaverse Pattern
The metaverse is the authoritative identity repository:
- **MetaverseObject**: Central identity entity (users, groups, custom types)
- **ConnectedSystem**: External systems synchronised with the metaverse
- **SyncRule**: Bidirectional mappings between connected systems and metaverse
- **Staging Areas**: Import/export staging for transactional integrity

**Rule**: All identity operations flow through the metaverse. Never sync directly between connected systems.

### 3. Modularity & Extensibility
- **Connectors**: Implement `IConnector` and related interfaces for new systems
- **Expressions**: C#-like expression language for attribute mappings, conditional logic, and scoping filters (see [Expression Language Guide](../docs/concepts/expressions.md))
- **Object Types**: Define custom identity types beyond built-in User/Group
- **Attributes**: Extensible attribute schema via MetaverseAttribute

**Rule**: Extend through interfaces, not modification. Keep connectors independent.

### 4. Architecture Diagrams

JIM's architecture is documented using C4 model diagrams (System Context, Container, Component levels).

**Viewing Diagrams**: See [docs/diagrams/structurizr/README.md](diagrams/structurizr/README.md) for instructions on running Structurizr Lite locally.

**Available Diagrams**:
- **System Context**: JIM's interactions with external systems and users
- **Container**: Internal deployable units (Web App, Worker, Scheduler, Connectors, Database, PowerShell Module)
- **Component**: Detailed views of Web Application, Application Layer, Worker Service, Connectors, and Scheduler

**Keeping Diagrams Up to Date**: When making architectural changes (new containers, components, connectors, or significant restructuring), update `docs/diagrams/structurizr/workspace.dsl` and regenerate the SVG images by running `jim-diagrams` from the repository root. Commit both the DSL changes and the regenerated SVGs together.

### 5. Process Diagrams

Detailed Mermaid diagrams document the runtime behaviour of JIM's synchronisation engine, worker, and scheduler. These are viewable directly in GitHub, VS Code, or any Mermaid-compatible markdown renderer.

**Synchronisation**:
- [Full Sync CSO Processing](../docs/developer/diagrams/FULL_SYNC_CSO_PROCESSING.md): Core per-CSO decision tree (scoping, join, projection, attribute flow, drift detection)
- [Delta Sync Flow](../docs/developer/diagrams/DELTA_SYNC_FLOW.md): How delta sync differs from full sync (watermark, early exit, CSO selection)
- [Full Import Flow](../docs/developer/diagrams/FULL_IMPORT_FLOW.md): Object import, duplicate detection, deletion detection, pending export reconciliation

**Export**:
- [Export Execution Flow](../docs/developer/diagrams/EXPORT_EXECUTION_FLOW.md): Batching, parallelism, deferred reference resolution, retry with backoff
- [Pending Export Lifecycle](../docs/developer/diagrams/PENDING_EXPORT_LIFECYCLE.md): Full lifecycle from creation through execution to confirmation

**Worker and Scheduling**:
- [Worker Task Lifecycle](../docs/developer/diagrams/WORKER_TASK_LIFECYCLE.md): Polling, dispatch, heartbeat, cancellation, SafeFailActivityAsync fallback
- [Schedule Execution Lifecycle](../docs/developer/diagrams/SCHEDULE_EXECUTION_LIFECYCLE.md): Step groups, worker-driven advancement, recovery mechanisms

**Supporting Concepts**:
- [Connector Lifecycle](../docs/developer/diagrams/CONNECTOR_LIFECYCLE.md): Interface hierarchy, resolution, import/export open/close lifecycles
- [Activity and RPEI Flow](../docs/developer/diagrams/ACTIVITY_AND_RPEI_FLOW.md): Activity creation, RPEI accumulation, status determination
- [MVO Deletion and Grace Period](../docs/developer/diagrams/MVO_DELETION_AND_GRACE_PERIOD.md): Deletion rules, grace periods, housekeeping cleanup

## Technology Stack

### Core Technologies (Required)
- **.NET 10.0**: All projects target `net10.0`
- **C# 14**: Language features, nullable reference types enabled
- **ASP.NET Core**: Web framework for Blazor and API
- **Entity Framework Core 10.0**: ORM for data persistence
- **PostgreSQL 18**: Primary database (via Npgsql)

### UI & Frontend
- **Blazor Server**: Interactive web UI with SignalR
- **MudBlazor 9.x**: Material Design component library
- **Razor Pages**: Server-side rendering

### Authentication & Security
- **OpenID Connect (OIDC)**: SSO authentication (required)
- **PKCE**: Enhanced auth flow security
- **Cookie Authentication**: Session management
- **Claims-based Authorisation**: Role-based access control from metaverse

**Rule**: No local authentication. SSO/OIDC required for all deployments.

### Infrastructure
- **Docker**: Container platform
- **Docker Compose**: Multi-service orchestration
- **Serilog**: Structured logging
- **GitHub Actions**: CI/CD

### Testing
- **NUnit**: Unit testing framework
- **Moq**: Mocking framework
- **coverlet**: Code coverage

## Development Guidelines

### 1. Project Organisation
When adding functionality:
- **Domain models**: Add to `src/JIM.Models/Models/`
- **DTOs**: Add to appropriate `DTOs/` subdirectories
- **Business logic**: Add to `src/JIM.Application/Servers/` or extend existing servers
- **Data access**: Add to `src/JIM.PostgresData/` repository classes
- **API endpoints**: Add to `src/JIM.Web/Controllers/Api/`
- **API models/DTOs**: Add to `src/JIM.Web/Models/Api/`
- **UI pages**: Add to `src/JIM.Web/Pages/`
- **Connectors**: Create new project or extend `src/JIM.Connectors/`

### 2. Coding Conventions
Use the en-GB region for spellings and formats.

**C# Standards**:
```csharp
// Nullable reference types enabled - use null-forgiving or null-conditional operators
public string? OptionalProperty { get; set; }
public string RequiredProperty { get; set; } = string.Empty;

// Implicit usings enabled - avoid redundant using statements
// Use dependency injection for all services
public class MyServer
{
    private readonly IRepository _repository;

    public MyServer(IRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }
}

// Use descriptive names, avoid abbreviations
// Good: ConnectedSystemServer, MetaverseObject
// Bad: CSSrv, MVObj
```

**Async/Await**:
```csharp
// All I/O operations must be async
public async Task<MetaverseObject> GetObjectAsync(Guid id)
{
    return await _repository.Metaverse.GetObjectAsync(id);
}
```

**Error Handling**:
```csharp
// Log errors with Serilog, provide context
try
{
    await ProcessSync();
}
catch (Exception ex)
{
    Log.Error(ex, "Synchronisation failed for {SystemId}", systemId);
    throw; // Re-throw unless handled
}
```

**GUID/UUID Handling**:

JIM exchanges identifiers with external systems that may use different binary representations (Microsoft GUID vs RFC 4122 UUID). Follow these rules to prevent identifier corruption:

```csharp
// SAFE: String-based exchange (preferred for all external interfaces)
var guid = Guid.TryParse(externalValue, out var parsed) ? parsed : (Guid?)null;
var canonical = guid.ToString("D"); // Standard hyphenated format

// SAFE: AD/Samba objectGUID - uses Microsoft byte order, matches .NET Guid
var objectGuid = new Guid(adBinaryBytes); // Only for AD/Samba LDAP attributes

// UNSAFE: Unknown-source binary bytes - may be RFC 4122 (different byte order)
var guid = new Guid(unknownBytes); // DO NOT do this without knowing the source
```

Rules:
- **Always use `Guid.TryParse()` in production code** - never `Guid.Parse()` for external input
- **Always use string format for API contracts** - JSON, CSV, and REST APIs exchange GUIDs as strings
- **Never construct `new Guid(byte[])` without documenting the source byte order** - AD uses Microsoft order, most non-Microsoft systems use RFC 4122
- **Never compare raw byte arrays from different sources** - parse to `Guid` first, then compare
- **Use `Guid.ToByteArray()` only for Microsoft-format targets** (AD, SQL Server) - RFC 4122 systems need byte-swapped first 3 components

For full details and connector-specific guidance, see [`docs/plans/doing/GUID_UUID_HANDLING.md`](plans/doing/GUID_UUID_HANDLING.md).

### 3. Database & Migrations

**Entity Framework Core**:
- Use Fluent API for complex configurations in `JimDbContext`
- Create migrations for schema changes: `dotnet ef migrations add MigrationName`
- Test migrations on PostgreSQL 18
- Use repository pattern, never access DbContext directly from application layer

> **CRITICAL: NEVER flatten, squash, delete, or reset EF Core migrations.**
>
> JIM is deployed in production environments. EF Core tracks applied migrations by name in the `__EFMigrationsHistory` table. If existing migrations are removed and replaced with a new combined migration, EF will not recognise it as already applied, will attempt to re-create all tables, and **will fail on every deployed instance**. Migrations are append-only; once committed to `main`, they are permanent. The only permitted operations are adding new migrations and, in rare cases, reverting the most recent migration on a feature branch before merge.

**Performance**:
- All EF Core queries default to `AsNoTracking`; `QueryTrackingBehavior.NoTracking` is configured on the shared `JimDbContext`. Read-only paths are fast and allocation-light without any per-query `.AsNoTracking()` call (#484).
- Write paths must explicitly opt in to change tracking via the `withChangeTracking: true` parameter on repository read methods, or by using `AsTracking()` on the underlying query. The worker and sync engine rely on this; forgetting to opt in on a write path results in entities not being persisted.
- Batch operations where possible
- Index frequently queried columns
- For high-throughput worker hot paths (per-page flushes, cross-page resolution, bulk inserts), prefer raw Npgsql over EF Core projection. See [`src/CLAUDE.md`](../src/CLAUDE.md) under "Worker Hot Path - Raw SQL Over EF Projection".

### 4. Dependency Injection

**Service Registration** (in `Program.cs` or startup):
```csharp
// Scoped for per-request instances
builder.Services.AddScoped<IRepository, PostgresDataRepository>();
builder.Services.AddScoped<JimApplication>();

// Transient for stateless services
builder.Services.AddTransient<IConnector, LdapConnector>();

// Singleton for shared state (use sparingly)
builder.Services.AddSingleton<IConfiguration>(configuration);
```

**Rule**: Prefer constructor injection. Avoid service locator pattern.

### 5. API Development

**Controllers**:
- Inherit from `ControllerBase`
- Use versioned attribute routing: `[Route("api/v{version:apiVersion}/[controller]")]`
- Add `[ApiVersion("1.0")]` to mark controller version
- Return `ActionResult<T>` for typed responses
- Use DTOs for request/response bodies
- Add XML comments for OpenAPI documentation

**Example**:
```csharp
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public class MetaverseController : ControllerBase
{
    /// <summary>
    /// Gets a metaverse object by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(MetaverseObjectDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<MetaverseObjectDto>> GetObject(Guid id)
    {
        var obj = await _app.Metaverse.GetObjectAsync(id);
        return obj == null ? NotFound() : Ok(obj);
    }
}
```

**API Versioning**:
- All endpoints are versioned using URL path (e.g., `/api/v1/certificates`)
- Current version: **1.0**
- Default version assumed if not specified in request
- Responses include `api-supported-versions` header
- To add v2: Create new methods/controllers with `[ApiVersion("2.0")]` and `[MapToApiVersion("2.0")]`
- Deprecate old versions: Add `[ApiVersion("1.0", Deprecated = true)]`

### 6. Blazor Development

**Component Structure**:
- Use code-behind pattern for complex components
- Inject services via `@inject` or constructor
- Use MudBlazor components for consistency
- Follow Blazor lifecycle methods

**Tabs**:
- Use `<NavigableMudTabs>` instead of `<MudTabs>` for all top-level page tabs. This wrapper component syncs the active tab with a `?t=slug` query string parameter, enabling browser back/forward navigation between tabs. It is a drop-in replacement; it accepts the same parameters as `MudTabs` (including `Header`, `@bind-ActivePanelIndex`, etc.)
- Plain `<MudTabs>` is still appropriate for tabs inside dialogs or nested sub-tabs within a parent tab, where URL navigation is not needed

**State Management**:
- Use cascading parameters for shared state
- Avoid static state
- Leverage SignalR for real-time updates

### 7. Background Processing

**Worker Architecture** (redesigned in #394):

The Worker separates pure domain logic from I/O via two core interfaces:

- **`ISyncEngine`**: stateless domain engine with 7 methods (join resolution, projection, attribute flow, scoping, etc.). Zero I/O dependencies; receives all data as parameters and returns results. Unit-testable without mocks.
- **`ISyncRepository`**: ~80-method data access boundary. Production implementation: `JIM.PostgresData.Repositories.SyncRepository`. Test implementation: `JIM.InMemoryData.SyncRepository`.

**Dependency Injection**: The Worker and Scheduler use `IJimApplicationFactory` and `IConnectorFactory` for per-task context isolation. Each dispatched task gets its own DI scope with independent `DbContext` and connector instances.

**Bulk Write Performance**:
- **`ParallelBatchWriter`**: splits bulk writes across N concurrent PostgreSQL connections
- **COPY binary protocol**: used for high-volume inserts (CSO creates, MVO creates, RPEIs, sync outcomes) via Npgsql's binary COPY API

**Task Processing**:
- Poll task queue from database
- Process tasks via specific processors (SyncImportTaskProcessor, SyncExportTaskProcessor, etc.)
- Update task status and activity log
- Handle errors gracefully, log failures
- Support parallel task execution: schedule steps with `ExecutionMode = Parallel` are dispatched concurrently via `Task.WhenAll`, each with its own DI scope

**Export Parallelism** (two independent axes):
- **LDAP Connector Pipelining** (`Export Concurrency` connector setting, 1-16): Multiple LDAP operations execute concurrently within a single export batch using `SemaphoreSlim`-based throttling and async APM wrappers (`LdapConnectionExtensions.SendRequestAsync`)
- **Parallel Batch Processing** (`MaxExportParallelism` per-Connected System, 1-16): Multiple export batches process concurrently with separate `IRepository` and `IConnector` instances per batch. Gated by `SupportsParallelExport` connector capability

**Rule**: All long-running operations should be queued as WorkerTasks, not executed synchronously in web requests.

### 8. Performance Diagnostics

JIM includes a built-in performance diagnostics infrastructure for measuring operation timings during sync operations. This uses `System.Diagnostics.ActivitySource` under the hood (the .NET OpenTelemetry-compatible API) but wraps it with JIM-specific terminology to avoid confusion with JIM's `Activity` class (used for audit/task tracking).

**Key Components** (in `src/JIM.Application/Diagnostics/`):

| Class | Purpose |
|-------|---------|
| `OperationSpan` | Wrapper around `System.Diagnostics.Activity` representing a timed operation |
| `DiagnosticSource` | Wrapper around `ActivitySource` for creating spans |
| `DiagnosticListener` | Logs span completions to Serilog with timing information |
| `Diagnostics` | Static entry point with pre-configured sources (Sync, Database, Connector, Expression) |

**Using Diagnostics in Code**:

```csharp
using JIM.Application.Diagnostics;

// Start a span for an operation
using var span = Diagnostics.Sync.StartSpan("FullImport");
span.SetTag("connectedSystemId", connectedSystem.Id);

try
{
    // Nested operations create child spans automatically
    using var pageSpan = Diagnostics.Sync.StartSpan("ImportPage");
    pageSpan.SetTag("pageNumber", pageNumber);

    await ProcessPageAsync();

    pageSpan.SetSuccess();
}
catch (Exception ex)
{
    span.SetError(ex);
    throw;
}

span.SetSuccess();
```

**Available Diagnostic Sources**:
- `Diagnostics.Sync` - Sync operations (import, sync, export)
- `Diagnostics.Database` - Database operations
- `Diagnostics.Connector` - Connector operations
- `Diagnostics.Expression` - Expression evaluation

**Enabling Diagnostics**:

Diagnostics are enabled automatically in:
- **Worker Service** (`src/JIM.Worker/Worker.cs`) - 100ms slow operation threshold
- **Unit Tests** (`GlobalTestSetup.cs` in test projects) - 50ms slow operation threshold

To enable manually:
```csharp
using var listener = Diagnostics.EnableLogging(slowOperationThresholdMs: 100);
// Operations logged to Serilog
```

**Viewing Diagnostic Output**:
- In Docker: `docker logs jim.worker` - Look for "DiagnosticListener:" entries
- In Tests: Run with verbose output to see timing in test results
- Slow operations (exceeding threshold) are logged at Warning level

**Path to OpenTelemetry**:

The diagnostics infrastructure uses `System.Diagnostics.ActivitySource`, which is the standard .NET API for OpenTelemetry. To export telemetry to external systems (Jaeger, Zipkin, Azure Monitor, etc.), simply add OpenTelemetry exporters - no instrumentation code changes required:

```csharp
// Future: Add OpenTelemetry SDK and configure exporters
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("JIM.Sync", "JIM.Database", "JIM.Connector", "JIM.Expression")
        .AddJaegerExporter());
```

See [GitHub Issue #212](https://github.com/TetronIO/JIM/issues/212) for .NET Aspire evaluation which includes comprehensive observability features.

## Security Considerations

### 1. Authentication
- **OIDC/SSO**: Required for all deployments
- **No local accounts**: Users authenticated via external identity provider
- **Claims mapping**: Map OIDC claims to metaverse objects for role assignment

### 2. Authorisation
- **Role-based**: Use claims-based authorisation in controllers/pages
- **Metaverse-driven**: Roles defined in metaverse, not hardcoded
- **Attribute-based**: Scoping and conditional logic driven by expressions and attribute values

### 3. Input Validation
- Validate all user input (web, API)
- Use DTOs with data annotations
- Sanitise for SQL injection (EF Core parameterises queries)
- Protect against XSS in Blazor components (framework handles by default)

### 4. Log Injection Prevention (Mandatory)
- **ALWAYS** wrap user-controlled `string?` values with `LogSanitiser.Sanitise()` from `JIM.Utilities` before passing them as arguments to any `ILogger` or Serilog log call
- This prevents log injection attacks (CWE-117 / OWASP Log Injection) where malicious input containing newline characters could forge fake log entries
- Integers, GUIDs, enums, and `DateTime` values are inherently safe and do not need wrapping
- `LogSanitiser.Sanitise()` handles null safely; it returns null for null input

```csharp
// BAD - user-controlled string passed directly
_logger.LogInformation("Search query: {Search}", request.Search);

// GOOD - sanitised before logging
_logger.LogInformation("Search query: {Search}", LogSanitiser.Sanitise(request.Search));

// Safe - non-string types don't need wrapping
_logger.LogInformation("Page: {Page}, Id: {Id}", page, objectId);
```

### 5. Secrets Management
- **Environment variables**: All secrets configured via `.env` file (gitignored)
- **No hardcoded secrets**: Never commit credentials, connection strings, API keys
- **Docker secrets**: Use Docker secrets for production deployments

### 6. Commit Signing (Mandatory)

All commits to JIM must be cryptographically signed. Signed commits are the foundation of verifiable code provenance: they cryptographically attribute each commit to a specific identity, which is the first line of defence against compromised credentials, malicious merges, and accidental contributions from an unintended identity.

**Policy:**
- Every commit on every branch must be signed.
- The signing key must be one of: an SSH key registered as a *signing key* on GitHub, a GPG key registered on GitHub, or (inside a Codespace) the built-in `gh-gpgsign` helper.
- The pre-commit hook at `.githooks/pre-commit` enforces this at commit time. The branch protection ruleset on `main` (see section 7 below) additionally enforces `required_signatures` server-side, so unsigned commits are rejected at push/merge time even if the local hook is bypassed.

**Setup in a devcontainer (automated):**

The devcontainer setup script (`.devcontainer/setup.sh`) configures signing automatically during container creation. It detects the environment and does the right thing:

- **In a GitHub Codespace**: uses the built-in `gh-gpgsign` helper, which signs via the GitHub API. No local key management required: GitHub signs and verifies your commits automatically. The one prerequisite is a single, account-wide, one-time toggle: enable **GPG verification** at github.com/settings/codespaces and allow this repository (or all repositories). **Do this before you create the codespace** and it just works. Without it, the API refuses to sign with `Current user GPG signing disabled`; and because the capability is minted into the codespace token at startup, enabling it on an already-running codespace requires a Stop/Start (or rebuild) to take effect. The setup script (and every attach) probes signing and prints these recovery steps if the capability is missing.
- **In a local devcontainer**: uses the host machine's SSH agent via forwarding. Your private SSH key never enters the container; only the public identity is referenced.

If signing is not successfully configured at container creation, the setup script prints a prominent warning with recovery steps. At any time, you can re-run the signing configuration via `jim-setup-signing` or inspect the current state via `jim-signing-status`.

**Host-side prerequisites (for local devcontainers):**

SSH agent forwarding only works if the host machine has an SSH agent running with your key loaded. This is per-OS:

- **macOS**: the Keychain-integrated SSH agent is usually running at login. Add your key once with `ssh-add --apple-use-keychain ~/.ssh/id_ed25519`; it persists across reboots. Verify with `ssh-add -l`.
- **Linux**: start an agent in your login shell and add your key. A common pattern in `~/.bashrc` or `~/.zshrc`:
  ```bash
  if ! pgrep -u "$USER" ssh-agent >/dev/null; then
      eval "$(ssh-agent -s)"
  fi
  ssh-add -l >/dev/null 2>&1 || ssh-add ~/.ssh/id_ed25519
  ```
- **Windows 11**: the built-in "OpenSSH Authentication Agent" service is present but **disabled by default**. Open `services.msc`, set the service to "Automatic", and start it. Then in PowerShell: `ssh-add $env:USERPROFILE\.ssh\id_ed25519`. Verify with `ssh-add -l`. This persists across reboots.

After loading the key on the host, **rebuild the devcontainer** (Command Palette: *Dev Containers: Rebuild Container*). This is essential; the devcontainer connects to the agent at startup and will not pick up a key added later unless rebuilt.

Verify after rebuild with `jim-signing-status`. A healthy state shows:
```
  gpg.format:         ssh
  user.signingkey:    key::ssh-ed25519 AAAA...
  commit.gpgsign:     true
  environment:        local devcontainer (or other)
  ssh agent:          forwarded, 1 key(s) loaded
```

**Registering your SSH key as a signing key on GitHub:**

The same physical SSH key can be used for both authentication and signing, but GitHub tracks them as *separate key registrations*. If you have only added your key as an authentication key, commits will be signed but GitHub will display them as "Unverified". To fix:

1. Visit https://github.com/settings/keys
2. Click "New SSH key"
3. Set "Key type" to **Signing Key** (not Authentication Key)
4. Paste the same public key text as your authentication key
5. Save

Commits made with that key will then show as "Verified" on GitHub.

**What the pre-commit hook checks:**

The hook at `.githooks/pre-commit` runs automatically before every `git commit` (once `core.hooksPath=.githooks` is set, which the devcontainer setup script does). It verifies:

1. `commit.gpgsign` is `true`
2. A signing mechanism is currently available (SSH agent with keys, or `gh-gpgsign` in Codespaces)
3. `user.signingkey` is set (for SSH signing)

If any check fails, the hook prints a prominent error with recovery steps and refuses the commit. To bypass the local hook in a genuine emergency: `git commit --no-verify`. This is rarely useful: the `main` ruleset enforces `required_signatures` server-side (see section 7 below), so an unsigned commit is rejected at push/merge time regardless of the local hook.

**Working outside the devcontainer:**

If you work directly against a local clone without the devcontainer (rare), you need to set up signing manually and install the hook:

```bash
git config --local core.hooksPath .githooks
git config --global gpg.format ssh
git config --global user.signingkey "key::$(ssh-add -L | head -1)"
git config --global commit.gpgsign true
git config --global tag.gpgsign true
```

### 7. Branch Protection Ruleset

The `main` branch is protected by the **"Protect Main"** repository ruleset, which enforces the quality and supply chain gates that CI provides. The ruleset ensures that CI is load-bearing: checks cannot be bypassed by merging a red PR or pushing directly to `main`.

**Enforced rules:**

| Rule | Purpose |
|------|---------|
| **Require pull request** | All changes to `main` must land via a PR; direct pushes are blocked. Even emergency fixes go through a PR. |
| **Require status checks to pass** | Every required CI check must pass before merge. See the check list below. |
| **Branches must be up to date** | PRs must be rebased onto the latest `main` before merge, so CI results reflect the actual merge state. |
| **Require conversation resolution** | All review comment threads must be resolved before merge. |
| **Require signed commits** | Every commit must carry a verified signature (`required_signatures`); unsigned commits are rejected at push/merge time. See section 6 for how each environment signs. |
| **No deletion** | `main` cannot be deleted. |
| **No force-push** | History on `main` cannot be rewritten. |

**Required status checks:**

| Check name | Source | What it validates |
|------------|--------|-------------------|
| `build-and-test` | CI workflow | .NET build, .NET tests, PowerShell Pester tests |
| `discover-base-images` | CI workflow | Production Dockerfile digest-pinning policy |
| `scan-base-images-summary` | CI workflow | All base image vulnerability scans passed (aggregates dynamic matrix legs) |
| `Analyze (actions)` | CodeQL workflow (`.github/workflows/codeql.yml`) | Static analysis of GitHub Actions workflows |
| `Analyze (csharp)` | CodeQL workflow (`.github/workflows/codeql.yml`) | Static analysis of C# code |
| `Analyze (javascript-typescript)` | CodeQL workflow (`.github/workflows/codeql.yml`) | Static analysis of JavaScript/TypeScript code |
| `claude-review` | Claude Code Review workflow | Automated code review on every PR |

**Why `scan-base-images-summary` exists:** the `scan-base-images` job uses a dynamic matrix whose leg names embed image digests (e.g. `scan-base-images (src/JIM.Web/Dockerfile, 10, mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:...)`). These names change with every base image update, making them unsuitable as required status checks. The summary job aggregates all matrix legs into a single stable check name.

**Human review:** the required approving review count is currently set to zero; the automated `claude-review` check provides a consistent independent review baseline across all PRs. As the team grows, human reviewer requirements will be layered onto the ruleset without restructuring.

**Signed commits (planned):** server-side enforcement of signed commits via `required_signatures` is deferred until all contributor environments are reliably producing signed commits. See section 6 above for the current local enforcement via pre-commit hook.

**Bypass actors:** none. No user or automation can bypass the ruleset.

## Testing Expectations

### Test-Driven Development (TDD)

JIM follows TDD as the standard development practice. Tests are written **before** the implementation they cover, not after.

**The Red → Green → Refactor cycle:**

1. **Red**: Write a failing test that describes the expected behaviour. Run it and confirm it fails (not just fails to compile; it must execute and fail the assertion).
2. **Green**: Write the minimum production code needed to make the test pass. Run the test and confirm it is green.
3. **Refactor**: Clean up the implementation and tests without breaking anything.

**Bug fix workflow:**
1. Write a test that **reproduces the bug**; it must fail before any fix is applied
2. Implement the fix
3. Run the test; it must now pass
4. Commit both the test and the fix together

This workflow is enforced because a test written after a fix cannot prove the fix was necessary; it could pass even on the broken code. The failing test is the evidence that the fix works.

**What this means in practice:**
- When investigating a bug, write the test as soon as you understand the failure condition, before touching production code
- When adding a feature, write tests for each acceptance criterion before implementing it
- Never open a PR where tests were written after the implementation without explicit justification

### Unit Tests
- Test business logic in `JIM.Application` servers
- Mock dependencies using Moq
- Use MockQueryable for EF Core query testing
- Aim for >70% code coverage on core logic

### Worker Tests
- Test synchronisation processors with mocked DbContext
- Use `MockFileConnector` for file-based import scenarios
- Located in `test/JIM.Worker.Tests/`

### Workflow Tests
Workflow tests sit between unit tests and integration tests - they test multi-step sync scenarios using real business logic but with mock connectors and in-memory database.

**Key Components** (in `test/JIM.Workflow.Tests/`):
- `WorkflowTestHarness`: Orchestrates multi-step test execution
- `WorkflowStateSnapshot`: Captures MVO, CSO, and PendingExport state after each step
- `MockCallConnector`: Call-based mock connector in `src/JIM.Connectors/Mock/`

**Benefits**:
- Fast execution (seconds vs minutes for integration tests)
- State snapshots after each step for diagnostics
- Reproducible scenarios with configurable fake data
- No external dependencies (LDAP, AD, etc.)

**Example**:
```csharp
[Test]
public async Task ProvisioningWorkflow_CompleteCycle_SucceedsAsync()
{
    // Setup systems and sync rules
    await SetUpProvisioningScenarioAsync(objectCount: 100);

    // Execute import
    _harness.GetConnector("HR").QueueImportObjects(GenerateUsers(100));
    await _harness.ExecuteFullImportAsync("HR");
    var afterImport = await _harness.TakeSnapshotAsync("After Import");

    Assert.That(afterImport.GetCsos("HR").Count, Is.EqualTo(100));

    // Execute sync and export evaluation
    await _harness.ExecuteFullSyncAsync("HR");
    await _harness.ExecuteExportEvaluationAsync("HR");
    var afterExportEval = await _harness.TakeSnapshotAsync("After Export Eval");

    // Verify PendingExports have CSO FKs
    Assert.That(afterExportEval.GetPendingExportsWithNullCsoFk(), Is.Empty);
}
```

**Running Workflow Tests**:
```bash
# Run all workflow tests
dotnet test test/JIM.Workflow.Tests/

# Run explicit tests (tests for known bugs)
dotnet test --filter "TestCategory=Explicit"
```

### Integration Tests
- Test repository implementations against PostgreSQL
- Use test containers or dedicated test database
- Verify migrations work correctly
- See [INTEGRATION_TESTING.md](INTEGRATION_TESTING.md) for setup instructions

### Naming Conventions
```csharp
[Test]
public void GetObjectAsync_WithValidId_ReturnsObject() { }

[Test]
public void GetObjectAsync_WithInvalidId_ReturnsNull() { }
```

## Common Patterns

### 1. Server Pattern
Business logic organised into domain-specific servers:
```csharp
public class MetaverseServer
{
    private readonly IRepository _repository;

    public MetaverseServer(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<MetaverseObject> GetObjectAsync(Guid id)
    {
        return await _repository.Metaverse.GetObjectAsync(id);
    }

    // More metaverse operations...
}
```

Accessed via `JimApplication` facade:
```csharp
public class JimApplication
{
    public MetaverseServer Metaverse { get; }
    public ConnectedSystemServer ConnectedSystems { get; }
    public SecurityServer Security { get; }
    // ... other servers

    public JimApplication(IRepository repository)
    {
        Metaverse = new MetaverseServer(repository);
        ConnectedSystems = new ConnectedSystemServer(repository);
        // ... initialise others
    }
}
```

### 2. Repository Pattern
Abstract data access behind interfaces:
```csharp
public interface IRepository
{
    IMetaverseRepository Metaverse { get; }
    IConnectedSystemsRepository ConnectedSystems { get; }
    ISecurityRepository Security { get; }
    // ... other repositories
}
```

**Rule**: Application layer depends on `IRepository`, not concrete implementations.

### 3. Connector Pattern
Implement connectors for external systems:
```csharp
public interface IConnector
{
    Task<ConnectorCapabilities> GetCapabilitiesAsync();
    Task<bool> TestConnectionAsync();
    // ... other required methods
}

// Optional interfaces for specific capabilities
public interface IConnectorImportUsingCalls : IConnector
{
    Task<List<ConnectedSystemObject>> ImportAsync(ConnectedSystem system);
}

public interface IConnectorExportUsingCalls : IConnector
{
    Task<ConnectorExportResult> ExportAsync(
        ConnectedSystemObject cso, PendingExport export,
        CancellationToken cancellationToken = default);
}
```

**Connector Capabilities**: Connectors declare capabilities via `IConnectorCapabilities` properties:
- `SupportsExport`, `SupportsImport`, `SupportsDeltaImport`, etc.
- `SupportsParallelExport`: when `true`, the Connected System UI shows the `MaxExportParallelism` setting, enabling parallel batch processing with separate DbContext and connector instances per batch

**Rule**: Keep connectors stateless. Store configuration in `ConnectedSystem.Configuration`.

### 4. Activity Logging
Log all significant operations for audit:
```csharp
var activity = new Activity
{
    Name = "Synchronisation",
    Description = $"Full sync for {connectedSystem.Name}",
    StartTime = DateTime.UtcNow,
    Status = ActivityStatus.InProgress
};

await _repository.Activity.CreateAsync(activity);

try
{
    // Perform operation
    activity.Status = ActivityStatus.Success;
}
catch (Exception ex)
{
    activity.Status = ActivityStatus.Failed;
    activity.ErrorMessage = ex.Message;
    throw;
}
finally
{
    activity.EndTime = DateTime.UtcNow;
    await _repository.Activity.UpdateAsync(activity);
}
```

## Development Environment

JIM uses GitHub Codespaces to provide a fully configured development environment with all dependencies pre-installed.

**Features**:
- Pre-installed .NET 10.0 SDK
- Docker and Docker Compose
- PostgreSQL 18 with optimised memory settings
- VS Code with recommended extensions
- Pre-configured shell aliases for common tasks

**Quick Start**:
1. Open repository in GitHub
2. Click **Code** > **Codespaces** > **Create codespace on main**
3. Wait for provisioning (automatic setup via `.devcontainer/setup.sh`)
4. Use shell aliases: `jim-db`, `jim-web`, `jim-stack`, etc.

> **Note**: The setup script automatically creates a `.env` file with development defaults. SSO is pre-configured for the bundled Keycloak; sign in with `admin` / `admin`. You can also set a `DOTENV_BASE64` GitHub Codespaces secret to restore your own `.env` file automatically.

**Available Shell Aliases**:
- `jim` - List all available jim aliases
- `jim-compile` - Build entire solution (dotnet build)
- `jim-test` - Run all tests
- `jim-db` - Start PostgreSQL (local debugging workflow)
- `jim-db-stop` - Stop PostgreSQL
- `jim-migrate` - Apply EF Core migrations
- `jim-stack` - Start Docker stack (includes bundled Keycloak)
- `jim-stack-logs` - View Docker stack logs
- `jim-stack-down` - Stop Docker stack
- `jim-keycloak` - Start Keycloak only (for local debugging workflow)
- `jim-keycloak-stop` - Stop Keycloak
- `jim-keycloak-logs` - View Keycloak logs

**Docker Builds** (rebuild and start services):
- `jim-build` - Build all services + start
- `jim-build-light` - Start db + Keycloak, run JIM.Web natively
- `jim-build-web` - Build jim.web + start
- `jim-build-worker` - Build jim.worker + start
- `jim-build-scheduler` - Build jim.scheduler + start

**Reset**:
- `jim-reset` - Reset JIM (delete database and logs volumes)

**Planning**:
- `jim-prd` - Create a new PRD from template (prompts for feature name)

**Development Workflows**:
1. **Local Debugging** (Recommended): Use `jim-build-light` to start db + Keycloak and run JIM.Web natively, then F5 to debug
2. **Full Stack**: Use `jim-stack` to run all services in containers

**Development URLs** (Docker stack):

| URL | Description |
|-----|-------------|
| `http://localhost:5200` | JIM Web UI |
| `http://localhost:5200/api/reference` | Scalar API reference (available in every environment) |
| `http://localhost:5200/dev/error-pages` | Error page preview (Development only) |
| `http://localhost:8181` | Keycloak admin console (`admin` / `admin`) |

**Git Configuration**:

VS Code automatically copies your host machine's `~/.gitconfig` into devcontainers (controlled by the `dev.containers.copyGitConfig` setting, enabled by default). This means you should configure Git on your **host machine** and it will be available in all devcontainers automatically.

Required setup on your **host machine** (one-time):
```bash
git config --global user.name "Your Name"
git config --global user.email "your@email.com"
```

Optional - SSH commit signing (recommended):
```bash
git config --global gpg.format ssh
git config --global commit.gpgsign true
git config --global user.signingkey "key::ssh-ed25519 AAAA... your-comment"
```

Notes:
- Use the `key::` prefix for `user.signingkey` so the key is stored as a literal string rather than a file path (file paths from the host won't exist inside the container)
- VS Code automatically forwards the SSH agent into the container, so SSH authentication for `git push`/`pull` works without copying keys
- VS Code also injects a credential helper that forwards HTTPS credential requests back to the host
- If `user.name` or `user.email` are missing in the container, it means they are not set on your host - configure them there, then rebuild the container

**Technical Details**:
- PostgreSQL is auto-tuned for the devcontainer's CPU/RAM during setup (see `jim-postgres-tune`)
- Port forwarding configured for Web + API (5200)
- Compose file layering: `docker-compose.yml` → `docker-compose.override.yml` → `docker-compose.local.yml` (gitignored, auto-tuned)
- Use VS Code database extensions (e.g., PostgreSQL) to connect to the database on port 5432

## Environment Configuration

Configuration via environment variables (defined in `.env`). See `.env.example` for detailed documentation.

### Database
- `JIM_DB_HOSTNAME`: PostgreSQL host
- `JIM_DB_NAME`: Database name
- `JIM_DB_USERNAME`: Database user
- `JIM_DB_PASSWORD`: Database password
- `JIM_DB_LOG_SENSITIVE_INFO`: Log sensitive SQL data (development only)

### SSO/Authentication (IDP-Agnostic)
JIM works with any OIDC-compliant Identity Provider (Entra ID, Okta, Auth0, Keycloak, AD FS, etc.).

**Development**: The devcontainer ships a bundled Keycloak instance, pre-configured with a `jim` realm and client. SSO works out of the box; sign in with `admin` / `admin`. The Keycloak admin console is available at `http://localhost:8181`. Use `jim-keycloak`, `jim-keycloak-stop`, and `jim-keycloak-logs` to manage it independently of the full stack.

**Production**: Override the `JIM_SSO_*` variables with your provider's settings. See the [SSO Setup Guide](SSO_SETUP_GUIDE.md).

- `JIM_SSO_AUTHORITY`: OIDC authority URL (e.g., `https://login.microsoftonline.com/{tenant-id}/v2.0`)
- `JIM_SSO_PUBLIC_AUTHORITY`: Optional client-facing authority URL. Only set when the backend and clients reach the identity provider on different URLs (dev devcontainer, split-horizon reverse proxies). Returned to interactive clients via `/api/v1/auth/config`. Backend token validation always uses `JIM_SSO_AUTHORITY`.
- `JIM_SSO_CLIENT_ID`: OIDC client/application ID (confidential client for the Blazor UI)
- `JIM_SSO_PUBLIC_CLIENT_ID`: Optional client ID for interactive public clients (PowerShell module). Required when the IdP mandates separate confidential and public client registrations (Keycloak); optional for IdPs that allow one registration to host both platforms (Entra ID, AD FS). Falls back to `JIM_SSO_CLIENT_ID` when unset.
- `JIM_SSO_SECRET`: OIDC client secret
- `JIM_SSO_API_SCOPE`: API scope for JWT bearer authentication (e.g., `api://{client-id}/access_as_user`)

### User Identity Mapping
JIM uses standard OIDC claims (`sub`, `name`, `given_name`, `family_name`, `preferred_username`) for user mapping.

- `JIM_SSO_CLAIM_TYPE`: JWT claim for unique user identification (recommended: `sub`)
- `JIM_SSO_MV_ATTRIBUTE`: Metaverse attribute to store the identifier (default: `Subject Identifier`)
- `JIM_SSO_INITIAL_ADMIN`: Claim value for initial admin user

> **Tip**: Log into JIM and visit `/claims` to see your OIDC claims and find your `sub` value.

### Logging
- `JIM_LOG_LEVEL`: Minimum log level (Verbose, Debug, Information, Warning, Error, Fatal)
- `JIM_LOG_PATH`: Directory for log files
- `JIM_LOG_REQUESTS`: Enable verbose HTTP request logging (true/false)

**Rule**: Never hardcode these values. Always use environment variables.

## Docker & Deployment

### Service Architecture
- **jim.web**: Blazor Server UI with integrated REST API at `/api/` (port 5200 HTTP / 5201 HTTPS). Interactive [Scalar](https://scalar.com/) API reference available at `/api/reference` in development (disabled in production).
- **jim.worker**: Background task processor built on `ISyncEngine` / `ISyncRepository` separation (see [Background Processing](#7-background-processing)). Per-task DI isolation, `ParallelBatchWriter` for concurrent writes, and COPY binary protocol for bulk inserts. Supports parallel schedule step execution and configurable LDAP pipelining.
- **jim.scheduler**: Schedule management service with 30-second polling cycle. Detects parallel step groups (steps sharing the same `StepIndex`) and queues them with `ExecutionMode = Parallel` for concurrent worker dispatch.
- **jim.database**: PostgreSQL 18
- **jim.keycloak**: Bundled Keycloak IdP for development SSO (port 8181). Pre-configured with a `jim` realm and client. Not included in production deployments.

**Database Access**:
- Use VS Code database extensions (e.g., PostgreSQL) to connect to the database on port 5432

### Docker Compose
- Base: `docker-compose.yml`: production/deployment defaults
- Dev overrides: `docker-compose.override.yml`: tracked dev settings (ports, env, LANG, conservative DB)
- Local tuning: `docker-compose.local.yml`: gitignored, auto-generated machine-specific DB tuning
- The `jim-*` aliases automatically include the local overlay when present

### PostgreSQL Tuning

#### Development (Automatic)

In devcontainers (Codespaces and local), PostgreSQL is **automatically tuned** during setup. The script `.devcontainer/postgres-tune.sh` detects available CPU/RAM and generates gitignored overlay files (`docker-compose.local.yml` and `db.local.yml`) with optimal [PGTune](https://pgtune.leopard.in.ua/) OLTP settings.

To re-tune after changing devcontainer resources:
```bash
jim-postgres-tune
jim-db-stop && jim-db
```

See `.devcontainer/POSTGRES_TUNING.md` for full details on tuning formulas and parameters.

#### Production (Manual)

The default PostgreSQL settings in `docker-compose.yml` are tuned for a **64GB Windows / 32GB WSL / 16 core** system. For other production environments, use [PGTune](https://pgtune.leopard.in.ua/) to generate settings, then override `command` and `shm_size` in a compose override file.

**Key settings to adjust:**
- `shared_buffers`: typically ~25% of available host RAM
- `effective_cache_size`: typically ~75% of available host RAM
- `shm_size` (Docker): must be >= `shared_buffers` with ~25% headroom

**Sizing reference:**

| Host RAM | `shared_buffers` | `shm_size` |
|----------|------------------|------------|
| 8GB      | 2GB              | 3gb        |
| 16GB     | 4GB              | 5gb        |
| 32GB     | 8GB              | 10gb       |
| 64GB     | 16GB             | 20gb       |
| 128GB    | 32GB             | 40gb       |

> **Warning**: If `shm_size` is smaller than `shared_buffers`, PostgreSQL will crash under load. Docker defaults `shm_size` to only 64MB, which is insufficient for any non-trivial `shared_buffers` value.

### Building Images
```bash
docker compose build
docker compose up -d
```

### Dependency Pinning and Updates

All dependency updates require human review before merging. Dependabot proposes weekly PRs for all ecosystems (NuGet, Docker, GitHub Actions), but there is no auto-merge - a maintainer must review and merge each PR manually.

#### NuGet Packages

- All NuGet package versions are pinned in `.csproj` files (no floating versions)
- Dependabot proposes weekly PRs for patch and minor version updates (major versions are ignored)
- Before merging a NuGet update PR:
  1. Review the package changelog for breaking changes or behavioural differences
  2. Verify CI build and tests pass
  3. Check for any new known vulnerabilities in transitive dependencies

#### Docker Base Images

All production Dockerfiles pin their dependencies for reproducible, auditable builds:

- **Base image digests**: Each `FROM` line includes a `@sha256:` digest, locking the exact OS + runtime layer. This prevents builds on different dates producing different images.
- **Functional apt packages**: Libraries that JIM calls at runtime (libldap, cifs-utils, krb5) are pinned to exact versions (e.g., `libldap2=2.6.10+dfsg-0ubuntu0.24.04.1`).
- **Diagnostic utilities**: Tools like `curl` and `iputils-ping` are not pinned, as they are only used for health checks and debugging, not functional code paths.

**The digest-pinning policy is machine-enforced.** Every production Dockerfile carries the directive `# jim-compliance: production-image` near the top of the file. The CI workflow (`.github/workflows/ci.yml`, `discover-base-images` job) scans the repository for Dockerfiles with this directive, parses every external `FROM` line, and fails the build if any of them is missing a `@sha256:` digest. The underlying script is [`.github/scripts/discover-base-images.ps1`](../.github/scripts/discover-base-images.ps1).

Dockerfiles without the compliance directive (`.devcontainer/Dockerfile`, the integration test fixture images under `test/integration/docker/`) are deliberately out of scope. They are dev or test infrastructure, not customer-shipped artefacts, and are expected to track upstream tags rather than pinned digests. **Do not add the directive to a non-production Dockerfile, and do not remove it from a production one.**

When adding a new production Dockerfile:

1. Add `# jim-compliance: production-image` to the file (see `src/JIM.Web/Dockerfile` for the canonical format)
2. Ensure every external `FROM` line uses `@sha256:` digest pinning
3. That's it. The discovery script finds the new file automatically; no workflow or config update is required.

Vulnerability scanning runs against every discovered production base image on every push and PR. Findings are surfaced in the GitHub Security tab via SARIF upload in addition to the Actions log, so they are visible to reviewers and auditable after the fact.

**Why this matters**: `System.DirectoryServices.Protocols` (the .NET LDAP client) P/Invokes into the native `libldap` shared library at runtime. An incompatible libldap version could cause silent behavioural differences or crashes during LDAP/AD operations.

Before merging a Docker digest update PR:

1. Check if apt package versions need updating against the new base image
2. Run integration tests (especially LDAP connector tests) against the updated image
3. Update pinned versions in the Dockerfile if they have changed

To check available package versions in a new base image:
```bash
docker run --rm <image>@<new-digest> bash -c \
  "apt-get update -qq && apt-cache policy libldap-common libldap2 cifs-utils libgssapi-krb5-2"
```

##### Automated apt pin update detection (`apt-pin-check`)

Checking those pinned apt versions by hand (step 1 above) is easy to forget, and the two systems you might expect to catch a stale pin do not:

- **Dependabot** only parses `FROM` lines for the Docker ecosystem; it never sees a `pkg=version` pinned inside a `RUN apt-get install`.
- **The `scan-base-images` Trivy job** scans each base image *by digest*. The packages JIM pins are installed *on top of* the base image, so they exist only in the built JIM image, which that job does not scan. (They are scanned at release time by the built-image Trivy step in `release.yml`, but only for HIGH/CRITICAL and only when a release is cut.)

The `apt-pin-check` workflow ([`.github/workflows/apt-pin-check.yml`](../.github/workflows/apt-pin-check.yml)) closes the gap. Daily, and on demand via *Run workflow*, it:

1. Discovers the same production Dockerfiles, parses every pinned `pkg=version`, and attributes each pin to the base image of the build stage it is installed into.
2. Queries that base image's archive for the current candidate version and flags any pin that is behind, noting whether the update comes from the `-security` pocket.
3. **Validates that the candidate is actually installable** in that base image (`apt-get install --dry-run`) before proposing it. This matters because CI does not build the JIM images on a PR, so the bot must not propose an unbuildable version.
4. Raises, or updates in place, a single pull request bumping the validated pins, for the same human review every other dependency update gets.

Backing scripts: [`.github/scripts/check-apt-pins.ps1`](../.github/scripts/check-apt-pins.ps1) (detection, installability validation, and the `-Apply` rewrite) and [`.github/scripts/open-pin-pr.ps1`](../.github/scripts/open-pin-pr.ps1) (the shared signed-commit PR opener, also used by `tooling-pin-check` below). Both run locally from the repository root for ad hoc checks; `check-apt-pins.ps1` needs Docker.

**Identity.** The PR is opened by the `jim-automation` GitHub App, an org-owned service principal, not a personal token. This is deliberate:

- `main` requires signed commits. The bot creates its commit through the GitHub API (the GraphQL `createCommitOnBranch` mutation), which GitHub signs as *Verified*; an App installation token authorises it.
- A PR opened with the default `GITHUB_TOKEN` does not trigger CI, so it could never satisfy branch protection. App-authored events do trigger CI, so the bump PR runs the required checks and is mergeable.

The App's credentials are the `APT_PIN_BOT_APP_ID` and `APT_PIN_BOT_PRIVATE_KEY` repository secrets; the workflow mints a short-lived (1 hour) installation token from them per run, so nothing personal is stored or used. The App is scoped to Contents and Pull requests (read/write) on this repository only.

**Adding a new pinned apt package** needs no change here: pin it as `pkg=version` in a production Dockerfile and the next run picks it up automatically, the same zero-config story as digest discovery.

Evaluate an `apt-pin-check` PR the same way as a Docker digest update: review the package changelog, run the LDAP/AD integration tests for any libldap or krb5 change, then squash-merge.

##### Automated tooling pin update detection (`tooling-pin-check`)

A handful of development tools are pinned to exact versions in places Dependabot does not read, for the same reason apt pins are invisible to it: they are not in a manifest its ecosystems parse.

- **`@playwright/mcp`** (the Playwright MCP server used for UI validation) is pinned in **both** `.devcontainer/setup.sh` (install-at-create) and `.mcp.json` (launch-at-runtime). The two must move in lockstep or the installed browser and the running server drift apart.
- **`dotnet-ef`** (the EF Core CLI) is installed via `dotnet tool install` in `.devcontainer/setup.sh`; it is not a `.csproj` reference, so the nuget ecosystem never sees it.

The `tooling-pin-check` workflow ([`.github/workflows/tooling-pin-check.yml`](../.github/workflows/tooling-pin-check.yml)) closes the gap. Weekly, and on demand via *Run workflow*, it reads each pinned version, queries the upstream registry (npm or NuGet) for the latest stable release, and raises (or updates in place) a single PR bumping any that are behind. When a tool is pinned in more than one file, every location is rewritten to the same version, so a Playwright bump can never leave the two files inconsistent. A registry query that fails is treated as a hard error, never as "current", so a stale pin is never silently masked.

Backing scripts: [`.github/scripts/check-tooling-pins.ps1`](../.github/scripts/check-tooling-pins.ps1) (detection via registry HTTP lookups; no Docker needed) and the shared [`.github/scripts/open-pin-pr.ps1`](../.github/scripts/open-pin-pr.ps1). Both run locally from the repository root for ad hoc checks.

**Identity** is the same as `apt-pin-check`, and it reuses the **same** `APT_PIN_BOT` App and its `APT_PIN_BOT_APP_ID` / `APT_PIN_BOT_PRIVATE_KEY` secrets: a signed commit via `createCommitOnBranch`, App-authored so CI's required checks fire and the PR is mergeable. The App is already scoped to Contents and Pull requests (read/write) on this repository, which is all this workflow needs, so adding it required no new credential.

**Adding a new manually-pinned tool:** add an entry to the `$tools` manifest at the top of `check-tooling-pins.ps1` (name, registry, package id, and one regex-located pin site per file). The next run picks it up. This is the single place to look when you pin a new dev tool outside a Dependabot-readable manifest.

Ranges (e.g. the `mkdocs>=1.6,<2` pins in `setup.sh`) are deliberately out of scope: a range already absorbs minor and patch releases, so only the upper bound is a periodic judgment call rather than an automatable bump.

##### When the scan-base-images gate blocks on an upstream-only CVE

The `scan-base-images` CI job fails the build whenever Trivy reports a fixable HIGH or CRITICAL CVE (CVSS >= 7.0) in any production base image. "Fixable" means an upstream-patched version of the affected package exists.

For most JIM-installed packages (the apt versions we pin in our Dockerfiles), "fixable" means we can bump the pinned version ourselves and the gate clears on the next build. But the bulk of packages in a base image come from the **Microsoft `dotnet/<runtime|aspnet|sdk>:10.0-noble` image layer**, not from JIM. We do not control when Microsoft rebuilds those images. Microsoft typically rebuilds on its own cadence (often monthly, sometimes longer); during the gap between an Ubuntu security release and a Microsoft refresh, Trivy can correctly flag a CVE as "fixable upstream" even though we have no way to apply the fix ourselves.

When this happens, every PR and every push to `main` will fail the `scan-base-images` job until either Microsoft publishes a refreshed digest (which Dependabot will then propose) or we apply a manual workaround.

**Response options, in order of preference:**

1. **Wait for the Microsoft rebuild.** This is the default and best response when the CVE risk is acceptable to wait out. Check https://mcr.microsoft.com/en-us/product/dotnet/runtime/tags for the current published digest of `10.0-noble`. If it differs from what is pinned in the Dockerfiles, bump the digest manually (or wait for Dependabot to propose it). This typically takes a few days to a few weeks depending on Microsoft's release cadence.

2. **Suppress the CVE via [`.trivyignore`](../.trivyignore)** when the reported CVE is a verified false positive (the base image is already patched but Trivy over-reports) or when the vulnerability is already mitigated at the application layer (e.g., a NuGet pin overrides the in-box assembly). See "Trivy CVE suppressions" below for the required justification format. This is preferable to option 3 because it scopes the suppression to specific CVEs rather than lowering the entire threshold.

3. **Apply `apt-get upgrade` in the Dockerfile** as a temporary measure if the CVE is severe enough that waiting is not acceptable. This pulls current Ubuntu security patches at *build* time rather than at *base image publish* time. Trade-off: it weakens the reproducibility guarantee of pinning by digest. Only do this for genuinely urgent issues, and revert as soon as Microsoft publishes a refreshed image.

4. **Temporarily lower the gate threshold from HIGH to CRITICAL** in `.github/workflows/ci.yml` (the `Fail build on fixable HIGH/CRITICAL Trivy findings` step) if the blocking CVE is HIGH but not CRITICAL and the work jam is unacceptable. Two-line change. **Revert as soon as the underlying CVE is resolved.**

5. **Dismiss the specific alert** in the GitHub Security tab with a "won't fix - upstream dependency" reason and a comment explaining why. This requires the alert to first reach the Security tab via SARIF upload, which it will on every CI run. Dismissal only suppresses the specific alert; if the same CVE is detected against a different package or a new base image, it reappears.

**Do not** add `continue-on-error: true` to the scan step. That permanently weakens the gate and is not the same as a documented temporary downgrade.

The choice between options 1-5 depends on the specific CVE, its CVSS score, the nature of the affected component, and how long Microsoft is likely to take. There is no pre-baked policy because the right answer is genuinely case-dependent. When in doubt, escalate to a maintainer.

##### Trivy CVE suppressions (`.trivyignore`)

A repo-root [`.trivyignore`](../.trivyignore) file suppresses individual CVEs from the `scan-base-images` job. It is wired into `.github/workflows/ci.yml` via the `trivyignores: '.trivyignore'` input on the `aquasecurity/trivy-action` step; Trivy drops suppressed findings from the SARIF output entirely, so they do not reach the custom PowerShell filter or the GitHub Security tab.

**When to add a suppression:**

- **Verified false positive**: the base image is already patched but Trivy still reports the CVE (common with .NET in-box assemblies where the patched DLL's file version can still read as pre-patch, causing Trivy to match it against the GHSA's NuGet version range).
- **Mitigated at the application layer**: JIM pins a newer NuGet version that overrides the in-box assembly at publish, eliminating the real exploit surface even though the base image still contains the older copy. Cross-reference the mitigating PR.
- **Not exploitable in JIM's usage**: the vulnerable code path is not reached by JIM at runtime. Document which class/API is affected and why it is unreachable.

**When NOT to add a suppression:**

- The CVE is genuinely exploitable and no mitigation exists. Fix it (options 1, 3, or 4 above).
- To skip the scan to unblock work without investigation. Always investigate first.
- Without a concrete mitigation story. "Won't fix" dismissals belong in the GitHub Security tab (option 5), not in the codebase.

**Required format:**

Every CVE entry in `.trivyignore` MUST be preceded by a comment block containing:

1. **CVE ID(s)** being suppressed
2. **Affected component** (package name and version range)
3. **Why Trivy flags it** (false positive mechanism or mitigation chain)
4. **Where the real mitigation lives** (PR number, NuGet pin, or code reference)
5. **Review date** — typically 3 months out. This is the hook to re-check whether the suppression is still needed (e.g., Trivy's matching may have been corrected, or the upstream image may have been rebuilt).

See the existing entries in `.trivyignore` for the canonical format. PR [#581](https://github.com/TetronIO/JIM/pull/581) established this pattern for `CVE-2026-26171` and `CVE-2026-33116`.

**Review discipline:**

Suppressions that pass their review date without action are a quiet failure mode — stale suppressions silently mask real vulnerabilities. When touching `.trivyignore` for any reason, re-evaluate entries whose review date has passed. Remove entries that are no longer justified; extend the review date with a fresh justification if they still are.

#### GitHub Actions

Every third-party and first-party action in `.github/workflows/*.yml` is pinned by its full 40-character commit SHA, with the human-readable version tag preserved as a trailing comment:

```yaml
- uses: actions/checkout@34e114876b0b11c390a56381ad16ebd13914f8d5  # v4.3.1
```

This protects against tag-rewrite attacks: the `v4` tag is mutable and can be silently moved to a malicious commit, but a 40-char commit SHA is immutable. SHA pinning is required for alignment with UK Software Security Code of Practice Principle 5 (Protect the build environment) and GitHub's own [security hardening for GitHub Actions](https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions) guidance.

**Adding a new action:**

1. Find the version tag you want to use (e.g., `v4.3.1`) on the action's repository.
2. Resolve the tag to its commit SHA. For lightweight tags:
   ```bash
   git ls-remote --tags https://github.com/owner/repo.git v4.3.1
   ```
   For annotated tags, dereference with `^{}`:
   ```bash
   git ls-remote --tags https://github.com/owner/repo.git 'v4.3.1^{}'
   ```
   (If the first form returns a SHA and a `v4.3.1^{}` entry exists, the lightweight SHA is the tag object; use the dereferenced SHA as the commit.)
3. Write the reference as `owner/repo@<40-char-sha>  # v4.3.1` (two spaces before the `#`, matching existing style).
4. Pin to a tagged release, not to a moving major-version tag's tip. If a repo's major tag (e.g., `v3`) is ahead of the latest semver release, pin to the latest semver release instead; this is more auditable and matches what Dependabot will track.

**Ongoing updates** are handled by Dependabot, which natively understands SHA-pinned actions. When a new version tag moves the underlying commit, Dependabot opens a PR that updates both the SHA and the version comment. Before merging, review the action's changelog for any changes to inputs, outputs, or behaviour.

### Migrations
Apply migrations on first run:
```bash
docker compose exec jim.web dotnet ef database update
```

## File Connector Setup

The JIM File Connector imports and exports identity data via CSV files. JIM ships with a Docker-managed named volume — `jim-connector-files-volume` — mounted at `/connector-files` inside both the JIM Web and JIM Worker containers. The connector reads and writes paths under that directory. The customer-facing reference is in [docs/connectors/jim-file-connector.md](../docs/connectors/jim-file-connector.md); this section is the developer-flavour view.

### Why a named volume

The worker container runs as the non-root `app` user (UID 1654). Bind-mounting a host directory into the container preserves the host UID, which usually doesn't match — so JIM can't write to it without explicit `chown` or mount-option intervention. Docker-managed named volumes inherit ownership from the container's mount point at first mount, so `app` always has read/write access. For dev and for the default customer deployment we use the named volume; for integration with external systems that write to fixed host paths, customers bind-mount over a subdirectory of `/connector-files`.

This is the same model dev and production use — there is no special dev override.

### Getting files into the volume during dev

Use `docker cp` against the worker container:

```bash
docker cp ./Users.csv jim.worker:/connector-files/Users.csv
```

The integration test harness uses the helper `Copy-CsvToConnectorFiles` in `test/integration/utils/Test-Helpers.ps1` to push test CSVs to `/connector-files/test-data/` — see `Generate-TestCSV.ps1` for the seeding pattern.

### Getting files out of the volume

```bash
docker cp jim.worker:/connector-files/Exports.csv ./Exports.csv
```

For ongoing/automated extraction, customers run a sidecar service or scheduled job that mounts the same volume and ships files elsewhere (SFTP, blob storage, etc.).

### Configuring the File Connector in JIM

When creating a Connected System with the File Connector, set the **File Path** to a path under `/connector-files`, e.g. `/connector-files/Users.csv`. The UI's file browser starts at `/connector-files` and shows what's currently in the volume.

### Network Share Access (Bind-mount over a subdirectory)

When integrating with an external system that writes files to a fixed location (commonly an SMB or NFS share already mounted on the host), bind-mount that path over a subdirectory of `/connector-files`:

```yaml
# In your production overlay or docker-compose.override.yml
services:
  jim.worker:
    volumes:
      - /mnt/hr-extracts:/connector-files/hr-input
  jim.web:
    volumes:
      - /mnt/hr-extracts:/connector-files/hr-input
```

JIM still sees `/connector-files` as a single filesystem; only the `hr-input` subdirectory comes from the host. The File Connector setting becomes `/connector-files/hr-input/employees.csv`.

For bind-mounted host paths, ensure the host files are readable/writable by UID 1654 — either `chown 1654:1654 /mnt/hr-extracts` or set mount options like `uid=1654,gid=1654` for CIFS/NFS.

### Troubleshooting

**File not found** — confirm the file is in the volume: `docker exec jim.worker ls /connector-files`. For bind-mounted subdirs, verify the host path is mounted: `docker compose config | grep connector-files`.

**Access denied during export** — the JIM worker (UID 1654) doesn't have write access to a bind-mounted host path. Either `chown -R 1654:1654 /your/host/path` or set the mount UID. The default named volume doesn't have this problem.

**Permission errors surface as RPEIs** — `Access to the path … is denied` will appear on the Activity for the failing import or export, not be silently swallowed.

## PowerShell Module Development

The JIM PowerShell module (`src/JIM.PowerShell/`) provides cmdlets for scripting and automation. It's designed to work with the JIM API.

> **For production installation** (PowerShell Gallery or air-gapped), see the [Deployment Guide - PowerShell Module](DEPLOYMENT_GUIDE.md#powershell-module). This section covers development and contribution workflows only.

### Module Structure

```
src/JIM.PowerShell/
+-- JIM.psd1              # Module manifest
+-- JIM.psm1              # Module loader
+-- Public/               # Exported cmdlets
|   +-- Activities/       # Get-JIMActivity, Get-JIMActivityItem
|   +-- ApiKeys/          # *-JIMApiKey cmdlets
|   +-- Certificates/     # *-JIMCertificate cmdlets
|   +-- Connection/       # Connect-JIM, Disconnect-JIM, Test-JIMConnection
|   +-- ConnectedSystems/ # *-JIMConnectedSystem cmdlets
|   +-- ExampleData/   # *-JIMExampleData* cmdlets
|   +-- Metaverse/        # *-JIMMetaverse* cmdlets
|   +-- RunProfiles/      # *-JIMRunProfile cmdlets
|   +-- SyncRules/        # *-JIMSyncRule cmdlets
+-- Private/              # Internal helper functions
+-- Tests/                # Pester tests
```

### Loading the Module in Devcontainer

The module is automatically available. Import it with:

```powershell
# Import from the repository
Import-Module ./src/JIM.PowerShell -Force

# Verify it loaded
Get-Module JIM
```

### Connecting to JIM

JIM supports two authentication methods for the PowerShell module:

#### Interactive Browser Authentication (Default)

Opens your default browser for SSO sign-in. This is the recommended method for interactive sessions:

```powershell
# Connect interactively - opens browser for SSO authentication
Connect-JIM -Url "http://localhost:5200"

# Force re-authentication even if already connected
Connect-JIM -Url "http://localhost:5200" -Force

# Specify custom timeout for browser authentication (default: 300 seconds)
Connect-JIM -Url "http://localhost:5200" -TimeoutSeconds 120
```

#### API Key Authentication (Automation)

Use API keys for scripts and automation where interactive sign-in isn't possible:

```powershell
# Connect using an API key (recommended for automation)
Connect-JIM -Url "http://localhost:5200" -ApiKey "jim_xxxxxxxxxxxx"
```

#### Testing and Managing Connections

```powershell
# Test the connection and view status
Test-JIMConnection

# Example output for OAuth connection:
# Connected      : True
# Url            : http://localhost:5200
# AuthMethod     : OAuth
# Status         : Healthy
# Message        : Connection successful
# TokenExpiresAt : 28/01/2026 22:30:00

# Quick check if connected (returns $true/$false)
Test-JIMConnection -Quiet

# Disconnect when done
Disconnect-JIM
```

### Running Pester Tests

```bash
# Run all PowerShell module tests
jim-test-ps
```

### Adding a New Cmdlet

1. **Create the cmdlet file** in the appropriate `Public/` subdirectory:
   ```powershell
   # Public/MyCategory/Verb-JIMNoun.ps1
   function Verb-JIMNoun {
       [CmdletBinding()]
       param(
           [Parameter(Mandatory = $true)]
           [string]$RequiredParam,

           [Parameter()]
           [string]$OptionalParam
       )

       # Ensure connected
       if (-not $script:JIMConnection) {
           throw "Not connected to JIM. Use Connect-JIM first."
       }

       # Make API call
       $response = Invoke-JIMApiRequest -Method Get -Endpoint "api/v1/endpoint"
       return $response
   }
   ```

2. **Add to module manifest** (`JIM.psd1`):
   - Add to `FunctionsToExport` array

3. **Write Pester tests** in `Tests/`:
   ```powershell
   # Tests/MyCategory.Tests.ps1
   Describe "Verb-JIMNoun" {
       BeforeAll {
           Import-Module $PSScriptRoot/../JIM.psd1 -Force
       }

       It "Should throw when not connected" {
           { Verb-JIMNoun -RequiredParam "test" } | Should -Throw "*Not connected*"
       }

       # More tests...
   }
   ```

4. **Test the cmdlet**:
   ```powershell
   Import-Module ./src/JIM.PowerShell -Force
   # Start JIM stack first: jim-stack
   Connect-JIM -BaseUrl "http://localhost:5200" -ApiKey "your-api-key"
   Verb-JIMNoun -RequiredParam "value"
   ```

### Naming Conventions

- **Cmdlet names**: Use approved PowerShell verbs (`Get`, `Set`, `New`, `Remove`, `Invoke`, `Start`, `Stop`)
- **Noun prefix**: Always use `JIM` prefix (e.g., `Get-JIMActivity`, `New-JIMSyncRule`)
- **Parameters**: Use PascalCase, support both ID and Name where applicable
- **British English**: Use British spelling in descriptions and comments

### Common Patterns

**Name-based parameter alternatives**:
```powershell
# Support both -ConnectedSystemId and -ConnectedSystemName
param(
    [Parameter(Mandatory = $true, ParameterSetName = "ById")]
    [Guid]$ConnectedSystemId,

    [Parameter(Mandatory = $true, ParameterSetName = "ByName")]
    [string]$ConnectedSystemName
)

# Resolve name to ID if needed
if ($PSCmdlet.ParameterSetName -eq "ByName") {
    $system = Get-JIMConnectedSystem -Name $ConnectedSystemName
    if (-not $system) {
        throw "Connected system '$ConnectedSystemName' not found"
    }
    $ConnectedSystemId = $system.Id
}
```

**Using the internal API helper**:
```powershell
# GET request
$result = Invoke-JIMApiRequest -Method Get -Endpoint "api/v1/connected-systems"

# POST with body
$body = @{ Name = "Test"; Description = "Test system" }
$result = Invoke-JIMApiRequest -Method Post -Endpoint "api/v1/connected-systems" -Body $body

# DELETE
Invoke-JIMApiRequest -Method Delete -Endpoint "api/v1/connected-systems/$id"
```

## Common Development Tasks

> **Note**: All `dotnet` commands below work out of the box in Codespaces. Use shell aliases like `jim-compile`, `jim-test`, and `jim-migrate` for convenience. Run `jim` to see all available aliases.

### Adding a New Connector
1. Create class implementing `IConnector` (and capability interfaces)
2. Add to `src/JIM.Connectors` project or create new project
3. Register in DI container
4. Add `ConnectorDefinition` entry in database seeding
5. Create configuration UI in JIM.Web
6. Add tests in `test/JIM.Connectors.Tests`

### Adding a New Metaverse Object Type
1. Add enum value to `MetaverseObjectType`
2. Create seeding data in `SeedingServer`
3. Define required attributes
4. Add UI pages for management
5. Update sync rules to support new type

### Adding a New API Endpoint
1. Add method to appropriate controller in `src/JIM.Web/Controllers/Api/`
2. Use DTOs for request/response (in `src/JIM.Web/Models/Api/`)
3. Add XML comments for OpenAPI documentation
4. Add authorisation attributes if needed
5. Test via the Scalar API reference at `/api/reference` (available in every environment)

### Modifying Database Schema
1. Update entity classes in `src/JIM.Models/Models/`
2. Update `JimDbContext` if needed (Fluent API)
3. Create migration: `dotnet ef migrations add MigrationName --project src/JIM.PostgresData`
4. Review generated migration
5. Test migration: `dotnet ef database update --project src/JIM.PostgresData`
6. Commit migration files

## Troubleshooting

**Build Errors**:
- Ensure .NET 10.0 SDK installed: `dotnet --version` (should show 10.0.x)
- Restore NuGet packages: `dotnet restore JIM.sln` or use `jim-compile` alias
- Clean build: `dotnet clean && dotnet build JIM.sln`

**Database Connection**:
- Start PostgreSQL: `jim-db`
- Verify PostgreSQL running: `docker compose ps`
- Check connection string in `.env`
- Apply migrations: `jim-migrate`

**Authentication Failures**:
- Verify SSO configuration in `.env`
- Check OIDC authority is accessible
- Review callback URL registration in IdP
- Ensure callback URLs include your Codespaces URL

**Worker Not Processing Tasks**:
- Check worker service is running: `docker compose logs jim.worker`
- Verify tasks in database: Check `WorkerTasks` table
- Review worker logs for errors

**Codespaces Issues**:
- **Port Forwarding**: Ensure ports are set to public if accessing from external browser
- **PostgreSQL Memory**: If database crashes (OOM), check that `shm_size` and `shared_buffers` are tuned for your host - see [PostgreSQL Tuning](#postgresql-tuning-important) above
- **Docker Issues**: Restart Codespace or rebuild container if Docker daemon issues occur
- **Missing Aliases**: Run `source ~/.zshrc` or restart terminal if shell aliases not available

**Commit Signing Issues**:
- **"Commit signing is not enabled" when committing**: the pre-commit hook detected that `commit.gpgsign` is false. Run `jim-setup-signing` to reconfigure, or see [Commit Signing](#6-commit-signing-mandatory) for manual setup.
- **"SSH agent not available or has no keys" when committing**: your host machine's SSH agent is not forwarding a key into the devcontainer. Follow the host-side prerequisites in the [Commit Signing](#6-commit-signing-mandatory) section for your OS, then rebuild the devcontainer. The hook will not run cleanly until the agent is fully configured.
- **Commits show as "Unverified" on GitHub**: your SSH key is signing commits correctly but has not been registered as a *Signing Key* on GitHub. Visit https://github.com/settings/keys and add your public key a second time with type "Signing Key" (this is separate from the authentication key registration). See [Commit Signing](#6-commit-signing-mandatory) for details.
- **Signing worked yesterday, fails today**: the host SSH agent may have been restarted or lost its keys. Run `ssh-add -l` on the host to check, add the key back if missing, then either rebuild the devcontainer or run `jim-setup-signing` inside the container to re-verify.
- **"Current user GPG signing disabled" in a Codespace**: `gh-gpgsign` signs via the GitHub API, which is refusing because GPG verification is not enabled for your account. Enable it at github.com/settings/codespaces (*GPG verification*, then allow this repository or all repositories), then restart the Codespace (Stop then Start, or rebuild) so a fresh token carries the capability; the running token will not pick it up until then. See [Commit Signing](#6-commit-signing-mandatory).

**Works In Dev But Fails In CI**: the devcontainer image (`mcr.microsoft.com/devcontainers/dotnet:1-10.0-bookworm`) is not digest-pinned; it tracks the upstream `:1-10.0-bookworm` tag which can change over time. If you see a build or test succeed in your devcontainer but fail in CI (or vice versa), check whether the devcontainer image has drifted from what CI is running. Rebuild the devcontainer to pick up the latest image. If this category of problem becomes frequent, consider revisiting whether to digest-pin the devcontainer image (currently left unpinned to reduce maintenance burden since dev-only images are not part of customer-shipped artefacts).

## Best Practices Summary

1. **Follow the layers**: Respect architectural boundaries
2. **Use dependency injection**: Constructor injection for all services
3. **Async all the way**: All I/O operations async
4. **Repository pattern**: Never access DbContext directly from application layer
5. **Log comprehensively**: Use Serilog with structured logging
6. **Sanitise log arguments**: Wrap all user-controlled `string?` values with `LogSanitiser.Sanitise()` from `JIM.Utilities` before passing them as arguments to any log call; prevents log injection (CWE-117). Integers, GUIDs, enums, and DateTimes are safe without wrapping.
7. **Validate input**: All user input validated at API/UI boundary
8. **Handle errors gracefully**: Try-catch, log, return meaningful responses
9. **Test thoroughly**: Unit tests for business logic, integration tests for data layer
10. **Secure by default**: OIDC/SSO required, no hardcoded secrets
11. **Document via code**: XML comments, clear naming, self-documenting code

## Resources

- **Repository**: https://github.com/TetronIO/JIM
- **Licensing**: https://junctional.io/license
- **Documentation**: `/home/user/JIM/README.md`
- **.NET Docs**: https://learn.microsoft.com/dotnet/
- **EF Core**: https://learn.microsoft.com/ef/core/
- **Blazor**: https://learn.microsoft.com/aspnet/core/blazor/
- **MudBlazor**: https://mudblazor.com/

---

**Last Updated**: 2026-04-22
**Version**: 1.5
**Applies to**: JIM v0.10.x (NET 10.0)
