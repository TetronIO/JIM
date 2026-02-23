using JIM.Data.Repositories;
namespace JIM.Data;

public interface IRepository : IDisposable
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

    public Task InitialiseDatabaseAsync();
    public Task InitialisationCompleteAsync();

    /// <summary>
    /// Clears all tracked entities from the change tracker.
    /// Use after long-running operations where accumulated tracked entities cause performance degradation.
    /// All tracked entities will become detached — any subsequent updates must re-attach them.
    /// </summary>
    public void ClearChangeTracker();

    /// <summary>
    /// Controls whether SaveChangesAsync automatically calls DetectChanges before saving.
    /// When disabled, the change tracker will NOT scan navigation properties to discover new/modified
    /// entities — only explicitly tracked entities (via Entry().State or Add/Update) will be saved.
    ///
    /// Use this when manually managing entity states with Entry().State to prevent DetectChanges from
    /// traversing navigation properties and discovering conflicting entity instances (e.g. shared
    /// MetaverseAttribute/MetaverseObjectType entities after ClearChangeTracker).
    ///
    /// IMPORTANT: Always restore to true after use, ideally in a try/finally block.
    /// </summary>
    public void SetAutoDetectChangesEnabled(bool enabled);
}