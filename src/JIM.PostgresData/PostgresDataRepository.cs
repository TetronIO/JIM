using JIM.Data.Repositories;
using JIM.Data;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Diagnostics;
namespace JIM.PostgresData;

public class PostgresDataRepository : IRepository
{
    public IActivityRepository Activity { get; }
    public IApiKeyRepository ApiKeys { get; }
    public IChangeHistoryRepository ChangeHistory { get; }
    public IConnectedSystemRepository ConnectedSystems { get; }
    public IDataGenerationRepository DataGeneration { get; }
    public IMetaverseRepository Metaverse { get; }
    public ISchedulingRepository Scheduling { get; }
    public ISearchRepository Search { get; }
    public ISecurityRepository Security { get; }
    public ISeedingRepository Seeding { get; }
    public IServiceSettingsRepository ServiceSettings { get; }
    public ITaskingRepository Tasking { get; }
    public ITrustedCertificateRepository TrustedCertificates { get; }

    internal JimDbContext Database { get; }

    public PostgresDataRepository(JimDbContext jimDbContext)
    {
        // DATETIME HANDLING DESIGN DECISION
        // =================================
        // JIM uses .NET DateTime (not DateTimeOffset) throughout the models with a strict UTC convention.
        // PostgreSQL stores these as "timestamp with time zone" (timestamptz) columns.
        //
        // Why DateTime instead of DateTimeOffset?
        // - Database portability: DateTimeOffset offset preservation varies by database:
        //   * SQL Server: Preserves offset in datetimeoffset columns
        //   * PostgreSQL/CockroachDB: Converts to UTC, offset becomes 0 on read
        //   * MySQL/MariaDB: No native offset support, stores UTC only
        // - Using DateTime + UTC convention is the most portable approach across all databases.
        //
        // Why this legacy switch?
        // - Npgsql 7.0+ changed default behaviour: DateTime maps to "timestamp without time zone"
        // - This switch reverts to Npgsql 6.x behaviour where DateTime maps to "timestamp with time zone"
        // - Without it, EF Core throws errors when assigning DateTime.UtcNow to timestamptz columns
        //
        // Runtime quirk to be aware of:
        // - When Npgsql reads timestamptz values, it returns them as DateTimeOffset at runtime
        // - Code that processes DateTime values from the database (e.g., expression evaluation)
        //   must handle both DateTime and DateTimeOffset types
        // - See DynamicExpressoEvaluator.ToFileTime() for an example of handling both types
        //
        // Convention: Always use DateTime.UtcNow, never DateTime.Now
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        Activity = new ActivityRepository(this);
        ApiKeys = new ApiKeyRepository(this);
        ChangeHistory = new ChangeHistoryRepository(jimDbContext);
        ConnectedSystems = new ConnectedSystemRepository(this);
        DataGeneration = new DataGenerationRepository(this);
        Database = jimDbContext; // the db context is passed in, so we can unit test jim and the data repository by passing in either a mock or the actual db context.
        Metaverse = new MetaverseRepository(this);
        Scheduling = new SchedulingRepository(this);
        Search = new SearchRepository(this);
        Security = new SecurityRepository(this);
        Seeding = new SeedingRepository(this);
        ServiceSettings = new ServiceSettingsRepository(this);
        Tasking = new TaskingRepository(this);
        TrustedCertificates = new TrustedCertificateRepository(this);
    }

    public async Task InitialiseDatabaseAsync()
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        await MigrateDatabaseAsync();
        stopwatch.Stop();
        Log.Verbose($"InitialiseDatabaseAsync: Completed in: {stopwatch.Elapsed}");
    }

    public async Task InitialisationCompleteAsync()
    {
        // when the database is created, it's done so in maintenance mode
        // now we're all done, take it out of maintenance mode to open the app up to requests
        var serviceSettings = await ServiceSettings.GetServiceSettingsAsync() ?? 
                              throw new Exception("ServiceSettings is null. Something has gone wrong with seeding.");

        serviceSettings.IsServiceInMaintenanceMode = false;
        await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
        Log.Verbose($"TakeServiceOutOfMaintenanceModeAsync: Done");
    }

    private async Task MigrateDatabaseAsync()
    {
        if ((await Database.Database.GetPendingMigrationsAsync()).Any())
            await Database.Database.MigrateAsync();
    }

    public void ClearChangeTracker()
    {
        try
        {
            var trackedCount = Database.ChangeTracker.Entries().Count();
            Database.ChangeTracker.Clear();
            Log.Debug("ClearChangeTracker: Cleared {Count} tracked entities", trackedCount);
        }
        catch (NullReferenceException)
        {
            // ChangeTracker is unavailable in unit test environments with mocked DbContext
        }
    }

    #region IDisposable

    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Dispose the DbContext to release database connections
            Database.Dispose();
        }

        _disposed = true;
    }

    #endregion
}
