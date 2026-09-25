// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Models.Transactional;

/// <summary>
/// A lightweight, denormalised view of one <see cref="GeneratedValueAssignment"/> for display on a Metaverse
/// Object (Unique Value Generation, #242, Phase 3): the committed generated values a Metaverse Object currently
/// holds, and where each one came from. Import mode only; an export-mode assignment belongs to a Connected
/// System Object, not a Metaverse Object, so it never appears here.
/// </summary>
public class GeneratedValueAssignmentHeader
{
    /// <summary>The assignment this row describes.</summary>
    public Guid AssignmentId { get; set; }

    /// <summary>The Metaverse attribute the value was generated for.</summary>
    public int MetaverseAttributeId { get; set; }

    /// <summary>The name of <see cref="MetaverseAttributeId"/>.</summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>The generated value, as held on the object.</summary>
    public string Value { get; set; } = null!;

    /// <summary>Which uniqueness token produced the value.</summary>
    public GeneratedValueTokenKind TokenKind { get; set; }

    /// <summary>The Synchronisation Rule whose mapping produced this assignment.</summary>
    public int SyncRuleId { get; set; }

    /// <summary>The name of <see cref="SyncRuleId"/>.</summary>
    public string? SyncRuleName { get; set; }

    /// <summary>The Attribute Flow mapping that produced this assignment.</summary>
    public int SyncRuleMappingId { get; set; }

    public GeneratedValueAssignmentState State { get; set; }

    /// <summary>
    /// True when the value was adopted from an existing accepted value rather than generated (plan decision 5,
    /// "adopt before generate").
    /// </summary>
    public bool Adopted { get; set; }

    /// <summary>When the value was committed to the object; falls back to when the assignment was created for
    /// a row that has not yet completed its first commit (a rare, transitional read).</summary>
    public DateTime AssignedDate { get; set; }
}
