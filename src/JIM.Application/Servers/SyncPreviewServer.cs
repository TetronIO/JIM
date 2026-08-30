// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Answers "what would happen if this object synchronised now?" without synchronising it (#288 plan
/// Phase 3): the per-object preview surface composing the inbound chain (scope, join or projection,
/// Attribute Flow) with the evaluation-only outbound path into a <see cref="SyncPreviewResult"/>,
/// persisting nothing and claiming nothing. Zero side effects is delivered as defence in depth (PRD
/// requirements 7 and 8): the engine verdicts are structurally pure; every read goes through a
/// <see cref="ReadOnlySyncRepositoryGuard"/> that throws on any write attempt; and the evaluation runs
/// inside a transaction that is unconditionally rolled back. Expected blocks (a missing object, an
/// ambiguous match, an attribute flow violation) return in <see cref="SyncPreviewResult.Errors"/> rather
/// than throwing (PRD requirement 5).
/// </summary>
public class SyncPreviewServer
{
    #region accessors
    private JimApplication Application { get; }
    private ISyncRepository SyncRepo { get; }
    private IExpressionEvaluator ExpressionEvaluator { get; }
    #endregion

    /// <summary>
    /// The pure decision engine, stateless and zero-dependency by design, so constructed inline as the
    /// other sync servers do.
    /// </summary>
    private readonly ISyncEngine _syncEngine = new SyncEngine();

    #region constructors
    internal SyncPreviewServer(JimApplication application, ISyncRepository syncRepository)
    {
        Application = application;
        SyncRepo = syncRepository;
        ExpressionEvaluator = new DynamicExpressoEvaluator();
    }
    #endregion

    #region public methods

    /// <summary>
    /// Previews what a synchronisation of one Metaverse Object would do now: the outbound decisions per
    /// export Synchronisation Rule, composed into the preview result with a speculative outcome tree.
    /// An MVO has no inbound chain, so <see cref="SyncPreviewResult.Inbound"/> is null.
    /// </summary>
    /// <param name="metaverseObjectId">The Metaverse Object to preview.</param>
    /// <param name="repositoryFactory">Optional factory for a preview-owned repository scope (its own
    /// DbContext), so the rolled-back transaction can never entangle a live run's context. When omitted,
    /// the ambient repository is used behind the guard.</param>
    public async Task<SyncPreviewResult> PreviewSyncForMvoAsync(
        Guid metaverseObjectId,
        Func<ISyncRepositoryScope>? repositoryFactory = null)
    {
        var result = new SyncPreviewResult();

        using var scope = repositoryFactory?.Invoke();
        var guardedRepository = new ReadOnlySyncRepositoryGuard(scope?.Repository ?? SyncRepo);
        var previewServer = new ExportEvaluationServer(Application, guardedRepository);
        await using var rollbackScope = await guardedRepository.BeginRollbackOnlyTransactionAsync();

        var mvo = (await guardedRepository.GetMetaverseObjectsByIdsNoTrackingAsync([metaverseObjectId]))
            .SingleOrDefault();
        if (mvo == null)
        {
            result.Errors.Add(new SyncPreviewMessage
            {
                Code = SyncPreviewMessageCode.ObjectNotFound,
                Detail = $"Metaverse Object {metaverseObjectId} does not exist."
            });
            return result;
        }

        var cache = await previewServer.BuildExportEvaluationCacheAsync();
        await previewServer.RefreshExportEvaluationCacheForPageAsync(cache, [metaverseObjectId]);

        var outbound = await previewServer.EvaluateOutboundPreviewForMaterialisedMvosAsync([mvo], cache);
        ComposeOutbound(result, outbound);
        BuildOutboundOutcomeNodes(result.OutcomeTree, outbound, BuildConnectedSystemNameLookup(cache));

        Log.Debug("PreviewSyncForMvoAsync: Previewed MVO {MvoId}: {EntryCount} outbound decision(s), {ErrorCount} error(s), {WarningCount} warning(s).",
            metaverseObjectId, outbound.Entries.Count, result.Errors.Count, result.Warnings.Count);
        return result;
    }

    /// <summary>
    /// Previews a set of Metaverse Objects in one pass, optionally against a proposed export Synchronisation Rule,
    /// building the shared export evaluation cache once for the whole set (#1437).
    /// </summary>
    /// <remarks>
    /// The outbound sibling of <see cref="PreviewSyncForCsosAsync"/>, and it exists for the same reason: a
    /// configuration change preview asks the same question of many objects, and the cache of export rules, target
    /// systems and system names is identical for every one of them.
    /// </remarks>
    /// <param name="metaverseObjectIds">The Metaverse Objects to preview.</param>
    /// <param name="proposedSyncRule">
    /// An unsaved export rule to evaluate in place of the stored rule of the same id. Substituted into the rule set
    /// the export evaluation cache is built from, so the whole outbound chain answers for the proposal; substituted
    /// by id and never added, exactly as the inbound path does.
    /// </param>
    /// <param name="cancellationToken">Honoured between objects; a cancelled preview stops rather than completing.</param>
    /// <param name="repositoryFactory">Optional factory for a preview-owned repository scope; see
    /// <see cref="PreviewSyncForMvoAsync"/>.</param>
    /// <param name="proposedRuleSet">
    /// A proposal about the rule SET rather than about one rule's contents: a rule that would start being
    /// evaluated, or stop. Takes precedence over <paramref name="proposedSyncRule"/>, which is the substitution
    /// case of the same idea. Needed because substitution alone cannot express the Enabled toggle (#1462): a
    /// disabled rule is not in the loaded set for a substitution to find, and a disabled stand-in substituted into
    /// it stays in the list, since nothing downstream of the load re-checks Enabled.
    /// </param>
    public async Task<Dictionary<Guid, SyncPreviewResult>> PreviewSyncForMvosAsync(
        IReadOnlyCollection<Guid> metaverseObjectIds,
        SyncRule? proposedSyncRule = null,
        CancellationToken cancellationToken = default,
        Func<ISyncRepositoryScope>? repositoryFactory = null,
        ProposedSyncRuleSet? proposedRuleSet = null)
    {
        var proposal = Proposal(proposedSyncRule, proposedRuleSet);
        ArgumentNullException.ThrowIfNull(metaverseObjectIds);

        var results = new Dictionary<Guid, SyncPreviewResult>();
        if (metaverseObjectIds.Count == 0)
            return results;

        using var scope = repositoryFactory?.Invoke();
        var guardedRepository = new ReadOnlySyncRepositoryGuard(scope?.Repository ?? SyncRepo);
        var previewServer = new ExportEvaluationServer(Application, guardedRepository);
        await using var rollbackScope = await guardedRepository.BeginRollbackOnlyTransactionAsync();

        var cache = await previewServer.BuildExportEvaluationCacheAsync(
            await LoadRulesForCacheAsync(guardedRepository, proposal));
        var systemNames = BuildConnectedSystemNameLookup(cache);

        var mvos = await guardedRepository.GetMetaverseObjectsByIdsNoTrackingAsync([.. metaverseObjectIds]);
        if (mvos.Count == 0)
            return results;

        await previewServer.RefreshExportEvaluationCacheForPageAsync(cache, [.. mvos.Select(mvo => mvo.Id)]);

        foreach (var mvo in mvos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = new SyncPreviewResult();
            var outbound = await previewServer.EvaluateOutboundPreviewForMaterialisedMvosAsync([mvo], cache);
            ComposeOutbound(result, outbound);
            BuildOutboundOutcomeNodes(result.OutcomeTree, outbound, systemNames);
            results[mvo.Id] = result;
        }

        Log.Debug("PreviewSyncForMvosAsync: Previewed {Count} Metaverse Object(s){Proposed}.",
            results.Count, proposal == null ? string.Empty : " against a proposed Synchronisation Rule");
        return results;
    }

