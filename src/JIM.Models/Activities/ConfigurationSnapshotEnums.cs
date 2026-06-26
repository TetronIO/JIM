// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// The kind of a <see cref="ConfigurationSnapshotNode"/>.
/// </summary>
public enum ConfigurationSnapshotNodeType
{
    /// <summary>A single value: string, number, bool, enum, date, or a redacted secret hash.</summary>
    Scalar,

    /// <summary>A nested object with child nodes.</summary>
    Object,

    /// <summary>A collection of object nodes, each typically carrying a stable <see cref="ConfigurationSnapshotNode.ItemId"/>.</summary>
    Collection
}
