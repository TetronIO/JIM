// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A page-scoped cache of export-matching candidates: the unjoined Connected System Object ids a batch
/// prefetch query found for each (Object Matching Rule, value) pair on the current page, plus which
/// (Metaverse Object, export Synchronisation Rule) pairs the prefetch covered.
/// <para>
/// Populated by a per-page batch prefetch (one query per Object Matching Rule per page) and cleared by
/// the caller once the page's export evaluation finishes; see <see cref="ExportEvaluationCache.ExportMatchCandidates"/>.
/// </para>
/// <para>
/// Value keys (the <c>object value</c> parameters below) use default <see cref="object.Equals(object?)"/>
/// and <see cref="object.GetHashCode"/> semantics: boxed <see cref="string"/> values compare ordinally,
/// and boxed <see cref="int"/>, <see cref="long"/>, <see cref="decimal"/> and <see cref="Guid"/> values
/// compare by their own value-type equality (for example, a boxed <c>5.0m</c> and a boxed <c>5.00m</c>
/// compare equal, matching PostgreSQL's scale-insensitive numeric equality). Callers must key with the
/// same type <see cref="ExportMatchingValue"/> resolved, never with a differently-typed equivalent value.
/// </para>
/// </summary>
public sealed class ExportMatchCandidates
{
    private readonly HashSet<(Guid MetaverseObjectId, int SyncRuleId)> _coveredPairs = [];
    private readonly Dictionary<(int ObjectMatchingRuleId, object Value), List<Guid>> _candidates = [];

    /// <summary>
    /// Marks that the page's batch prefetch covered the given (Metaverse Object, export Synchronisation
    /// Rule) pair, whether or not it found any candidates. Call <see cref="GetCandidates"/> for the
    /// pair's Object Matching Rule to read what (if anything) was found.
    /// </summary>
    public void MarkCovered(Guid metaverseObjectId, int syncRuleId)
        => _coveredPairs.Add((metaverseObjectId, syncRuleId));

    /// <summary>
    /// Whether the page's batch prefetch covered the given (Metaverse Object, export Synchronisation
    /// Rule) pair. <c>false</c> means "not prefetched; the caller must fall back to the per-object
    /// query", never "no match": an uncovered pair carries no information about whether a match exists.
    /// </summary>
    public bool IsCovered(Guid metaverseObjectId, int syncRuleId)
        => _coveredPairs.Contains((metaverseObjectId, syncRuleId));

    /// <summary>
    /// Records that the given Connected System Object is a candidate match for the given Object
    /// Matching Rule and value. Callers must supply candidates for a given (rule, value) pair already
    /// ordered by Connected System Object Id ascending; this method appends in call order.
    /// Duplicate calls for the same (rule, value, Connected System Object Id) are ignored.
    /// </summary>
    public void AddCandidate(int objectMatchingRuleId, object value, Guid connectedSystemObjectId)
    {
        var key = (objectMatchingRuleId, value);
        if (!_candidates.TryGetValue(key, out var candidateIds))
        {
            candidateIds = [];
            _candidates[key] = candidateIds;
        }

        if (!candidateIds.Contains(connectedSystemObjectId))
            candidateIds.Add(connectedSystemObjectId);
    }

    /// <summary>
    /// The remaining candidate Connected System Object ids for the given Object Matching Rule and
    /// value, in the order they were added (Connected System Object Id ascending). Empty when none
    /// were found, or when the (rule, value) pair was never populated.
    /// </summary>
    public IReadOnlyList<Guid> GetCandidates(int objectMatchingRuleId, object value)
        => _candidates.TryGetValue((objectMatchingRuleId, value), out var candidateIds) ? candidateIds : [];

    /// <summary>
    /// Removes the given Connected System Object from every candidate list it appears in, across every
    /// Object Matching Rule and value. Call this once a candidate has been claimed by, or lost to,
    /// another Metaverse Object on the same page, so it is never offered to a later lookup as though
    /// still unclaimed.
    /// </summary>
    public void Remove(Guid connectedSystemObjectId)
    {
        foreach (var candidateIds in _candidates.Values)
            candidateIds.Remove(connectedSystemObjectId);
    }
}
