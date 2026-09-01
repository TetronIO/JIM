// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using JIM.Models.Sync;

namespace JIM.Data.Repositories;

/// <summary>
/// The read-only repository facade behind the synchronisation preview path (#288, PRD requirement 8):
/// one of the defence-in-depth layers around the zero-side-effect guarantee. Reads delegate to the
/// wrapped repository; any member that would persist a change throws
/// <see cref="PreviewWriteAttemptedException"/>, so an orchestration bug that reaches for a write fails
/// loudly instead of committing. Change-tracker state operations (detach, clear, auto-detect toggling)
/// persist nothing and delegate, so reused read paths that tidy the tracker keep working. Generated
/// mechanically from <see cref="ISyncRepository"/>; a new interface member fails compilation here until
/// it is consciously classified, and the guard test suite sweeps the classification.
/// </summary>
public sealed class ReadOnlySyncRepositoryGuard(ISyncRepository inner) : ISyncRepository
{
    private readonly ISyncRepository _inner = inner;

    #region Reads and tracker-state operations (delegated)

    public Task<int> GetConnectedSystemObjectCountAsync(int connectedSystemId, int? partitionId = null)
        => _inner.GetConnectedSystemObjectCountAsync(connectedSystemId, partitionId);

    public Task<int> GetConnectedSystemObjectModifiedSinceCountAsync(int connectedSystemId, DateTime modifiedSince)
        => _inner.GetConnectedSystemObjectModifiedSinceCountAsync(connectedSystemId, modifiedSince);

    public Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(int connectedSystemId, int page, int pageSize, int? knownTotalCount = null, DateTime? lastSyncTimestamp = null, Guid? afterId = null)
        => _inner.GetConnectedSystemObjectsAsync(connectedSystemId, page, pageSize, knownTotalCount, lastSyncTimestamp, afterId);

