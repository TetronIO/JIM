using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using Serilog;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Production implementation of <see cref="ISyncRepository"/> for the Worker.
/// <para>
/// Delegates reads and simple writes to the shared EF Core repositories
/// (<see cref="PostgresDataRepository"/>). Hot-path bulk operations use direct SQL
/// via the raw SQL methods already in <c>ConnectedSystemRepository</c> and
/// <c>ActivitiesRepository</c>.
/// </para>
/// <para>
/// In a future phase, the hot-path methods will be migrated from the shared repositories
/// into this class with Worker-optimised direct SQL (COPY binary, batch UPDATE FROM VALUES).
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

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, int attributeValue)
        => _repo.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, string attributeValue)
        => _repo.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, Guid attributeValue)
        => _repo.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, long attributeValue)
        => _repo.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAsync(
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

    public Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
        => _repo.ConnectedSystems.UpdateConnectedSystemObjectsAsync(connectedSystemObjects);

    public Task UpdateConnectedSystemObjectJoinStatesAsync(List<ConnectedSystemObject> connectedSystemObjects)
        => _repo.ConnectedSystems.UpdateConnectedSystemObjectJoinStatesAsync(connectedSystemObjects);

    public Task UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(
        List<(ConnectedSystemObject cso, List<ConnectedSystemObjectAttributeValue> newAttributeValues)> updates)
        => _repo.ConnectedSystems.UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(updates);

    public Task DeleteConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
        => _repo.ConnectedSystems.DeleteConnectedSystemObjectsAsync(connectedSystemObjects);

    public Task<int> FixupCrossBatchReferenceIdsAsync(int connectedSystemId)
        => _repo.ConnectedSystems.FixupCrossBatchReferenceIdsAsync(connectedSystemId);

    public Task<int> FixupCrossBatchChangeRecordReferenceIdsAsync(int connectedSystemId)
        => _repo.ConnectedSystems.FixupCrossBatchChangeRecordReferenceIdsAsync(connectedSystemId);

    #endregion

    #region Object Matching — Data Access

    public Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(
        ConnectedSystemObject connectedSystemObject, MetaverseObjectType metaverseObjectType, ObjectMatchingRule objectMatchingRule)
        => _repo.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(connectedSystemObject, metaverseObjectType, objectMatchingRule);

    public Task<ConnectedSystemObject?> FindConnectedSystemObjectUsingMatchingRuleAsync(
        MetaverseObject metaverseObject, ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType, ObjectMatchingRule objectMatchingRule)
        => _repo.ConnectedSystems.FindConnectedSystemObjectUsingMatchingRuleAsync(metaverseObject, connectedSystem, connectedSystemObjectType, objectMatchingRule);

    #endregion

    #region Metaverse Object — Writes

    public Task CreateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        => _repo.Metaverse.CreateMetaverseObjectsAsync(metaverseObjects);

    public Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        => _repo.Metaverse.UpdateMetaverseObjectsAsync(metaverseObjects);

    public Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
        => _repo.Metaverse.UpdateMetaverseObjectAsync(metaverseObject);

    public Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject)
        => _repo.Metaverse.DeleteMetaverseObjectAsync(metaverseObject);

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

    public async Task UpdateActivityMessageAsync(Activity activity, string message)
    {
        activity.Message = message;
        await _repo.Activity.UpdateActivityAsync(activity);
    }

    public Task UpdateActivityProgressOutOfBandAsync(Activity activity)
        => _repo.Activity.UpdateActivityProgressOutOfBandAsync(activity);

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

    #region Change Tracker Management

    public void ClearChangeTracker()
        => _repo.ClearChangeTracker();

    public void SetAutoDetectChangesEnabled(bool enabled)
        => _repo.SetAutoDetectChangesEnabled(enabled);

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
        => _repo.ConnectedSystems.UpdateConnectedSystemObjectWithNewAttributeValuesAsync(connectedSystemObject, newAttributeValues);

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
