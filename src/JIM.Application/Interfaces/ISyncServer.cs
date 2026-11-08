// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Application.UniqueValues;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
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

    #region Metaverse-Derived Attribute Flows

    /// <summary>
    /// Builds the run's Metaverse-Derived Attribute Flow graph (#1750) through <see cref="DerivedFlowGraphFactory"/>.
    /// Call once at run start and attach the result to the run's <see cref="AttributePriorityContext"/>.
    /// </summary>
    /// <param name="allSyncRules">Every Synchronisation Rule the run's priority context is built from (all Connected
    /// Systems), with their Metaverse Object Types and attributes loaded, as the worker's all-rules load provides.</param>
    /// <exception cref="Exceptions.DerivedFlowCycleException">The enabled derived flows contain a dependency cycle:
    /// the run must fail hard before processing any object (plan decision 11).</exception>
    DerivedFlowGraph CreateDerivedFlowGraph(IReadOnlyCollection<SyncRule> allSyncRules);

    #endregion

    #region Object Matching

    /// <summary>
    /// Finds a matching MVO for join evaluation using the configured matching rules.
    /// </summary>
    Task<ImportMatch?> FindMatchingMetaverseObjectAsync(ConnectedSystemObject cso, List<ObjectMatchingRule> matchingRules);

    #endregion

    #region Connected System Operations

    /// <summary>
    /// Refreshes the auto-selected containers for a Connected System using the connector triad.
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
    /// Persists the connector's import watermark (PersistedConnectorData) as runtime state: no Activity, no
    /// configuration snapshot.
    /// </summary>
    Task UpdateConnectedSystemPersistedConnectorDataAsync(ConnectedSystem connectedSystem, string? persistedConnectorData);

    /// <summary>
    /// Gets the Delta Import watermark JIM holds for each Partition of a Connected System that has one (#2715).
    /// </summary>
    Task<List<ConnectedSystemPartitionWatermark>> GetConnectedSystemPartitionWatermarksAsync(int connectedSystemId);

    /// <summary>
    /// Records what a completed import reached, as runtime state (no Activity, no configuration snapshot), in one
    /// transaction: the persisted connector data when not null, each Partition watermark (a null value clears that
    /// Partition's), and the removal of the watermarks of Partitions no longer selected (#2715).
    /// </summary>
    Task<ImportWatermarkRecordingResult> RecordImportWatermarksAsync(ConnectedSystem connectedSystem, string? persistedConnectorData,
        IReadOnlyDictionary<int, string?> partitionWatermarks);

    #endregion

    #region MVO Deletion with Change Tracking

    /// <summary>
    /// Deletes an MVO, recording the deletion against the Activity it is made in: its initiator for the audit trail,
    /// and the Activity itself so the change record links to it (#2460).
    /// Handles change tracking (if enabled), then delegates the raw delete to ISyncRepository, which retires the
    /// object's generated values in the same transaction (#242, Phase 6) and reports them, for the caller's outcomes.
    /// </summary>
    Task<IReadOnlyList<GeneratedValueRetirement>> DeleteMetaverseObjectAsync(
        MetaverseObject metaverseObject,
        Activity activity,
        List<MetaverseObjectAttributeValue>? finalAttributeValues);

    /// <summary>
    /// Set-based form of <see cref="DeleteMetaverseObjectAsync"/> (issue #993): deletes multiple
    /// MVOs with one FK cleanup pass and bulk change record persistence. Semantically equivalent
    /// to calling the singular method per object, and likewise returns the generated values it retired.
    /// </summary>
    Task<IReadOnlyList<GeneratedValueRetirement>> DeleteMetaverseObjectsAsync(
        List<(MetaverseObject Mvo, List<MetaverseObjectAttributeValue> FinalAttributeValues)> deletions,
        Activity activity);

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
    /// <param name="previouslyCommittedCsoIds">IDs of CSOs committed in prior batches, allowing
    /// cross-batch FK references to be preserved during bulk insert instead of requiring post-hoc fixup.</param>
    Task CreateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis,
        HashSet<Guid>? previouslyCommittedCsoIds = null,
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
    /// before deletion for audit trail, then deletes the CSOs and writes the RPEIs' own rows and their Deleted change
    /// records in the same transaction (#2807), so a failure leaves neither. Each RPEI is given its id here where it
    /// has none; the caller's later RPEI flush must pass those ids to
    /// <see cref="JIM.Data.Repositories.ISyncRepository.BulkInsertRpeisAsync"/> as already written, and leave the
    /// RPEIs out of its change record write.
    /// </summary>
    Task DeleteConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis);

    /// <summary>
    /// Deletes CSOs quietly, without execution items: objects their Metaverse Object's deletion had already
    /// disconnected. Each deletion is still recorded when Connected System Object change tracking is on, attributed
    /// to <paramref name="activity"/>, so the object's history stays findable by its id once its row is gone.
    /// </summary>
    Task DeleteConnectedSystemObjectsQuietlyAsync(List<ConnectedSystemObject> connectedSystemObjects, Activity activity);

    /// <summary>
    /// Creates "Added" change tracking records for provisioned CSOs.
    /// Provisioning CSOs are created during export evaluation and don't have dedicated RPEIs;
    /// the originating RPEI is resolved via MVO ID lookup.
    /// </summary>
    Task CreateProvisioningCsoChangeRecordsAsync(
        List<ConnectedSystemObject> provisioningCsos,
        Dictionary<Guid, ActivityRunProfileExecutionItem> mvoIdToRpei);

    #endregion

    #region Scoping Evaluation

    /// <summary>
    /// Checks if a CSO is in scope for an import rule based on scoping criteria.
    /// No scoping criteria means all objects of the type are in scope.
    /// </summary>
    bool IsCsoInScopeForImportRule(ConnectedSystemObject cso, SyncRule importRule);

    #endregion

    #region Drift Detection

    /// <summary>
    /// Evaluates drift for a CSO that has been imported/synced against its joined MVO.
    /// Checks all export rules with EnforceState = true that target this CSO's Connected System.
    /// Returns corrective Pending Exports (not yet persisted) when drift is detected.
    /// <paramref name="generatedValueAssignments"/> is the run's Unique Value Generation cache, already prefetched
    /// for this Connected System Object: a generated export Attribute Flow's expected value is its export-mode
    /// assignment, read from there without a query (#242). <paramref name="heldReferenceAnswers"/> answers the attributes
    /// a Delta Synchronisation loaded without their values (#2818), from the database.
    /// </summary>
    DriftDetectionResult EvaluateDrift(
        ConnectedSystemObject cso,
        MetaverseObject? mvo,
        List<SyncRule> exportRules,
        Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>? importMappingsByAttribute = null,
        AttributePriorityContext? priorityContext = null,
        UniqueValueResolveOptions? generatedValueAssignments = null,
        IReadOnlyCollection<DriftHeldReferenceAnswer>? heldReferenceAnswers = null);

    #endregion

    #region Export Evaluation

    /// <summary>
    /// Builds a cache of export rules and CSO lookups for optimised batch evaluation.
    /// Call this once at the start of sync, then pass the cache to evaluation methods.
    /// </summary>
    Task<ExportEvaluationCache> BuildExportEvaluationCacheAsync(
        List<SyncRule>? preloadedSyncRules = null);

    /// <summary>
    /// Rebuilds the per-page portions of the export evaluation cache (CsoLookup, CsoAttributeValues)
    /// for only the MVOs that changed in the current page. This bounds memory to page size rather than
    /// total dataset size.
    /// </summary>
    Task RefreshExportEvaluationCacheForPageAsync(ExportEvaluationCache cache, IEnumerable<Guid> mvoIds);

    /// <summary>
    /// Rebuilds the per-page portions of the export evaluation cache for a synchronisation page's export evaluations,
    /// loading a target group's member attribute only for the members the page's changes add or remove, where nothing
    /// else could ask about it (#2832).
    /// </summary>
    Task RefreshExportEvaluationCacheForEvaluationsAsync(
        ExportEvaluationCache cache,
        IReadOnlyCollection<(MetaverseObject Mvo, IReadOnlyCollection<MetaverseObjectAttributeValue> ChangedAttributes)> evaluations);

    /// <summary>
    /// Prefetches export-matching candidates for a whole page of Metaverse Objects in one batch query per
    /// Object Matching Rule group, instead of the one-database-round-trip-per-object cost export matching
    /// otherwise pays for every provisioning verdict. Populates
    /// <see cref="ExportEvaluationCache.ExportMatchCandidates"/> with a fresh instance; the caller clears
    /// it back to null once the page's evaluation finishes, so page-scoped candidates never leak into any
    /// other evaluation path.
    /// </summary>
    Task PrefetchExportMatchCandidatesForPageAsync(ExportEvaluationCache cache, IReadOnlyCollection<MetaverseObject> metaverseObjects);

    /// <summary>
    /// Evaluates all export rules for an MVO that has changed, creating Pending Exports
    /// with no-net-change detection.
    /// </summary>
    Task<ExportEvaluationResult> EvaluateExportRulesWithNoNetChangeDetectionAsync(
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> changedAttributes,
        ExportEvaluationCache cache,
        bool deferSave = false,
        HashSet<MetaverseObjectAttributeValue>? removedAttributes = null,
        List<PendingExport>? existingPendingExports = null);

    /// <summary>
    /// Evaluates if an MVO has fallen out of scope for any export rules (deprovisioning). A Connected
    /// System Object of a different Connected System Object Type than a Rule targets is skipped quietly
    /// (#1331, #1399): its own type's Rule owns its lifecycle, so there is nothing here to report.
    /// </summary>
    Task<List<PendingExport>> EvaluateOutOfScopeExportsAsync(
        MetaverseObject mvo,
        ExportEvaluationCache cache,
        ExportEvaluationWorkingSet? workingSet = null);

    /// <summary>
    /// Evaluates export rules for an MVO being deleted. Creates delete exports for CSOs matched by
    /// an export Synchronisation Rule whose OutboundDeprovisionAction is Delete (issue #655) and
    /// disconnects every joined CSO. Pass the run-scoped cache where one exists; otherwise the
    /// enabled export rules are loaded from the repository.
    /// </summary>
    Task<List<PendingExport>> EvaluateMvoDeletionAsync(MetaverseObject mvo, ExportEvaluationCache? exportEvaluationCache = null,
        ExportEvaluationWorkingSet? workingSet = null);

    /// <summary>
    /// Set-based form of <see cref="EvaluateMvoDeletionAsync"/> (issue #993): one CSO fetch,
    /// Pending Export ensure and CSO disconnect pass for the whole batch of MVOs.
    /// </summary>
    Task<List<PendingExport>> EvaluateMvoDeletionsAsync(IReadOnlyCollection<MetaverseObject> mvos, ExportEvaluationCache? exportEvaluationCache = null,
        ExportEvaluationWorkingSet? workingSet = null);

    /// <summary>
    /// Captures the referencing-object and resolution state reference recall needs before Metaverse
    /// Objects are deleted (issue #908). Call before <see cref="EvaluateMvoDeletionAsync"/>.
    /// </summary>
    Task<ReferenceRecallContext> CaptureReferenceRecallContextAsync(IReadOnlyCollection<Guid> deletionCandidateMvoIds);

    /// <summary>
    /// Stages membership-removal Pending Exports for Metaverse Objects that referenced now-deleted
    /// Metaverse Objects (reference recall, issue #908). Call after the deletions have been performed.
    /// Pass a run-scoped <paramref name="recallCache"/> to avoid a per-call Synchronisation Rule
    /// reload (#1003); when null, the cache is built ad hoc.
    /// </summary>
    Task<ReferenceRecallResult> StageReferenceRecallExportsAsync(ReferenceRecallContext context, IReadOnlyCollection<Guid> deletedMvoIds,
        ExportEvaluationCache? recallCache = null);

    #endregion

    #region Export Execution

    /// <summary>
    /// Executes all Pending Exports for a Connected System via the connector.
    /// </summary>
    Task<ExportExecutionResult> ExecuteExportsAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        SyncRunMode runMode = SyncRunMode.PreviewAndSync);

    /// <summary>
    /// Executes all Pending Exports with full options including progress reporting,
    /// cancellation, and parallel batch support. <paramref name="activityId"/> is the export run's Activity, which a
    /// Metaverse Object change Collision Remediation records links to (#2460).
    /// </summary>
    Task<ExportExecutionResult> ExecuteExportsAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        SyncRunMode runMode,
        ExportExecutionOptions? options,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback = null,
        Func<IConnector>? connectorFactory = null,
        Func<ISyncRepositoryScope>? repositoryFactory = null,
        Func<List<ProcessedExportItem>, Task>? batchCompletedCallback = null,
        JIM.Application.UniqueValues.UniqueValueReservationSet? uniqueValueReservations = null,
        Action<IReadOnlyList<string>>? containersCreated = null,
        Guid? activityId = null);

    #endregion
}
