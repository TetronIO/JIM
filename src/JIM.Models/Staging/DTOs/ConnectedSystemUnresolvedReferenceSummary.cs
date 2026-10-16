// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// An overview of every unresolved reference value across a Connected System's Connector Space (#959): how many there
/// are and on how many objects, how they split by likely cause, and every next step with the values and objects it
/// addresses. What the filtered Connector Space shows as "Where they point".
/// </summary>
public class ConnectedSystemUnresolvedReferenceSummary
{
    /// <summary>The Connected System summarised.</summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The Connected System's Unresolved Reference Handling as it stands now, which decides whether its runs report the
    /// values summarised here.
    /// </summary>
    public UnresolvedReferenceHandling UnresolvedReferenceHandling { get; set; }

    /// <summary>How many reference values across the Connected System are unresolved.</summary>
    public int TotalCount { get; set; }

    /// <summary>How many Connected System Objects hold at least one of them.</summary>
    public int ObjectCount { get; set; }

    /// <summary>The likely causes, most frequent first.</summary>
    public List<UnresolvedReferenceCauseCount> Causes { get; set; } = [];

    /// <summary>
    /// Every distinct next step, with how many values and objects each addresses, most values first: one per change to
    /// JIM's configuration, and one per cause whose fix lies in the Connected System.
    /// </summary>
    public List<UnresolvedReferenceFix> Fixes { get; set; } = [];

    /// <summary>
    /// The JIM configuration change that would address the most values, or null where every cause lies in the Connected
    /// System.
    /// </summary>
    public UnresolvedReferenceFix? LargestFix { get; set; }
}
