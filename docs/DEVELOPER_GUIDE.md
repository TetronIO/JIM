# JIM Developer Guide

> **Project**: Junctional Identity Manager (JIM)
> **Purpose**: Enterprise-grade identity management system for synchronisation, governance, and domain migrations
> **License**: Source-available, commercial license required for production use

## Overview

JIM is a .NET-based Identity Management (IDM) system implementing the metaverse pattern for centralised identity governance. It synchronises identities across heterogeneous systems (Active Directory, LDAP, files, databases, etc.) with bi-directional attribute flows, provisioning rules, and compliance tracking.

## Architecture Principles

### 1. Layered Architecture
- **Presentation Layer**: JIM.Web (Blazor Server with integrated REST API at `/api/`)
- **Application Layer**: JIM.Application (business logic, domain servers)
- **Domain Layer**: JIM.Models (entities, DTOs, interfaces)
- **Data Layer**: JIM.Data (abstractions), JIM.PostgresData (implementation)
- **Integration Layer**: JIM.Connectors (external systems), JIM.Functions (extensibility)

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
- **Functions**: Custom business logic via `IFunction` interface
- **Object Types**: Define custom identity types beyond built-in User/Group
- **Attributes**: Extensible attribute schema via MetaverseAttribute

**Rule**: Extend through interfaces, not modification. Keep connectors independent.

### 4. Architecture Diagrams

JIM's architecture is documented using C4 model diagrams (System Context, Container, Component levels).

**Viewing Diagrams**: See [docs/diagrams/structurizr/README.md](diagrams/structurizr/README.md) for instructions on running Structurizr Lite locally.

**Available Diagrams**:
- **System Context**: JIM's interactions with external systems and users
- **Container**: Internal deployable units (Web App, Worker, Scheduler, Connectors, Database)
- **Component**: Detailed views of Web Application, Application Layer, Worker Service, Connectors, and Scheduler

## Technology Stack

### Core Technologies (Required)
- **.NET 9.0**: All projects target `net9.0`
- **C# 13**: Language features, nullable reference types enabled
- **ASP.NET Core**: Web framework for Blazor and API
- **Entity Framework Core 9.0**: ORM for data persistence
- **PostgreSQL 18**: Primary database (via Npgsql)

### UI & Frontend
- **Blazor Server**: Interactive web UI with SignalR
- **MudBlazor 8.x**: Material Design component library
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
- **Domain models**: Add to `JIM.Models/Models/`
- **DTOs**: Add to appropriate `DTOs/` subdirectories
- **Business logic**: Add to `JIM.Application/Servers/` or extend existing servers
- **Data access**: Add to `JIM.PostgresData/` repository classes
- **API endpoints**: Add to `JIM.Web/Controllers/Api/`
- **API models/DTOs**: Add to `JIM.Web/Models/Api/`
- **UI pages**: Add to `JIM.Web/Pages/`
- **Connectors**: Create new project or extend `JIM.Connectors/`

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

For full details and connector-specific guidance, see [`docs/plans/GUID_UUID_HANDLING.md`](plans/GUID_UUID_HANDLING.md).

### 3. Database & Migrations

**Entity Framework Core**:
- Use Fluent API for complex configurations in `JimDbContext`
- Create migrations for schema changes: `dotnet ef migrations add MigrationName`
- Test migrations on PostgreSQL 18
- Use repository pattern, never access DbContext directly from application layer

**Performance**:
- Use `.AsNoTracking()` for read-only queries
- Batch operations where possible
- Index frequently queried columns

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
- Add XML comments for Swagger documentation

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

**State Management**:
- Use cascading parameters for shared state
- Avoid static state
- Leverage SignalR for real-time updates

### 7. Background Processing

**Worker Services**:
- Poll task queue from database
- Process tasks via specific processors (SyncImportTaskProcessor, etc.)
- Update task status and activity log
- Handle errors gracefully, log failures

**Rule**: All long-running operations should be queued as WorkerTasks, not executed synchronously in web requests.

### 8. Performance Diagnostics

JIM includes a built-in performance diagnostics infrastructure for measuring operation timings during sync operations. This uses `System.Diagnostics.ActivitySource` under the hood (the .NET OpenTelemetry-compatible API) but wraps it with JIM-specific terminology to avoid confusion with JIM's `Activity` class (used for audit/task tracking).

