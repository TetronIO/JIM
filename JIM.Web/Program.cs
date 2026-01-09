using System.Text.Json;
using Asp.Versioning;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Core;
using JIM.PostgresData;
using JIM.Web.Middleware.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MudBlazor;
using MudBlazor.Services;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Json;
using System.Security.Claims;

// Required environment variables:
// -------------------------------
// JIM_LOG_LEVEL
// JIM_LOG_PATH
// JIM_DB_HOSTNAME - validated by the data layer
// JIM_DB_NAME - validated by the data layer
// JIM_DB_USERNAME - validated by the data layer
// JIM_DB_PASSWORD - validated by the data layer
// JIM_SSO_AUTHORITY
// JIM_SSO_CLIENT_ID
// JIM_SSO_SECRET
// JIM_SSO_API_SCOPE - The OAuth scope for API access (e.g., api://client-id/access_as_user for Entra ID)
// JIM_SSO_CLAIM_TYPE
// JIM_SSO_MV_ATTRIBUTE
// JIM_SSO_INITIAL_ADMIN

// Optional environment variables:
// -------------------------------
// JIM_LOG_REQUESTS
// JIM_INFRASTRUCTURE_API_KEY - Creates an infrastructure API key on startup for CI/CD automation (24hr expiry)
// JIM_ENCRYPTION_KEY_PATH - Custom path for encryption key storage (default: /data/keys or app data directory)

// initial logging setup for when the application has not yet been created (bootstrapping)...
InitialiseLogging(new LoggerConfiguration(), true);

