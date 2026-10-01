// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// A Generated Value a Metaverse Object currently holds, as value provenance (#399) needs it: which attribute,
/// which Attribute Flow produced it, and the value. Read from a live generated-value assignment, which exists
/// exactly while a Generated Value Attribute Flow is responsible for the attribute (it is deleted the moment
/// another contributor wins), so its presence is what makes a value a Generated Value.
/// </summary>
public class GeneratedValueOwnership
{
    /// <summary>The Metaverse attribute holding the Generated Value.</summary>
    public int AttributeId { get; set; }

    /// <summary>The Synchronisation Rule whose Attribute Flow produced the value.</summary>
    public int SyncRuleId { get; set; }

    /// <summary>The Attribute Flow (Synchronisation Rule mapping) that produced the value.</summary>
    public int SyncRuleMappingId { get; set; }

    /// <summary>The Generated Value, as held on the Metaverse Object.</summary>
    public string Value { get; set; } = null!;

    /// <summary>The value this one replaced when a collision revised it; null if it was never revised.</summary>
    public string? PreviousValue { get; set; }

    /// <summary>True when the value has been revised after a collision at least once.</summary>
    public bool Corrected { get; set; }

    /// <summary>
    /// True when the Metaverse Object currently holds <see cref="Value"/> for the attribute, contributed by
    /// <see cref="SyncRuleId"/>. A Generated Value Attribute Flow keeps its assignment while a higher-priority
    /// Attribute Flow supplies the attribute, so only then is the assignment the current value's source.
    /// </summary>
    public bool IsCurrentValue { get; set; }
}
