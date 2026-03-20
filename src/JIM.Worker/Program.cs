using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Connectors;
using JIM.Data;
using JIM.Models.Core;
using JIM.PostgresData;
using JIM.Worker;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        // Database connection — reads from environment variables, matching JIM.Web's pattern
        var dbHostName = Environment.GetEnvironmentVariable(Constants.Config.DatabaseHostname);
        var dbName = Environment.GetEnvironmentVariable(Constants.Config.DatabaseName);
        var dbUsername = Environment.GetEnvironmentVariable(Constants.Config.DatabaseUsername);
        var dbPassword = Environment.GetEnvironmentVariable(Constants.Config.DatabasePassword);
        var dbLogSensitiveInfo = Environment.GetEnvironmentVariable(Constants.Config.DatabaseLogSensitiveInformation);

        var connectionString = $"Host={dbHostName};Database={dbName};Username={dbUsername};Password={dbPassword}" +
                               ";Minimum Pool Size=5;Maximum Pool Size=30;Connection Idle Lifetime=300;Connection Pruning Interval=30";
        _ = bool.TryParse(dbLogSensitiveInfo, out var logSensitiveInfo);
        if (logSensitiveInfo)
            connectionString += ";Include Error Detail=True";

        // DbContextFactory — each CreateDbContext() call gets a fresh connection (no concurrency issues)
        services.AddDbContextFactory<JimDbContext>(options =>
            options.UseNpgsql(connectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning,
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning)));

        // Shared memory cache — singleton, used for CSO external ID lookup indexing
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));

        // Data Protection + Credential Protection — matches JIM.Web's encryption configuration
        var dataProtectionKeysPath = Environment.GetEnvironmentVariable("JIM_ENCRYPTION_KEY_PATH") ?? "/data/keys";
        services.AddDataProtection()
            .SetApplicationName("JIM")
            .UseCryptographicAlgorithms(new AuthenticatedEncryptorConfiguration
            {
                EncryptionAlgorithm = EncryptionAlgorithm.AES_256_GCM,
                ValidationAlgorithm = ValidationAlgorithm.HMACSHA256
            })
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
            .SetDefaultKeyLifetime(TimeSpan.FromDays(3650));
        services.AddSingleton<ICredentialProtectionService, CredentialProtectionService>();

        // Repository — transient, one per JimApplication instance
        services.AddTransient<IRepository>(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<JimDbContext>>();
            var context = factory.CreateDbContext();
            return new PostgresDataRepository(context);
        });

        // JimApplication — transient, each task gets its own instance with fresh DbContext
        services.AddTransient<JimApplication>(sp =>
        {
            var jim = new JimApplication(
                sp.GetRequiredService<IRepository>(),
                sp.GetRequiredService<IMemoryCache>());
            jim.CredentialProtection = sp.GetService<ICredentialProtectionService>();
            return jim;
        });

        // Factories
        services.AddSingleton<IJimApplicationFactory, JimApplicationFactory>();
        services.AddSingleton<IConnectorFactory, ConnectorFactory>();

        // Worker hosted service
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();
