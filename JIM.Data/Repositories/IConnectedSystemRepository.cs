using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
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
    /// Deletes a Pending Export object.
    /// </summary>
    /// <param name="pendingExport">The Pending Export to delete.</param>
    public Task DeletePendingExportAsync(PendingExport pendingExport);

    /// <summary>
    /// Updates a Pending Export object.
    /// Used when removing successfully applied attribute changes and updating error tracking.
    /// </summary>
    /// <param name="pendingExport">The Pending Export to update.</param>
    public Task UpdatePendingExportAsync(PendingExport pendingExport);

    /// <summary>
    /// Creates a new Pending Export object.
    /// </summary>
    /// <param name="pendingExport">The Pending Export to create.</param>
    public Task CreatePendingExportAsync(PendingExport pendingExport);

    /// <summary>
    /// Retrieves a page of Pending Export headers for a Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <param name="page">Which page to return results for, i.e. 1-n.</param>
    /// <param name="pageSize">How many results to return per page.</param>
    /// <param name="statusFilter">Optional filter by status.</param>
    /// <param name="searchQuery">Optional search query to filter by target object identifier, source MVO display name, or error message.</param>
    /// <param name="sortBy">Optional column to sort by (e.g., "changetype", "status", "created", "errors").</param>
    /// <param name="sortDescending">Whether to sort in descending order (default: true).</param>
    public Task<PagedResultSet<PendingExportHeader>> GetPendingExportHeadersAsync(
        int connectedSystemId,
        int page,
        int pageSize,
        PendingExportStatus? statusFilter = null,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true);

    /// <summary>
    /// Retrieves a single Pending Export by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the Pending Export.</param>
    public Task<PendingExport?> GetPendingExportAsync(Guid id);

    /// <summary>
    /// Gets all Connected System Objects that are joined to a specific Metaverse Object.
    /// Used for evaluating MVO deletion exports.
    /// </summary>
    /// <param name="metaverseObjectId">The MVO ID to find joined CSOs for.</param>
    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdAsync(Guid metaverseObjectId);

    /// <summary>
    /// Gets a Connected System Object by its joined Metaverse Object ID and Connected System.
    /// Used for finding existing CSOs during export evaluation.
    /// </summary>
    /// <param name="metaverseObjectId">The MVO ID.</param>
    /// <param name="connectedSystemId">The Connected System ID.</param>
    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByMetaverseObjectIdAsync(Guid metaverseObjectId, int connectedSystemId);

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
    public Task DeleteConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer);
    public Task DeleteConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition);
    public Task DeleteConnectedSystemRunProfileAsync(ConnectedSystemRunProfile runProfile);
    public Task DeleteConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);
    public Task DeleteConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile);
    public Task DeleteSyncRuleAsync(SyncRule syncRule);

    public Task<bool> IsObjectTypeAttributeBeingReferencedAsync(ConnectedSystemObjectTypeAttribute connectedSystemObjectTypeAttribute);

    #region Connected System Deletion
    /// <summary>
    /// Deletes all Connected System Objects and their dependencies for a Connected System.
    /// This is used by both ClearConnectedSystemObjects and DeleteConnectedSystem.
    /// Does NOT delete the Connected System itself or its configuration (sync rules, run profiles, etc.).
    /// </summary>
    /// <param name="connectedSystemId">The ID of the Connected System.</param>
    /// <param name="deleteChangeHistory">If true, deletes ConnectedSystemObjectChanges. If false, nulls the CSO FK.</param>
    Task DeleteAllConnectedSystemObjectsAndDependenciesAsync(int connectedSystemId, bool deleteChangeHistory);

    /// <summary>
    /// Deletes a Connected System and all its related data using bulk SQL operations for performance.
    /// Should only be called after verifying no sync operations are running.
    /// </summary>
    /// <param name="connectedSystemId">The ID of the Connected System to delete.</param>
    Task DeleteConnectedSystemAsync(int connectedSystemId);

    /// <summary>
    /// Gets the count of Sync Rules for a Connected System.
    /// </summary>
    Task<int> GetSyncRuleCountAsync(int connectedSystemId);

    /// <summary>
    /// Gets the count of Run Profiles for a Connected System.
    /// </summary>
    Task<int> GetRunProfileCountAsync(int connectedSystemId);

    /// <summary>
    /// Gets the count of Partitions for a Connected System.
    /// </summary>
    Task<int> GetPartitionCountAsync(int connectedSystemId);

    /// <summary>
    /// Gets the count of Containers for a Connected System.
    /// </summary>
    Task<int> GetContainerCountAsync(int connectedSystemId);

    /// <summary>
    /// Gets the count of Activities for a Connected System.
    /// </summary>
    Task<int> GetActivityCountAsync(int connectedSystemId);

    /// <summary>
    /// Gets the count of MVOs joined to CSOs for a Connected System.
    /// </summary>
    Task<int> GetJoinedMvoCountAsync(int connectedSystemId);

    /// <summary>
    /// Checks if there is a running sync task for a Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System ID to check.</param>
    /// <returns>The running task, or null if no task is running.</returns>
    Task<SynchronisationWorkerTask?> GetRunningSyncTaskAsync(int connectedSystemId);
    #endregion
}