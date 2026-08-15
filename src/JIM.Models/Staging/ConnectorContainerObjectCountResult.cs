// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What a Connector found when asked how many objects each of its Containers holds (#1276).
/// </summary>
public class ConnectorContainerObjectCountResult
{
    /// <summary>
    /// One count per Container identifier, in the Connector's own terms: the Distinguished Name for a directory,
    /// matching what partition discovery reported as each Container's id.
    /// </summary>
    /// <remarks>
    /// Counts objects sitting <i>directly</i> in each Container. JIM rolls those up into subtree totals against the
    /// hierarchy it holds, so the Connector never has to understand JIM's Container Scope setting, and the two can
    /// never disagree about what a Subtree statement reaches.
    ///
    /// A Container the search covered and found nothing in may be omitted; JIM reports an absent Container as zero,
    /// because a Container that was searched holds what the search returned. An identifier JIM does not hold in its
    /// hierarchy is not an error: JIM attributes those objects to the nearest Container above them.
    /// </remarks>
    public Dictionary<string, int> DirectCountsByContainerIdentifier { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether every matching object was counted. False when the search was cut short, by a server-imposed size or
    /// time limit, or by cancellation.
    /// </summary>
    /// <remarks>
    /// A truncated count is worse than no count: it reads as a complete answer and it is smaller than the truth, so
    /// an administrator deselecting a Container on the strength of it is told the change costs less than it does.
    /// JIM labels an incomplete result rather than presenting the figures plainly.
    /// </remarks>
    public bool Complete { get; set; } = true;

    /// <summary>
    /// Why the count is incomplete, in terms an administrator can act on. Null when <see cref="Complete"/> is true.
    /// </summary>
    public string? IncompleteReason { get; set; }
}