    /// <summary>
    /// The Synchronisation Rules the export evaluation cache is built from, with a proposal substituted for the
    /// stored rule it edits. Null when there is no proposal, so the cache loads the rules itself as it always has.
    /// </summary>
    private static async Task<List<SyncRule>?> LoadRulesForCacheAsync(
        ISyncRepository guardedRepository,
        ProposedSyncRuleSet? proposal)
    {
        if (proposal == null)
            return null;

        var allSyncRules = await guardedRepository.GetAllSyncRulesAsync();
        Substitute(allSyncRules, proposal);
        return allSyncRules;
    }

    /// <summary>
    /// Previews what a synchronisation of one Connected System Object would do now: the inbound chain
    /// (scope, join or projection, Attribute Flow) followed by the outbound decisions the prospective
    /// Metaverse Object state would produce, composed into the preview result with a speculative outcome
    /// tree. The join is probed read-only and never claimed; a projection's Metaverse Object exists only
    /// in memory.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System holding the object.</param>
    /// <param name="connectedSystemObjectId">The Connected System Object to preview.</param>
    /// <param name="repositoryFactory">Optional factory for a preview-owned repository scope; see
    /// <see cref="PreviewSyncForMvoAsync"/>.</param>
    /// <param name="proposedSyncRule">
    /// An unsaved Synchronisation Rule to evaluate in place of the stored rule of the same id, so a configuration
    /// change preview can ask what a synchronisation would do AFTER a proposed edit rather than only what it would
    /// do now (#1436). The substitute is used exactly where the stored rule would have been, scope gate included,
    /// so the whole chain answers for the proposal rather than the adapter reimplementing any part of it.
    ///
    /// Substituted by id into the loaded set, never added to it: a rule that is disabled (and so absent) stays
    /// absent, because previewing a disabled rule's proposed scope as though the rule also became enabled would
    /// answer a question nobody asked.
    /// </param>
    /// <param name="proposedRuleSet">
    /// A proposal about the rule SET rather than about one rule's contents: a rule that would start being
    /// evaluated, or stop. Takes precedence over <paramref name="proposedSyncRule"/>, which is the substitution
    /// case of the same idea. Needed because substitution alone cannot express the Enabled toggle (#1462): a
    /// disabled rule is not in the loaded set for a substitution to find, and a disabled stand-in substituted into
    /// it stays in the list, since nothing downstream of the load re-checks Enabled.
    /// </param>
    public async Task<SyncPreviewResult> PreviewSyncForCsoAsync(
        int connectedSystemId,
        Guid connectedSystemObjectId,
        Func<ISyncRepositoryScope>? repositoryFactory = null,
        SyncRule? proposedSyncRule = null,
        ProposedSyncRuleSet? proposedRuleSet = null)
    {
        using var scope = repositoryFactory?.Invoke();
        var guardedRepository = new ReadOnlySyncRepositoryGuard(scope?.Repository ?? SyncRepo);
        var previewServer = new ExportEvaluationServer(Application, guardedRepository);
        await using var rollbackScope = await guardedRepository.BeginRollbackOnlyTransactionAsync();

        var cso = await guardedRepository.GetConnectedSystemObjectAsync(connectedSystemId, connectedSystemObjectId);
        if (cso == null)
        {
            var notFound = new SyncPreviewResult();
            notFound.Errors.Add(new SyncPreviewMessage
            {
                Code = SyncPreviewMessageCode.ObjectNotFound,
                Detail = $"Connected System Object {connectedSystemObjectId} does not exist in Connected System {connectedSystemId}.",
                ConnectedSystemId = connectedSystemId
            });
            return notFound;
        }

        var context = await BuildCsoPreviewContextAsync(connectedSystemId, guardedRepository, previewServer,
            Proposal(proposedSyncRule, proposedRuleSet));
        return await PreviewCsoCoreAsync(cso, context, refreshCacheForWorkingMvo: true);
    }

