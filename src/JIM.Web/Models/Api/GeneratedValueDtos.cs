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
    public bool Adopted { get; set; }
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
        Adopted = model.Adopted,
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
