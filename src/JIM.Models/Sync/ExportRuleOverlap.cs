// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// Records a Metaverse Object that two or more enabled export Synchronisation Rules have in scope for the same Connected
/// System and Connected System Object Type. Nothing is staged for any of those rules on this object; its other Connected
/// Systems are unaffected. Surfaced to the administrator as an <c>ExportRuleOverlap</c> Run Profile Execution Item error.
/// </summary>
/// <remarks>
/// A Metaverse Object holds one Connected System Object per Connected System, and only one export rule may manage it until
/// several rules per object are supported (#2826, #2827). Before #2820 each overlapping rule staged its own Pending Export
/// for the one object; the page's flush then failed on IX_PendingExports_ConnectedSystemObjectId_Unique (the in-memory
/// repository's equivalent check in tests), losing every object on the page.
/// </remarks>
public class ExportRuleOverlap
{
    /// <summary>
    /// The Metaverse Object the rules cover.
    /// </summary>
    public required Guid MetaverseObjectId { get; set; }

    /// <summary>
    /// The Connected System the rules export to.
    /// </summary>
    public required int ConnectedSystemId { get; set; }

    /// <summary>
    /// The name of the Connected System the rules export to.
    /// </summary>
    public required string ConnectedSystemName { get; set; }

    /// <summary>
    /// The name of the Connected System Object Type the rules target.
    /// </summary>
    public required string ObjectTypeName { get; set; }

    /// <summary>
    /// The names of the overlapping export Synchronisation Rules, in the order the run evaluated them.
    /// </summary>
    public required List<string> SyncRuleNames { get; set; }

    /// <summary>
    /// The Connected System Object the Metaverse Object already holds in that Connected System, or null when it has none
    /// yet (the rules would both have provisioned one).
    /// </summary>
    public Guid? ExistingConnectedSystemObjectId { get; set; }

    /// <summary>
    /// Says what happened and how to fix it, for the administrator: the error message of the run's execution item and of
    /// the Synchronisation Preview's error alike, so the two read the same.
    /// </summary>
    public string DescribeForAdministrator()
    {
        var ruleNames = string.Join(", ", SyncRuleNames.Select(name => $"'{name}'"));
        return $"This Metaverse Object is in scope of {SyncRuleNames.Count} export Synchronisation Rules that target " +
               $"'{ObjectTypeName}' in '{ConnectedSystemName}': {ruleNames}. Only one export rule may manage an object, so " +
               "nothing is exported to that Connected System for it. Give the rules scopes that never overlap, or combine " +
               "them into one rule.";
    }
}
