using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using Serilog;

namespace JIM.PostgresData;

/// <summary>
/// Production implementation of <see cref="ISyncRepository"/> for pure data-access methods.
/// Delegates to the existing PostgresData sub-repositories (ConnectedSystemRepository,
/// MetaverseRepository, ActivityRepository, etc.) which use raw SQL for hot-path operations.
/// <para>
/// Methods that require Application-layer business logic (RPEI linking, change tracking,
/// object matching, caching, settings defaults, complex orchestration) are left as
/// <see langword="virtual"/> so that <see cref="JIM.Application.SyncRepositoryAdapter"/>
/// can override them with the appropriate business logic.
/// </para>
/// </summary>
public class SyncRepository : ISyncRepository
{
    private readonly PostgresDataRepository _repo;

    public SyncRepository(PostgresDataRepository repo)
    {
        _repo = repo;
    }

    #region Connected System Object — Reads

    public Task<int> GetConnectedSystemObjectCountAsync(int connectedSystemId)
        => _repo.ConnectedSystems.GetConnectedSystemObjectCountAsync(connectedSystemId);

    public Task<int> GetConnectedSystemObjectModifiedSinceCountAsync(int connectedSystemId, DateTime modifiedSince)
        => _repo.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(connectedSystemId, modifiedSince);

    public Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(int connectedSystemId, int page, int pageSize)
        => _repo.ConnectedSystems.GetConnectedSystemObjectsAsync(connectedSystemId, page, pageSize);

