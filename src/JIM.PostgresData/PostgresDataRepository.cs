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
    public IExampleDataRepository ExampleData { get; }
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
        ExampleData = new ExampleDataRepository(this);
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

    public async Task<bool> HasPendingMigrationsAsync()
    {
        return (await Database.Database.GetPendingMigrationsAsync()).Any();
    }

    private async Task MigrateDatabaseAsync()
    {
        if (await HasPendingMigrationsAsync())
            await Database.Database.MigrateAsync();
    }

    public void ClearChangeTracker()
    {
        // NOTE: Do NOT call ChangeTracker.Entries() before Clear() — Entries() triggers
        // DetectChanges() which walks navigation properties and can throw identity conflicts
        // when the tracker contains multiple in-memory instances of the same entity (e.g.
        // MetaverseAttribute loaded via different Include paths after a prior ClearChangeTracker).
        // ChangeTracker.Clear() does NOT trigger DetectChanges.
        Database.ChangeTracker.Clear();
        Log.Debug("ClearChangeTracker: Cleared change tracker");
    }

    public void SetAutoDetectChangesEnabled(bool enabled)
    {
        Database.ChangeTracker.AutoDetectChangesEnabled = enabled;
        Log.Debug("SetAutoDetectChangesEnabled: {Enabled}", enabled);
    }

    /// <summary>
    /// Updates an entity safely, avoiding EF Core graph traversal in all cases.
    /// Uses Entry().State = Modified which only marks the single entity without
    /// traversing navigation properties. This prevents EF from discovering and
    /// prematurely inserting related entities (e.g., RPEIs in Activity.RunProfileExecutionItems)
    /// during SaveChangesAsync.
    ///
    /// In contrast, Database.Update() traverses the full object graph and marks all
    /// reachable entities, which can cause identity conflicts with shared entities
    /// (MetaverseAttribute, MetaverseObjectType) and premature insertion of entities
    /// that should be persisted separately (RPEIs via raw SQL bulk insert).
    ///
    /// Always marks the entity as Modified (unless it's Added, which already implies persistence).
    /// This is necessary because callers invoke this method to persist changes, and when
    /// AutoDetectChangesEnabled is false (e.g., during page flush sequences), EF Core won't
    /// auto-detect property changes on Unchanged entities — causing SaveChangesAsync to skip them.
    /// </summary>
    internal void UpdateDetachedSafe<T>(T entity) where T : class
    {
        try
        {
            var entry = Database.Entry(entity);
            // Always mark as Modified unless the entity is Added (which already implies persistence).
            // Previously this only marked Detached entities, leaving Unchanged entities as-is under
            // the assumption that auto-detect would catch property changes. That assumption fails
            // when AutoDetectChangesEnabled is false (sync page flush), causing SaveChangesAsync
            // to generate no SQL for the entity — e.g., EvaluateMvoDeletionAsync's CSO FK null
            // was silently lost, leaving a dangling FK that blocked MVO deletion.
            if (entry.State != EntityState.Added)
                entry.State = EntityState.Modified;
        }
        catch (InvalidOperationException)
        {
            // Identity conflict: another instance with the same key is already tracked
            // (e.g. after ClearChangeTracker, a query re-loaded the entity).
            // Find the tracked instance and copy current values into it.
            // We cannot call Database.Entry(entity) here as it would throw the same exception,
            // so we extract key values via the EF model metadata and CLR property accessors.
            var entityType = Database.Model.FindEntityType(typeof(T));
            var keyProperties = entityType?.FindPrimaryKey()?.Properties;

            if (keyProperties != null)
            {
                var trackedEntry = Database.ChangeTracker.Entries<T>()
                    .FirstOrDefault(e => keyProperties.All(p =>
                        Equals(e.Property(p.Name).CurrentValue,
                               p.PropertyInfo?.GetValue(entity) ?? p.FieldInfo?.GetValue(entity))));

                if (trackedEntry != null)
                {
                    trackedEntry.CurrentValues.SetValues(entity);
                    trackedEntry.State = EntityState.Modified;
                }
            }
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
