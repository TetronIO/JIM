// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// An overview of a Connected System Object's unresolved reference values (#959): how many there are, how they split
/// by likely cause, and what to do about them.
/// </summary>
public class ConnectedSystemObjectUnresolvedReferenceSummary
{
    /// <summary>How many reference values on the object are unresolved.</summary>
    public int TotalCount { get; set; }

    /// <summary>The likely causes, most frequent first.</summary>
    public List<UnresolvedReferenceCauseCount> Causes { get; set; } = [];

    /// <summary>
    /// Every distinct next step, with how many values each addresses, most values first: one per change to JIM's
    /// configuration (a Container to select, say), and one per cause whose fix lies in the Connected System.
    /// </summary>
    public List<UnresolvedReferenceFix> Fixes { get; set; } = [];

    /// <summary>
    /// The JIM configuration change that would address the most values (a Container to select, an exclusion to
    /// lift, a partition to select, or a reference target to declare), with how many it covers. Null where every
    /// cause lies in the Connected System.
    /// </summary>
    public UnresolvedReferenceFix? LargestFix { get; set; }
}
