// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Exceptions;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Servers.Preview;
using JIM.Application.Services;
using JIM.Application.UniqueValues;
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
using System.Runtime.CompilerServices;

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
    /// The object of the Connected System being synchronised that a Full Synchronisation preview row is about (#1530):
    /// what the run would do to that object is the whole chain the row is one consequence of, and its own preview shows
    /// it. A row about the synchronised object is that object; a row about an object in a target system is the
    /// synchronised object joined to the same Metaverse Object. Null where the row leads to no object of this system.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System the preview synchronises.</param>
    /// <param name="objectConnectedSystemId">The Connected System the row's object is in.</param>
    /// <param name="connectedSystemObjectId">The row's Connected System Object, if it names one.</param>
    /// <param name="metaverseObjectId">The row's Metaverse Object, if it names one.</param>
    public async Task<ConnectedSystemObject?> GetFullSynchronisationRowSubjectAsync(int connectedSystemId, int? objectConnectedSystemId,
        Guid? connectedSystemObjectId, Guid? metaverseObjectId)
    {
        if (objectConnectedSystemId == connectedSystemId && connectedSystemObjectId is { } ownObjectId)
            return await SyncRepo.GetConnectedSystemObjectAsync(connectedSystemId, ownObjectId);

        return metaverseObjectId is { } identityId
            ? await SyncRepo.GetConnectedSystemObjectByMetaverseObjectIdAsync(identityId, connectedSystemId)
            : null;
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
        return await PreviewCsoCoreAsync(cso, context, refreshCacheForWorkingMvo: true, asTheRunWould: true);
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

            results[connectedSystemObjectId] = await PreviewCsoCoreAsync(cso, context, refreshCacheForWorkingMvo: true, asTheRunWould: false);
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
        var sampleCountsByCategory = new Dictionary<FullSyncPreviewCategory, int>();
        var bounds = new FullSyncPreviewStreamOptions
        {
            MaxObjects = options.MaxObjects,
            TimeBudget = options.TimeBudget,
            PageSize = options.PageSize
        };

        // One consumer of the stream (#1530): the count tier counts every item, and the sample tier keeps a bounded
        // number of full trees per category, so memory stays flat however large the population.
        await foreach (var item in StreamFullSyncPreviewAsync(connectedSystemId, bounds, repositoryFactory))
        {
            switch (item.Kind)
            {
                case FullSyncPreviewItemKind.Population:
                    result.TotalObjectCount = item.TotalObjectCount ?? 0;
                    break;

                case FullSyncPreviewItemKind.Refused:
                    result.Errors.AddRange(item.Preview!.Errors);
                    return result;

                case FullSyncPreviewItemKind.Obsolete:
                    result.ObsoleteObjectCount++;
                    AddOutboundToCounts(result.Counts, item.Preview!);
                    break;

                case FullSyncPreviewItemKind.Unchanged:
                    result.UnchangedObjectCount++;
                    break;

                case FullSyncPreviewItemKind.Evaluated:
                    result.EvaluatedObjectCount++;
                    var category = item.Category!.Value;
                    AddToCounts(result.Counts, category, item.Preview!);
                    var retained = sampleCountsByCategory.GetValueOrDefault(category);
                    if (retained < options.SampleTreesPerCategory)
                    {
                        sampleCountsByCategory[category] = retained + 1;
                        result.Samples.Add(new FullSyncPreviewSample
                        {
                            Category = category,
                            ConnectedSystemObjectId = item.ConnectedSystemObjectId!.Value,
                            Preview = item.Preview!
                        });
                    }
                    break;

                case FullSyncPreviewItemKind.ExportScopeReview:
                    result.Counts.ExportScopeReviewed++;
                    AddOutboundToCounts(result.Counts, item.Preview!);
                    break;

                case FullSyncPreviewItemKind.Truncated:
                    result.Truncated = true;
                    result.TruncationReason = item.TruncationReason!.Value;
                    break;
            }
        }

        Log.Information("PreviewFullSyncAsync: Previewed Connected System {SystemId}: {Evaluated}/{Total} object(s) evaluated ({Obsolete} obsolete, {Unchanged} unchanged), " +
            "{Project} would project, {Join} would join, {Flow} attribute flow, {OutOfScope} out of scope, {NotConnected} not connected, {Blocked} blocked; " +
            "{Reviewed} Metaverse Object(s) export scope reviewed; " +
            "{Creates} creates, {Updates} updates, {Deletes} deletes proposed; truncated: {Truncated} ({Reason}); {Elapsed:0.0}s.",
            connectedSystemId, result.EvaluatedObjectCount, result.TotalObjectCount, result.ObsoleteObjectCount, result.UnchangedObjectCount,
            result.Counts.WouldProject, result.Counts.WouldJoin, result.Counts.AttributeFlow, result.Counts.OutOfScope,
            result.Counts.NotConnected, result.Counts.BlockedByErrors, result.Counts.ExportScopeReviewed,
            result.Counts.ObjectsToCreate, result.Counts.ObjectsToUpdate, result.Counts.ObjectsToDelete,
            result.Truncated, result.TruncationReason, stopwatch.Elapsed.TotalSeconds);
        return result;
    }

    /// <summary>
    /// How many Connected System Objects a Full Synchronisation preview of the system would walk (#1530), read where the
    /// walk reads them.
    /// </summary>
    internal Task<int> GetFullSyncPopulationAsync(int connectedSystemId) => SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystemId);

    /// <summary>
    /// Streams what a Full Synchronisation of one Connected System would do (#1530), one object at a time and in the
    /// order the synchronisation would meet them, so a caller can evaluate the whole population without holding it:
    /// the population first, then every Connected System Object (evaluated, or skipped as the run would skip it), then
    /// the export scope review the run drains after its own objects. Read-only throughout, under the same backstops as
    /// every other preview: a guarded repository and a rollback-only transaction that live as long as the enumeration.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System to preview.</param>
    /// <param name="options">Optional bounds; by default none, and the whole population is evaluated.</param>
    /// <param name="repositoryFactory">Optional factory for a preview-owned repository scope; see
    /// <see cref="PreviewSyncForMvoAsync"/>.</param>
    /// <param name="cancellationToken">Stops the walk between objects.</param>
    public async IAsyncEnumerable<FullSyncPreviewItem> StreamFullSyncPreviewAsync(
        int connectedSystemId,
        FullSyncPreviewStreamOptions? options = null,
        Func<ISyncRepositoryScope>? repositoryFactory = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new FullSyncPreviewStreamOptions();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var scope = repositoryFactory?.Invoke();
        var guardedRepository = new ReadOnlySyncRepositoryGuard(scope?.Repository ?? SyncRepo);
        var previewServer = new ExportEvaluationServer(Application, guardedRepository);
        await using var rollbackScope = await guardedRepository.BeginRollbackOnlyTransactionAsync();

        // A system with no objects is still previewed: its synchronisation walks nothing, but it drains the export
        // scope review all the same (below).
        var totalObjectCount = await guardedRepository.GetConnectedSystemObjectCountAsync(connectedSystemId);
        yield return new FullSyncPreviewItem { Kind = FullSyncPreviewItemKind.Population, TotalObjectCount = totalObjectCount };

        var context = await BuildCsoPreviewContextAsync(connectedSystemId, guardedRepository, previewServer);

        // The real run would refuse to start on a derived flow dependency cycle (#1750, decision 11) and process no
        // object, so the walk is not started either: one error naming the cycle, rather than every object blocked.
        var refusal = new SyncPreviewResult();
        if (AddDerivedFlowCycleError(refusal.Errors, context))
        {
            Log.Warning("StreamFullSyncPreviewAsync: Connected System {SystemId} was not previewed: the enabled derived Attribute Flows contain a dependency cycle.",
                connectedSystemId);
            yield return new FullSyncPreviewItem { Kind = FullSyncPreviewItemKind.Refused, Preview = refusal };
            yield break;
        }

        // The unchanged-object optimisation, decided as the run decides it (SyncFullSyncTaskProcessor): while no
        // configuration has changed since it was last fully applied, the run skips every object unchanged since the
        // system's last synchronisation, drift and all, so the preview proposes nothing for one either (#1530).
        DateTime? unchangedWatermark = null;
        var watermarks = await guardedRepository.GetConnectedSystemSynchronisationWatermarksAsync(connectedSystemId);
        if (watermarks?.LastSyncCompletedAt != null)
        {
            unchangedWatermark = ConnectedSystem.GetUnchangedObjectWatermark(watermarks.Value.LastSyncCompletedAt,
                watermarks.Value.ConfigurationLastFullyAppliedAt, await guardedRepository.GetLatestSyncRuleConfigurationChangeAsync());
        }

        // Keyset pagination from the zero GUID, matching the sync processors' population walk.
        var afterId = Guid.Empty;
        var evaluated = 0;
        while (totalObjectCount > 0)
        {
            var page = await guardedRepository.GetConnectedSystemObjectsAsync(
                connectedSystemId, page: 1, pageSize: options.PageSize,
                knownTotalCount: totalObjectCount, afterId: afterId);
            if (page.Results.Count == 0)
                break;
            afterId = page.Results[^1].Id;

            // One outbound-cache refresh per page for the joined objects' Metaverse Objects, instead of
            // one per object inside the core. The same page-batched Connected System Object set also feeds
            // the out-of-scope destructive cascade's deletion-rule and downstream-deprovisioning reads
            // (#288 Phase 1 of the Sync Preview Surface plan): whichever of this page's joined objects turns
            // out to be out of scope finds its Metaverse Object's other joined objects already in memory,
            // rather than issuing its own read.
            var joinedMvoIds = page.Results
                .Where(c => c.MetaverseObjectId.HasValue)
                .Select(c => c.MetaverseObjectId!.Value)
                .ToList();
            if (joinedMvoIds.Count > 0)
            {
                await previewServer.RefreshExportEvaluationCacheForPageAsync(context.Cache, joinedMvoIds);
                context.JoinedCsosByMvoIdForDeletion = await guardedRepository.GetConnectedSystemObjectsForMvoDeletionAsync(joinedMvoIds);
            }
            else
            {
                context.JoinedCsosByMvoIdForDeletion = null;
            }

            // The run tears a page's obsolete objects down before processing the rest of it (pass 1, then pass 2), so
            // they are met first; the sort is stable, so each group keeps the page's order.
            foreach (var cso in page.Results.OrderBy(c => c.Status == ConnectedSystemObjectStatus.Obsolete ? 0 : 1))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Each bound is tested before the next object is evaluated, so none is evaluated past it, and a walk
                // that reached its bound on the last object is not reported as truncated.
                if (options.TimeBudget.HasValue && stopwatch.Elapsed >= options.TimeBudget.Value)
                {
                    yield return Truncation(FullSyncPreviewTruncationReason.TimeBudgetExhausted);
                    yield break;
                }
                if (options.MaxObjects.HasValue && evaluated >= options.MaxObjects.Value)
                {
                    yield return Truncation(FullSyncPreviewTruncationReason.ObjectCapReached);
                    yield break;
                }

                if (cso.Status == ConnectedSystemObjectStatus.Obsolete)
                {
                    var teardown = await PreviewObsoleteCsoAsync(cso, context, refreshCacheForWorkingMvo: false);
                    evaluated++;
                    yield return new FullSyncPreviewItem
                    {
                        Kind = FullSyncPreviewItemKind.Obsolete,
                        ConnectedSystemObjectId = cso.Id,
                        DisplayName = cso.NameOrId,
                        ObjectTypeName = cso.Type?.Name,
                        Preview = teardown
                    };
                    continue;
                }
                if (unchangedWatermark.HasValue && cso.IsUnchangedSince(unchangedWatermark.Value))
                {
                    yield return Skipped(FullSyncPreviewItemKind.Unchanged, cso);
                    continue;
                }

                var preview = await PreviewCsoCoreAsync(cso, context, refreshCacheForWorkingMvo: false, asTheRunWould: true);
                evaluated++;
                yield return new FullSyncPreviewItem
                {
                    Kind = FullSyncPreviewItemKind.Evaluated,
                    ConnectedSystemObjectId = cso.Id,
                    DisplayName = cso.NameOrId,
                    ObjectTypeName = cso.Type?.Name,
                    Category = Categorise(preview),
                    Preview = preview
                };
            }

            if (page.Results.Count < options.PageSize)
                break;
        }

        // The run drains the export scope review once its own objects are processed, so the preview does too.
        await foreach (var reviewed in StreamExportScopeReviewAsync(context, previewServer, guardedRepository, options, stopwatch, cancellationToken))
            yield return reviewed;
    }

    private static FullSyncPreviewItem Truncation(FullSyncPreviewTruncationReason reason) =>
        new() { Kind = FullSyncPreviewItemKind.Truncated, TruncationReason = reason };

    private static FullSyncPreviewItem Skipped(FullSyncPreviewItemKind kind, ConnectedSystemObject cso) => new()
    {
        Kind = kind,
        ConnectedSystemObjectId = cso.Id,
        DisplayName = cso.NameOrId,
        ObjectTypeName = cso.Type?.Name
    };

    #endregion

    #region private methods

    /// <summary>
    /// The export scope review (#892, #1925), previewed as the run drains it (<c>SyncTaskProcessorBase.
    /// ProcessScopeReviewPendingMetaverseObjectsAsync</c>): every Metaverse Object flagged <c>ScopeReviewPending</c>,
    /// whatever its type and wherever it is joined, evaluated for export scope alone against every target. An object the
    /// walk already reviewed is skipped, so its exports are proposed once; the run's second evaluation of it finds the
    /// first one's exports already staged and adds nothing.
    /// </summary>
    private async IAsyncEnumerable<FullSyncPreviewItem> StreamExportScopeReviewAsync(
        CsoPreviewContext context,
        ExportEvaluationServer previewServer,
        ISyncRepository guardedRepository,
        FullSyncPreviewStreamOptions options,
        System.Diagnostics.Stopwatch stopwatch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Read whole rather than in the run's batches: the run clears each batch's flags as it goes, which is how it
        // pages, and the preview clears nothing. The flagged set is what the review evaluates in full either way.
        var flaggedIds = (await guardedRepository.GetMetaverseObjectIdsWithScopeReviewPendingAsync(int.MaxValue))
            .Where(id => !context.ScopeReviewedMetaverseObjectIds.Contains(id))
            .ToList();

        foreach (var batch in flaggedIds.Chunk(options.PageSize))
        {
            await previewServer.RefreshExportEvaluationCacheForPageAsync(context.Cache, batch.ToList());
            foreach (var mvo in await guardedRepository.GetMetaverseObjectsByIdsNoTrackingAsync(batch))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (options.TimeBudget.HasValue && stopwatch.Elapsed >= options.TimeBudget.Value)
                {
                    yield return Truncation(FullSyncPreviewTruncationReason.TimeBudgetExhausted);
                    yield break;
                }

                var reviewed = new SyncPreviewResult();
                ComposeOutbound(reviewed, await previewServer.EvaluateOutboundPreviewForMaterialisedMvosAsync([mvo], context.Cache,
                    synchronisationChanges: ([], null)));
                yield return new FullSyncPreviewItem
                {
                    Kind = FullSyncPreviewItemKind.ExportScopeReview,
                    MetaverseObjectId = mvo.Id,
                    MetaverseObjectTypeId = mvo.Type?.Id,
                    DisplayName = mvo.NameOrId,
                    ObjectTypeName = mvo.Type?.Name,
                    Preview = reviewed
                };
            }
        }
    }

    /// <summary>
    /// Builds the shared, read-only inputs one Connected System's CSO previews evaluate against: the
    /// enabled Synchronisation Rules, the object types, the outbound evaluation cache and the target
    /// system name lookup. Built once per single-object preview, and once for a whole full-system walk.
    /// </summary>
    private async Task<CsoPreviewContext> BuildCsoPreviewContextAsync(
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

        // Metaverse-Derived Attribute Flows (#1750, plan Phase 5): the run's dependency graph, built by the same factory
        // from the same all-systems rule set the worker builds it from (with any proposal substituted, so a
        // Configuration Change Preview evaluates the derived flows the proposal would leave), and attached to the
        // priority context exactly as the worker attaches it. A cycle refuses the whole run in the worker (decision 11), so here it is recorded on the context
        // and every preview built from it reports it as a blocking error instead of evaluating on a guessed order.
        DerivedFlowGraph? derivedFlowGraph = null;
        string? derivedFlowCycleMessage = null;
        try
        {
            derivedFlowGraph = DerivedFlowGraphFactory.Create(allSyncRules, []);
        }
        catch (DerivedFlowCycleException cycle)
        {
            derivedFlowCycleMessage = cycle.Message;
        }
        var priorityContext = new AttributePriorityContext(allSyncRules, honourNullAssertions: true, derivedFlowGraph);

        var objectTypes = await guardedRepository.GetObjectTypesAsync(connectedSystemId);
        var cache = await previewServer.BuildExportEvaluationCacheAsync();

        // Unique Value Generation (#242): one service and one dry-run options instance per preview, over the
        // SAME read-only guarded repository every other preview read uses, so an orchestration bug that
        // reaches for a write (there is exactly one: CommitAssignmentsAsync, which this preview never calls)
        // fails loudly rather than persisting. DryRun means ResolveAsync also never reserves a sequence block;
        // SequenceAllocator simulates it locally instead. A fresh UniqueValueReservationSet and a random owner
        // id keep this preview's candidates from ever being seen as claimed by a real run's reservations, and
        // ResolveAsync releases whatever it claims under that id before it returns, so nothing outlives the call.
        var uniqueValueGenerationServer = new UniqueValueGenerationServer(guardedRepository);
        var uniqueValueResolveOptions = new UniqueValueResolveOptions
        {
            DryRun = true,
            Reservations = new UniqueValueReservationSet(),
            ReservationOwnerId = Guid.NewGuid()
        };

        // Drift correction's inputs, exactly as the run builds them (SyncTaskProcessorBase.BuildDriftDetectionCache):
        // the export rules that enforce state on THIS Connected System, and the import mappings of every system, so a
        // value this system legitimately contributes is not read as drift (#1530).
        var driftExportRules = syncRules
            .Where(sr => sr.Enabled && sr.Direction == SyncRuleDirection.Export && sr.EnforceState && sr.ConnectedSystemId == connectedSystemId)
            .ToList();
        var importMappingCache = DriftDetectionService.BuildImportMappingCache(allSyncRules);

        return new CsoPreviewContext(connectedSystemId, previewServer, syncRules, objectTypes, cache,
            BuildConnectedSystemNameLookup(cache), guardedRepository, priorityContext,
            uniqueValueGenerationServer, uniqueValueResolveOptions, derivedFlowCycleMessage,
            driftExportRules, importMappingCache);
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
    /// <param name="asTheRunWould">
    /// True to answer what a synchronisation of this object would do (#1530): exports are evaluated only over what
    /// the object's own inbound evaluation changed (or, for an object flagged for export scope review, for scope
    /// alone), and drift is corrected only in this Connected System, where an export rule enforces state. False asks
    /// the state-assertion question instead (every difference between the Metaverse Object and every target), which
    /// the configuration change adapters ask of a baseline and a proposal so the difference between the two cancels
    /// whatever neither changes.
    /// </param>
    private async Task<SyncPreviewResult> PreviewCsoCoreAsync(
        ConnectedSystemObject cso,
        CsoPreviewContext context,
        bool refreshCacheForWorkingMvo,
        bool asTheRunWould)
    {
        var result = new SyncPreviewResult();
        var connectedSystemId = context.ConnectedSystemId;
        var guardedRepository = context.GuardedRepository;
        var previewServer = context.PreviewServer;
        var objectTypes = context.ObjectTypes;

        // A dependency cycle among the enabled derived flows (#1750) refuses the whole synchronisation before any
        // object is processed (decision 11), so nothing about this object can be previewed either.
        if (AddDerivedFlowCycleError(result.Errors, context))
            return result;

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

            // A target's joined object still gets its drift corrected (#1530): with no import rule nothing flows in
            // and no export evaluation is queued, but the run checks the object against what its enforcing export
            // rules say it should hold, which is a Full Synchronisation of a target's whole point.
            if (asTheRunWould && cso.MetaverseObjectId.HasValue && context.DriftExportRules.Count > 0)
            {
                var joinedMvo = (await guardedRepository.GetMetaverseObjectsByIdsNoTrackingAsync([cso.MetaverseObjectId.Value]))
                    .SingleOrDefault();
                if (joinedMvo != null)
                {
                    inbound.AlreadyJoinedMetaverseObjectId = cso.MetaverseObjectId;
                    DescribeMetaverseObject(inbound, joinedMvo);
                    AddDriftCorrectionRoot(result, ProposeDriftCorrections(result, cso, CloneForPreview(joinedMvo), context));
                }
            }
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

            // The destructive cascade (#288 Phase 1 of the Sync Preview Surface plan): a real synchronisation
            // does not stop at the warning above for a JOINED object whose out-of-scope action is Disconnect.
            // It disconnects the object, puts the Metaverse Object to its type's Deletion Rule, and when the
            // object dies, deprovisions every downstream joined Connected System Object. Mirror that so the
            // preview's tree matches what the run would record (SyncTaskProcessorBase.HandleCsoOutOfScopeAsync
            // is the reference). An object that is not joined has nothing to cascade; RemainJoined keeps the
            // join intact, so nothing downstream changes, and the tree states only the retained join (#1649).
            if (cso.MetaverseObjectId.HasValue && outOfScopeAction == InboundOutOfScopeAction.Disconnect)
                await BuildOutOfScopeCascadeAsync(result, cso, importRules, context, refreshCacheForWorkingMvo);
            else if (cso.MetaverseObjectId.HasValue && outOfScopeAction == InboundOutOfScopeAction.RemainJoined)
                await BuildRetainedJoinRootAsync(result, cso, importRules, context);

            return result;
        }

        // Resolve the working Metaverse Object: the joined one, a read-only probed match (never claimed),
        // or a prospective in-memory projection. The working object is always the preview's own copy, so
        // Attribute Flow can mutate it without touching shared state.
        SyncRule? projectionSyncRule = null;
        MetaverseObject? workingMvo;
        var flaggedForScopeReview = false;
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
            flaggedForScopeReview = joinedMvo.ScopeReviewPending;
            DescribeMetaverseObject(inbound, joinedMvo);
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
                flaggedForScopeReview = matchedMvo.ScopeReviewPending;
                DescribeMetaverseObject(inbound, matchedMvo);
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
        var flowErrors = new List<(int? SyncRuleId, string? SyncRuleName, AttributeFlowError Error)>();
        List<(ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType, string AttributeName, string Value)> generatedValueOutcomes = [];
        var originalMetaverseObject = cso.MetaverseObject;
        var objectFailed = false;
        try
        {
            cso.MetaverseObject = workingMvo;
            foreach (var rule in inScopeRules)
            {
                try
                {
                    foreach (var flowError in _syncEngine.FlowInboundAttributes(cso, rule, objectTypes, ExpressionEvaluator,
                        priorityContext: context.PriorityContext))
                        flowErrors.Add((rule.Id, rule.Name, flowError));
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
                    objectFailed = true;
                    break;
                }
                catch (SyncExpressionMissingInputException missingInputEx)
                {
                    // Missing Input Behaviour "Fail the object": the real run records an ExpressionMissingInput error
                    // for the object and applies nothing, so the preview reports it as blocking, in the same words,
                    // rather than letting the exception escape the preview.
                    result.Errors.Add(new SyncPreviewMessage
                    {
                        Code = SyncPreviewMessageCode.ExpressionEvaluationError,
                        Detail = missingInputEx.DescribeForAdministrator(),
                        SyncRuleId = rule.Id,
                        SyncRuleName = rule.Name,
                        ConnectedSystemId = connectedSystemId,
                        AttributeName = missingInputEx.TargetAttributeName
                    });
                    objectFailed = true;
                    break;
                }
            }

            // The orphaned-contribution recall and the withdrawal re-election (#1899), between the ordinary pass and the
            // derived levels exactly as the run takes them, because both change values a derived flow reads. A survivor
            // whose own Expression fails fails the object in the run (its object-level catch), so it does here too.
            // Inside the try because the recall reads the working copy through the object's link.
            List<(ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType, string AttributeName, string Value)> reElectionOutcomes = [];
            if (!objectFailed)
            {
                try
                {
                    reElectionOutcomes = await RecallAndReElectForPreviewAsync(result, cso, workingMvo, context, asTheRunWould);
                }
                catch (SyncExpressionEvaluationException expressionEx)
                {
                    result.Errors.Add(new SyncPreviewMessage
                    {
                        Code = SyncPreviewMessageCode.ExpressionEvaluationError,
                        Detail = $"An Expression failed to evaluate while handing a withdrawn value to the next contributor: {expressionEx.Message}",
                        SyncRuleName = expressionEx.SyncRuleName,
                        ConnectedSystemId = connectedSystemId,
                        AttributeName = expressionEx.TargetAttributeName
                    });
                    objectFailed = true;
                }
                catch (SyncExpressionMissingInputException missingInputEx)
                {
                    result.Errors.Add(new SyncPreviewMessage
                    {
                        Code = SyncPreviewMessageCode.ExpressionEvaluationError,
                        Detail = missingInputEx.DescribeForAdministrator(),
                        SyncRuleName = missingInputEx.SyncRuleName,
                        ConnectedSystemId = connectedSystemId,
                        AttributeName = missingInputEx.TargetAttributeName
                    });
                    objectFailed = true;
                }
            }

            // Unique Value Generation (#242) and Metaverse-Derived Attribute Flows (#1750, plan Phase 5): the worker's
            // per-object level loop, read-only. Level 0 resolves the generations the ordinary pass recorded (dry run);
            // each derived level is then evaluated once and its generations resolved before the next level reads
            // them. Must run before the flows are captured immediately below, since the derived pass and
            // ApplyGeneratedValue stage their results the same way an ordinary Attribute Flow writer does (mirrors
            // the worker's own "before Count actual attribute changes" ordering). Inside the try because the derived
            // pass, like the ordinary one, reads the working copy through the object's link. A value the re-election
            // generated comes first, as the worker merges it.
            if (!objectFailed)
            {
                generatedValueOutcomes = [.. reElectionOutcomes, .. await ResolveGenerationsAndDerivedLevelsForPreviewAsync(
                    result, cso, workingMvo, inScopeRules, context, flowErrors)];
            }
        }
        finally
        {
            // The CSO instance may be shared (an in-memory repository hands out its stored instance);
            // the working object is the preview's alone, so the only mutation to undo is this link.
            cso.MetaverseObject = originalMetaverseObject;
        }

        // The run fails an object whose inbound Expression throws, or whose input is missing under Fail the object, and
        // discards everything its synchronisation would have done (SyncTaskProcessorBase's object-level catch): nothing
        // projects, joins, flows or exports, and no further rule is evaluated. So the preview proposes the error alone.
        if (objectFailed)
        {
            result.Inbound = new SyncPreviewInboundSummary { AlreadyJoinedMetaverseObjectId = cso.MetaverseObjectId };
            return result;
        }

        foreach (var (syncRuleId, syncRuleName, flowError) in flowErrors)
        {
            // The ternary here used to collapse ExpressionMissingInput and GeneratedBaseNotSingleValue (Unique
            // Value Generation, #242) into the same "a required input has no value" message; the latter is a
            // different failure (the base Expression evaluated fine but returned more than one value), so it
            // gets its own accurate message, matching the worker's own wording (DescribeAttributeFlowError).
            var (code, detail) = flowError.Kind switch
            {
                AttributeFlowErrorKind.MultiValuedToSingleValued => (
                    SyncPreviewMessageCode.MultiValuedToSingleValuedFlow,
                    $"The multi-valued source attribute '{flowError.SourceAttributeName}' holds {flowError.ValueCount} values but flows to the single-valued attribute '{flowError.TargetAttributeName}'; the attribute would not flow."),
                AttributeFlowErrorKind.GeneratedBaseNotSingleValue => (
                    SyncPreviewMessageCode.ExpressionEvaluationError,
                    $"The base Expression for the generated value targeting '{flowError.TargetAttributeName}' returned more than one value, so the attribute would not flow; a generated value's base Expression must produce a single text value, not an array: '{flowError.Expression}'."),
                _ => (
                    SyncPreviewMessageCode.ExpressionEvaluationError,
                    $"The Expression targeting '{flowError.TargetAttributeName}' was not evaluated: a required input has no value.")
            };

            // Only the derived pass stamps the error with its hosting rule (#1750); named as the worker names it.
            if (flowError.SyncRuleName != null)
                detail += $" The Attribute Flow is derived by Synchronisation Rule '{flowError.SyncRuleName}'.";

            result.Errors.Add(new SyncPreviewMessage
            {
                Code = code,
                Detail = detail,
                SyncRuleId = syncRuleId,
                SyncRuleName = syncRuleName,
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

        // The change set the run's export evaluation is given (SyncTaskProcessorBase snapshots the same two lists
        // before applying them): additions then removals, and the removals on their own.
        var changedAttributes = workingMvo.PendingAttributeValueAdditions
            .Concat(workingMvo.PendingAttributeValueRemovals)
            .ToList();
        var removedAttributes = workingMvo.PendingAttributeValueRemovals.Count > 0
            ? workingMvo.PendingAttributeValueRemovals.ToHashSet()
            : null;

        // What the run's Detailed tracking counts from the same lists (#91): asserted nulls, which are the "Null is a value"
        // markers written into the additions, and attributes cleared with no contributor to take them over.
        var assertedNullCount = workingMvo.PendingAttributeValueAdditions.Count(av => av.NullValue);
        var clearedAttributeCount = ContributorReElectionService.GetClearedAttributeIds(
            workingMvo, workingMvo.PendingAttributeValueAdditions, workingMvo.PendingAttributeValueRemovals).Count;

        _syncEngine.ApplyPendingAttributeChanges(workingMvo);

        // A projection is named as Attribute Flow names it; there is nothing for it to be called before.
        if (inbound.WouldProject)
            DescribeMetaverseObject(inbound, workingMvo);

        // The outbound chain over the prospective Metaverse Object state, against the context's shared cache. As the
        // run would: exports are evaluated only when Attribute Flow changed the object (the run queues no export
        // evaluation otherwise), or for scope alone when the object is flagged for export scope review, which the run
        // drains in the same synchronisation.
        if (refreshCacheForWorkingMvo && workingMvo.Id != Guid.Empty)
            await previewServer.RefreshExportEvaluationCacheForPageAsync(context.Cache, [workingMvo.Id]);
        OutboundPreviewResult outbound;
        if (!asTheRunWould)
            outbound = await previewServer.EvaluateOutboundPreviewForMaterialisedMvosAsync([workingMvo], context.Cache);
        else if (changedAttributes.Count > 0)
            outbound = await previewServer.EvaluateOutboundPreviewForMaterialisedMvosAsync([workingMvo], context.Cache,
                synchronisationChanges: (changedAttributes, removedAttributes));
        else if (flaggedForScopeReview)
            outbound = await previewServer.EvaluateOutboundPreviewForMaterialisedMvosAsync([workingMvo], context.Cache,
                synchronisationChanges: ([], null));
        else
            outbound = new OutboundPreviewResult();
        if (asTheRunWould && flaggedForScopeReview)
            context.ScopeReviewedMetaverseObjectIds.Add(workingMvo.Id);
        ComposeOutbound(result, outbound);

        // Drift correction, as the run would: this object's own values, where an export rule to this Connected System
        // enforces state, against what the Metaverse Object says they should be.
        var driftedAttributeCount = asTheRunWould ? ProposeDriftCorrections(result, cso, workingMvo, context) : 0;

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

            // Unique Value Generation (#242): a GeneratedValueAssigned child per
            // resolved attribute, mirroring exactly where the worker records them: a child of the root
            // (alongside, not nested inside, the Attribute Flow child), never gated to a tracking level,
            // since a generated value is as much an audit signal in a preview as it is in a real run. Added
            // BEFORE the Attribute Flow child, because that is the order the worker records them in (its root
            // builder in SyncTaskProcessorBase adds the generated children first), and the fidelity pairing
            // compares sibling order.
            foreach (var (outcomeType, attributeName, value) in generatedValueOutcomes)
            {
                root.Children.Add(new SyncOutcomeNode
                {
                    OutcomeType = outcomeType,
                    TargetEntityId = root.TargetEntityId,
                    TargetEntityDescription = root.TargetEntityDescription,
                    DetailMessage = $"{attributeName}: {value}",
                    Ordinal = root.Children.Count
                });
            }

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

            // Detailed tracking's asserted nulls and no-contributor clears (#91), beneath the root after the Attribute
            // Flow child, in the order the worker's root builder adds them; the run's export outcomes come later.
            if (assertedNullCount > 0)
            {
                root.Children.Add(new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull,
                    TargetEntityDescription = root.TargetEntityDescription,
                    DetailCount = assertedNullCount,
                    Ordinal = root.Children.Count
                });
            }

            if (clearedAttributeCount > 0)
            {
                root.Children.Add(new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor,
                    TargetEntityDescription = root.TargetEntityDescription,
                    DetailCount = clearedAttributeCount,
                    Ordinal = root.Children.Count
                });
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

        AddDriftCorrectionRoot(result, driftedAttributeCount);

        Log.Debug("PreviewCsoCoreAsync: Previewed CSO {CsoId} in system {SystemId}: {FlowCount} inbound flow(s), {EntryCount} outbound decision(s), {DriftCount} drifted attribute(s), {ErrorCount} error(s), {WarningCount} warning(s).",
            cso.Id, connectedSystemId, flowCount, outbound.Entries.Count, driftedAttributeCount, result.Errors.Count, result.Warnings.Count);
        return result;
    }

    /// <summary>
    /// The run records a drift correction on an execution item of its own, as a root outcome counting the drifted
    /// attributes, so the preview proposes it as a root of its own.
    /// </summary>
    private static void AddDriftCorrectionRoot(SyncPreviewResult result, int driftedAttributeCount)
    {
        if (driftedAttributeCount == 0)
            return;

        // Shaped as the run shapes it (SyncTaskProcessorBase.EvaluateDriftAndEnforceState): the count alone.
        result.OutcomeTree.Add(new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection,
            DetailCount = driftedAttributeCount,
            Ordinal = result.OutcomeTree.Count
        });
    }

    /// <summary>
    /// Proposes the corrections the run's drift detection would stage for <paramref name="cso"/> (#1530), through the
    /// run's own detection (<see cref="DriftDetectionService.EvaluateDrift"/>, which stages nothing itself). A
    /// correction for an object the export evaluation already proposes an update for is folded into that update, as
    /// the run merges the two Pending Exports for one object.
    /// </summary>
    /// <returns>How many attributes have drifted; zero when nothing enforces state in this Connected System.</returns>
    private int ProposeDriftCorrections(SyncPreviewResult result, ConnectedSystemObject cso, MetaverseObject workingMvo,
        CsoPreviewContext context)
    {
        // Not only for an object already joined: the run evaluates drift for every object it leaves joined, the one
        // this synchronisation joins or projects included, against the Metaverse Object as Attribute Flow left it.
        if (context.DriftExportRules.Count == 0)
            return 0;

        var drift = Application.DriftDetection.EvaluateDrift(cso, workingMvo, context.DriftExportRules,
            context.ImportMappingCache, context.PriorityContext, context.UniqueValueResolveOptions);
        if (!drift.HasDrift)
            return 0;

        // Detection stages each change by attribute id alone, which is all the run persists; the preview names the
        // attribute so it can be shown. The changes are fresh to this preview, so nothing shared is touched.
        var attributesById = context.ObjectTypes.SelectMany(t => t.Attributes).ToDictionary(a => a.Id);
        foreach (var change in drift.CorrectiveExports.SelectMany(pe => pe.AttributeValueChanges))
        {
            if (change.Attribute == null && attributesById.TryGetValue(change.AttributeId, out var attribute))
                change.Attribute = attribute;
        }

        // Which of the object's attributes are corrections, and from what, so a preview can state each one (#1530): the
        // value the object holds now, and what the correction writes.
        var correctiveChanges = drift.CorrectiveExports.SelectMany(pe => pe.AttributeValueChanges).ToList();
        result.DriftCorrections.AddRange(drift.DriftedAttributes.Select(drifted => new SyncPreviewDriftCorrection
        {
            AttributeId = drifted.Attribute.Id,
            AttributeName = drifted.Attribute.Name,
            CurrentValue = PreviewValueRenderer.Join(cso.AttributeValues
                .Where(value => value.AttributeId == drifted.Attribute.Id)
                .Select(value => value.ToStringNoName())),
            CorrectedValue = PreviewValueRenderer.Join(correctiveChanges
                .Where(change => change.AttributeId == drifted.Attribute.Id)
                .Select(PreviewValueRenderer.Render)),
            SyncRuleId = drifted.ExportRule.Id,
            SyncRuleName = drifted.ExportRule.Name
        }));

        foreach (var correction in drift.CorrectiveExports)
        {
            var proposed = result.Outbound.ProposedExports.Find(pe =>
                pe.ChangeType == PendingExportChangeType.Update && pe.ConnectedSystemObjectId == correction.ConnectedSystemObjectId);
            if (proposed == null)
            {
                result.Outbound.ProposedExports.Add(correction);
                continue;
            }

            proposed.AttributeValueChanges.AddRange(correction.AttributeValueChanges
                .Where(change => proposed.AttributeValueChanges.All(existing => existing.AttributeId != change.AttributeId)));
        }

        return drift.DriftedAttributes.Count;
    }

    /// <summary>
    /// Records the context's derived flow dependency cycle (#1750), if it has one, as a blocking
    /// <see cref="SyncPreviewMessageCode.DerivedFlowCycle"/> error.
    /// </summary>
    /// <returns>True when there is a cycle, so the caller previews nothing further.</returns>
    private static bool AddDerivedFlowCycleError(List<SyncPreviewMessage> errors, CsoPreviewContext context)
    {
        if (context.DerivedFlowCycleMessage == null)
            return false;

        errors.Add(new SyncPreviewMessage
        {
            Code = SyncPreviewMessageCode.DerivedFlowCycle,
            Detail = "A synchronisation would not start, so no object would be processed. " + context.DerivedFlowCycleMessage,
            ConnectedSystemId = context.ConnectedSystemId
        });
        return true;
    }

    /// <summary>
    /// The worker's per-object level loop (<c>SyncTaskProcessorBase.ResolveGenerationsAndDerivedLevelsAsync</c>;
    /// Metaverse-Derived Attribute Flows, #1750, plan Phase 5, FR 10 and FR 11), read-only: level 0 resolves the
    /// generation requests the ordinary pass recorded (dry run), then for each level 1 to the deepest level of the
    /// in-scope rules' Metaverse Object Types the engine evaluates that level's derived flows hosted on
    /// <paramref name="inScopeRules"/> (this Connected System's rules only, as in the real run), and any generation
    /// requests they recorded are resolved before the next level reads them. Each level is evaluated exactly once. With
    /// no derived flow graph (a cycle, reported on the context instead) only level 0 runs.
    /// </summary>
    /// <remarks>
    /// Mapping-level errors are appended to <paramref name="flowErrors"/> and reported with the ordinary pass's. A thrown
    /// derived expression error fails the object in the real run, so it is reported as a blocking error and no further
    /// level is evaluated. Nothing here writes: derived-input marks are collected and flushed only by the worker.
    /// </remarks>
    private async Task<List<(ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType, string AttributeName, string Value)>> ResolveGenerationsAndDerivedLevelsForPreviewAsync(
        SyncPreviewResult result,
        ConnectedSystemObject cso,
        MetaverseObject workingMvo,
        List<SyncRule> inScopeRules,
        CsoPreviewContext context,
        List<(int? SyncRuleId, string? SyncRuleName, AttributeFlowError Error)> flowErrors)
    {
        var outcomes = await ResolvePendingGeneratedValuesForPreviewAsync(result, workingMvo, context, ownCsoId: cso.Id);

        var graph = context.PriorityContext.DerivedFlowGraph;
        if (graph == null || inScopeRules.Count == 0)
            return outcomes;

        var maxLevel = inScopeRules
            .Select(rule => rule.MetaverseObjectTypeId)
            .Distinct()
            .Select(graph.MaxLevel)
            .Max();

        for (var level = 1; level <= maxLevel; level++)
        {
            try
            {
                foreach (var error in _syncEngine.EvaluateDerivedLevel(cso, level, inScopeRules, context.ObjectTypes, ExpressionEvaluator, context.PriorityContext))
                    flowErrors.Add((FindHostingRule(inScopeRules, error.SyncRuleName)?.Id, error.SyncRuleName, error));
            }
            catch (SyncExpressionEvaluationException expressionEx)
            {
                result.Errors.Add(new SyncPreviewMessage
                {
                    Code = SyncPreviewMessageCode.ExpressionEvaluationError,
                    Detail = $"An Expression failed to evaluate: {expressionEx.Message}" + DescribeDerivedHost(expressionEx.SyncRuleName),
                    SyncRuleId = FindHostingRule(inScopeRules, expressionEx.SyncRuleName)?.Id,
                    SyncRuleName = expressionEx.SyncRuleName,
                    ConnectedSystemId = context.ConnectedSystemId,
                    AttributeName = expressionEx.TargetAttributeName
                });
                workingMvo.PendingGeneratedValues.Clear();
                break;
            }
            catch (SyncExpressionMissingInputException missingInputEx)
            {
                result.Errors.Add(new SyncPreviewMessage
                {
                    Code = SyncPreviewMessageCode.ExpressionEvaluationError,
                    Detail = missingInputEx.DescribeForAdministrator(),
                    SyncRuleId = FindHostingRule(inScopeRules, missingInputEx.SyncRuleName)?.Id,
                    SyncRuleName = missingInputEx.SyncRuleName,
                    ConnectedSystemId = context.ConnectedSystemId,
                    AttributeName = missingInputEx.TargetAttributeName
                });
                workingMvo.PendingGeneratedValues.Clear();
                break;
            }

            outcomes.AddRange(await ResolvePendingGeneratedValuesForPreviewAsync(result, workingMvo, context, ownCsoId: cso.Id));
        }

        return outcomes;

        static string DescribeDerivedHost(string? syncRuleName) =>
            syncRuleName == null ? string.Empty : $" The Attribute Flow is derived by Synchronisation Rule '{syncRuleName}'.";
    }

    /// <summary>
    /// The two steps the run takes on a joined object between the ordinary pass and the derived levels
    /// (<c>SyncTaskProcessorBase.ProcessMetaverseObjectChangesAsync</c>, #1899), in its order, on the preview's working
    /// copy: the orphaned-contribution recall stages the removal of a value this system contributed through an
    /// Attribute Flow mapping that no longer exists (#1533), then the withdrawal re-election hands every attribute the
    /// ordinary pass or the recall cleared to the next surviving contributor (#91), or leaves it cleared.
    /// </summary>
    /// <remarks>
    /// The recall is made only as the run would. The configuration change previews ask the other question of a baseline
    /// and a proposal, and a proposal removing a mapping would recall its values on one side only, which those previews
    /// deliberately leave to the next Full Synchronisation rather than count against the save; nor can a proposal carry
    /// the keep-the-values choice that severs the provenance the recall reads (<c>SyncRuleAttributeFlowPreviewAdapter</c>).
    /// The re-election follows whatever the ordinary pass withdrew on either path, because it is the same
    /// synchronisation's answer to the same change. Survivors are re-flowed against the working copy and left as they
    /// were found, so nothing shared is touched.
    /// </remarks>
    /// <param name="result">The preview result, for a generation a re-elected survivor's mapping would fail.</param>
    /// <param name="cso">The previewed object, linked to <paramref name="workingMvo"/> for the call.</param>
    /// <param name="workingMvo">The preview's working copy, carrying the ordinary pass's staged changes.</param>
    /// <param name="context">The shared read-only inputs for the object's Connected System.</param>
    /// <param name="asTheRunWould">See <see cref="PreviewCsoCoreAsync"/>.</param>
    /// <returns>A Generated outcome per value a re-elected survivor's generated mapping resolved; usually empty.</returns>
    private async Task<List<(ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType, string AttributeName, string Value)>> RecallAndReElectForPreviewAsync(
        SyncPreviewResult result,
        ConnectedSystemObject cso,
        MetaverseObject workingMvo,
        CsoPreviewContext context,
        bool asTheRunWould)
    {
        if (asTheRunWould)
            _syncEngine.RecallOrphanedContributions(cso, context.PriorityContext);

        if (workingMvo.PendingAttributeValueRemovals.Count == 0)
            return [];

        // Only a genuine clear is withdrawn: an attribute the pass replaced, or that keeps another value, has nothing to
        // hand over. An asserted null writes a marker into the additions, so it is never treated as cleared here.
        var clearedAttributeIds = ContributorReElectionService.GetClearedAttributeIds(
            workingMvo, workingMvo.PendingAttributeValueAdditions, workingMvo.PendingAttributeValueRemovals);
        var withdrawnValues = workingMvo.PendingAttributeValueRemovals
            .Where(av => clearedAttributeIds.Contains(av.AttributeId))
            .ToList();
        if (withdrawnValues.Count == 0)
            return [];

        // The run's scope for a withdrawal (SyncTaskProcessorBase.ReElectSurvivingContributorsAsync): the withdrawing
        // system is excluded, since its other enabled rules have already flowed in this pass, and a generation the
        // survivor's mapping needs is resolved for the object leaving the attribute, never the person's own account.
        return await ContributorReElectionService.ReElectSurvivingContributorsAsync(
            workingMvo,
            withdrawnValues,
            ContributorRecallScope.ForObsoletingConnectedSystemObject(cso),
            context.PriorityContext,
            _syncEngine,
            context.GuardedRepository,
            (survivor, rule) => Application.ScopingEvaluation.IsCsoInScopeForImportRule(survivor, rule),
            context.ObjectTypes,
            ExpressionEvaluator,
            resolvePendingGeneratedValues: resolvedMvo => ResolvePendingGeneratedValuesForPreviewAsync(result, resolvedMvo, context, disconnectingCsoId: cso.Id),
            leaveSurvivorsAsFound: true);
    }

    /// <summary>
    /// The in-scope rule a derived flow error names, or null when the name is absent or not unique among them (the
    /// message still carries the name).
    /// </summary>
    private static SyncRule? FindHostingRule(List<SyncRule> inScopeRules, string? syncRuleName)
    {
        if (syncRuleName == null)
            return null;

        var matches = inScopeRules.Where(rule => string.Equals(rule.Name, syncRuleName, StringComparison.Ordinal)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Unique Value Generation (#242): resolves <paramref name="workingMvo"/>'s pending generated values
    /// (recorded by <see cref="ISyncEngine.FlowInboundAttributes"/>'s inbound pass, just like a real run) into
    /// candidate values, applies each one through <see cref="ISyncEngine.ApplyGeneratedValue"/> so it appears
    /// in the preview's Attribute Flow changes exactly as a real value would, and always clears
    /// <see cref="MetaverseObject.PendingGeneratedValues"/>, mirroring the worker's own
    /// <c>SyncTaskProcessorBase.ResolvePendingGeneratedValuesAsync</c>.
    /// <para>
    /// Participating targets come from <paramref name="context"/>'s own outbound evaluation cache and feed
    /// only the generation-time collision gate, exactly as in the worker (<see cref="GeneratedValueParticipation"/>).
    /// </para>
    /// </summary>
    /// <param name="result">The preview result to add a <see cref="SyncPreviewMessageCode.GeneratedValueWouldFail"/>
    /// warning to for any request that would fail in the real run (Exhausted, NoBaseValue, WidthExceeded):
    /// unlike the other failure surfaces this method used to stay silent on, showing nothing
    /// where the real run would record an error understates what synchronising would do.</param>
    /// <param name="workingMvo">The preview's own working copy of the Metaverse Object.</param>
    /// <param name="context">The shared read-only inputs for the object's Connected System.</param>
    /// <param name="ownCsoId">The previewed Connected System Object when it is joined to (or projects)
    /// <paramref name="workingMvo"/>: the person's own account for the connector-space gate, as in the worker.</param>
    /// <param name="disconnectingCsoId">The previewed Connected System Object when it is LEAVING
    /// <paramref name="workingMvo"/> (re-election after obsoletion): never the person's own account.</param>
    /// <returns>
    /// One (outcome type, attribute name, value) tuple per <c>Generated</c> result, for the
    /// caller to record as a <c>GeneratedValueAssigned</c> node in the speculative
    /// outcome tree; empty when nothing was generated.
    /// </returns>
    private async Task<List<(ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType, string AttributeName, string Value)>> ResolvePendingGeneratedValuesForPreviewAsync(
        SyncPreviewResult result, MetaverseObject workingMvo, CsoPreviewContext context, Guid? ownCsoId = null, Guid? disconnectingCsoId = null)
    {
        var pending = workingMvo.PendingGeneratedValues.ToList();
        if (pending.Count == 0)
            return [];

        var forOutcomeTree = new List<(ActivityRunProfileExecutionItemSyncOutcomeType, string, string)>();
        try
        {
            var exportRules = context.Cache.ExportRulesByMvoTypeId.Values.SelectMany(rules => rules);
            var requests = new List<GenerationRequest>(pending.Count);
            foreach (var p in pending)
            {
                var participatingTargets = GeneratedValueParticipation.ComputeParticipatingTargets(p.Mapping, exportRules);
                var connectorSpaceAttributeIds = participatingTargets.Select(t => t.AttributeId).Distinct().ToList();

                requests.Add(new GenerationRequest
                {
                    Mode = GeneratedValueMode.Import,
                    MetaverseObjectId = workingMvo.Id == Guid.Empty ? null : workingMvo.Id,
                    MetaverseAttributeId = p.AttributeId,
                    Generation = p.Mapping.Generation!,
                    TargetType = p.Mapping.TargetMetaverseAttribute!.Type,
                    AttributeName = p.Mapping.TargetMetaverseAttribute!.Name,
                    BaseValue = p.BaseValue,
                    ConnectorSpaceAttributeIds = connectorSpaceAttributeIds,
                    OwnConnectedSystemObjectIds = ownCsoId.HasValue ? [ownCsoId.Value] : [],
                    DisconnectingConnectedSystemObjectId = disconnectingCsoId,
                    StickyOnly = p.BaseUnavailable
                });
            }

            var outcomes = await context.UniqueValueGenerationServer.ResolveAsync(requests, context.UniqueValueResolveOptions);

            for (var i = 0; i < outcomes.Count; i++)
            {
                var outcome = outcomes[i];
                var request = pending[i];

                switch (outcome.Kind)
                {
                    case GenerationOutcomeKind.Generated:
                        _syncEngine.ApplyGeneratedValue(workingMvo, request, outcome.Value, outcome.NumericValue);
                        forOutcomeTree.Add((ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned, request.Mapping.TargetMetaverseAttribute!.Name, outcome.Value!));
                        await RecordGeneratedValueProbesAsync(result, request.Mapping, outcome.Value!, context, exportRules);
                        break;

                    case GenerationOutcomeKind.Sticky:
                        // Re-apply unconditionally, exactly as the worker does (generate once): a no-op when the
                        // object already holds the value; otherwise the winning generated mapping overwrites
                        // whatever another rule left behind, or a value cleared this pass.
                        _syncEngine.ApplyGeneratedValue(workingMvo, request, outcome.Value, outcome.NumericValue);
                        break;

                    case GenerationOutcomeKind.Waiting:
                        // Nothing to do: any existing value stays, and there is nothing new to apply.
                        break;

                    case GenerationOutcomeKind.Exhausted:
                    case GenerationOutcomeKind.NoBaseValue:
                    case GenerationOutcomeKind.WidthExceeded:
                        // A generation that would fail in the real run must not simply show nothing (#242,
                        // Phase 2 work package J): the reservation and other-live-assignment gates are
                        // time-sensitive, so a preview cannot promise a real run would hit the exact same
                        // collision, but the failure kind and the attribute it concerns are worth surfacing
                        // as a warning rather than left silent.
                        result.Warnings.Add(new SyncPreviewMessage
                        {
                            Code = SyncPreviewMessageCode.GeneratedValueWouldFail,
                            Detail = outcome.FailureMessage ?? "The generated value could not be resolved.",
                            ConnectedSystemId = context.ConnectedSystemId,
                            AttributeName = request.Mapping.TargetMetaverseAttribute!.Name
                        });
                        break;
                }
            }
        }
        finally
        {
            // Always clear, on every exit path, exactly like the worker's own resolution method: a leftover
            // request here would carry into whatever else touches this working copy next.
            workingMvo.PendingGeneratedValues.Clear();
        }

        return forOutcomeTree;
    }

    /// <summary>
    /// Records that the real synchronisation would probe for <paramref name="value"/> (#242, release 3), naming the
    /// Connected Systems it would probe, so the preview can say that it checked JIM's own records only. The preview
    /// itself never probes: it is a dry run. Computed from this preview's own export rules, through the same read
    /// model as the generated mapping's "Checked for availability in" panel.
    /// </summary>
    private async Task RecordGeneratedValueProbesAsync(SyncPreviewResult result, SyncRuleMapping mapping, string value, CsoPreviewContext context, IEnumerable<SyncRule> exportRules)
    {
        var generationId = mapping.Generation!.Id;
        if (!context.ProbedSystemNamesByGenerationId.TryGetValue(generationId, out var names))
        {
            var participants = await Application.ConnectedSystems.GetGeneratedValueParticipantsAsync(mapping, context.ConnectedSystemId, exportRules);
            names = participants
                .Where(p => p.Check == GeneratedValueParticipantCheck.JimRecordsAndProbe)
                .Select(p => p.ConnectedSystemName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            context.ProbedSystemNamesByGenerationId[generationId] = names;
        }

        if (names.Count == 0)
            return;

        result.GeneratedValueProbes.Add(new SyncPreviewGeneratedValueProbe
        {
            AttributeName = mapping.TargetMetaverseAttribute!.Name,
            Value = value,
            ConnectedSystemNames = [.. names]
        });
    }

    /// <summary>
    /// The Connected System Objects joined to a Metaverse Object, for the out-of-scope cascade's
    /// deletion-rule and downstream-deprovisioning reads. Prefers the full-system walk's per-page
    /// prefetch (<see cref="CsoPreviewContext.JoinedCsosByMvoIdForDeletion"/>) when one was populated, so a
    /// page's out-of-scope objects cost one batched query rather than one each; falls back to a live
    /// per-object read for the single/few-object preview entry points, which have no page to batch across.
    /// </summary>
    private static async Task<List<ConnectedSystemObject>> GetJoinedCsosForDeletionAsync(CsoPreviewContext context, Guid mvoId)
    {
        if (context.JoinedCsosByMvoIdForDeletion != null)
            return context.JoinedCsosByMvoIdForDeletion.GetValueOrDefault(mvoId) ?? [];

        var joinedCsosByMvo = await context.GuardedRepository.GetConnectedSystemObjectsForMvoDeletionAsync([mvoId]);
        return joinedCsosByMvo.GetValueOrDefault(mvoId) ?? [];
    }

    /// <summary>
    /// The retained join (#1649): a JOINED object whose out-of-scope action is RemainJoined keeps its Metaverse
    /// Object join, so the real run records a single OutOfScopeRetainJoin root attributed to the scoping rule and
    /// nothing beneath it (nothing flows, nothing is recalled, nothing downstream changes). Mirrored here so the
    /// preview's tree has the same shape as the one the run records.
    /// </summary>
    /// <param name="result">The preview result to add the root to. The object's
    /// <see cref="SyncPreviewMessageCode.OutOfScope"/> warning has already been added by the caller.</param>
    /// <param name="cso">The Connected System Object falling out of scope. Must be joined
    /// (<see cref="ConnectedSystemObject.MetaverseObjectId"/> set); the caller checks this before calling.</param>
    /// <param name="importRules">The applicable import Synchronisation Rules, for the scoping rule attribution:
    /// the same first-applicable rule the real run attributes the retained join to.</param>
    /// <param name="context">The shared read-only inputs for the object's Connected System.</param>
    private static async Task BuildRetainedJoinRootAsync(
        SyncPreviewResult result,
        ConnectedSystemObject cso,
        List<SyncRule> importRules,
        CsoPreviewContext context)
    {
        var mvoId = cso.MetaverseObjectId!.Value;
        var joinedMvo = (await context.GuardedRepository.GetMetaverseObjectsByIdsNoTrackingAsync([mvoId])).SingleOrDefault();
        var scopingSyncRule = importRules.FirstOrDefault(sr => sr.ObjectScopingCriteriaGroups.Count > 0);

        result.OutcomeTree.Add(new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin,
            TargetEntityId = mvoId,
            TargetEntityDescription = ObjectNaming.FirstPresent(joinedMvo?.Name),
            SyncRuleId = scopingSyncRule?.Id,
            SyncRuleName = scopingSyncRule?.Name
        });
    }

    /// <summary>
    /// The out-of-scope destructive cascade (#288 Phase 1 of the Sync Preview Surface plan): a JOINED object
    /// whose out-of-scope action is Disconnect does not just leave scope in a real synchronisation, it
    /// disconnects, puts the Metaverse Object to its type's Deletion Rule, and (when the object dies)
    /// deprovisions every other joined Connected System Object. Every decision here is put to the same pure
    /// engine method the real run calls (<see cref="ISyncEngine.EvaluateMvoDeletionRule"/> and
    /// <see cref="ISyncEngine.DecideMvoDeletionExport"/>); nothing about the decisions is reimplemented, only
    /// the node construction that <c>SyncTaskProcessorBase</c> and <c>ExportEvaluationServer</c> would
    /// otherwise perform against real entities. Every read goes through the caller's guarded repository, and
    /// the Metaverse Object worked on is a preview-owned clone (<see cref="CloneForPreview"/>), so nothing
    /// shared is mutated and nothing is persisted.
    /// </summary>
    /// <param name="result">The preview result to add the cascade's tree nodes, warnings and proposed
    /// deletions to. The disconnecting object's <see cref="SyncPreviewMessageCode.OutOfScope"/> warning has
    /// already been added by the caller.</param>
    /// <param name="cso">The Connected System Object falling out of scope. Must be joined
    /// (<see cref="ConnectedSystemObject.MetaverseObjectId"/> set); the caller checks this before calling.</param>
    /// <param name="importRules">The applicable import Synchronisation Rules, for the scoping rule
    /// attribution (#1085): the same first-applicable rule the real run attributes the disconnect to.</param>
    /// <param name="context">The shared read-only inputs for the object's Connected System.</param>
    /// <param name="refreshCacheForWorkingMvo">Whether to refresh the outbound cache for the Metaverse Object before
    /// evaluating the recall's exports; see <see cref="PreviewCsoCoreAsync"/>.</param>
    private async Task BuildOutOfScopeCascadeAsync(
        SyncPreviewResult result,
        ConnectedSystemObject cso,
        List<SyncRule> importRules,
        CsoPreviewContext context,
        bool refreshCacheForWorkingMvo)
    {
        var guardedRepository = context.GuardedRepository;
        var mvoId = cso.MetaverseObjectId!.Value;

        var joinedMvo = (await guardedRepository.GetMetaverseObjectsByIdsNoTrackingAsync([mvoId])).SingleOrDefault();
        if (joinedMvo == null)
            return; // defensive: nothing to cascade if the joined object has vanished since the CSO was loaded

        // A preview-owned clone, exactly as the ordinary inbound chain works on: attribute recall mutates
        // pending-change lists, and none of that may touch the shared instance.
        var workingMvo = CloneForPreview(joinedMvo);
        var mvoDisplayName = ObjectNaming.FirstPresent(joinedMvo.Name);
        if (result.Inbound != null)
            DescribeMetaverseObject(result.Inbound, joinedMvo);

        // The same first-applicable-scoping-rule attribution the real run's DisconnectedOutOfScope root
        // carries (#1085): the CSO fell out of scope of every rule with Scoping Criteria, so when several
        // exist the attribution is the deterministic first one, the one whose action governs the disconnect.
        var scopingSyncRule = importRules.FirstOrDefault(sr => sr.ObjectScopingCriteriaGroups.Count > 0);

        // Every Connected System Object still joined to the Metaverse Object (the disconnecting CSO
        // included: this preview never actually disconnects it). One dataset answers both questions the
        // cascade asks: the Connected System of each entry is the remaining-connectors input to the
        // deletion rule below, and the entries themselves are the downstream deprovisioning candidates
        // further down. Read once, from the page-batched prefetch when the full-system walk populated one
        // (context.JoinedCsosByMvoIdForDeletion), or as a live per-object read otherwise.
        var joinedCsos = await GetJoinedCsosForDeletionAsync(context, mvoId);
        if (joinedCsos.All(c => c.Id != cso.Id))
            joinedCsos = [.. joinedCsos, cso]; // defensive: keep the arithmetic correct if the lean read ever omits it

        // Remaining connectors after this one CSO's disconnect: the joined Connected System ids, minus one
        // occurrence of the disconnecting system's id (RemainingConnectorsCalculator mirrors
        // HandleCsoOutOfScopeAsync's List.Remove semantics exactly for the single-CSO case; see its remarks).
        var joinedSystemIds = joinedCsos.Select(c => c.ConnectedSystemId).ToList();
        var remainingConnectedSystemIds = RemainingConnectorsCalculator.RemainingConnectorsAfterDisconnection(
            joinedSystemIds, context.ConnectedSystemId);

        // The disconnecting system's name, so the decision's reason reads as the real run's does (which has
        // the system to hand as _connectedSystem.Name); the shared name lookup is what this preview has.
        context.SystemNames.TryGetValue(context.ConnectedSystemId, out var disconnectingSystemName);
        var deletionDecision = _syncEngine.EvaluateMvoDeletionRule(
            workingMvo, context.ConnectedSystemId, remainingConnectedSystemIds, disconnectingSystemName);

        // Attribute recall, mirrored from HandleCsoOutOfScopeAsync (#91, #1570): skipped entirely when the
        // Metaverse Object would be deleted immediately, since the work would be discarded moments later
        // (#390 optimisation) - the real run applies the identical short-circuit.
        var attributeChangeCount = 0;
        var clearedAttributeCount = 0;
        var preservedNoSourceAttributeCount = 0;
        // Pending covers both a deletion this disconnect would schedule and one already scheduled from an
        // earlier disconnect (DeletionEligibleDate set), exactly as HandleCsoOutOfScopeAsync tests it.
        var mvoDeletionPending = deletionDecision.Fate == MvoDeletionFate.DeletionScheduled
            || joinedMvo.DeletionEligibleDate != null;
        var skipRecallForImmediateDeletion = deletionDecision.Fate == MvoDeletionFate.DeletedImmediately;
        var csoType = context.ObjectTypes.FirstOrDefault(ot => ot.Id == cso.TypeId);
        var generatedValueOutcomes = new List<(ActivityRunProfileExecutionItemSyncOutcomeType OutcomeType, string AttributeName, string Value)>();

        if (csoType is { RemoveContributedAttributesOnObsoletion: true } && !skipRecallForImmediateDeletion && workingMvo.Type != null)
        {
            var contributedAttributes = workingMvo.AttributeValues
                .Where(av => av.ContributedBySystemId == context.ConnectedSystemId)
                .ToList();
            foreach (var attributeValue in contributedAttributes)
                workingMvo.PendingAttributeValueRemovals.Add(attributeValue);

            // Next-contributor recall fallback (#91): re-elect any still-joined lower-priority contributor
            // before the attribute is treated as genuinely cleared, exactly as the real disconnect does.
            // Unique Value Generation (#242, Phase 2 work package J): pass the preview's own dry-run resolver
            // so a re-elected survivor's generated mapping is resolved through this preview's context (same as
            // the ordinary inbound chain's ResolvePendingGeneratedValuesForPreviewAsync call), rather than
            // silently dropped, which is what happened before this fix (no resolver was passed at all).
            generatedValueOutcomes = await ContributorReElectionService.ReElectSurvivingContributorsAsync(
                workingMvo,
                contributedAttributes,
                ContributorRecallScope.ForObsoletingConnectedSystemObject(cso),
                context.PriorityContext,
                _syncEngine,
                guardedRepository,
                (survivor, rule) => Application.ScopingEvaluation.IsCsoInScopeForImportRule(survivor, rule),
                context.ObjectTypes,
                ExpressionEvaluator,
                resolvePendingGeneratedValues: resolvedMvo => ResolvePendingGeneratedValuesForPreviewAsync(result, resolvedMvo, context, disconnectingCsoId: cso.Id),
                leaveSurvivorsAsFound: true);

            var remainingImportSourceEvaluator = new RemainingImportSourceEvaluator(guardedRepository);
            var noImportSourceRemains = !await remainingImportSourceEvaluator.AnyImportSourceRemainsAsync(
                remainingConnectedSystemIds, workingMvo.Type.Id);

            if (mvoDeletionPending || noImportSourceRemains)
            {
                // Freeze: an attribute with no surviving contributor is preserved (a pending deletion's
                // grace window, or as last-known state when no import source remains), not cleared.
                // Re-elected attributes still replace the leaver's value; only the non-re-elected ones
                // are unmarked.
                var reElectedDuringFreeze = workingMvo.PendingAttributeValueAdditions.Select(a => a.AttributeId).ToHashSet();
                var frozenValues = contributedAttributes.Where(av => !reElectedDuringFreeze.Contains(av.AttributeId)).ToList();
                foreach (var frozen in frozenValues)
                    workingMvo.PendingAttributeValueRemovals.Remove(frozen);

                // A pending deletion explains itself via the deletion outcome; only the no-source
                // preservation gets its own ValuesPreserved outcome (#1570).
                if (!mvoDeletionPending)
                    preservedNoSourceAttributeCount = frozenValues.Count;
            }

            attributeChangeCount = workingMvo.PendingAttributeValueRemovals.Count + workingMvo.PendingAttributeValueAdditions.Count;
            if (attributeChangeCount > 0)
            {
                clearedAttributeCount = ContributorReElectionService.GetClearedAttributeIds(
                    workingMvo, workingMvo.PendingAttributeValueAdditions, workingMvo.PendingAttributeValueRemovals).Count;

                // The run queues the recall for export evaluation (HandleCsoOutOfScopeAsync), so every target holding a
                // recalled value is sent the change (#1530). Evaluated over the same change set, additions first and
                // every removal in the removed set, so a genuine clear null-clears the target and a re-elected value
                // exports as a change of value. The run records no outcome node for these exports on the departing
                // object's item, so they are proposed without one.
                var changedAttributes = workingMvo.PendingAttributeValueAdditions.Concat(workingMvo.PendingAttributeValueRemovals).ToList();
                var removedAttributes = workingMvo.PendingAttributeValueRemovals.ToHashSet();
                RecordMetaverseChanges(result, workingMvo.PendingAttributeValueAdditions, workingMvo.PendingAttributeValueRemovals);
                _syncEngine.ApplyPendingAttributeChanges(workingMvo);
                if (refreshCacheForWorkingMvo)
                    await context.PreviewServer.RefreshExportEvaluationCacheForPageAsync(context.Cache, [workingMvo.Id]);
                ComposeOutbound(result, await context.PreviewServer.EvaluateOutboundPreviewForMaterialisedMvosAsync([workingMvo], context.Cache,
                    synchronisationChanges: (changedAttributes, removedAttributes)));
            }
        }

        // The DisconnectedOutOfScope root: same shape as the real run's (#1085 attribution, #1086 identity
        // snapshot), DetailCount carrying the attribute change count only when there is one to show.
        var root = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
            TargetEntityId = mvoId,
            TargetEntityDescription = mvoDisplayName,
            SyncRuleId = scopingSyncRule?.Id,
            SyncRuleName = scopingSyncRule?.Name,
            DetailCount = attributeChangeCount > 0 ? attributeChangeCount : null
        };
        result.OutcomeTree.Add(root);

        // Unique Value Generation (#242, Phase 2 work package J): one GeneratedValueAssigned child
        // per resolved attribute, mirroring exactly where the ordinary inbound
        // chain records them (a child of the root, alongside the AttributeFlow child below, not gated to a
        // tracking level since a generated value is as much an audit signal in a preview as in a real run).
        foreach (var (generatedOutcomeType, attributeName, value) in generatedValueOutcomes)
        {
            root.Children.Add(new SyncOutcomeNode
            {
                OutcomeType = generatedOutcomeType,
                TargetEntityId = mvoId,
                TargetEntityDescription = mvoDisplayName,
                DetailMessage = $"{attributeName}: {value}",
                Ordinal = root.Children.Count
            });
        }

        if (attributeChangeCount > 0)
        {
            root.Children.Add(new SyncOutcomeNode
            {
                OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                TargetEntityDescription = mvoDisplayName,
                DetailCount = attributeChangeCount,
                Ordinal = root.Children.Count
            });
        }

        if (clearedAttributeCount > 0)
        {
            root.Children.Add(new SyncOutcomeNode
            {
                OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor,
                TargetEntityDescription = mvoDisplayName,
                DetailCount = clearedAttributeCount,
                Ordinal = root.Children.Count
            });
        }

        if (preservedNoSourceAttributeCount > 0)
        {
            root.Children.Add(new SyncOutcomeNode
            {
                OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved,
                TargetEntityDescription = mvoDisplayName,
                DetailCount = preservedNoSourceAttributeCount,
                Ordinal = root.Children.Count
            });
        }

        if (deletionDecision.Fate == MvoDeletionFate.NotDeleted)
            return;

        var deletionOutcomeType = deletionDecision.Fate == MvoDeletionFate.DeletedImmediately
            ? ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
            : ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled;
        // A speculative eligible-deletion date, computed for display only (never persisted): the real run
        // stamps LastConnectorDisconnectedDate at the moment it marks the object and adds the grace period;
        // "now" is this preview's best estimate of that moment.
        var speculativeEligibleDate = deletionDecision.Fate == MvoDeletionFate.DeletionScheduled
            ? DateTime.UtcNow.Add(deletionDecision.GracePeriod ?? TimeSpan.Zero)
            : (DateTime?)null;
        var deletionNode = new SyncOutcomeNode
        {
            OutcomeType = deletionOutcomeType,
            TargetEntityId = mvoId,
            TargetEntityDescription = mvoDisplayName,
            DetailMessage = ConnectedSystemObjectObsoletionService.BuildMvoDeletionDetailMessage(
                deletionDecision.Fate, deletionDecision.Reason, deletionDecision.GracePeriod, speculativeEligibleDate),
            Ordinal = root.Children.Count
        };
        root.Children.Add(deletionNode);

        // A scheduled deletion stages nothing: the object still exists, so there is nothing yet to
        // deprovision downstream. Only an immediate deletion cascades further (mirrors
        // SyncTaskProcessorBase.FindMvoDeletedOutcomeNodes, which nests deprovisioning only under MvoDeleted).
        if (deletionDecision.Fate != MvoDeletionFate.DeletedImmediately)
            return;

        await AddDownstreamDeprovisioningAsync(result, deletionNode, mvoId, workingMvo.Type?.Id, joinedCsos, cso.Id, context);
    }

    /// <summary>
    /// The teardown of an obsolete Connected System Object (#1530), which a synchronisation performs before processing
    /// anything else (<c>SyncTaskProcessorBase.ProcessObsoleteConnectedSystemObjectAsync</c>): the object is disconnected
    /// from its Metaverse Object, which is put to its type's Deletion Rule; what the object contributed is recalled (or
    /// kept, where a deletion is pending or no import source remains); and the object is deleted. Previewed through the
    /// run's own obsoletion core (<see cref="ConnectedSystemObjectObsoletionService"/>), driven read-only on preview-owned
    /// clones as the Connected System deletion preview drives it, so the tree it records is the tree the run records.
    /// What follows at the run's flush is previewed after it: the exports the recall causes, and the downstream
    /// deprovisioning of an immediately deleted Metaverse Object.
    /// </summary>
    /// <param name="cso">The obsolete Connected System Object.</param>
    /// <param name="context">The shared read-only inputs for the object's Connected System.</param>
    /// <param name="refreshCacheForWorkingMvo">Whether to refresh the outbound cache for the Metaverse Object before
    /// evaluating the recall's exports; see <see cref="PreviewCsoCoreAsync"/>.</param>
    private async Task<SyncPreviewResult> PreviewObsoleteCsoAsync(ConnectedSystemObject cso, CsoPreviewContext context, bool refreshCacheForWorkingMvo)
    {
        var result = new SyncPreviewResult { Inbound = new SyncPreviewInboundSummary { AlreadyJoinedMetaverseObjectId = cso.MetaverseObjectId } };
        var guardedRepository = context.GuardedRepository;

        MetaverseObject? joinedMvo = null;
        if (cso.MetaverseObjectId is { } joinedMvoId)
            joinedMvo = (await guardedRepository.GetMetaverseObjectsByIdsNoTrackingAsync([joinedMvoId])).SingleOrDefault();
        if (joinedMvo != null)
            DescribeMetaverseObject(result.Inbound, joinedMvo);
        var workingCso = ObsoletionPreviewClone.Of(cso, joinedMvo, []);

        var teardown = await ConnectedSystemObjectObsoletionService.ProcessObsoleteConnectedSystemObjectAsync(
            workingCso,
            context.SyncRules,
            ContributorRecallScope.ForObsoletingConnectedSystemObject(workingCso),
            context.PriorityContext,
            new RemainingImportSourceEvaluator(guardedRepository),
            _syncEngine,
            guardedRepository,
            (survivor, rule) => Application.ScopingEvaluation.IsCsoInScopeForImportRule(survivor, rule),
            context.ObjectTypes,
            ExpressionEvaluator,
            () => new ActivityRunProfileExecutionItem(),
            ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed,
            (mvo, disconnectingSystemId, remainingConnectedSystemIds) =>
                Task.FromResult(DecideDeletionForPreview(mvo, disconnectingSystemId, remainingConnectedSystemIds, context)),
            recordPreRecallAttributeSnapshot: _ => { },
            resolvePendingGeneratedValues: resolvedMvo => ResolvePendingGeneratedValuesForPreviewAsync(result, resolvedMvo, context, disconnectingCsoId: cso.Id),
            leaveSurvivorsAsFound: true);

        if (teardown.MvoAttributeChange is { } recall)
            RecordMetaverseChanges(result, recall.Additions, recall.Removals);

        result.OutcomeTree.AddRange(teardown.ExecutionItems
            .SelectMany(item => item.SyncOutcomes)
            .Where(outcome => outcome.ParentSyncOutcome == null && !outcome.ParentSyncOutcomeId.HasValue)
            .OrderBy(outcome => outcome.Ordinal)
            .Select(SyncOutcomeNode.FromSyncOutcome));

        // The recall's exports, queued by the core as the run queues them. The run records no outcome node for them on
        // the obsolete object's item, so they are proposed without one.
        if (teardown.ExportEvaluation is { } exportEvaluation)
        {
            if (refreshCacheForWorkingMvo)
                await context.PreviewServer.RefreshExportEvaluationCacheForPageAsync(context.Cache, [exportEvaluation.Mvo.Id]);
            ComposeOutbound(result, await context.PreviewServer.EvaluateOutboundPreviewForMaterialisedMvosAsync([exportEvaluation.Mvo], context.Cache,
                synchronisationChanges: (exportEvaluation.ChangedAttributes, exportEvaluation.RemovedAttributes)));
        }

        // An immediate deletion deprovisions downstream, nested under its deletion node as the run nests it.
        if (joinedMvo != null && teardown.MvoDeletionDecision?.Fate == MvoDeletionFate.DeletedImmediately
            && FindNode(result.OutcomeTree, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted) is { } deletionNode)
        {
            await AddDownstreamDeprovisioningAsync(result, deletionNode, joinedMvo.Id, joinedMvo.Type?.Id,
                await GetJoinedCsosForDeletionAsync(context, joinedMvo.Id), cso.Id, context);
        }

        Log.Debug("PreviewObsoleteCsoAsync: Previewed the teardown of obsolete CSO {CsoId} in system {SystemId}: deletion fate {Fate}, {ExportCount} export(s) proposed.",
            cso.Id, context.ConnectedSystemId, teardown.MvoDeletionDecision?.Fate, result.Outbound.ProposedExports.Count);
        return result;
    }

    /// <summary>
    /// The Deletion Rule's decision for a Metaverse Object whose connector is leaving, evaluated by the engine the run
    /// asks and applied to nothing but the preview's own clone: a scheduled deletion is stamped on the clone as the run
    /// stamps it, so the due date the outcome states is the one the run would record.
    /// </summary>
    private (MvoDeletionDecision Decision, string? PolicySnapshotJson) DecideDeletionForPreview(MetaverseObject mvo, int disconnectingSystemId,
        IReadOnlyCollection<int> remainingConnectedSystemIds, CsoPreviewContext context)
    {
        context.SystemNames.TryGetValue(disconnectingSystemId, out var disconnectingSystemName);
        var decision = _syncEngine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId, remainingConnectedSystemIds, disconnectingSystemName);
        if (decision.Fate == MvoDeletionFate.DeletionScheduled && mvo.LastConnectorDisconnectedDate == null)
        {
            mvo.LastConnectorDisconnectedDate = DateTime.UtcNow;
            mvo.DeletionTriggeredBySystemId = disconnectingSystemId;
        }

        return (decision, null);
    }

    /// <summary>
    /// The first node of <paramref name="outcomeType"/> in a tree, depth first.
    /// </summary>
    private static SyncOutcomeNode? FindNode(IEnumerable<SyncOutcomeNode> nodes, ActivityRunProfileExecutionItemSyncOutcomeType outcomeType) =>
        nodes.Select(node => node.OutcomeType == outcomeType ? node : FindNode(node.Children, outcomeType))
            .FirstOrDefault(found => found != null);

    /// <summary>
    /// What an immediate deletion of a Metaverse Object does downstream, nested under its deletion node as the run nests it
    /// (<c>SyncTaskProcessorBase.FindMvoDeletedOutcomeNodes</c>): every other Connected System Object still joined to it has
    /// its never-exported provisioning cancelled, or is deprovisioned, or (where no export rule stages a delete) is
    /// disconnected and left in place. Shared by a scope exit's cascade and an obsolete object's teardown (#1530).
    /// </summary>
    /// <param name="result">The preview result to add the proposed deletions and warnings to.</param>
    /// <param name="deletionNode">The <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted"/> node.</param>
    /// <param name="mvoId">The Metaverse Object being deleted.</param>
    /// <param name="mvoTypeId">Its type, which selects the export rules that decide each object's fate.</param>
    /// <param name="joinedCsos">Every Connected System Object joined to it, the disconnecting one included.</param>
    /// <param name="disconnectingCsoId">The object whose departure deletes it, which is not deprovisioned.</param>
    /// <param name="context">The shared read-only inputs for the previewed Connected System.</param>
    private async Task AddDownstreamDeprovisioningAsync(SyncPreviewResult result, SyncOutcomeNode deletionNode, Guid mvoId, int? mvoTypeId,
        List<ConnectedSystemObject> joinedCsos, Guid disconnectingCsoId, CsoPreviewContext context)
    {
        // Every OTHER Connected System Object still joined to the Metaverse Object, from the joined set the caller
        // read (the disconnecting CSO is excluded explicitly: this preview never actually disconnects it, so it is
        // still present in that set).
        var exportRulesByMvoTypeId = context.Cache.ExportRulesByMvoTypeId;

        // Provisioning that was never exported is cancelled outright by the real run, ahead of and regardless
        // of the rules (ExportEvaluationServer.EvaluateMvoDeletionsAsync), so it must be here too. The Pending
        // Export read is paid only when a downstream object is Pending Provisioning, as in the real run.
        var pendingProvisioningCsoIds = joinedCsos
            .Where(c => c.Id != disconnectingCsoId && c.Status == ConnectedSystemObjectStatus.PendingProvisioning)
            .Select(c => c.Id)
            .ToList();
        var existingPesByCsoId = pendingProvisioningCsoIds.Count > 0
            ? await context.GuardedRepository.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(pendingProvisioningCsoIds)
            : [];

        foreach (var downstreamCso in joinedCsos.Where(c => c.Id != disconnectingCsoId))
        {
            if (_syncEngine.IsProvisioningNeverExported(downstreamCso, existingPesByCsoId.GetValueOrDefault(downstreamCso.Id)))
            {
                context.SystemNames.TryGetValue(downstreamCso.ConnectedSystemId, out var cancelledSystemName);
                deletionNode.Children.Add(new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled,
                    TargetEntityDescription = cancelledSystemName,
                    DetailMessage = downstreamCso.ConnectedSystemId.ToString(),
                    Ordinal = deletionNode.Children.Count
                });
                continue;
            }

            var exportDecision = _syncEngine.DecideMvoDeletionExport(
                downstreamCso, mvoTypeId, exportRulesByMvoTypeId, existingPendingExport: null);
            context.SystemNames.TryGetValue(downstreamCso.ConnectedSystemId, out var targetSystemName);

            if (!exportDecision.ShouldStageDeleteExport)
            {
                // No node in the real tree either (SyncEngine.ExportEvaluation.DecideMvoDeletionExport's
                // disconnect-only verdicts get no outcome; ExportEvaluationServer.EvaluateMvoDeletionsAsync
                // just disconnects). Surfaced as a warning instead, so the surfaces can still show it.
                result.Warnings.Add(new SyncPreviewMessage
                {
                    Code = SyncPreviewMessageCode.DownstreamDisconnectOnly,
                    Detail = $"The Metaverse Object's deletion would disconnect its Connected System Object in " +
                        $"'{targetSystemName ?? downstreamCso.ConnectedSystemId.ToString()}' without deprovisioning it " +
                        "(no matching export Synchronisation Rule stages a delete).",
                    ConnectedSystemId = downstreamCso.ConnectedSystemId
                });
                continue;
            }

            // The secondary external ID (e.g. DN for LDAP), captured the same way
            // ExportEvaluationServer.EvaluateMvoDeletionsAsync builds the delete export, so the DetailCount
            // below matches what the real run would record.
            var attributeChanges = new List<PendingExportAttributeValueChange>();
            if (exportDecision.SecondaryExternalIdAttribute != null && exportDecision.SecondaryExternalIdValue != null)
            {
                attributeChanges.Add(new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    Attribute = exportDecision.SecondaryExternalIdAttribute,
                    AttributeId = exportDecision.SecondaryExternalIdAttribute.Id,
                    StringValue = exportDecision.SecondaryExternalIdValue,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    SyncRuleId = exportDecision.WinningRule?.Id,
                    SyncRuleName = exportDecision.WinningRule?.Name
                });
            }

            result.Outbound.ProposedExports.Add(new PendingExport
            {
                ChangeType = PendingExportChangeType.Delete,
                ConnectedSystemId = downstreamCso.ConnectedSystemId,
                ConnectedSystemObjectId = downstreamCso.Id,
                SourceMetaverseObjectId = mvoId,
                AttributeValueChanges = attributeChanges
            });

            deletionNode.Children.Add(new SyncOutcomeNode
            {
                OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
                TargetEntityDescription = targetSystemName,
                DetailCount = attributeChanges.Count,
                DetailMessage = downstreamCso.ConnectedSystemId.ToString(),
                StagedChangeType = PendingExportChangeType.Delete,
                Ordinal = deletionNode.Children.Count
            });
        }
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
        AttributePriorityContext PriorityContext,
        UniqueValueGenerationServer UniqueValueGenerationServer,
        UniqueValueResolveOptions UniqueValueResolveOptions,
        string? DerivedFlowCycleMessage,
        List<SyncRule> DriftExportRules,
        Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>> ImportMappingCache)
    {
        /// <summary>
        /// Per-page prefetch of every Connected System Object joined to this page's joined Metaverse
        /// Objects (#288 Phase 1 of the Sync Preview Surface plan), keyed by Metaverse Object id: the
        /// out-of-scope destructive cascade's deletion-rule and downstream-deprovisioning reads consult
        /// this first. Set once per page by <see cref="PreviewFullSyncAsync"/>; null for the single/few-object
        /// preview entry points (<see cref="PreviewSyncForCsoAsync"/>, <see cref="PreviewSyncForCsosAsync"/>),
        /// which have no page to batch across and fall back to a live per-object read in
        /// <see cref="BuildOutOfScopeCascadeAsync"/>.
        /// </summary>
        public Dictionary<Guid, List<ConnectedSystemObject>>? JoinedCsosByMvoIdForDeletion { get; set; }

        /// <summary>
        /// Per generated mapping (keyed on <c>SyncRuleMappingGeneration.Id</c>), the names of the Connected Systems the
        /// real synchronisation would probe for its value (#242, release 3), computed once from this context's own export
        /// rules on first need, so a full-system preview asks each Connector once rather than once per object.
        /// </summary>
        public Dictionary<int, List<string>> ProbedSystemNamesByGenerationId { get; } = [];

        /// <summary>
        /// The Metaverse Objects flagged for export scope review that a walked object's preview already evaluated
        /// outbound (#1530), so the review after a full-system walk does not propose their exports a second time.
        /// </summary>
        public HashSet<Guid> ScopeReviewedMetaverseObjectIds { get; } = [];
    }

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

        AddOutboundToCounts(counts, preview);
    }

    /// <summary>
    /// Folds one preview's proposed exports into the whole-population outbound counters.
    /// </summary>
    private static void AddOutboundToCounts(FullSyncPreviewCounts counts, SyncPreviewResult preview)
    {
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

                // A cancelled provisioning proposes no export: nothing exists in the target system, so there
                // is nothing to stage. It is still surfaced in the outcome tree, by BuildOutboundOutcomeNodes.
                case OutboundPreviewEntryKind.ProvisioningCancelled:
                    break;
            }
        }
    }

    /// <summary>
    /// Builds the outbound outcome nodes in the real tree's shape: a Provisioned node (with the staged
    /// Pending Export nested beneath) where the preview would create a target object, a Pending Export
    /// node where it would update one, a Deprovision Queued node where an out-of-scope object would
    /// have a Delete staged, and a Target Disconnected node where it would be disconnected instead.
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

                // A Disconnect stages nothing, but the real run still records the disconnection on the object's item
                // (#1966), so the preview proposes the same node and no export.
                case OutboundPreviewEntryKind.Deprovisioning
                    when entry.DeprovisioningDecision?.Action == OutOfScopeDeprovisioningAction.Disconnect:
                    siblings.Add(new SyncOutcomeNode
                    {
                        OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.TargetDisconnected,
                        TargetEntityId = entry.ExistingTargetCsoId,
                        TargetEntityDescription = systemName,
                        SyncRuleId = entry.SyncRuleId,
                        SyncRuleName = entry.SyncRuleName,
                        DetailMessage = entry.ConnectedSystemId.ToString(),
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
                        // A scope exit's Delete carries no attribute changes (unlike a deletion cascade's, which
                        // carries the target's secondary external id), and the real run records that count (#1964).
                        DetailCount = 0,
                        DetailMessage = entry.ConnectedSystemId.ToString(),
                        StagedChangeType = PendingExportChangeType.Delete,
                        Ordinal = siblings.Count
                    });
                    break;

                case OutboundPreviewEntryKind.ProvisioningCancelled:
                    siblings.Add(new SyncOutcomeNode
                    {
                        OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled,
                        TargetEntityDescription = systemName,
                        SyncRuleId = entry.SyncRuleId,
                        SyncRuleName = entry.SyncRuleName,
                        DetailMessage = entry.ConnectedSystemId.ToString(),
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
    /// <summary>
    /// Names the Metaverse Object a preview's inbound summary is about (#1530): its type, and its name as it stands.
    /// </summary>
    private static void DescribeMetaverseObject(SyncPreviewInboundSummary inbound, MetaverseObject mvo)
    {
        inbound.MetaverseObjectTypeId = mvo.Type?.Id;
        inbound.MetaverseObjectTypeName = mvo.Type?.Name;
        inbound.MetaverseObjectDisplayName = ObjectNaming.FirstPresent(mvo.Name);
    }

    /// <summary>
    /// Records a departing object's recall in the inbound summary (#1530), additions then removals as Attribute Flow's
    /// are, so the values withdrawn (and any surviving contributor's taking their place) can be stated per attribute.
    /// </summary>
    private static void RecordMetaverseChanges(SyncPreviewResult result, IEnumerable<MetaverseObjectAttributeValue> additions,
        IEnumerable<MetaverseObjectAttributeValue> removals)
    {
        result.Inbound ??= new SyncPreviewInboundSummary();
        result.Inbound.AttributeFlowChanges.AddRange(additions.Select(addition => BuildAttributeFlowChange(addition, isAddition: true)));
        result.Inbound.AttributeFlowChanges.AddRange(removals.Select(removal => BuildAttributeFlowChange(removal, isAddition: false)));
    }

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
    private static string? RenderValue(MetaverseObjectAttributeValue value) => PreviewValueRenderer.Render(value);

    #endregion
}
