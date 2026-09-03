// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Summary statistics for an executed stranded-value sweep (#1549): what the sweep found, recalled,
/// re-elected, cleared and preserved, across every import Synchronisation Rule of the swept Connected
/// System; recorded on the Full Synchronisation Activity and logged at completion.
/// </summary>
public class StrandedValueSweepResult
{
    /// <summary>
    /// How many of the system's import Synchronisation Rules had at least one stranded candidate and were
    /// processed by the recall engine. Excludes rules skipped for policy (Connected System Object Type has
    /// RemoveContributedAttributesOnObsoletion disabled) and rules with no stranded candidates at all.
    /// </summary>
    public int SyncRulesSwept { get; set; }

    /// <summary>
    /// How many Metaverse Objects held stranded values and were processed (recalled, re-elected or cleared;
    /// excludes objects whose values were preserved under the #1570 gate, see
    /// <see cref="MetaverseObjectsPreserved"/>).
    /// </summary>
    public int MetaverseObjectsProcessed { get; set; }

    /// <summary>
    /// How many attribute values the sweep withdrew (the stranded contributions).
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
    /// How many Metaverse Objects had their recall skipped under the #1570 last-known-state preservation
    /// gate: no remaining joined Connected System carries an enabled import Synchronisation Rule for the
    /// object's type, so its values were kept as-is.
    /// </summary>
    public int MetaverseObjectsPreserved { get; set; }

    /// <summary>
    /// How many attribute values were preserved rather than recalled under the gate above.
    /// </summary>
    public int ValuesPreserved { get; set; }

    /// <summary>
    /// How many Pending Exports the sweep staged for mapped target systems.
    /// </summary>
    public int PendingExportsStaged { get; set; }

    /// <summary>
    /// True when the sweep was armed but the #1605 Full Import gate was closed, so nothing above was
    /// touched: no recall, no marking, nothing staged. The arming stays in place for the next run. Every
    /// counter above is zero when this is true.
    /// </summary>
    public bool Skipped { get; set; }

    /// <summary>
    /// The sentence explaining why the sweep was skipped, appended to the Full Synchronisation Activity's
    /// Message. Null unless <see cref="Skipped"/> is true.
    /// </summary>
    public string? SkipReason { get; set; }
}
