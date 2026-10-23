// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.History;

/// <summary>
/// A validated Metaverse Object change search, built from <see cref="MetaverseObjectChangeSearchCriteria"/> once each
/// attribute condition has been checked against its attribute's definition.
/// </summary>
public class MetaverseObjectChangeSearchQuery
{
    public DateTime? FromDate { get; set; }

    public DateTime? ToDate { get; set; }

    public int? ObjectTypeId { get; set; }

    public string? DisplayNameSearch { get; set; }

    public List<ResolvedAttributeChangeCondition> Conditions { get; set; } = [];

    public AttributeConditionMatchMode MatchMode { get; set; } = AttributeConditionMatchMode.All;
}
