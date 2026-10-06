// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Transactional;

/// <summary>
/// One Collision Remediation revision for the repository to write atomically (Unique Value Generation, #242, release
/// 4): the assignment's new state and value, plus, in import mode, the Metaverse value, its change record and the
/// revision-pending record, or, in export mode, the queued export attribute change carrying the value. Either every
/// part is written or none is (<see cref="GeneratedValueRevisionResult"/>).
/// </summary>
public sealed class GeneratedValueRevision
{
    /// <summary>
    /// The assignment, already carrying its new value, normalised value, previous value, state and remediation
    /// bookkeeping. Its mode (import or export) decides which of the parts below apply.
    /// </summary>
    public required GeneratedValueAssignment Assignment { get; init; }

    /// <summary>
    /// The value being replaced, as the export carried it. The optimistic concurrency check: the write only happens
    /// while the object still holds this value (compared case-insensitively).
    /// </summary>
    public required string PreviousValue { get; init; }

    /// <summary>
    /// The numeric form of <see cref="PreviousValue"/> for a Number or Long Number attribute; null for Text.
    /// </summary>
    public long? PreviousNumericValue { get; init; }

    /// <summary>
    /// The numeric form of the new value for a Number or Long Number attribute; null for Text.
    /// </summary>
    public long? NewNumericValue { get; init; }

    /// <summary>
    /// Whether the attribute is a Long Number (written to <c>LongValue</c>) rather than a Number (<c>IntValue</c>).
    /// Ignored for Text.
    /// </summary>
    public bool IsLongNumber { get; init; }

    /// <summary>
    /// Whether the previous value is written to the retired values register (reason
    /// <see cref="RetiredGeneratedValueReason.Regenerated"/>) in the same transaction: true when the generated mapping
    /// never reuses values.
    /// </summary>
    public bool RetirePreviousValue { get; init; }

    /// <summary>
    /// Import mode: the change record describing the revision on the Metaverse Object's history; null when Metaverse
    /// Object change tracking is off.
    /// </summary>
    public MetaverseObjectChange? MetaverseObjectChange { get; init; }

    /// <summary>
    /// Import mode: the provenance written onto the revised Metaverse value, the generated mapping's Synchronisation
    /// Rule. Null leaves the value's existing provenance as it is.
    /// </summary>
    public int? ContributedBySyncRuleId { get; init; }

    /// <summary>
    /// Import mode: the Connected System of <see cref="ContributedBySyncRuleId"/>.
    /// </summary>
    public int? ContributedBySystemId { get; init; }

    /// <summary>
    /// Import mode: the Metaverse Object's denormalised display name after the revision, when the revised attribute
    /// is one the object is named by; null leaves it unchanged.
    /// </summary>
    public string? CachedDisplayName { get; init; }

    /// <summary>
    /// Import mode: the record that carries the revision to the queued exports at the next synchronisation.
    /// </summary>
    public GeneratedValueRevisionPending? RevisionPending { get; init; }

    /// <summary>
    /// Export mode: the queued Pending Export attribute change that carries the value, rewritten to the new value.
    /// </summary>
    public Guid? PendingExportAttributeValueChangeId { get; init; }
}
