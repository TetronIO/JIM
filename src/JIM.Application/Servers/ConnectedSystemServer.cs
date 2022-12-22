using JIM.Models.Logic;
using JIM.Models.Logic.Dtos;
using JIM.Models.Staging;
using JIM.Models.Staging.Dtos;
using JIM.Models.Transactional;

namespace JIM.Application.Servers
{
    public class ConnectedSystemServer
    {
        private JimApplication Application { get; }

        internal ConnectedSystemServer(JimApplication application)
        {
            Application = application;
        }

        public async Task<IList<ConnectedSystem>> GetConnectedSystemsAsync()
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemsAsync();
        }

        public async Task<IList<ConnectedSystemHeader>> GetConnectedSystemHeadersAsync()
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemHeadersAsync();
        }

        public async Task<ConnectedSystem?> GetConnectedSystemAsync(int id)
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(id);
        }

        public async Task<IList<SyncRun>?> GetSynchronisationRunsAsync(int id)
        {
            return await Application.Repository.ConnectedSystems.GetSynchronisationRunsAsync(id);
        }

        public async Task<IList<ConnectedSystemAttribute>?> GetAttributesAsync(int id)
        {
            return await Application.Repository.ConnectedSystems.GetAttributesAsync(id);
        }

        public async Task<IList<ConnectedSystemObjectType>?> GetObjectTypesAsync(int id)
        {
            return await Application.Repository.ConnectedSystems.GetObjectTypesAsync(id);
        }

        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, int id)
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, id);
        }

        public async Task<int> GetConnectedSystemObjectCountAsync()
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync();
        }

        public async Task<int> GetConnectedSystemObjectOfTypeCountAsync(ConnectedSystemObjectType connectedSystemObjectType)
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectOfTypeCountAsync(connectedSystemObjectType.Id);
        }

        public async Task<IList<SyncRule>> GetSyncRulesAsync()
        {
            return await Application.Repository.ConnectedSystems.GetSyncRulesAsync();
        }

        public async Task<IList<SyncRuleHeader>> GetSyncRuleHeadersAsync()
        {
            return await Application.Repository.ConnectedSystems.GetSyncRuleHeadersAsync();
        }

        public async Task<SyncRule?> GetSyncRuleAsync(int id)
        {
            return await Application.Repository.ConnectedSystems.GetSyncRuleAsync(id);
        }

        public async Task<List<ConnectorDefinitionHeader>> GetConnectorDefinitionHeadersAsync()
        {
            return await Application.Repository.ConnectedSystems.GetConnectorDefinitionHeadersAsync();
        }

        public async Task<ConnectorDefinition?> GetConnectorDefinitionAsync(int id)
        {
            return await Application.Repository.ConnectedSystems.GetConnectorDefinitionAsync(id);
        }
    }
}