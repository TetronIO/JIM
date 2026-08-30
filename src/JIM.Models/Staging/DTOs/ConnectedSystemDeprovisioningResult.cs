// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Summary statistics for an executed Connected System Synchronised Deprovisioning run (#809): what the
/// per-object pass processed and recalled, the Metaverse Object deletion-rule outcomes, what the residue
/// pass withdrew by provenance, and what was staged for export; recorded on the task's Activity and logged
/// at completion.
/// </summary>
public class ConnectedSystemDeprovisioningResult
{
    /// <summary>
    /// How many of the system's Connected System Objects the per-object pass processed through the
    /// obsoletion core (a resumed run counts only the objects it processed itself).
    /// </summary>
    public int ConnectedSystemObjectsProcessed { get; set; }

    /// <summary>
    /// How many attribute values a surviving contributor was re-elected for during the per-object pass (the
    /// attribute handed to another Connected System rather than blanked).
    /// </summary>
    public int AttributesReElected { get; set; }

    /// <summary>
    /// How many attributes were genuinely cleared during the per-object pass: recalled with no surviving
    /// contributor and no other value remaining (the No Contributor outcome).
    /// </summary>
    public int AttributesCleared { get; set; }

    /// <summary>
    /// How many Metaverse Objects the deletion rules deleted immediately (no grace period).
    /// </summary>
    public int MetaverseObjectsDeleted { get; set; }

    /// <summary>
    /// How many Metaverse Objects the deletion rules marked for deferred deletion (grace period configured);
    /// housekeeping deletes them once the grace window expires.
    /// </summary>
    public int MetaverseObjectsMarkedForDeletion { get; set; }

    /// <summary>
    /// How many Metaverse Objects the residue pass processed (objects holding values contributed by the
    /// system's Synchronisation Rules with no backing Connected System Object left to obsolete).
    /// </summary>
    public int ResidueMetaverseObjectsProcessed { get; set; }

    /// <summary>
    /// How many attribute values the residue pass withdrew by provenance.
    /// </summary>
    public int ResidueValuesRecalled { get; set; }

    /// <summary>
    /// How many Pending Exports the run staged for mapped target systems, across the per-object pass
    /// (recalls, re-elections, deletion cascades and reference recalls) and the residue pass.
    /// </summary>
    public int PendingExportsStaged { get; set; }
}
