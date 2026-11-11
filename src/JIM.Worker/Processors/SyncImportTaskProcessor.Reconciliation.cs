// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using JIM.Application.Diagnostics;
using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using Serilog;

namespace JIM.Worker.Processors;

/// <summary>
/// Pending Export reconciliation: the confirming import's check that what was exported is what the Connected System now
/// holds. It runs as each update batch of the save phase is saved (#2908), so it judges each object from the values the
/// import holds for it, exactly as saved, while they are still in memory, and writes the outcomes it merges onto that
/// batch's execution items before the next batch. An object whose held values cannot be shown to be what was saved is
/// reloaded from the database, as every object used to be.
/// </summary>
public partial class SyncImportTaskProcessor
{
    /// <summary>
    /// One run's reconciliation: what it was handed once, and what it has decided so far.
    /// </summary>
    private sealed class PendingExportReconciliationRun
    {
        public required Dictionary<Guid, PendingExport> PendingExportsByCsoId { get; init; }

        public required bool DatabaseFoldsCaseLikeIdentifierKey { get; init; }

        public required int PageSize { get; init; }

        /// <summary>
        /// Objects with a Pending Export that the save phase holds more than once: a Delta Import can update an object and
        /// then be told it was deleted under another Object Type (#2049), on the same page or a later one. Each is reconciled
        /// once, reloaded after every batch is saved, as all objects were before #2908.
        /// </summary>
        public required IReadOnlySet<Guid> DeferredIds { get; init; }

        /// <summary>
        /// For each deferred object, the first execution item from the import that names it, which its outcomes merge onto.
        /// </summary>
        public Dictionary<Guid, ActivityRunProfileExecutionItem> DeferredImportItems { get; } = [];

        /// <summary>
        /// Execution items raised for objects the import did not change (so had none of their own), already written with
        /// their batch, and still to be counted in the Activity's totals with the run's remaining items, as they always
        /// were counted. Empty where the repository records items on the Activity instead (the in-memory one).
        /// </summary>
        public List<ActivityRunProfileExecutionItem> RaisedItemsAwaitingTotals { get; } = [];

        public Dictionary<ReconciliationReloadReason, int> ReloadsByReason { get; } = [];

        public int ReconciledCount;
        public int JudgedFromHeldValuesCount;
        public int ReloadedCount;
        public int BatchCount;
        public int TotalConfirmed;
        public int TotalRetry;
        public int TotalHeld;
        public int TotalHeldForExchange;
        public int TotalReleased;
        public int ExportsDeleted;
    }

    /// <summary>
    /// What an object reconciliation may judge from memory held immediately before its batch was saved: its values, and the
    /// additions and removals the save was about to write.
    /// </summary>
    internal sealed record ValuesBeforeSave(
        IReadOnlyList<ConnectedSystemObjectAttributeValue> Held,
        IReadOnlyList<ConnectedSystemObjectAttributeValue> Additions,
        IReadOnlyList<ConnectedSystemObjectAttributeValue> Removals);

