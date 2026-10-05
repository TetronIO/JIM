// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.Scoping;

/// <summary>
/// Why an object is, or is not, in scope of one Synchronisation Rule (#348): the rule's outcome and every scoping
/// group and criterion as it evaluated. Produced by the same evaluation synchronisation uses, so the outcome is the
/// one synchronisation reaches.
/// </summary>
public sealed class ScopingExplanation
{
    public int SyncRuleId { get; set; }

    public string SyncRuleName { get; set; } = string.Empty;

    public SyncRuleDirection Direction { get; set; }

    public ScopingRuleOutcome Outcome { get; set; }

    /// <summary>False when the rule has no scoping criteria, which puts every object of its type in scope.</summary>
    public bool HasCriteria { get; set; }

    /// <summary>The instant relative date criteria were resolved against, in UTC.</summary>
    public DateTime EvaluatedAt { get; set; }

    /// <summary>
    /// The top-level groups, in evaluation order. They are ORed: one met group puts the object in scope.
    /// </summary>
    public List<ScopingGroupExplanation> Groups { get; set; } = [];
}
