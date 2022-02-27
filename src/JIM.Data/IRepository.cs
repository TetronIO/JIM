namespace JIM.Data
{
    public interface IRepository
    {
        public IConnectedSystemRepository ConnectedSystems { get; }
        public IMetaverseRepository Metaverse { get; }
        public IServiceSettingsRepository ServiceSettings { get; }
        public ISecurityRepository Security { get; }

        public Task SeedDatabaseAsync();
    }
}
