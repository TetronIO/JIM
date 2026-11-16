// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Diagnostics;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Utility;
using JIM.Utilities;
using Serilog;

namespace JIM.Worker.Processors;

/// <summary>
/// A Full Import saves what it reads a unit at a time (#2521, Phase 5), so its peak memory follows the unit, not the
/// Connected System. Pages accumulate into a unit until it holds <see cref="ImportBatchSize"/> objects or
/// <see cref="ImportUnitValueBudget"/> values; a larger page is split. Each unit is matched and compared as every page
/// always was, has its references resolved against itself and then against what is saved, and is saved, stamped and
/// reconciled exactly as the single save phase saved its batches; its values are then released. A reference whose object
/// the run has not saved yet (a later unit, or an Object Type change's replacement, #2049) is saved by its identifier and
/// noted; once the last unit, deletion detection and the replacements are saved, the sweep resolves what it can of the
/// noted references by the same rules, and reports what is left on the execution items already saved.
/// <para>
/// A run that stops part-way keeps the units it saved. The Connected System's import-incomplete marker, set before the
/// first unit is saved, holds synchronisation back until a Full Import completes. Delta Import still saves once, at the
/// end.
/// </para>
/// </summary>
public partial class SyncImportTaskProcessor
{
    /// <summary>
    /// The values one unit may hold, unless a single object holds more (#2521, decision 8): a constant, tuned from the
    /// measurements, rather than a Service Setting. At about 240 bytes a hydrated value, a unit of 250,000 values holds
    /// about 60 MB of them; a unit of people (2,000 objects of a dozen values) is far below it.
    /// </summary>
    internal const int DefaultImportUnitValueBudget = 250_000;

    /// <summary>
    /// How many values one unit of a Full Import may hold (<see cref="DefaultImportUnitValueBudget"/>). Settable at
    /// construction only, so a test can make a page of large groups cross units.
    /// </summary>
    internal int ImportUnitValueBudget { get; init; } = DefaultImportUnitValueBudget;

    /// <summary>How many units this run saved (#2521): zero for a Delta Import, which saves once.</summary>
    internal int StagedUnitCount { get; private set; }

    /// <summary>
    /// The most managed heap the run held at a unit boundary, as the last full garbage collection left it, in bytes: the
    /// measurement the lab reads at 500,000 objects to confirm the heap is flat in the Connected System's size (#2521).
    /// Zero until a full collection has run.
    /// </summary>
    internal long PeakUnitBoundaryLiveHeapBytes { get; private set; }

    /// <summary>
    /// How many objects the run saved with a reference to an object it had not saved yet, which the end-of-run sweep
    /// resolves or reports (#2521).
    /// </summary>
    internal int ObjectsWithOutstandingReferences => _outstandingReferenceHolders.Count;

    /// <summary>
    /// How many references the run's units resolved through the database to an object an Object Type change could still
    /// retire, kept for the sweep to recheck (#2049): zero on a run that does not allow Object Type changes.
    /// </summary>
    internal int ReferencesKeptForObjectTypeChangeRecheck { get; private set; }

    /// <summary>Whether this run saves each unit as it goes: a Full Import (#2521). A Delta Import is unchanged.</summary>
    private bool StagesUnits => _connectedSystemRunProfile.RunType == ConnectedSystemRunType.FullImport;

    /// <summary>The objects read into the unit being built, and their values.</summary>
    private int _unitObjectCount;
    private long _unitValueCount;

    /// <summary>
    /// The ids of the objects this run created, by every unit so far: what a later unit's values may name by id when they
    /// are written (<c>PrepareConnectedSystemObjectsForCreate</c>), as the single save phase's committed batches were.
    /// </summary>
    private readonly HashSet<Guid> _csoIdsCreatedThisRun = new();

    /// <summary>How many objects the units created and updated, for the Activity's message and the summary.</summary>
    private int _stagedCreateCount;
    private int _stagedUpdateCount;

    /// <summary>
    /// The run's Pending Export reconciliation, begun by the first unit that updates anything (null when the Connected
    /// System has no Pending Export then), and judged unit by unit.
    /// </summary>
    private PendingExportReconciliationRun? _unitReconciliation;
    private bool _unitReconciliationBegun;

    /// <summary>The objects saved with a reference to an object the run had not saved yet, by id.</summary>
    private readonly Dictionary<Guid, OutstandingReferenceHolder> _outstandingReferenceHolders = new();

    /// <summary>
    /// #2049: lookups over the Object Type change replacements held apart, rebuilt as their count changes, so a unit's
    /// reference to one is noted rather than resolved to the object it replaces; and over the replacements that stood, for
    /// the sweep, which resolves such a reference to its replacement as the whole run's in-memory resolution did.
    /// </summary>
    private ExternalIdLookups? _heldReplacementLookups;
    private int _heldReplacementLookupsBuiltFor = -1;
    private ExternalIdLookups? _standingReplacementLookups;

    /// <summary>
    /// #2049: the standing replacements whose held objects deletion detection newly marked as deleted this run, with those
    /// objects' Object Types. The single save phase resolved references with both in memory, the replacement and the
    /// object it retires under one identifier, so an undeclared reference to either was ambiguous between the two; the
    /// sweep reports it so, as the run's in-memory resolution did.
    /// </summary>
    private readonly Dictionary<ConnectedSystemObject, List<string>> _standingReplacementsBesideRetiredObjects = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// #2049: on a run that allows Object Type changes, the objects a unit's references were resolved to through the
    /// database, each with the objects naming it. A change carried out by a later unit may retire one of them, which the
    /// single save phase would have known when it resolved the reference; the sweep rechecks the references to each object
    /// a standing change retired. Only objects the run had not read when the reference was resolved are kept: one read this
    /// run under its own Object Type is not a deletion candidate, so no change can retire it. Null on any other run.
    /// </summary>
    private Dictionary<Guid, List<Guid>>? _referencesKeptForRecheck;

