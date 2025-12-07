using System.Text.Json;
using Asp.Versioning;
using JIM.Api.Middleware;
using JIM.Application;
using JIM.PostgresData;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;

// Required environment variables:
// -------------------------------
// LOGGING_LEVEL
// LOGGING_PATH
// DB_HOSTNAME - validated by data layer
// DB_NAME - validated by data layer
// DB_USERNAME - validated by data layer
// DB_PASSWORD - validated by data layer
// SSO_AUTHORITY
// SSO_CLIENT_ID
// SSO_SECRET
// SSO_API_SCOPE - The OAuth scope for API access (e.g., api://client-id/access_as_user for Entra ID)
// SSO_UNIQUE_IDENTIFIER_CLAIM_TYPE
// SSO_UNIQUE_IDENTIFIER_METAVERSE_ATTRIBUTE_NAME
// SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE

// Optional environment variables:
// -------------------------------
// ENABLE_REQUEST_LOGGING

// initial logging setup for when the application has not yet been created (bootstrapping)...
InitialiseLogging(new LoggerConfiguration(), true);

try
{
    Log.Information("Starting JIM.Api");

    var builder = WebApplication.CreateBuilder(args);

    // Add services to the container.
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddTransient(x => new JimApplication(new PostgresDataRepository(new JimDbContext())));
    builder.Services.Configure<RouteOptions>(ro => ro.LowercaseUrls = true);

    // Configure API versioning with URL path segment (e.g., /api/v1/...)
    builder.Services.AddApiVersioning(options =>
    {
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = true;
        options.ReportApiVersions = true;
        options.ApiVersionReader = new UrlSegmentApiVersionReader();
    }).AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    });

    // Setup JWT Bearer authentication using the same OIDC authority as JIM.Web
    var authority = Environment.GetEnvironmentVariable("SSO_AUTHORITY");
    var clientId = Environment.GetEnvironmentVariable("SSO_CLIENT_ID");
    var apiScope = Environment.GetEnvironmentVariable("SSO_API_SCOPE");

    // Extract the API identifier from the scope (e.g., "api://client-id/access_as_user" -> "api://client-id")
    var apiAudience = ExtractApiAudience(apiScope, clientId);
    var validIssuers = GetValidIssuers(authority);

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.Audience = apiAudience;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = validIssuers,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true
            };

            // Preserve standard OIDC claim names (sub, name, email, etc.) instead of mapping them
            // to Microsoft's legacy XML-based claim URIs. This makes JIM IDP-agnostic.
            options.MapInboundClaims = false;
        });

    builder.Services.AddAuthorization();

    // Fetch OIDC discovery document to get IDP-agnostic authorization endpoints
    var oidcConfig = await FetchOidcDiscoveryDocumentAsync(authority!);

    // Setup Swagger with OAuth2 support for testing authenticated endpoints
    // Uses OIDC discovery to get endpoints - works with any OIDC-compliant IDP
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "JIM API",
            Version = "v1",
            Description = "JIM (Junctional Identity Manager) REST API for managing identity synchronisation.",
            Contact = new OpenApiContact
            {
                Name = "Tetron",
                Url = new Uri("https://github.com/TetronIO/JIM")
            }
        });

        // Include XML comments for API documentation
        var xmlFilename = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFilename);
        if (File.Exists(xmlPath))
        {
            options.IncludeXmlComments(xmlPath);
        }

        options.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = new Uri(oidcConfig.AuthorizationEndpoint),
                    TokenUrl = new Uri(oidcConfig.TokenEndpoint),
                    Scopes = new Dictionary<string, string>
                    {
                        { "openid", "OpenID Connect" },
                        { apiScope!, "Access JIM API" }
                    }
                }
            }
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "oauth2"
                    }
                },
                new[] { "openid", apiScope! }
            }
        });
    });    

    // now setup logging with the web framework
    builder.Host.UseSerilog((context, services, configuration) => InitialiseLogging(configuration, false));

    var app = builder.Build();

    // Global exception handler - must be first in the pipeline
    app.UseGlobalExceptionHandler();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.OAuthClientId(clientId);
            // No client secret - Swagger uses SPA platform with PKCE (public client)
            options.OAuthUsePkce();
        });
    }

    // todo: don't think this will work with docker
    app.UseHttpsRedirection();
    app.UseAuthentication();
    app.UseJimRoleEnrichment(); // Enrich JWT claims with JIM roles from database
    app.UseAuthorization();
    app.MapControllers();

    // only enable request logging if configured to do some from env vars, as it adds a LOT to the logs
    var enableRequestLogging = Environment.GetEnvironmentVariable("ENABLE_REQUEST_LOGGING");
    if (enableRequestLogging != null && bool.Parse(enableRequestLogging))
        app.UseSerilogRequestLogging();

    app.Logger.LogInformation("The JIM API has started");
    app.Run();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Fetches the OIDC discovery document from the authority's well-known endpoint.
/// This provides IDP-agnostic endpoint discovery for any OIDC-compliant provider.
/// </summary>
/// <param name="authority">The OIDC authority URL (e.g., https://login.microsoftonline.com/tenant-id/v2.0)</param>
/// <returns>The parsed OIDC configuration containing authorization and token endpoints</returns>
static async Task<OidcDiscoveryDocument> FetchOidcDiscoveryDocumentAsync(string authority)
{
    using var httpClient = new HttpClient();
    var discoveryUrl = $"{authority.TrimEnd('/')}/.well-known/openid-configuration";

    Log.Information("Fetching OIDC discovery document from {Url}", discoveryUrl);

    var response = await httpClient.GetStringAsync(discoveryUrl);
    var doc = JsonSerializer.Deserialize<OidcDiscoveryDocument>(response)
        ?? throw new ApplicationException("Failed to parse OIDC discovery document");

    Log.Information("OIDC discovery complete. Authorization endpoint: {AuthEndpoint}", doc.AuthorizationEndpoint);

    return doc;
}


