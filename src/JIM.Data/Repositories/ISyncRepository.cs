using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;

namespace JIM.Data.Repositories;

/// <summary>
/// Consolidated data access boundary for all worker sync operations.
/// <para>
/// This interface encapsulates every I/O operation that sync processors (import, sync, export)
/// need during a synchronisation run. In production, the implementation uses raw SQL/Npgsql
/// for hot-path operations. In tests, a purpose-built in-memory implementation provides
/// deterministic behaviour without EF Core quirks.
/// </para>
/// <para>
/// This replaces the current pattern where sync processors call through JimApplication's
/// 17 server properties (ConnectedSystems, Metaverse, Activities, etc.) and directly access
/// the repository layer, which caused three-way code path divergence between production,
/// workflow tests, and unit tests.
/// </para>
/// </summary>
/// <remarks>
/// Design decisions:
/// <list type="bullet">
/// <item>
/// Export evaluation, drift detection, and scoping evaluation are NOT on this interface.
/// They are application-layer domain logic that will move to ISyncEngine in a future phase.
/// </item>
/// <item>
/// Settings reads (page size, tracking levels) ARE included because they require database access.
/// </item>
/// <item>
/// Change tracker management (clear, auto-detect toggle) IS included because sync processors
/// need explicit control during batch page processing.
/// </item>
/// </list>
/// </remarks>
public interface ISyncRepository
{
    #region Connected System Object — Reads

    /// <summary>
    /// Gets the total number of CSOs for a connected system.
    /// Used to calculate page count at sync start.
    /// </summary>
    Task<int> GetConnectedSystemObjectCountAsync(int connectedSystemId);

    /// <summary>
    /// Gets the count of CSOs modified since the specified date.
    /// Used by delta sync to calculate page count.
    /// </summary>
    Task<int> GetConnectedSystemObjectModifiedSinceCountAsync(int connectedSystemId, DateTime modifiedSince);

    /// <summary>
    /// Loads a page of CSOs with full attribute values for sync processing.
    /// </summary>
    Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(int connectedSystemId, int page, int pageSize);

    /// <summary>
    /// Loads a page of CSOs modified since the specified date, with full attribute values.
    /// Used by delta sync to process only recently changed objects.
    /// </summary>
    Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsModifiedSinceAsync(int connectedSystemId, DateTime modifiedSince, int page, int pageSize);

