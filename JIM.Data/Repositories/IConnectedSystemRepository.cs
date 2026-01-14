using JIM.Models.Core;
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
    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, long attributeValue);
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
    /// Deletes multiple Pending Export objects in a single batch operation.
    /// Used to efficiently remove confirmed pending exports during sync.
    /// </summary>
    /// <param name="pendingExports">The Pending Exports to delete.</param>
    public Task DeletePendingExportsAsync(IEnumerable<PendingExport> pendingExports);

    /// <summary>
    /// Updates multiple Pending Export objects in a single batch operation.
    /// Used to efficiently update pending exports during sync.
    /// </summary>
    /// <param name="pendingExports">The Pending Exports to update.</param>
    public Task UpdatePendingExportsAsync(IEnumerable<PendingExport> pendingExports);

    /// <summary>
    /// Creates a new Pending Export object.
    /// </summary>
    /// <param name="pendingExport">The Pending Export to create.</param>
    public Task CreatePendingExportAsync(PendingExport pendingExport);

    /// <summary>
    /// Creates multiple Pending Export objects in a single batch operation.
    /// More efficient than creating one at a time when processing pages of objects.
    /// </summary>
    /// <param name="pendingExports">The Pending Exports to create.</param>
    public Task CreatePendingExportsAsync(IEnumerable<PendingExport> pendingExports);

    /// <summary>
    /// Retrieves a page of Pending Export headers for a Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <param name="page">Which page to return results for, i.e. 1-n.</param>
    /// <param name="pageSize">How many results to return per page.</param>
    /// <param name="statusFilters">Optional filter by one or more statuses.</param>
    /// <param name="searchQuery">Optional search query to filter by target object identifier, source MVO display name, or error message.</param>
    /// <param name="sortBy">Optional column to sort by (e.g., "changetype", "status", "created", "errors").</param>
    /// <param name="sortDescending">Whether to sort in descending order (default: true).</param>
    public Task<PagedResultSet<PendingExportHeader>> GetPendingExportHeadersAsync(
        int connectedSystemId,
        int page,
        int pageSize,
        IEnumerable<PendingExportStatus>? statusFilters = null,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true);

    /// <summary>
    /// Retrieves a single Pending Export by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the Pending Export.</param>
    public Task<PendingExport?> GetPendingExportAsync(Guid id);

    /// <summary>
    /// Retrieves the Pending Export for a specific Connected System Object.
    /// There should only be one PendingExport per CSO at any time.
    /// </summary>
    /// <param name="connectedSystemObjectId">The unique identifier of the Connected System Object.</param>
    /// <returns>The PendingExport for the CSO, or null if none exists.</returns>
    public Task<PendingExport?> GetPendingExportByConnectedSystemObjectIdAsync(Guid connectedSystemObjectId);

    /// <summary>
    /// Retrieves Pending Exports for multiple Connected System Objects in a single query.
    /// More efficient than calling GetPendingExportByConnectedSystemObjectIdAsync multiple times.
    /// </summary>
    /// <param name="connectedSystemObjectIds">The CSO IDs to retrieve pending exports for.</param>
    /// <returns>A dictionary mapping CSO ID to its pending export (if any).</returns>
    public Task<Dictionary<Guid, PendingExport>> GetPendingExportsByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds);

    /// <summary>
    /// Gets all Connected System Objects that are joined to a specific Metaverse Object.
    /// Used for evaluating MVO deletion exports.
    /// </summary>
    /// <param name="metaverseObjectId">The MVO ID to find joined CSOs for.</param>
    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdAsync(Guid metaverseObjectId);

    /// <summary>
    /// Gets the count of Connected System Objects joined to a specific Metaverse Object.
    /// Used to determine if an MVO has any remaining connectors before deletion.
    /// </summary>
    /// <param name="metaverseObjectId">The MVO ID to count joined CSOs for.</param>
    public Task<int> GetConnectedSystemObjectCountByMetaverseObjectIdAsync(Guid metaverseObjectId);

    /// <summary>
    /// Gets a Connected System Object by its joined Metaverse Object ID and Connected System.
    /// Used for finding existing CSOs during export evaluation.
    /// </summary>
    /// <param name="metaverseObjectId">The MVO ID.</param>
    /// <param name="connectedSystemId">The Connected System ID.</param>
    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByMetaverseObjectIdAsync(Guid metaverseObjectId, int connectedSystemId);

    /// <summary>
    /// Batch loads all Connected System Objects that are joined to Metaverse Objects, grouped by target Connected System.
    /// Used for pre-loading CSO lookup data to avoid O(N×M) queries during export evaluation.
    /// </summary>
    /// <param name="targetConnectedSystemIds">The Connected System IDs to load CSOs for.</param>
    /// <returns>A dictionary keyed by (MvoId, ConnectedSystemId) for O(1) lookup.</returns>
    public Task<Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>> GetConnectedSystemObjectsByTargetSystemsAsync(IEnumerable<int> targetConnectedSystemIds);

    /// <summary>
    /// Batch loads CSO attribute values for the specified CSO IDs.
    /// Used for per-page caching during export evaluation to enable no-net-change detection.
    /// </summary>
    /// <param name="csoIds">The CSO IDs to load attribute values for.</param>
    /// <returns>A list of CSO attribute values with their Attribute navigation property populated.</returns>
    public Task<List<ConnectedSystemObjectAttributeValue>> GetCsoAttributeValuesByCsoIdsAsync(IEnumerable<Guid> csoIds);

    /// <summary>
    /// Finds a Connected System Object that matches the given Metaverse Object using the specified matching rule.
    /// This is the reverse of FindMetaverseObjectUsingMatchingRuleAsync - it looks up CSOs by MVO attribute values.
    /// Used during export evaluation to find existing CSOs for provisioning decisions.
    /// </summary>
    /// <param name="metaverseObject">The MVO to find a matching CSO for.</param>
    /// <param name="connectedSystem">The target Connected System.</param>
    /// <param name="connectedSystemObjectType">The target CSO type.</param>
    /// <param name="objectMatchingRule">The matching rule defining how to match.</param>
    /// <returns>The matching CSO, or null if no match found.</returns>
    public Task<ConnectedSystemObject?> FindConnectedSystemObjectUsingMatchingRuleAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        ObjectMatchingRule objectMatchingRule);

    /// <summary>
    /// Gets a Connected System Object by its secondary external ID attribute value.
    /// Used to find PendingProvisioning CSOs during import reconciliation when the
    /// primary external ID (e.g., objectGUID) is system-assigned and not yet known.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System ID.</param>
    /// <param name="objectTypeId">The object type ID.</param>
    /// <param name="secondaryExternalIdValue">The secondary external ID value (e.g., DN for LDAP).</param>
    /// <returns>The matching CSO, or null if not found.</returns>
    public Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAsync(int connectedSystemId, int objectTypeId, string secondaryExternalIdValue);

    /// <summary>
    /// Gets a Connected System Object by its secondary external ID attribute value across ALL object types.
    /// This is used for reference resolution where the referenced object can be of any type
    /// (e.g., a group's member reference can point to a user, another group, or other object types).
    /// </summary>
    /// <param name="connectedSystemId">The connected system to search within.</param>
    /// <param name="secondaryExternalIdValue">The secondary external ID value to search for (e.g., a DN).</param>
    /// <returns>The matching CSO, or null if not found.</returns>
    public Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(int connectedSystemId, string secondaryExternalIdValue);

    /// <summary>
    /// Retrieves all the Connected System Object Types for a given Connected System.
    /// Includes Attributes.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to return the types for.</param>
    public Task<List<ConnectedSystemObjectType>> GetObjectTypesAsync(int connectedSystemId);

    public Task<IList<ConnectedSystemPartition>> GetConnectedSystemPartitionsAsync(ConnectedSystem connectedSystem);

    /// <summary>
    /// Gets a Connected System Partition by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the partition.</param>
    Task<ConnectedSystemPartition?> GetConnectedSystemPartitionAsync(int id);

    /// <summary>
    /// Updates a Connected System Partition.
    /// </summary>
    /// <param name="partition">The partition to update.</param>
    Task UpdateConnectedSystemPartitionAsync(ConnectedSystemPartition partition);

    /// <summary>
    /// Gets a Connected System Container by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the container.</param>
    Task<ConnectedSystemContainer?> GetConnectedSystemContainerAsync(int id);

    /// <summary>
    /// Updates a Connected System Container.
    /// </summary>
    /// <param name="container">The container to update.</param>
    Task UpdateConnectedSystemContainerAsync(ConnectedSystemContainer container);
    public Task<IList<ConnectorDefinitionHeader>> GetConnectorDefinitionHeadersAsync();
    public Task<List<SyncRule>> GetSyncRulesAsync();

    /// <summary>
    /// Retrieves all the sync rules for a given Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <param name="includeDisabledSyncRules">Controls whether to return sync rules that are disabled</param>
    public Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabledSyncRules);

    public Task<IList<SyncRuleHeader>> GetSyncRuleHeadersAsync();

    #region Sync Rule Mappings
    /// <summary>
    /// Gets all mappings for a sync rule.
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
    Task<List<SyncRuleMapping>> GetSyncRuleMappingsAsync(int syncRuleId);

    /// <summary>
    /// Gets a specific sync rule mapping by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the mapping.</param>
    Task<SyncRuleMapping?> GetSyncRuleMappingAsync(int id);

    /// <summary>
    /// Creates a new sync rule mapping.
    /// </summary>
    /// <param name="mapping">The mapping to create.</param>
    Task CreateSyncRuleMappingAsync(SyncRuleMapping mapping);

    /// <summary>
    /// Updates an existing sync rule mapping.
    /// </summary>
    /// <param name="mapping">The mapping to update.</param>
    Task UpdateSyncRuleMappingAsync(SyncRuleMapping mapping);

    /// <summary>
    /// Deletes a sync rule mapping.
    /// </summary>
    /// <param name="mapping">The mapping to delete.</param>
    Task DeleteSyncRuleMappingAsync(SyncRuleMapping mapping);
    #endregion

    public Task<List<ConnectedSystem>> GetConnectedSystemsAsync();
    public Task<List<ConnectedSystemHeader>> GetConnectedSystemHeadersAsync();
    public Task<List<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(ConnectedSystem connectedSystem);
    public Task<List<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(int connectedSystemId);
    public Task<PagedResultSet<ConnectedSystemObjectHeader>> GetConnectedSystemObjectHeadersAsync(
        int connectedSystemId,
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        IEnumerable<ConnectedSystemObjectStatus>? statusFilter = null);
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

    /// <summary>
    /// Retrieves a page's worth of Connected System Objects for a specific system that have been created or modified since a given timestamp.
    /// Used for delta synchronisation to process only changed objects.
    /// Returns CSOs where Created > modifiedSince OR LastUpdated > modifiedSince, ensuring both new and updated objects are included.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="modifiedSince">Only return CSOs where Created or LastUpdated is greater than this timestamp.</param>
    /// <param name="page">Which page to return results for, i.e. 1-n.</param>
    /// <param name="pageSize">How many Connected System Objects to return in this page of result.</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsModifiedSinceAsync(
        int connectedSystemId,
        DateTime modifiedSince,
        int page,
        int pageSize);

    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System that have been created or modified since a given timestamp.
    /// Used for delta synchronisation statistics.
    /// Returns count of CSOs where Created > modifiedSince OR LastUpdated > modifiedSince.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <param name="modifiedSince">Only count CSOs where Created or LastUpdated is greater than this timestamp.</param>
    public Task<int> GetConnectedSystemObjectModifiedSinceCountAsync(int connectedSystemId, DateTime modifiedSince);

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

    /// <summary>
    /// Returns the count of CSOs in a connected system that are joined to a specific MVO.
    /// Used during sync to check if an MVO already has a join in this connected system (1:1 constraint).
    /// </summary>
    public Task<int> GetConnectedSystemObjectCountByMvoAsync(int connectedSystemId, Guid metaverseObjectId);

    public int GetConnectedSystemCount();
    public Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int objectTypeId);
    public Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int objectTypeId);
    public Task<List<long>> GetAllExternalIdAttributeValuesOfTypeLongAsync(int connectedSystemId, int objectTypeId);
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

    /// <summary>
    /// Updates a Connected System Object and explicitly adds new attribute values to the DbContext.
    /// This is needed when adding attribute values to a CSO that was loaded without any (e.g., PendingProvisioning).
    /// </summary>
    public Task UpdateConnectedSystemObjectWithNewAttributeValuesAsync(ConnectedSystemObject connectedSystemObject, List<ConnectedSystemObjectAttributeValue> newAttributeValues);
    public Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem);
    public Task UpdateSyncRuleAsync(SyncRule syncRule);


    public Task DeleteConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject);
    public Task DeleteConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects);
    public Task DeleteConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer);
    public Task DeleteConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition);
    public Task DeleteConnectedSystemRunProfileAsync(ConnectedSystemRunProfile runProfile);
    public Task DeleteConnectorDefinitionAsync(ConnectorDefinition connectorDefinition);
    public Task DeleteConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile);
    public Task DeleteSyncRuleAsync(SyncRule syncRule);

    public Task<bool> IsObjectTypeAttributeBeingReferencedAsync(ConnectedSystemObjectTypeAttribute connectedSystemObjectTypeAttribute);

    #region Object Types and Attributes
    /// <summary>
    /// Gets a Connected System Object Type by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the object type.</param>
    Task<ConnectedSystemObjectType?> GetObjectTypeAsync(int id);

    /// <summary>
    /// Updates a Connected System Object Type.
    /// </summary>
    /// <param name="objectType">The object type to update.</param>
    Task UpdateObjectTypeAsync(ConnectedSystemObjectType objectType);

    /// <summary>
    /// Gets a Connected System Attribute by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the attribute.</param>
    Task<ConnectedSystemObjectTypeAttribute?> GetAttributeAsync(int id);

    /// <summary>
    /// Updates a Connected System Attribute.
    /// </summary>
    /// <param name="attribute">The attribute to update.</param>
    Task UpdateAttributeAsync(ConnectedSystemObjectTypeAttribute attribute);

    /// <summary>
    /// Batch updates multiple Connected System Attributes in a single transaction.
    /// </summary>
    /// <param name="attributes">The attributes to update.</param>
    Task UpdateAttributesAsync(IEnumerable<ConnectedSystemObjectTypeAttribute> attributes);
    #endregion

    #region Object Matching Rules
    /// <summary>
    /// Creates a new object matching rule for a Connected System Object Type.
    /// </summary>
    Task CreateObjectMatchingRuleAsync(ObjectMatchingRule rule);

    /// <summary>
    /// Updates an existing object matching rule.
    /// </summary>
    Task UpdateObjectMatchingRuleAsync(ObjectMatchingRule rule);

    /// <summary>
    /// Deletes an object matching rule and its sources.
    /// </summary>
    Task DeleteObjectMatchingRuleAsync(ObjectMatchingRule rule);

    /// <summary>
    /// Gets an object matching rule by ID with all related entities loaded.
    /// </summary>
    Task<ObjectMatchingRule?> GetObjectMatchingRuleAsync(int id);
    #endregion

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