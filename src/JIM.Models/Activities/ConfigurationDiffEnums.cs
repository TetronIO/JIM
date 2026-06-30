// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// How a <see cref="ConfigurationDiffNode"/> changed between two configuration snapshots.
/// </summary>
public enum ConfigurationDiffChangeType
{
    /// <summary>The node is identical in both snapshots.</summary>
    Unchanged,

    /// <summary>The node exists only in the newer snapshot.</summary>
    Added,

    /// <summary>The node exists only in the older snapshot.</summary>
    Removed,

    /// <summary>The node exists in both snapshots but its value (or a descendant) changed.</summary>
    Modified
}
