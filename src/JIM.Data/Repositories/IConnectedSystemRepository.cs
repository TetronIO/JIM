using JIM.Models.Logic;
using JIM.Models.Logic.Dtos;
using JIM.Models.Staging;
using JIM.Models.Staging.Dtos;
using JIM.Models.Transactional;

namespace JIM.Data.Repositories
{
    public interface IConnectedSystemRepository
    {
        public Task CreateConnectedSystemAsync(ConnectedSystem connectedSystem);       
        public Task CreateConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer);
        public Task CreateConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition);
        public Task CreateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile runProfile);
        public Task CreateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);
        public Task CreateConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile);


        public Task DeleteConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer);
        public Task DeleteConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition);
        public Task DeleteConnectedSystemRunProfileAsync(ConnectedSystemRunProfile runProfile);
        public Task DeleteConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);
        public Task DeleteConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile);


        public Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem);
        public Task UpdateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile);
        public Task UpdateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);


        public Task<ConnectedSystem?> GetConnectedSystemAsync(int id);
        public Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, int id);
        public Task<ConnectorDefinition?> GetConnectorDefinitionAsync(int id);
        public Task<ConnectorDefinition?> GetConnectorDefinitionAsync(string name);
        public Task<IList<ConnectedSystem>> GetConnectedSystemsAsync();
        public Task<IList<ConnectedSystemContainer>> GetConnectedSystemContainersAsync(ConnectedSystem connectedSystem);
        public Task<IList<ConnectedSystemHeader>> GetConnectedSystemHeadersAsync();
        public Task<IList<ConnectedSystemObjectType>?> GetObjectTypesAsync(int id);
        public Task<IList<ConnectedSystemPartition>> GetConnectedSystemPartitionsAsync(ConnectedSystem connectedSystem);
        public Task<IList<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(ConnectedSystem connectedSystem);
        public Task<IList<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(int connectedSystemId);
        public Task<IList<ConnectorDefinitionHeader>> GetConnectorDefinitionHeadersAsync();
        public Task<IList<SyncRule>> GetSyncRulesAsync();
        public Task<IList<SyncRuleHeader>> GetSyncRuleHeadersAsync();
        public Task<IList<SyncRun>?> GetSynchronisationRunsAsync(int id);
        public Task<int> GetConnectedSystemObjectCountAsync();
        public Task<int> GetConnectedSystemObjectOfTypeCountAsync(int connectedSystemObjectTypeId);
        public Task<SyncRule?> GetSyncRuleAsync(int id);
    }
}
