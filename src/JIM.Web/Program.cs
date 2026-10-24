// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Utilities;
using JIM.Application;
using JIM.Application.Hosting;
using JIM.Application.Diagnostics;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Data;
using JIM.Web.Authentication;
using JIM.Web.Models;
using JIM.Web.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.PostgresData;
using JIM.Utilities;
using JIM.Web.Extensions;
using JIM.Web.Extensions.Api;
using JIM.Web.Hubs;
using JIM.Web.Logging;
using JIM.Web.Middleware;
using JIM.Web.Scim;
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
            // Both suppressions are explained in JimDbContext.OnConfiguring. The
            // PendingModelChangesWarning one is load-bearing: the Npgsql legacy timestamp
            // switch makes the runtime model differ from the migrations by 99 DateTime
            // columns, so removing it stops this service booting.
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

        // Let small configuration change previews (#827) run in this process rather than waiting for JIM.Worker to
        // poll for them. Same injection point and reasoning as the line above: the implementation is a JIM.Web
        // concern the application layer declares but cannot construct.
        jim.ConfigurationChangePreviews.BackgroundRunner = sp.GetService<IConfigurationChangePreviewBackgroundRunner>();
        return jim;
    });
    builder.Services.AddSingleton<IJimApplicationFactory, JimApplicationFactory>();
    builder.Services.AddSingleton<IFailedAuthenticationRecorder, FailedAuthenticationRecorder>();
    // SCIM summary Activity (#124, D17): requests counted in memory, flushed every ten seconds and at shutdown.
    builder.Services.AddSingleton<JIM.Web.Scim.ScimRequestTallyBuffer>();
    builder.Services.AddScoped<JIM.Web.Scim.ScimRequestTallyFilter>();
    builder.Services.AddSingleton<JIM.Web.Scim.ScimRequestTallyFlusher>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<JIM.Web.Scim.ScimRequestTallyFlusher>());
    builder.Services.AddSingleton<LogReaderService>();
    builder.Services.AddExpressionEvaluation();

    // Register UI theme settings from environment variable.
    // The paths carry a content-derived version because these are the only stylesheets not emitted by a tag helper
    // with asp-append-version: the same value is used by the <link> in _Layout.cshtml, by Error.cshtml and by the
    // runtime theme swap in MainLayout, so versioning it here is what covers all three. Without it a browser keeps
    // a theme file across an upgrade that changed it, while every other stylesheet refreshes.
    var themeName = Environment.GetEnvironmentVariable(Constants.Config.Theme) ?? "navy-o6";
    builder.Services.AddSingleton(new ThemeSettings
    {
        LightThemePath = JIM.Web.Helpers.AppendFileVersion(builder.Environment.WebRootPath, $"css/themes/{themeName}-light.css"),
        DarkThemePath = JIM.Web.Helpers.AppendFileVersion(builder.Environment.WebRootPath, $"css/themes/{themeName}-dark.css")
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
    var apiAudience = ApiAudienceResolver.Resolve(apiScope, clientId);
    var validIssuers = GetValidIssuers(authority);

    // Skip authentication setup in OpenAPI generation mode (no IdP needed for schema generation)
    if (!isOpenApiGenerateMode)
    {
    // Three schemes: the cookie for the Blazor portal, JWT bearer for SSO access tokens, and API keys for
    // non-interactive callers. Both API credentials are bearer tokens in the Authorization header; the cookie scheme
    // forwards to whichever one the token's shape names (see ApiAuthenticationSchemeSelector).
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
        .AddApiKeyAuthentication()
        .AddCookie(options =>
        {
            // Forward a bearer token to the API key or JWT bearer scheme; no bearer token keeps the cookie.
            options.ForwardDefaultSelector = ApiAuthenticationSchemeSelector.SelectScheme;

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
            // An ID token signed by a key JIM has not seen makes it fetch the key set early, at most every 30 seconds
            // rather than IdentityModel's 5 minutes, so the sign-in restarted after one succeeds (#2442).
            options.RefreshInterval = IdentityProviderSigningKeys.RefreshInterval;

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

                // Failures a fresh attempt recovers completely (a replayed callback whose single-use code is
                // already spent, a lost correlation cookie) restart the sign-in instead of surfacing an
                // exception page; the audit record above still captures every occurrence. The restart is
                // bounded, and refused outright where the browser can never keep the sign-in cookies (plain
                // HTTP from another machine), so it cannot become a silent loop. Everything else still throws,
                // keeping genuine provider and configuration errors loud and diagnosable.
                return OidcSignInRecovery.HandleRemoteFailureAsync(ctx);
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
                    await ApiUnauthorisedResponse.WriteAsync(ctx.HttpContext);
                    ctx.HandleResponse(); // Mark response as handled
                }
            };
        })
        // Resolve (and, for the initial admin, just-in-time provision) the JIM identity for
        // bearer-token callers too, so the PowerShell module / REST API can administer a fresh
        // deployment without anyone first signing in to the web portal. The resolved roles and
        // MetaverseObject id are attached to this request's principal. The scheme's settings live in
        // JimJwtBearerOptions so the tests drive the same ones.
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            JimJwtBearerOptions.Configure(options, authority, apiAudience, validIssuers, async ctx =>
            {
                await ResolveAndAttachJimIdentityAsync(ctx.Principal, ctx.HttpContext);
            }));

    // Both handlers reach the identity provider through one back-channel, which also accepts a chain the JIM certificate
    // store vouches for when a certificate there is trusted for sign-in (#2363): an identity provider whose certificate
    // comes from an internal certificate authority (AD FS, an on-premises Keycloak) then works without an image
    // rebuild, and trusting its authority takes effect on the next sign-in.
    builder.Services.AddIdentityProviderTrust();

    // Neither handler accepts a token signed by a key the identity provider has retired (#2442).
    builder.Services.AddIdentityProviderSigningKeyPolicy();

    // setup authorisation policies
    builder.Services.AddJimAuthorisation();

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
    // The pagination depth filter (issue #487) applies the shared retrieval depth ceiling to every action,
    // including the endpoints that bind page/pageSize as bare query parameters rather than a PaginationRequest.
    // Registered globally so a new paginated endpoint is guarded by default rather than by the author remembering.
    // ApiChallengeResultFilter puts the WWW-Authenticate challenge on 401s that actions return themselves (#2088).
    builder.Services.AddControllers(options =>
        {
            options.Filters.Add<PaginationDepthActionFilter>();
            options.Filters.Add<ApiChallengeResultFilter>();
        })
        .AddJsonOptions(options => JIM.Web.ApiJsonConfiguration.Configure(options.JsonSerializerOptions));
    builder.Services.AddEndpointsApiExplorer();

    // The HTTP JSON options are the ones the OpenAPI schema generator reads (MVC controllers use the
    // separate options configured above), so they carry the same policy, or the document would describe
    // types differently from how the API serialises them. ApiJsonConfiguration.ConfigureHttp explains more.
    builder.Services.ConfigureHttpJsonOptions(JIM.Web.ApiJsonConfiguration.ConfigureHttp);

    builder.Services.Configure<RouteOptions>(ro => ro.LowercaseUrls = true);

    // Configure API versioning with URL path segment (e.g., /api/v1/...)
    builder.Services.AddJimApiVersioning();

    // The OAuth endpoints for Scalar's "Try It" flow (they do not affect the documentation's content). Read from the
    // identity provider's own configuration when the document is generated at runtime, through the same back-channel
    // as sign-in; never at start-up, where an identity provider that is down or not yet trusted would stop JIM starting
    // at all (#2363). Placeholders built from the authority stand in when it cannot be read, and always in OpenAPI
    // generation mode, which has no identity provider.
    var authorityBase = (authority ?? "https://idp.example.com/realms/jim").TrimEnd('/');
    var placeholderAuthorizationEndpoint = $"{authorityBase}/protocol/openid-connect/auth";
    var placeholderTokenEndpoint = $"{authorityBase}/protocol/openid-connect/token";

    // Setup OpenAPI with OAuth2 + API Key security schemes for API documentation
    builder.Services.AddOpenApi("v1", options =>
    {
        options.AddDocumentTransformer(async (document, context, cancellationToken) =>
        {
            var (authorizationEndpoint, tokenEndpoint) = isOpenApiGenerateMode
                ? (placeholderAuthorizationEndpoint, placeholderTokenEndpoint)
                : await ReadOAuthEndpointsAsync(context.ApplicationServices, placeholderAuthorizationEndpoint, placeholderTokenEndpoint, cancellationToken);

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

            OpenApiSecurityConfiguration.Apply(document, new Uri(authorizationEndpoint), new Uri(tokenEndpoint), apiScope!);
        });

        // Anonymous endpoints (health probes, version, auth configuration) opt out of the document-wide requirement,
        // and a response that declares its own content type (a certificate download) is published with that type only.
        options.AddOperationTransformer((operation, context, _) =>
        {
            OpenApiSecurityConfiguration.ApplyToOperation(operation, context.Description.ActionDescriptor.EndpointMetadata);
            OpenApiResponseContentTypes.ApplyToOperation(operation, context.Description.ActionDescriptor.EndpointMetadata);
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
    builder.Services.AddScoped<TrustedCertificateChanges>();

    // Configuration change previews (#827): surfaces start a preview through the starter rather than the
    // application layer directly, so the informed-choice prompt for a large data set is applied in one place
    // instead of remembered per surface. Scoped, because showing a dialog belongs to a user's circuit.
    builder.Services.AddScoped<IPreviewDataSetSizePrompt, PreviewDataSetSizePrompt>();
    builder.Services.AddScoped<IConfigurationChangePreviewStarter, ConfigurationChangePreviewStarter>();

    // Real-time UI notifications (issue #307): a background service listens for PostgreSQL NOTIFY events
    // on a dedicated non-pooled connection and fans them out to the in-process relay (consumed by Blazor
    // Server components) and the SignalR hub (for non-Blazor consumers). The listener connection string is
    // resolved lazily, so OpenAPI generation mode (which never starts hosted services) is unaffected.
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<UiNotificationService>();
    builder.Services.AddSingleton<IUiNotificationService>(sp => sp.GetRequiredService<UiNotificationService>());
    builder.Services.AddSingleton<IPasswordChangeNotifications>(sp => sp.GetRequiredService<UiNotificationService>());
    // Waits on a queued password change for the Set Password dialog's result stage and the REST endpoint's `wait` (#1635):
    // woken by the relay above, polling as a safety net, one JimApplication per read.
    builder.Services.AddSingleton<IPasswordChangeOutcomeWaiter, PasswordChangeOutcomeWaiter>();
    builder.Services.AddSingleton<IDatabaseNotificationListener>(_ =>
        new PostgresNotificationListener(JimDbContext.BuildListenerConnectionString()));
    builder.Services.AddHostedService<NotificationListenerService>();

    // One instance serving both roles: the hosted service that drains the queue, and the runner the application
    // layer hands previews to. Registering it twice would give the enqueuing half a consumer that never runs.
    builder.Services.AddSingleton<ConfigurationChangePreviewBackgroundRunner>();
    builder.Services.AddSingleton<IConfigurationChangePreviewBackgroundRunner>(sp => sp.GetRequiredService<ConfigurationChangePreviewBackgroundRunner>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ConfigurationChangePreviewBackgroundRunner>());

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
            // One preferred scheme, not both: Scalar applies every preferred scheme together, and with API keys and
            // SSO tokens now both in the Authorization header it merged them ("Bearer , Bearer jim_ak_..."), which
            // JIM rightly refuses. OAuth2 stays selectable from Try It's authentication drop-down.
            .AddPreferredSecuritySchemes(OpenApiSecurityConfiguration.ApiKeySchemeName)
            .AddAuthorizationCodeFlow(OpenApiSecurityConfiguration.OAuth2SchemeName, flow =>
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

    // Health probes are exempt, so that a probe over the loopback HTTP listener sees JIM's real state rather than
    // a redirect that counts as healthy.
    app.UseHttpsRedirectionExceptHealthProbes(app.Environment);
    app.UseStaticFiles();
    app.UseRouting();

    // Global exception handler for API endpoints
    app.UseWhen(
        context => context.Request.Path.StartsWithSegments("/api"),
        appBuilder => appBuilder.UseGlobalExceptionHandler()
    );

    // The SCIM 2.0 Service Provider endpoints (#124): SCIM-shaped errors for anything unhandled, dark (404) while the
    // feature flag is off, and refused over an unconfirmed transport, all decided before authentication reads a key.
    app.UseWhen(
        context => ScimRoutes.IsScimRequest(context.Request),
        appBuilder => appBuilder
            .UseMiddleware<ScimExceptionMiddleware>()
            .UseMiddleware<ScimFeatureGateMiddleware>()
            .UseMiddleware<ScimSecureTransportMiddleware>()
    );

    // A portal sign-in that cannot reach or trust the identity provider lands on a page explaining why and how to fix
    // it (#2363), rather than the generic error page. Wraps authentication, which is where both failures are thrown.
    app.UseMiddleware<IdentityProviderUnavailableMiddleware>();

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

    // An unknown /scim path is a SCIM 404, never the portal's page (whose default policy would answer a SCIM client
    // with a refusal instead).
    app.MapFallback("/scim/{**path}", context => ScimResponses.WriteErrorAsync(context, StatusCodes.Status404NotFound));

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

    // Non-zero when a background service failed and stopped the host, so the container runtime, or systemd, sees
    // the failure rather than a clean stop.
    return await HostRunner.RunAsync(app);
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

    // Wait for the database server first: until it answers, each readiness check below would throw and end the host.
    // Nothing can cancel this yet (the host's shutdown handling starts with app.Run), so there is no token to pass.
    using (var waitScope = app.Services.CreateScope())
        await waitScope.ServiceProvider.GetRequiredService<JimApplication>()
            .WaitForDatabaseAsync(JimApplication.DefaultDatabaseWaitBudget, null, CancellationToken.None);

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
    if (!infrastructureApiKey.StartsWith(ApiKeyMaterial.Prefix, StringComparison.Ordinal))
    {
        Log.Warning("InitialiseInfrastructureApiKeyAsync: {EnvVar} must start with '{Prefix}'. Key not created.",
            Constants.Config.InfrastructureApiKey, ApiKeyMaterial.Prefix);
        return;
    }

    if (infrastructureApiKey.Length < 32)
    {
        Log.Warning("InitialiseInfrastructureApiKeyAsync: {EnvVar} is too short. Use at least 32 characters. Key not created.",
            Constants.Config.InfrastructureApiKey);
        return;
    }

    var keyHash = ApiKeyMaterial.Hash(infrastructureApiKey);
    var keyPrefix = infrastructureApiKey[..12];
    var outcome = await jim.Security.EnsureInfrastructureApiKeyAsync(keyHash, keyPrefix);
    switch (outcome)
    {
        case InfrastructureApiKeyOutcome.Created:
            Log.Information("InitialiseInfrastructureApiKeyAsync: Created infrastructure API key (prefix: {Prefix}), valid for {Hours} hours.",
                keyPrefix, SecurityServer.InfrastructureApiKeyLifetime.TotalHours);
            break;
        case InfrastructureApiKeyOutcome.Renewed:
            Log.Information("InitialiseInfrastructureApiKeyAsync: Infrastructure API key (prefix: {Prefix}) had expired; renewed for {Hours} hours.",
                keyPrefix, SecurityServer.InfrastructureApiKeyLifetime.TotalHours);
            break;
        case InfrastructureApiKeyOutcome.Unchanged:
            Log.Information("InitialiseInfrastructureApiKeyAsync: Infrastructure API key already exists (prefix: {Prefix}).", keyPrefix);
            break;
        case InfrastructureApiKeyOutcome.Disabled:
            Log.Warning("InitialiseInfrastructureApiKeyAsync: Infrastructure API key (prefix: {Prefix}) is disabled; it is left disabled and not renewed.", keyPrefix);
            break;
        case InfrastructureApiKeyOutcome.AdministratorRoleMissing:
            Log.Error("InitialiseInfrastructureApiKeyAsync: Administrator role not found. Cannot create infrastructure key.");
            break;
    }
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
        DisplayName = principal.FindFirstValue(SsoIdentity.DisplayNameClaimType),
        FirstName = principal.FindFirstValue(SsoIdentity.FirstNameClaimType),
        LastName = principal.FindFirstValue(SsoIdentity.LastNameClaimType),
        UserPrincipalName = principal.FindFirstValue(SsoIdentity.UserPrincipalNameClaimType)
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
    var clientIp = httpContext.Connection.RemoteIpAddress.ToNormalisedString();
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
/// Records an aggregated interactive sign-in failure security audit event, off the request path, through the shared
/// <see cref="IFailedAuthenticationRecorder"/>. Never throws into the caller.
/// </summary>
static void RecordFailedSignInEvent(HttpContext httpContext, string reason)
{
    var recorder = httpContext.RequestServices.GetService<IFailedAuthenticationRecorder>();
    if (recorder == null)
    {
        Log.Warning("RecordFailedSignInEvent: Could not resolve IFailedAuthenticationRecorder; failed sign-in event not recorded.");
        return;
    }

    var clientIp = httpContext.Connection.RemoteIpAddress.ToNormalisedString();
    _ = recorder.RecordAsync(FailedAuthenticationEventNames.InteractiveSignIn, reason, apiKeyPrefix: null, clientIp);
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
/// Reads the authorisation and token endpoints for the runtime OpenAPI document from the identity provider's
/// configuration, as the OpenID Connect handler holds it (so through the sign-in back-channel and its trust), falling
/// back to placeholders when the identity provider cannot be reached or is not trusted yet. Only Scalar's "Try It"
/// sign-in uses them, so a fallback never blocks the document.
/// </summary>
static async Task<(string AuthorizationEndpoint, string TokenEndpoint)> ReadOAuthEndpointsAsync(
    IServiceProvider services, string placeholderAuthorizationEndpoint, string placeholderTokenEndpoint, CancellationToken cancellationToken)
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OpenIdConnectOptions>>()
        .Get(OpenIdConnectDefaults.AuthenticationScheme);

    try
    {
        var configuration = options.ConfigurationManager == null
            ? null
            : await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);

        if (!string.IsNullOrEmpty(configuration?.AuthorizationEndpoint) && !string.IsNullOrEmpty(configuration.TokenEndpoint))
            return (configuration.AuthorizationEndpoint, configuration.TokenEndpoint);
    }
    catch (InvalidOperationException ex)
    {
        Log.Warning(ex, "Could not read the identity provider's configuration for the API reference; its sign-in uses placeholder endpoints");
    }

    return (placeholderAuthorizationEndpoint, placeholderTokenEndpoint);
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
