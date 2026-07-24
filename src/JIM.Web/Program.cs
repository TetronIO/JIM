// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using Asp.Versioning;
using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Web.Models;
using JIM.Web.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.PostgresData;
using JIM.Utilities;
using JIM.Web.Hubs;
using JIM.Web.Logging;
using JIM.Web.Middleware;
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
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Microsoft.AspNetCore.OpenApi;
using Scalar.AspNetCore;
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
// JIM_THEME - Built-in colour theme name (default: refined)
// JIM_TRUSTED_PROXIES - Comma-separated trusted proxy IPs/CIDR networks; enables X-Forwarded-For/-Proto (default: none trusted)

// initial logging setup for when the application has not yet been created (bootstrapping)...
InitialiseLogging(new LoggerConfiguration(), true);

// Enable performance diagnostics for the lifetime of the process so span completions
// from the Application/Repository layers reach the Serilog pipeline.
// Web requests should be much faster than worker batches, so a tighter slow threshold makes sense here.
using var diagnosticListener = Diagnostics.EnableLogging(slowOperationThresholdMs: 250);

try
{
    // Lightweight mode: generate the OpenAPI document and exit without starting the full application.
    // Used during Docker builds and local dev via jim-openapi-generate.
    var isOpenApiGenerateMode = Environment.GetEnvironmentVariable(Constants.Config.OpenApiGenerate)
        ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    Log.Information("Starting JIM.Web{Mode}", isOpenApiGenerateMode ? " (OpenAPI generation mode)" : "");

    // Fail fast if the SSO/admin configuration is missing, before the web host (whose OIDC setup reads the
    // same variables) is built. The database-dependent part of bootstrap runs after the host is built, so it
    // can resolve a fully-configured JimApplication from DI (see CompleteJimApplicationBootstrapAsync).
    if (!isOpenApiGenerateMode)
    {
        ValidateSsoConfiguration();
    }

    var builder = WebApplication.CreateBuilder(args);

    // Reverse proxy trust (optional; container orchestration override, per CLAUDE.md's environment-variable
    // design principle). Unset by default: no forwarded headers are trusted and RemoteIpAddress is used as-is,
    // which is correct when JIM is reached directly. Set JIM_TRUSTED_PROXIES to a comma-separated list of proxy
    // IPs and/or CIDR networks when JIM sits behind a reverse proxy/load balancer, so X-Forwarded-For/-Proto are
    // trusted only from those sources and the real client IP (used for unauthenticated API rate limiting,
    // logging, HTTPS redirection, etc.) is recovered rather than the proxy's own address. Deliberately not part
    // of ValidateSsoConfiguration's fail-fast checks: it is an optional deployment-topology override, not
    // required configuration.
    var trustedProxiesValue = Environment.GetEnvironmentVariable(Constants.Config.TrustedProxies);
    var trustedProxies = TrustedProxyParser.Parse(trustedProxiesValue);
    if (!trustedProxies.IsEmpty)
    {
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in trustedProxies.KnownProxies)
                options.KnownProxies.Add(proxy);
            foreach (var network in trustedProxies.KnownNetworks)
                options.KnownIPNetworks.Add(network);
        });
        Log.Information("Trusting forwarded headers from {ProxyCount} known proxy address(es) and {NetworkCount} known network(s)",
            trustedProxies.KnownProxies.Count, trustedProxies.KnownNetworks.Count);
    }

    // Configure database connection
    var connectionString = JimDbContext.BuildConnectionString();

    // Use DbContextFactory for Blazor Server to avoid concurrent DbContext access issues
    // Blazor Server pre-rendering and interactive rendering can happen concurrently
    // Note: EnableRetryOnFailure is NOT configured here because the codebase has
    // manual transactions (BeginTransactionAsync) that are incompatible with
    // NpgsqlRetryingExecutionStrategy. See issue #408.
    // Transient failures are handled at the API level by GlobalExceptionHandler (HTTP 503).
    builder.Services.AddDbContextFactory<JimDbContext>(options =>
        options.UseNpgsql(connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(warnings => warnings.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning,
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning)));

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
        var repo = sp.GetRequiredService<IRepository>();
        var syncRepo = repo.Sync;
        var jim = new JimApplication(repo, syncRepository: syncRepo);
        // Inject credential protection service for connector password encryption/decryption
        jim.CredentialProtection = sp.GetService<ICredentialProtectionService>();
        return jim;
    });
    builder.Services.AddSingleton<IJimApplicationFactory, JimApplicationFactory>();
    builder.Services.AddSingleton<LogReaderService>();
    builder.Services.AddExpressionEvaluation();

    // Register UI theme settings from environment variable
    var themeName = Environment.GetEnvironmentVariable(Constants.Config.Theme) ?? "navy-o6";
    builder.Services.AddSingleton(new ThemeSettings
    {
        LightThemePath = $"css/themes/{themeName}-light.css",
        DarkThemePath = $"css/themes/{themeName}-dark.css"
    });

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

    // Skip authentication setup in OpenAPI generation mode (no IdP needed for schema generation)
    if (!isOpenApiGenerateMode)
    {
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
            // Forward to appropriate authentication scheme based on request headers
            options.ForwardDefaultSelector = context =>
            {
                // Check if the request has an API key header
                if (context.Request.Headers.ContainsKey("X-API-Key"))
                {
                    return ApiKeyAuthenticationHandler.SchemeName;
                }

                // Check if the request has a Bearer token (JWT)
                var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return JwtBearerDefaults.AuthenticationScheme;
                }

                // Otherwise use default Cookie authentication
                return null;
            };

            // Global authentication epoch: reject any cookie issued before
            // ServiceSettings.SessionsValidFromUtc, forcing re-authentication. A factory reset advances
            // this value so every existing portal session is invalidated at once (no stale role claims
            // or Metaverse Object references survive a wipe). API keys are unaffected; they are
            // validated against the database per request via their own scheme.
            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = async context =>
                {
                    var issuedUtc = context.Properties?.IssuedUtc;
                    if (issuedUtc == null)
                        return;

                    var factory = context.HttpContext.RequestServices.GetService<IJimApplicationFactory>();
                    if (factory == null)
                        return;

                    using var jim = factory.Create();
                    var serviceSettings = await jim.ServiceSettings.GetServiceSettingsAsync();
                    if (serviceSettings?.SessionsValidFromUtc is { } validFrom && issuedUtc.Value.UtcDateTime < validFrom)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                }
            };
        })
        .AddOpenIdConnect(options =>
        {
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.UseTokenLifetime = true; // respect the IdP token lifetime and use our session lifetime

            // Persist the ID token in the authentication cookie so that, on sign-out, the OIDC
            // middleware can include it as the id_token_hint parameter on the end-session request.
            // Keycloak (and other strict OIDC providers) require id_token_hint on RP-initiated
            // logout per the OIDC spec; without it, Keycloak rejects the request with
            // "Missing parameters: id_token_hint".
            options.SaveTokens = true;

            options.Authority = authority;

            // Allow HTTP authority for local development (e.g. bundled Keycloak at http://localhost:8181)
            if (authority != null && authority.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                options.RequireHttpsMetadata = false;

            // Development-only: relax correlation/nonce cookies so plain-HTTP localhost works in Safari.
            // ASP.NET Core defaults both cookies to SameSite=None; Secure. Chrome/Edge/Firefox treat
            // http://localhost as a secure context and accept Secure cookies on it; Safari does not
            // (WebKit bug 232088), silently drops them, and the OIDC callback fails with "Correlation
            // failed". Production keeps the secure defaults, which are correct over HTTPS and required
            // if the IdP ever responds via form_post (cross-site POST needs SameSite=None).
            if (builder.Environment.IsDevelopment())
            {
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            }
            options.ClientId = clientId;
            options.ClientSecret = clientSecret;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.Scope.Add("profile");

            // Disable Pushed Authorization Requests (PAR), enabled by default in .NET 10.
            // JIM must work with any OIDC provider; PAR support varies widely (e.g. Entra ID
            // does not support PAR at all). Standard authorization code flow with PKCE provides
            // sufficient security for JIM's deployment scenarios.
            options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;

            // Preserve standard OIDC claim names (sub, name, email, etc.) instead of mapping them
            // to Microsoft's legacy XML-based claim URIs. This makes JIM IDP-agnostic.
            options.MapInboundClaims = false;

            // With MapInboundClaims disabled, Identity.Name won't resolve automatically because
            // .NET looks for the legacy XML URI by default. Point it at the standard OIDC claim.
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = Constants.BuiltInRoles.RoleClaimType;

            // By default, the ASP.NET Core OpenIdConnect handler drops a number of "protocol" claims
            // (iss, aud, azp, acr, auth_time, ...) after it has validated them, so they never reach
            // the cookie identity. Explicitly map them back in so administrators can diagnose sign-in
            // issues from the /claims page without enabling trace logging. These values are not used
            // for authorisation decisions; they are informational only.
            options.ClaimActions.MapJsonKey("iss", "iss");
            options.ClaimActions.MapJsonKey("aud", "aud");
            options.ClaimActions.MapJsonKey("azp", "azp");
            options.ClaimActions.MapJsonKey("acr", "acr");
            options.ClaimActions.MapJsonKey("auth_time", "auth_time");

            // Accept tokens from any configured valid issuer (supports Docker DNS + localhost dual-path)
            if (validIssuers.Length > 0)
            {
                options.TokenValidationParameters.ValidIssuers = validIssuers;
                options.TokenValidationParameters.ValidateIssuer = true;
            }

            // intercept the user login when a token is received and validate we can map them to a JIM user
            options.Events.OnTicketReceived = async ctx =>
            {
                var user = await ResolveAndAttachJimIdentityAsync(ctx.Principal, ctx.HttpContext);
                if (user != null)
                {
                    // One security audit Activity per session establishment (not per request), attributed to the
                    // signed-in user. Non-blocking: a failed audit write must never fail the sign-in it instruments.
                    RecordSuccessfulSignInEvent(ctx.HttpContext, user);
                }
            };

            // Security audit events (issue #500): the IdP rejected the sign-in outright (e.g. access denied,
            // provider-side error) before any token reached us.
            options.Events.OnRemoteFailure = ctx =>
            {
                RecordFailedSignInEvent(ctx.HttpContext, CategoriseOidcFailure(ctx.Failure));
                return Task.CompletedTask;
            };

            // Security audit events (issue #500): a token was returned but failed local validation (bad signature,
            // expired, wrong audience, replayed nonce, ...).
            options.Events.OnAuthenticationFailed = ctx =>
            {
                RecordFailedSignInEvent(ctx.HttpContext, CategoriseOidcFailure(ctx.Exception));
                return Task.CompletedTask;
            };

            // Prevent OIDC redirects for API requests - they should return 401 instead
            // Exception: endpoints marked with [AllowAnonymous] should not trigger authentication
            options.Events.OnRedirectToIdentityProvider = async ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    // Check if the endpoint allows anonymous access
                    var endpoint = ctx.HttpContext.GetEndpoint();
                    var allowAnonymous = endpoint?.Metadata?.GetMetadata<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() != null;

                    if (allowAnonymous)
                    {
                        Log.Debug("Skipping OIDC redirect for anonymous API endpoint: {Path}", ctx.Request.Path);
                        // Don't handle the response - let the request continue to the controller
                        ctx.HandleResponse();
                        return;
                    }

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

            if (authority != null && authority.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                options.RequireHttpsMetadata = false;
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

            options.TokenValidationParameters.RoleClaimType = Constants.BuiltInRoles.RoleClaimType;

            // Resolve (and, for the initial admin, just-in-time provision) the JIM identity for
            // bearer-token callers too, so the PowerShell module / REST API can administer a fresh
            // deployment without anyone first signing in to the web portal. The resolved roles and
            // MetaverseObject id are attached to this request's principal.
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async ctx =>
                {
                    await ResolveAndAttachJimIdentityAsync(ctx.Principal, ctx.HttpContext);
                }
            };
        });

    // setup authorisation policies
    builder.Services.AddAuthorization();

    // REST API rate limiting (issue #500, OWASP Top 10:2025 A02). Registers the settings cache and the global
    // limiter; UseRateLimiter() below wires it into the pipeline. Not needed in OpenAPI generation mode, which
    // never accepts requests.
    builder.Services.AddApiRateLimiting();
    } // end of !isOpenApiGenerateMode auth block

    // Add services to the container.
    builder.Services.AddRazorPages();
    builder.Services.AddServerSideBlazor();

    // Add API controller support with JSON serialisation policy centralised in
    // ApiJsonConfiguration (see that class for the rationale and for unit tests).
    builder.Services.AddControllers()
        .AddJsonOptions(options => JIM.Web.ApiJsonConfiguration.Configure(options.JsonSerializerOptions));
    builder.Services.AddEndpointsApiExplorer();

    // Increase MaxDepth for the HTTP JSON options used by the OpenAPI schema generator.
    // The default of 64 is insufficient for JIM's deep DTO graph; the schema exporter and
    // its internal ResolveReferences expansion need headroom for recursive types.
    // This only affects schema generation and minimal API endpoints; MVC controllers use
    // separate JsonOptions. See: https://github.com/dotnet/aspnetcore/issues/63857
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.MaxDepth = 256;
    });

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

    // Fetch OIDC discovery document to get IDP-agnostic authorization endpoints.
    // In OpenAPI generation mode, use constructed placeholder URLs to avoid requiring a live IdP.
    // These only affect Scalar's "Try It" auth flow, not the documentation content.
    OidcDiscoveryDocument oidcConfig;
    if (isOpenApiGenerateMode)
    {
        var authorityBase = (authority ?? "https://idp.example.com/realms/jim").TrimEnd('/');
        oidcConfig = new OidcDiscoveryDocument
        {
            AuthorizationEndpoint = $"{authorityBase}/protocol/openid-connect/auth",
            TokenEndpoint = $"{authorityBase}/protocol/openid-connect/token"
        };
    }
    else
    {
        oidcConfig = await FetchOidcDiscoveryDocumentAsync(authority!);
    }

    // Setup OpenAPI with OAuth2 + API Key security schemes for API documentation
    builder.Services.AddOpenApi("v1", options =>
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "JIM API",
                Version = "v1",
                Description = "Programmatic access to JIM's identity lifecycle management capabilities. Use this API to configure Connected Systems, manage Synchronisation Rules, query the Metaverse, monitor Activities, and automate deployment via scripting or CI/CD pipelines.",
                Contact = new OpenApiContact
                {
                    Name = "Tetron",
                    Url = new Uri("https://github.com/TetronIO/JIM")
                }
            };

            // OAuth2 security scheme for SSO authentication
            var oauth2Scheme = new OpenApiSecurityScheme
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
            };

            // API Key security scheme for non-interactive authentication
            var apiKeyScheme = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-API-Key",
                Description = "API key for non-interactive authentication. Format: jim_ak_<random>"
            };

            var components = document.Components ??= new OpenApiComponents();
            var schemes = components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            schemes.Add("OAuth2", oauth2Scheme);
            schemes.Add("ApiKey", apiKeyScheme);

            // Both authentication methods are valid; API will accept either
            (document.Security ??= []).Add(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecuritySchemeReference("OAuth2"),
                    new List<string> { "openid", apiScope! }
                },
                {
                    new OpenApiSecuritySchemeReference("ApiKey"),
                    new List<string>()
                }
            });

            return Task.CompletedTask;
        });
    });

    // setup logging properly now (it's been bootstrapped initially)
    builder.Services.AddSerilog(configuration => InitialiseLogging(configuration, false));

    // setup MudBlazor
    builder.Services.AddMudServices(config =>
    {
        config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
    });

    // User preferences service for storing UI settings in browser localStorage
    builder.Services.AddScoped<IUserPreferenceService, UserPreferenceService>();

    // Real-time UI notifications (issue #307): a background service listens for PostgreSQL NOTIFY events
    // on a dedicated non-pooled connection and fans them out to the in-process relay (consumed by Blazor
    // Server components) and the SignalR hub (for non-Blazor consumers). The listener connection string is
    // resolved lazily, so OpenAPI generation mode (which never starts hosted services) is unaffected.
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<UiNotificationService>();
    builder.Services.AddSingleton<IUiNotificationService>(sp => sp.GetRequiredService<UiNotificationService>());
    builder.Services.AddSingleton<IDatabaseNotificationListener>(_ =>
        new PostgresNotificationListener(JimDbContext.BuildListenerConnectionString()));
    builder.Services.AddHostedService<NotificationListenerService>();

    // Live run-profile progress (issue #202): one shared ETA tracker so the progress API endpoint
    // and the Activity detail page derive their throughput estimates from the same sample history.
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<IActivityEtaTracker, ActivityEtaTracker>();

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }

    // Defence-in-depth security response headers (issue #500, OWASP Top 10:2025 A02), applied site-wide: Blazor
    // UI, static assets and the REST API alike. Registered here, not gated on isOpenApiGenerateMode (unlike
    // UseRateLimiter below): the middleware needs no registered services, so there is nothing to fail at
    // pipeline-build time, and it never actually executes in that mode anyway (document generation resolves
    // IOpenApiDocumentProvider directly and returns before app.Run() starts accepting requests).
    // Placement is deliberate on both sides: it must come AFTER UseExceptionHandler/UseHsts above, because
    // ExceptionHandlerMiddleware calls Response.Clear() (wiping any headers already set) before re-running the
    // downstream pipeline against the error path, so headers set upstream of it would be lost on a genuine 500;
    // running downstream of it means this middleware re-applies its headers on that second pass. It must come
    // BEFORE UseStaticFiles below, because StaticFileMiddleware short-circuits (never calls next()) once it
    // serves a match, so anything registered after it would never run for a static asset request.
    app.UseSecurityHeaders();

    // API documentation: serve Scalar API reference with the OpenAPI document.
    // Static mode: if a pre-generated file exists at wwwroot/api/openapi/v1.json (baked into
    // Docker images during build), serve it via UseStaticFiles. Works in any environment.
    // Dynamic fallback: in development without a static file, generate at runtime with caching.
    // See https://github.com/dotnet/aspnetcore/issues/63857 for why runtime generation is expensive.
    var staticOpenApiPath = Path.Combine(app.Environment.WebRootPath, "api", "openapi", "v1.json");
    var hasStaticOpenApiDoc = File.Exists(staticOpenApiPath);

    // JIM-branded Scalar configuration: deep navy background with purple accent,
    // matching the JIM web UI theme (navy-o6)
    void ConfigureScalar(ScalarOptions options, string openApiRoutePattern)
    {
        options.WithTitle("JIM API Reference")
            .WithFavicon("/images/jim-logo.png")
            .ForceDarkMode()
            // Scalar defaults to loading its "Inter"/"JetBrains Mono" fonts from its own CDN. JIM is air-gap
            // deployable (no third-party network dependency, per root CLAUDE.md's design principles) and the
            // stage-one CSP's font-src is 'self' only, so leaving the default enabled would silently degrade
            // to system fonts under CSP; disable it outright so the API reference page never attempts the
            // external fetch in the first place.
            .DisableDefaultFonts()
            .WithCustomCss("""
                .dark-mode {
                    --scalar-background-1: #051526;
                    --scalar-background-2: #0d1e30;
                    --scalar-background-3: #15293c;
                    --scalar-background-accent: rgba(97, 75, 158, 0.12);
                    --scalar-color-1: rgba(255, 255, 255, 0.85);
                    --scalar-color-2: rgba(255, 255, 255, 0.55);
                    --scalar-color-3: rgba(255, 255, 255, 0.35);
                    --scalar-color-accent: #9585c8;
                    --scalar-border-color: rgba(255, 255, 255, 0.08);
                    --scalar-button-1: #614b9e;
                    --scalar-button-1-hover: #4e3c80;
                    --scalar-button-1-color: #ffffff;
                }
                """)
            .WithOpenApiRoutePattern(openApiRoutePattern)
            .AddPreferredSecuritySchemes("OAuth2", "ApiKey")
            .AddAuthorizationCodeFlow("OAuth2", flow =>
            {
                flow.ClientId = clientId!;
                flow.Pkce = Pkce.Sha256;
            });
    }

    if (hasStaticOpenApiDoc)
    {
        // Static file is served automatically by UseStaticFiles() at /api/openapi/v1.json
        app.MapScalarApiReference("/api/reference", o => ConfigureScalar(o, "/api/openapi/v1.json"));
        app.Logger.LogInformation("Serving static OpenAPI document from {Path}", staticOpenApiPath);
    }
    else if (app.Environment.IsDevelopment())
    {
        // Runtime generation with response caching (first load takes ~1-2 minutes)
        string? cachedOpenApiDoc = null;
        app.MapOpenApi("/api/openapi/{documentName}.json");
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/openapi"))
            {
                if (cachedOpenApiDoc != null)
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(cachedOpenApiDoc);
                    return;
                }

                var originalBody = context.Response.Body;
                using var memStream = new MemoryStream();
                context.Response.Body = memStream;

                await next(context);

                if (context.Response.StatusCode == 200)
                {
                    memStream.Seek(0, SeekOrigin.Begin);
                    cachedOpenApiDoc = await new StreamReader(memStream).ReadToEndAsync();
                    memStream.Seek(0, SeekOrigin.Begin);
                    context.Response.Body = originalBody;
                    await memStream.CopyToAsync(originalBody);

                    GC.Collect(2, GCCollectionMode.Aggressive, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, true);
                }
                else
                {
                    memStream.Seek(0, SeekOrigin.Begin);
                    context.Response.Body = originalBody;
                    await memStream.CopyToAsync(originalBody);
                }
                return;
            }
            await next(context);
        });

        app.MapScalarApiReference("/api/reference", o => ConfigureScalar(o, "/api/openapi/{documentName}.json"));
        app.Logger.LogInformation("No static OpenAPI document found; using runtime generation (development only)");
    }

    // Forwarded headers (X-Forwarded-For / X-Forwarded-Proto) must be processed before anything that reads the
    // client IP or scheme: HTTPS redirection, authentication, and rate limiting all depend on it. A no-op unless
    // JIM_TRUSTED_PROXIES was set above (the common case: JIM reached directly, no proxy to trust).
    if (!trustedProxies.IsEmpty)
        app.UseForwardedHeaders();

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

    // REST API rate limiting (issue #500). Must run after authentication and role enrichment so the
    // GlobalLimiter's partition factory sees a fully-resolved identity (roles, the Metaverse Object ID claim),
    // and BEFORE authorisation: a request that will fail authorisation (missing/invalid credentials, insufficient
    // role) must still be counted and throttled per client IP, otherwise unauthenticated brute-force/credential
    // probing against protected endpoints would bypass the limiter entirely via authorisation's 401/403
    // short-circuit. Applied pipeline-wide (not UseWhen-scoped to /api) because the GlobalLimiter itself already
    // returns a no-op limiter for non-API paths; a single registration keeps that decision in one place
    // (RateLimitPartitionResolver) instead of splitting it between the pipeline and the partition factory.
    // Skipped in OpenAPI generation mode, matching the AddApiRateLimiting registration above: that mode never
    // serves requests, and UseRateLimiter verifies its services are registered at pipeline-build time, so an
    // ungated call crashes the openapi-gen Docker build stage at startup.
    if (!isOpenApiGenerateMode)
        app.UseRateLimiter();

    app.UseAuthorization();

    // Map API controllers
    app.MapControllers();

    // Map Blazor endpoints
    app.MapBlazorHub();

    // Map the real-time notification hub for non-Blazor consumers (issue #307)
    app.MapHub<JimNotificationHub>("/hubs/notifications");

    app.MapFallbackToPage("/_Host");

    // only enable request logging if configured to do so from env vars, as it adds a LOT to the logs
    var enableRequestLogging = Environment.GetEnvironmentVariable(Constants.Config.LogRequests);
    if (enableRequestLogging != null && bool.Parse(enableRequestLogging))
        app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = (_, _, _) => Serilog.Events.LogEventLevel.Debug;
        });

    // In OpenAPI generation mode, generate the document and exit without starting Kestrel
    if (isOpenApiGenerateMode)
    {
        var outputPath = Environment.GetEnvironmentVariable(Constants.Config.OpenApiOutputPath)
            ?? Path.Combine(app.Environment.WebRootPath, "api", "openapi", "v1.json");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        app.Logger.LogInformation("Generating OpenAPI document...");

        var documentProvider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await documentProvider.GetOpenApiDocumentAsync();

        await using var fileStream = File.Create(outputPath);
        await using var textWriter = new StreamWriter(fileStream);
        var jsonWriter = new OpenApiJsonWriter(textWriter);
        document.SerializeAs(OpenApiSpecVersion.OpenApi3_1, jsonWriter);
        await textWriter.FlushAsync();

        var fileSize = new FileInfo(outputPath).Length;
        app.Logger.LogInformation("OpenAPI document generated at {Path} ({Size:N0} bytes)", outputPath, fileSize);
        return 0;
    }

    // Complete bootstrap now the host is built: wait for the database to be ready, mirror SSO configuration, and
    // create the infrastructure API key. This runs here (not before builder.Build) so it resolves JimApplication
    // from DI with the credential protection service attached; a hand-built instance would lack it and silently
    // degrade configuration change capture, whose hash key is stored encrypted at rest.
    await CompleteJimApplicationBootstrapAsync(app);

    // Eager initialisation: warm up EF Core compiled model and database connection pool
    // before accepting requests. This prevents the first browser request from hanging while
    // .NET JIT-compiles the EF Core model, establishes the initial DB connection, and builds
    // the Blazor component tree. The cost is a slightly longer container startup, but the
    // first request will be as fast as any subsequent request.
    app.Logger.LogInformation("Warming up EF Core model and database connection pool...");
    using (var warmupScope = app.Services.CreateScope())
    {
        var factory = warmupScope.ServiceProvider.GetRequiredService<IDbContextFactory<JimDbContext>>();
        await using var warmupContext = await factory.CreateDbContextAsync();
        // Force EF Core to build and cache its compiled model by executing a trivial query
        _ = await warmupContext.Database.CanConnectAsync();
    }
    app.Logger.LogInformation("Warmup complete — connection pool: Min={MinPoolSize}, Max={MaxPoolSize}",  5, 30);

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
    // Suppress EF Core SQL query logging — these are noise with no diagnostic value
    loggerConfiguration.MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Fatal);
    loggerConfiguration.Enrich.FromLogContext();

    // The sinks sit behind a demoting wrapper so benign Blazor circuit-disconnect noise (framework Error
    // events caused by JSDisconnectedException when a browser disconnects mid-operation) is logged at
    // Warning. Real circuit errors pass through unchanged. See CircuitDisconnectDemotingSink.
    var sinkLogger = new LoggerConfiguration()
        .MinimumLevel.Verbose()
        .WriteTo.File(
            formatter: new RenderedCompactJsonFormatter(),
            path: Path.Combine(loggingPath, "jim.web..log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 100,
            fileSizeLimitBytes: 50 * 1024 * 1024,  // 50MB per file; keeps files manageable for analysis
            rollOnFileSizeLimit: true)
        .WriteTo.Console()
        .CreateLogger();
    loggerConfiguration.WriteTo.Sink(new CircuitDisconnectDemotingSink(sinkLogger));

    if (assignLogLogger)
        Log.Logger = loggerConfiguration.CreateLogger();
}