try
{
    Log.Information("Starting JIM.Web");
    await InitialiseJimApplicationAsync();

    var builder = WebApplication.CreateBuilder(args);

    // Configure database connection
    var dbHostName = Environment.GetEnvironmentVariable(Constants.Config.DatabaseHostname);
    var dbName = Environment.GetEnvironmentVariable(Constants.Config.DatabaseName);
    var dbUsername = Environment.GetEnvironmentVariable(Constants.Config.DatabaseUsername);
    var dbPassword = Environment.GetEnvironmentVariable(Constants.Config.DatabasePassword);
    var dbLogSensitiveInfo = Environment.GetEnvironmentVariable(Constants.Config.DatabaseLogSensitiveInformation);

    var connectionString = $"Host={dbHostName};Database={dbName};Username={dbUsername};Password={dbPassword};Minimum Pool Size=5;Maximum Pool Size=50;Connection Idle Lifetime=300;Connection Pruning Interval=30";
    _ = bool.TryParse(dbLogSensitiveInfo, out var logSensitiveInfo);
    if (logSensitiveInfo)
        connectionString += ";Include Error Detail=True";

    // Use DbContextFactory for Blazor Server to avoid concurrent DbContext access issues
    // Blazor Server pre-rendering and interactive rendering can happen concurrently
    builder.Services.AddDbContextFactory<JimDbContext>(options =>
        options.UseNpgsql(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

    // Register repository and application as transient to prevent DbContext concurrency issues
    // In Blazor Server, multiple async operations within a page can run concurrently
    // Using transient lifetime ensures each operation gets its own DbContext instance
    builder.Services.AddTransient<IRepository>(sp =>
    {
        var factory = sp.GetRequiredService<IDbContextFactory<JimDbContext>>();
        var context = factory.CreateDbContext();
        return new PostgresDataRepository(context);
    });
    builder.Services.AddTransient<JimApplication>(sp =>
    {
        var jim = new JimApplication(sp.GetRequiredService<IRepository>());
        // Inject credential protection service for connector password encryption/decryption
        jim.CredentialProtection = sp.GetService<ICredentialProtectionService>();
        return jim;
    });
    builder.Services.AddSingleton<LogReaderService>();
    builder.Services.AddExpressionEvaluation();

    // Configure ASP.NET Core Data Protection for credential encryption
    // Using AES-256-GCM (FIPS-approved authenticated encryption) for future-proofing
    var dataProtectionKeysPath = GetDataProtectionKeysPath();
    builder.Services.AddDataProtection()
        .SetApplicationName("JIM")
        .UseCryptographicAlgorithms(new AuthenticatedEncryptorConfiguration
        {
            EncryptionAlgorithm = EncryptionAlgorithm.AES_256_GCM,
            ValidationAlgorithm = ValidationAlgorithm.HMACSHA256
        })
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
        .SetDefaultKeyLifetime(TimeSpan.FromDays(3650)); // 10 years for credential stability

    builder.Services.AddSingleton<ICredentialProtectionService, CredentialProtectionService>();

    // setup OpenID Connect (OIDC) authentication for Blazor UI
    var authority = Environment.GetEnvironmentVariable(Constants.Config.SsoAuthority);
    var clientId = Environment.GetEnvironmentVariable(Constants.Config.SsoClientId);
    var clientSecret = Environment.GetEnvironmentVariable(Constants.Config.SsoSecret);
    var apiScope = Environment.GetEnvironmentVariable(Constants.Config.SsoApiScope);

    // Extract the API identifier from the scope for JWT validation
    var apiAudience = ExtractApiAudience(apiScope, clientId);
    var validIssuers = GetValidIssuers(authority);

    // Configure triple authentication: Cookies for Blazor UI, JWT Bearer for SSO API, API Key for non-interactive API
    // Configure authentication with multiple schemes
    // Cookie is default for Blazor UI, but forwards to API Key when X-API-Key header is present
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
        .AddApiKeyAuthentication()
        .AddCookie(options =>
        {
            // Forward to API Key authentication when X-API-Key header is present
            options.ForwardDefaultSelector = context =>
            {
                // Check if the request has an API key header
                if (context.Request.Headers.ContainsKey("X-API-Key"))
                {
                    return ApiKeyAuthenticationHandler.SchemeName;
                }
                // Otherwise use default Cookie authentication
                return null;
            };
        })
        .AddOpenIdConnect(options =>
        {
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.UseTokenLifetime = true; // respect the IdP token lifetime and use our session lifetime
            options.Authority = authority;
            options.ClientId = clientId;
            options.ClientSecret = clientSecret;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.Scope.Add("profile");

            // Preserve standard OIDC claim names (sub, name, email, etc.) instead of mapping them
            // to Microsoft's legacy XML-based claim URIs. This makes JIM IDP-agnostic.
            options.MapInboundClaims = false;

            // intercept the user login when a token is received and validate we can map them to a JIM user
            options.Events.OnTicketReceived = async ctx =>
            {
                await AuthoriseAndUpdateUserAsync(ctx);
            };

            // Prevent OIDC redirects for API requests - they should return 401 instead
            options.Events.OnRedirectToIdentityProvider = async ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    Log.Debug("Suppressing OIDC redirect for API request: {Path}, returning 401", ctx.Request.Path);
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized", message = "Authentication required" });
                    ctx.HandleResponse(); // Mark response as handled
                }
            };
        })
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
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

            // Preserve standard OIDC claim names for API requests
            options.MapInboundClaims = false;
        });

    // setup authorisation policies
    builder.Services.AddAuthorization();

    // Add services to the container.
    builder.Services.AddRazorPages();
    builder.Services.AddServerSideBlazor();

    // Add API controller support with JSON serialization configured to use string enums
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });
    builder.Services.AddEndpointsApiExplorer();
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

    // Fetch OIDC discovery document to get IDP-agnostic authorization endpoints
    var oidcConfig = await FetchOidcDiscoveryDocumentAsync(authority!);

    // Setup Swagger with OAuth2 support for testing authenticated API endpoints
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

        // OAuth2 security scheme for SSO authentication
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

        // API Key security scheme for non-interactive authentication
        options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-API-Key",
            Description = "API key for non-interactive authentication. Format: jim_ak_<random>"
        });

        // Both authentication methods are valid - API will accept either
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
            },
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "ApiKey"
                    }
                },
                Array.Empty<string>()
            }
        });
    });

    // setup logging properly now (it's been bootstrapped initially)
    builder.Services.AddSerilog(configuration => InitialiseLogging(configuration, false));

    // setup MudBlazor
    builder.Services.AddMudServices(config =>
    {
        config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
    });

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }

    // Swagger UI available at /api/swagger in development
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger(c => c.RouteTemplate = "api/swagger/{documentName}/swagger.json");
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/api/swagger/v1/swagger.json", "JIM API v1");
            options.RoutePrefix = "api/swagger";
            options.OAuthClientId(clientId);
            options.OAuthUsePkce();
        });
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseRouting();

    // Global exception handler for API endpoints
    app.UseWhen(
        context => context.Request.Path.StartsWithSegments("/api"),
        appBuilder => appBuilder.UseGlobalExceptionHandler()
    );

    app.UseAuthentication();

    // JIM role enrichment for API requests (JWT Bearer auth)
    app.UseWhen(
        context => context.Request.Path.StartsWithSegments("/api"),
        appBuilder => appBuilder.UseJimRoleEnrichment()
    );

    app.UseAuthorization();

    // Map API controllers
    app.MapControllers();

    // Map Blazor endpoints
    app.MapBlazorHub();
    app.MapFallbackToPage("/_Host");

    // only enable request logging if configured to do so from env vars, as it adds a LOT to the logs
    var enableRequestLogging = Environment.GetEnvironmentVariable(Constants.Config.LogRequests);
    if (enableRequestLogging != null && bool.Parse(enableRequestLogging))
        app.UseSerilogRequestLogging();

    app.Logger.LogInformation("The JIM Web has started");
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