    /// <summary>
    /// #2049: the objects naming those kept for a recheck that are not otherwise noted for the sweep: what it needs of them,
    /// their stamps held as an outstanding reference's are, so a run that stops before the recheck compares them again.
    /// </summary>
    private Dictionary<Guid, OutstandingReferenceHolder>? _holdersKeptForRecheck;

    /// <summary>#2049: the held objects the standing Object Type changes retired, once deletion detection has decided them.</summary>
    private HashSet<Guid>? _csoIdsRetiredByStandingObjectTypeChanges;

    /// <summary>The run's sync page size, read once: the whole-object database reference lookups are asked in pages of it.</summary>
    private int? _referenceLookupPageSize;

    /// <summary>
    /// How many values one id-only reference lookup asks about (#2521). Larger than the sync page size the whole-object
    /// lookups use, as it returns two columns a value rather than objects with every value: a unit of large groups names
    /// hundreds of thousands of members, all saved by earlier units.
    /// </summary>
    private const int ReferenceIdLookupChunkSize = 5_000;

    /// <summary>
    /// The reference values left unresolved, and those ambiguous across Object Types, over the whole run: reported once,
    /// when resolution is over (<see cref="ReportReferenceResolutionSummary"/>).
    /// </summary>
    private int _unresolvedReferenceTotal;
    private int _ambiguousReferenceTotal;

    /// <summary>
    /// Whether the run's execution items were written by the bulk writer, which counts them into the Activity's totals as
    /// it writes them; false where the repository keeps them on the Activity (the in-memory one), which totals them at the
    /// end.
    /// </summary>
    private bool _itemsCountedAsWritten;

    /// <summary>
    /// One object saved with a reference to an object the run had not saved yet: what the sweep needs of it once its unit
    /// has been released. Its stamp is held back until the sweep has settled its references, so a run that stops before
    /// then leaves it to be compared again rather than skipped by its hash.
    /// </summary>
    private sealed class OutstandingReferenceHolder(Guid csoId, int objectTypeId, Guid? executionItemId,
        ActivityRunProfileExecutionItemErrorType? itemErrorWhenWritten, bool stampRequested, Guid? stampHash, Guid? stampFingerprint,
        int unresolvedCountWhenWritten = 0)
    {
        public Guid CsoId { get; } = csoId;
        public int ObjectTypeId { get; } = objectTypeId;
        public Guid? ExecutionItemId { get; } = executionItemId;
        public ActivityRunProfileExecutionItemErrorType? ItemErrorWhenWritten { get; } = itemErrorWhenWritten;
        public bool StampRequested { get; } = stampRequested;
        public Guid? StampHash { get; } = stampHash;
        public Guid? StampFingerprint { get; } = stampFingerprint;

        /// <summary>
        /// The values its item already reported as unresolved or ambiguous when it was written: those of an object noted only
        /// for an Object Type change recheck (#2049), which the sweep's report adds to rather than replaces.
        /// </summary>
        public int UnresolvedCountWhenWritten { get; } = unresolvedCountWhenWritten;

        /// <summary>What the object was left with: unresolved values, and those ambiguous across Object Types.</summary>
        public List<(string AttributeName, string Value, IReadOnlyList<string>? AmbiguousWith)> LeftUnresolved { get; } = [];

        public bool ItemHadAnError => ItemErrorWhenWritten is not (null or ActivityRunProfileExecutionItemErrorType.NotSet);
    }

    /// <summary>
    /// The values an import object brings: what a unit's value budget counts. Each list is read only when it holds
    /// something, as reading one allocates it.
    /// </summary>
    private static int ImportValueCount(ConnectedSystemImportObject importObject) =>
        importObject.Attributes.Sum(attribute =>
            (attribute.HasStringValues ? attribute.StringValues.Count : 0) +
            (attribute.HasIntValues ? attribute.IntValues.Count : 0) +
            (attribute.HasLongValues ? attribute.LongValues.Count : 0) +
            (attribute.HasDecimalValues ? attribute.DecimalValues.Count : 0) +
            (attribute.HasGuidValues ? attribute.GuidValues.Count : 0) +
            (attribute.HasBoolValues ? attribute.BoolValues.Count : 0) +
            (attribute.HasDateTimeValues ? attribute.DateTimeValues.Count : 0) +
            (attribute.HasByteValues ? attribute.ByteValues.Count : 0) +
            (attribute.HasReferenceValues ? attribute.ReferenceValues.Count : 0));

