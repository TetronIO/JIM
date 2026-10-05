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
    public Guid AssignmentId { get; set; }
    public int MetaverseAttributeId { get; set; }
    public string AttributeName { get; set; } = null!;
    public string Value { get; set; } = null!;
    public GeneratedValueTokenKind TokenKind { get; set; }
    public int SyncRuleId { get; set; }
    public string? SyncRuleName { get; set; }
    public int SyncRuleMappingId { get; set; }
    public GeneratedValueAssignmentState State { get; set; }
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
/// API representation of a generated Sequence mapping's counter state (Unique Value Generation, #242, Phase 3).
/// See <see cref="GeneratedValueSequenceState"/> for the source model.
/// </summary>
public class GeneratedValueSequenceStateDto
{
    public string AttributeName { get; set; } = null!;
    public long NextNumber { get; set; }
    public string NextNumberFormatted { get; set; } = null!;
    public bool NextNumberWidthExceeded { get; set; }
    public long AssignedCount { get; set; }
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
/// API representation of a "Start again" outcome (Unique Value Generation, #242, Phase 3). See
/// <see cref="GeneratedValueRestartResult"/> for the source model.
/// </summary>
public class GeneratedValueRestartResultDto
{
    public int RetiredValuesForgotten { get; set; }
    public long? CounterFrom { get; set; }
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
