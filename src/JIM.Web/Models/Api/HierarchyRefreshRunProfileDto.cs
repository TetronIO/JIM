// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Run Profile that targets a partition a hierarchy refresh removes, or would remove.
/// </summary>
public class HierarchyRefreshRunProfileDto
{
    /// <summary>
    /// The Run Profile's ID.
    /// </summary>
    public int RunProfileId { get; set; }

    /// <summary>
    /// The Run Profile's name.
    /// </summary>
    public string RunProfileName { get; set; } = null!;

    /// <summary>
    /// The name of the partition it targets, which the Connected System no longer has.
    /// </summary>
    public string PartitionName { get; set; } = null!;

    /// <summary>
    /// Whether the refresh disabled it. False when the refresh was cancelled and nothing was changed.
    /// </summary>
    public bool Disabled { get; set; }

    /// <summary>
    /// Creates an API DTO from the domain model.
    /// </summary>
    public static HierarchyRefreshRunProfileDto FromModel(HierarchyRefreshRunProfileItem item) => new()
    {
        RunProfileId = item.RunProfileId,
        RunProfileName = item.RunProfileName,
        PartitionName = item.PartitionName,
        Disabled = item.Disabled
    };
}