    /// <summary>
    /// Processes one Connector page. A Delta Import processes it as every page always was. A Full Import adds it to the
    /// unit being built, saving the unit first when the page would take it past its object limit or value budget; a page
    /// larger than a unit on its own is split, classified first as a whole (which copies repeat on the page, and which an
    /// earlier page returned, #2063) so splitting it changes nothing about which copies are rejected.
    /// </summary>
    private async Task ProcessImportedPageAsync(ConnectedSystemImportResult page, List<ConnectedSystemObject> connectedSystemObjectsToBeCreated,
        List<ConnectedSystemObject> connectedSystemObjectsToBeUpdated, HashSet<string>? crossPageSeenExternalIds)
    {
        if (!StagesUnits)
        {
            await ProcessImportObjectsAsync(page, connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated, crossPageSeenExternalIds);
            return;
        }

        var pageValues = page.ImportObjects.Sum(o => (long)ImportValueCount(o));
        if (!FitsInUnit(page.ImportObjects.Count, pageValues) && _unitObjectCount > 0)
            await StageUnitAsync(connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated);

        if (FitsInUnit(page.ImportObjects.Count, pageValues))
        {
            await ProcessImportObjectsAsync(page, connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated, crossPageSeenExternalIds);
            _unitObjectCount += page.ImportObjects.Count;
            _unitValueCount += pageValues;
            return;
        }

        // Larger than a unit on its own. The page's External IDs are looked up, and its copies classified, as a whole, as
        // processing the page in one piece would; each part is then processed as its own page would be, from hydration on.
        await PrepareCsoExternalIdLookupForPageAsync(page, crossPageSeenExternalIds);
        var classification = FindDuplicateExternalIds(page.ImportObjects, crossPageSeenExternalIds, sweepNullValues: false);
        var valueCounts = page.ImportObjects.Select(ImportValueCount).ToList();
        var parts = ValueBudgetedBatchPlanner.Plan(valueCounts, ImportBatchSize, ImportUnitValueBudget);
        Log.Information("ProcessImportedPageAsync: A page of {ObjectCount} objects and {ValueCount} values is larger than a unit ({MaxObjects} objects, {ValueBudget} values), so it is processed in {PartCount} parts.",
            page.ImportObjects.Count, pageValues, ImportBatchSize, ImportUnitValueBudget, parts.Count);

        foreach (var (start, count) in parts)
        {
            if (_unitObjectCount > 0)
                await StageUnitAsync(connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated);
            if (_cancellationTokenSource.IsCancellationRequested)
                return;

            var part = new ConnectedSystemImportResult { ImportObjects = page.ImportObjects.GetRange(start, count) };
            await ProcessImportObjectsAsync(part, connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated, crossPageSeenExternalIds, classification);
            _unitObjectCount += count;
            _unitValueCount += valueCounts.Skip(start).Take(count).Sum(c => (long)c);
        }
    }

    /// <summary>Whether a page of this many objects and values fits in the unit being built.</summary>
    private bool FitsInUnit(int objectCount, long valueCount) =>
        _unitObjectCount + objectCount <= ImportBatchSize && _unitValueCount + valueCount <= ImportUnitValueBudget;

    /// <summary>
    /// Saves the unit being built: resolves its references (noting those whose object is not saved yet), then saves,
    /// stamps and reconciles it exactly as the single save phase did its batches, and releases it. The Connected System is
    /// marked import incomplete before the run's first unit is written (#2521, Phase 4).
    /// </summary>
    private async Task StageUnitAsync(List<ConnectedSystemObject> connectedSystemObjectsToBeCreated, List<ConnectedSystemObject> connectedSystemObjectsToBeUpdated)
    {
        var objectsRead = _unitObjectCount;
        var valuesRead = _unitValueCount;
        _unitObjectCount = 0;
        _unitValueCount = 0;

        if (connectedSystemObjectsToBeCreated.Count == 0 && connectedSystemObjectsToBeUpdated.Count == 0)
            return;

        using var span = Diagnostics.Sync.StartSpan("StageImportUnit")
            .SetTag("unit", StagedUnitCount + 1)
            .SetTag("createCount", connectedSystemObjectsToBeCreated.Count)
            .SetTag("updateCount", connectedSystemObjectsToBeUpdated.Count);

        await MarkImportIncompleteBeforeFirstWriteAsync();

        // Ids first, so an object saved with a reference still to resolve can be noted by its id.
        foreach (var cso in connectedSystemObjectsToBeCreated)
            TimeOrderedIds.AssignMissing(cso);

        using (Diagnostics.Sync.StartSpan("ResolveReferences").SetTag("unit", true))
            await ResolveReferencesAsync(connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated, stagingUnit: true);
        SampleHeldAttributeValues(connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated);

        var createCount = connectedSystemObjectsToBeCreated.Count;
        var updateCount = connectedSystemObjectsToBeUpdated.Count;
        var reconciliation = await StageImportedObjectsAsync(connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated, stagingUnit: true);

        // An object saved more than once in the unit is reconciled once, after the unit's batches, as one saved more than
        // once was after the single save phase.
        if (reconciliation != null && !_cancellationTokenSource.IsCancellationRequested)
            await ReconcileDeferredAsync(reconciliation);

        _stagedCreateCount += createCount;
        _stagedUpdateCount += updateCount;
        connectedSystemObjectsToBeUpdated.Clear();
        StagedUnitCount++;
        SampleHeldAttributeValues(connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated);

        // The measurement the lab reads (#2521): the heap the last full collection left, which a run that holds one unit at
        // a time keeps flat however many units it saves; no collection is forced for it.
        var lastFullCollection = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        var liveAfterLastFullCollection = lastFullCollection.Index == 0 ? 0 : lastFullCollection.HeapSizeBytes - lastFullCollection.FragmentedBytes;
        PeakUnitBoundaryLiveHeapBytes = Math.Max(PeakUnitBoundaryLiveHeapBytes, liveAfterLastFullCollection);
        Log.Information("StageUnitAsync: Unit {Unit} saved: {ObjectsRead} objects read ({ValuesRead} values), {Created} created, {Updated} updated, {Outstanding} objects with references still to resolve so far. " +
            "GC heap: {HeapMB:N0}MB, live after the last full collection: {LiveMB:N0}MB, working set: {WorkingSetMB:N0}MB",
            StagedUnitCount, objectsRead, valuesRead, createCount, updateCount, _outstandingReferenceHolders.Count,
            GC.GetTotalMemory(false) / 1024 / 1024, liveAfterLastFullCollection / 1024 / 1024,
            Environment.WorkingSet / 1024 / 1024);
        span.SetSuccess();
    }

