using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;

namespace JIM.Application;

/// <summary>
/// Adapter that implements <see cref="ISyncRepository"/> by delegating to the existing
/// <see cref="JimApplication"/> server methods and repository interfaces.
/// <para>
/// This is the production implementation for the transitional period while sync processors
/// are being migrated from direct <c>_jim.*</c> calls to <c>ISyncRepository</c>.
/// It preserves all existing business logic (CSO change tracking, RPEI linking, etc.)
/// by routing through the application-layer servers rather than bypassing them.
/// </para>
/// <para>
/// In a future phase, this adapter will be replaced by a direct <c>SyncRepository</c>
/// in JIM.PostgresData that uses raw SQL/Npgsql for all operations.
/// </para>
/// </summary>
public class SyncRepositoryAdapter : ISyncRepository
{
    private readonly JimApplication _jim;

    public SyncRepositoryAdapter(JimApplication jim)
    {
        _jim = jim;
    }

    #region Connected System Object — Reads

    public Task<int> GetConnectedSystemObjectCountAsync(int connectedSystemId)
        => _jim.ConnectedSystems.GetConnectedSystemObjectCountAsync(connectedSystemId);

    public Task<int> GetConnectedSystemObjectModifiedSinceCountAsync(int connectedSystemId, DateTime modifiedSince)
        => _jim.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(connectedSystemId, modifiedSince);

    public Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(int connectedSystemId, int page, int pageSize)
        => _jim.ConnectedSystems.GetConnectedSystemObjectsAsync(connectedSystemId, page, pageSize);

