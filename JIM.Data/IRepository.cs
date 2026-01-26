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
    public ISearchRepository Search { get; }
    public ISecurityRepository Security { get; }
    public ISeedingRepository Seeding { get; }
    public IServiceSettingsRepository ServiceSettings { get; }
    public ITaskingRepository Tasking { get; }
    public ITrustedCertificateRepository TrustedCertificates { get; }

    public Task InitialiseDatabaseAsync();
    public Task InitialisationCompleteAsync();
}