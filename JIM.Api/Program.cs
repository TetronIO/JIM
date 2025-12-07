using System.Text.Json;
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
    builder.Services.AddTransient<JimApplication>(x => new JimApplication(new PostgresDataRepository(new JimDbContext())));
    builder.Services.Configure<RouteOptions>(ro => ro.LowercaseUrls = true);

    // Setup JWT Bearer authentication using the same OIDC authority as JIM.Web
    var authority = Environment.GetEnvironmentVariable("SSO_AUTHORITY");
    var clientId = Environment.GetEnvironmentVariable("SSO_CLIENT_ID");
    var apiScope = Environment.GetEnvironmentVariable("SSO_API_SCOPE");

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.Audience = clientId;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
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
        options.SwaggerDoc("v1", new OpenApiInfo { Title = "JIM API", Version = "v1" });

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
        var clientSecret = Environment.GetEnvironmentVariable("SSO_SECRET");
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.OAuthClientId(clientId);
            options.OAuthClientSecret(clientSecret);
            options.OAuthUsePkce();
        });
    }

    // todo: don't think this will work with docker
    app.UseHttpsRedirection();
    app.UseAuthentication();
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
/// Represents the relevant fields from an OIDC discovery document.
/// </summary>
internal class OidcDiscoveryDocument
{
    [System.Text.Json.Serialization.JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; set; } = string.Empty;
}