    /// <summary>
    /// Closes a Full Import stopped after saving units (#2521): the reconciliation the saved units carried out is summarised
    /// and the execution items it raised, written with their units, are counted in the Activity's totals, as a run stopped
    /// in its single save phase counts them. Everything after the units (deletion detection, the sweep, the watermark) is
    /// left to the next Full Import. Does nothing for a run that saved no unit.
    /// </summary>
    private async Task CloseStoppedStagedRunAsync()
    {
        if (!StagesUnits || _unitReconciliation == null)
            return;

        CompletePendingExportReconciliation(_unitReconciliation);
        await FlushImportRpeisAsync([], alsoCountInActivityTotals: _unitReconciliation.RaisedItemTotals);
    }

    /// <summary>
    /// The run's Pending Export reconciliation for one unit's updates: begun by the first unit that updates anything, as
    /// the single save phase began it, then readied for each later unit with the objects that unit saves more than once.
    /// Null when the Connected System had no Pending Export when it began.
    /// </summary>
    private async Task<PendingExportReconciliationRun?> PrepareUnitReconciliationAsync(IReadOnlyCollection<Guid> updatedCsoIds)
    {
        if (updatedCsoIds.Count == 0)
            return _unitReconciliation;

        if (!_unitReconciliationBegun)
        {
            _unitReconciliationBegun = true;
            _unitReconciliation = await BeginPendingExportReconciliationAsync(updatedCsoIds);
            return _unitReconciliation;
        }

        if (_unitReconciliation != null)
        {
            _unitReconciliation.DeferredIds = await FindObjectsSavedMoreThanOnceWithPendingExportsAsync(updatedCsoIds);
            _unitReconciliation.DeferredImportItems.Clear();
        }

        return _unitReconciliation;
    }

    /// <summary>
    /// Notes an object saved with a reference value whose object the run has not saved yet, once: its execution item's id
    /// (given one now, if it has none) and error as it will be written, and its stamp, held back until the sweep.
    /// </summary>
    private OutstandingReferenceHolder NoteOutstandingReference(ConnectedSystemObject cso, IReadOnlyDictionary<ConnectedSystemObject, ActivityRunProfileExecutionItem> itemsByCso)
    {
        if (_outstandingReferenceHolders.TryGetValue(cso.Id, out var holder))
            return holder;

        itemsByCso.TryGetValue(cso, out var item);
        if (item is { Id: var itemId } && itemId == Guid.Empty)
            item.Id = TimeOrderedIds.New();

        holder = new OutstandingReferenceHolder(cso.Id, cso.Type?.Id ?? cso.TypeId, item?.Id, item?.ErrorType,
            cso.PendingImportStateStampRequested, cso.PendingImportStateHash, cso.PendingImportStateFingerprint);

        // Stamped by the sweep once its references are settled: a stamp now would let a run that stops before the sweep
        // leave the object to be skipped by its hash, with its reference still unresolved, rather than compared again.
        cso.PendingImportStateStampRequested = false;
        _outstandingReferenceHolders[cso.Id] = holder;
        return holder;
    }

    /// <summary>
    /// #2049: whether a reference value names an Object Type change replacement the run holds apart: it is then left for
    /// the sweep, which resolves it to the replacement if it stands, as the whole run's in-memory resolution did, rather
    /// than to the object it replaces, which the database holds until deletion detection retires it.
    /// </summary>
    private bool NamesAHeldReplacement(ConnectedSystemObjectAttributeValue attrValue, ConnectedSystemObjectTypeAttribute externalIdAttribute,
        IReadOnlyDictionary<int, ReferenceTargetCandidate> candidatesByTypeId, IReadOnlyList<ReferenceTargetCandidate> allCandidates)
    {
        if (_objectTypeChangesReconciled || _objectTypeChangesCarriedOut.Count == 0)
            return false;

        if (_heldReplacementLookupsBuiltFor != _objectTypeChangesCarriedOut.Count)
        {
            _heldReplacementLookups = BuildExternalIdLookups(_objectTypeChangesCarriedOut.Select(change => change.Replacement).ToList(), []);
            _heldReplacementLookupsBuiltFor = _objectTypeChangesCarriedOut.Count;
        }

        return FindInLookups(attrValue, externalIdAttribute, _heldReplacementLookups!, candidatesByTypeId, allCandidates).Count > 0;
    }