    /// <summary>
    /// Gets a single CSO by ID with full attribute values.
    /// Used for cross-page reference resolution and lazy attribute loading.
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid csoId);

    /// <summary>
    /// Gets a CSO by its external ID attribute value (int type).
    /// Used during import to match incoming objects to existing CSOs.
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, int attributeValue);

    /// <summary>
    /// Gets a CSO by its external ID attribute value (string type).
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, string attributeValue);

    /// <summary>
    /// Gets a CSO by its external ID attribute value (Guid type).
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, Guid attributeValue);

    /// <summary>
    /// Gets a CSO by its external ID attribute value (long type).
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, long attributeValue);

    /// <summary>
    /// Gets a CSO by its secondary external ID attribute value.
    /// Used during confirming imports to match exported objects.
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAsync(int connectedSystemId, int objectTypeId, string secondaryExternalIdValue);

    /// <summary>
    /// Gets a CSO by secondary external ID searching across all object types.
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(int connectedSystemId, string secondaryExternalIdValue);

    /// <summary>
    /// Gets multiple CSOs by their external ID attribute values (batch lookup).
    /// Returns a dictionary keyed by the string representation of the attribute value.
    /// Used during import to batch-match incoming objects.
    /// </summary>
    Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsByAttributeValuesAsync(int connectedSystemId, int attributeId, IEnumerable<string> attributeValues);

    /// <summary>
    /// Gets multiple CSOs by secondary external ID values (batch lookup, any object type).
    /// Returns a dictionary keyed by the secondary external ID value.
    /// </summary>
    Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(int connectedSystemId, IEnumerable<string> secondaryExternalIdValues);

    /// <summary>
    /// Gets all external ID attribute values of type int for a connected system and object type.
    /// Used during import to build the full set of known external IDs for deletion detection.
    /// </summary>
    Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int objectTypeId);

    /// <summary>
    /// Gets all external ID attribute values of type string.
    /// </summary>
    Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int objectTypeId);

    /// <summary>
    /// Gets all external ID attribute values of type Guid.
    /// </summary>
    Task<List<Guid>> GetAllExternalIdAttributeValuesOfTypeGuidAsync(int connectedSystemId, int objectTypeId);

    /// <summary>
    /// Gets all external ID attribute values of type long.
    /// </summary>
    Task<List<long>> GetAllExternalIdAttributeValuesOfTypeLongAsync(int connectedSystemId, int objectTypeId);

    /// <summary>
    /// Loads CSOs by ID for cross-page reference resolution.
    /// Only loads CSOs and their attribute values — no navigation properties beyond that.
    /// </summary>
    Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsForReferenceResolutionAsync(IList<Guid> csoIds);

    /// <summary>
    /// Gets the string representations of reference attribute values for a CSO.
    /// Used during cross-page reference resolution to find unresolved references.
    /// </summary>
    Task<Dictionary<Guid, string>> GetReferenceExternalIdsAsync(Guid csoId);

    /// <summary>
    /// Gets the count of CSOs joined to a specific MVO across all connected systems.
    /// Used to determine whether an MVO should be deleted when its last CSO is disconnected.
    /// </summary>
    Task<int> GetConnectedSystemObjectCountByMetaverseObjectIdAsync(Guid metaverseObjectId);

    /// <summary>
    /// Gets the count of CSOs joined to a specific MVO within a single connected system.
    /// Used during join validation to check maximum join cardinality.
    /// </summary>
    Task<int> GetConnectedSystemObjectCountByMvoAsync(int connectedSystemId, Guid metaverseObjectId);

    #endregion

    #region Connected System Object — Writes

    /// <summary>
    /// Bulk creates CSOs with their attribute values.
    /// Uses raw SQL bulk operations in production for performance.
    /// </summary>
    Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects);

    /// <summary>
    /// Bulk creates CSOs with associated RPEIs. Persists the CSOs via raw SQL,
    /// then links change tracking records to the corresponding RPEIs.
    /// </summary>
    Task CreateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis,
        Func<int, Task>? onBatchPersisted = null);

    /// <summary>
    /// Bulk updates CSOs with their attribute values.
    /// Uses raw SQL bulk operations in production for performance.
    /// </summary>
    Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects);

    /// <summary>
    /// Bulk updates CSOs with associated RPEIs. Persists the CSO updates,
    /// then links change tracking records to the corresponding RPEIs.
    /// </summary>
    Task UpdateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis);

    /// <summary>
    /// Updates only the join state fields (MetaverseObjectId, JoinType, Status) on CSOs
    /// without touching their attribute values. Used after join/projection operations.
    /// </summary>
    Task UpdateConnectedSystemObjectJoinStatesAsync(List<ConnectedSystemObject> connectedSystemObjects);

    /// <summary>
    /// Updates CSOs that have new attribute values added (e.g., secondary external ID during export).
    /// </summary>
    Task UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(List<(ConnectedSystemObject cso, List<ConnectedSystemObjectAttributeValue> newAttributeValues)> updates);

    /// <summary>
    /// Deletes CSOs and their attribute values without change tracking.
    /// Used for quiet deletions (e.g., pre-disconnected CSOs).
    /// </summary>
    Task DeleteConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects);

    /// <summary>
    /// Deletes CSOs with associated RPEIs. Captures final attribute snapshots on the RPEIs
    /// before deletion for audit trail, then deletes the CSOs.
    /// </summary>
    Task DeleteConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis);

    /// <summary>
    /// Resolves cross-batch reference attribute values that could not be resolved during initial import
    /// because the target CSO was in a later batch. Uses raw SQL JOIN with partial indexes.
    /// Returns the count of resolved references.
    /// </summary>
    Task<int> FixupCrossBatchReferenceIdsAsync(int connectedSystemId);

    #endregion

    #region Metaverse Object — Reads

    /// <summary>
    /// Finds a matching MVO for join evaluation using the configured matching rules.
    /// Returns null if no match is found; throws or returns specific results for multiple matches
    /// depending on the matching rule configuration.
    /// </summary>
    Task<MetaverseObject?> FindMatchingMetaverseObjectAsync(ConnectedSystemObject cso, List<ObjectMatchingRule> matchingRules);

    #endregion

    #region Metaverse Object — Writes

    /// <summary>
    /// Bulk creates MVOs with their attribute values.
    /// </summary>
    Task CreateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects);

    /// <summary>
    /// Bulk updates MVOs with their attribute values.
    /// </summary>
    Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects);

    /// <summary>
    /// Updates a single MVO. Used for ad-hoc updates outside of batch processing
    /// (e.g., disconnection handling).
    /// </summary>
    Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject);

    /// <summary>
    /// Deletes an MVO, cascading FK cleanup via raw SQL to prevent constraint violations.
    /// Records the deletion with initiator information for audit trail.
    /// </summary>
    Task DeleteMetaverseObjectAsync(
        MetaverseObject metaverseObject,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName,
        List<MetaverseObjectAttributeValue>? finalAttributeValues);

    #endregion

    #region Pending Exports

    /// <summary>
    /// Gets all pending exports for a connected system.
    /// Used at sync start to build the O(1) pending export lookup by CSO ID.
    /// </summary>
    Task<List<PendingExport>> GetPendingExportsAsync(int connectedSystemId);

    /// <summary>
    /// Gets the count of pending exports for a connected system.
    /// Used by the export processor to determine paging.
    /// </summary>
    Task<int> GetPendingExportsCountAsync(int connectedSystemId);

    /// <summary>
    /// Bulk creates pending exports with their attribute value changes.
    /// Uses raw SQL bulk operations in production for performance.
    /// </summary>
    Task CreatePendingExportsAsync(IEnumerable<PendingExport> pendingExports);

    /// <summary>
    /// Bulk deletes pending exports.
    /// Uses raw SQL bulk operations in production for performance.
    /// </summary>
    Task DeletePendingExportsAsync(IEnumerable<PendingExport> pendingExports);

    /// <summary>
    /// Bulk updates pending exports.
    /// Uses raw SQL bulk operations in production for performance.
    /// </summary>
    Task UpdatePendingExportsAsync(IEnumerable<PendingExport> pendingExports);

    /// <summary>
    /// Deletes pending exports by their associated CSO IDs.
    /// Returns the count of deleted pending exports.
    /// Used during obsolete CSO processing to clean up orphaned exports.
    /// </summary>
    Task<int> DeletePendingExportsByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds);

    /// <summary>
    /// Gets a single pending export by its associated CSO ID.
    /// Used during export evaluation to check for existing pending exports in the database.
    /// </summary>
    Task<PendingExport?> GetPendingExportByConnectedSystemObjectIdAsync(Guid connectedSystemObjectId);

    /// <summary>
    /// Gets pending exports for multiple CSOs in a single query.
    /// Returns a dictionary keyed by CSO ID.
    /// </summary>
    Task<Dictionary<Guid, PendingExport>> GetPendingExportsByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds);

    /// <summary>
    /// Lightweight version of <see cref="GetPendingExportsByConnectedSystemObjectIdsAsync"/> for reconciliation.
    /// Returns pending exports with only scalar fields loaded (no attribute value changes).
    /// </summary>
    Task<Dictionary<Guid, PendingExport>> GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds);

    /// <summary>
    /// Gets CSO IDs that have pending exports for a connected system.
    /// Used during import reconciliation to identify which CSOs have outstanding exports.
    /// </summary>
    Task<HashSet<Guid>> GetCsoIdsWithPendingExportsByConnectedSystemAsync(int connectedSystemId);

    /// <summary>
    /// Deletes pending exports that are not tracked by the EF change tracker.
    /// Used during import reconciliation to clean up confirmed exports.
    /// </summary>
    Task DeleteUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports);

    /// <summary>
    /// Deletes pending export attribute value changes that are not tracked by the EF change tracker.
    /// Used during import reconciliation to clean up partially confirmed exports.
    /// </summary>
    Task DeleteUntrackedPendingExportAttributeValueChangesAsync(IEnumerable<PendingExportAttributeValueChange> untrackedAttributeValueChanges);

    /// <summary>
    /// Updates pending exports that are not tracked by the EF change tracker.
    /// Used during import reconciliation to update export status after confirmation.
    /// </summary>
    Task UpdateUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports);

    #endregion

    #region Activity and RPEIs

    /// <summary>
    /// Updates an activity's fields (progress, message, status, etc.).
    /// </summary>
    Task UpdateActivityAsync(Activity activity);

    /// <summary>
    /// Updates an activity's message field.
    /// Convenience method that updates only the Message property.
    /// </summary>
    Task UpdateActivityMessageAsync(Activity activity, string message);

    /// <summary>
    /// Updates activity progress fields using an independent database connection,
    /// bypassing any in-flight transaction on the main connection.
    /// Progress updates are immediately visible to other sessions (e.g., the UI).
    /// </summary>
    Task UpdateActivityProgressOutOfBandAsync(Activity activity);

    /// <summary>
    /// Marks an activity as failed with the specified error message.
    /// </summary>
    Task FailActivityWithErrorAsync(Activity activity, string errorMessage);

    /// <summary>
    /// Marks an activity as failed with details from the specified exception.
    /// </summary>
    Task FailActivityWithErrorAsync(Activity activity, Exception exception);

    /// <summary>
    /// Bulk inserts RPEIs via raw SQL, bypassing the EF change tracker.
    /// Returns true if raw SQL was used (RPEIs are outside EF tracking),
    /// false if the EF fallback was used (RPEIs remain tracked).
    /// </summary>
    Task<bool> BulkInsertRpeisAsync(List<ActivityRunProfileExecutionItem> rpeis);

    /// <summary>
    /// Bulk updates OutcomeSummary and error fields on already-persisted RPEIs,
    /// and inserts any new SyncOutcomes added after initial persistence.
    /// Used by confirming imports to merge reconciliation outcomes onto existing RPEIs.
    /// </summary>
    Task BulkUpdateRpeiOutcomesAsync(List<ActivityRunProfileExecutionItem> rpeis, List<ActivityRunProfileExecutionItemSyncOutcome> newOutcomes);

    /// <summary>
    /// Detaches RPEIs from the EF change tracker so they are not persisted by subsequent
    /// SaveChangesAsync calls. Call after raw SQL bulk insert has persisted them.
    /// </summary>
    void DetachRpeisFromChangeTracker(List<ActivityRunProfileExecutionItem> rpeis);

    /// <summary>
    /// Queries the database for RPEI error counts without loading RPEIs into memory.
    /// Returns total RPEIs with errors, total RPEIs, and total UnhandledError RPEIs.
    /// UnhandledError RPEIs indicate code/logic bugs and escalate activity status.
    /// </summary>
    Task<(int TotalWithErrors, int TotalRpeis, int TotalUnhandledErrors)> GetActivityRpeiErrorCountsAsync(Guid activityId);

    /// <summary>
    /// Persists ConnectedSystemObjectChange records attached to RPEIs.
    /// Used by the export processor to persist change history after RPEI raw SQL bulk insert
    /// (which only inserts RPEI scalar columns, not related change records).
    /// </summary>
    Task PersistRpeiCsoChangesAsync(List<ActivityRunProfileExecutionItem> rpeis);

    #endregion

    #region Sync Rules and Configuration

    /// <summary>
    /// Gets sync rules for a connected system.
    /// When <paramref name="includeDisabled"/> is false, only active rules are returned.
    /// </summary>
    Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabled);

    /// <summary>
    /// Gets all sync rules across all connected systems.
    /// Used to build the drift detection cache which needs rules from all systems.
    /// </summary>
    Task<List<SyncRule>> GetAllSyncRulesAsync();

    /// <summary>
    /// Gets the object types (schema) for a connected system.
    /// Used during sync to resolve attribute mappings.
    /// </summary>
    Task<List<ConnectedSystemObjectType>> GetObjectTypesAsync(int connectedSystemId);

    /// <summary>
    /// Updates a connected system's fields (e.g., LastDeltaSyncCompletedAt watermark).
    /// </summary>
    Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem);

    #endregion

    #region Settings

    /// <summary>
    /// Gets the configured sync page size (number of CSOs per page).
    /// </summary>
    Task<int> GetSyncPageSizeAsync();

    /// <summary>
    /// Gets the configured sync outcome tracking level.
    /// Controls how much detail is captured in RPEI outcome trees.
    /// </summary>
    Task<ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel> GetSyncOutcomeTrackingLevelAsync();

    /// <summary>
    /// Gets whether CSO change tracking is enabled.
    /// When enabled, attribute-level change records are persisted during import.
    /// </summary>
    Task<bool> GetCsoChangeTrackingEnabledAsync();

    /// <summary>
    /// Gets whether MVO change tracking is enabled.
    /// When enabled, attribute-level change records are persisted during sync.
    /// </summary>
    Task<bool> GetMvoChangeTrackingEnabledAsync();

    #endregion

    #region Change Tracker Management

    /// <summary>
    /// Clears all tracked entities from the EF change tracker.
    /// Called at page boundaries to prevent memory accumulation during large sync runs.
    /// In the in-memory test implementation, this is a no-op.
    /// </summary>
    void ClearChangeTracker();

    /// <summary>
    /// Controls whether SaveChangesAsync automatically calls DetectChanges.
    /// Disabled during batch page processing to prevent navigation property traversal
    /// from discovering conflicting entity instances after ClearChangeTracker.
    /// In the in-memory test implementation, this is a no-op.
    /// </summary>
    void SetAutoDetectChangesEnabled(bool enabled);

    #endregion

    #region CSO Lookup Cache

    /// <summary>
    /// Adds a CSO to the in-memory lookup cache for fast external ID matching during import.
    /// Keyed by (connectedSystemId, externalIdAttributeId, externalIdValue).
    /// </summary>
    void AddCsoToCache(int connectedSystemId, int externalIdAttributeId, string externalIdValue, Guid csoId);

    /// <summary>
    /// Removes a CSO from the in-memory lookup cache.
    /// Called when a CSO is deleted or its external ID changes.
    /// </summary>
    void EvictCsoFromCache(int connectedSystemId, int externalIdAttributeId, string externalIdValue);

    #endregion

    #region Connected System Operations

    /// <summary>
    /// Refreshes the auto-selected containers for a connected system using the connector triad
    /// (ObjectTypes, Partitions, Attributes), and creates RPEIs for any new containers discovered.
    /// </summary>
    Task RefreshAndAutoSelectContainersWithTriadAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        IReadOnlyList<string> createdContainerExternalIds,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName,
        Activity? parentActivity = null);

    /// <summary>
    /// Updates a connected system with the latest triad data from the connector.
    /// Called during import to refresh schema and partition information.
    /// </summary>
    Task UpdateConnectedSystemWithTriadAsync(
        ConnectedSystem connectedSystem,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName);

    #endregion

    #region MVO Change History

    /// <summary>
    /// Creates an MVO change record directly via raw SQL, bypassing EF tracking.
    /// Used for deletion change records where the MVO is about to be removed.
    /// </summary>
    Task CreateMetaverseObjectChangeDirectAsync(MetaverseObjectChange change);

    #endregion
}
