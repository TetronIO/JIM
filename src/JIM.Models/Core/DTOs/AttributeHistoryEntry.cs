// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// One entry in an attribute's change history on a Metaverse Object.
/// </summary>
public class AttributeHistoryEntry
{
    public AttributeHistoryChangeKind Kind { get; set; }

    public string? Value { get; set; }

    /// <summary>The replaced value, for <see cref="AttributeHistoryChangeKind.Set"/>.</summary>
    public string? PreviousValue { get; set; }

    /// <summary>The Synchronisation Rule that contributed the value at the time (snapshot survives rule deletion).</summary>
    public int? SyncRuleId { get; set; }

    public string? SyncRuleName { get; set; }

    /// <summary>
    /// True when the value was produced by a Generated Value Attribute Flow rather than read from the Connected
    /// System (see <see cref="ProvenanceLogic.IsGeneratedHistoryValue"/>).
    /// </summary>
    public bool IsGeneratedValue { get; set; }

    public ProvenanceChange Change { get; set; } = new();
}