    /// <summary>
    /// #2049: on a run that allows Object Type changes, keeps the references a unit resolved through the database to objects
    /// the run had not read, so the sweep can recheck those an Object Type change carried out by a later unit retires. An
    /// object holding one has its stamp held, as an outstanding reference's is, with its execution item's error and count
    /// as written, for the recheck's report.
    /// </summary>
    private void KeepReferencesForObjectTypeChangeRecheck(IReadOnlyCollection<(ConnectedSystemObject Cso, Guid TargetId)> resolvedThroughDatabase,
        IReadOnlyDictionary<ConnectedSystemObject, ActivityRunProfileExecutionItem> itemsByCso)
    {
        foreach (var (cso, targetId) in resolvedThroughDatabase.Where(pair =>
                     !_csoIdsCreatedThisRun.Contains(pair.TargetId) && !_csoIdsQueuedForUpdate.Contains(pair.TargetId)))
        {
            _referencesKeptForRecheck ??= new Dictionary<Guid, List<Guid>>();
            _holdersKeptForRecheck ??= new Dictionary<Guid, OutstandingReferenceHolder>();
            if (!_referencesKeptForRecheck.TryGetValue(targetId, out var holders))
            {
                holders = [];
                _referencesKeptForRecheck[targetId] = holders;
            }

            holders.Add(cso.Id);
            ReferencesKeptForObjectTypeChangeRecheck++;
            if (_outstandingReferenceHolders.ContainsKey(cso.Id) || _holdersKeptForRecheck.ContainsKey(cso.Id))
                continue;

            itemsByCso.TryGetValue(cso, out var item);
            if (item is { Id: var itemId } && itemId == Guid.Empty)
                item.Id = TimeOrderedIds.New();

            _holdersKeptForRecheck[cso.Id] = new OutstandingReferenceHolder(cso.Id, cso.Type?.Id ?? cso.TypeId, item?.Id, item?.ErrorType,
                cso.PendingImportStateStampRequested, cso.PendingImportStateHash, cso.PendingImportStateFingerprint, item?.UnresolvedReferenceCount ?? 0);

            // Stamped once the recheck is decided: a stamp now would let a run that stops before it leave the object to be
            // skipped by its hash, still naming an object a change may retire.
            cso.PendingImportStateStampRequested = false;
        }
    }

    /// <summary>
    /// #2049: re-opens the kept references to objects a standing Object Type change retired, so the sweep resolves them by
    /// its rules as the single save phase would have with the replacement in hand: ambiguous where the retired object was
    /// newly marked this run, the replacement where it was already Obsolete. Their objects join the sweep's notes; the kept
    /// references are released.
    /// </summary>
    private async Task ReopenReferencesRetiredByObjectTypeChangesAsync()
    {
        var kept = _referencesKeptForRecheck;
        _referencesKeptForRecheck = null;
        if (kept == null || _csoIdsRetiredByStandingObjectTypeChanges is not { Count: > 0 } retired)
            return;

        var retiredNamed = kept.Keys.Where(retired.Contains).ToList();
        var affected = retiredNamed.SelectMany(targetId => kept[targetId]).ToHashSet();
        if (affected.Count == 0)
            return;

        foreach (var csoId in affected.Where(id => _holdersKeptForRecheck?.ContainsKey(id) == true))
        {
            _outstandingReferenceHolders[csoId] = _holdersKeptForRecheck![csoId];
            _holdersKeptForRecheck.Remove(csoId);
        }

        var itemIds = affected
            .Select(csoId => _outstandingReferenceHolders.TryGetValue(csoId, out var holder) ? holder.ExecutionItemId : null)
            .OfType<Guid>()
            .ToList();
        var reopened = await _syncRepo.ReopenReferencesToRetiredObjectsAsync(affected, retiredNamed, itemIds);
        Log.Information("ReopenReferencesRetiredByObjectTypeChangesAsync: {ValueCount} reference value(s) on {ObjectCount} object(s) named {RetiredCount} object(s) an Object Type change retired after the reference was resolved; the sweep resolves them again.",
            reopened, affected.Count, retiredNamed.Count);
    }

