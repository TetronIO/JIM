namespace JIM.Data
{
    public interface IRepository: IDisposable
    {
        public IConnectedSystemRepository ConnectedSystems { get; }
        public IMetaverseRepository Metaverse { get; }
        public IServiceSettingsRepository ServiceSettings { get; }
        public ISecurityRepository Security { get; }

        public Task InitialiseDatabaseAsync();
    }
}
