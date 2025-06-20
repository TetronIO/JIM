using JIM.Application;
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
    // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddTransient<JimApplication>(x => new JimApplication(new PostgresDataRepository(new JimDbContext())));
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