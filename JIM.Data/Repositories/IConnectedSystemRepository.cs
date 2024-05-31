using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Utility;
namespace JIM.Data.Repositories;

public interface IConnectedSystemRepository
{
    public Task<ConnectedSystem?> GetConnectedSystemAsync(int id);
    public Task<ConnectedSystemHeader?> GetConnectedSystemHeaderAsync(int id);
    public Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id);
    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, Guid attributeValue);
    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, int attributeValue);
    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue);
    public Task<ConnectedSystemRunProfileHeader?> GetConnectedSystemRunProfileHeaderAsync(int connectedSystemRunProfileId);
    public Task<ConnectorDefinition?> GetConnectorDefinitionAsync(int id);
    public Task<ConnectorDefinition?> GetConnectorDefinitionAsync(string name);
    public Task<Guid?> GetConnectedSystemObjectIdByAttributeValueAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue);
    public Task<IList<ConnectedSystemContainer>> GetConnectedSystemContainersAsync(ConnectedSystem connectedSystem);

    /// <summary>
    /// Retrieves all the Pending Exports for a given Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System the Pending Exports relate to.</param>
    public Task<List<PendingExport>> GetPendingExportsAsync(int connectedSystemId);

    /// <summary>
    /// Retrieves the count of how many Pending Export objects there are for a particular Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System the Pending Exports relate to.</param>
    public Task<int> GetPendingExportsCountAsync(int connectedSystemId);

    /// <summary>
    /// Retrieves all the Connected System Object Types for a given Connected System.
    /// Includes Attributes.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to return the types for.</param>
    public Task<List<ConnectedSystemObjectType>> GetObjectTypesAsync(int connectedSystemId);

    public Task<IList<ConnectedSystemPartition>> GetConnectedSystemPartitionsAsync(ConnectedSystem connectedSystem);
    public Task<IList<ConnectorDefinitionHeader>> GetConnectorDefinitionHeadersAsync();
    public Task<List<SyncRule>> GetSyncRulesAsync();

    /// <summary>
    /// Retrieves all the sync rules for a given Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <param name="includeDisabledSyncRules">Controls whether to return sync rules that are disabled</param>
    public Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabledSyncRules);

    public Task<IList<SyncRuleHeader>> GetSyncRuleHeadersAsync();
    public Task<List<ConnectedSystem>> GetConnectedSystemsAsync();
    public Task<List<ConnectedSystemHeader>> GetConnectedSystemHeadersAsync();
    public Task<List<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(ConnectedSystem connectedSystem);
    public Task<List<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(int connectedSystemId);
    public Task<PagedResultSet<ConnectedSystemObjectHeader>> GetConnectedSystemObjectHeadersAsync(int connectedSystemId, int page, int pageSize, QuerySortBy querySortBy = QuerySortBy.DateCreated, QueryRange queryRange = QueryRange.Forever);
    public Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(int connectedSystemId, int page, int pageSize, bool returnAttributes = false);
    
    /// <summary>
    /// Returns all the CSOs for a Connected System that are marked as Obsolete.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="returnAttributes">Controls whether ConnectedSystemObject.AttributeValues[n].Attribute is populated. By default, it isn't for performance reasons.</param>
    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsObsoleteAsync(int connectedSystemId, bool returnAttributes);

    /// <summary>
    /// Returns all the CSOs for a Connected System that are not joined to Metaverse Objects.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="returnAttributes">Controls whether ConnectedSystemObject.AttributeValues[n].Attribute is populated. By default, it isn't for performance reasons.</param>
    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsUnJoinedAsync(int connectedSystemId, bool returnAttributes);
    
    public Task<SyncRule?> GetSyncRuleAsync(int id);

    /// <summary>
    /// Returns the count of all Connected System Objects across all Connected Systems.
    /// </summary>
    public Task<int> GetConnectedSystemObjectCountAsync();

    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to find the object count for.</param>s
    public Task<int> GetConnectedSystemObjectCountAsync(int connectedSystemId);

    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System, where the status is Obosolete.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to find the Obosolete object count for.</param>
    public Task<int> GetConnectedSystemObjectObsoleteCountAsync(int connectedSystemId);

    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System, that are not joined to a Metaverse Object.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to find the unjoined object count for.</param>
    public Task<int> GetConnectedSystemObjectUnJoinedCountAsync(int connectedSystemId);

    public int GetConnectedSystemCount();
    public Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int objectTypeId);
    public Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int objectTypeId);
    public Task<List<Guid>> GetAllExternalIdAttributeValuesOfTypeGuidAsync(int connectedSystemId, int objectTypeId);


    public Task CreateConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile);
    public Task CreateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);
    public Task CreateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile runProfile);
    public Task CreateConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition);
    public Task CreateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject);
    public Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects);
    public Task CreateConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer);
    public Task CreateConnectedSystemAsync(ConnectedSystem connectedSystem);
    public Task CreateSyncRuleAsync(SyncRule syncRule);


    public Task UpdateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);
    public Task UpdateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile);
    public Task UpdateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject);
    public Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects);
    public Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem);
    public Task UpdateSyncRuleAsync(SyncRule syncRule);


    public Task DeleteConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject);
    public Task DeleteAllConnectedSystemObjectsAsync(int connectedSystemId, bool deleteAllConnectedSystemObjectChangeObjects);
    public void DeleteAllPendingExportObjects(int connectedSystemId);
    public Task DeleteConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer);
    public Task DeleteConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition);
    public Task DeleteConnectedSystemRunProfileAsync(ConnectedSystemRunProfile runProfile);
    public Task DeleteConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);
    public Task DeleteConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile);
    public Task DeleteSyncRuleAsync(SyncRule syncRule);

    public Task<bool> IsObjectTypeAttributeBeingReferencedAsync(ConnectedSystemObjectTypeAttribute connectedSystemObjectTypeAttribute);
}