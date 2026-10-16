// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;

namespace JIM.Application.UniqueValues;

/// <summary>
/// A process-wide, thread-safe set of candidate values claimed by in-flight synchronisation runs (Unique Value
/// Generation, #242, plan "The service"). Generalised from <c>ExampleDataValueTrackerStore</c>'s lock-free
/// reserved-values dictionary: one instance lives on the worker for its whole process lifetime and is handed to
/// every run, so that two schedule steps racing in parallel (release 4's parallel export batches, or two
/// unrelated runs) can never both claim the same candidate before either has written it to the database. The
/// cross-assignment unique index (plan decision 13) remains the final arbiter; this set exists to make that
/// race rare rather than routine, and to give the losing side a place to check before it ever reaches the
/// database.
/// <para>
/// Keys are (<see cref="UniqueValueScope"/>, attribute id, normalised value); callers normalise (lower-case)
/// the value themselves before calling, the same convention the repository's case-insensitive gates use, so a
/// caller that forgets is defending against nothing: the set does not re-normalise for them.
/// </para>
/// </summary>
public sealed class UniqueValueReservationSet
{
    private readonly ConcurrentDictionary<(UniqueValueScope Scope, int AttributeId, string NormalisedValue), Guid> _reservations = new();

    /// <summary>
    /// Attempts to claim <paramref name="normalisedValue"/> for <paramref name="ownerId"/>. Returns true the
    /// first time any owner claims this (scope, attribute, value) triple; false if it is already claimed, by
    /// this owner or another, since a value claimed once must never be claimed twice regardless of who holds
    /// it. The check-and-claim is atomic, so two concurrent callers racing for the same candidate can never both
    /// succeed.
    /// </summary>
    public bool TryReserve(Guid ownerId, UniqueValueScope scope, int attributeId, string normalisedValue) =>
        _reservations.TryAdd((scope, attributeId, normalisedValue), ownerId);

    /// <summary>
    /// Claims every one of <paramref name="keys"/> for <paramref name="ownerId"/>, or none of them (Generated Value Forms,
    /// #2023: a candidate and the values built from it are issued together or not at all). Returns false, leaving
    /// nothing claimed by this call, as soon as one is already claimed.
    /// </summary>
    public bool TryReserveAll(Guid ownerId, IReadOnlyCollection<(UniqueValueScope Scope, int AttributeId, string NormalisedValue)> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var claimed = new List<(UniqueValueScope Scope, int AttributeId, string NormalisedValue)>(keys.Count);
        foreach (var key in keys.Distinct())
        {
            if (!_reservations.TryAdd(key, ownerId))
            {
                foreach (var undo in claimed)
                    Release(ownerId, undo.Scope, undo.AttributeId, undo.NormalisedValue);
                return false;
            }

            claimed.Add(key);
        }

        return true;
    }

    /// <summary>
    /// Releases one claim, if <paramref name="ownerId"/> holds it: for values claimed for a candidate that was then not
    /// issued after all. A claim held by another owner is left alone.
    /// </summary>
    public void Release(Guid ownerId, UniqueValueScope scope, int attributeId, string normalisedValue) =>
        _reservations.TryRemove(new KeyValuePair<(UniqueValueScope, int, string), Guid>((scope, attributeId, normalisedValue), ownerId));

    /// <summary>
    /// Whether (scope, attribute, value) is currently claimed by an owner other than <paramref name="ownerId"/>.
    /// Used as a candidate's first gate (plan "The service"): a cheap, in-process check before the repository's
    /// gates are queried at all. Returns false both when the triple is unclaimed and when it is claimed by
    /// <paramref name="ownerId"/> itself, since a run does not block its own claims.
    /// </summary>
    public bool IsReservedByAnotherOwner(Guid ownerId, UniqueValueScope scope, int attributeId, string normalisedValue) =>
        _reservations.TryGetValue((scope, attributeId, normalisedValue), out var owner) && owner != ownerId;

    /// <summary>
    /// Releases every claim held by <paramref name="ownerId"/>. Called once at run end, after the run's
    /// generated values are committed and visible to the database gates (so a later run's candidates are
    /// checked against the database, not against claims this run no longer needs); a dry run (Sync Preview)
    /// releases its own throwaway owner id at the end of each resolve call instead, since nothing it proposes
    /// is ever committed.
    /// </summary>
    public void ReleaseAll(Guid ownerId)
    {
        foreach (var key in _reservations.Keys)
        {
            if (_reservations.TryGetValue(key, out var owner) && owner == ownerId)
                _reservations.TryRemove(key, out _);
        }
    }
}
