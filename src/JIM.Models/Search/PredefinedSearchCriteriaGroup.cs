// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Search;

/// <summary>
/// A logical group of search criteria combined using either All (AND) or Any (OR) logic.
/// Groups can be nested to construct complex queries.
/// </summary>
public class PredefinedSearchCriteriaGroup
{
    public int Id { get; set; }

    /// <summary>
    /// The foreign key scalar for the owning <see cref="PredefinedSearch"/>, populated only for a top-level group
    /// (attached directly to the search, as opposed to nested under another group, which instead carries
    /// <see cref="ParentGroupId"/>). Maps to the existing EF shadow FK column, so exposing this scalar changes no
    /// schema. Prefer this over navigation-based existence checks under AsNoTracking (see src/CLAUDE.md).
    /// </summary>
    public int? PredefinedSearchId { get; set; }

    /// <summary>
    /// Determines how criteria within this group are combined: All (AND) or Any (OR).
    /// </summary>
    public SearchGroupType Type { get; set; }

    /// <summary>
    /// The individual search criteria within this group.
    /// </summary>
    public List<PredefinedSearchCriteria> Criteria { get; set; } = new();

    /// <summary>
    /// The display order of this group relative to its siblings.
    /// </summary>
    public int Position { get; set; } = 0;

    /// <summary>
    /// PredefinedSearchCriteriaGroups can be nested, to enable more complex queries to be constructed, i.e. ANY(ALL(x=1,y=2),ANY(c=1,d=1))
    /// </summary>
    public List<PredefinedSearchCriteriaGroup> ChildGroups { get; set; } = new();

    /// <summary>
    /// Navigation property for child groups
    /// </summary>
    public PredefinedSearchCriteriaGroup? ParentGroup { get; set; }

    /// <summary>
    /// The foreign key scalar for <see cref="ParentGroup"/>, populated only for a nested group (as opposed to a
    /// top-level group, which instead carries <see cref="PredefinedSearchId"/>). Maps to the existing EF shadow FK
    /// column, so exposing this scalar changes no schema. Prefer this over navigation-based existence checks under
    /// AsNoTracking (see src/CLAUDE.md).
    /// </summary>
    public int? ParentGroupId { get; set; }
}