    /// <summary>
    /// Previews a set of Connected System Objects in one pass, optionally against a proposed Synchronisation Rule,
    /// building the shared evaluation context once for the whole set (#1436).
    /// </summary>
    /// <remarks>
    /// Exists because a configuration change preview asks the same question of many objects at once. Calling
    /// <see cref="PreviewSyncForCsoAsync"/> per object would rebuild the rules, the object types and the whole
    /// export evaluation cache each time, which is the expensive half of a single-object preview and is identical
    /// for every object in the set.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System holding the objects.</param>
    /// <param name="connectedSystemObjectIds">The objects to preview, in the order results are wanted.</param>
    /// <param name="proposedSyncRule">
    /// An unsaved rule to evaluate in place of the stored rule of the same id; see
    /// <see cref="PreviewSyncForCsoAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Honoured between objects; a cancelled preview stops rather than completing.</param>
    /// <param name="repositoryFactory">Optional factory for a preview-owned repository scope; see
    /// <see cref="PreviewSyncForMvoAsync"/>.</param>
    /// <param name="proposedRuleSet">
    /// A proposal about the rule SET rather than about one rule's contents: a rule that would start being
    /// evaluated, or stop. Takes precedence over <paramref name="proposedSyncRule"/>, which is the substitution
    /// case of the same idea. Needed because substitution alone cannot express the Enabled toggle (#1462): a
    /// disabled rule is not in the loaded set for a substitution to find, and a disabled stand-in substituted into
    /// it stays in the list, since nothing downstream of the load re-checks Enabled.
    /// </param>
    public async Task<Dictionary<Guid, SyncPreviewResult>> PreviewSyncForCsosAsync(
        int connectedSystemId,
        IReadOnlyCollection<Guid> connectedSystemObjectIds,
        SyncRule? proposedSyncRule = null,
        CancellationToken cancellationToken = default,
        Func<ISyncRepositoryScope>? repositoryFactory = null,
        ProposedSyncRuleSet? proposedRuleSet = null)
    {
        ArgumentNullException.ThrowIfNull(connectedSystemObjectIds);

        var results = new Dictionary<Guid, SyncPreviewResult>();
        if (connectedSystemObjectIds.Count == 0)
            return results;

        using var scope = repositoryFactory?.Invoke();
        var guardedRepository = new ReadOnlySyncRepositoryGuard(scope?.Repository ?? SyncRepo);
        var previewServer = new ExportEvaluationServer(Application, guardedRepository);
        await using var rollbackScope = await guardedRepository.BeginRollbackOnlyTransactionAsync();

        var context = await BuildCsoPreviewContextAsync(connectedSystemId, guardedRepository, previewServer,
            Proposal(proposedSyncRule, proposedRuleSet));

        foreach (var connectedSystemObjectId in connectedSystemObjectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cso = await guardedRepository.GetConnectedSystemObjectAsync(connectedSystemId, connectedSystemObjectId);
            if (cso == null)
                continue;

            results[connectedSystemObjectId] = await PreviewCsoCoreAsync(cso, context, refreshCacheForWorkingMvo: true);
        }

        return results;
    }

    /// <summary>
    /// Previews what a full synchronisation of one Connected System would do now (#288 plan Phase 4, PRD
    /// decision D2): every object is classified into the whole-population count tier, a bounded number of
    /// full outcome trees is retained per category, and an explicit work budget (object cap and/or time)
    /// stops the walk with the truncation flagged, so a 100K+ system cannot run unbounded and cannot hold
    /// 100K trees in memory. Runs under the same defence-in-depth backstops as the single-object previews.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System to preview.</param>
    /// <param name="options">The work budget and sampling bounds; defaults applied when omitted.</param>
    /// <param name="repositoryFactory">Optional factory for a preview-owned repository scope; see
    /// <see cref="PreviewSyncForMvoAsync"/>.</param>
    public async Task<FullSyncPreviewResult> PreviewFullSyncAsync(
        int connectedSystemId,
        FullSyncPreviewOptions? options = null,
        Func<ISyncRepositoryScope>? repositoryFactory = null)
    {
        options ??= new FullSyncPreviewOptions();
        var result = new FullSyncPreviewResult { ConnectedSystemId = connectedSystemId };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var scope = repositoryFactory?.Invoke();
        var guardedRepository = new ReadOnlySyncRepositoryGuard(scope?.Repository ?? SyncRepo);
        var previewServer = new ExportEvaluationServer(Application, guardedRepository);
        await using var rollbackScope = await guardedRepository.BeginRollbackOnlyTransactionAsync();

        result.TotalObjectCount = await guardedRepository.GetConnectedSystemObjectCountAsync(connectedSystemId);
        if (result.TotalObjectCount == 0)
            return result;

        var context = await BuildCsoPreviewContextAsync(connectedSystemId, guardedRepository, previewServer);
        var sampleCountsByCategory = new Dictionary<FullSyncPreviewCategory, int>();

        // Keyset pagination from the zero GUID, matching the sync processors' population walk.
        var afterId = Guid.Empty;
        var stopped = false;
        while (!stopped)
        {
            var page = await guardedRepository.GetConnectedSystemObjectsAsync(
                connectedSystemId, page: 1, pageSize: options.PageSize,
                knownTotalCount: result.TotalObjectCount, afterId: afterId);
            if (page.Results.Count == 0)
                break;
            afterId = page.Results[^1].Id;

            // One outbound-cache refresh per page for the joined objects' Metaverse Objects, instead of
            // one per object inside the core.
            var joinedMvoIds = page.Results
                .Where(c => c.MetaverseObjectId.HasValue)
                .Select(c => c.MetaverseObjectId!.Value)
                .ToList();
            if (joinedMvoIds.Count > 0)
                await previewServer.RefreshExportEvaluationCacheForPageAsync(context.Cache, joinedMvoIds);

            foreach (var cso in page.Results)
            {
                if (options.TimeBudget.HasValue && stopwatch.Elapsed >= options.TimeBudget.Value)
                {
                    result.Truncated = true;
                    result.TruncationReason = FullSyncPreviewTruncationReason.TimeBudgetExhausted;
                    stopped = true;
                    break;
                }
                if (options.MaxObjects.HasValue && result.EvaluatedObjectCount >= options.MaxObjects.Value)
                {
                    result.Truncated = true;
                    result.TruncationReason = FullSyncPreviewTruncationReason.ObjectCapReached;
                    stopped = true;
                    break;
                }
                if (cso.Status == ConnectedSystemObjectStatus.Obsolete)
                {
                    result.SkippedObjectCount++;
                    continue;
                }

                var preview = await PreviewCsoCoreAsync(cso, context, refreshCacheForWorkingMvo: false);
                result.EvaluatedObjectCount++;

                var category = Categorise(preview);
                AddToCounts(result.Counts, category, preview);

                var retained = sampleCountsByCategory.GetValueOrDefault(category);
                if (retained < options.SampleTreesPerCategory)
                {
                    sampleCountsByCategory[category] = retained + 1;
                    result.Samples.Add(new FullSyncPreviewSample
                    {
                        Category = category,
                        ConnectedSystemObjectId = cso.Id,
                        Preview = preview
                    });
                }
            }

            if (page.Results.Count < options.PageSize)
                break;
        }

        Log.Information("PreviewFullSyncAsync: Previewed Connected System {SystemId}: {Evaluated}/{Total} object(s) evaluated ({Skipped} skipped), " +
            "{Project} would project, {Join} would join, {Flow} attribute flow, {OutOfScope} out of scope, {NotConnected} not connected, {Blocked} blocked; " +
            "{Creates} creates, {Updates} updates, {Deletes} deletes proposed; truncated: {Truncated} ({Reason}); {Elapsed:0.0}s.",
            connectedSystemId, result.EvaluatedObjectCount, result.TotalObjectCount, result.SkippedObjectCount,
            result.Counts.WouldProject, result.Counts.WouldJoin, result.Counts.AttributeFlow, result.Counts.OutOfScope,
            result.Counts.NotConnected, result.Counts.BlockedByErrors,
            result.Counts.ObjectsToCreate, result.Counts.ObjectsToUpdate, result.Counts.ObjectsToDelete,
            result.Truncated, result.TruncationReason, stopwatch.Elapsed.TotalSeconds);
        return result;
    }

