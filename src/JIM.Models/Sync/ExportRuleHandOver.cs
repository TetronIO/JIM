// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// Records a Connected System Object handed from one export Synchronisation Rule to another (#2822): its Metaverse Object
/// left the scope of the rule that managed it while another enabled export rule on the same Connected System and Object
/// Type still covers it, so that rule took the object over instead of it being deprovisioned. Surfaced as a
/// <c>HandedToAnotherExportRule</c> outcome on the object's Run Profile Execution Item.
/// </summary>
public class ExportRuleHandOver
{
    /// <summary>
    /// The Connected System Object handed over.
    /// </summary>
    public required Guid ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The Metaverse Object the Connected System Object is joined to.
    /// </summary>
    public required Guid MetaverseObjectId { get; set; }

    /// <summary>
    /// The Connected System the Connected System Object belongs to.
    /// </summary>
    public required int ConnectedSystemId { get; set; }

    /// <summary>
    /// The export Synchronisation Rule whose scope the Metaverse Object left.
    /// </summary>
    public required int DepartedSyncRuleId { get; set; }

    /// <summary>
    /// The name of the export Synchronisation Rule whose scope the Metaverse Object left.
    /// </summary>
    public required string DepartedSyncRuleName { get; set; }

    /// <summary>
    /// The export Synchronisation Rule that still covers the Metaverse Object and now manages the Connected System Object.
    /// </summary>
    public required int CoveringSyncRuleId { get; set; }

    /// <summary>
    /// The name of the export Synchronisation Rule that now manages the Connected System Object.
    /// </summary>
    public required string CoveringSyncRuleName { get; set; }

    /// <summary>
    /// Says what happened, for the administrator: the detail of the run's outcome and of the Synchronisation Preview's alike.
    /// </summary>
    public string DescribeForAdministrator() =>
        $"Left the scope of '{DepartedSyncRuleName}'. '{CoveringSyncRuleName}' still covers this object, so it now manages the " +
        "account instead of it being deprovisioned.";
}
