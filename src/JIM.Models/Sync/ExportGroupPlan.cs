// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Models.Sync;

/// <summary>
/// How the export Synchronisation Rules covering one Metaverse Object in one Connected System and Connected System Object
/// Type are staged together (#2826): into the one Connected System Object the Metaverse Object holds there, and so into its
/// one Pending Export. Built by <c>SyncEngine.PlanExportGroups</c>.
/// </summary>
public sealed class ExportGroupPlan
{
    /// <summary>
    /// The Connected System the group's rules export to.
    /// </summary>
    public required int ConnectedSystemId { get; init; }

    /// <summary>
    /// The Connected System Object Type the group's rules target.
    /// </summary>
    public required int ConnectedSystemObjectTypeId { get; init; }

    /// <summary>
    /// The group's rules in staging order: the rule that manages the account, or where none does the first rule that
    /// provisions, then the rest by id. The first rule creates the account where one is created, so every other rule's
    /// changes join its Create.
    /// </summary>
    public required IReadOnlyList<SyncRule> OrderedRules { get; init; }

    /// <summary>
    /// The target attributes more than one of the group's rules writes. Neither rule stages them, and Drift Detection
    /// corrects neither; the account keeps what it holds until the configuration gives the attribute one writer.
    /// </summary>
    public required IReadOnlySet<int> ClashedAttributeIds { get; init; }

    /// <summary>
    /// The names of <see cref="ClashedAttributeIds"/>, in attribute id order, for reporting.
    /// </summary>
    public required IReadOnlyList<string> ClashedAttributeNames { get; init; }

    /// <summary>
    /// The rules that write a clashed attribute, in <see cref="OrderedRules"/> order; empty when nothing clashes.
    /// </summary>
    public required IReadOnlyList<SyncRule> ClashingRules { get; init; }

    /// <summary>
    /// Whether the group has more than one rule, so its rules' changes are merged into one Pending Export.
    /// </summary>
    public bool IsShared => OrderedRules.Count > 1;
}
