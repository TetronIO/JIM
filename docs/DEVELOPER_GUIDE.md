# JIM Developer Guide

> **Project**: Junctional Identity Manager (JIM)
> **Purpose**: Enterprise-grade identity management system for synchronisation, governance, and domain migrations
> **License**: Source-available, commercial license required for production use

## Overview

JIM is a .NET-based Identity Management (IDM) system implementing the metaverse pattern for centralised identity governance. It synchronises identities across heterogeneous systems (Active Directory, LDAP, files, databases, etc.) with bi-directional attribute flows, provisioning rules, and compliance tracking.

## Architecture Principles

### 1. Layered Architecture
- **Presentation Layer**: JIM.Web (Blazor Server), JIM.Api (Web API)
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
- **API endpoints**: Add to `JIM.Api/Controllers/`
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
- Use attribute routing: `[Route("api/[controller]")]`
- Return `ActionResult<T>` for typed responses
- Use DTOs for request/response bodies
- Add XML comments for Swagger documentation

**Example**:
```csharp
[ApiController]
[Route("api/[controller]")]
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

### Integration Tests
- Test repository implementations against PostgreSQL
- Use test containers or dedicated test database
- Verify migrations work correctly

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
- `jim-build` - Build entire solution
- `jim-test` - Run all tests
- `jim-db` - Start PostgreSQL (local debugging workflow)
- `jim-db-stop` - Stop PostgreSQL
- `jim-migrate` - Apply EF Core migrations
- `jim-stack` - Start full Docker stack (all services containerised)
- `jim-stack-down` - Stop full Docker stack

**Development Workflows**:
1. **Local Debugging** (Recommended): Use `jim-db` to start database, then F5 to debug services locally
2. **Full Stack**: Use `jim-stack` to run all services in containers for integration testing

**Technical Details**:
- PostgreSQL memory settings automatically optimised for Codespaces constraints
- Port forwarding configured for Web (5200), API (5202), and Adminer (8080)
- Custom docker-compose override: `docker-compose.override.codespaces.yml`

## Environment Configuration

Configuration via environment variables (defined in `.env`):

### Database
- `DB_HOST`: PostgreSQL host
- `DB_PORT`: PostgreSQL port (default: 5432)
- `DB_NAME`: Database name
- `DB_USERNAME`: Database user
- `DB_PASSWORD`: Database password

### SSO/Authentication
- `SSO_AUTHORITY`: OIDC authority URL
- `SSO_CLIENTID`: OIDC client ID
- `SSO_CLIENTSECRET`: OIDC client secret
- `SSO_CALLBACKPATH`: OAuth callback path

### Logging
- `LOGGING_CONSOLE_MINIMUMLEVEL`: Console log level (Information, Debug, etc.)
- `LOGGING_FILE_MINIMUMLEVEL`: File log level
- `LOGGING_FILE_PATH`: Log file path
- `ENABLE_REQUEST_LOGGING`: Enable verbose HTTP request logging (true/false)

### Initial Setup
- `INITIAL_ADMIN_USERNAME`: Initial admin user from SSO
- `INITIAL_ADMIN_DISPLAYNAME`: Admin display name

**Rule**: Never hardcode these values. Always use environment variables.

## Docker & Deployment

### Service Architecture
- **jim.web**: Blazor Server UI (port 5201 HTTPS)
- **jim.api**: Web API (port 5203 HTTPS)
- **jim.worker**: Background task processor
- **jim.scheduler**: Scheduled job execution
- **jim.database**: PostgreSQL 18
- **adminer**: Database admin UI (port 8080)

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

## Common Development Tasks

> **Note**: All `dotnet` commands below work out of the box in Codespaces. Use shell aliases like `jim-build`, `jim-test`, and `jim-migrate` for convenience.

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
1. Add method to appropriate controller in `JIM.Api/Controllers/`
2. Use DTOs for request/response
3. Add XML comments for Swagger
4. Add authorisation attributes if needed
5. Test via Swagger UI

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
- Restore NuGet packages: `dotnet restore JIM.sln` or use `jim-build` alias
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

**Last Updated**: 2025-11-18
**Version**: 1.0
**Applies to**: JIM v1.x (NET 9.0)