    public Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsModifiedSinceAsync(
        int connectedSystemId, DateTime modifiedSince, int page, int pageSize)
        => _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(connectedSystemId, modifiedSince, page, pageSize);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid csoId)
        => _jim.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, csoId);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, int attributeValue)
        => _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, string attributeValue)
        => _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, Guid attributeValue)
        => _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, long attributeValue)
        => _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAsync(
        int connectedSystemId, int objectTypeId, string secondaryExternalIdValue)
        => _jim.ConnectedSystems.GetConnectedSystemObjectBySecondaryExternalIdAsync(connectedSystemId, objectTypeId, secondaryExternalIdValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(
        int connectedSystemId, string secondaryExternalIdValue)
        => _jim.ConnectedSystems.GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(connectedSystemId, secondaryExternalIdValue);

    public Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsByAttributeValuesAsync(
        int connectedSystemId, int attributeId, IEnumerable<string> attributeValues)
        => _jim.ConnectedSystems.GetConnectedSystemObjectsByAttributeValuesAsync(connectedSystemId, attributeId, attributeValues);

    public Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(
        int connectedSystemId, IEnumerable<string> secondaryExternalIdValues)
        => _jim.ConnectedSystems.GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(connectedSystemId, secondaryExternalIdValues);

    public Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int objectTypeId)
        => _jim.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeIntAsync(connectedSystemId, objectTypeId);

    public Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int objectTypeId)
        => _jim.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeStringAsync(connectedSystemId, objectTypeId);

    public Task<List<Guid>> GetAllExternalIdAttributeValuesOfTypeGuidAsync(int connectedSystemId, int objectTypeId)
        => _jim.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeGuidAsync(connectedSystemId, objectTypeId);

    public Task<List<long>> GetAllExternalIdAttributeValuesOfTypeLongAsync(int connectedSystemId, int objectTypeId)
        => _jim.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeLongAsync(connectedSystemId, objectTypeId);

    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsForReferenceResolutionAsync(IList<Guid> csoIds)
        => _jim.ConnectedSystems.GetConnectedSystemObjectsForReferenceResolutionAsync(csoIds);

    public Task<Dictionary<Guid, string>> GetReferenceExternalIdsAsync(Guid csoId)
        => _jim.ConnectedSystems.GetReferenceExternalIdsAsync(csoId);

    public Task<int> GetConnectedSystemObjectCountByMetaverseObjectIdAsync(Guid metaverseObjectId)
        => _jim.ConnectedSystems.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(metaverseObjectId);

    public Task<int> GetConnectedSystemObjectCountByMvoAsync(int connectedSystemId, Guid metaverseObjectId)
        => _jim.ConnectedSystems.GetConnectedSystemObjectCountByMvoAsync(connectedSystemId, metaverseObjectId);

    #endregion

    #region Connected System Object — Writes

    public Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
        => _jim.ConnectedSystems.CreateConnectedSystemObjectsAsync(connectedSystemObjects);

    public Task CreateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis,
        Func<int, Task>? onBatchPersisted = null)
        => _jim.ConnectedSystems.CreateConnectedSystemObjectsAsync(connectedSystemObjects, rpeis, onBatchPersisted);

    public Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
        => _jim.Repository.ConnectedSystems.UpdateConnectedSystemObjectsAsync(connectedSystemObjects);

    public Task UpdateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis)
        => _jim.ConnectedSystems.UpdateConnectedSystemObjectsAsync(connectedSystemObjects, rpeis);

    public Task UpdateConnectedSystemObjectJoinStatesAsync(List<ConnectedSystemObject> connectedSystemObjects)
        => _jim.ConnectedSystems.UpdateConnectedSystemObjectJoinStatesAsync(connectedSystemObjects);

    public Task UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(
        List<(ConnectedSystemObject cso, List<ConnectedSystemObjectAttributeValue> newAttributeValues)> updates)
        => _jim.Repository.ConnectedSystems.UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(updates);

    public Task DeleteConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
        => _jim.ConnectedSystems.DeleteConnectedSystemObjectsAsync(connectedSystemObjects);

    public Task DeleteConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis)
        => _jim.ConnectedSystems.DeleteConnectedSystemObjectsAsync(connectedSystemObjects, rpeis);

    public Task<int> FixupCrossBatchReferenceIdsAsync(int connectedSystemId)
        => _jim.ConnectedSystems.FixupCrossBatchReferenceIdsAsync(connectedSystemId);

    #endregion

    #region Object Matching — Data Access

    public Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(
        ConnectedSystemObject connectedSystemObject, MetaverseObjectType metaverseObjectType, ObjectMatchingRule objectMatchingRule)
        => _jim.Repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(connectedSystemObject, metaverseObjectType, objectMatchingRule);

    public Task<ConnectedSystemObject?> FindConnectedSystemObjectUsingMatchingRuleAsync(
        MetaverseObject metaverseObject, ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType, ObjectMatchingRule objectMatchingRule)
        => _jim.Repository.ConnectedSystems.FindConnectedSystemObjectUsingMatchingRuleAsync(metaverseObject, connectedSystem, connectedSystemObjectType, objectMatchingRule);

    #endregion

    #region Metaverse Object — Reads

    public Task<MetaverseObject?> FindMatchingMetaverseObjectAsync(ConnectedSystemObject cso, List<ObjectMatchingRule> matchingRules)
        => _jim.ObjectMatching.FindMatchingMetaverseObjectAsync(cso, matchingRules);

    #endregion

    #region Metaverse Object — Writes

    public Task CreateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        => _jim.Metaverse.CreateMetaverseObjectsAsync(metaverseObjects);

    public Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        => _jim.Metaverse.UpdateMetaverseObjectsAsync(metaverseObjects);

    public Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
        => _jim.Metaverse.UpdateMetaverseObjectAsync(metaverseObject);

    public Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject)
        => _jim.Repository.Metaverse.DeleteMetaverseObjectAsync(metaverseObject);

    #endregion

    #region Pending Exports

    public Task<List<PendingExport>> GetPendingExportsAsync(int connectedSystemId)
        => _jim.ConnectedSystems.GetPendingExportsAsync(connectedSystemId);

    public Task<int> GetPendingExportsCountAsync(int connectedSystemId)
        => _jim.ConnectedSystems.GetPendingExportsCountAsync(connectedSystemId);

    public Task CreatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
        => _jim.ConnectedSystems.CreatePendingExportsAsync(pendingExports);

    public Task DeletePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
        => _jim.ConnectedSystems.DeletePendingExportsAsync(pendingExports);

    public Task UpdatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
        => _jim.ConnectedSystems.UpdatePendingExportsAsync(pendingExports);

    public Task<int> DeletePendingExportsByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds)
        => _jim.ConnectedSystems.DeletePendingExportsByConnectedSystemObjectIdsAsync(connectedSystemObjectIds);

    public Task<PendingExport?> GetPendingExportByConnectedSystemObjectIdAsync(Guid connectedSystemObjectId)
        => _jim.Repository.ConnectedSystems.GetPendingExportByConnectedSystemObjectIdAsync(connectedSystemObjectId);

    public Task<Dictionary<Guid, PendingExport>> GetPendingExportsByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds)
        => _jim.Repository.ConnectedSystems.GetPendingExportsByConnectedSystemObjectIdsAsync(connectedSystemObjectIds);

    public Task<Dictionary<Guid, PendingExport>> GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds)
        => _jim.Repository.ConnectedSystems.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(connectedSystemObjectIds);

    public Task<HashSet<Guid>> GetCsoIdsWithPendingExportsByConnectedSystemAsync(int connectedSystemId)
        => _jim.Repository.ConnectedSystems.GetCsoIdsWithPendingExportsByConnectedSystemAsync(connectedSystemId);

    public Task DeleteUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports)
        => _jim.Repository.ConnectedSystems.DeleteUntrackedPendingExportsAsync(untrackedPendingExports);

    public Task DeleteUntrackedPendingExportAttributeValueChangesAsync(IEnumerable<PendingExportAttributeValueChange> untrackedAttributeValueChanges)
        => _jim.Repository.ConnectedSystems.DeleteUntrackedPendingExportAttributeValueChangesAsync(untrackedAttributeValueChanges);

    public Task UpdateUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports)
        => _jim.Repository.ConnectedSystems.UpdateUntrackedPendingExportsAsync(untrackedPendingExports);

    #endregion

    #region Activity and RPEIs

    public Task UpdateActivityAsync(Activity activity)
        => _jim.Activities.UpdateActivityAsync(activity);

    public Task UpdateActivityMessageAsync(Activity activity, string message)
        => _jim.Activities.UpdateActivityMessageAsync(activity, message);

    public Task UpdateActivityProgressOutOfBandAsync(Activity activity)
        => _jim.Activities.UpdateActivityProgressOutOfBandAsync(activity);

    public Task FailActivityWithErrorAsync(Activity activity, string errorMessage)
        => _jim.Activities.FailActivityWithErrorAsync(activity, errorMessage);

    public Task FailActivityWithErrorAsync(Activity activity, Exception exception)
        => _jim.Activities.FailActivityWithErrorAsync(activity, exception);

    public Task<bool> BulkInsertRpeisAsync(List<ActivityRunProfileExecutionItem> rpeis)
        => _jim.Activities.BulkInsertRpeisAsync(rpeis);

    public Task BulkUpdateRpeiOutcomesAsync(
        List<ActivityRunProfileExecutionItem> rpeis,
        List<ActivityRunProfileExecutionItemSyncOutcome> newOutcomes)
        => _jim.Activities.BulkUpdateRpeiOutcomesAsync(rpeis, newOutcomes);

    public void DetachRpeisFromChangeTracker(List<ActivityRunProfileExecutionItem> rpeis)
        => _jim.Activities.DetachRpeisFromChangeTracker(rpeis);

    public Task<(int TotalWithErrors, int TotalRpeis, int TotalUnhandledErrors)> GetActivityRpeiErrorCountsAsync(Guid activityId)
        => _jim.Activities.GetActivityRpeiErrorCountsAsync(activityId);

    public Task PersistRpeiCsoChangesAsync(List<ActivityRunProfileExecutionItem> rpeis)
        => _jim.Activities.PersistRpeiCsoChangesAsync(rpeis);

    #endregion

    #region Sync Rules and Configuration

    public Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabled)
        => _jim.ConnectedSystems.GetSyncRulesAsync(connectedSystemId, includeDisabled);

    public Task<List<SyncRule>> GetAllSyncRulesAsync()
        => _jim.ConnectedSystems.GetSyncRulesAsync();

    public Task<List<ConnectedSystemObjectType>> GetObjectTypesAsync(int connectedSystemId)
        => _jim.ConnectedSystems.GetObjectTypesAsync(connectedSystemId);

    public Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem)
        => _jim.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);

    #endregion

    #region Settings

    public Task<int> GetSyncPageSizeAsync()
        => _jim.ServiceSettings.GetSyncPageSizeAsync();

    public Task<ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel> GetSyncOutcomeTrackingLevelAsync()
        => _jim.ServiceSettings.GetSyncOutcomeTrackingLevelAsync();

    public Task<bool> GetCsoChangeTrackingEnabledAsync()
        => _jim.ServiceSettings.GetCsoChangeTrackingEnabledAsync();

    public Task<bool> GetMvoChangeTrackingEnabledAsync()
        => _jim.ServiceSettings.GetMvoChangeTrackingEnabledAsync();

    #endregion

    #region Change Tracker Management

    public void ClearChangeTracker()
        => _jim.Repository.ClearChangeTracker();

    public void SetAutoDetectChangesEnabled(bool enabled)
        => _jim.Repository.SetAutoDetectChangesEnabled(enabled);

    #endregion

    #region CSO Lookup Cache

    public void AddCsoToCache(int connectedSystemId, int externalIdAttributeId, string externalIdValue, Guid csoId)
        => _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, externalIdAttributeId, externalIdValue, csoId);

    public void EvictCsoFromCache(int connectedSystemId, int externalIdAttributeId, string externalIdValue)
        => _jim.ConnectedSystems.EvictCsoFromCache(connectedSystemId, externalIdAttributeId, externalIdValue);

    #endregion

    #region Connected System Operations

    public Task RefreshAndAutoSelectContainersWithTriadAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        IReadOnlyList<string> createdContainerExternalIds,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName,
        Activity? parentActivity = null)
        => _jim.ConnectedSystems.RefreshAndAutoSelectContainersWithTriadAsync(
            connectedSystem, connector, createdContainerExternalIds,
            initiatorType, initiatorId, initiatorName, parentActivity);

    public Task UpdateConnectedSystemWithTriadAsync(
        ConnectedSystem connectedSystem,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
        => _jim.ConnectedSystems.UpdateConnectedSystemWithTriadAsync(
            connectedSystem, initiatorType, initiatorId, initiatorName);

    #endregion

    #region MVO Change History

    public Task CreateMetaverseObjectChangeDirectAsync(MetaverseObjectChange change)
        => _jim.Repository.Metaverse.CreateMetaverseObjectChangeDirectAsync(change);

    #endregion

    #region Connected System Object — Singular Convenience Methods

    public Task CreateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
        => _jim.Repository.ConnectedSystems.CreateConnectedSystemObjectAsync(connectedSystemObject);

    public Task UpdateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
        => _jim.Repository.ConnectedSystems.UpdateConnectedSystemObjectAsync(connectedSystemObject);

    public Task UpdateConnectedSystemObjectWithNewAttributeValuesAsync(
        ConnectedSystemObject connectedSystemObject,
        List<ConnectedSystemObjectAttributeValue> newAttributeValues)
        => _jim.Repository.ConnectedSystems.UpdateConnectedSystemObjectWithNewAttributeValuesAsync(
            connectedSystemObject, newAttributeValues);

    #endregion

    #region Pending Export — Singular Convenience Methods

    public Task CreatePendingExportAsync(PendingExport pendingExport)
        => _jim.Repository.ConnectedSystems.CreatePendingExportAsync(pendingExport);

    public Task DeletePendingExportAsync(PendingExport pendingExport)
        => _jim.Repository.ConnectedSystems.DeletePendingExportAsync(pendingExport);

    public Task UpdatePendingExportAsync(PendingExport pendingExport)
        => _jim.Repository.ConnectedSystems.UpdatePendingExportAsync(pendingExport);

    #endregion

    #region Export Evaluation Support

    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdAsync(Guid metaverseObjectId)
        => _jim.Repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdAsync(metaverseObjectId);

    public Task<Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>> GetConnectedSystemObjectsByTargetSystemsAsync(
        IEnumerable<int> targetConnectedSystemIds)
        => _jim.Repository.ConnectedSystems.GetConnectedSystemObjectsByTargetSystemsAsync(targetConnectedSystemIds);

    public Task<List<ConnectedSystemObjectAttributeValue>> GetCsoAttributeValuesByCsoIdsAsync(IEnumerable<Guid> csoIds)
        => _jim.Repository.ConnectedSystems.GetCsoAttributeValuesByCsoIdsAsync(csoIds);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByMetaverseObjectIdAsync(Guid metaverseObjectId, int connectedSystemId)
        => _jim.Repository.ConnectedSystems.GetConnectedSystemObjectByMetaverseObjectIdAsync(metaverseObjectId, connectedSystemId);

    public Task<Dictionary<Guid, ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdsAsync(
        IEnumerable<Guid> metaverseObjectIds, int connectedSystemId)
        => _jim.Repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(metaverseObjectIds, connectedSystemId);

    public Task<ConnectedSystemObjectTypeAttribute?> GetAttributeAsync(int id)
        => _jim.Repository.ConnectedSystems.GetAttributeAsync(id);

    public Task<Dictionary<int, ConnectedSystemObjectTypeAttribute>> GetAttributesByIdsAsync(IEnumerable<int> ids)
        => _jim.Repository.ConnectedSystems.GetAttributesByIdsAsync(ids);

    public Task<ConnectedSystemObject?> FindMatchingConnectedSystemObjectAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        List<ObjectMatchingRule> matchingRules)
        => _jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            metaverseObject, connectedSystem, connectedSystemObjectType, matchingRules);

    #endregion

    #region Export Execution Support

    public Task<int> GetExecutableExportCountAsync(int connectedSystemId)
        => _jim.Repository.ConnectedSystems.GetExecutableExportCountAsync(connectedSystemId);

    public Task<List<PendingExport>> GetExecutableExportsAsync(int connectedSystemId)
        => _jim.Repository.ConnectedSystems.GetExecutableExportsAsync(connectedSystemId);

    public Task<List<PendingExport>> GetExecutableExportBatchAsync(int connectedSystemId, int skip, int take)
        => _jim.Repository.ConnectedSystems.GetExecutableExportBatchAsync(connectedSystemId, skip, take);

    public Task MarkPendingExportsAsExecutingAsync(IList<PendingExport> pendingExports)
        => _jim.Repository.ConnectedSystems.MarkPendingExportsAsExecutingAsync(pendingExports);

    public Task<List<PendingExport>> GetPendingExportsByIdsAsync(IList<Guid> pendingExportIds)
        => _jim.Repository.ConnectedSystems.GetPendingExportsByIdsAsync(pendingExportIds);

    #endregion
}