static void InitialiseLogging(LoggerConfiguration loggerConfiguration, bool assignLogLogger)
{
    var loggingMinimumLevel = Environment.GetEnvironmentVariable(Constants.Config.LogLevel);
    if (loggingMinimumLevel == null)
        throw new ApplicationException($"{Constants.Config.LogLevel} environment variable not found. Cannot continue");

    var loggingPath = Environment.GetEnvironmentVariable(Constants.Config.LogPath);
    if (loggingPath == null)
        throw new ApplicationException($"{Constants.Config.LogPath} environment variable not found. Cannot continue");

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
    loggerConfiguration.WriteTo.File(
        formatter: new RenderedCompactJsonFormatter(),
        path: Path.Combine(loggingPath, "jim.web..log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31,  // Keep 31 days of logs for integration test analysis
        fileSizeLimitBytes: 500 * 1024 * 1024,  // 500MB per file max
        rollOnFileSizeLimit: true);
    loggerConfiguration.WriteTo.Console();

    if (assignLogLogger)
        Log.Logger = loggerConfiguration.CreateLogger();
}

static async Task InitialiseJimApplicationAsync()
{
    // Sets up the JIM application, pass in the right database repository (could pass in something else for testing, i.e. In Memory db).
    // then ensure SSO and Initial admin are setup.

    // collect auth config variables
    Log.Verbose("InitialiseJimApplicationAsync: Called.");
    var ssoAuthority = Environment.GetEnvironmentVariable(Constants.Config.SsoAuthority);
    if (string.IsNullOrEmpty(ssoAuthority))
        throw new Exception($"{Constants.Config.SsoAuthority} environment variable missing");

    var ssoClientId = Environment.GetEnvironmentVariable(Constants.Config.SsoClientId);
    if (string.IsNullOrEmpty(ssoClientId))
        throw new Exception($"{Constants.Config.SsoClientId} environment variable missing");

    var ssoSecret = Environment.GetEnvironmentVariable(Constants.Config.SsoSecret);
    if (string.IsNullOrEmpty(ssoSecret))
        throw new Exception($"{Constants.Config.SsoSecret} environment variable missing");

    // collect claim mapping config variables
    var uniqueIdentifierClaimType = Environment.GetEnvironmentVariable(Constants.Config.SsoClaimType);
    if (string.IsNullOrEmpty(uniqueIdentifierClaimType))
        throw new Exception($"{Constants.Config.SsoClaimType} environment variable missing");

    var uniqueIdentifierMetaverseAttributeName = Environment.GetEnvironmentVariable(Constants.Config.SsoMvAttribute);
    if (string.IsNullOrEmpty(uniqueIdentifierMetaverseAttributeName))
        throw new Exception($"{Constants.Config.SsoMvAttribute} environment variable missing");

    var initialAdminClaimValue = Environment.GetEnvironmentVariable(Constants.Config.SsoInitialAdmin);
    if (string.IsNullOrEmpty(initialAdminClaimValue))
        throw new Exception($"{Constants.Config.SsoInitialAdmin} environment variable missing");

    while (true)
    {
        var jimApplication = new JimApplication(new PostgresDataRepository(new JimDbContext()));
        if (await jimApplication.IsApplicationReadyAsync())
        {
            await jimApplication.InitialiseSsoAsync(ssoAuthority, ssoClientId, ssoSecret, uniqueIdentifierClaimType, uniqueIdentifierMetaverseAttributeName, initialAdminClaimValue);
            await InitialiseInfrastructureApiKeyAsync(jimApplication);
            break;
        }

        Log.Information("JIM.Application is not ready yet. Sleeping...");
        await Task.Delay(1000);
    }
}

static async Task InitialiseInfrastructureApiKeyAsync(JimApplication jim)
{
    // Check if an infrastructure API key should be created from environment variable
    var infrastructureApiKey = Environment.GetEnvironmentVariable(Constants.Config.InfrastructureApiKey);
    if (string.IsNullOrEmpty(infrastructureApiKey))
    {
        Log.Verbose("InitialiseInfrastructureApiKeyAsync: No {EnvVar} environment variable set.", Constants.Config.InfrastructureApiKey);
        return;
    }

    // Validate the key format
    if (!infrastructureApiKey.StartsWith(ApiKeyAuthenticationHandler.ApiKeyPrefix))
    {
        Log.Warning("InitialiseInfrastructureApiKeyAsync: {EnvVar} must start with '{Prefix}'. Key not created.",
            Constants.Config.InfrastructureApiKey, ApiKeyAuthenticationHandler.ApiKeyPrefix);
        return;
    }

    if (infrastructureApiKey.Length < 32)
    {
        Log.Warning("InitialiseInfrastructureApiKeyAsync: {EnvVar} is too short. Use at least 32 characters. Key not created.",
            Constants.Config.InfrastructureApiKey);
        return;
    }

    // Check if this key already exists (by hash)
    var keyHash = ApiKeyAuthenticationHandler.HashApiKey(infrastructureApiKey);
    var existingKey = await jim.Repository.ApiKeys.GetByHashAsync(keyHash);

    if (existingKey != null)
    {
        Log.Information("InitialiseInfrastructureApiKeyAsync: Infrastructure API key already exists (prefix: {Prefix}).", existingKey.KeyPrefix);
        return;
    }

    // Get the Administrator role
    var roles = await jim.Security.GetRolesAsync();
    var adminRole = roles.FirstOrDefault(r => r.Name == "Administrator");

    if (adminRole == null)
    {
        Log.Error("InitialiseInfrastructureApiKeyAsync: Administrator role not found. Cannot create infrastructure key.");
        return;
    }

    // Create the infrastructure API key with 24-hour expiry
    var keyPrefix = infrastructureApiKey.Length >= 12
        ? infrastructureApiKey[..12]
        : infrastructureApiKey;

    var apiKey = new JIM.Models.Security.ApiKey
    {
        Id = Guid.NewGuid(),
        Name = "Infrastructure Key",
        Description = "Auto-created from the JIM_INFRASTRUCTURE_API_KEY environment variable. This key expires 24 hours after creation.",
        KeyHash = keyHash,
        KeyPrefix = keyPrefix,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddHours(24),
        IsEnabled = true,
        IsInfrastructureKey = true,
        Roles = [adminRole]
    };

    await jim.Repository.ApiKeys.CreateAsync(apiKey);
    Log.Information("InitialiseInfrastructureApiKeyAsync: Created infrastructure API key (prefix: {Prefix}, expires: {Expiry}).",
        keyPrefix, apiKey.ExpiresAt);
}

static async Task AuthoriseAndUpdateUserAsync(TicketReceivedContext context)
{
    // When a user signs in, we need to see if we can map the identity in the received token, to a user in the Metaverse.
    // If we do, then the user's roles are retrieved and added to their identity, if not, they receive no roles and will
    // not be able to access any part of JIM.
    //
    // Also, if the user has claims that map to the user's Metaverse attributes that have no values, then those attributes
    // will be set from the claim values, i.e. assign initial values. This ensures initial admins are represented properly,
    // i.e. have a Display Name.

    Log.Verbose("AuthoriseAndUpdateUserAsync: Called.");

    if (context.Principal?.Identity == null)
    {
        Log.Error($"AuthoriseAndUpdateUserAsync: User doesn't have a principal or identity");
        return;
    }

    // there's probably a better way to do this, i.e. getting JimApplication from Services somehow
    var jim = new JimApplication(new PostgresDataRepository(new JimDbContext()));
    var serviceSettings = await jim.ServiceSettings.GetServiceSettingsAsync() ??
        throw new Exception("ServiceSettings was null. Cannot continue.");

    if (serviceSettings.SSOUniqueIdentifierMetaverseAttribute == null)
        throw new Exception("ServiceSettings.SSOUniqueIdentifierMetaverseAttribute is null!");

    if (string.IsNullOrEmpty(serviceSettings.SSOUniqueIdentifierClaimType))
        throw new Exception("ServiceSettings.SSOUniqueIdentifierClaimType is null or empty!");

    var uniqueIdClaimValue = context.Principal.FindFirstValue(serviceSettings.SSOUniqueIdentifierClaimType);
    if (string.IsNullOrEmpty(uniqueIdClaimValue))
    {
        Log.Warning($"AuthoriseAndUpdateUserAsync: User '{context.Principal.Identity.Name}' doesn't have a '{serviceSettings.SSOUniqueIdentifierClaimType}' claim that's needed to identify the user.");
        return;
    }

    Log.Debug($"AuthoriseAndUpdateUserAsync: User '{context.Principal.Identity.Name}' has a '{serviceSettings.SSOUniqueIdentifierClaimType}' claim value of '{uniqueIdClaimValue}'.");

    // get the user using their unique id claim value
    var userType = await jim.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.User, false) ??
        throw new Exception("Could not retrieve User object type");

    var user = await jim.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(userType, serviceSettings.SSOUniqueIdentifierMetaverseAttribute, uniqueIdClaimValue);
    if (user != null)
    {
        // we mapped a token user to a Metaverse user, now we need to create a new ASP.NET identity that represents an internal view
        // of the user, with their roles claims. We have to create a new identity as we cannot modify the default ASP.NET one.
        // This will do to start with. When we need a more developed RBAC system later, we might need to extend ClaimsIdentity to accomodate more complex roles.

        // retrieve the existing JIM role assignments for this user.
        var userRoles = await jim.Security.GetMetaverseObjectRolesAsync(user);

        // convert their JIM role assignments to ASP.NET claims.
        var userRoleClaims = userRoles.Select(role => new Claim(Constants.BuiltInRoles.RoleClaimType, role.Name)).ToList();

        // add a virtual-role claim for user.
        // this role provides basic access to JIM.Web. If we can't map a user, they don't get this role, and therefore they can't access much.
        userRoleClaims.Add(new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User));

        // add their metaverse object id claim to the new identity as well.
        // we'll use this to attribute user actions to the claims identity.
        userRoleClaims.Add(new Claim(Constants.BuiltInClaims.MetaverseObjectId, user.Id.ToString()));

        // the new JIM-specific identity is ready, now add it to the ASP.NET identity so it can be easily retrieved later.
        var jimIdentity = new ClaimsIdentity(userRoleClaims) { Label = "JIM.Web" };
        context.Principal.AddIdentity(jimIdentity);

        // now see if we can supplement the JIM identity with any supplied from the IdP to more fully populate the user.
        await UpdateUserAttributesFromClaimsAsync(jim, user, context.Principal);
    }

    // we couldn't map the token user to a Metaverse user. Quit
    // this will be the user will have no roles added, so they won't be able to access JIM.Web
}

