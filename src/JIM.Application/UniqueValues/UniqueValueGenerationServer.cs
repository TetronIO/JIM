// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;

namespace JIM.Application.UniqueValues;

/// <summary>
/// The caller-agnostic unique value service (Unique Value Generation, #242, FR 21). Resolves a batch of
/// <see cref="GenerationRequest"/>s to <see cref="GenerationOutcome"/>s: sticky first, then candidate
/// generation through five ordered gates, then, once the caller has persisted the objects, saving the
/// assignments it proposed. Precedence is not this service's concern: the caller only raises a request where the
/// generated mapping is the attribute's winning contributor under the ordinary Attribute Flow priority model, and
/// the mapping then contributes its existing assignment if it has one and generates one otherwise (generate once;
/// product-owner decision 2026-10-01). This type knows nothing about synchronisation runs,
/// Synchronisation Rules, or which caller asked; it is handed data access (<see cref="ISyncRepository"/>) the
/// same way <c>IExpressionEvaluator</c> is handed to the sync engine, so the sync engine, Sync Preview (over
/// <see cref="ReadOnlySyncRepositoryGuard"/>), and any future caller construct their own instance over
/// whichever repository fits their unit of work.
/// </summary>
public sealed class UniqueValueGenerationServer
{
    private readonly ISyncRepository _repository;