    /// <summary>
    /// The end-of-run reference sweep (#2521): reads the outstanding reference values of the objects saved before what they
    /// name, a bounded batch of objects at a time, resolves each by the rules a unit's resolution follows (a standing
    /// Object Type change replacement first, #2049, then the database: a declared target type alone, #1285, compared as
    /// its anchor compares, #2051, and a value matching more than one Object Type reported, never guessed), writes what it
    /// resolved, reports what is left on the execution items already saved, and stamps the objects whose stamps it held.
    /// </summary>
    private async Task ResolveOutstandingReferencesAsync()
    {
        await ReopenReferencesRetiredByObjectTypeChangesAsync();
        if (_outstandingReferenceHolders.Count == 0)
        {
            await StampHoldersAsync();
            return;
        }

        _activity.ObjectsToProcess = _outstandingReferenceHolders.Count;

        using var span = Diagnostics.Sync.StartSpan("ResolveOutstandingReferences").SetTag("objectCount", _outstandingReferenceHolders.Count);
        var (candidatesByTypeId, allCandidates) = ReferenceTargetCandidates();
        var objectTypesById = new Dictionary<int, ConnectedSystemObjectType>();
        var attributesById = new Dictionary<int, ConnectedSystemObjectTypeAttribute>();
        foreach (var objectType in _connectedSystem.ObjectTypes!)
        {
            objectTypesById.TryAdd(objectType.Id, objectType);
            foreach (var attribute in objectType.Attributes)
                attributesById.TryAdd(attribute.Id, attribute);
        }
        var handling = _connectedSystem.UnresolvedReferenceHandling;

        var valuesRead = 0;
        var resolvedCount = 0;
        var processed = 0;
        foreach (var batch in _outstandingReferenceHolders.Values.Chunk(DeletionCandidateBatchSize))
        {
            var holdersById = batch.ToDictionary(holder => holder.CsoId);
            var outstanding = await _syncRepo.GetOutstandingReferenceValuesAsync(holdersById.Keys);
            valuesRead += outstanding.Count;

            var toLookUp = new List<(OutstandingReferenceHolder Owner, ConnectedSystemObjectAttributeValue AttrValue, ConnectedSystemObjectTypeAttribute ExternalIdAttribute)>();
            var resolutions = new List<OutstandingReferenceResolution>();
            foreach (var value in outstanding.Where(v => holdersById.ContainsKey(v.ConnectedSystemObjectId)))
            {
                var holder = holdersById[value.ConnectedSystemObjectId];
                if (!objectTypesById.TryGetValue(value.ConnectedSystemObjectTypeId, out var holderType) || !attributesById.TryGetValue(value.AttributeId, out var attribute))
                {
                    holder.LeftUnresolved.Add(($"attribute {value.AttributeId}", value.UnresolvedReferenceValue, null));
                    continue;
                }

                var externalIdAttribute = holderType.Attributes.SingleOrDefault(a => a.IsSecondaryExternalId) ?? holderType.Attributes.Single(a => a.IsExternalId);
                var attrValue = new ConnectedSystemObjectAttributeValue
                {
                    Id = value.AttributeValueId, AttributeId = attribute.Id, Attribute = attribute, UnresolvedReferenceValue = value.UnresolvedReferenceValue
                };

                // The whole run's in-memory resolution found a replacement that stood before the database was asked, so the
                // sweep does too; with the object it retired beside it where deletion detection marked that object this run.
                if (_standingReplacementLookups != null)
                {
                    var replacementMatches = FindInLookups(attrValue, externalIdAttribute, _standingReplacementLookups, candidatesByTypeId, allCandidates);
                    if (replacementMatches.Count == 1 && attribute.ReferencedObjectTypeId == null &&
                        _standingReplacementsBesideRetiredObjects.TryGetValue(replacementMatches[0].Match, out var retiredTypeNames))
                    {
                        holder.LeftUnresolved.Add((attribute.Name, value.UnresolvedReferenceValue,
                            DistinctTypeNames(retiredTypeNames.Append(replacementMatches[0].TypeName))));
                        continue;
                    }

                    if (replacementMatches.Count == 1)
                    {
                        resolutions.Add(new OutstandingReferenceResolution(value.AttributeValueId, replacementMatches[0].Match.Id, holder.ExecutionItemId,
                            attribute.Id, value.UnresolvedReferenceValue));
                        continue;
                    }

                    if (replacementMatches.Count > 1)
                    {
                        holder.LeftUnresolved.Add((attribute.Name, value.UnresolvedReferenceValue, DistinctTypeNames(replacementMatches.Select(m => m.TypeName))));
                        continue;
                    }
                }

                toLookUp.Add((holder, attrValue, externalIdAttribute));
            }

            foreach (var (holder, attrValue, referencedId, ambiguousWith) in await ResolveReferenceIdsFromDatabaseAsync(toLookUp, candidatesByTypeId, allCandidates))
            {
                if (referencedId is { } id)
                    resolutions.Add(new OutstandingReferenceResolution(attrValue.Id, id, holder.ExecutionItemId, attrValue.AttributeId, attrValue.UnresolvedReferenceValue!));
                else
                    holder.LeftUnresolved.Add((attrValue.Attribute!.Name, attrValue.UnresolvedReferenceValue!, ambiguousWith == null ? null : DistinctTypeNames(ambiguousWith)));
            }

            resolvedCount += await _syncRepo.ApplyOutstandingReferenceResolutionsAsync(resolutions);
            processed += batch.Length;
            _activity.ObjectsProcessed = processed;
            await _syncRepo.UpdateActivityProgressOutOfBandAsync(_activity);
        }

        await ReportOutstandingReferencesLeftUnresolvedAsync(handling);
        await StampHoldersAsync();

        Log.Information("ResolveOutstandingReferencesAsync: The sweep read {ValueCount} outstanding reference value(s) on {ObjectCount} object(s) saved before the objects they name, and resolved {ResolvedCount}.",
            valuesRead, _outstandingReferenceHolders.Count, resolvedCount);
        span.SetTag("valueCount", valuesRead).SetTag("resolvedCount", resolvedCount).SetSuccess();
    }