static async Task UpdateUserAttributesFromClaimsAsync(JimApplication jim, MetaverseObject user, ClaimsPrincipal claimsPrincipal)
{
    Log.Verbose("UpdateUserAttributesFromClaimsAsync: Called.");
    var updateRequired = false;

    if (!user.HasAttributeValue(Constants.BuiltInAttributes.DisplayName))
    {
        var nameClaim = claimsPrincipal.Claims.FirstOrDefault(q => q.Type == "name");
        if (nameClaim != null)
        {
            var displayNameAttribute = await jim.Metaverse.GetMetaverseAttributeAsync(Constants.BuiltInAttributes.DisplayName);
            if (displayNameAttribute != null)
            {
                user.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = displayNameAttribute,
                    StringValue = nameClaim.Value
                });

                updateRequired = true;
                Log.Verbose("UpdateUserAttributesFromClaimsAsync: Added value from claim: " + nameClaim.Type);
            }
        }
    }


    // Map given_name claim (standard OIDC claim for first name)
    if (!user.HasAttributeValue(Constants.BuiltInAttributes.FirstName))
    {
        var givenNameClaim = claimsPrincipal.Claims.FirstOrDefault(q => q.Type == "given_name");
        if (givenNameClaim != null)
        {
            var firstNameAttribute = await jim.Metaverse.GetMetaverseAttributeAsync(Constants.BuiltInAttributes.FirstName);
            if (firstNameAttribute != null)
            {
                user.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = firstNameAttribute,
                    StringValue = givenNameClaim.Value
                });

                updateRequired = true;
                Log.Verbose("UpdateUserAttributesFromClaimsAsync: Added value from claim: " + givenNameClaim.Type);
            }
        }
    }

    // Map family_name claim (standard OIDC claim for last name)
    if (!user.HasAttributeValue(Constants.BuiltInAttributes.LastName))
    {
        var familyNameClaim = claimsPrincipal.Claims.FirstOrDefault(q => q.Type == "family_name");
        if (familyNameClaim != null)
        {
            var lastNameAttribute = await jim.Metaverse.GetMetaverseAttributeAsync(Constants.BuiltInAttributes.LastName);
            if (lastNameAttribute != null)
            {
                user.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = lastNameAttribute,
                    StringValue = familyNameClaim.Value
                });

                updateRequired = true;
                Log.Verbose("UpdateUserAttributesFromClaimsAsync: Added value from claim: " + familyNameClaim.Type);
            }
        }
    }

    // Map preferred_username claim (standard OIDC claim, often contains UPN for Entra ID)
    if (!user.HasAttributeValue(Constants.BuiltInAttributes.UserPrincipalName))
    {
        var preferredUsernameClaim = claimsPrincipal.Claims.FirstOrDefault(q => q.Type == "preferred_username");
        if (preferredUsernameClaim != null)
        {
            var upnAttribute = await jim.Metaverse.GetMetaverseAttributeAsync(Constants.BuiltInAttributes.UserPrincipalName);
            if (upnAttribute != null)
            {
                user.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = upnAttribute,
                    StringValue = preferredUsernameClaim.Value
                });

                updateRequired = true;
                Log.Verbose("UpdateUserAttributesFromClaimsAsync: Added value from claim: " + preferredUsernameClaim.Type);
            }
        }
    }

    if (updateRequired)
    {
        // update the user with the new attribute values
        await jim.Metaverse.UpdateMetaverseObjectAsync(user);
        Log.Debug("UpdateUserAttributesFromClaimsAsync: Updated user with new attribute values from some claims");
    }
}

