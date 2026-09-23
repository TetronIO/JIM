// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Transactional;

namespace JIM.Application.UniqueValues;

/// <summary>
/// The caller-agnostic unique value service (Unique Value Generation, #242, FR 21). Resolves a batch of
/// <see cref="GenerationRequest"/>s to <see cref="GenerationOutcome"/>s: sticky first, then adopt before
/// generate, then candidate generation through four ordered gates, then, once the caller has persisted the
/// objects, saving the assignments it proposed. This type knows nothing about synchronisation runs,
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
    /// sticky, then adopt before generate, then candidate generation through the reservation, Metaverse (or
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
            var toAdopt = new List<int>();

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

                toAdopt.Add(i);
            }

            var toGenerate = new List<int>();
            foreach (var i in toAdopt)
            {
                var request = requests[i];
                if (!string.IsNullOrEmpty(request.AdoptableValue))
                {
                    outcomes[i] = await TryAdoptAsync(request, options, ownerId);
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
    /// queries and caches for itself on a cache miss.
    /// </summary>
    public async Task PrefetchAssignmentsAsync(IReadOnlyCollection<Guid> metaverseObjectIds, IReadOnlyCollection<Guid> connectedSystemObjectIds, UniqueValueResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

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
    /// Persists the <see cref="GenerationOutcomeKind.Generated"/> and <see cref="GenerationOutcomeKind.Adopted"/>
    /// outcomes' assignments, once the caller has persisted the objects themselves. <paramref name="objectIdResolver"/>
    /// returns the now-persisted object id for a request (the caller correlates through
    /// <see cref="GenerationRequest.CallerState"/>). Every other outcome kind is ignored.
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

        var toCommit = outcomes.Where(o => o.Kind is GenerationOutcomeKind.Generated or GenerationOutcomeKind.Adopted).ToList();
        if (toCommit.Count == 0)
            return [];

        foreach (var outcome in toCommit)
        {
            var assignment = outcome.Assignment!;
            var objectId = objectIdResolver(outcome.Request);
            if (outcome.Request.Mode == GeneratedValueMode.Import)
                assignment.MetaverseObjectId = objectId;
            else
                assignment.ConnectedSystemObjectId = objectId;
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
                match = exportBag.FirstOrDefault(a => a.ConnectedSystemObjectTypeAttributeId == request.ConnectedSystemObjectTypeAttributeId);

            if (match != null)
                result[i] = match;
        }

        return result;
    }

    private static GenerationOutcome BuildStickyOutcome(GenerationRequest request, GeneratedValueAssignment assignment) =>
        new(request, GenerationOutcomeKind.Sticky, assignment.Value, TryParseNumeric(request, assignment.Value), assignment, null);

    // ---- Adopt before generate ----

    private async Task<GenerationOutcome> TryAdoptAsync(GenerationRequest request, UniqueValueResolveOptions options, Guid ownerId)
    {
        var value = request.AdoptableValue!;
        var normalisedValue = value.ToLowerInvariant();
        var (attributeId, scope) = AttributeAndScope(request);

        if (await IsHeldByAnotherLiveAssignmentAsync(request, normalisedValue))
            return AdoptionConflict(request, value);

        if (!options.Reservations.TryReserve(ownerId, scope, attributeId, normalisedValue))
            return AdoptionConflict(request, value);

        var assignment = BuildAssignment(request, value, normalisedValue, adopted: true);
        return new GenerationOutcome(request, GenerationOutcomeKind.Adopted, value, TryParseNumeric(request, value), assignment, null);
    }

    private static GenerationOutcome AdoptionConflict(GenerationRequest request, string value) =>
        new(request, GenerationOutcomeKind.AdoptionConflict, null, null, null,
            $"\"{value}\" could not be adopted for {request.AttributeName} because another object already holds it.");

    private async Task<bool> IsHeldByAnotherLiveAssignmentAsync(GenerationRequest request, string normalisedValue)
    {
        var (attributeId, _) = AttributeAndScope(request);
        var mvAttributeId = request.Mode == GeneratedValueMode.Import ? attributeId : (int?)null;
        var csAttributeId = request.Mode == GeneratedValueMode.Export ? attributeId : (int?)null;
        var excludingId = ExcludingObjectId(request);

        var taken = await _repository.GetGeneratedValueAssignmentValuesInUseAsync(mvAttributeId, csAttributeId, [normalisedValue], excludingId);
        return taken.Contains(normalisedValue);
    }

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

                var (candidate, terminal) = await ComputeCandidateAsync(request, attemptIndex, options);
                if (terminal != null)
                {
                    outcomes[i] = terminal;
                    continue;
                }

                lastCandidate[i] = candidate!.Value.Text;
                candidates[i] = candidate.Value;
                drawn.Add(i);
            }

            // Gates (a), (c), (d), (e) in order, each narrowing the survivor list; a request a gate rejects is
            // never passed to a later gate this round (the short-circuit the test suite proves), and is instead
            // retried with a fresh candidate next round.
            var survivors = FilterReservationGate(drawn, requests, candidates, options, ownerId, claimedThisCall, lastRejectionGate);

            if (survivors.Count > 0)
                survivors = await FilterValueGateAsync(survivors, requests, candidates, lastRejectionGate);

            if (survivors.Count > 0)
                survivors = await FilterConnectorSpaceGateAsync(survivors, requests, candidates, lastRejectionGate);

            if (survivors.Count > 0)
                survivors = await FilterOtherAssignmentsGateAsync(survivors, requests, candidates, lastRejectionGate);

            var rejectedThisRound = drawn.Except(survivors).ToList();

            // Every request still surviving here cleared all four gates this round: claim the candidate and
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

                var assignment = BuildAssignment(request, candidate.Text, normalisedValue, adopted: false);
                outcomes[i] = new GenerationOutcome(request, GenerationOutcomeKind.Generated, candidate.Text, candidate.Numeric, assignment, null);
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
    /// Gate (d), import mode only: every id in <see cref="GenerationRequest.ConnectorSpaceAttributeIds"/>, with
    /// no exclusion (plan "Behaviour (ResolveAsync)": when the object's own target already held a value it
    /// would have been adopted in step 2, so no exclusion is needed here).
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
            var takenValues = await _repository.GetConnectedSystemAttributeValuesInUseAsync(csAttributeId, values, null);
            foreach (var i in indices)
            {
                if (takenValues.Contains(candidates[i].Text) && taken.Add(i))
                    lastRejectionGate[i] = "a Connected System Object";
            }
        }

        foreach (var (csAttributeId, indices) in numberByAttribute)
        {
            var values = indices.Select(i => candidates[i].Numeric!.Value).Distinct().ToList();
            var takenValues = await _repository.GetConnectedSystemAttributeNumbersInUseAsync(csAttributeId, values, null);
            foreach (var i in indices)
            {
                if (takenValues.Contains(candidates[i].Numeric!.Value) && taken.Add(i))
                    lastRejectionGate[i] = "a Connected System Object";
            }
        }

        return active.Where(i => !taken.Contains(i)).ToList();
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

    // ---- Candidate generation ----

    private async Task<((string Text, long? Numeric)? Candidate, GenerationOutcome? Terminal)> ComputeCandidateAsync(
        GenerationRequest request, int attemptIndex, UniqueValueResolveOptions options)
    {
        var generation = request.Generation;
        var isNumberTarget = request.TargetType is AttributeDataType.Number or AttributeDataType.LongNumber;

        switch (generation.TokenKind)
        {
            case GeneratedValueTokenKind.OnlyIfTaken:
            {
                if (string.IsNullOrWhiteSpace(request.BaseValue))
                    return (null, NoBaseValue(request));

                var text = UniqueValueCandidates.OnlyIfTakenCandidate(
                    request.BaseValue, attemptIndex, generation.SuffixStyle, generation.SuffixStart, generation.Separator);
                return ((text, null), null);
            }

            case GeneratedValueTokenKind.Sequence:
            {
                var number = await SequenceAllocator.NextNumberAsync(
                    _repository, options, options.DryRun,
                    request.MetaverseAttributeId, request.ConnectedSystemObjectTypeAttributeId,
                    generation.SequenceStart, generation.SequenceIncrement);

                var (rendered, widthExceeded) = UniqueValueCandidates.RenderSequenceNumber(number, generation.FixedWidth, generation.OnWidthExceeded);
                if (widthExceeded)
                    return (null, WidthExceeded(request, rendered, generation.FixedWidth!.Value));

                var text = UniqueValueCandidates.Place(request.BaseValue, rendered, generation.Separator);
                var numeric = isNumberTarget ? number : (long?)null;
                return ((text, numeric), null);
            }

            case GeneratedValueTokenKind.Random:
            {
                var forceNonZeroLeadingDigit = isNumberTarget && generation.RandomFormat == GeneratedValueRandomFormat.Digits;
                var token = UniqueValueCandidates.GenerateRandomToken(generation.RandomFormat, generation.RandomLength, forceNonZeroLeadingDigit);
                var text = UniqueValueCandidates.Place(request.BaseValue, token, generation.Separator);
                var numeric = isNumberTarget && generation.RandomFormat == GeneratedValueRandomFormat.Digits
                    ? long.Parse(token, CultureInfo.InvariantCulture)
                    : (long?)null;
                return ((text, numeric), null);
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

    private static GeneratedValueAssignment BuildAssignment(GenerationRequest request, string value, string normalisedValue, bool adopted)
    {
        var now = DateTime.UtcNow;
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            Value = value,
            NormalisedValue = normalisedValue,
            State = adopted ? GeneratedValueAssignmentState.Committed : GeneratedValueAssignmentState.Proposed,
            SyncRuleMappingGenerationId = request.Generation.Id,
            Adopted = adopted,
            Created = now,
            LastUpdated = now,
            CommittedAt = adopted ? now : null
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
}
