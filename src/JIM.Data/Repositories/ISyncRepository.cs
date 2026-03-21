using JIM.Models.Activities;
using JIM.Models.Core;
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
/// This interface is PURE DATA ACCESS. Business logic (object matching, CSO caching, settings,
/// RPEI-linking, connector triad operations, activity failure) lives on <c>ISyncServer</c>.
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
    /// Bulk updates CSOs with their attribute values.
    /// Uses raw SQL bulk operations in production for performance.
    /// </summary>
    Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects);

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
    /// Resolves cross-batch reference attribute values that could not be resolved during initial import
    /// because the target CSO was in a later batch. Uses raw SQL JOIN with partial indexes.
    /// Returns the count of resolved references.
    /// </summary>
    Task<int> FixupCrossBatchReferenceIdsAsync(int connectedSystemId);

    #endregion

    #region Object Matching — Data Access

    /// <summary>
    /// Finds an MVO that matches a CSO using the specified matching rule.
    /// Used by object matching during import (CSO→MVO join).
    /// </summary>
    Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(
        ConnectedSystemObject connectedSystemObject,
        MetaverseObjectType metaverseObjectType,
        ObjectMatchingRule objectMatchingRule);

    /// <summary>
    /// Finds a CSO that matches an MVO using the specified matching rule.
    /// Used by object matching during export (MVO→CSO lookup).
    /// </summary>
    Task<ConnectedSystemObject?> FindConnectedSystemObjectUsingMatchingRuleAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        ObjectMatchingRule objectMatchingRule);

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
    /// </summary>
    Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject);

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

    #region MVO Change History

    /// <summary>
    /// Creates an MVO change record directly via raw SQL, bypassing EF tracking.
    /// Used for deletion change records where the MVO is about to be removed.
    /// </summary>
    Task CreateMetaverseObjectChangeDirectAsync(MetaverseObjectChange change);

    #endregion

    #region Connected System Object — Singular Convenience Methods

    /// <summary>
    /// Creates a single CSO. Convenience wrapper around <see cref="CreateConnectedSystemObjectsAsync(List{ConnectedSystemObject})"/>.
    /// </summary>
    Task CreateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject);

    /// <summary>
    /// Updates a single CSO. Convenience wrapper around <see cref="UpdateConnectedSystemObjectsAsync(List{ConnectedSystemObject})"/>.
    /// </summary>
    Task UpdateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject);

    /// <summary>
    /// Updates a single CSO with new attribute values (e.g., secondary external ID during export).
    /// Convenience wrapper around <see cref="UpdateConnectedSystemObjectsWithNewAttributeValuesAsync"/>.
    /// </summary>
    Task UpdateConnectedSystemObjectWithNewAttributeValuesAsync(
        ConnectedSystemObject connectedSystemObject,
        List<ConnectedSystemObjectAttributeValue> newAttributeValues);

    #endregion

    #region Pending Export — Singular Convenience Methods

    /// <summary>
    /// Creates a single pending export. Convenience wrapper around <see cref="CreatePendingExportsAsync"/>.
    /// </summary>
    Task CreatePendingExportAsync(PendingExport pendingExport);

    /// <summary>
    /// Deletes a single pending export. Convenience wrapper around <see cref="DeletePendingExportsAsync"/>.
    /// </summary>
    Task DeletePendingExportAsync(PendingExport pendingExport);

    /// <summary>
    /// Updates a single pending export. Convenience wrapper around <see cref="UpdatePendingExportsAsync"/>.
    /// </summary>
    Task UpdatePendingExportAsync(PendingExport pendingExport);

    #endregion

    #region Export Evaluation Support

    /// <summary>
    /// Gets all CSOs joined to a specific MVO across all connected systems.
    /// Used during MVO deletion to find all provisioned CSOs for delete exports.
    /// </summary>
    Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdAsync(Guid metaverseObjectId);

    /// <summary>
    /// Gets CSOs joined to MVOs that are targeted by the specified connected systems.
    /// Returns a dictionary keyed by (MvoId, ConnectedSystemId) for O(1) lookup during export evaluation.
    /// </summary>
    Task<Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>> GetConnectedSystemObjectsByTargetSystemsAsync(
        IEnumerable<int> targetConnectedSystemIds);

    /// <summary>
    /// Batch loads CSO attribute values for the specified CSO IDs.
    /// Used to pre-load target CSO attribute values for no-net-change detection during export evaluation.
    /// </summary>
    Task<List<ConnectedSystemObjectAttributeValue>> GetCsoAttributeValuesByCsoIdsAsync(IEnumerable<Guid> csoIds);

    /// <summary>
    /// Gets a single CSO joined to a specific MVO within a connected system.
    /// Used during export evaluation to find existing CSOs for provisioning decisions.
    /// </summary>
    Task<ConnectedSystemObject?> GetConnectedSystemObjectByMetaverseObjectIdAsync(Guid metaverseObjectId, int connectedSystemId);

    /// <summary>
    /// Gets CSOs joined to multiple MVOs within a connected system.
    /// Returns a dictionary keyed by MVO ID for O(1) lookup.
    /// Used for reference resolution during export processing.
    /// </summary>
    Task<Dictionary<Guid, ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdsAsync(
        IEnumerable<Guid> metaverseObjectIds, int connectedSystemId);

    /// <summary>
    /// Gets a single connected system object type attribute by ID.
    /// Used during export to determine attribute data types.
    /// </summary>
    Task<ConnectedSystemObjectTypeAttribute?> GetAttributeAsync(int id);

    /// <summary>
    /// Gets multiple connected system object type attributes by their IDs.
    /// Returns a dictionary keyed by attribute ID for O(1) lookup.
    /// Used during batch export processing to pre-fetch attribute definitions.
    /// </summary>
    Task<Dictionary<int, ConnectedSystemObjectTypeAttribute>> GetAttributesByIdsAsync(IEnumerable<int> ids);

    #endregion

    #region Export Execution Support

    /// <summary>
    /// Gets the count of pending exports that are ready for execution.
    /// Applies database-level filtering for status, retry timing, and max retries.
    /// </summary>
    Task<int> GetExecutableExportCountAsync(int connectedSystemId);

    /// <summary>
    /// Gets all pending exports that are ready for execution.
    /// Applies database-level filtering for status, retry timing, and max retries.
    /// </summary>
    Task<List<PendingExport>> GetExecutableExportsAsync(int connectedSystemId);

    /// <summary>
    /// Gets a batch of executable exports using paged loading.
    /// Uses AsNoTracking in production for minimal EF overhead.
    /// </summary>
    Task<List<PendingExport>> GetExecutableExportBatchAsync(int connectedSystemId, int skip, int take);

    /// <summary>
    /// Marks pending exports as Executing with the current UTC timestamp.
    /// Uses raw SQL in production for efficiency.
    /// </summary>
    Task MarkPendingExportsAsExecutingAsync(IList<PendingExport> pendingExports);

    /// <summary>
    /// Reloads pending exports by their IDs with full object graph.
    /// Used during parallel export processing to reload exports in a separate context.
    /// </summary>
    Task<List<PendingExport>> GetPendingExportsByIdsAsync(IList<Guid> pendingExportIds);

    #endregion
}