    public Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsModifiedSinceAsync(int connectedSystemId, DateTime modifiedSince, int page, int pageSize, int? knownTotalCount = null)
        => _inner.GetConnectedSystemObjectsModifiedSinceAsync(connectedSystemId, modifiedSince, page, pageSize, knownTotalCount);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid csoId)
        => _inner.GetConnectedSystemObjectAsync(connectedSystemId, csoId);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, int attributeValue)
        => _inner.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, string attributeValue)
        => _inner.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, Guid attributeValue)
        => _inner.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, long attributeValue)
        => _inner.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int attributeId, decimal attributeValue)
        => _inner.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAsync(int connectedSystemId, int objectTypeId, string secondaryExternalIdValue)
        => _inner.GetConnectedSystemObjectBySecondaryExternalIdAsync(connectedSystemId, objectTypeId, secondaryExternalIdValue);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(int connectedSystemId, string secondaryExternalIdValue)
        => _inner.GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(connectedSystemId, secondaryExternalIdValue);

    public Task<Dictionary<string, Guid>> GetAllCsoExternalIdMappingsAsync(int connectedSystemId)
        => _inner.GetAllCsoExternalIdMappingsAsync(connectedSystemId);

    public Task<Dictionary<string, CsoImportStateLookupEntry>> GetAllCsoImportStateLookupAsync(int connectedSystemId)
        => _inner.GetAllCsoImportStateLookupAsync(connectedSystemId);

    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByIdsAsync(int connectedSystemId, IEnumerable<Guid> csoIds)
        => _inner.GetConnectedSystemObjectsByIdsAsync(connectedSystemId, csoIds);

    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByIdsNoTrackingAsync(int connectedSystemId, IEnumerable<Guid> csoIds)
        => _inner.GetConnectedSystemObjectsByIdsNoTrackingAsync(connectedSystemId, csoIds);

    public Task<Dictionary<Guid, ConnectedSystemObjectDisplaySnapshot>> GetConnectedSystemObjectDisplaySnapshotsAsync(IReadOnlyCollection<Guid> csoIds)
        => _inner.GetConnectedSystemObjectDisplaySnapshotsAsync(csoIds);

    public Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsByAttributeValuesAsync(int connectedSystemId, int attributeId, IEnumerable<string> attributeValues)
        => _inner.GetConnectedSystemObjectsByAttributeValuesAsync(connectedSystemId, attributeId, attributeValues);

    public Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(int connectedSystemId, IEnumerable<string> secondaryExternalIdValues)
        => _inner.GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(connectedSystemId, secondaryExternalIdValues);

    public Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int objectTypeId, int? partitionId = null)
        => _inner.GetAllExternalIdAttributeValuesOfTypeIntAsync(connectedSystemId, objectTypeId, partitionId);

    public Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int objectTypeId, int? partitionId = null)
        => _inner.GetAllExternalIdAttributeValuesOfTypeStringAsync(connectedSystemId, objectTypeId, partitionId);

    public Task<List<Guid>> GetAllExternalIdAttributeValuesOfTypeGuidAsync(int connectedSystemId, int objectTypeId, int? partitionId = null)
        => _inner.GetAllExternalIdAttributeValuesOfTypeGuidAsync(connectedSystemId, objectTypeId, partitionId);

    public Task<List<long>> GetAllExternalIdAttributeValuesOfTypeLongAsync(int connectedSystemId, int objectTypeId, int? partitionId = null)
        => _inner.GetAllExternalIdAttributeValuesOfTypeLongAsync(connectedSystemId, objectTypeId, partitionId);

    public Task<List<decimal>> GetAllExternalIdAttributeValuesOfTypeDecimalAsync(int connectedSystemId, int objectTypeId, int? partitionId = null)
        => _inner.GetAllExternalIdAttributeValuesOfTypeDecimalAsync(connectedSystemId, objectTypeId, partitionId);

    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsForReferenceResolutionAsync(IList<Guid> csoIds)
        => _inner.GetConnectedSystemObjectsForReferenceResolutionAsync(csoIds);

    public Task<Dictionary<Guid, string>> GetReferenceExternalIdsAsync(Guid csoId)
        => _inner.GetReferenceExternalIdsAsync(csoId);

    public Task<Dictionary<Guid, Dictionary<Guid, string>>> GetReferenceExternalIdsForCsosAsync(IReadOnlyCollection<Guid> csoIds)
        => _inner.GetReferenceExternalIdsForCsosAsync(csoIds);

    public Task<List<int>> GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(Guid metaverseObjectId)
        => _inner.GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(metaverseObjectId);

    public Task<int> GetConnectedSystemObjectCountByMvoAsync(int connectedSystemId, Guid metaverseObjectId)
        => _inner.GetConnectedSystemObjectCountByMvoAsync(connectedSystemId, metaverseObjectId);

    public Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(ConnectedSystemObject connectedSystemObject, MetaverseObjectType metaverseObjectType, ObjectMatchingRule objectMatchingRule)
        => _inner.FindMetaverseObjectUsingMatchingRuleAsync(connectedSystemObject, metaverseObjectType, objectMatchingRule);

    public Task<ConnectedSystemObject?> FindConnectedSystemObjectUsingMatchingRuleAsync(MetaverseObject metaverseObject, ConnectedSystem connectedSystem, ConnectedSystemObjectType connectedSystemObjectType, ObjectMatchingRule objectMatchingRule)
        => _inner.FindConnectedSystemObjectUsingMatchingRuleAsync(metaverseObject, connectedSystem, connectedSystemObjectType, objectMatchingRule);

    public Task<List<Guid>> GetMetaverseObjectIdsWithScopeReviewPendingAsync(int maxResults)
        => _inner.GetMetaverseObjectIdsWithScopeReviewPendingAsync(maxResults);

    public Task<List<MetaverseObject>> GetMetaverseObjectsByIdsNoTrackingAsync(IEnumerable<Guid> ids)
        => _inner.GetMetaverseObjectsByIdsNoTrackingAsync(ids);

    public Task<List<MetaverseObject>> GetMetaverseObjectsByIdsForUpdateAsync(IEnumerable<Guid> ids)
        => _inner.GetMetaverseObjectsByIdsForUpdateAsync(ids);

    public Task<List<Guid>> GetMetaverseObjectIdsWithValuesContributedBySyncRuleAsync(int syncRuleId)
        => _inner.GetMetaverseObjectIdsWithValuesContributedBySyncRuleAsync(syncRuleId);

    public Task<List<Guid>> GetMetaverseObjectIdsWithStrandedValuesContributedBySyncRuleAsync(int syncRuleId, int connectedSystemId)
        => _inner.GetMetaverseObjectIdsWithStrandedValuesContributedBySyncRuleAsync(syncRuleId, connectedSystemId);

    public Task<Dictionary<Guid, string?>> GetMetaverseObjectDisplayNamesAsync(IReadOnlyCollection<Guid> ids)
        => _inner.GetMetaverseObjectDisplayNamesAsync(ids);

    public Task<List<MvoReferenceRecallCandidate>> GetMetaverseObjectReferenceRecallCandidatesAsync(IReadOnlyCollection<Guid> referencedMetaverseObjectIds)
        => _inner.GetMetaverseObjectReferenceRecallCandidatesAsync(referencedMetaverseObjectIds);

    public Task<List<MetaverseObjectRecallSummary>> GetMetaverseObjectRecallSummariesAsync(IReadOnlyCollection<Guid> metaverseObjectIds, IReadOnlyCollection<int> scopingAttributeIds)
        => _inner.GetMetaverseObjectRecallSummariesAsync(metaverseObjectIds, scopingAttributeIds);

    public Task<List<ConnectedSystemObjectRecallTarget>> GetConnectedSystemObjectRecallTargetsAsync(IReadOnlyCollection<Guid> metaverseObjectIds, IReadOnlyCollection<int> targetConnectedSystemIds)
        => _inner.GetConnectedSystemObjectRecallTargetsAsync(metaverseObjectIds, targetConnectedSystemIds);

    public Task<List<CsoReferenceValueMatch>> GetCsoReferenceValueMatchesAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds, IReadOnlyCollection<int> connectedSystemAttributeIds, IReadOnlyCollection<Guid> deletedReferenceCsoIds, IReadOnlyCollection<string> loweredReferenceValues)
        => _inner.GetCsoReferenceValueMatchesAsync(connectedSystemObjectIds, connectedSystemAttributeIds, deletedReferenceCsoIds, loweredReferenceValues);

    public Task<List<PendingExport>> GetPendingExportsAsync(int connectedSystemId)
        => _inner.GetPendingExportsAsync(connectedSystemId);

    public Task<List<PendingExport>> GetPendingExportsWithUnresolvedReferencesAsync(int connectedSystemId)
        => _inner.GetPendingExportsWithUnresolvedReferencesAsync(connectedSystemId);

    public Task<int> GetPendingExportsCountAsync(int connectedSystemId)
        => _inner.GetPendingExportsCountAsync(connectedSystemId);

    public Task<List<PendingInitialPassword>> GetOutstandingInitialPasswordsAsync(int connectedSystemId, int maximum)
        => _inner.GetOutstandingInitialPasswordsAsync(connectedSystemId, maximum);

    public Task<Dictionary<int, SyncRuleInitialPassword>> GetInitialPasswordConfigurationsAsync(IReadOnlyCollection<int> syncRuleIds)
        => _inner.GetInitialPasswordConfigurationsAsync(syncRuleIds);

    public Task<ConnectedSystemPasswordPolicy?> GetDiscoveredPasswordPolicyAsync(int connectedSystemId)
        => _inner.GetDiscoveredPasswordPolicyAsync(connectedSystemId);

    public Task<Dictionary<int, InitialPasswordAttention>> GetInitialPasswordAttentionBySyncRuleAsync(IReadOnlyCollection<int> syncRuleIds)
        => _inner.GetInitialPasswordAttentionBySyncRuleAsync(syncRuleIds);

    public Task<Dictionary<int, InitialPasswordAttention>> GetInitialPasswordAttentionByConnectedSystemAsync(IReadOnlyCollection<int> connectedSystemIds)
        => _inner.GetInitialPasswordAttentionByConnectedSystemAsync(connectedSystemIds);

    public Task<List<InitialPasswordRejection>> GetParkedInitialPasswordReasonsAsync(int syncRuleId)
        => _inner.GetParkedInitialPasswordReasonsAsync(syncRuleId);

    public Task<List<PendingPasswordChange>> GetDuePasswordChangesAsync(int connectedSystemId, DateTime asOf, int maximum)
        => _inner.GetDuePasswordChangesAsync(connectedSystemId, asOf, maximum);

    public Task<List<int>> GetConnectedSystemIdsWithDuePasswordChangesAsync(DateTime asOf)
        => _inner.GetConnectedSystemIdsWithDuePasswordChangesAsync(asOf);

    public Task<Dictionary<int, PasswordQueueAttention>> GetPasswordQueueAttentionAsync(IReadOnlyCollection<int> connectedSystemIds)
        => _inner.GetPasswordQueueAttentionAsync(connectedSystemIds);

    public Task<RangeResultSet<PendingPasswordChangeHeader>> GetPendingPasswordChangeHeadersAsync(
        PendingPasswordChangeFilter filter,
        int startIndex,
        int count,
        string sortBy,
        bool sortDescending,
        bool includeTotalCount)
        => _inner.GetPendingPasswordChangeHeadersAsync(filter, startIndex, count, sortBy, sortDescending, includeTotalCount);

    public Task<PasswordQueueSummary> GetPasswordQueueSummaryAsync(DateTime asOf)
        => _inner.GetPasswordQueueSummaryAsync(asOf);

    public Task<PendingExport?> GetPendingExportByConnectedSystemObjectIdAsync(Guid connectedSystemObjectId)
        => _inner.GetPendingExportByConnectedSystemObjectIdAsync(connectedSystemObjectId);

    public Task<PendingExport?> GetPendingExportLightweightByConnectedSystemObjectIdAsync(Guid connectedSystemObjectId)
        => _inner.GetPendingExportLightweightByConnectedSystemObjectIdAsync(connectedSystemObjectId);

    public Task<Dictionary<Guid, PendingExport>> GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds)
        => _inner.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(connectedSystemObjectIds);

    public Task<HashSet<Guid>> GetCsoIdsWithPendingExportsByConnectedSystemAsync(int connectedSystemId)
        => _inner.GetCsoIdsWithPendingExportsByConnectedSystemAsync(connectedSystemId);

    public Task<Dictionary<Guid, PendingExport>> GetPendingExportsLightweightByConnectedSystemIdAsync(int connectedSystemId, int? chunkSize = null)
        => _inner.GetPendingExportsLightweightByConnectedSystemIdAsync(connectedSystemId, chunkSize);

    public Task<List<CrossPageMergeRpei>> GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(Guid activityId, IReadOnlyCollection<Guid> csoIds)
        => _inner.GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(activityId, csoIds);

    public void DetachRpeisFromChangeTracker(List<ActivityRunProfileExecutionItem> rpeis)
        => _inner.DetachRpeisFromChangeTracker(rpeis);

    public Task<(int TotalWithErrors, int TotalRpeis, int TotalUnhandledErrors)> GetActivityRpeiErrorCountsAsync(Guid activityId)
        => _inner.GetActivityRpeiErrorCountsAsync(activityId);

    public Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabled, bool withChangeTracking = false)
        => _inner.GetSyncRulesAsync(connectedSystemId, includeDisabled, withChangeTracking);

    public Task<List<SyncRule>> GetAllSyncRulesAsync(bool withChangeTracking = false)
        => _inner.GetAllSyncRulesAsync(withChangeTracking);

    public Task<DateTime?> GetLatestSyncRuleConfigurationChangeAsync()
        => _inner.GetLatestSyncRuleConfigurationChangeAsync();

    public Task<HashSet<int>> GetSyncRuleIdsWithInitialPasswordEnabledAsync(IReadOnlyCollection<int> syncRuleIds)
        => _inner.GetSyncRuleIdsWithInitialPasswordEnabledAsync(syncRuleIds);

    public Task<List<ConnectedSystemObjectType>> GetObjectTypesAsync(int connectedSystemId)
        => _inner.GetObjectTypesAsync(connectedSystemId);

    public Task<Dictionary<int, string>> GetConnectedSystemNamesAsync()
        => _inner.GetConnectedSystemNamesAsync();

    public void ClearChangeTracker()
        => _inner.ClearChangeTracker();

    public int GetChangeTrackerEntityCount()
        => _inner.GetChangeTrackerEntityCount();

    public void DetachSchemaEntitiesFromTracker()
        => _inner.DetachSchemaEntitiesFromTracker();

    public void SetAutoDetectChangesEnabled(bool enabled)
        => _inner.SetAutoDetectChangesEnabled(enabled);

    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdAsync(Guid metaverseObjectId)
        => _inner.GetConnectedSystemObjectsByMetaverseObjectIdAsync(metaverseObjectId);

    public Task<Dictionary<Guid, List<ConnectedSystemObject>>> GetConnectedSystemObjectsForMvoDeletionAsync(IReadOnlyCollection<Guid> metaverseObjectIds)
        => _inner.GetConnectedSystemObjectsForMvoDeletionAsync(metaverseObjectIds);

    public Task<Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>> GetConnectedSystemObjectsByTargetSystemsAsync(IEnumerable<int> targetConnectedSystemIds)
        => _inner.GetConnectedSystemObjectsByTargetSystemsAsync(targetConnectedSystemIds);

    public Task<Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>> GetConnectedSystemObjectsByMvoIdsAndTargetSystemsAsync(IEnumerable<Guid> mvoIds, IEnumerable<int> targetConnectedSystemIds)
        => _inner.GetConnectedSystemObjectsByMvoIdsAndTargetSystemsAsync(mvoIds, targetConnectedSystemIds);

    public Task<List<ConnectedSystemObjectAttributeValue>> GetCsoAttributeValuesByCsoIdsAsync(IEnumerable<Guid> csoIds)
        => _inner.GetCsoAttributeValuesByCsoIdsAsync(csoIds);

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByMetaverseObjectIdAsync(Guid metaverseObjectId, int connectedSystemId)
        => _inner.GetConnectedSystemObjectByMetaverseObjectIdAsync(metaverseObjectId, connectedSystemId);

    public Task<Dictionary<Guid, ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdsAsync(IEnumerable<Guid> metaverseObjectIds, int connectedSystemId)
        => _inner.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(metaverseObjectIds, connectedSystemId);

    public Task<ConnectedSystemObjectTypeAttribute?> GetAttributeAsync(int id)
        => _inner.GetAttributeAsync(id);

    public Task<Dictionary<int, ConnectedSystemObjectTypeAttribute>> GetAttributesByIdsAsync(IEnumerable<int> ids)
        => _inner.GetAttributesByIdsAsync(ids);

    public Task<int> GetExecutableExportCountAsync(int connectedSystemId)
        => _inner.GetExecutableExportCountAsync(connectedSystemId);

    public Task<List<PendingExport>> GetExecutableExportsAsync(int connectedSystemId)
        => _inner.GetExecutableExportsAsync(connectedSystemId);

    public Task<List<PendingExport>> GetExecutableExportBatchAsync(int connectedSystemId, int take, DateTime? afterCreatedAt, Guid? afterId)
        => _inner.GetExecutableExportBatchAsync(connectedSystemId, take, afterCreatedAt, afterId);

    public Task<List<PendingExport>> GetRemainingDeferredExportsAsync(int connectedSystemId, DateTime? afterCreatedAt, Guid? afterId)
        => _inner.GetRemainingDeferredExportsAsync(connectedSystemId, afterCreatedAt, afterId);

    public Task<bool> AnyExecutableNonDeferredExportsAfterAsync(int connectedSystemId, DateTime? afterCreatedAt, Guid? afterId)
        => _inner.AnyExecutableNonDeferredExportsAfterAsync(connectedSystemId, afterCreatedAt, afterId);

    public Task<List<PendingExportSummary>> GetExecutableExportSummariesAsync(int connectedSystemId)
        => _inner.GetExecutableExportSummariesAsync(connectedSystemId);

    public Task<List<PendingExport>> GetPendingExportsByIdsAsync(IList<Guid> pendingExportIds)
        => _inner.GetPendingExportsByIdsAsync(pendingExportIds);

    /// <summary>
    /// The rollback-only transaction is itself a preview backstop, so the guard hands it through.
    /// </summary>
    public Task<IAsyncDisposable?> BeginRollbackOnlyTransactionAsync()
        => _inner.BeginRollbackOnlyTransactionAsync();

    public Task<Dictionary<int, string>> GetMetaverseAttributeNamesAsync()
        => _inner.GetMetaverseAttributeNamesAsync();

    #endregion

    #region Writes (always throw)

    public Task StampImportStateAsync(IReadOnlyCollection<(Guid CsoId, Guid? Hash, Guid? Fingerprint)> stamps)
        => throw new PreviewWriteAttemptedException(nameof(StampImportStateAsync));

    public Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, HashSet<Guid>? previouslyCommittedCsoIds = null)
        => throw new PreviewWriteAttemptedException(nameof(CreateConnectedSystemObjectsAsync));

    public Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, List<(Guid CsoId, ConnectedSystemObjectAttributeValue Value)>? pendingAdditions = null, List<Guid>? pendingRemovalIds = null)
        => throw new PreviewWriteAttemptedException(nameof(UpdateConnectedSystemObjectsAsync));

    public Task UpdateConnectedSystemObjectJoinStatesAsync(List<ConnectedSystemObject> connectedSystemObjects)
        => throw new PreviewWriteAttemptedException(nameof(UpdateConnectedSystemObjectJoinStatesAsync));

    public Task ClearConnectedSystemObjectScopeReviewPendingAsync(IReadOnlyCollection<Guid> ids)
        => throw new PreviewWriteAttemptedException(nameof(ClearConnectedSystemObjectScopeReviewPendingAsync));

    public Task UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(List<(ConnectedSystemObject cso, List<ConnectedSystemObjectAttributeValue> newAttributeValues)> updates)
        => throw new PreviewWriteAttemptedException(nameof(UpdateConnectedSystemObjectsWithNewAttributeValuesAsync));

    public Task ApplyExportedAttributeValuesAsync(List<ConnectedSystemObjectAttributeValue> additions, List<Guid> removalValueIds, IReadOnlyCollection<Guid> affectedCsoIds)
        => throw new PreviewWriteAttemptedException(nameof(ApplyExportedAttributeValuesAsync));

    public Task DeleteConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
        => throw new PreviewWriteAttemptedException(nameof(DeleteConnectedSystemObjectsAsync));

    public Task<int> FixupCrossBatchReferenceIdsAsync(int connectedSystemId)
        => throw new PreviewWriteAttemptedException(nameof(FixupCrossBatchReferenceIdsAsync));

    public Task<int> FixupCrossBatchChangeRecordReferenceIdsAsync(int connectedSystemId, int? batchSize = null)
        => throw new PreviewWriteAttemptedException(nameof(FixupCrossBatchChangeRecordReferenceIdsAsync));

    public Task<int> FixupMvoReferenceValueIdsAsync(IReadOnlyList<(Guid MvoId, int AttributeId, Guid TargetMvoId)> fixups)
        => throw new PreviewWriteAttemptedException(nameof(FixupMvoReferenceValueIdsAsync));

    public Task ClearMetaverseObjectScopeReviewPendingAsync(IReadOnlyCollection<Guid> ids)
        => throw new PreviewWriteAttemptedException(nameof(ClearMetaverseObjectScopeReviewPendingAsync));

    public Task CreateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        => throw new PreviewWriteAttemptedException(nameof(CreateMetaverseObjectsAsync));

    public Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        => throw new PreviewWriteAttemptedException(nameof(UpdateMetaverseObjectsAsync));

    public Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
        => throw new PreviewWriteAttemptedException(nameof(UpdateMetaverseObjectAsync));

    public Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject)
        => throw new PreviewWriteAttemptedException(nameof(DeleteMetaverseObjectAsync));

    public Task DeleteMetaverseObjectsAsync(IReadOnlyCollection<MetaverseObject> metaverseObjects)
        => throw new PreviewWriteAttemptedException(nameof(DeleteMetaverseObjectsAsync));

    public Task DeleteMetaverseObjectAttributeValuesByIdsAsync(IReadOnlyList<Guid> attributeValueIds)
        => throw new PreviewWriteAttemptedException(nameof(DeleteMetaverseObjectAttributeValuesByIdsAsync));

    public Task CreatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
        => throw new PreviewWriteAttemptedException(nameof(CreatePendingExportsAsync));

    public Task StageInitialPasswordsAsync(IEnumerable<PendingInitialPassword> pendingInitialPasswords)
        => throw new PreviewWriteAttemptedException(nameof(StageInitialPasswordsAsync));

    public Task RecordInitialPasswordAttemptsAsync(IEnumerable<PendingInitialPassword> attempts)
        => throw new PreviewWriteAttemptedException(nameof(RecordInitialPasswordAttemptsAsync));

    public Task DeleteInitialPasswordsAsync(IEnumerable<Guid> ids)
        => throw new PreviewWriteAttemptedException(nameof(DeleteInitialPasswordsAsync));

    public Task<int> ReleaseParkedInitialPasswordsAsync(int syncRuleId)
        => throw new PreviewWriteAttemptedException(nameof(ReleaseParkedInitialPasswordsAsync));

    public Task<int> ExpireInitialPasswordsAsync(int connectedSystemId, DateTime asOf)
        => throw new PreviewWriteAttemptedException(nameof(ExpireInitialPasswordsAsync));

    public Task<int> DeleteTerminalInitialPasswordsAsync(DateTime olderThan, int maxRecords)
        => throw new PreviewWriteAttemptedException(nameof(DeleteTerminalInitialPasswordsAsync));

    public Task QueuePasswordChangesAsync(IEnumerable<PendingPasswordChange> changes)
        => throw new PreviewWriteAttemptedException(nameof(QueuePasswordChangesAsync));

    public Task RecordPasswordChangeAttemptsAsync(IEnumerable<PendingPasswordChange> changes)
        => throw new PreviewWriteAttemptedException(nameof(RecordPasswordChangeAttemptsAsync));

    public Task DeletePasswordChangesAsync(IEnumerable<Guid> ids)
        => throw new PreviewWriteAttemptedException(nameof(DeletePasswordChangesAsync));

    public Task<int> ExpirePasswordChangesAsync(int connectedSystemId, DateTime asOf)
        => throw new PreviewWriteAttemptedException(nameof(ExpirePasswordChangesAsync));

    public Task<int> ReleasePasswordChangesForDeliveryAsync(int connectedSystemId)
        => throw new PreviewWriteAttemptedException(nameof(ReleasePasswordChangesForDeliveryAsync));

    public Task<int> DeleteTerminalPasswordChangesAsync(DateTime olderThan, int maxRecords)
        => throw new PreviewWriteAttemptedException(nameof(DeleteTerminalPasswordChangesAsync));

    public Task<int> RetryPasswordChangesAsync(PendingPasswordChangeFilter filter)
        => throw new PreviewWriteAttemptedException(nameof(RetryPasswordChangesAsync));

    public Task<int> CancelPasswordChangesAsync(
        PendingPasswordChangeFilter filter,
        Guid? cancelledById,
        string? cancelledByName,
        DateTime asOf)
        => throw new PreviewWriteAttemptedException(nameof(CancelPasswordChangesAsync));

    public Task DeletePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
        => throw new PreviewWriteAttemptedException(nameof(DeletePendingExportsAsync));

    public Task UpdatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
        => throw new PreviewWriteAttemptedException(nameof(UpdatePendingExportsAsync));

    public Task<int> DeletePendingExportsByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds)
        => throw new PreviewWriteAttemptedException(nameof(DeletePendingExportsByConnectedSystemObjectIdsAsync));

    public Task DeleteUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports)
        => throw new PreviewWriteAttemptedException(nameof(DeleteUntrackedPendingExportsAsync));

    public Task DeleteUntrackedPendingExportAttributeValueChangesAsync(IEnumerable<PendingExportAttributeValueChange> untrackedAttributeValueChanges)
        => throw new PreviewWriteAttemptedException(nameof(DeleteUntrackedPendingExportAttributeValueChangesAsync));

    public Task UpdateUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports)
        => throw new PreviewWriteAttemptedException(nameof(UpdateUntrackedPendingExportsAsync));

    public Task UpdateActivityAsync(Activity activity)
        => throw new PreviewWriteAttemptedException(nameof(UpdateActivityAsync));

    public Task UpdateActivityMessageAsync(Activity activity, string message)
        => throw new PreviewWriteAttemptedException(nameof(UpdateActivityMessageAsync));

    public Task UpdateActivityProgressOutOfBandAsync(Activity activity)
        => throw new PreviewWriteAttemptedException(nameof(UpdateActivityProgressOutOfBandAsync));

    public Task SaveActivityPhasesAsync(IReadOnlyList<ActivityPhase> phases)
        => throw new PreviewWriteAttemptedException(nameof(SaveActivityPhasesAsync));

    public Task RecordExclusionDiscardCountsAsync(Guid activityId, IReadOnlyDictionary<int, long> entriesDiscardedByContainerId)
        => throw new PreviewWriteAttemptedException(nameof(RecordExclusionDiscardCountsAsync));

    public Task<bool> BulkInsertRpeisAsync(List<ActivityRunProfileExecutionItem> rpeis)
        => throw new PreviewWriteAttemptedException(nameof(BulkInsertRpeisAsync));

    public Task BulkUpdateRpeiOutcomesAsync(List<ActivityRunProfileExecutionItem> rpeis, List<ActivityRunProfileExecutionItemSyncOutcome> newOutcomes)
        => throw new PreviewWriteAttemptedException(nameof(BulkUpdateRpeiOutcomesAsync));

    public Task PersistRpeiCsoChangesAsync(List<ActivityRunProfileExecutionItem> rpeis)
        => throw new PreviewWriteAttemptedException(nameof(PersistRpeiCsoChangesAsync));

    public Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem)
        => throw new PreviewWriteAttemptedException(nameof(UpdateConnectedSystemAsync));

    public Task CreateMetaverseObjectChangeDirectAsync(MetaverseObjectChange change)
        => throw new PreviewWriteAttemptedException(nameof(CreateMetaverseObjectChangeDirectAsync));

    public Task PersistPendingMvoChangesAsync(List<MetaverseObjectChange> newChanges, List<MetaverseObjectChange> attributeAppendsToExistingChanges)
        => throw new PreviewWriteAttemptedException(nameof(PersistPendingMvoChangesAsync));

    public Task CreateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
        => throw new PreviewWriteAttemptedException(nameof(CreateConnectedSystemObjectAsync));

    public Task UpdateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
        => throw new PreviewWriteAttemptedException(nameof(UpdateConnectedSystemObjectAsync));

    public Task UpdateConnectedSystemObjectWithNewAttributeValuesAsync(ConnectedSystemObject connectedSystemObject, List<ConnectedSystemObjectAttributeValue> newAttributeValues)
        => throw new PreviewWriteAttemptedException(nameof(UpdateConnectedSystemObjectWithNewAttributeValuesAsync));

    public Task<bool> TryClaimConnectedSystemObjectForJoinAsync(Guid connectedSystemObjectId, Guid metaverseObjectId, DateTime dateJoined)
        => throw new PreviewWriteAttemptedException(nameof(TryClaimConnectedSystemObjectForJoinAsync));

    public Task CreatePendingExportAsync(PendingExport pendingExport)
        => throw new PreviewWriteAttemptedException(nameof(CreatePendingExportAsync));

    public Task DeletePendingExportAsync(PendingExport pendingExport)
        => throw new PreviewWriteAttemptedException(nameof(DeletePendingExportAsync));

    public Task UpdatePendingExportAsync(PendingExport pendingExport)
        => throw new PreviewWriteAttemptedException(nameof(UpdatePendingExportAsync));

    public Task DisconnectConnectedSystemObjectsAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds)
        => throw new PreviewWriteAttemptedException(nameof(DisconnectConnectedSystemObjectsAsync));

    public Task DeletePendingExportsByIdsAsync(IList<Guid> pendingExportIds)
        => throw new PreviewWriteAttemptedException(nameof(DeletePendingExportsByIdsAsync));

    public Task MarkPendingExportsAsExecutingAsync(IList<PendingExport> pendingExports)
        => throw new PreviewWriteAttemptedException(nameof(MarkPendingExportsAsExecutingAsync));

    public Task SetPendingExportQueueingItemsAsync(IReadOnlyCollection<(Guid PendingExportId, Guid QueuedByRunProfileExecutionItemId)> stamps)
        => throw new PreviewWriteAttemptedException(nameof(SetPendingExportQueueingItemsAsync));

    public Task BulkInsertCausalEdgesAsync(List<CausalEdge> edges)
        => throw new PreviewWriteAttemptedException(nameof(BulkInsertCausalEdgesAsync));

    #endregion
}
