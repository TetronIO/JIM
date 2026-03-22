using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Connectors;
using JIM.Data;
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
        // Database connection — uses shared connection string builder with bulk operation timeout
        var connectionString = JimDbContext.BuildConnectionString(
            commandTimeoutSeconds: PostgresDataRepository.BulkOperationCommandTimeoutSeconds);

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
            var repo = sp.GetRequiredService<IRepository>();
            var syncRepo = new JIM.PostgresData.Repositories.SyncRepository((JIM.PostgresData.PostgresDataRepository)repo);
            var jim = new JimApplication(repo, sp.GetRequiredService<IMemoryCache>(), syncRepo);
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
