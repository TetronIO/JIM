// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Transactional;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of one committed generated value a Metaverse Object holds (Unique Value Generation, #242,
/// Phase 3). See <see cref="GeneratedValueAssignmentHeader"/> for the source model.
/// </summary>
public class GeneratedValueAssignmentHeaderDto
{
    /// <summary>
    /// The unique identifier of the assignment.
    /// </summary>
    public Guid AssignmentId { get; set; }

    /// <summary>
    /// The unique identifier of the Metaverse Attribute the value was generated for.
    /// </summary>
    public int MetaverseAttributeId { get; set; }

    /// <summary>
    /// That attribute's name.
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// The generated value.
    /// </summary>
    public string Value { get; set; } = null!;

    /// <summary>
    /// How the value was made unique: a number added only if the value is taken, the next number in a sequence, or
    /// a random token.
    /// </summary>
    public GeneratedValueTokenKind TokenKind { get; set; }

    /// <summary>
    /// The unique identifier of the Synchronisation Rule whose Attribute Flow generated the value.
    /// </summary>
    public int SyncRuleId { get; set; }

    /// <summary>
    /// That Synchronisation Rule's name.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// The unique identifier of the Attribute Flow that generated the value.
    /// </summary>
    public int SyncRuleMappingId { get; set; }

    /// <summary>
    /// Where the value stands: Committed once saved onto the object, Remediated after JIM revised it when a target
    /// refused it, or NeedsDecision when it could not be revised automatically and waits for an administrator.
    /// Proposed values are never saved, so never appear here.
    /// </summary>
    public GeneratedValueAssignmentState State { get; set; }

    /// <summary>
    /// When the value was assigned (UTC).
    /// </summary>
    public DateTime AssignedDate { get; set; }

    public static GeneratedValueAssignmentHeaderDto FromModel(GeneratedValueAssignmentHeader model) => new()
    {
        AssignmentId = model.AssignmentId,
        MetaverseAttributeId = model.MetaverseAttributeId,
        AttributeName = model.AttributeName,
        Value = model.Value,
        TokenKind = model.TokenKind,
        SyncRuleId = model.SyncRuleId,
        SyncRuleName = model.SyncRuleName,
        SyncRuleMappingId = model.SyncRuleMappingId,
        State = model.State,
        AssignedDate = model.AssignedDate
    };
}

/// <summary>
/// A generated Sequence mapping's counter: the next number it would issue, without reserving it.
/// </summary>
public class GeneratedValueSequenceStateDto
{
    /// <summary>
    /// The attribute the counter issues values for.
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// The next number the counter would issue: the higher of its current position and the mapping's Sequence
    /// Start. Before the counter is seeded, the higher of the Sequence Start and one step past the attribute's
    /// highest existing number.
    /// </summary>
    public long NextNumber { get; set; }

    /// <summary>
    /// The next number as it would be issued, padded to the mapping's Fixed Width when it has one.
    /// </summary>
    public string NextNumberFormatted { get; set; } = null!;

    /// <summary>
    /// Whether the next number no longer fits the mapping's Fixed Width on a mapping that stops rather than issue a
    /// longer number: the next value cannot be generated until the width or the counter is changed. False on a
    /// mapping that allows a longer number.
    /// </summary>
    public bool NextNumberWidthExceeded { get; set; }

    /// <summary>
    /// How many numbers the counter has issued; 0 before it is seeded.
    /// </summary>
    public long AssignedCount { get; set; }

    /// <summary>
    /// Whether the counter has been seeded. Until it is, the first value seeds it from the higher of the Sequence
    /// Start and the attribute's highest existing number.
    /// </summary>
    public bool IsSeeded { get; set; }

    public static GeneratedValueSequenceStateDto FromModel(GeneratedValueSequenceState model) => new()
    {
        AttributeName = model.AttributeName,
        NextNumber = model.NextNumber,
        NextNumberFormatted = model.NextNumberFormatted,
        NextNumberWidthExceeded = model.NextNumberWidthExceeded,
        AssignedCount = model.AssignedCount,
        IsSeeded = model.IsSeeded
    };
}

/// <summary>
/// What starting a generated mapping again did.
/// </summary>
public class GeneratedValueRestartResultDto
{
    /// <summary>
    /// How many of the attribute's retired values were forgotten, and so can be issued again; 0 for a mapping whose
    /// token is not a Sequence.
    /// </summary>
    public int RetiredValuesForgotten { get; set; }

    /// <summary>
    /// The next number before the restart; null when the counter had never been seeded, or the token is not a
    /// Sequence.
    /// </summary>
    public long? CounterFrom { get; set; }

    /// <summary>
    /// The next number after the restart, which is the mapping's Sequence Start; null when CounterFrom is.
    /// </summary>
    public long? CounterTo { get; set; }

    public static GeneratedValueRestartResultDto FromModel(GeneratedValueRestartResult model) => new()
    {
        RetiredValuesForgotten = model.RetiredValuesForgotten,
        CounterFrom = model.CounterFrom,
        CounterTo = model.CounterTo
    };
}

/// <summary>
/// API representation of one value in an attribute's retired values register (Unique Value Generation, #242,
/// Phase 6): a value JIM issued and will never issue again for the attribute. See
/// <see cref="RetiredGeneratedValueHeader"/> for the source model.
/// </summary>
public class RetiredGeneratedValueDto
{
    /// <summary>The register entry's own identifier.</summary>
    public long Id { get; set; }

    /// <summary>The Metaverse Attribute the value was generated for (an import flow); null for an export flow.</summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>The Connected System attribute the value was generated for (an export flow); null for an import flow.</summary>
    public int? ConnectedSystemObjectTypeAttributeId { get; set; }

    /// <summary>The attribute's name.</summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>The value as it was issued.</summary>
    public string Value { get; set; } = null!;

    /// <summary>When it was retired (UTC).</summary>
    public DateTime RetiredAt { get; set; }

    /// <summary>
    /// Why it was retired: <c>ObjectDeleted</c> (the object that held it was deleted), <c>Superseded</c> (another
    /// Attribute Flow took the attribute over, or the value was cleared), <c>Recalled</c> (the generating Attribute
    /// Flow was removed), or <c>Regenerated</c> (reserved for Collision Remediation; not written yet).
    /// </summary>
    public RetiredGeneratedValueReason Reason { get; set; }

    /// <summary>The object that held it: a Metaverse Object id (import) or Connected System Object id (export).</summary>
    public Guid? FromObjectId { get; set; }

    /// <summary>The holder's display name, captured when the value was retired.</summary>
    public string? FromObjectDisplayName { get; set; }

    /// <summary>Whether the object that held the value still exists.</summary>
    public bool FromObjectExists { get; set; }

    /// <summary>The Activity during which the value was retired, when one was recorded.</summary>
    public Guid? ActivityId { get; set; }

    public static RetiredGeneratedValueDto FromModel(RetiredGeneratedValueHeader model) => new()
    {
        Id = model.Id,
        MetaverseAttributeId = model.MetaverseAttributeId,
        ConnectedSystemObjectTypeAttributeId = model.ConnectedSystemObjectTypeAttributeId,
        AttributeName = model.AttributeName,
        Value = model.Value,
        RetiredAt = model.RetiredAt,
        Reason = model.Reason,
        FromObjectId = model.FromObjectId,
        FromObjectDisplayName = model.FromObjectDisplayName,
        FromObjectExists = model.FromObjectExists,
        ActivityId = model.ActivityId
    };
}