    #endregion

    #region private methods

    /// <summary>
    /// Builds the shared, read-only inputs one Connected System's CSO previews evaluate against: the
    /// enabled Synchronisation Rules, the object types, the outbound evaluation cache and the target
    /// system name lookup. Built once per single-object preview, and once for a whole full-system walk.
    /// </summary>
    private static async Task<CsoPreviewContext> BuildCsoPreviewContextAsync(
        int connectedSystemId,
        ISyncRepository guardedRepository,
        ExportEvaluationServer previewServer,
        ProposedSyncRuleSet? proposal = null)
    {
        var syncRules = await guardedRepository.GetSyncRulesAsync(connectedSystemId, includeDisabled: false);
        Substitute(syncRules, proposal);

        // The Attribute Priority contributors, from EVERY rule across EVERY Connected System, exactly as the real
        // synchronisation builds them (#1441). Attribute ownership is a property of the whole configuration, not of
        // the system being previewed: the rule that owns an attribute is routinely one on another system, and a
        // context built from this system's rules alone would report that rule as no contributor at all.
        var allSyncRules = await guardedRepository.GetAllSyncRulesAsync();
        Substitute(allSyncRules, proposal);
        var priorityContext = new AttributePriorityContext(allSyncRules, honourNullAssertions: true);

        var objectTypes = await guardedRepository.GetObjectTypesAsync(connectedSystemId);
        var cache = await previewServer.BuildExportEvaluationCacheAsync();
        return new CsoPreviewContext(connectedSystemId, previewServer, syncRules, objectTypes, cache,
            BuildConnectedSystemNameLookup(cache), guardedRepository, priorityContext);
    }

    /// <summary>
    /// Swaps a proposed Synchronisation Rule in for the stored rule of the same id, in place.
    /// </summary>
    /// <remarks>
    /// Positional, so the rule keeps its place in the order the engine applies rules in, and never added: a rule
    /// that is disabled (and so absent) stays absent. Applied to the priority contributors as well as to the rules
    /// that flow, because a proposal that changes a mapping's Priority has to be resolved against the proposal's
    /// own priorities; resolving it against the stored ones would answer for a configuration that never existed.
    /// </remarks>
    /// <summary>
    /// The proposal to apply to a loaded rule set, from whichever of the two parameters the caller supplied.
    /// </summary>
    /// <remarks>
    /// A caller passing <c>proposedSyncRule</c> is asking for the substitution case of
    /// <see cref="ProposedSyncRuleSet"/>, so it is expressed as one rather than handled separately: the engine
    /// keeps a single notion of what a proposal is, and the two entry shapes cannot drift apart.
    /// </remarks>
    private static ProposedSyncRuleSet? Proposal(SyncRule? proposedSyncRule, ProposedSyncRuleSet? proposedRuleSet) =>
        proposedRuleSet ?? (proposedSyncRule == null ? null : ProposedSyncRuleSet.Substituting(proposedSyncRule));

    private static void Substitute(List<SyncRule> syncRules, ProposedSyncRuleSet? proposal) =>
        proposal?.Apply(syncRules);