**Key Components** (in `JIM.Application/Diagnostics/`):

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
- **Worker Service** (`JIM.Worker/Worker.cs`) - 100ms slow operation threshold
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
- **Attribute-based**: Support for custom authorisation rules via Functions

### 3. Input Validation
- Validate all user input (web, API)
- Use DTOs with data annotations
- Sanitise for SQL injection (EF Core parameterises queries)
- Protect against XSS in Blazor components (framework handles by default)

### 4. Secrets Management
- **Environment variables**: All secrets configured via `.env` file (gitignored)
- **No hardcoded secrets**: Never commit credentials, connection strings, API keys
- **Docker secrets**: Use Docker secrets for production deployments

## Testing Expectations

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
- `MockCallConnector`: Call-based mock connector in `JIM.Connectors/Mock/`

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
    Task ExportAsync(ConnectedSystem system, List<ConnectedSystemObject> objects);
}
```

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
- Pre-installed .NET 9.0 SDK
- Docker and Docker Compose
- PostgreSQL 18 with optimised memory settings
- VS Code with recommended extensions
- Pre-configured shell aliases for common tasks

**Quick Start**:
1. Open repository in GitHub
2. Click **Code** > **Codespaces** > **Create codespace on main**
3. Wait for provisioning (automatic setup via `.devcontainer/setup.sh`)
4. Update the auto-generated `.env` file with your SSO configuration
5. Use shell aliases: `jim-db`, `jim-web`, `jim-stack`, etc.

> **Note**: The setup script automatically creates a `.env` file with development defaults. You can also set a `DOTENV_BASE64` GitHub Codespaces secret to restore your own `.env` file automatically.

**Available Shell Aliases**:
- `jim` - List all available jim aliases
- `jim-compile` - Build entire solution (dotnet build)
- `jim-test` - Run all tests
- `jim-db` - Start PostgreSQL + Adminer (local debugging workflow)
- `jim-db-stop` - Stop PostgreSQL + Adminer
- `jim-migrate` - Apply EF Core migrations
- `jim-stack` - Start Docker stack (no dev tools, production-like)
- `jim-stack-dev` - Start Docker stack + Adminer
- `jim-stack-logs` - View Docker stack logs
- `jim-stack-down` - Stop Docker stack

**Docker Builds** (rebuild and start services):
- `jim-build` - Build all services + start (no dev tools)
- `jim-build-dev` - Build all services + start + Adminer
- `jim-build-web` - Build jim.web + start
- `jim-build-worker` - Build jim.worker + start
- `jim-build-scheduler` - Build jim.scheduler + start

**Reset**:
- `jim-reset` - Reset JIM (delete database and logs volumes)

**Development Workflows**:
1. **Local Debugging** (Recommended): Use `jim-db` to start database + Adminer, then F5 to debug services locally
2. **Full Stack**: Use `jim-stack` (production-like) or `jim-stack-dev` (with Adminer) to run all services in containers

**Technical Details**:
- PostgreSQL memory settings automatically optimised for Codespaces constraints
- Port forwarding configured for Web + API (5200), Adminer (8080) when using dev tools
- Custom docker-compose override: `docker-compose.override.codespaces.yml`
- Dev tools (Adminer) separated into `docker-compose.dev-tools.yml` (not included in production releases)

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

For detailed setup instructions, see the [SSO Setup Guide](SSO_SETUP_GUIDE.md).

- `JIM_SSO_AUTHORITY`: OIDC authority URL (e.g., `https://login.microsoftonline.com/{tenant-id}/v2.0`)
- `JIM_SSO_CLIENT_ID`: OIDC client/application ID
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
- **jim.web**: Blazor Server UI with integrated REST API at `/api/` (port 5200 HTTP / 5201 HTTPS). Swagger available at `/api/swagger` in development.
- **jim.worker**: Background task processor
- **jim.scheduler**: Scheduled job execution
- **jim.database**: PostgreSQL 18

**Development Tools** (via `docker-compose.dev-tools.yml`, not included in production):
- **adminer**: Database admin UI (port 8080) - use `jim-stack-dev` or `jim-db` to start

### Docker Compose
- Base: `docker-compose.yml`
- Overrides: `docker-compose.override.{windows|macos|linux}.yml`
- Use platform-specific overrides for optimal performance

### Building Images
```bash
docker compose build
docker compose up -d
```

### Migrations
Apply migrations on first run:
```bash
docker compose exec jim.web dotnet ef database update
```

