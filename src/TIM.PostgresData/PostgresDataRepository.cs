using TIM.Data;

namespace TIM.PostgresData
{
    public class PostgresDataRepository : IRepository
    {
        public IConnectedSystemRepository ConnectedSystems { get; }
        public IMetaverseRepository Metaverse { get ;}
        public IServiceSettingsRepository ServiceSettings { get; }
        public ISecurityRepository Security { get; }

        public PostgresDataRepository()
        {
            ConnectedSystems = new ConnectedSystemRepository(this);
            Metaverse = new MetaverseRepository(this);
            ServiceSettings = new ServiceSettingsRepository(this);
            Security = new SecurityRepository(this);
        }
    }
}