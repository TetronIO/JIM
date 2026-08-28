// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// Quantifies the Metaverse attribute values a Synchronisation Rule currently contributes (#1537), so a
/// deletion surface can state the impact before the administrator chooses to recall or keep them. Built
/// from count queries only; no value rows are materialised.
/// </summary>
public class ContributedValuesSummary
{
    /// <summary>
    /// Per-attribute breakdown of the rule's contributions, ordered by attribute name.
    /// </summary>
    public List<ContributedValuesAttributeSummary> Attributes { get; set; } = [];

    /// <summary>
    /// Total contributed value rows across all attributes.
    /// </summary>
    public int TotalValues => Attributes.Sum(a => a.ValueCount);

    /// <summary>
    /// Distinct Metaverse Objects holding at least one of the rule's contributed values. Not the sum of the
    /// per-attribute object counts: an object holding values for several attributes counts once.
    /// </summary>
    public int TotalObjects { get; set; }
}

/// <summary>
/// One attribute's slice of a <see cref="ContributedValuesSummary"/>.
/// </summary>
public class ContributedValuesAttributeSummary
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
    /// How many contributed value rows the rule holds for this attribute (multi-valued attributes can
    /// contribute several per object).
    /// </summary>
    public int ValueCount { get; set; }

    /// <summary>
    /// How many distinct Metaverse Objects hold at least one of those values.
    /// </summary>
    public int ObjectCount { get; set; }
}
