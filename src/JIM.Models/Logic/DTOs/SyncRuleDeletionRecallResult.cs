// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.DTOs;

/// <summary>
/// Summary statistics for an executed Synchronisation Rule deletion recall task (#1537): what the recall
/// withdrew, what surviving contributors took over, and what was staged for export, recorded on the task's
/// Activity and logged at completion.
/// </summary>
public class SyncRuleDeletionRecallResult
{
    /// <summary>
    /// How many Metaverse Objects held values contributed by the deleted rule and were processed.
    /// </summary>
    public int MetaverseObjectsProcessed { get; set; }

    /// <summary>
    /// How many attribute values the recall withdrew (the deleted rule's contributions).
    /// </summary>
    public int ValuesRecalled { get; set; }

    /// <summary>
    /// How many attribute values a surviving contributor was re-elected for (the attribute handed over
    /// rather than blanked).
    /// </summary>
    public int AttributesReElected { get; set; }

    /// <summary>
    /// How many attributes were genuinely cleared: recalled with no surviving contributor and no other
    /// value remaining (the No Contributor outcome).
    /// </summary>
    public int AttributesCleared { get; set; }

    /// <summary>
    /// How many Pending Exports the recall staged for mapped target systems.
    /// </summary>
    public int PendingExportsStaged { get; set; }
}
