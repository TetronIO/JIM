using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Core;
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
    private readonly ExportEvaluationServer _exportEval;
    private readonly ExportExecutionServer _exportExec;
    private readonly ScopingEvaluationServer _scoping;
    private readonly DriftDetectionService _drift;

    public SyncServer(JimApplication jim)
    {
        _exportEval = jim.ExportEvaluation;
        _exportExec = jim.ExportExecution;
        _scoping = jim.ScopingEvaluation;
        _drift = jim.DriftDetection;
    }

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
        Func<ISyncRepository>? repositoryFactory = null)
        => _exportExec.ExecuteExportsAsync(
            connectedSystem, connector, runMode, options, cancellationToken,
            progressCallback, connectorFactory, repositoryFactory);

    #endregion
}
