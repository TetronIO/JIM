// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// An import Synchronisation Rule a joined Connected System Object has left while another import rule still covers it
/// (#2821). The rule's own Out-of-Scope Action is Disconnect and its Object Type recalls contributed values, so the
/// values it contributed give way: the covering rules replace or take them over as they flow, and whatever is left is
/// withdrawn. The join stays. Recorded on the object's execution item as a scope exit naming the rule.
/// </summary>
public class DepartedImportRule
{
    /// <summary>
    /// The Synchronisation Rule the object left.
    /// </summary>
    public required int SyncRuleId { get; init; }

    /// <summary>
    /// The Synchronisation Rule's name, as it stood when the object left it.
    /// </summary>
    public required string SyncRuleName { get; init; }

    /// <summary>
    /// How many values the rule had contributed to the Metaverse Object when the object left it: each is handed over to
    /// a covering rule, taken over by one with the same value, or withdrawn.
    /// </summary>
    public required int ContributedValueCount { get; init; }
}