    public Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsModifiedSinceAsync(
        int connectedSystemId, DateTime modifiedSince, int page, int pageSize)
        => _repo.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(connectedSystemId, modifiedSince, page, pageSize);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid csoId)
        => _repo.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, csoId);

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> to add CSO lookup caching.
    /// </summary>
    public virtual Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, int attributeValue)
        => _repo.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> to add CSO lookup caching.
    /// </summary>
    public virtual Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, string attributeValue)
        => _repo.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> to add CSO lookup caching.
    /// </summary>
    public virtual Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, Guid attributeValue)
        => _repo.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> to add CSO lookup caching.
    /// </summary>
    public virtual Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, long attributeValue)
        => _repo.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> to add conditional caching.
    /// </summary>
    public virtual Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAsync(
        int connectedSystemId, int objectTypeId, string secondaryExternalIdValue)
        => _repo.ConnectedSystems.GetConnectedSystemObjectBySecondaryExternalIdAsync(connectedSystemId, objectTypeId, secondaryExternalIdValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(
        int connectedSystemId, string secondaryExternalIdValue)
        => _repo.ConnectedSystems.GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(connectedSystemId, secondaryExternalIdValue);

    public Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsByAttributeValuesAsync(
        int connectedSystemId, int attributeId, IEnumerable<string> attributeValues)
        => _repo.ConnectedSystems.GetConnectedSystemObjectsByAttributeValuesAsync(connectedSystemId, attributeId, attributeValues);

    public Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(
        int connectedSystemId, IEnumerable<string> secondaryExternalIdValues)
        => _repo.ConnectedSystems.GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(connectedSystemId, secondaryExternalIdValues);

    public Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int objectTypeId)
        => _repo.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeIntAsync(connectedSystemId, objectTypeId);

    public Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int objectTypeId)
        => _repo.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeStringAsync(connectedSystemId, objectTypeId);

    public Task<List<Guid>> GetAllExternalIdAttributeValuesOfTypeGuidAsync(int connectedSystemId, int objectTypeId)
        => _repo.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeGuidAsync(connectedSystemId, objectTypeId);

    public Task<List<long>> GetAllExternalIdAttributeValuesOfTypeLongAsync(int connectedSystemId, int objectTypeId)
        => _repo.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeLongAsync(connectedSystemId, objectTypeId);

    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsForReferenceResolutionAsync(IList<Guid> csoIds)
        => _repo.ConnectedSystems.GetConnectedSystemObjectsForReferenceResolutionAsync(csoIds);

    public Task<Dictionary<Guid, string>> GetReferenceExternalIdsAsync(Guid csoId)
        => _repo.ConnectedSystems.GetReferenceExternalIdsAsync(csoId);

    public Task<int> GetConnectedSystemObjectCountByMetaverseObjectIdAsync(Guid metaverseObjectId)
        => _repo.ConnectedSystems.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(metaverseObjectId);

    public Task<int> GetConnectedSystemObjectCountByMvoAsync(int connectedSystemId, Guid metaverseObjectId)
        => _repo.ConnectedSystems.GetConnectedSystemObjectCountByMvoAsync(connectedSystemId, metaverseObjectId);

    #endregion

    #region Connected System Object — Writes

    public Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
        => _repo.ConnectedSystems.CreateConnectedSystemObjectsAsync(connectedSystemObjects);

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> to add RPEI linking and change tracking.
    /// </summary>
    public virtual Task CreateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis,
        Func<int, Task>? onBatchPersisted = null)
        => _repo.ConnectedSystems.CreateConnectedSystemObjectsAsync(connectedSystemObjects, onBatchPersisted);

    public Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
        => _repo.ConnectedSystems.UpdateConnectedSystemObjectsAsync(connectedSystemObjects);

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> to add RPEI linking and change tracking.
    /// </summary>
    public virtual Task UpdateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis)
        => _repo.ConnectedSystems.UpdateConnectedSystemObjectsAsync(connectedSystemObjects);

    public Task UpdateConnectedSystemObjectJoinStatesAsync(List<ConnectedSystemObject> connectedSystemObjects)
        => _repo.ConnectedSystems.UpdateConnectedSystemObjectJoinStatesAsync(connectedSystemObjects);

    public Task UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(
        List<(ConnectedSystemObject cso, List<ConnectedSystemObjectAttributeValue> newAttributeValues)> updates)
        => _repo.ConnectedSystems.UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(updates);

    public Task DeleteConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
    {
        Log.Debug("DeleteConnectedSystemObjectsAsync: Quietly batch deleted {Count} CSOs (no RPEI)", connectedSystemObjects.Count);
        return _repo.ConnectedSystems.DeleteConnectedSystemObjectsAsync(connectedSystemObjects);
    }

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> to capture attribute snapshots
    /// on RPEIs before deletion for audit trail.
    /// </summary>
    public virtual Task DeleteConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis)
        => _repo.ConnectedSystems.DeleteConnectedSystemObjectsAsync(connectedSystemObjects);

    public Task<int> FixupCrossBatchReferenceIdsAsync(int connectedSystemId)
        => _repo.ConnectedSystems.FixupCrossBatchReferenceIdsAsync(connectedSystemId);

    #endregion

    #region Metaverse Object — Reads

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with matching rule evaluation logic.
    /// </summary>
    public virtual Task<MetaverseObject?> FindMatchingMetaverseObjectAsync(ConnectedSystemObject cso, List<ObjectMatchingRule> matchingRules)
        => throw new NotSupportedException("FindMatchingMetaverseObjectAsync requires Application-layer object matching logic. Use SyncRepositoryAdapter.");

    #endregion

    #region Metaverse Object — Writes

    public Task CreateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        => _repo.Metaverse.CreateMetaverseObjectsAsync(metaverseObjects);

    public Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        => _repo.Metaverse.UpdateMetaverseObjectsAsync(metaverseObjects);

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> to add MVO change tracking.
    /// </summary>
    public virtual Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
        => _repo.Metaverse.UpdateMetaverseObjectAsync(metaverseObject);

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with cascade deletion
    /// and change tracking logic.
    /// </summary>
    public virtual Task DeleteMetaverseObjectAsync(
        MetaverseObject metaverseObject,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName,
        List<MetaverseObjectAttributeValue>? finalAttributeValues)
        => throw new NotSupportedException("DeleteMetaverseObjectAsync requires Application-layer cascade and change tracking logic. Use SyncRepositoryAdapter.");

    #endregion

    #region Pending Exports

    public Task<List<PendingExport>> GetPendingExportsAsync(int connectedSystemId)
        => _repo.ConnectedSystems.GetPendingExportsAsync(connectedSystemId);

    public Task<int> GetPendingExportsCountAsync(int connectedSystemId)
        => _repo.ConnectedSystems.GetPendingExportsCountAsync(connectedSystemId);

    public Task CreatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
        => _repo.ConnectedSystems.CreatePendingExportsAsync(pendingExports);

    public Task DeletePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
        => _repo.ConnectedSystems.DeletePendingExportsAsync(pendingExports);

    public Task UpdatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
        => _repo.ConnectedSystems.UpdatePendingExportsAsync(pendingExports);

    public Task<int> DeletePendingExportsByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds)
        => _repo.ConnectedSystems.DeletePendingExportsByConnectedSystemObjectIdsAsync(connectedSystemObjectIds);

    public Task<PendingExport?> GetPendingExportByConnectedSystemObjectIdAsync(Guid connectedSystemObjectId)
        => _repo.ConnectedSystems.GetPendingExportByConnectedSystemObjectIdAsync(connectedSystemObjectId);

    public Task<Dictionary<Guid, PendingExport>> GetPendingExportsByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds)
        => _repo.ConnectedSystems.GetPendingExportsByConnectedSystemObjectIdsAsync(connectedSystemObjectIds);

    public Task<Dictionary<Guid, PendingExport>> GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds)
        => _repo.ConnectedSystems.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(connectedSystemObjectIds);

    public Task<HashSet<Guid>> GetCsoIdsWithPendingExportsByConnectedSystemAsync(int connectedSystemId)
        => _repo.ConnectedSystems.GetCsoIdsWithPendingExportsByConnectedSystemAsync(connectedSystemId);

    public Task DeleteUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports)
        => _repo.ConnectedSystems.DeleteUntrackedPendingExportsAsync(untrackedPendingExports);

    public Task DeleteUntrackedPendingExportAttributeValueChangesAsync(IEnumerable<PendingExportAttributeValueChange> untrackedAttributeValueChanges)
        => _repo.ConnectedSystems.DeleteUntrackedPendingExportAttributeValueChangesAsync(untrackedAttributeValueChanges);

    public Task UpdateUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports)
        => _repo.ConnectedSystems.UpdateUntrackedPendingExportsAsync(untrackedPendingExports);

    #endregion

    #region Activity and RPEIs

    public Task UpdateActivityAsync(Activity activity)
        => _repo.Activity.UpdateActivityAsync(activity);

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> to set the message then persist.
    /// </summary>
    public virtual Task UpdateActivityMessageAsync(Activity activity, string message)
    {
        activity.Message = message;
        return _repo.Activity.UpdateActivityAsync(activity);
    }

    public Task UpdateActivityProgressOutOfBandAsync(Activity activity)
        => _repo.Activity.UpdateActivityProgressOutOfBandAsync(activity);

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with timing calculation
    /// and error formatting logic.
    /// </summary>
    public virtual Task FailActivityWithErrorAsync(Activity activity, string errorMessage)
        => throw new NotSupportedException("FailActivityWithErrorAsync requires Application-layer timing and error formatting logic. Use SyncRepositoryAdapter.");

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with exception unwrapping,
    /// timing calculation, and stack trace capture logic.
    /// </summary>
    public virtual Task FailActivityWithErrorAsync(Activity activity, Exception exception)
        => throw new NotSupportedException("FailActivityWithErrorAsync requires Application-layer timing and error formatting logic. Use SyncRepositoryAdapter.");

    public Task<bool> BulkInsertRpeisAsync(List<ActivityRunProfileExecutionItem> rpeis)
        => _repo.Activity.BulkInsertRpeisAsync(rpeis);

    public Task BulkUpdateRpeiOutcomesAsync(
        List<ActivityRunProfileExecutionItem> rpeis,
        List<ActivityRunProfileExecutionItemSyncOutcome> newOutcomes)
        => _repo.Activity.BulkUpdateRpeiOutcomesAsync(rpeis, newOutcomes);

    public void DetachRpeisFromChangeTracker(List<ActivityRunProfileExecutionItem> rpeis)
        => _repo.Activity.DetachRpeisFromChangeTracker(rpeis);

    public Task<(int TotalWithErrors, int TotalRpeis, int TotalUnhandledErrors)> GetActivityRpeiErrorCountsAsync(Guid activityId)
        => _repo.Activity.GetActivityRpeiErrorCountsAsync(activityId);

    public Task PersistRpeiCsoChangesAsync(List<ActivityRunProfileExecutionItem> rpeis)
        => _repo.Activity.PersistRpeiCsoChangesAsync(rpeis);

    #endregion

    #region Sync Rules and Configuration

    public Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabled)
        => _repo.ConnectedSystems.GetSyncRulesAsync(connectedSystemId, includeDisabled);

    public Task<List<SyncRule>> GetAllSyncRulesAsync()
        => _repo.ConnectedSystems.GetSyncRulesAsync();

    public Task<List<ConnectedSystemObjectType>> GetObjectTypesAsync(int connectedSystemId)
        => _repo.ConnectedSystems.GetObjectTypesAsync(connectedSystemId);

    public Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem)
        => _repo.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);

    #endregion

    #region Settings

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with default value logic.
    /// </summary>
    public virtual Task<int> GetSyncPageSizeAsync()
        => throw new NotSupportedException("GetSyncPageSizeAsync requires Application-layer setting defaults. Use SyncRepositoryAdapter.");

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with default value logic.
    /// </summary>
    public virtual Task<ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel> GetSyncOutcomeTrackingLevelAsync()
        => throw new NotSupportedException("GetSyncOutcomeTrackingLevelAsync requires Application-layer setting defaults. Use SyncRepositoryAdapter.");

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with default value logic.
    /// </summary>
    public virtual Task<bool> GetCsoChangeTrackingEnabledAsync()
        => throw new NotSupportedException("GetCsoChangeTrackingEnabledAsync requires Application-layer setting defaults. Use SyncRepositoryAdapter.");

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with default value logic.
    /// </summary>
    public virtual Task<bool> GetMvoChangeTrackingEnabledAsync()
        => throw new NotSupportedException("GetMvoChangeTrackingEnabledAsync requires Application-layer setting defaults. Use SyncRepositoryAdapter.");

    #endregion

    #region Change Tracker Management

    public void ClearChangeTracker()
        => _repo.ClearChangeTracker();

    public void SetAutoDetectChangesEnabled(bool enabled)
        => _repo.SetAutoDetectChangesEnabled(enabled);

    #endregion

    #region CSO Lookup Cache

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with cache management logic.
    /// </summary>
    public virtual void AddCsoToCache(int connectedSystemId, int externalIdAttributeId, string externalIdValue, Guid csoId)
    {
        // No-op at the data layer — caching is an Application-layer concern
    }

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with cache management logic.
    /// </summary>
    public virtual void EvictCsoFromCache(int connectedSystemId, int externalIdAttributeId, string externalIdValue)
    {
        // No-op at the data layer — caching is an Application-layer concern
    }

    #endregion

    #region Connected System Operations

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with container hierarchy,
    /// partition management, and activity tracking logic.
    /// </summary>
    public virtual Task RefreshAndAutoSelectContainersWithTriadAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        IReadOnlyList<string> createdContainerExternalIds,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName,
        Activity? parentActivity = null)
        => throw new NotSupportedException("RefreshAndAutoSelectContainersWithTriadAsync requires Application-layer orchestration. Use SyncRepositoryAdapter.");

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with validation,
    /// audit tracking, and activity creation logic.
    /// </summary>
    public virtual Task UpdateConnectedSystemWithTriadAsync(
        ConnectedSystem connectedSystem,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
        => throw new NotSupportedException("UpdateConnectedSystemWithTriadAsync requires Application-layer orchestration. Use SyncRepositoryAdapter.");

    #endregion

    #region MVO Change History

    public Task CreateMetaverseObjectChangeDirectAsync(MetaverseObjectChange change)
        => _repo.Metaverse.CreateMetaverseObjectChangeDirectAsync(change);

    #endregion

    #region Connected System Object — Singular Convenience Methods

    public Task CreateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
        => _repo.ConnectedSystems.CreateConnectedSystemObjectAsync(connectedSystemObject);

    public Task UpdateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
        => _repo.ConnectedSystems.UpdateConnectedSystemObjectAsync(connectedSystemObject);

    public Task UpdateConnectedSystemObjectWithNewAttributeValuesAsync(
        ConnectedSystemObject connectedSystemObject,
        List<ConnectedSystemObjectAttributeValue> newAttributeValues)
        => _repo.ConnectedSystems.UpdateConnectedSystemObjectWithNewAttributeValuesAsync(
            connectedSystemObject, newAttributeValues);

    #endregion

    #region Pending Export — Singular Convenience Methods

    public Task CreatePendingExportAsync(PendingExport pendingExport)
        => _repo.ConnectedSystems.CreatePendingExportAsync(pendingExport);

    public Task DeletePendingExportAsync(PendingExport pendingExport)
        => _repo.ConnectedSystems.DeletePendingExportAsync(pendingExport);

    public Task UpdatePendingExportAsync(PendingExport pendingExport)
        => _repo.ConnectedSystems.UpdatePendingExportAsync(pendingExport);

    #endregion

    #region Export Evaluation Support

    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdAsync(Guid metaverseObjectId)
        => _repo.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdAsync(metaverseObjectId);

    public Task<Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>> GetConnectedSystemObjectsByTargetSystemsAsync(
        IEnumerable<int> targetConnectedSystemIds)
        => _repo.ConnectedSystems.GetConnectedSystemObjectsByTargetSystemsAsync(targetConnectedSystemIds);

    public Task<List<ConnectedSystemObjectAttributeValue>> GetCsoAttributeValuesByCsoIdsAsync(IEnumerable<Guid> csoIds)
        => _repo.ConnectedSystems.GetCsoAttributeValuesByCsoIdsAsync(csoIds);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByMetaverseObjectIdAsync(Guid metaverseObjectId, int connectedSystemId)
        => _repo.ConnectedSystems.GetConnectedSystemObjectByMetaverseObjectIdAsync(metaverseObjectId, connectedSystemId);

    public Task<Dictionary<Guid, ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdsAsync(
        IEnumerable<Guid> metaverseObjectIds, int connectedSystemId)
        => _repo.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(metaverseObjectIds, connectedSystemId);

    public Task<ConnectedSystemObjectTypeAttribute?> GetAttributeAsync(int id)
        => _repo.ConnectedSystems.GetAttributeAsync(id);

    public Task<Dictionary<int, ConnectedSystemObjectTypeAttribute>> GetAttributesByIdsAsync(IEnumerable<int> ids)
        => _repo.ConnectedSystems.GetAttributesByIdsAsync(ids);

    /// <summary>
    /// Overridden in <see cref="JIM.Application.SyncRepositoryAdapter"/> with matching rule evaluation logic.
    /// </summary>
    public virtual Task<ConnectedSystemObject?> FindMatchingConnectedSystemObjectAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        List<ObjectMatchingRule> matchingRules)
        => throw new NotSupportedException("FindMatchingConnectedSystemObjectAsync requires Application-layer object matching logic. Use SyncRepositoryAdapter.");

    #endregion

    #region Export Execution Support

    public Task<int> GetExecutableExportCountAsync(int connectedSystemId)
        => _repo.ConnectedSystems.GetExecutableExportCountAsync(connectedSystemId);

    public Task<List<PendingExport>> GetExecutableExportsAsync(int connectedSystemId)
        => _repo.ConnectedSystems.GetExecutableExportsAsync(connectedSystemId);

    public Task<List<PendingExport>> GetExecutableExportBatchAsync(int connectedSystemId, int skip, int take)
        => _repo.ConnectedSystems.GetExecutableExportBatchAsync(connectedSystemId, skip, take);

    public Task MarkPendingExportsAsExecutingAsync(IList<PendingExport> pendingExports)
        => _repo.ConnectedSystems.MarkPendingExportsAsExecutingAsync(pendingExports);

    public Task<List<PendingExport>> GetPendingExportsByIdsAsync(IList<Guid> pendingExportIds)
        => _repo.ConnectedSystems.GetPendingExportsByIdsAsync(pendingExportIds);

    #endregion
}