    /// <summary>
    /// Constructs the service over <paramref name="repository"/>. The sync engine and worker pass
    /// <c>PostgresData.SyncRepository</c> (via <c>JimApplication.UniqueValues</c>); Sync Preview constructs its
    /// own instance over <see cref="ReadOnlySyncRepositoryGuard"/> so a preview resolve can never write.
    /// </summary>
    public UniqueValueGenerationServer(ISyncRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Resolves every request in <paramref name="requests"/> against <paramref name="options"/>'s reservation
    /// set and run-scoped caches, in the order described in the plan's "Behaviour (ResolveAsync)" section:
    /// sticky, then candidate generation through the reservation, retired values register, Metaverse (or
    /// Connected System), connector space, and other-live-assignment gates in that order, batched per
    /// (attribute, gate) within this call. Returns exactly one outcome per request, in the same order.
    /// <para>
    /// Performs no repository writes under <see cref="UniqueValueResolveOptions.DryRun"/>; every write this
    /// method could otherwise make (only <see cref="ISyncRepository.ReserveGeneratedValueSequenceBlockAsync"/>:
    /// every gate query is a read) is replaced with a local simulation, and any reservation-set claim this call
    /// makes is released again before it returns.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<GenerationOutcome>> ResolveAsync(IReadOnlyList<GenerationRequest> requests, UniqueValueResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(options);

        if (requests.Count == 0)
            return [];

        // A dry run claims and releases under a throwaway owner id of its own: a Sync Preview call must leave
        // no trace in the shared reservation set once it returns, regardless of what options.ReservationOwnerId
        // is used for elsewhere by the caller.
        var ownerId = options.DryRun ? Guid.NewGuid() : options.ReservationOwnerId;
        var claimedThisCall = new HashSet<(UniqueValueScope Scope, int AttributeId, string NormalisedValue)>();
        var outcomes = new GenerationOutcome?[requests.Count];

        try
        {
            var stickyMap = await LoadStickyAssignmentsAsync(requests, options);
            var toGenerate = new List<int>();

            // Generate once (product-owner decision 2026-10-01): a live assignment is contributed as it stands,
            // whatever value another rule may have left on the object, exactly as an ordinary Attribute Flow
            // winning priority contributes its value over whatever was there before. Only a request with no
            // assignment generates.
            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                if (stickyMap.TryGetValue(i, out var sticky))
                {
                    outcomes[i] = BuildStickyOutcome(request, sticky);
                    continue;
                }

                if (request.StickyOnly)
                {
                    outcomes[i] = new GenerationOutcome(request, GenerationOutcomeKind.Waiting, null, null, null, null);
                    continue;
                }

                toGenerate.Add(i);
            }

            await ResolveGenerationRoundsAsync(requests, toGenerate, outcomes, options, ownerId, claimedThisCall);

            return outcomes!;
        }
        finally
        {
            if (options.DryRun)
                options.Reservations.ReleaseAll(ownerId);
        }
    }

    /// <summary>
    /// Loads the live assignments for the given objects into <paramref name="options"/>'s run-scoped cache, in
    /// one query per mode, so that <see cref="ResolveAsync"/> calls made for these objects later in the same
    /// run answer the sticky check from the cache rather than querying again. Callers use this ahead of a page
    /// to avoid a per-object query; it is an optimisation, not a requirement, since <see cref="ResolveAsync"/>
    /// queries and caches for itself on a cache miss. Objects already known to the run are not queried again.
    /// </summary>
    public async Task PrefetchAssignmentsAsync(IReadOnlyCollection<Guid> metaverseObjectIds, IReadOnlyCollection<Guid> connectedSystemObjectIds, UniqueValueResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // An object already known to the run is answered from the cache, which the run keeps current as it commits
        // and deletes assignments; querying it again would cost a round trip whose result TryAdd discards anyway.
        // Drift Detection's per-page prefetch (#242) relies on this: export evaluation may already have resolved
        // some of the page's Connected System Objects.
        metaverseObjectIds = metaverseObjectIds.Where(id => !options.KnownMetaverseAssignments.ContainsKey(id)).ToList();
        connectedSystemObjectIds = connectedSystemObjectIds.Where(id => !options.KnownConnectedSystemAssignments.ContainsKey(id)).ToList();

        if (metaverseObjectIds.Count > 0)
        {
            var assignments = await _repository.GetGeneratedValueAssignmentsForMetaverseObjectsAsync(metaverseObjectIds);
            var byObject = assignments.Where(a => a.MetaverseObjectId.HasValue).ToLookup(a => a.MetaverseObjectId!.Value);
            foreach (var id in metaverseObjectIds)
                options.KnownMetaverseAssignments.TryAdd(id, new ConcurrentBag<GeneratedValueAssignment>(byObject[id]));
        }

        if (connectedSystemObjectIds.Count > 0)
        {
            var assignments = await _repository.GetGeneratedValueAssignmentsForConnectedSystemObjectsAsync(connectedSystemObjectIds);
            var byObject = assignments.Where(a => a.ConnectedSystemObjectId.HasValue).ToLookup(a => a.ConnectedSystemObjectId!.Value);
            foreach (var id in connectedSystemObjectIds)
                options.KnownConnectedSystemAssignments.TryAdd(id, new ConcurrentBag<GeneratedValueAssignment>(byObject[id]));
        }
    }

    /// <summary>
    /// Persists the <see cref="GenerationOutcomeKind.Generated"/> outcomes' assignments, once the caller has
    /// persisted the objects themselves, as <see cref="GeneratedValueAssignmentState.Committed"/> with
    /// <see cref="GeneratedValueAssignment.CommittedAt"/> set (#1904: Committed means saved onto the object).
    /// <paramref name="objectIdResolver"/> returns the now-persisted object id for a request (the caller
    /// correlates through <see cref="GenerationRequest.CallerState"/>). Every other outcome kind is ignored.
    /// <para>
    /// Tries the whole batch first; on <see cref="GeneratedValueConflictException"/> (the cross-assignment
    /// unique index caught a losing-run collision, plan decision 13), falls back to inserting one at a time to
    /// identify exactly which outcomes lost the race, and returns those (the caller regenerates them next page
    /// or run). Every other outcome is saved. Never throws for a conflict; any other exception propagates.
    /// </para>
    /// <para>
    /// For every saved outcome whose <see cref="Logic.SyncRuleMappingGeneration.TokenKind"/> is
    /// <see cref="GeneratedValueTokenKind.Sequence"/>, advances that attribute's counter's display-only
    /// assigned count once, by however many of that attribute's sequence outcomes were actually saved.
    /// </para>
    /// <para>
    /// When <paramref name="options"/> is given, also records each saved assignment in its run-scoped cache
    /// (<see cref="UniqueValueResolveOptions.KnownMetaverseAssignments"/> /
    /// <see cref="UniqueValueResolveOptions.KnownConnectedSystemAssignments"/>), so a later
    /// <see cref="ResolveAsync"/> call for the same object in the same run sees it as
    /// <see cref="GenerationOutcomeKind.Sticky"/> without a query.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<GenerationOutcome>> CommitAssignmentsAsync(
        IReadOnlyList<GenerationOutcome> outcomes,
        Func<GenerationRequest, Guid> objectIdResolver,
        UniqueValueResolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(objectIdResolver);

        var toCommit = outcomes.Where(o => o.Kind == GenerationOutcomeKind.Generated).ToList();
        if (toCommit.Count == 0)
            return [];

        // #1904 (product-owner decision 2026-10-01, option A): Committed means the value has been saved onto its
        // object. This method's contract is that the caller has already persisted the objects (the page flush), so
        // every assignment it saves is Committed, stamped now. Whether a target has accepted the value is a
        // separate question (anchoring, release 4), never this state.
        var committedAt = DateTime.UtcNow;
        foreach (var outcome in toCommit)
        {
            var assignment = outcome.Assignment!;
            var objectId = objectIdResolver(outcome.Request);
            if (outcome.Request.Mode == GeneratedValueMode.Import)
                assignment.MetaverseObjectId = objectId;
            else
                assignment.ConnectedSystemObjectId = objectId;

            assignment.State = GeneratedValueAssignmentState.Committed;
            assignment.CommittedAt = committedAt;
            assignment.LastUpdated = committedAt;
        }

        var losers = new List<GenerationOutcome>();
        try
        {
            await _repository.CreateGeneratedValueAssignmentsAsync(toCommit.Select(o => o.Assignment!).ToList());
        }
        catch (GeneratedValueConflictException)
        {
            // The batch insert is transactional: nothing from it was persisted, so every outcome is retried
            // individually to identify exactly which one(s) lost the race.
            foreach (var outcome in toCommit)
            {
                try
                {
                    await _repository.CreateGeneratedValueAssignmentsAsync([outcome.Assignment!]);
                }
                catch (GeneratedValueConflictException)
                {
                    losers.Add(outcome);
                }
            }
        }

        // A loser was never saved, so it was never committed either: put it back as it was resolved.
        foreach (var loser in losers)
        {
            loser.Assignment!.State = GeneratedValueAssignmentState.Proposed;
            loser.Assignment.CommittedAt = null;
        }

        var saved = toCommit.Except(losers).ToList();

        if (options != null)
        {
            foreach (var outcome in saved)
            {
                var assignment = outcome.Assignment!;
                if (outcome.Request.Mode == GeneratedValueMode.Import && assignment.MetaverseObjectId.HasValue)
                    options.KnownMetaverseAssignments.GetOrAdd(assignment.MetaverseObjectId.Value, static _ => []).Add(assignment);
                else if (outcome.Request.Mode == GeneratedValueMode.Export && assignment.ConnectedSystemObjectId.HasValue)
                    options.KnownConnectedSystemAssignments.GetOrAdd(assignment.ConnectedSystemObjectId.Value, static _ => []).Add(assignment);
            }
        }

        var savedSequenceOutcomesByAttribute = saved
            .Where(o => o.Request.Generation.TokenKind == GeneratedValueTokenKind.Sequence)
            .GroupBy(o => (o.Request.MetaverseAttributeId, o.Request.ConnectedSystemObjectTypeAttributeId));

        foreach (var group in savedSequenceOutcomesByAttribute)
        {
            var sequence = await _repository.GetGeneratedValueSequenceAsync(group.Key.MetaverseAttributeId, group.Key.ConnectedSystemObjectTypeAttributeId);
            if (sequence != null)
                await _repository.IncrementGeneratedValueSequenceAssignedCountAsync(sequence.Id, group.Count());
        }

        return losers;
    }

    /// <summary>
    /// Deletes the given assignments and drops them from <paramref name="options"/>'s run-scoped cache in the
    /// same call (Unique Value Generation, #242, Phase 2 work package G: page-flush lifecycle reconciliation,
    /// plan "Assignment lifecycle"). The two are done together, rather than leaving the caller to delete and
    /// separately evict, because <see cref="UniqueValueResolveOptions.KnownMetaverseAssignments"/> and
    /// <see cref="UniqueValueResolveOptions.KnownConnectedSystemAssignments"/> are internal to this assembly:
    /// only the service can keep the cache honest, the same way <see cref="CommitAssignmentsAsync"/> is the
    /// only thing that populates it. A caller with nothing to delete pays no repository round trip.
    /// </summary>
    public async Task DeleteAssignmentsAsync(IReadOnlyCollection<Guid> assignmentIds, UniqueValueResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(assignmentIds);
        ArgumentNullException.ThrowIfNull(options);

        if (assignmentIds.Count == 0)
            return;

        await _repository.DeleteGeneratedValueAssignmentsAsync(assignmentIds);

        var idSet = assignmentIds as HashSet<Guid> ?? assignmentIds.ToHashSet();
        EvictDeletedAssignments(options.KnownMetaverseAssignments, idSet);
        EvictDeletedAssignments(options.KnownConnectedSystemAssignments, idSet);
    }

    // ---- Collision Remediation (release 4) ----

    /// <summary>
    /// How many times Collision Remediation may revise one assignment's value over its lifetime (plan decision 15):
    /// past this, a rejection enters Needs Decision rather than drawing yet another value, because a target that keeps
    /// refusing every value JIM issues is telling an administrator something a further rename will not fix. An
    /// administrator's authorised rename is not held back by it.
    /// </summary>
    public const int MaximumRemediations = 5;

    /// <summary>
    /// Draws the next value for an object whose generated value a Connected System has rejected as already in use
    /// (Collision Remediation, release 4; plan decision 8). Unlike <see cref="ResolveAsync"/>, never returns the
    /// object's existing assignment as sticky (that is the value being replaced): it goes straight to candidate
    /// generation, through the same gates, with <see cref="GenerationRequest.RejectedValues"/> taken whatever JIM's
    /// records say. Only the local gates apply in an export run, which has no probe session. A value issued here is
    /// claimed in <paramref name="options"/>'s reservation set under its owner id, exactly as a synchronisation's is.
    /// </summary>
    /// <returns>A <see cref="GenerationOutcomeKind.Generated"/> outcome carrying the new value and an unsaved
    /// assignment for it, or a failure kind when no value could be drawn.</returns>
    public async Task<GenerationOutcome> RegenerateAsync(GenerationRequest request, UniqueValueResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        if (request.StickyOnly)
            return new GenerationOutcome(request, GenerationOutcomeKind.Waiting, null, null, null, null);

        var outcomes = new GenerationOutcome?[1];
        var ownerId = options.DryRun ? Guid.NewGuid() : options.ReservationOwnerId;
        try
        {
            await ResolveGenerationRoundsAsync([request], [0], outcomes, options, ownerId, []);
            return outcomes[0]!;
        }
        finally
        {
            if (options.DryRun)
                options.Reservations.ReleaseAll(ownerId);
        }
    }

    /// <summary>
    /// Whether a generated value a Connected System rejected is anchored (plan decision 10): another participating
    /// Connected System has accepted it for the same object, so revising it would rename an account already in use.
    /// A participating system's Connected System Object joined to <paramref name="metaverseObjectId"/> holding the value
    /// (case-insensitively) in its connector space for the attribute an export mapping flows it to anchors it; an
    /// export accepted by a target is optimistically applied to its Connected System Object (#1079), so a successful
    /// export counts as soon as it happens. A participating system that has not completed a Full Import since its
    /// connector space was cleared cannot say, and missing knowledge never permits a rename. The rejecting system itself
    /// is never asked. Export-mode values are never anchored, so callers only ask for import mode.
    /// </summary>
    /// <param name="metaverseObjectId">The object the value belongs to.</param>
    /// <param name="value">The rejected value.</param>
    /// <param name="numericValue">Its numeric form, for a Number or Long Number attribute; null for Text.</param>
    /// <param name="participatingTargets">The generated mapping's participating targets
    /// (<see cref="GeneratedValueParticipation.ComputeParticipatingTargets"/>: exclusions already removed).</param>
    /// <param name="rejectingConnectedSystemId">The Connected System that rejected the value.</param>
    /// <param name="connectedSystems">The participating Connected Systems, for whether each can tell; one missing
    /// from the map cannot tell.</param>
    public async Task<GeneratedValueAnchoringVerdict> IsAnchoredAsync(
        Guid metaverseObjectId,
        string value,
        long? numericValue,
        IReadOnlyCollection<(int ConnectedSystemId, int AttributeId)> participatingTargets,
        int rejectingConnectedSystemId,
        IReadOnlyDictionary<int, ConnectedSystem> connectedSystems)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(participatingTargets);
        ArgumentNullException.ThrowIfNull(connectedSystems);

        int? cannotTellSystemId = null;
        foreach (var (connectedSystemId, attributeId) in participatingTargets.Where(t => t.ConnectedSystemId != rejectingConnectedSystemId))
        {
            var holders = numericValue.HasValue
                ? await _repository.GetConnectedSystemAttributeNumberHoldersAsync(attributeId, [numericValue.Value])
                : await _repository.GetConnectedSystemAttributeValueHoldersAsync(attributeId, [value.ToLowerInvariant()]);

            // Anchored is definitive, so it is answered before any "cannot tell": a system that does hold the value has
            // told JIM all it needs to know.
            if (holders.Any(h => h.MetaverseObjectId == metaverseObjectId))
                return new GeneratedValueAnchoringVerdict(GeneratedValueAnchoring.Anchored, connectedSystemId);

            if (cannotTellSystemId == null && !CanTell(connectedSystems.GetValueOrDefault(connectedSystemId)))
                cannotTellSystemId = connectedSystemId;
        }

        return cannotTellSystemId.HasValue
            ? new GeneratedValueAnchoringVerdict(GeneratedValueAnchoring.CannotTell, cannotTellSystemId)
            : GeneratedValueAnchoringVerdict.Unanchored;
    }

    /// <summary>
    /// Whether a Connected System's connector space can say if it holds a value: it can unless its connector space was
    /// cleared (which arms the stranded-value sweep, #1605) and no Full Import has completed successfully since.
    /// </summary>
    internal static bool CanTell(ConnectedSystem? connectedSystem)
    {
        if (connectedSystem == null)
            return false;

        if (!connectedSystem.StrandedValueSweepArmedAt.HasValue)
            return true;

        return connectedSystem.LastSuccessfulFullImportCompletedAt.HasValue
               && connectedSystem.LastSuccessfulFullImportCompletedAt.Value > connectedSystem.StrandedValueSweepArmedAt.Value;
    }

    /// <summary>
    /// Puts an assignment into Needs Decision (plan decisions 11 and 12): a rejected value that could not safely be
    /// revised (anchored, anchoring unknown, or remediation exhausted) waits on an administrator, recording which system
    /// rejected it, which anchors it, why it is held, and the execution item that reported it. The caller parks the export.
    /// </summary>
    public async Task EnterNeedsDecisionAsync(
        GeneratedValueAssignment assignment, int rejectedByConnectedSystemId, int? anchoredByConnectedSystemId, Guid executionItemId,
        GeneratedValueNeedsDecisionReason reason)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        assignment.State = GeneratedValueAssignmentState.NeedsDecision;
        assignment.RejectedByConnectedSystemId = rejectedByConnectedSystemId;
        assignment.AnchoredByConnectedSystemId = anchoredByConnectedSystemId;
        assignment.NeedsDecisionEnteredAt = DateTime.UtcNow;
        assignment.NeedsDecisionActivityRunProfileExecutionItemId = executionItemId;
        assignment.NeedsDecisionReason = reason;
        await _repository.UpdateGeneratedValueAssignmentAsync(assignment);
    }

    /// <summary>
    /// "Allow the rename" (plan decision 11): records that an administrator has authorised JIM to rename every system
    /// holding the value, and releases the Needs Decision so the next export run reaches the rejection again, where the
    /// worker performs the rename instead of stopping (the portal cannot probe, so the new value is decided there).
    /// Why and since when the value was held are kept, so the allowed rename still says what was decided about until the
    /// next export spends it. Returns false when the assignment no longer exists or does not need a decision. Records
    /// only the authorisation: the audited Activity is the caller's (<c>GeneratedValueDecisionServer</c>).
    /// </summary>
    public async Task<bool> AuthoriseRenameAsync(Guid assignmentId, string authorisedByName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorisedByName);

        var assignment = await _repository.GetGeneratedValueAssignmentByIdAsync(assignmentId);
        if (assignment is not { State: GeneratedValueAssignmentState.NeedsDecision })
            return false;

        assignment.RenameAuthorised = true;
        assignment.RenameAuthorisedAt = DateTime.UtcNow;
        assignment.RenameAuthorisedByName = authorisedByName;
        await ReleaseNeedsDecisionAsync(assignment, keepDecisionContext: true);
        return true;
    }

    /// <summary>
    /// "Retry" (plan decision 11): releases a Needs Decision so the next export run tries the same value again, for
    /// when the conflict has been fixed where it arose. The assignment returns to Committed and its Parked export to
    /// Pending, with its error count untouched. Returns false when the assignment no longer exists or does not need a
    /// decision.
    /// </summary>
    public async Task<bool> RetryAsync(Guid assignmentId)
    {
        var assignment = await _repository.GetGeneratedValueAssignmentByIdAsync(assignmentId);
        if (assignment is not { State: GeneratedValueAssignmentState.NeedsDecision })
            return false;

        await ReleaseNeedsDecisionAsync(assignment);
        return true;
    }

    /// <summary>
    /// Releases every Needs Decision a generated mapping's assignments are waiting on, for when the mapping's
    /// generation configuration has changed (FR 16), mirroring how a changed initial password configuration releases
    /// its parked accounts (#1121): the change is the administrator's answer, so the next export run tries again under
    /// it. Returns how many assignments were released.
    /// </summary>
    public async Task<int> ReleaseNeedsDecisionForMappingAsync(int syncRuleMappingGenerationId)
    {
        var waiting = (await _repository.GetGeneratedValueAssignmentsForGenerationAsync(syncRuleMappingGenerationId))
            .Where(a => a.State == GeneratedValueAssignmentState.NeedsDecision)
            .ToList();

        foreach (var assignment in waiting)
            await ReleaseNeedsDecisionAsync(assignment);

        return waiting.Count;
    }

    private async Task ReleaseNeedsDecisionAsync(GeneratedValueAssignment assignment, bool keepDecisionContext = false)
    {
        var rejectedBy = assignment.RejectedByConnectedSystemId;

        assignment.State = GeneratedValueAssignmentState.Committed;
        assignment.NeedsDecisionActivityRunProfileExecutionItemId = null;
        if (!keepDecisionContext)
        {
            assignment.NeedsDecisionEnteredAt = null;
            assignment.AnchoredByConnectedSystemId = null;
            assignment.NeedsDecisionReason = null;
        }

        await _repository.UpdateGeneratedValueAssignmentAsync(assignment);

        // The Parked export is the rejecting system's: export mode, the object's own Connected System Object; import
        // mode, the Metaverse Object's account in the system that rejected the value.
        var connectedSystemObjectId = assignment.ConnectedSystemObjectId;
        if (!connectedSystemObjectId.HasValue && assignment.MetaverseObjectId.HasValue && rejectedBy.HasValue)
            connectedSystemObjectId = (await _repository.GetConnectedSystemObjectByMetaverseObjectIdAsync(assignment.MetaverseObjectId.Value, rejectedBy.Value))?.Id;

        if (connectedSystemObjectId.HasValue)
            await _repository.ReleaseParkedPendingExportsAsync([connectedSystemObjectId.Value]);
    }

    /// <summary>
    /// Deletes the given assignments and, in the same statement, retires each value whose generated mapping never
    /// reuses values (Unique Value Generation, #242, Phase 6; plan decision 4, "Assignment lifecycle" row 3): the
    /// page-flush reconciliation's path for a value that is no longer generated because another Attribute Flow took
    /// the attribute over or the value was cleared. Drops the assignments from <paramref name="options"/>'s run cache
    /// in the same call, as <see cref="DeleteAssignmentsAsync"/> does. Returns what was actually retired, so the
    /// caller can record a <c>GeneratedValueRetired</c> outcome for each; a flow with "Never reuse a value" off, or a
    /// value already retired, contributes nothing.
    /// </summary>
    public async Task<IReadOnlyList<GeneratedValueRetirement>> RetireAndDeleteAssignmentsAsync(
        IReadOnlyCollection<Guid> assignmentIds, RetiredGeneratedValueReason reason, Guid? activityId, UniqueValueResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(assignmentIds);
        ArgumentNullException.ThrowIfNull(options);

        if (assignmentIds.Count == 0)
            return [];

        var retired = await _repository.RetireAndDeleteGeneratedValueAssignmentsAsync(assignmentIds, reason, activityId);

        var idSet = assignmentIds as HashSet<Guid> ?? assignmentIds.ToHashSet();
        EvictDeletedAssignments(options.KnownMetaverseAssignments, idSet);
        EvictDeletedAssignments(options.KnownConnectedSystemAssignments, idSet);

        return retired;
    }

    /// <summary>
    /// How many values the retired values register holds for <paramref name="mapping"/>'s target attribute (the
    /// register is per attribute, so this counts what every flow generating it has retired). Zero for a mapping with
    /// no target attribute.
    /// </summary>
    public async Task<int> GetRetiredValueCountAsync(SyncRuleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        int[] mvAttributeIds = mapping.TargetMetaverseAttributeId is { } mvId ? [mvId] : [];
        int[] csAttributeIds = mapping.TargetConnectedSystemAttributeId is { } csId ? [csId] : [];
        if (mvAttributeIds.Length == 0 && csAttributeIds.Length == 0)
            return 0;

        var counts = await _repository.GetRetiredGeneratedValueCountsAsync(mvAttributeIds, csAttributeIds);
        return counts.Sum(c => c.Count);
    }

    /// <summary>
    /// The register's size for every attribute in the two lists that has any entries, in one grouped query: what an
    /// Attribute Flow list loads once per page to show each generated row's "N retired" chip.
    /// </summary>
    public Task<List<RetiredGeneratedValueCount>> GetRetiredValueCountsAsync(IReadOnlyCollection<int> metaverseAttributeIds, IReadOnlyCollection<int> connectedSystemObjectTypeAttributeIds)
        => _repository.GetRetiredGeneratedValueCountsAsync(metaverseAttributeIds, connectedSystemObjectTypeAttributeIds);

    /// <summary>
    /// One window of an attribute's retired values, newest first, optionally narrowed by <paramref name="search"/>
    /// (the value or the holder's name). Exactly one attribute id must be given. A null total means "not counted".
    /// </summary>
    public Task<(List<RetiredGeneratedValueHeader> Items, int? TotalCount)> GetRetiredValuesAsync(
        int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, string? search, int offset, int count, bool includeTotalCount)
        => _repository.GetRetiredGeneratedValueHeadersRangeAsync(metaverseAttributeId, connectedSystemObjectTypeAttributeId, search, offset, count, includeTotalCount);

    /// <summary>
    /// Every value retired from one object, newest first: the retirement events on its change history.
    /// </summary>
    public Task<List<RetiredGeneratedValueHeader>> GetRetiredValuesForObjectAsync(Guid fromObjectId)
        => _repository.GetRetiredGeneratedValueHeadersForObjectAsync(fromObjectId);

    private static void EvictDeletedAssignments(
        ConcurrentDictionary<Guid, ConcurrentBag<GeneratedValueAssignment>> cache, HashSet<Guid> deletedIds)
    {
        // A snapshot (ToList) so the eviction below does not mutate the dictionary while this enumerates it.
        foreach (var (objectId, bag) in cache.ToList().Where(kv => kv.Value.Any(a => deletedIds.Contains(a.Id))))
            cache[objectId] = new ConcurrentBag<GeneratedValueAssignment>(bag.Where(a => !deletedIds.Contains(a.Id)));
    }

    // ---- Sticky ----

    private async Task<Dictionary<int, GeneratedValueAssignment>> LoadStickyAssignmentsAsync(IReadOnlyList<GenerationRequest> requests, UniqueValueResolveOptions options)
    {
        var unknownImportIds = new HashSet<Guid>();
        var unknownExportIds = new HashSet<Guid>();

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            if (request.Mode == GeneratedValueMode.Import && request.MetaverseObjectId.HasValue
                && !options.KnownMetaverseAssignments.ContainsKey(request.MetaverseObjectId.Value))
                unknownImportIds.Add(request.MetaverseObjectId.Value);
            else if (request.Mode == GeneratedValueMode.Export && request.ConnectedSystemObjectId.HasValue
                && !options.KnownConnectedSystemAssignments.ContainsKey(request.ConnectedSystemObjectId.Value))
                unknownExportIds.Add(request.ConnectedSystemObjectId.Value);
        }

        if (unknownImportIds.Count > 0 || unknownExportIds.Count > 0)
            await PrefetchAssignmentsAsync(unknownImportIds, unknownExportIds, options);

        var result = new Dictionary<int, GeneratedValueAssignment>();
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            GeneratedValueAssignment? match = null;

            if (request.Mode == GeneratedValueMode.Import && request.MetaverseObjectId.HasValue
                && options.KnownMetaverseAssignments.TryGetValue(request.MetaverseObjectId.Value, out var importBag))
                match = importBag.FirstOrDefault(a => a.MetaverseAttributeId == request.MetaverseAttributeId);
            else if (request.Mode == GeneratedValueMode.Export && request.ConnectedSystemObjectId.HasValue
                && options.KnownConnectedSystemAssignments.TryGetValue(request.ConnectedSystemObjectId.Value, out var exportBag))
                match = UniqueValueResolveOptions.FindConnectedSystemAssignment(exportBag, request.ConnectedSystemObjectTypeAttributeId);

            if (match != null)
                result[i] = match;
        }

        return result;
    }

    private static GenerationOutcome BuildStickyOutcome(GenerationRequest request, GeneratedValueAssignment assignment) =>
        new(request, GenerationOutcomeKind.Sticky, assignment.Value, TryParseNumeric(request, assignment.Value), assignment, null);

    // ---- Generation rounds ----

    private async Task ResolveGenerationRoundsAsync(
        IReadOnlyList<GenerationRequest> requests,
        List<int> pending,
        GenerationOutcome?[] outcomes,
        UniqueValueResolveOptions options,
        Guid ownerId,
        HashSet<(UniqueValueScope Scope, int AttributeId, string NormalisedValue)> claimedThisCall)
    {
        var attemptsMade = new int[requests.Count];
        var lastCandidate = new string?[requests.Count];
        var lastRejectionGate = new string?[requests.Count];

        // A Sequence request's last drawn number, and the floor it resumes drawing from once a skip over a run of
        // held numbers has found where that run ends (#2031).
        var sequenceNumbers = new long?[requests.Count];
        var sequenceFloors = new long?[requests.Count];

        // The ProbeGate's run of this call (release 3): never under a dry run, since Sync Preview checks the local
        // gates only, and not at all without a session. Answers are cached per (request, target) so an only-if-taken
        // window answers later rounds without another search; the systems that could not answer for each request's
        // current candidate are held until the value is actually issued, when the session is told.
        var probeSession = options.DryRun ? null : options.ProbeSession;
        var probeAnswers = new Dictionary<(int Request, UniquenessProbeTarget Target), Dictionary<string, ProbeAnswer>>();
        var undeterminedSystems = new Dictionary<int, List<int>>();

        // 'active' is the set of requests still to try this round; a request leaves it for good either by
        // getting a terminal outcome (Exhausted, NoBaseValue, WidthExceeded, Generated) or, when a gate rejects
        // its candidate, by carrying forward into next round's 'active' to draw a fresh one.
        var active = pending;

        while (active.Count > 0)
        {
            var withinLimit = new List<int>();
            foreach (var i in active)
            {
                if (attemptsMade[i] >= requests[i].Generation.AttemptLimit)
                    outcomes[i] = BuildExhaustedOutcome(requests[i], lastCandidate[i], lastRejectionGate[i]);
                else
                    withinLimit.Add(i);
            }

            if (withinLimit.Count == 0)
                break;

            // Draw this round's candidate for every still-active request; a terminal outcome here (NoBaseValue,
            // WidthExceeded) is final for that request: it never retries.
            var candidates = new Dictionary<int, (string Text, long? Numeric)>();
            var drawn = new List<int>();

            foreach (var i in withinLimit)
            {
                var request = requests[i];
                var attemptIndex = attemptsMade[i];
                attemptsMade[i]++;

                var (candidate, sequenceNumber, terminal) = await ComputeCandidateAsync(request, attemptIndex, options, sequenceFloors[i]);
                if (terminal != null)
                {
                    outcomes[i] = terminal;
                    continue;
                }

                sequenceNumbers[i] = sequenceNumber;
                lastCandidate[i] = candidate!.Value.Text;
                candidates[i] = candidate.Value;
                undeterminedSystems.Remove(i);
                drawn.Add(i);
            }

            // Gates (a) to (e) in order (reservation, retired, Metaverse or connector-space value, connector space,
            // other live assignments), each narrowing the survivor list; a request a gate rejects is
            // never passed to a later gate this round (the short-circuit the test suite proves), and is instead
            // retried with a fresh candidate next round.
            var survivors = FilterReservationGate(drawn, requests, candidates, options, ownerId, claimedThisCall, lastRejectionGate);
            var reachedRecordGates = survivors;

            if (survivors.Count > 0)
                survivors = await FilterRetiredGateAsync(survivors, requests, candidates, lastRejectionGate);

            if (survivors.Count > 0)
                survivors = await FilterValueGateAsync(survivors, requests, candidates, lastRejectionGate);

            if (survivors.Count > 0)
                survivors = await FilterConnectorSpaceGateAsync(survivors, requests, candidates, lastRejectionGate);

            if (survivors.Count > 0)
                survivors = await FilterOtherAssignmentsGateAsync(survivors, requests, candidates, lastRejectionGate);

            // A Sequence number held in JIM's own records (gates b to e) is usually the start of a run of them, for
            // example after Start again over a population that still holds its numbers: find where the run ends in
            // one lookup, rather than spending an attempt on every number in it (#2031).
            var heldInRecords = reachedRecordGates.Except(survivors)
                .Where(i => requests[i].Generation.TokenKind == GeneratedValueTokenKind.Sequence && sequenceNumbers[i].HasValue)
                .ToList();
            if (heldInRecords.Count > 0)
                await SkipHeldSequenceRunsAsync(heldInRecords, requests, sequenceNumbers, sequenceFloors);

            // The ProbeGate is last, after every local gate, so a candidate JIM already knows is taken never costs a
            // search of a target system (plan Phase 7 item 4).
            if (survivors.Count > 0 && probeSession != null)
                survivors = await FilterProbeGateAsync(survivors, requests, candidates, attemptsMade, probeSession, probeAnswers, undeterminedSystems, lastRejectionGate);

            var rejectedThisRound = drawn.Except(survivors).ToList();

            // Every request still surviving here cleared every gate this round: claim the candidate and
            // finalise the outcome. A request that loses the reservation race here (a genuinely concurrent
            // parallel batch claimed the same value first) simply retries next round too.
            foreach (var i in survivors)
            {
                var request = requests[i];
                var (attributeId, scope) = AttributeAndScope(request);
                var candidate = candidates[i];
                var normalisedValue = candidate.Text.ToLowerInvariant();

                if (!options.Reservations.TryReserve(ownerId, scope, attributeId, normalisedValue))
                {
                    lastRejectionGate[i] = "another object in this run";
                    rejectedThisRound.Add(i);
                    continue;
                }

                claimedThisCall.Add((scope, attributeId, normalisedValue));

                var assignment = BuildAssignment(request, candidate.Text, normalisedValue);
                outcomes[i] = new GenerationOutcome(request, GenerationOutcomeKind.Generated, candidate.Text, candidate.Numeric, assignment, null);

                // Only a value actually issued counts towards the run's "chosen using JIM's own records only" warning.
                if (probeSession != null && undeterminedSystems.Remove(i, out var systemIds))
                {
                    foreach (var systemId in systemIds)
                        probeSession.RecordValueChosenWithoutProbe(systemId);
                }
            }

            active = rejectedThisRound;
        }
    }

    private static List<int> FilterReservationGate(
        List<int> active,
        IReadOnlyList<GenerationRequest> requests,
        Dictionary<int, (string Text, long? Numeric)> candidates,
        UniqueValueResolveOptions options,
        Guid ownerId,
        HashSet<(UniqueValueScope Scope, int AttributeId, string NormalisedValue)> claimedThisCall,
        string?[] lastRejectionGate)
    {
        var survivors = new List<int>(active.Count);
        foreach (var i in active)
        {
            var (attributeId, scope) = AttributeAndScope(requests[i]);
            var normalisedValue = candidates[i].Text.ToLowerInvariant();

            // Collision Remediation (release 4): a value a target has just refused is taken, whatever JIM's own records
            // say; that is the whole of what the rejection told JIM.
            if (requests[i].RejectedValues.Contains(candidates[i].Text, StringComparer.OrdinalIgnoreCase))
            {
                lastRejectionGate[i] = "the Connected System that rejected it";
                continue;
            }

            if (options.Reservations.IsReservedByAnotherOwner(ownerId, scope, attributeId, normalisedValue)
                || claimedThisCall.Contains((scope, attributeId, normalisedValue)))
            {
                lastRejectionGate[i] = "another object in this run";
                continue;
            }

            survivors.Add(i);
        }

        return survivors;
    }

    /// <summary>
    /// Gate (b), the retired gate (Unique Value Generation, #242, Phase 6; plan decision 4): a value in the
    /// attribute's retired values register is taken, case-insensitively, for a flow that never reuses values
    /// ("Never reuse a value" on, or a Sequence token, which always never reuses). A flow with the switch off skips
    /// the gate, so a rehire may receive a previous holder's value; that is the switch's whole purpose. Batched per
    /// attribute (one query per attribute per round, whichever objects ask), and never excludes the requesting
    /// object: a retired value belongs to nobody, so not even the object that once held it gets it back.
    /// </summary>
    private async Task<List<int>> FilterRetiredGateAsync(
        List<int> active,
        IReadOnlyList<GenerationRequest> requests,
        Dictionary<int, (string Text, long? Numeric)> candidates,
        string?[] lastRejectionGate)
    {
        var taken = new HashSet<int>();

        foreach (var group in active.Where(i => NeverReuses(requests[i].Generation)).GroupBy(i => (requests[i].Mode, AttributeAndScope(requests[i]).AttributeId)))
        {
            var values = group.Select(i => candidates[i].Text.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var mvAttributeId = group.Key.Mode == GeneratedValueMode.Import ? group.Key.AttributeId : (int?)null;
            var csAttributeId = group.Key.Mode == GeneratedValueMode.Export ? group.Key.AttributeId : (int?)null;

            var retired = await _repository.GetRetiredGeneratedValuesInUseAsync(mvAttributeId, csAttributeId, values);

            // taken.Add doubles as the predicate and the record of the rejection.
            foreach (var i in group.Where(i => retired.Contains(candidates[i].Text.ToLowerInvariant())).Where(taken.Add))
                lastRejectionGate[i] = "the retired values register (it was issued before and is never reused)";
        }

        return active.Where(i => !taken.Contains(i)).ToList();
    }

    /// <summary>
    /// Whether a generated mapping never reuses a value: "Never reuse a value" on, or a Sequence token, whose numbers
    /// are never reused whatever the stored switch says (plan decision 4).
    /// </summary>
    internal static bool NeverReuses(SyncRuleMappingGeneration generation) =>
        generation.NeverReuse || generation.TokenKind == GeneratedValueTokenKind.Sequence;

    /// <summary>
    /// Gate (c): batched per (attribute, excluded object id) so that, in the common case of a page of brand new
    /// objects sharing <c>excludingId = null</c>, every request against one attribute costs a single query.
    /// </summary>
    private async Task<List<int>> FilterValueGateAsync(
        List<int> active,
        IReadOnlyList<GenerationRequest> requests,
        Dictionary<int, (string Text, long? Numeric)> candidates,
        string?[] lastRejectionGate)
    {
        var taken = new HashSet<int>();
        var isNumberTarget = active.ToDictionary(i => i, i => requests[i].TargetType is AttributeDataType.Number or AttributeDataType.LongNumber);

        foreach (var group in active.Where(i => !isNumberTarget[i]).GroupBy(i => (AttributeAndScope(requests[i]).AttributeId, ExcludingObjectId(requests[i]))))
        {
            // Values must arrive already lower-cased: the repository's expression index is on the lower-cased
            // stored value, and does not lower the query's own array a second time.
            var values = group.Select(i => candidates[i].Text.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var mode = requests[group.First()].Mode;
            var takenValues = mode == GeneratedValueMode.Import
                ? await _repository.GetMetaverseAttributeValuesInUseAsync(group.Key.AttributeId, values, group.Key.Item2)
                : await _repository.GetConnectedSystemAttributeValuesInUseAsync(group.Key.AttributeId, values, group.Key.Item2);

            foreach (var i in group)
            {
                if (!takenValues.Contains(candidates[i].Text))
                    continue;
                taken.Add(i);
                lastRejectionGate[i] = mode == GeneratedValueMode.Import ? "another Metaverse Object" : "a Connected System Object";
            }
        }

        foreach (var group in active.Where(i => isNumberTarget[i]).GroupBy(i => (AttributeAndScope(requests[i]).AttributeId, ExcludingObjectId(requests[i]))))
        {
            var values = group.Select(i => candidates[i].Numeric!.Value).Distinct().ToList();
            var mode = requests[group.First()].Mode;
            var takenValues = mode == GeneratedValueMode.Import
                ? await _repository.GetMetaverseAttributeNumbersInUseAsync(group.Key.AttributeId, values, group.Key.Item2)
                : await _repository.GetConnectedSystemAttributeNumbersInUseAsync(group.Key.AttributeId, values, group.Key.Item2);

            foreach (var i in group)
            {
                if (!takenValues.Contains(candidates[i].Numeric!.Value))
                    continue;
                taken.Add(i);
                lastRejectionGate[i] = mode == GeneratedValueMode.Import ? "another Metaverse Object" : "a Connected System Object";
            }
        }

        return active.Where(i => !taken.Contains(i)).ToList();
    }

    /// <summary>
    /// Gate (d), import mode only: every id in <see cref="GenerationRequest.ConnectorSpaceAttributeIds"/> (plan
    /// "Behaviour (ResolveAsync)"): a value any participating target already holds is taken, EXCEPT one held by
    /// the requesting object's own account. That is the same person, not a collision, so the object gets the
    /// value exactly as an ordinary Attribute Flow would write it (product-owner decision 2026-10-01). An account
    /// is the object's own when it is joined to it in memory this pass
    /// (<see cref="GenerationRequest.OwnConnectedSystemObjectIds"/>, which covers a join or projection not saved
    /// yet, including a brand-new Metaverse Object with no id) or its saved join names the object, unless it is
    /// <see cref="GenerationRequest.DisconnectingConnectedSystemObjectId"/>. One holder lookup per attribute
    /// serves the whole batch; the per-request decision is made in memory.
    /// </summary>
    private async Task<List<int>> FilterConnectorSpaceGateAsync(
        List<int> active,
        IReadOnlyList<GenerationRequest> requests,
        Dictionary<int, (string Text, long? Numeric)> candidates,
        string?[] lastRejectionGate)
    {
        var relevant = active.Where(i => requests[i].Mode == GeneratedValueMode.Import && requests[i].ConnectorSpaceAttributeIds.Count > 0).ToList();
        if (relevant.Count == 0)
            return active;

        var taken = new HashSet<int>();
        var isNumberTarget = relevant.ToDictionary(i => i, i => requests[i].TargetType is AttributeDataType.Number or AttributeDataType.LongNumber);

        var stringByAttribute = new Dictionary<int, List<int>>();
        var numberByAttribute = new Dictionary<int, List<int>>();
        foreach (var i in relevant)
        {
            var byAttribute = isNumberTarget[i] ? numberByAttribute : stringByAttribute;
            foreach (var csAttributeId in requests[i].ConnectorSpaceAttributeIds)
            {
                if (!byAttribute.TryGetValue(csAttributeId, out var list))
                    byAttribute[csAttributeId] = list = [];
                list.Add(i);
            }
        }

        foreach (var (csAttributeId, indices) in stringByAttribute)
        {
            var values = indices.Select(i => candidates[i].Text.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var holdersByValue = (await _repository.GetConnectedSystemAttributeValueHoldersAsync(csAttributeId, values))
                .ToLookup(h => h.NormalisedValue!, StringComparer.OrdinalIgnoreCase);
            // taken.Add doubles as the predicate and the dedup record (a request can reach this gate through
            // more than one participating attribute).
            foreach (var i in indices.Where(i => holdersByValue[candidates[i].Text.ToLowerInvariant()].Any(h => !IsOwnAccount(requests[i], h))).Where(taken.Add))
                lastRejectionGate[i] = "a Connected System Object";
        }

        foreach (var (csAttributeId, indices) in numberByAttribute)
        {
            var values = indices.Select(i => candidates[i].Numeric!.Value).Distinct().ToList();
            var holdersByValue = (await _repository.GetConnectedSystemAttributeNumberHoldersAsync(csAttributeId, values))
                .ToLookup(h => h.NumberValue!.Value);
            // taken.Add doubles as the predicate and the dedup record (a request can reach this gate through
            // more than one participating attribute).
            foreach (var i in indices.Where(i => holdersByValue[candidates[i].Numeric!.Value].Any(h => !IsOwnAccount(requests[i], h))).Where(taken.Add))
                lastRejectionGate[i] = "a Connected System Object";
        }

        return active.Where(i => !taken.Contains(i)).ToList();
    }

    /// <summary>
    /// Whether <paramref name="holder"/> is the requesting object's own account for the connector-space gate:
    /// joined to it in memory this pass, or by a saved join, and not leaving it this pass.
    /// </summary>
    private static bool IsOwnAccount(GenerationRequest request, ConnectorSpaceValueHolder holder) =>
        IsOwnAccount(request, holder.ConnectedSystemObjectId, holder.MetaverseObjectId);

    private static bool IsOwnAccount(GenerationRequest request, Guid connectedSystemObjectId, Guid? joinedMetaverseObjectId)
    {
        if (connectedSystemObjectId == request.DisconnectingConnectedSystemObjectId)
            return false;

        return request.OwnConnectedSystemObjectIds.Contains(connectedSystemObjectId)
            || (request.MetaverseObjectId.HasValue && joinedMetaverseObjectId == request.MetaverseObjectId);
    }

    /// <summary>
    /// Gate (e): every other object's live assignment for the same attribute, batched per (attribute, excluding
    /// id) over the targeted, indexed <see cref="ISyncRepository.GetGeneratedValueAssignmentValuesInUseAsync"/>
    /// read; scoped by attribute rather than by which generation row asked, since two different generation rows
    /// can target the same attribute (decision 3) and must not be able to issue it the same value twice.
    /// </summary>
    private async Task<List<int>> FilterOtherAssignmentsGateAsync(
        List<int> active,
        IReadOnlyList<GenerationRequest> requests,
        Dictionary<int, (string Text, long? Numeric)> candidates,
        string?[] lastRejectionGate)
    {
        var taken = new HashSet<int>();

        foreach (var group in active.GroupBy(i => (AttributeAndScope(requests[i]).AttributeId, requests[i].Mode, ExcludingObjectId(requests[i]))))
        {
            var values = group.Select(i => candidates[i].Text.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var mvAttributeId = group.Key.Mode == GeneratedValueMode.Import ? group.Key.AttributeId : (int?)null;
            var csAttributeId = group.Key.Mode == GeneratedValueMode.Export ? group.Key.AttributeId : (int?)null;

            var takenValues = await _repository.GetGeneratedValueAssignmentValuesInUseAsync(mvAttributeId, csAttributeId, values, group.Key.Item3);

            foreach (var i in group)
            {
                var normalisedValue = candidates[i].Text.ToLowerInvariant();
                if (!takenValues.Contains(normalisedValue))
                    continue;

                taken.Add(i);
                lastRejectionGate[i] = "another generated value assignment";
            }
        }

        return active.Where(i => !taken.Contains(i)).ToList();
    }

    /// <summary>
    /// What a probe said about one candidate for one target: the outcome, whether the target was probed at all, and
    /// the Connected System's name for rejection text.
    /// </summary>
    private readonly record struct ProbeAnswer(UniquenessProbeOutcome Outcome, bool IsProbed, string ConnectedSystemName);

    /// <summary>
    /// Gate (f), the ProbeGate (release 3, plan Phase 7 item 4): asks each of a request's
    /// <see cref="GenerationRequest.ProbeTargets"/>, in order, whether the target system already holds the candidate.
    /// <see cref="UniquenessProbeOutcome.Found"/> rejects it, naming the Connected System;
    /// <see cref="UniquenessProbeOutcome.NotFound"/> and <see cref="UniquenessProbeOutcome.CouldNotDetermine"/> accept
    /// it, the latter noted against the request so the session hears of it once the value is issued. A candidate in
    /// <see cref="GenerationRequest.ProbeExemptValues"/> is accepted without a probe.
    /// </summary>
    private static async Task<List<int>> FilterProbeGateAsync(
        List<int> active,
        IReadOnlyList<GenerationRequest> requests,
        Dictionary<int, (string Text, long? Numeric)> candidates,
        int[] attemptsMade,
        IUniquenessProbeSession session,
        Dictionary<(int Request, UniquenessProbeTarget Target), Dictionary<string, ProbeAnswer>> answers,
        Dictionary<int, List<int>> undeterminedSystems,
        string?[] lastRejectionGate)
    {
        var survivors = new List<int>(active.Count);
        foreach (var i in active)
        {
            var request = requests[i];
            var candidate = candidates[i].Text;
            string? foundIn = null;
            List<int>? undeterminedHere = null;

            var targets = IsProbeExempt(request, candidate) ? [] : request.ProbeTargets;
            foreach (var target in targets)
            {
                // attemptsMade has already been advanced past this round's draw.
                var answer = await GetProbeAnswerAsync(i, request, target, candidate, attemptsMade[i] - 1, session, answers);
                if (answer.Outcome == UniquenessProbeOutcome.Found)
                {
                    foundIn = answer.ConnectedSystemName;
                    break;
                }

                if (answer.Outcome == UniquenessProbeOutcome.CouldNotDetermine && answer.IsProbed)
                    (undeterminedHere ??= []).Add(target.ConnectedSystemId);
            }

            if (foundIn != null)
            {
                lastRejectionGate[i] = $"an account in {foundIn}";
                continue;
            }

            if (undeterminedHere != null)
                undeterminedSystems[i] = undeterminedHere;

            survivors.Add(i);
        }

        return survivors;
    }

    /// <summary>
    /// The answer for one candidate against one target, from the request's cached window when an earlier round's
    /// batch already covered it, otherwise from a new batch (<see cref="BuildProbeBatch"/>), whose every answer is
    /// cached for later rounds.
    /// </summary>
    private static async Task<ProbeAnswer> GetProbeAnswerAsync(
        int requestIndex,
        GenerationRequest request,
        UniquenessProbeTarget target,
        string candidate,
        int attemptIndex,
        IUniquenessProbeSession session,
        Dictionary<(int Request, UniquenessProbeTarget Target), Dictionary<string, ProbeAnswer>> answers)
    {
        if (!answers.TryGetValue((requestIndex, target), out var known))
            answers[(requestIndex, target)] = known = new Dictionary<string, ProbeAnswer>(StringComparer.OrdinalIgnoreCase);

        if (known.TryGetValue(candidate, out var cached))
            return cached;

        var batch = BuildProbeBatch(request, candidate, attemptIndex);
        var result = await session.ProbeAsync(target, batch);

        // A session answers one outcome per candidate. Should one ever not, nothing it said can be matched to a
        // candidate, so the whole batch is treated as unanswered rather than guessed at.
        for (var k = 0; k < batch.Count; k++)
        {
            var outcome = result.Outcomes.Count == batch.Count ? result.Outcomes[k] : UniquenessProbeOutcome.CouldNotDetermine;
            known[batch[k]] = new ProbeAnswer(outcome, result.IsProbed, result.ConnectedSystemName);
        }

        return known[candidate];
    }

    /// <summary>
    /// The candidates one probe batch carries (plan decision 16, revised 2026-10-05): for an only-if-taken token, the
    /// object's lazy window, the current candidate and the ones after it, up to
    /// <see cref="UniquenessProbeRequest.MaximumCandidates"/> and never past the attempt limit, skipping exempt values;
    /// for a sequence or random token, the drawn candidate alone, since drawing ahead would consume sequence numbers
    /// the run then never issues.
    /// </summary>
    private static List<string> BuildProbeBatch(GenerationRequest request, string candidate, int attemptIndex)
    {
        var batch = new List<string> { candidate };
        var generation = request.Generation;

        if (generation.TokenKind != GeneratedValueTokenKind.OnlyIfTaken || string.IsNullOrWhiteSpace(request.BaseValue))
            return batch;

        for (var attempt = attemptIndex + 1; attempt < generation.AttemptLimit && batch.Count < UniquenessProbeRequest.MaximumCandidates; attempt++)
        {
            var next = UniqueValueCandidates.OnlyIfTakenCandidate(request.BaseValue, attempt, generation.SuffixStyle, generation.SuffixStart, generation.Separator);
            if (!IsProbeExempt(request, next) && !batch.Contains(next, StringComparer.OrdinalIgnoreCase))
                batch.Add(next);
        }

        return batch;
    }

    private static bool IsProbeExempt(GenerationRequest request, string candidate) =>
        request.ProbeExemptValues.Contains(candidate, StringComparer.OrdinalIgnoreCase);

    // ---- Held sequence runs (#2031) ----

    /// <summary>
    /// For each Sequence request whose number was held in JIM's own records this round, sets the floor it resumes
    /// drawing from: the first number above the one it drew that nothing holds against it. One lookup per group of
    /// requests that read the same run (same attribute, placement and step) serves the whole group, whatever the
    /// run's length; each request then applies the gates' own-value rules in memory, so a number held only by its own
    /// value, assignment or account stops the skip there, exactly as the gates would have let it through. The floor is
    /// where drawing resumes, never a value to issue: the gates still check whatever the sequence draws next.
    /// </summary>
    private async Task SkipHeldSequenceRunsAsync(
        List<int> heldInRecords,
        IReadOnlyList<GenerationRequest> requests,
        long?[] sequenceNumbers,
        long?[] sequenceFloors)
    {
        foreach (var group in heldInRecords.GroupBy(i => HeldRunKey(requests[i], sequenceNumbers[i]!.Value)))
        {
            var from = group.Min(i => NextAfter(requests[i], sequenceNumbers[i]!.Value));
            var run = await _repository.GetSequenceHeldRunAsync(BuildSequenceSkipQuery(requests[group.First()], from));

            var holdersByNumber = run.Holders.ToLookup(h => h.Number);
            var holdersByObject = run.Holders.Where(h => h.ObjectId.HasValue).ToLookup(h => h.ObjectId!.Value);
            var holdersByJoinedObject = run.Holders.Where(h => h.MetaverseObjectId.HasValue).ToLookup(h => h.MetaverseObjectId!.Value);

            foreach (var i in group)
            {
                var floor = FirstNumberFreeFor(requests[i], NextAfter(requests[i], sequenceNumbers[i]!.Value), run, holdersByNumber, holdersByObject, holdersByJoinedObject);
                sequenceFloors[i] = Math.Max(sequenceFloors[i] ?? floor, floor);
            }
        }
    }

    /// <summary>
    /// The first number at or above <paramref name="from"/> that is free for <paramref name="request"/>: a number in
    /// the run whose every holding is the request's own (its own value or assignment, or one of its own accounts),
    /// or else the first number above the run. Only the request's own holdings are examined, through the lookups, so
    /// the cost per request does not grow with the run's length.
    /// </summary>
    private static long FirstNumberFreeFor(
        GenerationRequest request,
        long from,
        SequenceHeldRun run,
        ILookup<long, SequenceNumberHolder> holdersByNumber,
        ILookup<Guid, SequenceNumberHolder> holdersByObject,
        ILookup<Guid, SequenceNumberHolder> holdersByJoinedObject)
    {
        // Above the run this lookup describes (it began lower, for another request in the group): nothing is known
        // about this request's next number beyond that it is the next one to try.
        if (from >= run.FirstFreeNumber)
            return from;

        var ownObjectIds = new List<Guid>(request.OwnConnectedSystemObjectIds);
        if (ExcludingObjectId(request) is { } excludingObjectId)
            ownObjectIds.Add(excludingObjectId);

        var ownHoldings = ownObjectIds.SelectMany(id => holdersByObject[id]);
        if (request.Mode == GeneratedValueMode.Import && request.MetaverseObjectId.HasValue)
            ownHoldings = ownHoldings.Concat(holdersByJoinedObject[request.MetaverseObjectId.Value]);

        var increment = request.Generation.SequenceIncrement;
        var heldOnlyByItself = ownHoldings
            .Where(h => h.Number >= from && (h.Number - from) % increment == 0 && IsOwnHolding(request, h))
            .Select(h => h.Number)
            .Distinct()
            .Order()
            .Where(n => holdersByNumber[n].All(h => IsOwnHolding(request, h)))
            .Select(n => (long?)n)
            .FirstOrDefault();

        return heldOnlyByItself ?? run.FirstFreeNumber;
    }

    /// <summary>
    /// Whether a holding of a sequence number is <paramref name="request"/>'s own, by the same rules the gates apply:
    /// the value and assignment gates exclude the requesting object, the connector-space gate its own accounts, and
    /// the retired gate nobody.
    /// </summary>
    private static bool IsOwnHolding(GenerationRequest request, SequenceNumberHolder holder) => holder.Kind switch
    {
        SequenceNumberHolderKind.AttributeValue or SequenceNumberHolderKind.Assignment =>
            holder.ObjectId.HasValue && holder.ObjectId == ExcludingObjectId(request),
        SequenceNumberHolderKind.ConnectorSpace =>
            holder.ObjectId.HasValue && IsOwnAccount(request, holder.ObjectId.Value, holder.MetaverseObjectId),
        _ => false
    };

    private static long NextAfter(GenerationRequest request, long number) => number + request.Generation.SequenceIncrement;

    /// <summary>
    /// Requests sharing this key read the same run: the same attribute and mode, the same placement around the
    /// number, the same step and the same position within it (so the run's numbers are numbers each would draw), and
    /// the same participating target attributes.
    /// </summary>
    private static string HeldRunKey(GenerationRequest request, long number)
    {
        var query = BuildSequenceSkipQuery(request, number);
        var increment = request.Generation.SequenceIncrement;
        var position = ((number % increment) + increment) % increment;
        return string.Join('|',
            request.Mode, AttributeAndScope(request).AttributeId, query.Prefix, query.Suffix, query.FixedWidth, query.NumericTarget,
            increment, position, string.Join(',', query.ConnectorSpaceAttributeIds.Order()));
    }

    private static SequenceSkipQuery BuildSequenceSkipQuery(GenerationRequest request, long from)
    {
        var generation = request.Generation;
        var (prefix, suffix) = UniqueValueCandidates.SplitPlacement(request.BaseValue, generation.Separator);
        var importMode = request.Mode == GeneratedValueMode.Import;

        return new SequenceSkipQuery
        {
            MetaverseAttributeId = importMode ? request.MetaverseAttributeId : null,
            ConnectedSystemObjectTypeAttributeId = importMode ? null : request.ConnectedSystemObjectTypeAttributeId,
            From = from,
            Increment = generation.SequenceIncrement,
            NumericTarget = request.TargetType is AttributeDataType.Number or AttributeDataType.LongNumber,
            Prefix = prefix.ToLowerInvariant(),
            Suffix = suffix.ToLowerInvariant(),
            FixedWidth = generation.FixedWidth,
            ConnectorSpaceAttributeIds = importMode ? request.ConnectorSpaceAttributeIds : []
        };
    }

    // ---- Candidate generation ----

    /// <summary>
    /// Draws the candidate for <paramref name="request"/>'s attempt <paramref name="attemptIndex"/>, or the terminal
    /// outcome that ends it. A Sequence also returns the number it drew, and draws at or above
    /// <paramref name="sequenceFloor"/> once a skip over held numbers has set one (#2031).
    /// </summary>
    private async Task<((string Text, long? Numeric)? Candidate, long? SequenceNumber, GenerationOutcome? Terminal)> ComputeCandidateAsync(
        GenerationRequest request, int attemptIndex, UniqueValueResolveOptions options, long? sequenceFloor)
    {
        var generation = request.Generation;
        var isNumberTarget = request.TargetType is AttributeDataType.Number or AttributeDataType.LongNumber;

        switch (generation.TokenKind)
        {
            case GeneratedValueTokenKind.OnlyIfTaken:
            {
                if (string.IsNullOrWhiteSpace(request.BaseValue))
                    return (null, null, NoBaseValue(request));

                var text = UniqueValueCandidates.OnlyIfTakenCandidate(
                    request.BaseValue, attemptIndex, generation.SuffixStyle, generation.SuffixStart, generation.Separator);
                return ((text, null), null, null);
            }

            case GeneratedValueTokenKind.Sequence:
            {
                var floor = sequenceFloor.HasValue ? Math.Max(generation.SequenceStart, sequenceFloor.Value) : generation.SequenceStart;
                var number = await SequenceAllocator.NextNumberAsync(
                    _repository, options, options.DryRun,
                    request.MetaverseAttributeId, request.ConnectedSystemObjectTypeAttributeId,
                    floor, generation.SequenceIncrement);

                var (rendered, widthExceeded) = UniqueValueCandidates.RenderSequenceNumber(number, generation.FixedWidth, generation.OnWidthExceeded);
                if (widthExceeded)
                    return (null, null, WidthExceeded(request, rendered, generation.FixedWidth!.Value));

                var text = UniqueValueCandidates.Place(request.BaseValue, rendered, generation.Separator);
                var numeric = isNumberTarget ? number : (long?)null;
                return ((text, numeric), number, null);
            }

            case GeneratedValueTokenKind.Random:
            {
                var forceNonZeroLeadingDigit = isNumberTarget && generation.RandomFormat == GeneratedValueRandomFormat.Digits;
                var token = UniqueValueCandidates.GenerateRandomToken(generation.RandomFormat, generation.RandomLength, forceNonZeroLeadingDigit);
                var text = UniqueValueCandidates.Place(request.BaseValue, token, generation.Separator);
                var numeric = isNumberTarget && generation.RandomFormat == GeneratedValueRandomFormat.Digits
                    ? long.Parse(token, CultureInfo.InvariantCulture)
                    : (long?)null;
                return ((text, numeric), null, null);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(request), generation.TokenKind, "Unrecognised token kind.");
        }
    }

    // ---- Failure builders ----

    private static GenerationOutcome NoBaseValue(GenerationRequest request) =>
        new(request, GenerationOutcomeKind.NoBaseValue, null, null, null,
            $"{request.AttributeName} could not be generated: \"Only if taken\" needs a base value to append its collision suffix to, and none was supplied.");

    private static GenerationOutcome WidthExceeded(GenerationRequest request, string candidate, int fixedWidth) =>
        new(request, GenerationOutcomeKind.WidthExceeded, null, null, null,
            $"The next value for {request.AttributeName} would be \"{candidate}\", which no longer fits the configured fixed width of {fixedWidth} digits.");

    private static GenerationOutcome BuildExhaustedOutcome(GenerationRequest request, string? lastCandidate, string? lastRejectionGate)
    {
        var candidateText = lastCandidate ?? "(no candidate was tried)";
        var gateText = lastRejectionGate ?? "another object";
        var message = $"No free value was found for {request.AttributeName} after {request.Generation.AttemptLimit} attempts; " +
                      $"the last candidate, \"{candidateText}\", is already held by {gateText}.";
        return new GenerationOutcome(request, GenerationOutcomeKind.Exhausted, null, null, null, message);
    }

    // ---- Shared helpers ----

    private static (int AttributeId, UniqueValueScope Scope) AttributeAndScope(GenerationRequest request) =>
        request.Mode == GeneratedValueMode.Import
            ? (request.MetaverseAttributeId!.Value, UniqueValueScope.MetaverseAttribute)
            : (request.ConnectedSystemObjectTypeAttributeId!.Value, UniqueValueScope.ConnectedSystemAttribute);

    private static Guid? ExcludingObjectId(GenerationRequest request) =>
        request.Mode == GeneratedValueMode.Import ? request.MetaverseObjectId : request.ConnectedSystemObjectId;

    private static long? TryParseNumeric(GenerationRequest request, string value) =>
        request.TargetType is AttributeDataType.Number or AttributeDataType.LongNumber
        && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
            ? numeric
            : null;

    private static GeneratedValueAssignment BuildAssignment(GenerationRequest request, string value, string normalisedValue)
    {
        var now = DateTime.UtcNow;
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            Value = value,
            NormalisedValue = normalisedValue,
            BaseValue = string.IsNullOrWhiteSpace(request.BaseValue) ? null : request.BaseValue,
            State = GeneratedValueAssignmentState.Proposed,
            SyncRuleMappingGenerationId = request.Generation.Id,
            Created = now,
            LastUpdated = now
        };

        if (request.Mode == GeneratedValueMode.Import)
        {
            assignment.MetaverseObjectId = request.MetaverseObjectId;
            assignment.MetaverseAttributeId = request.MetaverseAttributeId;
        }
        else
        {
            assignment.ConnectedSystemObjectId = request.ConnectedSystemObjectId;
            assignment.ConnectedSystemObjectTypeAttributeId = request.ConnectedSystemObjectTypeAttributeId;
        }

        return assignment;
    }

    // ---- Configuration and Metaverse Object surfaces (#242, Phase 3) ----

    /// <summary>
    /// The read-only counter state behind the portal, REST and PowerShell surfaces' "next number" preview
    /// (plan Phase 3 point 4: a read method callable BEFORE saving a raised <see cref="SyncRuleMappingGeneration.SequenceStart"/>).
    /// Allocates nothing: <paramref name="mapping"/>'s target attribute's counter is read and, when it has never
    /// been seeded, the seed is computed the same way <see cref="SequenceAllocator"/> would (the higher of the
    /// flow's start value and the attribute's highest existing numeric value plus the increment) without
    /// reserving it. Returns null for a mapping that is not a generated Sequence mapping: the state has no
    /// meaning for any other token kind.
    /// </summary>
    public async Task<GeneratedValueSequenceState?> GetSequenceStateAsync(SyncRuleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var generation = mapping.Generation;
        if (generation == null || generation.TokenKind != GeneratedValueTokenKind.Sequence)
            return null;

        var mvAttributeId = mapping.TargetMetaverseAttributeId;
        var csAttributeId = mapping.TargetConnectedSystemAttributeId;
        var attributeName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";

        var existing = await _repository.GetGeneratedValueSequenceAsync(mvAttributeId, csAttributeId);

        long nextNumber;
        long assignedCount;
        bool isSeeded;

        if (existing != null)
        {
            nextNumber = Math.Max(existing.NextValue, generation.SequenceStart);
            assignedCount = existing.AssignedCount;
            isSeeded = true;
        }
        else
        {
            var highest = await _repository.GetHighestNumericValueForAttributeAsync(mvAttributeId, csAttributeId);
            nextNumber = highest.HasValue
                ? Math.Max(generation.SequenceStart, highest.Value + generation.SequenceIncrement)
                : generation.SequenceStart;
            assignedCount = 0;
            isSeeded = false;
        }

        var (formatted, widthExceeded) = UniqueValueCandidates.RenderSequenceNumber(nextNumber, generation.FixedWidth, generation.OnWidthExceeded);

        return new GeneratedValueSequenceState
        {
            AttributeName = attributeName,
            NextNumber = nextNumber,
            NextNumberFormatted = formatted,
            NextNumberWidthExceeded = widthExceeded,
            AssignedCount = assignedCount,
            IsSeeded = isSeeded
        };
    }

    /// <summary>
    /// The committed generated values a Metaverse Object currently holds (plan Phase 3 point 2:
    /// <c>GetGeneratedValuesForMetaverseObjectAsync</c>), for the object's own detail page. A thin passthrough:
    /// all the joining and denormalisation happens in the repository projection.
    /// </summary>
    public Task<List<GeneratedValueAssignmentHeader>> GetAssignmentsForMetaverseObjectAsync(Guid metaverseObjectId)
        => _repository.GetGeneratedValueAssignmentHeadersForMetaverseObjectAsync(metaverseObjectId);

    /// <summary>
    /// How many Metaverse Objects of <paramref name="metaverseObjectTypeId"/>, joined to a Connected System
    /// Object of <paramref name="connectedSystemId"/>, currently hold no value for
    /// <paramref name="metaverseAttributeId"/> (plan Phase 3 point 2: the form's "N existing objects would
    /// receive a value" preview line). Takes the ids directly rather than a mapping, so it answers for a
    /// generated mapping an administrator is still composing and has not saved yet.
    /// </summary>
    public Task<int> CountObjectsAwaitingValueAsync(int metaverseObjectTypeId, int connectedSystemId, int metaverseAttributeId)
        => _repository.CountMetaverseObjectsAwaitingGeneratedValueAsync(metaverseObjectTypeId, connectedSystemId, metaverseAttributeId);

    /// <summary>
    /// Applies plan decision 3's save-time counter move: when <paramref name="mapping"/> is a generated Sequence
    /// mapping whose configured <see cref="SyncRuleMappingGeneration.SequenceStart"/> stands above the target
    /// attribute's counter, raises the counter to that value and returns the move so the caller can report it.
    /// A no-op (returns null) for every other token kind, for a start value at or below the counter's current
    /// position, and when the counter has never been seeded (nothing to skip ahead from yet).
    /// </summary>
    public async Task<SequenceSkippedAhead?> RaiseSequenceStartIfHigherAsync(SyncRuleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var generation = mapping.Generation;
        if (generation == null || generation.TokenKind != GeneratedValueTokenKind.Sequence)
            return null;

        var previous = await _repository.RaiseGeneratedValueSequenceIfHigherAsync(
            mapping.TargetMetaverseAttributeId, mapping.TargetConnectedSystemAttributeId, generation.SequenceStart, mapping.Id);

        return previous.HasValue ? new SequenceSkippedAhead(previous.Value, generation.SequenceStart) : null;
    }

    /// <summary>
    /// Hands back the sequence numbers the run reserved and never drew (#2044): for each attribute's latest block, moves
    /// the counter back to the first undrawn number, so the next run carries on from there instead of from the end of
    /// the block. Call once, when the run has finished drawing; the undrawn numbers are discarded from
    /// <paramref name="options"/> first, so nothing can be drawn from them afterwards. Each hand-back is a
    /// compare-and-swap that only applies while nothing else has moved the counter since the reservation (another
    /// run's reservation, a raised start, Start again); otherwise the tail simply stays a gap. Numbers drawn and not
    /// issued (a gate rejected them, or the object failed) are never handed back. A no-op under
    /// <see cref="UniqueValueResolveOptions.DryRun"/>, which reserves nothing.
    /// </summary>
    /// <returns>How many numbers were handed back.</returns>
    public async Task<long> ReturnUnusedSequenceNumbersAsync(UniqueValueResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.DryRun)
            return 0;

        // Only the latest block can have an undrawn tail: a queue is refilled only once it is empty.
        var tails = options.SequenceReservations
            .Select(reservation => (reservation.Key, Block: reservation.Value, Queue: options.SequenceBlocks.GetValueOrDefault(reservation.Key)))
            .Where(tail => tail.Queue is { IsEmpty: false })
            .ToList();

        long returned = 0;
        foreach (var (key, block, queue) in tails)
        {
            // A snapshot in queue order, so its first number is the first undrawn one; cleared at once so nothing is
            // drawn from numbers that are about to belong to the counter again.
            var undrawn = queue!.ToArray();
            queue.Clear();

            if (await _repository.ReturnUnusedGeneratedValueSequenceNumbersAsync(key.MetaverseAttributeId, key.ConnectedSystemObjectTypeAttributeId, block, undrawn[0]))
                returned += undrawn.Length;
        }

        options.SequenceReservations.Clear();
        return returned;
    }

    /// <summary>
    /// "Start again" (plan "The service": <c>StartAgainAsync</c>): for a generated Sequence mapping, moves the
    /// target attribute's counter back (or forward; the direction is whatever the flow's configured
    /// <see cref="SyncRuleMappingGeneration.SequenceStart"/> calls for) to that start value. Existing values and
    /// assignments are left untouched, deliberately: there is no recall here (#1537's recall stages removal
    /// exports, which would rename or strip an already-exported value out from under a live account; plan "The
    /// service"). For every other token kind, and for a Sequence mapping whose counter has never been seeded,
    /// this is a documented no-op. <paramref name="mapping"/>'s <see cref="SyncRuleMapping.Id"/> is recorded as
    /// the mover on the counter, for the same audit reason a save-time raise records it.
    /// </summary>
    public async Task<GeneratedValueRestartResult> RestartAsync(SyncRuleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var generation = mapping.Generation;
        if (generation == null || generation.TokenKind != GeneratedValueTokenKind.Sequence)
            return new GeneratedValueRestartResult { RetiredValuesForgotten = 0, CounterFrom = null, CounterTo = null };

        // Forget the attribute's retired values first (Phase 6; FR 33): numbers it burnt can be issued again, but the
        // Metaverse and connector-space gates still skip any number an object holds when the counter reaches it.
        // Purged whether or not the counter has been seeded: a register can hold values a since-removed flow retired.
        var forgotten = await _repository.DeleteRetiredGeneratedValuesForAttributeAsync(
            mapping.TargetMetaverseAttributeId, mapping.TargetConnectedSystemAttributeId);

        var previous = await _repository.ResetGeneratedValueSequenceAsync(
            mapping.TargetMetaverseAttributeId, mapping.TargetConnectedSystemAttributeId, generation.SequenceStart, mapping.Id);

        return new GeneratedValueRestartResult
        {
            RetiredValuesForgotten = forgotten,
            CounterFrom = previous,
            CounterTo = previous.HasValue ? generation.SequenceStart : null
        };
    }

    /// <summary>
    /// A pure, no-I/O preview of what <paramref name="generation"/>'s uniqueness token would produce against
    /// <paramref name="sampleBaseValue"/> (plan Phase 3 point 2: <c>DescribeGeneratedCandidates</c>), built on
    /// <see cref="UniqueValueCandidates"/> so a form's live preview matches the real engine exactly. A Sequence
    /// preview starts from <paramref name="generation"/>'s configured <see cref="SyncRuleMappingGeneration.SequenceStart"/>,
    /// never the attribute's live counter (see <see cref="GetSequenceStateAsync"/> for that).
    /// </summary>
    /// <param name="generation">The settings to preview. Never persisted or read from.</param>
    /// <param name="targetAttributeType">The target attribute's data type, which decides whether a Random
    /// preview must draw a non-zero leading digit.</param>
    /// <param name="sampleBaseValue">A sample base value to place the token against; null or empty previews the
    /// token on its own.</param>
    /// <param name="count">How many candidates to compute for a token kind that produces a sequence of them
    /// (only-if-taken, sequence). Ignored for random, which returns a single example.</param>
    public GeneratedValueCandidatePreview DescribeGeneratedCandidates(
        SyncRuleMappingGeneration generation, AttributeDataType targetAttributeType, string? sampleBaseValue, int count)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Must be 1 or higher.");

        var placementText = BuildPlacementText(sampleBaseValue);

        switch (generation.TokenKind)
        {
            case GeneratedValueTokenKind.OnlyIfTaken:
            {
                var baseValue = sampleBaseValue ?? string.Empty;
                var candidates = new List<string>(count);
                for (var attempt = 1; attempt <= count; attempt++)
                    candidates.Add(UniqueValueCandidates.OnlyIfTakenCandidate(baseValue, attempt, generation.SuffixStyle, generation.SuffixStart, generation.Separator));

                return new GeneratedValueCandidatePreview
                {
                    FirstValue = string.IsNullOrEmpty(sampleBaseValue) ? null : baseValue,
                    Candidates = candidates,
                    PlacementText = placementText
                };
            }

            case GeneratedValueTokenKind.Sequence:
            {
                var candidates = new List<string>(count);
                for (var i = 0; i < count; i++)
                {
                    var number = generation.SequenceStart + (long)i * generation.SequenceIncrement;
                    var (rendered, _) = UniqueValueCandidates.RenderSequenceNumber(number, generation.FixedWidth, generation.OnWidthExceeded);
                    candidates.Add(UniqueValueCandidates.Place(sampleBaseValue, rendered, generation.Separator));
                }

                return new GeneratedValueCandidatePreview
                {
                    FirstValue = candidates.Count > 0 ? candidates[0] : null,
                    Candidates = candidates,
                    PlacementText = placementText
                };
            }

            case GeneratedValueTokenKind.Random:
            {
                var isNumberTarget = targetAttributeType is AttributeDataType.Number or AttributeDataType.LongNumber;
                var forceNonZeroLeadingDigit = isNumberTarget && generation.RandomFormat == GeneratedValueRandomFormat.Digits;
                var token = UniqueValueCandidates.GenerateRandomToken(generation.RandomFormat, generation.RandomLength, forceNonZeroLeadingDigit);

                return new GeneratedValueCandidatePreview
                {
                    Example = UniqueValueCandidates.Place(sampleBaseValue, token, generation.Separator),
                    PlacementText = placementText
                };
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(generation), generation.TokenKind, "Unrecognised token kind.");
        }
    }

    private static string BuildPlacementText(string? sampleBaseValue) =>
        string.IsNullOrEmpty(sampleBaseValue)
            ? "There is no base value, so the generated value is the token on its own."
            : sampleBaseValue.Contains('@')
                ? "The token is placed immediately before the \"@\" in the base value."
                : "The token is appended to the end of the base value.";
}
