using JIM.Models.Logic;
using JIM.Models.Logic.Dtos;
using JIM.Models.Staging;
using JIM.Models.Staging.Dtos;
using JIM.Models.Transactional;

namespace JIM.Data.Repositories
{
    public interface IConnectedSystemRepository
    {
        public Task CreateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);
        public Task CreateConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile);
        public Task CreateConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition);
        public Task CreateConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer);


        public Task DeleteConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);
        public Task DeleteConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile);
        public Task DeleteConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition);
        public Task DeleteConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer);


        public Task UpdateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);


        public Task<ConnectedSystem?> GetConnectedSystemAsync(int id);
        public Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, int id);
        public Task<ConnectorDefinition?> GetConnectorDefinitionAsync(int id);
        public Task<ConnectorDefinition?> GetConnectorDefinitionAsync(string name);
        public Task<IList<ConnectedSystem>> GetConnectedSystemsAsync();
        public Task<IList<ConnectedSystemAttribute>?> GetAttributesAsync(int id);
        public Task<IList<ConnectedSystemHeader>> GetConnectedSystemHeadersAsync();
        public Task<IList<ConnectedSystemObjectType>?> GetObjectTypesAsync(int id);
        public Task<IList<SyncRule>> GetSyncRulesAsync();
        public Task<IList<SyncRuleHeader>> GetSyncRuleHeadersAsync();
        public Task<IList<SyncRun>?> GetSynchronisationRunsAsync(int id);
        public Task<int> GetConnectedSystemObjectCountAsync();
        public Task<int> GetConnectedSystemObjectOfTypeCountAsync(int connectedSystemObjectTypeId);
        public Task<IList<ConnectorDefinitionHeader>> GetConnectorDefinitionHeadersAsync();
        public Task<SyncRule?> GetSyncRuleAsync(int id);
        public Task<IList<ConnectedSystemPartition>> GetConnectedSystemPartitionsAsync(ConnectedSystem connectedSystem);
        public Task<IList<ConnectedSystemContainer>> GetConnectedSystemContainersAsync(ConnectedSystem connectedSystem);
    }
}