## File Connector Setup

The JIM File Connector imports identity data from CSV files. Because JIM runs in Docker containers, files must be accessible via Docker volumes.

> **📝 Quick Start for Development**: Test data files from `test/Data/` are automatically available via symlink at `/var/connector-files/test-data/` in the devcontainer. See [FILE_CONNECTOR_TEST_DATA.md](FILE_CONNECTOR_TEST_DATA.md) for details.

### Understanding Docker Volumes

Docker volumes bridge your host filesystem to the container. The File Connector expects files at a **container path** (e.g., `/var/connector-files/Users.csv`), which maps to a **host path** on your machine.

### Volume Configuration by Environment

| Environment | Host Path | Container Path |
|-------------|-----------|----------------|
| **Windows** | `c:/temp/jim-connector-files/` | `/var/connector-files/` |
| **Linux** | `~/temp/jim-connector-files/` | `/var/connector-files/` |
| **macOS** | `~/temp/jim-connector-files/` | `/var/connector-files/` |
| **Codespaces** | `/tmp/jim-connector-files/` | `/var/connector-files/` |

These mappings are already pre-configured in the respective `docker-compose.override.*.yml` files - no additional Docker volume commands are required. The `jim-stack` alias automatically uses the correct override file for your environment.

### Setup Steps