// Validates that the SSO and initial-administrator environment variables required for authentication are present.
// Runs before the web host is built so a misconfigured deployment fails fast with a clear message, before the OIDC
// setup (which reads the same variables) is configured.
static void ValidateSsoConfiguration()
{
    Log.Verbose("ValidateSsoConfiguration: Called.");
    string[] requiredKeys =
    [
        Constants.Config.SsoAuthority,
        Constants.Config.SsoClientId,
        Constants.Config.SsoSecret,
        Constants.Config.SsoClaimType,
        Constants.Config.SsoMvAttribute,
        Constants.Config.SsoInitialAdmin
    ];

    var missingKey = requiredKeys.FirstOrDefault(key => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)));
    if (missingKey != null)
        throw new Exception($"{missingKey} environment variable missing");
}

// Completes JIM.Web bootstrap once the host is built and the database is ready: mirrors the SSO configuration into
// the database (so administrators can view it in the portal) and creates the infrastructure API key if requested.
// Resolves JimApplication from the DI container rather than constructing one by hand, so it carries the credential
// protection service. Without it, configuration change capture silently degrades: the capture hash key is stored
// encrypted at rest, so reading it back without the protection service fails and no snapshot is recorded.
static async Task CompleteJimApplicationBootstrapAsync(WebApplication app)
{
    // Environment variables were validated by ValidateSsoConfiguration before the host was built.
    var ssoAuthority = Environment.GetEnvironmentVariable(Constants.Config.SsoAuthority)!;
    var ssoClientId = Environment.GetEnvironmentVariable(Constants.Config.SsoClientId)!;
    var ssoSecret = Environment.GetEnvironmentVariable(Constants.Config.SsoSecret)!;
    var uniqueIdentifierClaimType = Environment.GetEnvironmentVariable(Constants.Config.SsoClaimType)!;
    var uniqueIdentifierMetaverseAttributeName = Environment.GetEnvironmentVariable(Constants.Config.SsoMvAttribute)!;

    while (true)
    {
        // A fresh scope per attempt gives each iteration its own DbContext and disposes it when the scope ends.
        using var scope = app.Services.CreateScope();
        var jimApplication = scope.ServiceProvider.GetRequiredService<JimApplication>();
        if (await jimApplication.IsApplicationReadyAsync())
        {
            await jimApplication.InitialiseSsoAsync(ssoAuthority, ssoClientId, ssoSecret, uniqueIdentifierClaimType, uniqueIdentifierMetaverseAttributeName);
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
    var existingKey = await jim.Security.GetApiKeyByHashAsync(keyHash);

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
        Created = DateTime.UtcNow,
        CreatedByType = JIM.Models.Activities.ActivityInitiatorType.System,
        CreatedByName = "System",
        ExpiresAt = DateTime.UtcNow.AddHours(24),
        IsEnabled = true,
        IsInfrastructureKey = true,
        Roles = [adminRole]
    };

    await jim.Security.CreateApiKeyAsync(apiKey);
    Log.Information("InitialiseInfrastructureApiKeyAsync: Created infrastructure API key (prefix: {Prefix}, expires: {Expiry}).",
        keyPrefix, apiKey.ExpiresAt);
}

/// <summary>
/// Resolves the authenticated SSO identity to a JIM MetaverseObject (bootstrapping the initial
/// administrator just-in-time on first contact, via either the interactive browser or a bearer
/// token), then attaches a JIM ClaimsIdentity carrying the user's roles and MetaverseObject id to
/// the current principal. Without a successful mapping the principal gains no JIM roles and cannot
/// access JIM. The provisioning decision itself lives in the application layer (jim.Auth); this
/// method only bridges the web layer's ClaimsPrincipal to that logic and back.
/// </summary>
/// <returns>The resolved MetaverseObject, or null if no JIM identity could be established. The cookie flow
/// (<c>OnTicketReceived</c>) uses a non-null result to record an interactive sign-in success security audit event;
/// the bearer-token flow (<c>OnTokenValidated</c>) discards it, since bearer calls are per-request, not per-session.</returns>
static async Task<MetaverseObject?> ResolveAndAttachJimIdentityAsync(ClaimsPrincipal? principal, HttpContext httpContext)
{
    if (principal?.Identity == null)
    {
        Log.Error("ResolveAndAttachJimIdentityAsync: Request has no principal or identity.");
        return null;
    }

    var factory = httpContext.RequestServices.GetService<IJimApplicationFactory>();
    if (factory == null)
    {
        Log.Error("ResolveAndAttachJimIdentityAsync: Could not resolve IJimApplicationFactory.");
        return null;
    }

    using var jim = factory.Create();
    var serviceSettings = await jim.ServiceSettings.GetServiceSettingsAsync() ??
        throw new InvalidOperationException("ServiceSettings was null. Cannot authorise the user.");

    if (string.IsNullOrEmpty(serviceSettings.SSOUniqueIdentifierClaimType))
    {
        Log.Error("ResolveAndAttachJimIdentityAsync: ServiceSettings.SSOUniqueIdentifierClaimType is not configured.");
        return null;
    }

    var uniqueId = principal.FindFirstValue(serviceSettings.SSOUniqueIdentifierClaimType);
    if (string.IsNullOrEmpty(uniqueId))
    {
        Log.Warning("ResolveAndAttachJimIdentityAsync: User '{Name}' has no '{ClaimType}' claim; cannot identify them.",
            LogSanitiser.Sanitise(principal.Identity.Name), serviceSettings.SSOUniqueIdentifierClaimType);
        return null;
    }

    // Hand a provider-agnostic identity to the application layer to resolve or provision.
    var ssoIdentity = new SsoIdentity
    {
        UniqueId = uniqueId,
        DisplayName = principal.FindFirstValue("name"),
        FirstName = principal.FindFirstValue("given_name"),
        LastName = principal.FindFirstValue("family_name"),
        UserPrincipalName = principal.FindFirstValue("preferred_username")
    };

    var user = await jim.Auth.ResolveOrProvisionSsoIdentityAsync(ssoIdentity);
    if (user == null)
    {
        // Not a known user and not the initial admin: no roles are added, so they cannot access JIM.
        Log.Debug("ResolveAndAttachJimIdentityAsync: No JIM identity for '{ClaimType}'='{UniqueId}'.",
            serviceSettings.SSOUniqueIdentifierClaimType, LogSanitiser.Sanitise(uniqueId));
        return null;
    }

    // Build a JIM-specific identity carrying the user's role claims and MetaverseObject id, and add it
    // to the current principal so this request (and, for the cookie flow, the session) is authorised.
    var roleClaims = (await jim.Security.GetMetaverseObjectRolesAsync(user))
        .Select(role => new Claim(Constants.BuiltInRoles.RoleClaimType, role.Name))
        .ToList();

    // Baseline role granting access to JIM.Web; a user without a JIM identity never receives it.
    roleClaims.Add(new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User));
    roleClaims.Add(new Claim(Constants.BuiltInClaims.MetaverseObjectId, user.Id.ToString()));

    var jimIdentity = new ClaimsIdentity(roleClaims, authenticationType: null, nameType: null, roleType: Constants.BuiltInRoles.RoleClaimType) { Label = "JIM" };
    principal.AddIdentity(jimIdentity);

    return user;
}

