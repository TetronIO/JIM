using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Data.Repositories;

/// <summary>
/// Domain logic boundary for sync operations.
/// <para>
/// This interface encapsulates all domain logic that sync processors need beyond data access
/// (which is handled by <see cref="ISyncRepository"/>). It covers scoping evaluation, drift
/// detection, export rule evaluation, and export execution.
/// </para>
/// <para>
/// The production implementation (<c>SyncServer</c>) delegates to existing application-layer
/// servers (ExportEvaluationServer, ExportExecutionServer, ScopingEvaluationServer,
/// DriftDetectionService). Those servers now use <see cref="ISyncRepository"/> for all data
/// access rather than reaching through JimApplication.
/// </para>
/// </summary>
public interface ISyncServer
{
    #region Scoping Evaluation

    /// <summary>
    /// Checks if a CSO is in scope for an import rule based on scoping criteria.
    /// No scoping criteria means all objects of the type are in scope.
    /// </summary>
    bool IsCsoInScopeForImportRule(ConnectedSystemObject cso, SyncRule importRule);

    /// <summary>
    /// Checks if an MVO is in scope for an export rule based on scoping criteria.
    /// No scoping criteria means all objects of the type are in scope.
    /// </summary>
    bool IsMvoInScopeForExportRule(MetaverseObject mvo, SyncRule exportRule);

    #endregion

    #region Drift Detection

    /// <summary>
    /// Evaluates drift for a CSO that has been imported/synced against its joined MVO.
    /// Checks all export rules with EnforceState = true that target this CSO's connected system.
    /// Returns corrective pending exports (not yet persisted) when drift is detected.
    /// </summary>
    DriftDetectionResult EvaluateDrift(
        ConnectedSystemObject cso,
        MetaverseObject? mvo,
        List<SyncRule> exportRules,
        Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>? importMappingsByAttribute = null);

    #endregion

    #region Export Evaluation

    /// <summary>
    /// Builds a cache of export rules and CSO lookups for optimised batch evaluation.
    /// Call this once at the start of sync, then pass the cache to evaluation methods.
    /// </summary>
    Task<ExportEvaluationCache> BuildExportEvaluationCacheAsync(
        int sourceConnectedSystemId,
        List<SyncRule>? preloadedSyncRules = null);

    /// <summary>
    /// Evaluates all export rules for an MVO that has changed, creating pending exports
    /// with no-net-change detection.
    /// </summary>
    Task<ExportEvaluationResult> EvaluateExportRulesWithNoNetChangeDetectionAsync(
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> changedAttributes,
        ConnectedSystem? sourceSystem,
        ExportEvaluationCache cache,
        bool deferSave = false,
        HashSet<MetaverseObjectAttributeValue>? removedAttributes = null,
        List<PendingExport>? existingPendingExports = null);

    /// <summary>
    /// Evaluates if an MVO has fallen out of scope for any export rules (deprovisioning).
    /// </summary>
    Task<List<PendingExport>> EvaluateOutOfScopeExportsAsync(
        MetaverseObject mvo,
        ConnectedSystem? sourceSystem,
        ExportEvaluationCache cache);

    /// <summary>
    /// Evaluates export rules for an MVO being deleted.
    /// Creates delete exports for provisioned CSOs and disconnects them.
    /// </summary>
    Task<List<PendingExport>> EvaluateMvoDeletionAsync(MetaverseObject mvo);

    #endregion

    #region Export Execution

    /// <summary>
    /// Executes all pending exports for a connected system via the connector.
    /// </summary>
    Task<ExportExecutionResult> ExecuteExportsAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        SyncRunMode runMode = SyncRunMode.PreviewAndSync);

    /// <summary>
    /// Executes all pending exports with full options including progress reporting,
    /// cancellation, and parallel batch support.
    /// </summary>
    Task<ExportExecutionResult> ExecuteExportsAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        SyncRunMode runMode,
        ExportExecutionOptions? options,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback = null,
        Func<IConnector>? connectorFactory = null,
        Func<ISyncRepository>? repositoryFactory = null);

    #endregion
}
