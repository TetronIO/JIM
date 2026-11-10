// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What Pending Export reconciliation asks the database about one multi-valued reference attribute of one Connected
/// System Object, instead of loading every value it holds (#2669). The answer is a <see cref="ProbedAttributeValues"/>:
/// the rows that could match one of the changes asked about, and the attribute's value count capped at two when a
/// change's verdict depends on it.
/// </summary>
/// <remarks>
/// The database only narrows: every row it returns is judged by the same .NET comparison a full load is judged by
/// (<c>SyncEngine.ValueExistsOnCso</c>), so each way of asking must return a superset of the rows that comparison could
/// match. That is why the three value lists differ in the rows they search: a resolved row names its object by id
/// alone for a change that names one too, so it is found by <see cref="ReferenceIds"/>; an unresolved row is found by
/// its string, exactly (<see cref="ExactValues"/>, for a case-exact attribute) or ignoring case
/// (<see cref="CaseInsensitiveValues"/>), both searched over unresolved rows only, the rows such a change can match by
/// string. A change naming no object, which matches a resolved row by its string too, cannot be put this way (#2871). A
/// question that cannot be put this way is never asked: the pair takes the full load (see
/// <c>SyncEngine.PlanReconciliationProbes</c>).
/// </remarks>
public sealed class ReconciliationProbeQuestion
{
    public required Guid ConnectedSystemObjectId { get; init; }

    public required int AttributeId { get; init; }

    /// <summary>
    /// The changes this question was asked for: every change awaiting confirmation on this attribute of this object. The
    /// answer is valid for these changes only; reconciliation fails rather than judge any other change from it.
    /// </summary>
    public required IReadOnlySet<Guid> ChangeIds { get; init; }

    /// <summary>
    /// The referenced objects' ids sought among resolved rows (<c>ReferenceValueId</c>).
    /// </summary>
    public required IReadOnlyCollection<Guid> ReferenceIds { get; init; }

    /// <summary>
    /// Reference strings sought exactly, among unresolved rows only (<c>UnresolvedReferenceValue</c>).
    /// </summary>
    public required IReadOnlyCollection<string> ExactValues { get; init; }

    /// <summary>
    /// Reference strings sought ignoring case, among unresolved rows only. ASCII only: the database lowers both sides,
    /// and only for ASCII values (with the database's folding checked against .NET's once per run) is that known to find
    /// every row .NET's folding would match.
    /// </summary>
    public required IReadOnlyCollection<string> CaseInsensitiveValues { get; init; }

    /// <summary>
    /// Whether a change's verdict depends on how many values the attribute holds: an empty Add or Update, a RemoveAll,
    /// or an Update setting a multi-valued attribute's whole value set.
    /// </summary>
    public required bool NeedsCount { get; init; }

    /// <summary>
    /// How many values this question sends to the database: one per id or string sought, and one for the count.
    /// </summary>
    public int TupleCount => ReferenceIds.Count + ExactValues.Count + CaseInsensitiveValues.Count + (NeedsCount ? 1 : 0);
}
