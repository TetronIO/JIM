// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.History;

/// <summary>
/// What an administrator asks of the Metaverse Object change search: changes in a time range, to objects of a type,
/// whose attributes changed in stated ways. Every criterion is optional; none at all returns every recorded change.
/// </summary>
public class MetaverseObjectChangeSearchCriteria
{
    /// <summary>
    /// Only changes made on or after this time (UTC).
    /// </summary>
    public DateTime? FromDate { get; set; }

    /// <summary>
    /// Only changes made on or before this time (UTC).
    /// </summary>
    public DateTime? ToDate { get; set; }

    /// <summary>
    /// Only changes to Metaverse Objects of this Metaverse Object Type, including objects since deleted.
    /// </summary>
    public int? ObjectTypeId { get; set; }

    /// <summary>
    /// Only changes to Metaverse Objects whose display name contains this text.
    /// </summary>
    public string? DisplayNameSearch { get; set; }

    public List<MetaverseObjectChangeAttributeCondition> AttributeConditions { get; set; } = [];

    public AttributeConditionMatchMode MatchMode { get; set; } = AttributeConditionMatchMode.All;
}