    /// <summary>
    /// Reports what the sweep left unresolved, once per object, on its execution item (#959): the count under every
    /// handling, and under Error the unresolved reference error, where the item recorded no error of its own when it was
    /// written, as resolution before the save always gave it the slot first. Warn and Ignore leave the item un-errored.
    /// </summary>
    private async Task ReportOutstandingReferencesLeftUnresolvedAsync(UnresolvedReferenceHandling handling)
    {
        var reports = new List<UnresolvedReferenceReport>();
        var newlyErrored = 0;
        var objectsMissingAnItem = 0;
        foreach (var holder in _outstandingReferenceHolders.Values.Where(h => h.LeftUnresolved.Count > 0))
        {
            foreach (var (_, value, ambiguousWith) in holder.LeftUnresolved)
            {
                if (ambiguousWith == null)
                {
                    _unresolvedReferenceTotal++;
                    if (handling == UnresolvedReferenceHandling.Warn)
                        Log.Warning("ResolveOutstandingReferencesAsync: Couldn't resolve a CSO reference ({UnresolvedRef}) for CSO {CsoId}. The referenced object may be outside the configured Container Scope, or inside a Container it excludes.",
                            LogSanitiser.Sanitise(value), holder.CsoId);
                    else
                        Log.Debug("ResolveOutstandingReferencesAsync: Couldn't resolve a CSO reference ({UnresolvedRef}) for CSO {CsoId} (handling: {UnresolvedReferenceHandling}).",
                            LogSanitiser.Sanitise(value), holder.CsoId, handling);
                }
                else
                {
                    _ambiguousReferenceTotal++;
                    Log.Debug("ResolveOutstandingReferencesAsync: A reference value ({AmbiguousRef}) on CSO {CsoId} matches objects of more than one Object Type ({CandidateNames}) (handling: {UnresolvedReferenceHandling}).",
                        LogSanitiser.Sanitise(value), holder.CsoId, string.Join(", ", ambiguousWith), handling);
                }
            }

            if (holder.ExecutionItemId is not { } itemId)
            {
                objectsMissingAnItem++;
                continue;
            }

            var takesTheErrorSlot = handling == UnresolvedReferenceHandling.Error && !holder.ItemHadAnError;
            if (takesTheErrorSlot)
                newlyErrored++;
            reports.Add(new UnresolvedReferenceReport(itemId, holder.UnresolvedCountWhenWritten + holder.LeftUnresolved.Count,
                takesTheErrorSlot ? ActivityRunProfileExecutionItemErrorType.UnresolvedReference : null,
                takesTheErrorSlot ? ImportOutcomeMessage.ForUnresolvedReferences(holder.LeftUnresolved) : null));
        }

        if (objectsMissingAnItem > 0)
            Log.Warning("ResolveOutstandingReferencesAsync: {Count} Connected System Object(s) with unresolved references had no Run Profile Execution Item; their unresolved values are recorded on the objects only.",
                objectsMissingAnItem);

        await _syncRepo.UpdateUnresolvedReferenceReportsAsync(reports);

        // The items were counted into the Activity's totals as they were written, before they took the error; resolution
        // before the save gave them the error first, so it was counted with them.
        if (_itemsCountedAsWritten)
            _activity.TotalErrors += newlyErrored;
    }

    /// <summary>
    /// Writes the stamps the sweep held back (SPEC-1082 D7), by the rule the save phase applies to every other object: a
    /// requested clear always, and a hash only where the object's item ends the run without an error of its own (Verification
    /// Mode's report aside). Those of the objects kept only for an Object Type change recheck that needed none are written
    /// too, and released (#2049).
    /// </summary>
    private async Task StampHoldersAsync()
    {
        var handling = _connectedSystem.UnresolvedReferenceHandling;
        var stamps = _outstandingReferenceHolders.Values
            .Concat(_holdersKeptForRecheck?.Values ?? Enumerable.Empty<OutstandingReferenceHolder>())
            .Where(holder => holder.StampRequested)
            .Where(holder =>
            {
                var tookTheErrorSlot = handling == UnresolvedReferenceHandling.Error && holder.LeftUnresolved.Count > 0 && !holder.ItemHadAnError && holder.ExecutionItemId.HasValue;
                var erroredWhenWritten = holder.ItemHadAnError && holder.ItemErrorWhenWritten != ActivityRunProfileExecutionItemErrorType.ImportHashVerificationFailed;
                return holder.StampHash == null || (!tookTheErrorSlot && !erroredWhenWritten);
            })
            .Select(holder => (CsoId: holder.CsoId, Hash: holder.StampHash, Fingerprint: holder.StampFingerprint))
            .ToList();

        foreach (var chunk in stamps.Chunk(DeletionCandidateBatchSize))
            await _syncRepo.StampImportStateAsync(chunk);
        _holdersKeptForRecheck = null;
    }

    /// <summary>
    /// Logs, and under Warn adds to the Activity's warning, what reference resolution left unresolved or ambiguous over the
    /// whole run: once, whether the run resolved its references in one pass or unit by unit with a sweep at the end.
    /// </summary>
    private void ReportReferenceResolutionSummary()
    {
        var handling = _connectedSystem.UnresolvedReferenceHandling;
        if (_ambiguousReferenceTotal > 0)
        {
            Log.Information("ResolveReferencesAsync: {Count} reference value(s) were ambiguous across Object Types (handling: {UnresolvedReferenceHandling}).",
                _ambiguousReferenceTotal, handling);

            if (handling == UnresolvedReferenceHandling.Warn)
                AppendActivityWarning($"{_ambiguousReferenceTotal} reference value(s) matched objects of more than one Object Type and could not be resolved. Declare each Reference attribute's target Object Type in the Connected System's schema configuration if the Connector supports doing so.");
        }

        // Always logged when references were left unresolved, regardless of handling mode.
        if (_unresolvedReferenceTotal > 0)
        {
            Log.Information("ResolveReferencesAsync: {Count} reference value(s) could not be resolved to Connected System Objects (handling: {UnresolvedReferenceHandling}).",
                _unresolvedReferenceTotal, handling);

            // Both ways an object can be out of scope, named explicitly (#1255): its Container was never selected, or a
            // Container excludes it. The second is the harder to spot, because the branch above the referenced object is
            // selected and looks right.
            if (handling == UnresolvedReferenceHandling.Warn)
                AppendActivityWarning($"{_unresolvedReferenceTotal} reference value(s) could not be resolved to Connected System Objects. The referenced objects may be outside the configured Container Scope, or inside a Container it excludes. View the affected Connected System Objects for details.");
        }
    }

    private void AppendActivityWarning(string warning) =>
        _activity.WarningMessage = string.IsNullOrEmpty(_activity.WarningMessage) ? warning : $"{_activity.WarningMessage}\n{warning}";

