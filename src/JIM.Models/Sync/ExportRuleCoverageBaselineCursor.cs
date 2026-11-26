// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// Where the export rule coverage baseline has got to (#2826): the rule it is recording, and the last Connected System Object
/// of that rule's Object Type it has looked at. Held by the Worker between idle ticks; lost on a restart, which costs only a
/// repeat of what was done, since recording a coverage twice changes nothing.
/// </summary>
public class ExportRuleCoverageBaselineCursor
{
    /// <summary>
    /// The export rule being recorded, or null before the first page.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The last Connected System Object looked at for <see cref="SyncRuleId"/>; the next page starts after it.
    /// </summary>
    public Guid AfterConnectedSystemObjectId { get; set; }
}
