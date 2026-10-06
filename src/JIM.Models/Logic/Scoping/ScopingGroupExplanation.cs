// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Search;

namespace JIM.Models.Logic.Scoping;

/// <summary>
/// How one scoping criteria group evaluated (#348). Its children are its criteria followed by its child groups, the
/// order the evaluator takes them in and the order <see cref="Path"/> numbers them.
/// </summary>
public sealed class ScopingGroupExplanation
{
    public int GroupId { get; set; }

    /// <summary>
    /// Where the group sits: the top-level group's one-based position, then each child's one-based position within
    /// its group, dot-separated, criteria counted before child groups (<c>1.3</c> is the third child of the first
    /// top-level group).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    public SearchGroupType Type { get; set; }

    /// <summary>
    /// Whether the group is met; null when it holds an <see cref="ScopingCriterionOutcome.Invalid"/> criterion at
    /// any depth, since synchronisation fails on reaching it. An empty group is met.
    /// </summary>
    public bool? Met { get; set; }

    /// <summary>How many of the group's children (criteria and child groups) are met.</summary>
    public int MetCount { get; set; }

    /// <summary>How many children (criteria and child groups) the group has.</summary>
    public int ChildCount { get; set; }

    public List<ScopingCriterionExplanation> Criteria { get; set; } = [];

    public List<ScopingGroupExplanation> ChildGroups { get; set; } = [];

    /// <summary>The group's line in the tree, for example "Any one of these (none met)".</summary>
    public string Description { get; set; } = string.Empty;
}
