// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// One piece of Activity history evidencing how a Connected System Object was joined (#348): a projection or inbound
/// join recorded against the object, or the synchronisation that staged its provisioning. Used to link a join to its
/// Activity, and to name the joining Synchronisation Rule for objects joined before joins were recorded.
/// </summary>
public sealed class JoinHistoryEntry
{
    public Guid ConnectedSystemObjectId { get; set; }

    /// <summary>The kind of join this history evidences.</summary>
    public ConnectedSystemObjectJoinType JoinType { get; set; }

    public Guid ActivityId { get; set; }

    /// <summary>When the Activity was created, in UTC; the start of the window the join was made in.</summary>
    public DateTime ActivityCreated { get; set; }

    /// <summary>How long the Activity took from creation to completion; null while it runs or where not recorded.</summary>
    public TimeSpan? ActivityDuration { get; set; }

    public Guid RunProfileExecutionItemId { get; set; }

    /// <summary>The Synchronisation Rule the history names, where it names one (an inbound join names none).</summary>
    public int? SyncRuleId { get; set; }

    public string? SyncRuleName { get; set; }
}