    /// <summary>
    /// Readies reconciliation for the run: the Connected System's Pending Exports (pre-loaded during the import where it
    /// could be), whether the database folds case as JIM does, and the page size. Null when there is nothing to reconcile.
    /// </summary>
    private async Task<PendingExportReconciliationRun?> BeginPendingExportReconciliationAsync(
        IReadOnlyList<ConnectedSystemObject> updatedCsos, Task<Dictionary<Guid, PendingExport>>? preLoadedPendingExportsTask)
    {
        var updatedCount = updatedCsos.Count;
        if (updatedCount == 0)
            return null;

        // Use pre-loaded Pending Exports if available (loaded in parallel during import processing
        // on a separate DbContext). Otherwise, fall back to loading them now.
        Dictionary<Guid, PendingExport> allPendingExportsByCsoId;
        if (preLoadedPendingExportsTask != null)
        {
            using (Diagnostics.Sync.StartSpan("AwaitPreLoadedPendingExports"))
            {
                allPendingExportsByCsoId = await preLoadedPendingExportsTask;
            }
            Log.Information("BeginPendingExportReconciliationAsync: Using pre-loaded Pending Exports ({Count} loaded in background)",
                allPendingExportsByCsoId.Count);
        }
        else
        {
            using (Diagnostics.Sync.StartSpan("BulkLoadPendingExports"))
            {
                allPendingExportsByCsoId = await _syncRepo.GetPendingExportsLightweightByConnectedSystemIdAsync(_connectedSystem.Id);
            }
        }

        if (allPendingExportsByCsoId.Count == 0)
        {
            Log.Debug("BeginPendingExportReconciliationAsync: No Pending Exports found for Connected System {ConnectedSystemId}. Skipping reconciliation",
                _connectedSystem.Id);
            return null;
        }

        // Once per run (#2669): reconciliation asks the database to match references ignoring case only when its lower()
        // folds case as JIM's identifier comparison does; otherwise those references are loaded and compared in .NET. The
        // same plan is followed for objects judged from memory, so their verdicts and recorded values are a probe's.
        var databaseFoldsCaseLikeIdentifierKey = await _syncRepo.DoesDatabaseFoldCaseLikeIdentifierKeyAsync();
        if (!databaseFoldsCaseLikeIdentifierKey)
            Log.Warning("BeginPendingExportReconciliationAsync: The database's lower() does not fold case as JIM does; references compared " +
                "ignoring case are loaded in full for reconciliation rather than probed");

        var pageSize = await _syncServer.GetSyncPageSizeAsync();
        Log.Information("BeginPendingExportReconciliationAsync: {PendingExportCount} Pending Exports to reconcile against {UpdatedCount} updated " +
            "Connected System Objects, as each update batch is saved; objects reloaded from the database are read in pages of {PageSize}",
            allPendingExportsByCsoId.Count, updatedCount, pageSize);

        var deferredIds = updatedCsos
            .Where(cso => allPendingExportsByCsoId.ContainsKey(cso.Id))
            .GroupBy(cso => cso.Id)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        if (deferredIds.Count > 0)
            Log.Information("BeginPendingExportReconciliationAsync: {Count} Connected System Object(s) with Pending Exports are saved more than once " +
                "this run (updated, then reported deleted under another Object Type); each is reconciled once, after the save phase", deferredIds.Count);

        return new PendingExportReconciliationRun
        {
            DeferredIds = deferredIds,
            PendingExportsByCsoId = allPendingExportsByCsoId,
            DatabaseFoldsCaseLikeIdentifierKey = databaseFoldsCaseLikeIdentifierKey,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Takes, for each object of the batch with a Pending Export that this run's hydration loaded whole, what it holds and
    /// what the save is about to write, so <see cref="WhyNotHeldAsSaved"/> can show afterwards that it holds exactly what
    /// was saved. Taken immediately before the save, which empties the pending lists.
    /// </summary>
    private Dictionary<ConnectedSystemObject, ValuesBeforeSave> CaptureValuesBeforeSave(PendingExportReconciliationRun run,
        IEnumerable<ConnectedSystemObject> csoBatch) =>
        csoBatch
            .Where(cso => run.PendingExportsByCsoId.ContainsKey(cso.Id) && !run.DeferredIds.Contains(cso.Id) &&
                          _connectedSystemObjectsLoadedWhole.Contains(cso) && !cso.IsPartiallyLoaded)
            .ToDictionary(cso => cso,
                cso => new ValuesBeforeSave([.. cso.AttributeValues], [.. cso.PendingAttributeValueAdditions], [.. cso.PendingAttributeValueRemovals]),
                (IEqualityComparer<ConnectedSystemObject>)ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Why an object's values in memory cannot stand in for the database's once its batch is saved, or null when they can.
    /// They can when the object was loaded whole by this run and compared in no other way (<paramref name="beforeSave"/> was
    /// taken), and after the save it holds exactly what the save wrote: the values it held, less those removed by id, plus
    /// those added. The database then holds the same, because nothing else in the run writes an updated object's values,
    /// with one exception: the cross-batch reference fix-up, which resolves outstanding references after the creates and
    /// before the updates, so an object that held one is reloaded whenever the fix-up resolved anything.
    /// </summary>
    /// <param name="cso">The object, after its batch was saved.</param>
    /// <param name="beforeSave">What it held immediately before the save, or null when it was not loaded whole by this run.</param>
    /// <param name="referencesResolvedSinceHydration">Whether the cross-batch reference fix-up resolved any value this run.</param>
    internal static ReconciliationReloadReason? WhyNotHeldAsSaved(ConnectedSystemObject cso, ValuesBeforeSave? beforeSave,
        bool referencesResolvedSinceHydration)
    {
        if (beforeSave == null)
            return ReconciliationReloadReason.NotLoadedWholeByThisRun;

        if (cso.IsPartiallyLoaded)
            return ReconciliationReloadReason.LoadedInPart;

        // The database removes by id, and an addition removed again is written and then not taken back (the writer deletes
        // before it inserts), where memory drops it: neither can be shown equal, so either is reloaded.
        var additions = new HashSet<ConnectedSystemObjectAttributeValue>(beforeSave.Additions, ReferenceEqualityComparer.Instance);
        if (beforeSave.Removals.Any(removal => removal.Id == Guid.Empty || additions.Contains(removal)))
            return ReconciliationReloadReason.RemovesAValueNotSaved;

        if (referencesResolvedSinceHydration &&
            beforeSave.Held.Any(value => value.ReferenceValueId == null && value.UnresolvedReferenceValue != null))
            return ReconciliationReloadReason.HeldAnOutstandingReference;

        var removedIds = beforeSave.Removals.Select(removal => removal.Id).ToHashSet();
        var expected = new HashSet<ConnectedSystemObjectAttributeValue>(ReferenceEqualityComparer.Instance);
        expected.UnionWith(beforeSave.Held.Where(value => !removedIds.Contains(value.Id)));
        expected.UnionWith(beforeSave.Additions);
        if (cso.AttributeValues.Count != expected.Count || !cso.AttributeValues.All(expected.Contains))
            return ReconciliationReloadReason.ChangesNotAppliedAsSaved;

        // Reconciliation groups an object's values by attribute id, which the database always has.
        if (cso.AttributeValues.Any(value => value.AttributeId == 0))
            return ReconciliationReloadReason.ValueWithoutAttributeId;

        return null;
    }

    /// <summary>
    /// Reconciles the Pending Exports of one just-saved update batch: confirms the exported changes its objects now hold,
    /// marks the rest for retry, deletes or updates each Pending Export accordingly, merges the outcomes onto the batch's
    /// execution items and writes them. Objects whose held values are exactly what was saved are judged from them; any
    /// other is reloaded from the database, in pages of the sync page size, as all of them used to be.
    /// </summary>
    private async Task ReconcileSavedBatchAsync(PendingExportReconciliationRun run, List<ConnectedSystemObject> csoBatch,
        IReadOnlyDictionary<ConnectedSystemObject, ValuesBeforeSave> valuesBeforeSave,
        Dictionary<Guid, ActivityRunProfileExecutionItem> importItemsByCsoId, bool referencesResolvedSinceHydration)
    {
        // An object saved more than once is reconciled once, after the save phase (#2908), against its first item.
        foreach (var (csoId, item) in importItemsByCsoId.Where(pair => run.DeferredIds.Contains(pair.Key)))
            run.DeferredImportItems.TryAdd(csoId, item);

        var withExports = csoBatch.Where(cso => run.PendingExportsByCsoId.ContainsKey(cso.Id) && !run.DeferredIds.Contains(cso.Id)).ToList();
        if (withExports.Count == 0)
            return;

        using var batchSpan = Diagnostics.Sync.StartSpan("ReconcilePendingExports").SetTag("csoCount", withExports.Count);

        var heldAsSaved = new List<ConnectedSystemObject>(withExports.Count);
        var toReload = new List<Guid>();
        foreach (var cso in withExports)
        {
            var reason = WhyNotHeldAsSaved(cso, valuesBeforeSave.GetValueOrDefault(cso), referencesResolvedSinceHydration);
            if (reason == null)
            {
                heldAsSaved.Add(cso);
                continue;
            }

            toReload.Add(cso.Id);
            run.ReloadsByReason[reason.Value] = run.ReloadsByReason.GetValueOrDefault(reason.Value) + 1;
        }

        batchSpan.SetTag("heldAsSavedCount", heldAsSaved.Count);
        batchSpan.SetTag("reloadedCount", toReload.Count);
        await ReconcileObjectsAsync(run, heldAsSaved, toReload, importItemsByCsoId);
        batchSpan.SetSuccess();
    }

    /// <summary>
    /// Reconciles the objects saved more than once this run (<see cref="PendingExportReconciliationRun.DeferredIds"/>), once
    /// each, after every batch is saved: reloaded, as every object was reconciled before #2908.
    /// </summary>
    private async Task ReconcileDeferredAsync(PendingExportReconciliationRun run)
    {
        if (run.DeferredIds.Count == 0)
            return;

        using var span = Diagnostics.Sync.StartSpan("ReconcilePendingExports").SetTag("csoCount", run.DeferredIds.Count).SetTag("deferred", true);
        run.ReloadsByReason[ReconciliationReloadReason.SavedMoreThanOnce] = run.DeferredIds.Count;
        await ReconcileObjectsAsync(run, [], [.. run.DeferredIds], run.DeferredImportItems);
        span.SetSuccess();
    }

    /// <summary>
    /// Judges each object's Pending Export, those held as saved from memory and the others reloaded, and writes the outcome:
    /// first the execution items (those reconciliation raises, and the outcomes it merges onto the import's), then the
    /// Pending Export changes. Written in that order, a failure part way can leave a recorded outcome whose Pending Export
    /// the next import judges again, never a Pending Export removed with nothing recording why.
    /// </summary>
    private async Task ReconcileObjectsAsync(PendingExportReconciliationRun run, List<ConnectedSystemObject> heldAsSaved, List<Guid> toReload,
        Dictionary<Guid, ActivityRunProfileExecutionItem> importItemsByCsoId)
    {
        var isFirstBatch = run.BatchCount++ == 0;
        run.JudgedFromHeldValuesCount += heldAsSaved.Count;
        run.ReloadedCount += toReload.Count;

        var csosToJudge = new List<ConnectedSystemObject>(heldAsSaved.Count + toReload.Count);
        var probedValuesByCsoId = new Dictionary<Guid, Dictionary<int, ProbedAttributeValues>>();

        // Judged from memory. The probe plan is the one a reload would follow, and its questions are answered from the values
        // held, so every verdict and every recorded imported value is the probe's (#2669) without asking the database.
        if (heldAsSaved.Count > 0)
        {
            using var heldSpan = Diagnostics.Sync.StartSpan("ReconcileHeldValues").SetTag("csoCount", heldAsSaved.Count);
            var plan = SyncEngine.PlanReconciliationProbes(heldAsSaved.Select(cso => run.PendingExportsByCsoId[cso.Id]),
                run.DatabaseFoldsCaseLikeIdentifierKey);
            var answers = plan.Questions.Count == 0
                ? []
                : SyncEngine.AnswerReconciliationProbes(plan.Questions,
                    heldAsSaved.ToDictionary(cso => cso.Id, cso => (IReadOnlyList<ConnectedSystemObjectAttributeValue>)cso.AttributeValues));
            IndexProbeAnswers(plan.Questions, answers, probedValuesByCsoId);
            heldSpan.SetTag("probedPairs", plan.Questions.Count);
            heldSpan.SetTag("fullLoadPairs", plan.FullLoadPairCount);
            heldSpan.SetSuccess();
            csosToJudge.AddRange(heldAsSaved);
        }

        // Reloaded, exactly as every object was before #2908.
        foreach (var pageCsoIds in toReload.Chunk(run.PageSize))
            csosToJudge.AddRange(await ReloadForReconciliationAsync(run, pageCsoIds, probedValuesByCsoId));

        // Which of the batch's accounts are still owed a password, read only when it holds a change written with an interim
        // value until an Initial Password is delivered (#2039), and which of its objects still have a change on the Exchange
        // queue, read only when it holds a connector-defined change awaiting Exchange (#2021). Null otherwise, never read.
        IReadOnlySet<Guid>? metaverseObjectIdsOwedPassword;
        IReadOnlySet<Guid>? connectedSystemObjectIdsWithExchangeChange;
        using (var lookupsSpan = Diagnostics.Sync.StartSpan("ReconcilePageLookups"))
        {
            metaverseObjectIdsOwedPassword = await GetMetaverseObjectIdsOwedPasswordForPageAsync(csosToJudge, run.PendingExportsByCsoId);
            connectedSystemObjectIdsWithExchangeChange = await GetConnectedSystemObjectIdsWithExchangeChangeForPageAsync(csosToJudge, run.PendingExportsByCsoId);
            lookupsSpan.SetTag("owedPasswordCount", metaverseObjectIdsOwedPassword?.Count ?? 0);
            lookupsSpan.SetTag("exchangeChangeCount", connectedSystemObjectIdsWithExchangeChange?.Count ?? 0);
            lookupsSpan.SetSuccess();
        }

        // Thread-safe collections for the batch's deferred writes.
        var pendingExportsToDelete = new ConcurrentBag<PendingExport>();
        var pendingExportsToUpdate = new ConcurrentBag<PendingExport>();
        var confirmedAttrChangesToDelete = new ConcurrentBag<PendingExportAttributeValueChange>();
        var raisedItems = new ConcurrentBag<ActivityRunProfileExecutionItem>();
        var modifiedImportItems = new ConcurrentDictionary<Guid, ActivityRunProfileExecutionItem>();
        var probeFailures = new ConcurrentQueue<ReconciliationProbeIncompleteException>();
        long reconcileTicksTotal = 0;
        long rpeiTicksTotal = 0;

        // Reconciliation is pure in-memory comparison: every Pending Export is loaded, each object has its own result, the
        // engine's reconciliation methods are stateless, and shared collections and counters are thread-safe.
        using (Diagnostics.Sync.StartSpan("ProcessReconciliation").SetTag("csoCount", csosToJudge.Count))
        {
            Parallel.ForEach(csosToJudge, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, cso =>
            {
                try
                {
                    var perCsoSw = isFirstBatch ? Stopwatch.StartNew() : null;
                    run.PendingExportsByCsoId.TryGetValue(cso.Id, out var pendingExport);

                    var result = new PendingExportReconciliationResult();
                    _syncEngine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, metaverseObjectIdsOwedPassword,
                        connectedSystemObjectIdsWithExchangeChange, probedValuesByCsoId.GetValueOrDefault(cso.Id));

                    if (isFirstBatch)
                    {
                        Interlocked.Add(ref reconcileTicksTotal, perCsoSw!.ElapsedTicks);
                        perCsoSw.Restart();
                    }

                    RecordReconciliationResult(run, cso, pendingExport, result, importItemsByCsoId, pendingExportsToDelete,
                        pendingExportsToUpdate, confirmedAttrChangesToDelete, raisedItems, modifiedImportItems);

                    if (isFirstBatch && result.HasChanges)
                        Interlocked.Add(ref rpeiTicksTotal, perCsoSw!.ElapsedTicks);

                    Interlocked.Increment(ref run.ReconciledCount);
                }
                catch (ReconciliationProbeIncompleteException ex)
                {
                    // A defect in planning or answering the probe, never a state of the data (#2669): collected and rethrown
                    // below, before anything of this batch is persisted, so the Activity fails rather than leave a change unjudged.
                    probeFailures.Enqueue(ex);
                    Interlocked.Increment(ref run.ReconciledCount);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "ReconcileSavedBatchAsync: Error reconciling Pending Exports for CSO {CsoId}", cso.Id);
                    Interlocked.Increment(ref run.ReconciledCount);
                }
            });

            if (probeFailures.TryPeek(out var firstProbeFailure))
            {
                Log.Error(firstProbeFailure, "ReconcileSavedBatchAsync: {Count} Connected System Object(s) in update batch {Batch} had a change their " +
                    "probed values could not answer; failing the import", probeFailures.Count, run.BatchCount);
                ExceptionDispatchInfo.Capture(firstProbeFailure).Throw();
            }

            if (isFirstBatch)
            {
                var tickFreq = (double)Stopwatch.Frequency;
                Log.Information("ReconcileSavedBatchAsync: First batch timing breakdown: Reconcile: {ReconcileMs:F1}ms, RPEI/Outcome: {RpeiMs:F1}ms " +
                    "(thread-aggregated across {CsoCount} CSOs)", reconcileTicksTotal / tickFreq * 1000.0, rpeiTicksTotal / tickFreq * 1000.0,
                    csosToJudge.Count);
            }
        }

        // The execution items first, so a Pending Export is only ever removed once what confirmed it is recorded (#2908).
        // Items raised here are written now, not with the run's last items: a later batch can still fail the run. They are
        // counted in the Activity's totals with the last items, as before, so the totals do not depend on the batches.
        var raised = raisedItems.ToList();
        if (raised.Count > 0)
        {
            var written = raised.ToList();
            if (await FlushImportRpeisAsync(raised, countInActivityTotals: false))
                run.RaisedItemsAwaitingTotals.AddRange(written);
        }

        await PersistMergedReconciliationOutcomesAsync(modifiedImportItems.Values.ToList());
        await FlushPendingExportChangesAsync(pendingExportsToDelete, pendingExportsToUpdate, confirmedAttrChangesToDelete);
    }

    /// <summary>
    /// Reloads one page of objects reconciliation cannot judge from memory, probing rather than loading the values of large
    /// multi-valued references (#2669), and adds the probe answers to <paramref name="probedValuesByCsoId"/>.
    /// </summary>
    private async Task<List<ConnectedSystemObject>> ReloadForReconciliationAsync(PendingExportReconciliationRun run,
        IReadOnlyCollection<Guid> pageCsoIds, Dictionary<Guid, Dictionary<int, ProbedAttributeValues>> probedValuesByCsoId)
    {
        // Probe rather than load the values of large multi-valued references (#2669): a group's member attribute would
        // otherwise bring every member's value for a handful of changes (761,000 and 1.73 million values on two pages
        // of a 500,000-user, 25,000-group run). The planner decides which attributes of which objects can be asked
        // about exactly; the database returns the rows that could match each change and, where a verdict depends on
        // it, the value count capped at two; the engine judges them with the same comparison it applies to a full load.
        // What is probed is left out of the load below, decided from the answers so the two can never disagree.
        var probePlan = SyncEngine.PlanReconciliationProbes(
            pageCsoIds.Select(id => run.PendingExportsByCsoId.GetValueOrDefault(id)).OfType<PendingExport>(),
            run.DatabaseFoldsCaseLikeIdentifierKey);
        using (var probeSpan = Diagnostics.Sync.StartSpan("ReconcileProbeValues"))
        {
            var answers = probePlan.Questions.Count == 0
                ? []
                : await _syncRepo.GetReconciliationProbeValuesAsync(probePlan.Questions);
            IndexProbeAnswers(probePlan.Questions, answers, probedValuesByCsoId);

            probeSpan.SetTag("probedPairs", probePlan.Questions.Count);
            probeSpan.SetTag("fullLoadPairs", probePlan.FullLoadPairCount);
            probeSpan.SetTag("tuples", probePlan.Questions.Sum(q => q.TupleCount));
            probeSpan.SetTag("candidateRows", answers.Sum(a => a.CandidateValues.Count));
            probeSpan.SetSuccess();

            // Why a page's Pending Exports took the full load (#2809): once per page that loaded any pair in full, so
            // a run's log says why its reconciliation read whole groups, without a line per object.
            if (probePlan.FullLoadPairCount > 0)
            {
                Log.Information("ReconcileSavedBatchAsync: A page of {PageCount} reloaded object(s): {FullLoadPairs} multi-valued reference " +
                    "attribute(s) loaded in full rather than probed ({Reasons}); {ProbedPairs} probed with {Tuples} tuple(s), " +
                    "{CandidateRows} candidate row(s)",
                    pageCsoIds.Count, probePlan.FullLoadPairCount,
                    string.Join(", ", probePlan.FullLoadPairCountsByReason.OrderBy(r => r.Key).Select(r => $"{r.Key}={r.Value}")),
                    probePlan.Questions.Count, probePlan.Questions.Sum(q => q.TupleCount), answers.Sum(a => a.CandidateValues.Count));
            }
        }

        // The Core load leaves out the objects a reference names (#2669): reconciliation compares a reference by its id and
        // string only, and a group would otherwise bring every member's object with it. Objects are loaded in groups sharing
        // the attributes left out of them: usually one group with the member attribute left out and one with nothing left out.
        var pageCsos = new List<ConnectedSystemObject>(pageCsoIds.Count);
        using (var loadSpan = Diagnostics.Sync.StartSpan("ReconcileLoadPageCsos"))
        {
            var loadGroups = pageCsoIds
                .GroupBy(id => probedValuesByCsoId.TryGetValue(id, out var byAttributeId)
                    ? string.Join(",", byAttributeId.Keys.Order())
                    : string.Empty)
                .ToList();
            foreach (var loadGroup in loadGroups)
            {
                var groupIds = loadGroup.ToList();
                var excludedAttributeIds = loadGroup.Key.Length == 0 ? null : probedValuesByCsoId[groupIds[0]].Keys.ToList();
                pageCsos.AddRange(await _syncRepo.GetConnectedSystemObjectsCoreByIdsNoTrackingAsync(
                    _connectedSystem.Id, groupIds, excludedAttributeIds));
            }

            // The Core load leaves out the Object Type; an execution item reconciliation raises names it, as every
            // other import item does, so it is attached from the Connected System's own types (#2908).
            AttachObjectTypes(pageCsos);

            loadSpan.SetTag("loadGroupCount", loadGroups.Count);
            loadSpan.SetTag("csoCount", pageCsos.Count);
            loadSpan.SetTag("attributeValueCount", pageCsos.Sum(cso => cso.AttributeValues.Count));
            loadSpan.SetSuccess();
        }

        return pageCsos;
    }

    /// <summary>
    /// Files probe answers by object and attribute, failing on an attribute answered twice or a question left unanswered:
    /// either is a defect in asking, and a change judged from an incomplete answer could be confirmed wrongly.
    /// </summary>
    private static void IndexProbeAnswers(IReadOnlyCollection<ReconciliationProbeQuestion> questions, IEnumerable<ProbedAttributeValues> answers,
        Dictionary<Guid, Dictionary<int, ProbedAttributeValues>> probedValuesByCsoId)
    {
        foreach (var answer in answers)
        {
            if (!probedValuesByCsoId.TryGetValue(answer.ConnectedSystemObjectId, out var byAttributeId))
            {
                byAttributeId = new Dictionary<int, ProbedAttributeValues>();
                probedValuesByCsoId[answer.ConnectedSystemObjectId] = byAttributeId;
            }

            if (!byAttributeId.TryAdd(answer.AttributeId, answer))
                throw new ReconciliationProbeIncompleteException(
                    $"The reconciliation probe answered attribute {answer.AttributeId} of Connected System Object {answer.ConnectedSystemObjectId} twice.");
        }

        var unanswered = questions.FirstOrDefault(q =>
            !probedValuesByCsoId.TryGetValue(q.ConnectedSystemObjectId, out var byAttributeId) || !byAttributeId.ContainsKey(q.AttributeId));
        if (unanswered != null)
            throw new ReconciliationProbeIncompleteException(
                $"The reconciliation probe returned no answer for attribute {unanswered.AttributeId} of Connected System Object {unanswered.ConnectedSystemObjectId}.");
    }

    /// <summary>
    /// Records what reconciliation decided for one object: counts it, collects its Pending Export for deletion or update,
    /// and merges an ExportConfirmed or ExportFailed outcome onto the object's execution item from the import, or raises
    /// an item of its own where the import changed nothing about the object (one item per object). Thread-safe.
    /// </summary>
    private void RecordReconciliationResult(PendingExportReconciliationRun run, ConnectedSystemObject cso, PendingExport? pendingExport,
        PendingExportReconciliationResult result, Dictionary<Guid, ActivityRunProfileExecutionItem> importItemsByCsoId,
        ConcurrentBag<PendingExport> pendingExportsToDelete, ConcurrentBag<PendingExport> pendingExportsToUpdate,
        ConcurrentBag<PendingExportAttributeValueChange> confirmedAttrChangesToDelete, ConcurrentBag<ActivityRunProfileExecutionItem> raisedItems,
        ConcurrentDictionary<Guid, ActivityRunProfileExecutionItem> modifiedImportItems)
    {
        // A held change changes nothing and raises no item (#2039); it is counted for the summary only.
        Interlocked.Add(ref run.TotalHeld, result.HeldChanges.Count);
        // Likewise a change Exchange still owes (#2021).
        Interlocked.Add(ref run.TotalHeldForExchange, result.HeldForExchangeChanges.Count);

        if (!result.HasChanges)
            return;

        Interlocked.Add(ref run.TotalConfirmed, result.ConfirmedChanges.Count);
        Interlocked.Add(ref run.TotalRetry, result.RetryChanges.Count);
        // A release is persisted with the Pending Export below, but raises no item: the interim value was written on
        // purpose, so it is not an unconfirmed export (#2039).
        Interlocked.Add(ref run.TotalReleased, result.ReleasedChanges.Count);

        if (result.PendingExportToDelete != null)
        {
            pendingExportsToDelete.Add(result.PendingExportToDelete);
            Interlocked.Increment(ref run.ExportsDeleted);
        }
        else if (result.PendingExportToUpdate != null)
        {
            pendingExportsToUpdate.Add(result.PendingExportToUpdate);

            // Loaded untracked, so EF Core detects no collection removals: confirmed changes are deleted explicitly.
            foreach (var confirmedChange in result.ConfirmedChanges)
                confirmedAttrChangesToDelete.Add(confirmedChange);
        }

        // Outcomes merge onto the object's execution item from the import where it has one (one item per object);
        // otherwise (a confirming import that changed nothing about the object) reconciliation raises one.
        importItemsByCsoId.TryGetValue(cso.Id, out var existingRpei);

        if (result.ConfirmedChanges.Count > 0)
        {
            if (existingRpei != null)
            {
                ActivityRunProfileExecutionItemSyncOutcome? confirmedOutcome = null;
                if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                {
                    confirmedOutcome = SyncOutcomeBuilder.AddRootOutcome(existingRpei,
                        ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed,
                        detailCount: result.ConfirmedChanges.Count);
                }
                RecordExportConfirmationCause(existingRpei, confirmedOutcome, pendingExport, cso);
                modifiedImportItems.TryAdd(existingRpei.Id, existingRpei);
            }
            else
            {
                var executionItem = new ActivityRunProfileExecutionItem
                {
                    Activity = _activity,
                    ConnectedSystemObject = cso,
                    ConnectedSystemObjectId = cso.Id,
                    ObjectChangeType = ObjectChangeType.Updated
                };
                executionItem.SnapshotCsoDisplayFields(cso);

                ActivityRunProfileExecutionItemSyncOutcome? confirmedOutcome = null;
                if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                {
                    confirmedOutcome = SyncOutcomeBuilder.AddRootOutcome(executionItem,
                        ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed,
                        detailCount: result.ConfirmedChanges.Count);
                }
                RecordExportConfirmationCause(executionItem, confirmedOutcome, pendingExport, cso);

                raisedItems.Add(executionItem);
            }
        }

        // Changes not confirmed: reported at every import that misses them (#2119).
        if (result.RetryChanges.Count == 0)
            return;

        var retryAttrNames = string.Join(", ", result.RetryChanges
            .GroupBy(c => c.Attribute?.Name ?? "unknown")
            .Select(g => g.Count() > 1 ? $"{g.Key} (x{g.Count()})" : g.Key));
        var errorMessage = $"We exported a change, but did not get confirmation of it when a confirming import was performed. Details: {result.RetryChanges.Count} attribute(s): {retryAttrNames}. Will attempt to reassert the change on the next export run.";

        if (existingRpei != null)
        {
            if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
            {
                SyncOutcomeBuilder.AddRootOutcome(existingRpei,
                    ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed,
                    detailCount: result.RetryChanges.Count);
            }
            // Set error fields only if the existing RPEI doesn't already have an error
            if (existingRpei.ErrorType is null or ActivityRunProfileExecutionItemErrorType.NotSet)
            {
                existingRpei.ErrorType = ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed;
                existingRpei.ErrorMessage = errorMessage;
            }
            modifiedImportItems.TryAdd(existingRpei.Id, existingRpei);
        }
        else
        {
            var executionItem = new ActivityRunProfileExecutionItem
            {
                Activity = _activity,
                ConnectedSystemObject = cso,
                ConnectedSystemObjectId = cso.Id,
                ObjectChangeType = ObjectChangeType.Updated,
                ErrorType = ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed,
                ErrorMessage = errorMessage
            };
            executionItem.SnapshotCsoDisplayFields(cso);

            if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
            {
                SyncOutcomeBuilder.AddRootOutcome(executionItem,
                    ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed,
                    detailCount: result.RetryChanges.Count);
            }

            raisedItems.Add(executionItem);
        }
    }

    /// <summary>
    /// Writes one batch's Pending Export changes: the deletions, the confirmed changes taken off the Pending Exports kept,
    /// and the Pending Exports kept. Uses the id-based, tracker-aware methods, since the Pending Exports were loaded
    /// untracked.
    /// </summary>
    private async Task FlushPendingExportChangesAsync(IReadOnlyCollection<PendingExport> pendingExportsToDelete,
        IReadOnlyCollection<PendingExport> pendingExportsToUpdate, IReadOnlyCollection<PendingExportAttributeValueChange> confirmedAttrChangesToDelete)
    {
        using var span = Diagnostics.Sync.StartSpan("FlushPendingExportChanges")
            .SetTag("deleteCount", pendingExportsToDelete.Count)
            .SetTag("updateCount", pendingExportsToUpdate.Count)
            .SetTag("confirmedAttrChangeDeleteCount", confirmedAttrChangesToDelete.Count);

        if (pendingExportsToDelete.Count > 0)
            await _syncRepo.DeleteUntrackedPendingExportsAsync(pendingExportsToDelete);

        // Delete confirmed attribute changes before updating (AsNoTracking requires explicit child deletion)
        if (confirmedAttrChangesToDelete.Count > 0)
            await _syncRepo.DeleteUntrackedPendingExportAttributeValueChangesAsync(confirmedAttrChangesToDelete);

        if (pendingExportsToUpdate.Count > 0)
            await _syncRepo.UpdateUntrackedPendingExportsAsync(pendingExportsToUpdate);

        span.SetSuccess();
    }

    /// <summary>
    /// Writes the outcomes reconciliation merged onto one batch's execution items from the import, which were written when
    /// the batch was saved: their rebuilt outcome summaries and error fields, the new outcomes, and the causal edges. One
    /// write per batch (#2908), so the outcomes never accumulate across the run: written once at the end, the 524,997 of a
    /// 500,000-object confirming import took 26 s.
    /// </summary>
    private async Task PersistMergedReconciliationOutcomesAsync(List<ActivityRunProfileExecutionItem> modifiedImportItems)
    {
        if (modifiedImportItems.Count == 0)
            return;

        if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
        {
            foreach (var rpei in modifiedImportItems)
                SyncOutcomeBuilder.BuildOutcomeSummary(rpei);
        }

        // Only the outcomes reconciliation added: those already written carry the ids the flush gave them.
        var newOutcomes = new List<ActivityRunProfileExecutionItemSyncOutcome>();
        foreach (var rpei in modifiedImportItems)
        {
            foreach (var outcome in rpei.SyncOutcomes.Where(o => o.ParentSyncOutcome == null && o.ParentSyncOutcomeId == null && o.Id == Guid.Empty))
            {
                outcome.Id = ChangeRecordIds.New();
                outcome.ActivityRunProfileExecutionItemId = rpei.Id;
                newOutcomes.Add(outcome);
            }
        }

        using (var persistSpan = Diagnostics.Sync.StartSpan("ReconcilePersistRpeiOutcomes")
            .SetTag("rpeiCount", modifiedImportItems.Count)
            .SetTag("outcomeCount", newOutcomes.Count))
        {
            await _syncRepo.BulkUpdateRpeiOutcomesAsync(modifiedImportItems, newOutcomes);
            persistSpan.SetSuccess();
        }

        Log.Debug("PersistMergedReconciliationOutcomesAsync: Updated {RpeiCount} existing RPEIs with {OutcomeCount} new reconciliation outcomes",
            modifiedImportItems.Count, newOutcomes.Count);
    }

    /// <summary>
    /// Closes the run's reconciliation: the Activity records how many Pending Exports were confirmed, and the summary is
    /// logged. The items it raised were written with their batches; they are counted in the Activity's totals with the
    /// run's last items.
    /// </summary>
    private void CompletePendingExportReconciliation(PendingExportReconciliationRun run)
    {
        _activity.PendingExportsConfirmed = run.ExportsDeleted;

        Log.Information("CompletePendingExportReconciliation: Reconciled {Reconciled} Connected System Objects with Pending Exports over {Batches} " +
            "update batch(es): {Held} judged from the values the import held as saved, {Reloaded} reloaded ({ReloadReasons}). Confirmed: {Confirmed}, " +
            "Retry: {Retry}, Held awaiting Initial Password: {HeldForPassword}, Released (no Initial Password owed): {Released}, " +
            "Held awaiting Exchange: {HeldForExchange}, Exports deleted: {Deleted}",
            run.ReconciledCount, run.BatchCount, run.JudgedFromHeldValuesCount, run.ReloadedCount,
            run.ReloadsByReason.Count == 0 ? "none" : string.Join(", ", run.ReloadsByReason.OrderBy(r => r.Key).Select(r => $"{r.Key}={r.Value}")),
            run.TotalConfirmed, run.TotalRetry, run.TotalHeld, run.TotalReleased, run.TotalHeldForExchange, run.ExportsDeleted);
    }

    /// <summary>
    /// Records which export execution a confirmation confirms (#1223).
    /// </summary>
    /// <remarks>
    /// This hop cannot be reconstructed afterwards, which is why it earns an edge where the other same-item
    /// hops do not. Reconciliation pairs a Pending Export with an imported object by Connected System Object id
    /// alone, and an object cycles through export and import repeatedly, so a later attempt to pair a
    /// confirmation with its export by object id can land on the wrong cycle and report a confirmation against
    /// an export that did not produce it. The Pending Export row IS the cycle, and this reconciliation is about
    /// to delete it, so the link is recorded here or not at all.
    ///
    /// No reason code: the effect outcome already distinguishes confirmed from failed, and cohorts are computed
    /// per effect, so a code here would add nothing to group on.
    /// </remarks>
    private void RecordExportConfirmationCause(
        ActivityRunProfileExecutionItem item,
        ActivityRunProfileExecutionItemSyncOutcome? confirmedOutcome,
        PendingExport? pendingExport,
        ConnectedSystemObject cso)
    {
        if (pendingExport == null)
            return;

        item.CausalEdges.Add(new CausalCause
        {
            PendingExportId = pendingExport.Id,
            ConnectedSystemObjectId = cso.Id,
            // Named from the object being confirmed, so the chain still reads once the Connected System Object
            // is gone; the Pending Export itself is deleted moments from now and can never supply a name.
            DisplayName = cso.Name,
            ConnectedSystemId = _connectedSystem.Id,
            ConnectedSystemName = _connectedSystem.Name
        }.ToEdge(CausalEdgeType.ExportCausedImportConfirmation, confirmedOutcome));
    }

    /// <summary>
    /// Gives each object loaded without its Object Type the Connected System's own instance of it, so an execution item
    /// raised for it snapshots the type's name (#2908). An object whose type the Connected System does not hold is left as
    /// it is.
    /// </summary>
    private void AttachObjectTypes(IEnumerable<ConnectedSystemObject> connectedSystemObjects)
    {
        var objectTypesById = _connectedSystem.ObjectTypes?.ToDictionary(type => type.Id);
        if (objectTypesById == null)
            return;

        foreach (var cso in connectedSystemObjects.Where(cso => cso.Type == null && objectTypesById.ContainsKey(cso.TypeId)))
            cso.Type = objectTypesById[cso.TypeId];
    }
}
