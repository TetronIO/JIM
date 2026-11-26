// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Records that an export Synchronisation Rule covers a Connected System Object it does not manage (#2826): its Metaverse
/// Object is in the rule's scope, and the rule writes attributes to it. The rule that manages the object (the one that
/// provisioned it, or matched it on export) is never recorded here; the object's join record names it. Read when a rule
/// starts covering an object, so it sends every attribute it writes, and when it stops, so it can take back what it wrote.
/// </summary>
/// <remarks>
/// Live data, not history: a row exists while the coverage does, and goes with the object or the rule.
/// </remarks>
public class ExportRuleCoverage
{
    /// <summary>
    /// The Connected System Object the rule covers.
    /// </summary>
    public Guid ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The export Synchronisation Rule that covers it.
    /// </summary>
    public int SyncRuleId { get; set; }

    /// <summary>
    /// When the coverage was first recorded (UTC).
    /// </summary>
    public DateTime Recorded { get; set; }
}
