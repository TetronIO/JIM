using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;

namespace JIM.Data.Repositories
{
    public interface IConnectedSystemRepository
    {
        public Task<ConnectedSystem?> GetConnectedSystemAsync(int id);
        public Task<ConnectedSystemHeader?> GetConnectedSystemHeaderAsync(int id);
        public Task<PagedResultSet<ConnectedSystemObjectHeader>> GetConnectedSystemObjectHeadersAsync(int connectedSystemId, int page, int pageSize, int maxResults, QuerySortBy querySortBy = QuerySortBy.DateCreated, QueryRange queryRange = QueryRange.Forever);
        public Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id);
        public Task<ConnectedSystemObject?> GetConnectedSystemObjectByUniqueIdAsync(int connectedSystemId, int connectedSystemAttributeId, Guid attributeValue);
        public Task<ConnectedSystemObject?> GetConnectedSystemObjectByUniqueIdAsync(int connectedSystemId, int connectedSystemAttributeId, int attributeValue);
        public Task<ConnectedSystemObject?> GetConnectedSystemObjectByUniqueIdAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue);
        public Task<ConnectedSystemRunProfileHeader?> GetConnectedSystemRunProfileHeaderAsync(int connectedSystemRunProfileId);
        public Task<ConnectorDefinition?> GetConnectorDefinitionAsync(int id);
        public Task<ConnectorDefinition?> GetConnectorDefinitionAsync(string name);
        public Task<IList<ConnectedSystemContainer>> GetConnectedSystemContainersAsync(ConnectedSystem connectedSystem);
        public Task<IList<ConnectedSystemObjectType>?> GetObjectTypesAsync(int id);
        public Task<IList<ConnectedSystemPartition>> GetConnectedSystemPartitionsAsync(ConnectedSystem connectedSystem);
        public Task<IList<ConnectorDefinitionHeader>> GetConnectorDefinitionHeadersAsync();
        public Task<IList<SyncRule>> GetSyncRulesAsync();
        public Task<IList<SyncRuleHeader>> GetSyncRuleHeadersAsync();
        public Task<int> GetConnectedSystemObjectCountAsync();
        public Task<int> GetConnectedSystemObjectOfTypeCountAsync(int connectedSystemObjectTypeId);
        public Task<List<ConnectedSystem>> GetConnectedSystemsAsync();
        public Task<List<ConnectedSystemHeader>> GetConnectedSystemHeadersAsync();
        public Task<List<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(ConnectedSystem connectedSystem);
        public Task<List<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(int connectedSystemId);
        public Task<SyncRule?> GetSyncRuleAsync(int id);


        public Task CreateConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile);
        public Task CreateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);
        public Task CreateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile runProfile);
        public Task CreateConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition);
        public Task CreateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject);
        public Task CreateConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer);
        public Task CreateConnectedSystemAsync(ConnectedSystem connectedSystem);


        public Task UpdateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);
        public Task UpdateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile);
        public Task UpdateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject);
        public Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem);


        public Task DeleteConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile);
        public Task DeleteConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);
        public Task DeleteConnectedSystemRunProfileAsync(ConnectedSystemRunProfile runProfile);
        public Task DeleteConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition);
        public Task DeleteConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer);
    }
}