    /// <summary>
    /// The per-object CSO preview core shared by <see cref="PreviewSyncForCsoAsync"/> and
    /// <see cref="PreviewFullSyncAsync"/>: the inbound chain evaluated read-only against the context's
    /// shared inputs, then the outbound chain over the prospective Metaverse Object state.
    /// </summary>
    /// <param name="cso">The Connected System Object to preview.</param>
    /// <param name="context">The shared read-only inputs for the object's Connected System.</param>
    /// <param name="refreshCacheForWorkingMvo">Whether to refresh the outbound cache for the working
    /// Metaverse Object before evaluating outbound. Single-object previews pass true; the full-system walk
    /// passes false, having refreshed the whole page's joined Metaverse Objects in one call.</param>
    private async Task<SyncPreviewResult> PreviewCsoCoreAsync(
        ConnectedSystemObject cso,
        CsoPreviewContext context,
        bool refreshCacheForWorkingMvo)
    {
        var result = new SyncPreviewResult();
        var connectedSystemId = context.ConnectedSystemId;
        var guardedRepository = context.GuardedRepository;
        var previewServer = context.PreviewServer;
        var objectTypes = context.ObjectTypes;

        var inbound = new SyncPreviewInboundSummary();
        result.Inbound = inbound;

        // The applicable import Synchronisation Rules, per the real processor's filter.
        var importRules = context.SyncRules
            .Where(sr => sr.Direction == SyncRuleDirection.Import && sr.ConnectedSystemObjectTypeId == cso.TypeId)
            .ToList();
        if (importRules.Count == 0)
        {
            result.Warnings.Add(new SyncPreviewMessage
            {
                Code = SyncPreviewMessageCode.NoApplicableSyncRule,
                Detail = "No enabled import Synchronisation Rule applies to this object's type; a synchronisation would not process it inbound.",
                ConnectedSystemId = connectedSystemId
            });
            return result;
        }

        // Scope is per rule (#1199): an unscoped rule is in scope; a CSO out of scope of every scoped rule
        // stops the chain exactly as the real processor's out-of-scope handling would.
        var inScopeRules = importRules
            .Where(sr => sr.ObjectScopingCriteriaGroups.Count == 0
                || Application.ScopingEvaluation.IsCsoInScopeForImportRule(cso, sr))
            .ToList();
        if (inScopeRules.Count == 0 && importRules.Any(sr => sr.ObjectScopingCriteriaGroups.Count > 0))
        {
            var outOfScopeAction = _syncEngine.DetermineOutOfScopeAction(cso, importRules);
            result.Warnings.Add(new SyncPreviewMessage
            {
                Code = SyncPreviewMessageCode.OutOfScope,
                Detail = $"The object is out of scope of every import Synchronisation Rule with Scoping Criteria; a synchronisation would apply the out-of-scope action '{outOfScopeAction}'.",
                ConnectedSystemId = connectedSystemId
            });
            return result;
        }

        // Resolve the working Metaverse Object: the joined one, a read-only probed match (never claimed),
        // or a prospective in-memory projection. The working object is always the preview's own copy, so
        // Attribute Flow can mutate it without touching shared state.
        SyncRule? projectionSyncRule = null;
        MetaverseObject? workingMvo;
        if (cso.MetaverseObjectId.HasValue)
        {
            inbound.AlreadyJoinedMetaverseObjectId = cso.MetaverseObjectId;
            var joinedMvo = (await guardedRepository.GetMetaverseObjectsByIdsNoTrackingAsync([cso.MetaverseObjectId.Value]))
                .SingleOrDefault();
            if (joinedMvo == null)
            {
                result.Errors.Add(new SyncPreviewMessage
                {
                    Code = SyncPreviewMessageCode.ObjectNotFound,
                    Detail = $"The joined Metaverse Object {cso.MetaverseObjectId} could not be loaded.",
                    ConnectedSystemId = connectedSystemId
                });
                return result;
            }
            workingMvo = CloneForPreview(joinedMvo);
        }
        else
        {
            var matchedMvo = await ProbeForJoinAsync(cso, inScopeRules, objectTypes, guardedRepository, result);
            if (result.HasBlockingErrors)
                return result;

            if (matchedMvo != null)
            {
                inbound.WouldJoinMetaverseObjectId = matchedMvo.Id;
                workingMvo = CloneForPreview(matchedMvo);
            }
            else
            {
                var projectionDecision = _syncEngine.EvaluateProjection(cso, inScopeRules);
                if (!projectionDecision.ShouldProject)
                {
                    result.Warnings.Add(new SyncPreviewMessage
                    {
                        Code = SyncPreviewMessageCode.NoApplicableSyncRule,
                        Detail = "No Object Matching Rule matched an existing Metaverse Object and no import Synchronisation Rule projects; a synchronisation would leave the object unconnected.",
                        ConnectedSystemId = connectedSystemId
                    });
                    return result;
                }

                projectionSyncRule = projectionDecision.ProjectionSyncRule;
                inbound.WouldProject = true;
                inbound.ProjectedMetaverseObjectTypeId = projectionDecision.MetaverseObjectType!.Id;
                inbound.ProjectedMetaverseObjectTypeName = projectionDecision.MetaverseObjectType.Name;
                workingMvo = new MetaverseObject { Type = projectionDecision.MetaverseObjectType };
            }
        }

        // Inbound Attribute Flow onto the working copy, in one pass (references included: for a single
        // object preview, every other object's join state already exists, so no deferred pass is needed).
        var flowErrors = new List<(SyncRule Rule, AttributeFlowError Error)>();
        var originalMetaverseObject = cso.MetaverseObject;
        try
        {
            cso.MetaverseObject = workingMvo;
            foreach (var rule in inScopeRules)
            {
                try
                {
                    foreach (var flowError in _syncEngine.FlowInboundAttributes(cso, rule, objectTypes, ExpressionEvaluator,
                        priorityContext: context.PriorityContext))
                        flowErrors.Add((rule, flowError));
                }
                catch (SyncExpressionEvaluationException expressionEx)
                {
                    result.Errors.Add(new SyncPreviewMessage
                    {
                        Code = SyncPreviewMessageCode.ExpressionEvaluationError,
                        Detail = $"An Expression failed to evaluate: {expressionEx.Message}",
                        SyncRuleId = rule.Id,
                        SyncRuleName = rule.Name,
                        ConnectedSystemId = connectedSystemId
                    });
                }
            }
        }
        finally
        {
            // The CSO instance may be shared (an in-memory repository hands out its stored instance);
            // the working object is the preview's alone, so the only mutation to undo is this link.
            cso.MetaverseObject = originalMetaverseObject;
        }

        foreach (var (rule, flowError) in flowErrors)
        {
            result.Errors.Add(new SyncPreviewMessage
            {
                Code = flowError.Kind == AttributeFlowErrorKind.MultiValuedToSingleValued
                    ? SyncPreviewMessageCode.MultiValuedToSingleValuedFlow
                    : SyncPreviewMessageCode.ExpressionEvaluationError,
                Detail = flowError.Kind == AttributeFlowErrorKind.MultiValuedToSingleValued
                    ? $"The multi-valued source attribute '{flowError.SourceAttributeName}' holds {flowError.ValueCount} values but flows to the single-valued attribute '{flowError.TargetAttributeName}'; the attribute would not flow."
                    : $"The Expression targeting '{flowError.TargetAttributeName}' was not evaluated: a required input has no value.",
                SyncRuleId = rule.Id,
                SyncRuleName = rule.Name,
                ConnectedSystemId = connectedSystemId,
                AttributeName = flowError.TargetAttributeName
            });
        }

        // Capture the flows before applying them, exactly as the real processor snapshots its change lists.
        foreach (var addition in workingMvo.PendingAttributeValueAdditions)
            inbound.AttributeFlowChanges.Add(BuildAttributeFlowChange(addition, isAddition: true));
        foreach (var removal in workingMvo.PendingAttributeValueRemovals)
            inbound.AttributeFlowChanges.Add(BuildAttributeFlowChange(removal, isAddition: false));
        var flowCount = inbound.AttributeFlowChanges.Count;

        _syncEngine.ApplyPendingAttributeChanges(workingMvo);

        // The outbound chain over the prospective Metaverse Object state, against the context's shared cache.
        if (refreshCacheForWorkingMvo && workingMvo.Id != Guid.Empty)
            await previewServer.RefreshExportEvaluationCacheForPageAsync(context.Cache, [workingMvo.Id]);
        var outbound = await previewServer.EvaluateOutboundPreviewForMaterialisedMvosAsync([workingMvo], context.Cache);
        ComposeOutbound(result, outbound);

        foreach (var rule in inScopeRules.Where(rule => result.AffectedSyncRules.All(r => r.Id != rule.Id)))
            result.AffectedSyncRules.Add(new SyncPreviewSyncRuleReference { Id = rule.Id, Name = rule.Name });

        // The speculative outcome tree, in the real tree's shape: a Projected/Joined/AttributeFlow root
        // when attributes would flow, an Attribute Flow child under Projected/Joined, and the outbound
        // outcomes beneath (mirroring the real processor's exportParent selection).
        if (flowCount > 0)
        {
            var rootType = inbound.WouldProject ? ActivityRunProfileExecutionItemSyncOutcomeType.Projected
                : inbound.WouldJoinMetaverseObjectId.HasValue ? ActivityRunProfileExecutionItemSyncOutcomeType.Joined
                : ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow;
            var root = new SyncOutcomeNode
            {
                OutcomeType = rootType,
                TargetEntityId = workingMvo.Id != Guid.Empty ? workingMvo.Id : null,
                TargetEntityDescription = ObjectNaming.FirstPresent(workingMvo.Name),
                SyncRuleId = rootType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected ? projectionSyncRule?.Id : null,
                SyncRuleName = rootType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected ? projectionSyncRule?.Name : null
            };
            result.OutcomeTree.Add(root);

            SyncOutcomeNode? attributeFlowChild = null;
            if (rootType is ActivityRunProfileExecutionItemSyncOutcomeType.Projected
                or ActivityRunProfileExecutionItemSyncOutcomeType.Joined)
            {
                attributeFlowChild = new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                    TargetEntityDescription = root.TargetEntityDescription,
                    DetailCount = flowCount,
                    Ordinal = root.Children.Count
                };
                root.Children.Add(attributeFlowChild);
            }

            // The outbound outcomes nest under the Attribute Flow child where there is one, because what
            // is exported is caused by what flowed in. Fidelity (PRD requirement 9, the paired test)
            // mirrors what a real run RECORDS rather than what its comments intend, so this line follows
            // the real builder in SyncTaskProcessorBase; keep the two moving together (#1428).
            BuildOutboundOutcomeNodes(attributeFlowChild?.Children ?? root.Children, outbound, context.SystemNames);
        }
        else
        {
            BuildOutboundOutcomeNodes(result.OutcomeTree, outbound, context.SystemNames);
        }

