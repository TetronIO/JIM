// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// One node in a configuration diff tree, produced by comparing two <see cref="ConfigurationSnapshot"/> trees. Mirrors
/// the shape of <see cref="ConfigurationSnapshotNode"/> but carries a <see cref="ChangeType"/> and both the old and new
/// values, so a single renderer can present additions, removals and modifications.
/// </summary>
public class ConfigurationDiffNode
{
    /// <summary>Stable machine key identifying this node among its siblings.</summary>
    public string Key { get; set; } = null!;

    /// <summary>A friendly, human-readable label for display. Falls back to <see cref="Key"/> when not set.</summary>
    public string? Label { get; set; }

    /// <summary>Whether this node is a scalar value, a nested object, or a collection of objects.</summary>
    public ConfigurationSnapshotNodeType NodeType { get; set; }

    /// <summary>How this node changed between the two snapshots.</summary>
    public ConfigurationDiffChangeType ChangeType { get; set; }

    /// <summary>The scalar value in the older snapshot; null for objects, collections, secrets, and added scalars.</summary>
    public string? OldValue { get; set; }

    /// <summary>The scalar value in the newer snapshot; null for objects, collections, secrets, and removed scalars.</summary>
    public string? NewValue { get; set; }

    /// <summary>Human-friendly rendering of <see cref="OldValue"/> for display (FK "Name (id)", spaced enum), if captured.</summary>
    public string? OldDisplayValue { get; set; }

    /// <summary>Human-friendly rendering of <see cref="NewValue"/> for display (FK "Name (id)", spaced enum), if captured.</summary>
    public string? NewDisplayValue { get; set; }

    /// <summary>
    /// True when this scalar represents a secret. A secret change is detected via its keyed hash and reported only as a
    /// <see cref="ChangeType"/>; <see cref="OldValue"/> and <see cref="NewValue"/> are never populated for secrets.
    /// </summary>
    public bool IsSecret { get; set; }

    /// <summary>For a collection item, the stable database identifier of the underlying entity. Null otherwise.</summary>
    public int? ItemId { get; set; }

    /// <summary>Child diff nodes for Object and Collection nodes. Null for scalars.</summary>
    public List<ConfigurationDiffNode>? Children { get; set; }
}