/// <summary>
/// Fetches the OIDC discovery document from the authority's well-known endpoint.
/// This provides IDP-agnostic endpoint discovery for any OIDC-compliant provider.
/// </summary>
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
/// Extracts the API audience from an OAuth scope.
/// For Entra ID scopes like "api://client-id/access_as_user", extracts "api://client-id".
/// For other IDPs, falls back to the client ID.
/// </summary>
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
static string[] GetValidIssuers(string? authority)
{
    // Check if user has configured explicit issuers
    var configuredIssuers = Environment.GetEnvironmentVariable(Constants.Config.SsoValidIssuers);
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
/// Gets the path for storing Data Protection encryption keys.
/// Priority: 1) JIM_ENCRYPTION_KEY_PATH env var, 2) /data/keys (Docker), 3) app data directory
/// </summary>
static string GetDataProtectionKeysPath()
{
    // 1. Check for explicit environment variable
    var envPath = Environment.GetEnvironmentVariable(Constants.Config.EncryptionKeyPath);
    if (!string.IsNullOrEmpty(envPath))
    {
        Log.Information("Using encryption key path from environment: {Path}", envPath);
        EnsureDirectoryExists(envPath);
        return envPath;
    }

    // 2. Check for Docker volume mount (common in containerised deployments)
    const string dockerPath = "/data/keys";
    if (Directory.Exists("/data"))
    {
        Log.Information("Using Docker volume for encryption keys: {Path}", dockerPath);
        EnsureDirectoryExists(dockerPath);
        return dockerPath;
    }

    // 3. Fallback to application data directory (platform-specific)
    // Linux: ~/.local/share/JIM/keys
    // Windows: %LOCALAPPDATA%\JIM\keys
    var appDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JIM",
        "keys");
    Log.Information("Using application data directory for encryption keys: {Path}", appDataPath);
    EnsureDirectoryExists(appDataPath);
    return appDataPath;
}

/// <summary>
/// Ensures a directory exists, creating it with restricted permissions if necessary.
/// </summary>
static void EnsureDirectoryExists(string path)
{
    if (Directory.Exists(path))
        return;

    try
    {
        var directoryInfo = Directory.CreateDirectory(path);

        // On Unix-like systems, set restrictive permissions (700 = owner only)
        if (!OperatingSystem.IsWindows())
        {
            directoryInfo.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        }

        Log.Information("Created encryption key directory: {Path}", path);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to create encryption key directory: {Path}", path);
        throw;
    }
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
