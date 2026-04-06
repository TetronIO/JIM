using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Servers;

/// <summary>
/// Production implementation of <see cref="ISyncServer"/> that delegates to existing
/// application-layer servers for domain logic.
/// </summary>
public class SyncServer : ISyncServer
{
    private readonly JimApplication _jim;
    private readonly ISyncRepository _syncRepo;
    private readonly ExportEvaluationServer _exportEval;
    private readonly ExportExecutionServer _exportExec;
    private readonly ScopingEvaluationServer _scoping;
    private readonly DriftDetectionService _drift;

    public SyncServer(JimApplication jim)
    {
        _jim = jim;
        _syncRepo = jim.SyncRepo;
        _exportEval = jim.ExportEvaluation;
        _exportExec = jim.ExportExecution;
        _scoping = jim.ScopingEvaluation;
        _drift = jim.DriftDetection;
    }

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

    #region CSO Lookup Cache

    public void AddCsoToCache(int connectedSystemId, int externalIdAttributeId, string externalIdValue, Guid csoId)
        => _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, externalIdAttributeId, externalIdValue, csoId);

    public void EvictCsoFromCache(int connectedSystemId, int externalIdAttributeId, string externalIdValue)
        => _jim.ConnectedSystems.EvictCsoFromCache(connectedSystemId, externalIdAttributeId, externalIdValue);

    #endregion

    #region Object Matching

    public Task<MetaverseObject?> FindMatchingMetaverseObjectAsync(ConnectedSystemObject cso, List<ObjectMatchingRule> matchingRules)
        => _jim.ObjectMatching.FindMatchingMetaverseObjectAsync(cso, matchingRules);

    public Task<ConnectedSystemObject?> FindMatchingConnectedSystemObjectAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        List<ObjectMatchingRule> matchingRules)
        => _jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(metaverseObject, connectedSystem, connectedSystemObjectType, matchingRules);

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

    #region Activity Management

    public Task FailActivityWithErrorAsync(Activity activity, string errorMessage)
        => _jim.Activities.FailActivityWithErrorAsync(activity, errorMessage);

    public Task FailActivityWithErrorAsync(Activity activity, Exception exception)
        => _jim.Activities.FailActivityWithErrorAsync(activity, exception);

    #endregion

    #region MVO Deletion with Change Tracking

    public async Task DeleteMetaverseObjectAsync(
        MetaverseObject metaverseObject,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName,
        List<MetaverseObjectAttributeValue>? finalAttributeValues)
    {
        var changeTrackingEnabled = await GetMvoChangeTrackingEnabledAsync();

        if (changeTrackingEnabled)
        {
            var attributesToCapture = finalAttributeValues ?? metaverseObject.AttributeValues.ToList();
            var mvoId = metaverseObject.Id;

            var displayName = metaverseObject.DisplayName;
            if (displayName == null && attributesToCapture.Count > 0)
            {
                var displayNameAttrValue = attributesToCapture.SingleOrDefault(
                    av => av.Attribute?.Name == Constants.BuiltInAttributes.DisplayName);
                displayName = displayNameAttrValue?.StringValue;
            }

            var change = new MetaverseObjectChange
            {
                ChangeType = ObjectChangeType.Deleted,
                ChangeTime = DateTime.UtcNow,
                InitiatedByType = initiatorType,
                InitiatedById = initiatorId,
                InitiatedByName = initiatorName,
                ChangeInitiatorType = initiatorType == ActivityInitiatorType.User
                    ? MetaverseObjectChangeInitiatorType.User
                    : MetaverseObjectChangeInitiatorType.NotSet,
                DeletedMetaverseObjectId = mvoId,
                DeletedObjectTypeId = metaverseObject.Type?.Id,
                DeletedObjectDisplayName = displayName
            };

            foreach (var attributeValue in attributesToCapture)
                MetaverseServer.AddMvoChangeAttributeValueObject(change, attributeValue, ValueChangeType.Remove);

            await _syncRepo.DeleteMetaverseObjectAsync(metaverseObject);
            await _syncRepo.CreateMetaverseObjectChangeDirectAsync(change);
            return;
        }

        await _syncRepo.DeleteMetaverseObjectAsync(metaverseObject);
    }

    #endregion

    #region CSO Persistence with Change Tracking

    public async Task CreateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis,
        HashSet<Guid>? previouslyCommittedCsoIds = null,
        Func<int, Task>? onBatchPersisted = null)
    {
        await _syncRepo.CreateConnectedSystemObjectsAsync(connectedSystemObjects, previouslyCommittedCsoIds);
        if (onBatchPersisted != null)
            await onBatchPersisted(connectedSystemObjects.Count);

        var changeTrackingEnabled = await GetCsoChangeTrackingEnabledAsync();
        _jim.ConnectedSystems.LinkCreateChangeRecords(connectedSystemObjects, rpeis, changeTrackingEnabled);
    }

    public async Task UpdateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis)
    {
        // Snapshot pending attribute changes BEFORE LinkUpdateChangeRecords, which clears
        // PendingAttributeValueAdditions/Removals after building change history records.
        // The repository needs these lists to persist attribute value inserts/deletes.
        var pendingAdditions = connectedSystemObjects
            .SelectMany(cso => cso.PendingAttributeValueAdditions.Select(av => (CsoId: cso.Id, Value: av)))
            .ToList();
        var pendingRemovals = connectedSystemObjects
            .SelectMany(cso => cso.PendingAttributeValueRemovals)
            .Where(av => av.Id != Guid.Empty)
            .Select(av => av.Id)
            .ToList();

        var changeTrackingEnabled = await GetCsoChangeTrackingEnabledAsync();
        _jim.ConnectedSystems.LinkUpdateChangeRecords(connectedSystemObjects, rpeis, changeTrackingEnabled);

        await _syncRepo.UpdateConnectedSystemObjectsAsync(connectedSystemObjects, pendingAdditions, pendingRemovals);
    }

    public async Task DeleteConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis)
    {
        var changeTrackingEnabled = await GetCsoChangeTrackingEnabledAsync();
        _jim.ConnectedSystems.LinkDeleteChangeRecords(connectedSystemObjects, rpeis, changeTrackingEnabled);

        await _syncRepo.DeleteConnectedSystemObjectsAsync(connectedSystemObjects);
    }

    #endregion

    #region Scoping Evaluation

    public bool IsCsoInScopeForImportRule(ConnectedSystemObject cso, SyncRule importRule)
        => _scoping.IsCsoInScopeForImportRule(cso, importRule);

    public bool IsMvoInScopeForExportRule(MetaverseObject mvo, SyncRule exportRule)
        => _scoping.IsMvoInScopeForExportRule(mvo, exportRule);

    #endregion

    #region Drift Detection

    public DriftDetectionResult EvaluateDrift(
        ConnectedSystemObject cso,
        MetaverseObject? mvo,
        List<SyncRule> exportRules,
        Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>? importMappingsByAttribute = null)
        => _drift.EvaluateDrift(cso, mvo, exportRules, importMappingsByAttribute);

    #endregion

    #region Export Evaluation

    public Task<ExportEvaluationCache> BuildExportEvaluationCacheAsync(
        int sourceConnectedSystemId,
        List<SyncRule>? preloadedSyncRules = null)
        => _exportEval.BuildExportEvaluationCacheAsync(sourceConnectedSystemId, preloadedSyncRules);

    public Task RefreshExportEvaluationCacheForPageAsync(ExportEvaluationCache cache, IEnumerable<Guid> mvoIds)
        => _exportEval.RefreshExportEvaluationCacheForPageAsync(cache, mvoIds);

    public Task<ExportEvaluationResult> EvaluateExportRulesWithNoNetChangeDetectionAsync(
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> changedAttributes,
        ConnectedSystem? sourceSystem,
        ExportEvaluationCache cache,
        bool deferSave = false,
        HashSet<MetaverseObjectAttributeValue>? removedAttributes = null,
        List<PendingExport>? existingPendingExports = null)
        => _exportEval.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, changedAttributes, sourceSystem, cache, deferSave, removedAttributes, existingPendingExports);

    public Task<List<PendingExport>> EvaluateOutOfScopeExportsAsync(
        MetaverseObject mvo,
        ConnectedSystem? sourceSystem,
        ExportEvaluationCache cache)
        => _exportEval.EvaluateOutOfScopeExportsAsync(mvo, sourceSystem, cache);

    public Task<List<PendingExport>> EvaluateMvoDeletionAsync(MetaverseObject mvo)
        => _exportEval.EvaluateMvoDeletionAsync(mvo);

    #endregion

    #region Export Execution

    public Task<ExportExecutionResult> ExecuteExportsAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        SyncRunMode runMode = SyncRunMode.PreviewAndSync)
        => _exportExec.ExecuteExportsAsync(connectedSystem, connector, runMode);

    public Task<ExportExecutionResult> ExecuteExportsAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        SyncRunMode runMode,
        ExportExecutionOptions? options,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback = null,
        Func<IConnector>? connectorFactory = null,
        Func<ISyncRepository>? repositoryFactory = null,
        Func<List<ProcessedExportItem>, Task>? batchCompletedCallback = null)
        => _exportExec.ExecuteExportsAsync(
            connectedSystem, connector, runMode, options, cancellationToken,
            progressCallback, connectorFactory, repositoryFactory, batchCompletedCallback);

    #endregion
}