/// <summary>
/// Initialises the Serilog logging configuration based on environment variables.
/// </summary>
/// <param name="loggerConfiguration">The LoggerConfiguration instance to configure</param>
/// <param name="assignLogLogger">If true, assigns the created logger to the static Log.Logger property</param>
/// <exception cref="ApplicationException">Thrown when required environment variables LOGGING_LEVEL or LOGGING_PATH are not set</exception>
/// <remarks>
/// This method configures logging with the following features:
/// - Sets minimum log level based on LOGGING_LEVEL environment variable
/// - Configures file logging to the path specified in LOGGING_PATH environment variable
/// - Sets up console logging
/// - Overrides Microsoft framework logging levels to reduce noise
/// - Uses daily rolling file interval for log files
/// </remarks>
static void InitialiseLogging(LoggerConfiguration loggerConfiguration, bool assignLogLogger)
{
    var loggingMinimumLevel = Environment.GetEnvironmentVariable("LOGGING_LEVEL");
    var loggingPath = Environment.GetEnvironmentVariable("LOGGING_PATH");

    if (loggingMinimumLevel == null)
        throw new ApplicationException("LOGGING_LEVEL environment variable not found. Cannot continue");
    if (loggingPath == null)
        throw new ApplicationException("LOGGING_PATH environment variable not found. Cannot continue");

    switch (loggingMinimumLevel)
    {
        case "Verbose":
            loggerConfiguration.MinimumLevel.Verbose();
            break;
        case "Debug":
            loggerConfiguration.MinimumLevel.Debug();
            break;
        case "Information":
            loggerConfiguration.MinimumLevel.Information();
            break;
        case "Warning":
            loggerConfiguration.MinimumLevel.Warning();
            break;
        case "Error":
            loggerConfiguration.MinimumLevel.Error();
            break;
        case "Fatal":
            loggerConfiguration.MinimumLevel.Fatal();
            break;
    }

    loggerConfiguration.MinimumLevel.Override("Microsoft", LogEventLevel.Information);
    loggerConfiguration.MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning);
    loggerConfiguration.Enrich.FromLogContext();
    loggerConfiguration.WriteTo.File(Path.Combine(loggingPath, "jim.api..log"), rollingInterval: RollingInterval.Day);
    loggerConfiguration.WriteTo.Console();

    if (assignLogLogger)
        Log.Logger = loggerConfiguration.CreateLogger();
}

/// <summary>
/// Extracts the API audience from an OAuth scope.
/// For Entra ID scopes like "api://client-id/access_as_user", extracts "api://client-id".
/// For other IDPs, falls back to the client ID.
/// </summary>
/// <param name="apiScope">The full API scope (e.g., api://client-id/access_as_user)</param>
/// <param name="clientId">The OAuth client ID as fallback</param>
/// <returns>The API audience for JWT validation</returns>
static string? ExtractApiAudience(string? apiScope, string? clientId)
{
    if (string.IsNullOrEmpty(apiScope))
        return clientId;

    // For scopes like "api://client-id/access_as_user", extract "api://client-id"
    var lastSlashIndex = apiScope.LastIndexOf('/');
    if (lastSlashIndex > 0 && apiScope.StartsWith("api://"))
        return apiScope[..lastSlashIndex];

    return clientId;
}

/// <summary>
/// Gets the valid token issuers for JWT validation.
/// Auto-detects Entra ID and configures both v1 and v2 issuer formats.
/// For other IDPs, uses the authority as the issuer.
/// </summary>
/// <param name="authority">The OIDC authority URL</param>
/// <returns>Array of valid issuer URLs</returns>
static string[] GetValidIssuers(string? authority)
{
    // Check if user has configured explicit issuers
    var configuredIssuers = Environment.GetEnvironmentVariable("SSO_VALID_ISSUERS");
    if (!string.IsNullOrEmpty(configuredIssuers))
    {
        return configuredIssuers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    if (string.IsNullOrEmpty(authority))
        return [];

    // Auto-detect Entra ID and handle v1/v2 token format quirks
    // Entra ID authority format: https://login.microsoftonline.com/{tenant-id}/v2.0
    if (authority.Contains("login.microsoftonline.com"))
    {
        // Extract tenant ID from the authority URL
        var uri = new Uri(authority);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 1)
        {
            var tenantId = segments[0];
            Log.Information("Detected Entra ID authority, configuring v1 and v2 issuers for tenant {TenantId}", tenantId);

            return
            [
                $"https://sts.windows.net/{tenantId}/",                 // v1 issuer format
                $"https://login.microsoftonline.com/{tenantId}/v2.0"    // v2 issuer format
            ];
        }
    }

    // For other IDPs, the issuer typically matches the authority
    return [authority];
}

/// <summary>
/// Represents the relevant fields from an OIDC discovery document.
/// </summary>
internal class OidcDiscoveryDocument
{
    [System.Text.Json.Serialization.JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; set; } = string.Empty;
}