1. **Create the host directory** (if it doesn't exist):
   ```bash
   # For Codespaces:
   mkdir -p /tmp/jim-connector-files

   # For Linux/macOS:
   mkdir -p ~/temp/jim-connector-files

   # For Windows (PowerShell):
   New-Item -ItemType Directory -Force -Path "c:\temp\jim-connector-files"
   ```

2. **Place your CSV file in the host directory**:
   ```bash
   # Example: copy a file to the connector files directory
   cp /path/to/your/Users.csv /tmp/jim-connector-files/
   ```

3. **Restart the Docker stack** (if already running) to pick up the volume mount:
   ```bash
   jim-stack-down && jim-stack
   ```

4. **In the JIM UI**, enter the **container path** for "Import File Path":
   ```
   /var/connector-files/Users.csv
   ```

### File Connector Settings

When creating a Connected System with the File Connector:

| Setting | Description | Example |
|---------|-------------|---------|
| **Import File Path** | Container path to the CSV file to import | `/var/connector-files/Users.csv` |
| **Object Type Column** | Column containing object type (optional) | `Type` |
| **Object Type** | Fixed object type if file contains single type (optional) | `User` |
| **Delimiter** | CSV delimiter character | `,` (default) |
| **Culture** | Culture for parsing (optional) | `en-gb` |

### Schema Discovery

When you retrieve the schema, the File Connector:
1. Opens the CSV file at the "Import File Path"
2. Reads column headers as attribute names
3. Inspects up to 50 rows to detect data types (Text, Number, Boolean, Guid, DateTime)
4. Detects multi-valued attributes (duplicate column names)

### Run Profile Configuration

When creating a Run Profile for the File Connector:
- The connector reads the file specified in "Import File Path" during import operations

### Troubleshooting

**"File path not provided, the path couldn't be accessed, or the file doesn't exist"**
- Verify the file exists in your host directory
- Check the Docker stack is running with volume mounts: `docker compose ps`
- Ensure you're using the container path (`/var/connector-files/...`), not the host path
- Restart the stack if you added files after starting: `jim-stack-down && jim-stack`

**File not found during import**
- The Run Profile's File Path must also use the container path
- Verify the file exists and has read permissions

### Network Share Access (Advanced)

For accessing network shares (e.g., Windows file shares), you can mount them into the host directory:

```bash
# Linux example - mount CIFS/SMB share
sudo mkdir -p /mnt/jim_share
sudo mount -t cifs //server/share /mnt/jim_share -o username=user,password=pass

# Then symlink or copy to the connector files directory
ln -s /mnt/jim_share/Users.csv /tmp/jim-connector-files/Users.csv
```

For production deployments, consider using Docker volume drivers or bind mounts to network storage.

## PowerShell Module Development

The JIM PowerShell module (`JIM.PowerShell/JIM/`) provides cmdlets for scripting and automation. It's designed to work with the JIM API.

### Module Structure

```
JIM.PowerShell/
+-- JIM/
    +-- JIM.psd1              # Module manifest
    +-- JIM.psm1              # Module loader
    +-- Public/               # Exported cmdlets
    |   +-- Activities/       # Get-JIMActivity, Get-JIMActivityItem
    |   +-- ApiKeys/          # *-JIMApiKey cmdlets
    |   +-- Certificates/     # *-JIMCertificate cmdlets
    |   +-- Connection/       # Connect-JIM, Disconnect-JIM, Test-JIMConnection
    |   +-- ConnectedSystems/ # *-JIMConnectedSystem cmdlets
    |   +-- DataGeneration/   # *-JIMDataGeneration* cmdlets
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
Import-Module ./JIM.PowerShell/JIM -Force

# Verify it loaded
Get-Module JIM
```

### Connecting to JIM

```powershell
# Connect using an API key (recommended for automation)
Connect-JIM -BaseUrl "http://localhost:5200" -ApiKey "jim_xxxxxxxxxxxx"

# Test the connection
Test-JIMConnection

# Disconnect when done
Disconnect-JIM
```

### Running Pester Tests

```powershell
# Run all PowerShell module tests
pwsh -NoProfile -Command "
    Import-Module Pester -MinimumVersion 5.0 -Force
    \$config = New-PesterConfiguration
    \$config.Run.Path = './JIM.PowerShell/JIM/Tests'
    \$config.Run.Exit = \$true
    \$config.Output.Verbosity = 'Detailed'
    Invoke-Pester -Configuration \$config
"
```

Or use the simpler form:

```powershell
cd JIM.PowerShell/JIM
Invoke-Pester -Path ./Tests -Output Detailed
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
   Import-Module ./JIM.PowerShell/JIM -Force
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
2. Add to `JIM.Connectors` project or create new project
3. Register in DI container
4. Add `ConnectorDefinition` entry in database seeding
5. Create configuration UI in JIM.Web
6. Add tests in `JIM.Connectors.Tests`

### Adding a New Metaverse Object Type
1. Add enum value to `MetaverseObjectType`
2. Create seeding data in `SeedingServer`
3. Define required attributes
4. Add UI pages for management
5. Update sync rules to support new type

### Adding a New API Endpoint
1. Add method to appropriate controller in `JIM.Web/Controllers/Api/`
2. Use DTOs for request/response (in `JIM.Web/Models/Api/`)
3. Add XML comments for Swagger
4. Add authorisation attributes if needed
5. Test via Swagger UI at `/api/swagger`

### Modifying Database Schema
1. Update entity classes in `JIM.Models/Models/`
2. Update `JimDbContext` if needed (Fluent API)
3. Create migration: `dotnet ef migrations add MigrationName --project JIM.PostgresData`
4. Review generated migration
5. Test migration: `dotnet ef database update --project JIM.PostgresData`
6. Commit migration files

## Troubleshooting

**Build Errors**:
- Ensure .NET 9.0 SDK installed: `dotnet --version` (should show 9.0.x)
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
- **PostgreSQL Memory**: If database crashes, memory settings are pre-optimised in `.devcontainer/setup.sh`
- **Docker Issues**: Restart Codespace or rebuild container if Docker daemon issues occur
- **Missing Aliases**: Run `source ~/.zshrc` or restart terminal if shell aliases not available

## Best Practices Summary

1. **Follow the layers**: Respect architectural boundaries
2. **Use dependency injection**: Constructor injection for all services
3. **Async all the way**: All I/O operations async
4. **Repository pattern**: Never access DbContext directly from application layer
5. **Log comprehensively**: Use Serilog with structured logging
6. **Validate input**: All user input validated at API/UI boundary
7. **Handle errors gracefully**: Try-catch, log, return meaningful responses
8. **Test thoroughly**: Unit tests for business logic, integration tests for data layer
9. **Secure by default**: OIDC/SSO required, no hardcoded secrets
10. **Document via code**: XML comments, clear naming, self-documenting code

## Resources

- **Repository**: https://github.com/TetronIO/JIM
- **Licensing**: https://tetron.io/jim/#licensing
- **Documentation**: `/home/user/JIM/README.md`
- **.NET Docs**: https://learn.microsoft.com/dotnet/
- **EF Core**: https://learn.microsoft.com/ef/core/
- **Blazor**: https://learn.microsoft.com/aspnet/core/blazor/
- **MudBlazor**: https://mudblazor.com/

---

**Last Updated**: 2025-12-23
**Version**: 1.3
**Applies to**: JIM v1.x (NET 9.0)
