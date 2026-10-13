// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Synchronisation Rule deletion that recalls the values the rule contributed first. The rule is disabled at once
/// and deleted when the recall finishes.
/// </summary>
public class SyncRuleDeletionQueuedResponse
{
    /// <summary>
    /// The ID of the Activity recalling the values. The Synchronisation Rule is deleted as its last step.
    /// </summary>
    public Guid RecallActivityId { get; set; }

    /// <summary>
    /// How many Metaverse attribute values the Synchronisation Rule contributed when the deletion was asked for.
    /// </summary>
    public int AffectedValueCount { get; set; }

    /// <summary>
    /// How many Metaverse Objects held at least one of those values.
    /// </summary>
    public int AffectedObjectCount { get; set; }

    /// <summary>
    /// The Metaverse-Derived Attribute Flows on other Synchronisation Rules that the deletion leaves with a missing
    /// input, because an attribute they read lost its last enabled contributor. The rule is disabled at once, so they
    /// are affected from now. The deletion goes ahead regardless. Always present.
    /// </summary>
    public List<DependentDerivedFlow> DependentDerivedFlows { get; set; } = new();
}

/// <summary>
/// The Metaverse attribute values a Synchronisation Rule (or one of its Attribute Flow mappings) currently
/// contributes (#1537): per-attribute value and object counts plus totals, so callers can state the impact of
/// a deletion before choosing to recall or keep the values. Built from count queries only.
/// </summary>
public class ContributedValuesSummaryDto
{
    /// <summary>
    /// Per-attribute breakdown of the contributions, ordered by attribute name. Empty when nothing is
    /// contributed (including for export mappings, which contribute nothing to the Metaverse).
    /// </summary>
    public List<ContributedValuesAttributeSummaryDto> Attributes { get; set; } = [];

    /// <summary>
    /// Total contributed value rows across all attributes.
    /// </summary>
    public int TotalValues { get; set; }

    /// <summary>
    /// Distinct Metaverse Objects holding at least one contributed value. Not the sum of the per-attribute
    /// object counts: an object holding values for several attributes counts once.
    /// </summary>
    public int TotalObjects { get; set; }

    public static ContributedValuesSummaryDto FromModel(ContributedValuesSummary model)
    {
        return new ContributedValuesSummaryDto
        {
            Attributes = model.Attributes.Select(ContributedValuesAttributeSummaryDto.FromModel).ToList(),
            TotalValues = model.TotalValues,
            TotalObjects = model.TotalObjects
        };
    }
}

/// <summary>
/// One attribute's slice of a <see cref="ContributedValuesSummaryDto"/>.
/// </summary>
public class ContributedValuesAttributeSummaryDto
{
    /// <summary>
    /// The Metaverse Attribute's id.
    /// </summary>
    public int AttributeId { get; set; }

    /// <summary>
    /// The Metaverse Attribute's name, for display without a further lookup.
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// How many contributed value rows exist for this attribute (multi-valued attributes can contribute
    /// several per object).
    /// </summary>
    public int ValueCount { get; set; }

    /// <summary>
    /// How many distinct Metaverse Objects hold at least one of those values.
    /// </summary>
    public int ObjectCount { get; set; }

    public static ContributedValuesAttributeSummaryDto FromModel(ContributedValuesAttributeSummary model)
    {
        return new ContributedValuesAttributeSummaryDto
        {
            AttributeId = model.AttributeId,
            AttributeName = model.AttributeName,
            ValueCount = model.ValueCount,
            ObjectCount = model.ObjectCount
        };
    }
}
