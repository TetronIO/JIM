using JIM.Data.Repositories;

namespace JIM.Data
{
    public interface IRepository: IDisposable
    {
        public IConnectedSystemRepository ConnectedSystems { get; }
        public IMetaverseRepository Metaverse { get; }
        public IServiceSettingsRepository ServiceSettings { get; }
        public ISecurityRepository Security { get; }
        public IDataGenerationRepository DataGeneration { get; }
        public ISeedingRepository Seeding { get; }
        public ITaskingRepository Tasking { get; }

        public Task InitialiseDatabaseAsync();
        public Task InitialisationCompleteAsync();
    }
}
