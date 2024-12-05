using JIM.Application;
using JIM.Models.Core;
using JIM.PostgresData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using MudBlazor;
using MudBlazor.Services;
using Serilog;
using Serilog.Events;
using System.Security.Claims;

// Required environment variables:
// -------------------------------
// LOGGING_LEVEL
// LOGGING_PATH
// DB_HOSTNAME - validated by the data layer
// DB_NAME - validated by the data layer
// DB_USERNAME - validated by the data layer
// DB_PASSWORD - validated by the data layer
// SSO_AUTHORITY
// SSO_CLIENT_ID
// SSO_SECRET
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
    Log.Information("Starting JIM.Web");
    await InitialiseJimApplicationAsync();

    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddTransient<JimApplication>(_ => new JimApplication(new PostgresDataRepository(new JimDbContext())));

    // setup OpenID Connect (OIDC) authentication
    var authority = Environment.GetEnvironmentVariable("SSO_AUTHORITY");
    var clientId = Environment.GetEnvironmentVariable("SSO_CLIENT_ID");
    var clientSecret = Environment.GetEnvironmentVariable("SSO_SECRET");

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
        .AddCookie()
        .AddOpenIdConnect(options =>
        {
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.UseTokenLifetime = true; // respect the IdP token lifetime and use it as our session lifetime
            options.Authority = authority;
            options.ClientId = clientId;
            options.ClientSecret = clientSecret;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.Scope.Add("profile");

            // intercept the user login when a token is received and validate we can map them to a JIM user
            options.Events.OnTicketReceived = async ctx =>
            {
                await AuthoriseAndUpdateUserAsync(ctx);
            };
        });

    // setup authorisation policies
    builder.Services.AddAuthorization(options =>
    {
        // require all users to be authenticated with our IdP
        // eventually this will probably have to change, so we can make some pages anonymous for things like load-balance health monitors
        options.FallbackPolicy = options.DefaultPolicy;
    });

    // Add services to the container.
    builder.Services.AddRazorPages();
    builder.Services.AddServerSideBlazor();

    // setup logging properly now (it's been bootstrapped initially)
    builder.Services.AddSerilog(configuration => InitialiseLogging(configuration, false));
    
    // setup MudBlazor
    builder.Services.AddMudServices(config => {
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

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRouting();
    app.MapBlazorHub();
    app.MapFallbackToPage("/_Host");

    // only enable request logging if configured to do some from env vars, as it adds a LOT to the logs
    var enableRequestLogging = Environment.GetEnvironmentVariable("ENABLE_REQUEST_LOGGING");
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
    var loggingMinimumLevel = Environment.GetEnvironmentVariable("LOGGING_LEVEL");
    if (loggingMinimumLevel == null)
        throw new ApplicationException("LOGGING_LEVEL environment variable not found. Cannot continue");
    
    var loggingPath = Environment.GetEnvironmentVariable("LOGGING_PATH");
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
    loggerConfiguration.WriteTo.File(Path.Combine(loggingPath, "jim.web..log"), rollingInterval: RollingInterval.Day);
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
    var ssoAuthority = Environment.GetEnvironmentVariable("SSO_AUTHORITY");
    if (string.IsNullOrEmpty(ssoAuthority))
        throw new Exception("SSO_AUTHORITY environment variable missing");

    var ssoClientId = Environment.GetEnvironmentVariable("SSO_CLIENT_ID");
    if (string.IsNullOrEmpty(ssoClientId))
        throw new Exception("SSO_CLIENT_ID environment variable missing");

    var ssoSecret = Environment.GetEnvironmentVariable("SSO_SECRET");
    if (string.IsNullOrEmpty(ssoSecret))
        throw new Exception("SSO_SECRET environment variable missing");

    // collect claim mapping config variables
    var uniqueIdentifierClaimType = Environment.GetEnvironmentVariable("SSO_UNIQUE_IDENTIFIER_CLAIM_TYPE");
    if (string.IsNullOrEmpty(uniqueIdentifierClaimType))
        throw new Exception("SSO_UNIQUE_IDENTIFIER_CLAIM_TYPE environment variable missing");

    var uniqueIdentifierMetaverseAttributeName = Environment.GetEnvironmentVariable("SSO_UNIQUE_IDENTIFIER_METAVERSE_ATTRIBUTE_NAME");
    if (string.IsNullOrEmpty(uniqueIdentifierMetaverseAttributeName))
        throw new Exception("SSO_UNIQUE_IDENTIFIER_METAVERSE_ATTRIBUTE_NAME environment variable missing");

    var initialAdminClaimValue = Environment.GetEnvironmentVariable("SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE");
    if (string.IsNullOrEmpty(initialAdminClaimValue))
        throw new Exception("SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE environment variable missing");

    while (true)
    {
        var jimApplication = new JimApplication(new PostgresDataRepository(new JimDbContext()));
        if (await jimApplication.IsApplicationReadyAsync())
        {
            await jimApplication.InitialiseSsoAsync(ssoAuthority, ssoClientId, ssoSecret, uniqueIdentifierClaimType, uniqueIdentifierMetaverseAttributeName, initialAdminClaimValue);
            break;
        }

        Log.Information("JIM.Application is not ready yet. Sleeping...");
        Thread.Sleep(1000);
    }
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
    var userType = await jim.Metaverse.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.Users, false) ?? 
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
        userRoleClaims.Add(new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.Users));

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

    // do it again. some IDPs use "name" instead of the xmlsoap version below for conveying display name
    if (!user.HasAttributeValue(Constants.BuiltInAttributes.DisplayName))
    {
        var nameClaim = claimsPrincipal.Claims.FirstOrDefault(q => q.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name");
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

    if (!user.HasAttributeValue(Constants.BuiltInAttributes.FirstName))
    {
        var givenNameClaim = claimsPrincipal.Claims.FirstOrDefault(q => q.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname");
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

    if (!user.HasAttributeValue(Constants.BuiltInAttributes.LastName))
    {
        var surnameClaim = claimsPrincipal.Claims.FirstOrDefault(q => q.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname");
        if (surnameClaim != null)
        {
            var lastNameAttribute = await jim.Metaverse.GetMetaverseAttributeAsync(Constants.BuiltInAttributes.LastName);
            if (lastNameAttribute != null)
            {
                user.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = lastNameAttribute,
                    StringValue = surnameClaim.Value
                });

                updateRequired = true;
                Log.Verbose("UpdateUserAttributesFromClaimsAsync: Added value from claim: " + surnameClaim.Type);
            }
        }
    }

    if (!user.HasAttributeValue(Constants.BuiltInAttributes.UserPrincipalName))
    {
        var upnClaim = claimsPrincipal.Claims.FirstOrDefault(q => q.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn");
        if (upnClaim != null)
        {
            var upnAttribute = await jim.Metaverse.GetMetaverseAttributeAsync(Constants.BuiltInAttributes.UserPrincipalName);
            if (upnAttribute != null)
            {
                user.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = upnAttribute,
                    StringValue = upnClaim.Value
                });

                updateRequired = true;
                Log.Verbose("UpdateUserAttributesFromClaimsAsync: Added value from claim: " + upnClaim.Type);
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