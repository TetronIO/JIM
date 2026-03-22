using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.PostgresData;
using JIM.Scheduler;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.EntityFrameworkCore;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        // Database connection — uses shared connection string builder
        var connectionString = JimDbContext.BuildConnectionString();

        // DbContextFactory — each CreateDbContext() call gets a fresh connection
        services.AddDbContextFactory<JimDbContext>(options =>
            options.UseNpgsql(connectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning,
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning)));

        // Data Protection + Credential Protection
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

        // JimApplication — transient (Scheduler does not need IMemoryCache)
        services.AddTransient<JimApplication>(sp =>
        {
            var repo = sp.GetRequiredService<IRepository>();
            var syncRepo = new JIM.PostgresData.Repositories.SyncRepository((JIM.PostgresData.PostgresDataRepository)repo);
            var jim = new JimApplication(repo, syncRepository: syncRepo);
            jim.CredentialProtection = sp.GetService<ICredentialProtectionService>();
            return jim;
        });

        // Factory
        services.AddSingleton<IJimApplicationFactory, JimApplicationFactory>();

        // Scheduler hosted service
        services.AddHostedService<Scheduler>();
    })
    .Build();

await host.RunAsync();