    private static List<string> DistinctTypeNames(IEnumerable<string> typeNames) =>
        typeNames.Distinct().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The Object Types a reference could point at, each with its primary anchor attribute (#1285), by id and in order.
    /// </summary>
    private (Dictionary<int, ReferenceTargetCandidate> ByTypeId, List<ReferenceTargetCandidate> All) ReferenceTargetCandidates()
    {
        var all = (_connectedSystem.ObjectTypes ?? Enumerable.Empty<ConnectedSystemObjectType>())
            .Select(objectType => (ObjectType: objectType, Anchor: objectType.Attributes.SingleOrDefault(a => a.IsExternalId)))
            .Where(pair => pair.Anchor != null)
            .Select(pair => new ReferenceTargetCandidate(pair.ObjectType, pair.Anchor!))
            .ToList();
        return (all.ToDictionary(candidate => candidate.ObjectType.Id), all);
    }

    /// <summary>
    /// Looks up reference values in the database by id alone (#2521), by the rules of the whole-object fallback: grouped by
    /// the anchor each must be looked up against (a declared target names one; an undeclared primary reference could match
    /// any Object Type's anchor, and one found under more than one is ambiguous, #1285), or through Secondary External IDs
    /// of any type, compared as each anchor compares (#2051). Loads and tracks no object.
    /// </summary>
    /// <returns>For each value, the id of the object it names, or null with the Object Types it is ambiguous between
    /// (null when it names none).</returns>
    private async Task<List<(TOwner Owner, ConnectedSystemObjectAttributeValue AttrValue, Guid? ReferencedId, List<string>? AmbiguousWith)>> ResolveReferenceIdsFromDatabaseAsync<TOwner>(
        IReadOnlyCollection<(TOwner Owner, ConnectedSystemObjectAttributeValue AttrValue, ConnectedSystemObjectTypeAttribute ExternalIdAttribute)> items,
        IReadOnlyDictionary<int, ReferenceTargetCandidate> candidatesByTypeId, IReadOnlyList<ReferenceTargetCandidate> allCandidates)
    {
        var results = new List<(TOwner Owner, ConnectedSystemObjectAttributeValue AttrValue, Guid? ReferencedId, List<string>? AmbiguousWith)>(items.Count);
        if (items.Count == 0)
            return results;

        var secondaryValues = new HashSet<string>(StringComparer.Ordinal);
        var primaryValueGroups = new Dictionary<int, HashSet<string>>();
        var anchorCaseExactById = new Dictionary<int, bool>();
        foreach (var (_, attrValue, externalIdAttribute) in items)
        {
            if (UsesSecondaryReferenceResolution(attrValue, externalIdAttribute))
            {
                secondaryValues.Add(attrValue.UnresolvedReferenceValue!);
                continue;
            }

            foreach (var candidate in FallbackCandidatesFor(attrValue, candidatesByTypeId, allCandidates))
            {
                if (!primaryValueGroups.TryGetValue(candidate.AnchorAttribute.Id, out var values))
                {
                    values = new HashSet<string>(IdentifierKey.Comparer(candidate.AnchorAttribute.CaseExact));
                    primaryValueGroups[candidate.AnchorAttribute.Id] = values;
                    anchorCaseExactById[candidate.AnchorAttribute.Id] = candidate.AnchorAttribute.CaseExact;
                }

                values.Add(attrValue.UnresolvedReferenceValue!);
            }
        }

        var primaryResultsByAnchor = new Dictionary<int, Dictionary<string, Guid>>();
        foreach (var (anchorAttributeId, values) in primaryValueGroups)
        {
            var groupResults = new Dictionary<string, Guid>(IdentifierKey.Comparer(anchorCaseExactById[anchorAttributeId]));
            primaryResultsByAnchor[anchorAttributeId] = groupResults;
            foreach (var page in values.Chunk(ReferenceIdLookupChunkSize))
            {
                foreach (var (value, id) in await _syncRepo.GetConnectedSystemObjectIdsByAttributeValuesAsync(_connectedSystem.Id, anchorAttributeId, page))
                    groupResults.TryAdd(value, id);
            }
        }

        var secondaryResults = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var page in secondaryValues.Chunk(ReferenceIdLookupChunkSize))
        {
            foreach (var (value, id) in await _syncRepo.GetConnectedSystemObjectIdsBySecondaryExternalIdAnyTypeValuesAsync(_connectedSystem.Id, page))
                secondaryResults.TryAdd(value, id);
        }

        foreach (var (owner, attrValue, externalIdAttribute) in items)
        {
            if (UsesSecondaryReferenceResolution(attrValue, externalIdAttribute))
            {
                results.Add((owner, attrValue, secondaryResults.TryGetValue(attrValue.UnresolvedReferenceValue!, out var secondaryId) ? secondaryId : null, null));
                continue;
            }

            var hits = FallbackCandidatesFor(attrValue, candidatesByTypeId, allCandidates)
                .Where(candidate => primaryResultsByAnchor.TryGetValue(candidate.AnchorAttribute.Id, out var groupResults) &&
                                    groupResults.ContainsKey(attrValue.UnresolvedReferenceValue!))
                .Select(candidate => (Id: primaryResultsByAnchor[candidate.AnchorAttribute.Id][attrValue.UnresolvedReferenceValue!], TypeName: candidate.ObjectType.Name))
                .ToList();

            switch (hits.Count)
            {
                case 0:
                    results.Add((owner, attrValue, null, null));
                    break;
                case 1:
                    results.Add((owner, attrValue, hits[0].Id, null));
                    break;
                default:
                    results.Add((owner, attrValue, null, hits.Select(hit => hit.TypeName).ToList()));
                    break;
            }
        }

        return results;
    }
}
