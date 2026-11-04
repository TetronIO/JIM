// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// A Run Profile that targets a Partition a hierarchy refresh removes, or would remove.
/// </summary>
/// <remarks>#2740.</remarks>
public class HierarchyRefreshRunProfileItem
{
    /// <summary>
    /// The Run Profile's id.
    /// </summary>
    public int RunProfileId { get; set; }

    /// <summary>
    /// The Run Profile's name.
    /// </summary>
    public string RunProfileName { get; set; } = null!;

    /// <summary>
    /// The name of the Partition it targets, which the Connected System no longer has.
    /// </summary>
    public string PartitionName { get; set; } = null!;

    /// <summary>
    /// Whether the refresh disabled it. False when the refresh was cancelled and nothing was changed.
    /// </summary>
    public bool Disabled { get; set; }
}