/// <summary>
/// Records an interactive sign-in success security audit event on a background task with a fresh DI scope
/// (via <see cref="IJimApplicationFactory"/>, which is safe to call after the originating request's DI scope has
/// been disposed, since it resolves its DbContext via <c>IDbContextFactory</c>). Non-blocking: a failed audit
/// write must never fail a sign-in that has already succeeded.
/// </summary>
static void RecordSuccessfulSignInEvent(HttpContext httpContext, MetaverseObject user)
{
    var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
    var factory = httpContext.RequestServices.GetService<IJimApplicationFactory>();
    if (factory == null)
    {
        Log.Warning("RecordSuccessfulSignInEvent: Could not resolve IJimApplicationFactory; sign-in success event not recorded.");
        return;
    }

    _ = Task.Run(async () =>
    {
        try
        {
            using var jim = factory.Create();
            await jim.SecurityAudit.RecordInteractiveSignInSucceededAsync(user, clientIp);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "RecordSuccessfulSignInEvent: Failed to record interactive sign-in success event");
        }
    });
}

/// <summary>
/// Records an aggregated interactive sign-in failure security audit event on a background task with a fresh DI
/// scope, mirroring <see cref="RecordSuccessfulSignInEvent"/>. Non-blocking and never throws into the caller:
/// <c>SecurityAuditServer.RecordFailedAuthenticationAsync</c> itself swallows audit-write failures.
/// </summary>
static void RecordFailedSignInEvent(HttpContext httpContext, string reason)
{
    var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
    var factory = httpContext.RequestServices.GetService<IJimApplicationFactory>();
    if (factory == null)
    {
        Log.Warning("RecordFailedSignInEvent: Could not resolve IJimApplicationFactory; failed sign-in event not recorded.");
        return;
    }

    _ = Task.Run(async () =>
    {
        try
        {
            using var jim = factory.Create();
            await jim.SecurityAudit.RecordFailedAuthenticationAsync("Interactive sign-in failed", reason, apiKeyPrefix: null, clientIp);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "RecordFailedSignInEvent: Failed to record failed authentication event");
        }
    });
}

