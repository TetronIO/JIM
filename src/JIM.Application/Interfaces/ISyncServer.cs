using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Interfaces;

/// <summary>
/// Domain logic boundary for sync operations.
/// <para>
/// This interface encapsulates all domain/application logic that sync processors need beyond
/// data access (which is handled by <see cref="ISyncRepository"/>). It covers settings,
/// CSO caching, object matching, RPEI-linking, connector triad operations, scoping evaluation,
/// drift detection, export rule evaluation, and export execution.
/// </para>
/// <para>
/// The production implementation (<c>SyncServer</c>) delegates to existing application-layer
/// servers (ExportEvaluationServer, ExportExecutionServer, ScopingEvaluationServer,
/// DriftDetectionService, ConnectedSystemServer, ServiceSettingsServer, etc.).
/// </para>
/// </summary>
public interface ISyncServer
{
    #region Settings

    /// <summary>
    /// Gets the configured sync page size (number of CSOs per page).
    /// </summary>
    Task<int> GetSyncPageSizeAsync();

    /// <summary>
    /// Gets the configured sync outcome tracking level.
    /// </summary>
    Task<ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel> GetSyncOutcomeTrackingLevelAsync();

    /// <summary>
    /// Gets whether CSO change tracking is enabled.
    /// </summary>
    Task<bool> GetCsoChangeTrackingEnabledAsync();

    /// <summary>
    /// Gets whether MVO change tracking is enabled.
    /// </summary>
    Task<bool> GetMvoChangeTrackingEnabledAsync();

    #endregion

    #region CSO Lookup Cache

    /// <summary>
    /// Adds a CSO to the in-memory lookup cache for fast external ID matching during import.
    /// </summary>
    void AddCsoToCache(int connectedSystemId, int externalIdAttributeId, string externalIdValue, Guid csoId);

    /// <summary>
    /// Removes a CSO from the in-memory lookup cache.
    /// </summary>
    void EvictCsoFromCache(int connectedSystemId, int externalIdAttributeId, string externalIdValue);

    #endregion

    #region Object Matching

    /// <summary>
    /// Finds a matching MVO for join evaluation using the configured matching rules.
    /// </summary>
    Task<MetaverseObject?> FindMatchingMetaverseObjectAsync(ConnectedSystemObject cso, List<ObjectMatchingRule> matchingRules);

    /// <summary>
    /// Finds a matching CSO for export provisioning using object matching rules.
    /// </summary>
    Task<ConnectedSystemObject?> FindMatchingConnectedSystemObjectAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        List<ObjectMatchingRule> matchingRules);

    #endregion

    #region Connected System Operations

    /// <summary>
    /// Refreshes the auto-selected containers for a connected system using the connector triad.
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
    /// </summary>
    Task UpdateConnectedSystemWithTriadAsync(
        ConnectedSystem connectedSystem,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName);

    #endregion

    #region Activity Management

    /// <summary>
    /// Marks an activity as failed with the specified error message.
    /// </summary>
    Task FailActivityWithErrorAsync(Activity activity, string errorMessage);

    /// <summary>
    /// Marks an activity as failed with details from the specified exception.
    /// </summary>
    Task FailActivityWithErrorAsync(Activity activity, Exception exception);

    #endregion

    #region CSO Persistence with Change Tracking

    /// <summary>
    /// Bulk creates CSOs with associated RPEIs. Persists the CSOs via the data layer,
    /// then links change tracking records to the corresponding RPEIs.
    /// </summary>
    Task CreateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis,
        Func<int, Task>? onBatchPersisted = null);

    /// <summary>
    /// Bulk updates CSOs with associated RPEIs. Persists the CSO updates,
    /// then links change tracking records to the corresponding RPEIs.
    /// </summary>
    Task UpdateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis);

    /// <summary>
    /// Deletes CSOs with associated RPEIs. Captures final attribute snapshots on the RPEIs
    /// before deletion for audit trail, then deletes the CSOs.
    /// </summary>
    Task DeleteConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis);

    #endregion

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
