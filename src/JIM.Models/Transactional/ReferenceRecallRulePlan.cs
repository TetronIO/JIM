// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// The run-shape classification reference recall staging uses to route referencing Metaverse
/// Objects between the set-based fast path and the full-evaluation fallback (#1003). Built from
/// the export rule cache and the candidate reference attribute ids; the classification depends
/// only on Synchronisation Rule configuration, never on data, so it is deterministic for a run.
/// </summary>
public class ReferenceRecallRulePlan
{
    /// <summary>
    /// Fast-path flows: per Metaverse Object Type id, the direct single-source reference mappings
    /// keyed by source Metaverse Attribute id. A referencing object of one of these types has its
    /// removals synthesised directly from these flows.
    /// </summary>
    public Dictionary<int, Dictionary<int, List<ReferenceRecallDirectFlow>>> DirectFlowsByTypeThenAttribute { get; init; } = [];

    /// <summary>
    /// Metaverse Object Type ids that must use the full-evaluation fallback because at least one
    /// applicable export rule sources a candidate reference attribute through an expression or a
    /// multi-source chain. The whole type falls back (all its rules evaluate together and merge
    /// into one Pending Export per CSO, so paths cannot be mixed per rule).
    /// </summary>
    public HashSet<int> FallbackTypeIds { get; init; } = [];

    /// <summary>
    /// Union of Metaverse Attribute ids referenced by fast-path rules' scoping criteria, used to
    /// lean-load only those attribute values for scope evaluation.
    /// </summary>
    public HashSet<int> ScopingAttributeIds { get; init; } = [];

    /// <summary>
    /// Distinct target Connected System ids across all fast-path flows.
    /// </summary>
    public HashSet<int> FastTargetSystemIds { get; init; } = [];
}

/// <summary>
/// One fast-path flow: a direct single-source reference mapping from a Metaverse Attribute to a
/// Connected System attribute under a specific export Synchronisation Rule.
/// </summary>
public class ReferenceRecallDirectFlow
{
    public required SyncRule ExportRule { get; init; }

    public required ConnectedSystemObjectTypeAttribute TargetAttribute { get; init; }

    /// <summary>
    /// The source Metaverse Attribute's plurality: multi-valued sources synthesise Remove changes
    /// carrying the resolved value; single-valued sources synthesise a null-clearing Update change.
    /// </summary>
    public required AttributePlurality SourcePlurality { get; init; }
}