/// <summary>
/// Maps an OIDC sign-in failure exception to a short, sanitised reason category for the security audit log.
/// Deliberately does not surface the raw exception message: it can carry attacker-influenced content (e.g. a
/// manipulated OIDC <c>error_description</c>) and is unbounded in length, neither of which belongs in an
/// aggregation-key column (see Activity.SecurityEventReason).
/// </summary>
static string CategoriseOidcFailure(Exception? exception)
{
    return exception switch
    {
        null => "OIDC sign-in failed",
        SecurityTokenExpiredException => "OIDC token expired",
        SecurityTokenValidationException => "OIDC token validation failed",
        SecurityTokenException => "OIDC token error",
        _ when exception.Message.Contains("Correlation failed", StringComparison.OrdinalIgnoreCase) => "OIDC correlation failed",
        _ when exception.Message.Contains("access_denied", StringComparison.OrdinalIgnoreCase) => "OIDC access denied",
        _ => "OIDC sign-in failed"
    };
}

/// <summary>
/// Fetches the OIDC discovery document from the authority's well-known endpoint.
/// This provides IDP-agnostic endpoint discovery for any OIDC-compliant provider.
/// Retries with exponential backoff to handle slow-starting identity providers (e.g. Keycloak).
/// </summary>
static async Task<OidcDiscoveryDocument> FetchOidcDiscoveryDocumentAsync(string authority)
{
    const int maxRetries = 5;
    const int baseDelayMs = 2000;

    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var discoveryUrl = $"{authority.TrimEnd('/')}/.well-known/openid-configuration";

    Log.Information("Fetching OIDC discovery document from {Url}", discoveryUrl);

    var attempt = 0;
    while (true)
    {
        try
        {
            var response = await httpClient.GetAsync(discoveryUrl);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var doc = JsonSerializer.Deserialize<OidcDiscoveryDocument>(content)
                ?? throw new ApplicationException("Failed to parse OIDC discovery document");

            Log.Information("OIDC discovery complete. Authorisation endpoint: {AuthEndpoint}", doc.AuthorizationEndpoint);
            return doc;
        }
        catch (Exception ex) when (IsTransientHttpError(ex) && attempt < maxRetries)
        {
            attempt++;
            var delay = baseDelayMs * (int)Math.Pow(2, attempt - 1);
            Log.Warning(
                "OIDC discovery attempt {Attempt}/{MaxRetries} failed: {Message}. Retrying in {Delay}ms...",
                attempt, maxRetries, ex.Message, delay);
            await Task.Delay(delay);
        }
    }
}

static bool IsTransientHttpError(Exception ex) =>
    ex is HttpRequestException or TaskCanceledException;

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
