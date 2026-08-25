// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// The whole-population count tier of a full-system preview (#288, PRD requirement 12): how many evaluated
/// objects fall into each outcome category, and the aggregated outbound counters, without a per-object tree
/// for any of them.
/// </summary>
public class FullSyncPreviewCounts
{
    /// <summary>
    /// Objects that would project a new Metaverse Object.
    /// </summary>
    public int WouldProject { get; set; }

    /// <summary>
    /// Objects that would join an existing Metaverse Object.
    /// </summary>
    public int WouldJoin { get; set; }

    /// <summary>
    /// Objects already joined whose Attribute Flows and outbound chain were evaluated.
    /// </summary>
    public int AttributeFlow { get; set; }

    /// <summary>
    /// Objects out of scope of every applicable import Synchronisation Rule with Scoping Criteria.
    /// </summary>
    public int OutOfScope { get; set; }

    /// <summary>
    /// Objects nothing would connect: no match and no projection.
    /// </summary>
    public int NotConnected { get; set; }

    /// <summary>
    /// Objects whose preview surfaced at least one blocking error.
    /// </summary>
    public int BlockedByErrors { get; set; }

    /// <summary>
    /// Target objects that would be created across all export Synchronisation Rules (provisioning).
    /// </summary>
    public int ObjectsToCreate { get; set; }

    /// <summary>
    /// Target objects that would be updated across all export Synchronisation Rules.
    /// </summary>
    public int ObjectsToUpdate { get; set; }

    /// <summary>
    /// Target objects that would be deleted across all export Synchronisation Rules (deprovisioning).
    /// </summary>
    public int ObjectsToDelete { get; set; }

    /// <summary>
    /// Attribute changes that would be staged across all proposed exports.
    /// </summary>
    public int TotalAttributeChanges { get; set; }
}
