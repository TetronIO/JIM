// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Shortens a Container's name against the partition it is being listed under (#1275).
///
/// A directory names a container by its full Distinguished Name, so a partition's container list repeated the
/// partition's own suffix on every row, under a heading that had just said it. On a deep tree that is most of the
/// width of the row spent on the part every row has in common, and the part that actually distinguishes one
/// container from another is pushed to the left edge of a wall of identical text.
/// </summary>
public static class ContainerDisplayName
{
    /// <summary>
    /// The container's name with the partition's name removed from its tail, or the name unchanged where it does
    /// not end in the partition's name. Connectors that do not name containers hierarchically are therefore
    /// unaffected: nothing matches, and nothing is trimmed.
    /// </summary>
    public static string RelativeTo(string? containerName, string? partitionName)
    {
        if (string.IsNullOrEmpty(containerName))
            return string.Empty;

        // The comparison is case-insensitive because directories treat Distinguished Names that way and routinely
        // return the same suffix with different casing at different depths; a case-sensitive match would shorten
        // some rows in a list and not others, which reads as a bug rather than as a convention.
        if (string.IsNullOrEmpty(partitionName) ||
            !containerName.EndsWith(partitionName, StringComparison.OrdinalIgnoreCase))
        {
            return containerName;
        }

        var relative = containerName[..^partitionName.Length].TrimEnd();

        // A container whose name *is* the partition's keeps its own name: there is nothing left of it otherwise, and
        // a blank row would be worse than a repeated suffix.
        if (relative.Length == 0)
            return containerName;

        // The separator between the relative part and the suffix belongs to the suffix, not to the name.
        return relative.EndsWith(',') ? relative[..^1] : relative;
    }
}
