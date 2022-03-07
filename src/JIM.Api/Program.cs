using JIM.Application;
using JIM.Data;
using JIM.PostgresData;
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
// SSO_NAMEID_ATTRIBUTE
// SSO_INITIAL_ADMIN_NAMEID

// Optional environment variables:
// -------------------------------
// ENABLE_REQUEST_LOGGING

// initial logging setup for when the application has not yet been created (bootstrapping)...
InitialiseLogging(new LoggerConfiguration(), true);

try
{
    Log.Information("Starting JIM.Api");
    await InitialiseJimApplicationAsync();

    var builder = WebApplication.CreateBuilder(args);

    // Add services to the container.
    builder.Services.AddControllers();
    // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddScoped<IRepository, PostgresDataRepository>();
    builder.Services.AddScoped<JimApplication>();
    builder.Services.Configure<RouteOptions>(ro => ro.LowercaseUrls = true);    

    // now setup logging with the web framework
    builder.Host.UseSerilog((context, services, configuration) => InitialiseLogging(configuration, false));

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // todo: don't think this will work with docker
    app.UseHttpsRedirection();
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
/// Sets up the JIM application, pass in the right database repository (could pass in something else for testing, i.e. In Memory db).
/// then ensure SSO and Initial admin are setup.
/// </summary>
static async Task InitialiseJimApplicationAsync()
{
    var ssoNameIdAttribute = Environment.GetEnvironmentVariable("SSO_NAMEID_ATTRIBUTE");
    if (string.IsNullOrEmpty(ssoNameIdAttribute))
        throw new Exception("SSO_NAMEID_ATTRIBUTE environment variable missing");
    var ssoInitialAdminNameId = Environment.GetEnvironmentVariable("SSO_INITIAL_ADMIN_NAMEID");
    if (string.IsNullOrEmpty(ssoInitialAdminNameId))
        throw new Exception("SSO_INITIAL_ADMIN_NAMEID environment variable missing");

    using var jimApplication = new JimApplication(new PostgresDataRepository());
    while (!jimApplication.IsApplicationReady())
    {
        Log.Information("Application is not ready yet. Sleeping...");
        Thread.Sleep(1000);
    }

    await jimApplication.InitialiseSSOAsync(ssoNameIdAttribute, ssoInitialAdminNameId);
}