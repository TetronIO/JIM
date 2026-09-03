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
    /// How many Pending Exports the sweep staged for mapped target systems (value recall, Deletion Rule
    /// evaluation and the zero-join pass all contribute to this total).
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

    /// <summary>
    /// True when the gate was open but the re-join shortfall check (#1605 Functional Requirement 9)
    /// refused the reconciliation: too great a share of the objects recorded at the clear have not
    /// rejoined. Neither the value recall, the Deletion Rule evaluation nor the zero-join pass ran; the
    /// arming and the join record both stay in place so a later run can retry once the administrator has
    /// re-imported, or raised the threshold setting. Every counter above is zero when this is true.
    /// </summary>
    public bool Refused { get; set; }

    /// <summary>
    /// The sentence explaining why the sweep refused, appended to the Full Synchronisation Activity's
    /// Message. Null unless <see cref="Refused"/> is true.
    /// </summary>
    public string? RefuseReason { get; set; }

    /// <summary>
    /// How many recorded Metaverse Objects that still lack a re-join were evaluated against their type's
    /// Deletion Rule (#1605 Functional Requirement 7).
    /// </summary>
    public int MetaverseObjectsEvaluatedForDeletion { get; set; }

    /// <summary>
    /// How many of those evaluated objects were marked for deletion after a grace period.
    /// </summary>
    public int MetaverseObjectsMarkedForDeletion { get; set; }

    /// <summary>
    /// How many of those evaluated objects were deleted immediately (no grace period configured).
    /// </summary>
    public int MetaverseObjectsDeleted { get; set; }

    /// <summary>
    /// How many Metaverse Objects the state-convergent zero-join pass (#1605 Functional Requirement 10)
    /// marked for deletion: Projected objects with no joined Connected System Object at all, whose type's
    /// Deletion Rule is state-convergent. Always a marking (never an immediate delete); housekeeping
    /// removes a no-grace object on its next tick.
    /// </summary>
    public int MetaverseObjectsMarkedWithNoConnector { get; set; }
}
