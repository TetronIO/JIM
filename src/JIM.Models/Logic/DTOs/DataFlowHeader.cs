// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.DTOs;

/// <summary>
/// One attribute data flow, in either direction, for the system-wide Data Flow view (#1199). A flow is one
/// Synchronisation Rule mapping: an Import flow reads one or more Connected System attributes and writes a single
/// Metaverse Attribute; an Export flow reads one or more Metaverse Attributes and writes a single Connected System
/// attribute. The target is therefore always singular and the source side is a list.
///
/// Priority and "Null is a value" are Attribute Priority concerns and appear on Import flows only; Enforce State is
/// a Drift Correction concern and appears on Export flows only.
/// </summary>
public class DataFlowHeader
{
    /// <summary>
    /// The Synchronisation Rule mapping this flow is. Identifies the flow uniquely.
    /// </summary>
    public int SyncRuleMappingId { get; set; }

    public int SyncRuleId { get; set; }

    public string SyncRuleName { get; set; } = null!;

    /// <summary>
    /// Whether the owning Synchronisation Rule is enabled. A disabled rule's flows are shown, because they remain
    /// configuration an administrator is reasoning about, but they never move data.
    /// </summary>
    public bool SyncRuleEnabled { get; set; }

    public SyncRuleDirection Direction { get; set; }

    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = null!;

    public int ConnectedSystemObjectTypeId { get; set; }

    public string ConnectedSystemObjectTypeName { get; set; } = null!;

    public int MetaverseObjectTypeId { get; set; }

    public string MetaverseObjectTypeName { get; set; } = null!;

    /// <summary>
    /// The Metaverse Attribute an Import flow writes. Null on an Export flow, where the Metaverse side is the source.
    /// </summary>
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>
    /// The Metaverse Attribute an Import flow writes. Null on an Export flow.
    /// </summary>
    public string? TargetMetaverseAttributeName { get; set; }

    /// <summary>
    /// The Connected System attribute an Export flow writes. Null on an Import flow, where the Connected System side
    /// is the source.
    /// </summary>
    public int? TargetConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// The Connected System attribute an Export flow writes. Null on an Import flow.
    /// </summary>
    public string? TargetConnectedSystemAttributeName { get; set; }

    /// <summary>
    /// What feeds the target: Connected System attributes on an Import flow, Metaverse Attributes on an Export flow,
    /// or an expression on either.
    /// </summary>
    public List<DataFlowSource> Sources { get; set; } = new();

    /// <summary>
    /// The flow's position in its target Metaverse Attribute's priority order, 1 being highest. Import flows only.
    /// A flow that has never been ordered carries the safe-addition sentinel (<see cref="int.MaxValue"/>), which the
    /// portal renders as unranked rather than as a number nobody chose.
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// Whether this contribution asserts "no value" authoritatively rather than falling through to the next
    /// contributor. Import flows only.
    /// </summary>
    public bool? NullIsValue { get; set; }

    /// <summary>
    /// Whether the owning Export Synchronisation Rule corrects the Connected System when its value diverges from the
    /// Metaverse. Export flows only.
    /// </summary>
    public bool? EnforceState { get; set; }

    /// <summary>
    /// How many flows contribute to this flow's target Metaverse Attribute for this Metaverse Object Type. Import
    /// flows only, and only meaningful there: a count above one is what makes priority matter. Populated by the
    /// application layer rather than the query, which sees one flow at a time.
    /// </summary>
    public int? ContributorCount { get; set; }

    /// <summary>
    /// Whether the flow's target Metaverse Attribute has more than one contributor, so its priority order decides
    /// which value wins. Import flows only.
    /// </summary>
    public bool HasMultipleContributors => ContributorCount > 1;
}