        Log.Debug("PreviewCsoCoreAsync: Previewed CSO {CsoId} in system {SystemId}: {FlowCount} inbound flow(s), {EntryCount} outbound decision(s), {ErrorCount} error(s), {WarningCount} warning(s).",
            cso.Id, connectedSystemId, flowCount, outbound.Entries.Count, result.Errors.Count, result.Warnings.Count);
        return result;
    }

    /// <summary>
    /// The shared, read-only inputs one Connected System's CSO previews evaluate against.
    /// </summary>
    private sealed record CsoPreviewContext(
        int ConnectedSystemId,
        ExportEvaluationServer PreviewServer,
        List<SyncRule> SyncRules,
        List<ConnectedSystemObjectType> ObjectTypes,
        ExportEvaluationCache Cache,
        Dictionary<int, string> SystemNames,
        ISyncRepository GuardedRepository,
        AttributePriorityContext PriorityContext);

    /// <summary>
    /// Classifies one per-object preview into its full-system category. Blocking errors take precedence:
    /// an object that both projects and errors is a problem to fix before it is a projection.
    /// </summary>
    private static FullSyncPreviewCategory Categorise(SyncPreviewResult preview)
    {
        if (preview.HasBlockingErrors)
            return FullSyncPreviewCategory.BlockedByErrors;
        if (preview.Warnings.Any(w => w.Code == SyncPreviewMessageCode.OutOfScope))
            return FullSyncPreviewCategory.OutOfScope;

        var inbound = preview.Inbound;
        if (inbound == null)
            return FullSyncPreviewCategory.NotConnected;
        if (inbound.WouldProject)
            return FullSyncPreviewCategory.WouldProject;
        if (inbound.WouldJoinMetaverseObjectId.HasValue)
            return FullSyncPreviewCategory.WouldJoin;
        if (inbound.AlreadyJoinedMetaverseObjectId.HasValue)
            return FullSyncPreviewCategory.AttributeFlow;
        return FullSyncPreviewCategory.NotConnected;
    }

    /// <summary>
    /// Folds one per-object preview into the whole-population count tier.
    /// </summary>
    private static void AddToCounts(FullSyncPreviewCounts counts, FullSyncPreviewCategory category, SyncPreviewResult preview)
    {
        switch (category)
        {
            case FullSyncPreviewCategory.WouldProject: counts.WouldProject++; break;
            case FullSyncPreviewCategory.WouldJoin: counts.WouldJoin++; break;
            case FullSyncPreviewCategory.AttributeFlow: counts.AttributeFlow++; break;
            case FullSyncPreviewCategory.OutOfScope: counts.OutOfScope++; break;
            case FullSyncPreviewCategory.NotConnected: counts.NotConnected++; break;
            case FullSyncPreviewCategory.BlockedByErrors: counts.BlockedByErrors++; break;
        }

        counts.ObjectsToCreate += preview.Outbound.ObjectsToCreate;
        counts.ObjectsToUpdate += preview.Outbound.ObjectsToUpdate;
        counts.ObjectsToDelete += preview.Outbound.ObjectsToDelete;
        counts.TotalAttributeChanges += preview.Outbound.TotalAttributeChanges;
    }

    /// <summary>
    /// Probes the Object Matching Rules for an existing Metaverse Object the Connected System Object would
    /// join, without claiming anything. Advanced mode reads each import rule's own matching rules; simple
    /// mode's rules live on the object type. An ambiguous match is an expected block: it lands in the
    /// result's Errors, exactly as the real synchronisation would fail the object with an AmbiguousMatch.
    /// </summary>
    private async Task<MetaverseObject?> ProbeForJoinAsync(
        ConnectedSystemObject cso,
        List<SyncRule> inScopeRules,
        List<ConnectedSystemObjectType> objectTypes,
        ISyncRepository guardedRepository,
        SyncPreviewResult result)
    {
        var candidateRuleSets = new List<List<ObjectMatchingRule>>();
        foreach (var importRule in inScopeRules.Where(sr => sr.ObjectMatchingRules.Count > 0))
        {
            var matchingRules = importRule.ObjectMatchingRules.ToList();
            foreach (var matchingRule in matchingRules.Where(mr => mr.MetaverseObjectType == null))
                matchingRule.MetaverseObjectType = importRule.MetaverseObjectType;
            candidateRuleSets.Add(matchingRules);
        }

        // Simple mode fallback, as the real join path implements it: matching rules on the object type
        // itself, each carrying its own Metaverse Object Type. In advanced mode this set is empty.
        if (candidateRuleSets.Count == 0)
        {
            var typeRules = objectTypes.FirstOrDefault(ot => ot.Id == cso.TypeId)?.ObjectMatchingRules?.ToList();
            if (typeRules is { Count: > 0 })
                candidateRuleSets.Add(typeRules);
        }

        foreach (var matchingRules in candidateRuleSets)
        {
            foreach (var matchingRule in matchingRules.OrderBy(mr => mr.Order).Where(mr => mr.MetaverseObjectType != null))
            {
                try
                {
                    var mvo = await guardedRepository.FindMetaverseObjectUsingMatchingRuleAsync(
                        cso, matchingRule.MetaverseObjectType!, matchingRule);
                    if (mvo != null)
                        return mvo;
                }
                catch (MultipleMatchesException ex)
                {
                    result.Errors.Add(new SyncPreviewMessage
                    {
                        Code = SyncPreviewMessageCode.AmbiguousMatch,
                        Detail = $"Multiple Metaverse Objects ({ex.Matches.Count}) match this Connected System Object; a synchronisation would fail it with an AmbiguousMatch error. Matching MVO IDs: {string.Join(", ", ex.Matches)}",
                        ConnectedSystemId = cso.ConnectedSystemId
                    });
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A preview-owned copy of a Metaverse Object, so Attribute Flow and the pending-change application
    /// mutate the preview's state and never a shared instance (an in-memory repository hands out its
    /// stored objects; a database read is already detached). Value instances are shared, not copied:
    /// the flow adds and removes list entries but never mutates a value in place.
    /// </summary>
    private static MetaverseObject CloneForPreview(MetaverseObject mvo)
    {
        var clone = new MetaverseObject
        {
            Id = mvo.Id,
            Type = mvo.Type,
            Origin = mvo.Origin,
            CachedDisplayName = mvo.CachedDisplayName
        };
        foreach (var attributeValue in mvo.AttributeValues)
            clone.AttributeValues.Add(attributeValue);
        return clone;
    }

    /// <summary>
    /// Composes the outbound decision records into the preview result (PRD requirement 2: the proposed
    /// exports and the create/update/delete counters have one definition,
    /// <see cref="ExportEvaluationPreviewResult"/>), and reports the participating rules.
    /// </summary>
    private static void ComposeOutbound(SyncPreviewResult result, OutboundPreviewResult outbound)
    {
        result.OutboundDecisions = outbound;

        foreach (var entry in outbound.Entries)
        {
            if (result.AffectedSyncRules.All(r => r.Id != entry.SyncRuleId))
                result.AffectedSyncRules.Add(new SyncPreviewSyncRuleReference { Id = entry.SyncRuleId, Name = entry.SyncRuleName });

            switch (entry.Kind)
            {
                // A real evaluation stages a Pending Export for a Create always (provisioning carries the
                // initial values) and for an Update only when there are changes to write.
                case OutboundPreviewEntryKind.Staging when entry.EffectiveChangeType == PendingExportChangeType.Create
                    || (entry.EffectiveChangeType.HasValue && entry.AttributeChanges.Count > 0):
                    result.Outbound.ProposedExports.Add(new PendingExport
                    {
                        ChangeType = entry.EffectiveChangeType!.Value,
                        ConnectedSystemId = entry.ConnectedSystemId,
                        ConnectedSystemObjectId = entry.WouldJoinCsoId ?? entry.ExistingTargetCsoId,
                        SourceMetaverseObjectId = entry.MetaverseObjectId,
                        AttributeValueChanges = entry.AttributeChanges.ToList()
                    });
                    break;

                case OutboundPreviewEntryKind.Deprovisioning
                    when entry.DeprovisioningDecision?.Action == OutOfScopeDeprovisioningAction.StageDeleteExport:
                    result.Outbound.ProposedExports.Add(new PendingExport
                    {
                        ChangeType = PendingExportChangeType.Delete,
                        ConnectedSystemId = entry.ConnectedSystemId,
                        ConnectedSystemObjectId = entry.ExistingTargetCsoId,
                        SourceMetaverseObjectId = entry.MetaverseObjectId
                    });
                    break;
            }
        }
    }

    /// <summary>
    /// Builds the outbound outcome nodes in the real tree's shape: a Provisioned node (with the staged
    /// Pending Export nested beneath) where the preview would create a target object, a Pending Export
    /// node where it would update one, and a Deprovision Queued node where an out-of-scope object would
    /// have a Delete staged.
    /// </summary>
    private static void BuildOutboundOutcomeNodes(
        List<SyncOutcomeNode> siblings,
        OutboundPreviewResult outbound,
        IReadOnlyDictionary<int, string> connectedSystemNames)
    {
        foreach (var entry in outbound.Entries)
        {
            connectedSystemNames.TryGetValue(entry.ConnectedSystemId, out var systemName);

            switch (entry.Kind)
            {
                case OutboundPreviewEntryKind.Staging when entry.EffectiveChangeType == PendingExportChangeType.Create:
                    var provisioned = new SyncOutcomeNode
                    {
                        OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
                        TargetEntityDescription = systemName,
                        SyncRuleId = entry.SyncRuleId,
                        SyncRuleName = entry.SyncRuleName,
                        Ordinal = siblings.Count
                    };
                    provisioned.Children.Add(new SyncOutcomeNode
                    {
                        OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
                        TargetEntityDescription = systemName,
                        DetailCount = entry.AttributeChanges.Count,
                        DetailMessage = entry.ConnectedSystemId.ToString(),
                        StagedChangeType = entry.EffectiveChangeType,
                        Ordinal = 0
                    });
                    siblings.Add(provisioned);
                    break;

                case OutboundPreviewEntryKind.Staging when entry.EffectiveChangeType.HasValue && entry.AttributeChanges.Count > 0:
                    siblings.Add(new SyncOutcomeNode
                    {
                        OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
                        TargetEntityDescription = systemName,
                        SyncRuleId = entry.SyncRuleId,
                        SyncRuleName = entry.SyncRuleName,
                        DetailCount = entry.AttributeChanges.Count,
                        DetailMessage = entry.ConnectedSystemId.ToString(),
                        StagedChangeType = entry.EffectiveChangeType,
                        Ordinal = siblings.Count
                    });
                    break;

                case OutboundPreviewEntryKind.Deprovisioning
                    when entry.DeprovisioningDecision?.Action == OutOfScopeDeprovisioningAction.StageDeleteExport:
                    siblings.Add(new SyncOutcomeNode
                    {
                        OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
                        TargetEntityDescription = systemName,
                        SyncRuleId = entry.SyncRuleId,
                        SyncRuleName = entry.SyncRuleName,
                        DetailMessage = entry.ConnectedSystemId.ToString(),
                        StagedChangeType = PendingExportChangeType.Delete,
                        Ordinal = siblings.Count
                    });
                    break;
            }
        }
    }

    /// <summary>
    /// A Connected System id to name lookup from the export evaluation cache's rules, matching how the
    /// real outcome builder resolves target names (the entries deliberately carry ids, not entity graphs).
    /// </summary>
    private static Dictionary<int, string> BuildConnectedSystemNameLookup(ExportEvaluationCache cache)
    {
        return cache.ExportRulesByMvoTypeId.Values
            .SelectMany(rules => rules)
            .Where(sr => sr.ConnectedSystem != null)
            .GroupBy(sr => sr.ConnectedSystemId)
            .ToDictionary(g => g.Key, g => g.First().ConnectedSystem.Name);
    }

    /// <summary>
    /// Maps one pending Metaverse attribute value change into the inbound summary's display shape.
    /// </summary>
    private static SyncPreviewAttributeFlowChange BuildAttributeFlowChange(MetaverseObjectAttributeValue value, bool isAddition)
    {
        return new SyncPreviewAttributeFlowChange
        {
            AttributeId = value.AttributeId,
            AttributeName = value.Attribute?.Name ?? string.Empty,
            IsAddition = isAddition,
            Value = RenderValue(value),
            SyncRuleId = value.ContributedBySyncRuleId ?? value.ContributedBySyncRule?.Id,
            SyncRuleName = value.ContributedBySyncRule?.Name
        };
    }

    /// <summary>
    /// Renders an attribute value for display, without the attribute-name prefix the entity's own
    /// ToString carries.
    /// </summary>
    private static string? RenderValue(MetaverseObjectAttributeValue value)
    {
        if (value.NullValue)
            return null;
        if (value.StringValue != null)
            return value.StringValue;
        if (value.IntValue.HasValue)
            return value.IntValue.Value.ToString();
        if (value.LongValue.HasValue)
            return value.LongValue.Value.ToString();
        if (value.DecimalValue.HasValue)
            return value.DecimalValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value.DateTimeValue.HasValue)
            return value.DateTimeValue.Value.ToString("O");
        if (value.BoolValue.HasValue)
            return value.BoolValue.Value.ToString();
        if (value.GuidValue.HasValue)
            return value.GuidValue.Value.ToString();
        if (value.ReferenceValueId.HasValue || value.ReferenceValue != null)
            return (value.ReferenceValueId ?? value.ReferenceValue!.Id).ToString();
        if (value.UnresolvedReferenceValueId.HasValue || value.UnresolvedReferenceValue != null)
            return (value.UnresolvedReferenceValueId ?? value.UnresolvedReferenceValue!.Id).ToString();
        if (value.ByteValue != null)
            return $"{value.ByteValue.Length} bytes";
        return null;
    }

    #endregion
}
