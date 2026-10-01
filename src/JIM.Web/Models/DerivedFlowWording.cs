// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// What the portal says about Metaverse-Derived Attribute Flows (#1750), in one place: the Derived chip and its
/// tooltip, the loop list in the Attribute Flow dialog, and the dependants confirmation. Administrators see "step N of
/// M", never the dependency graph's underlying level (product owner, 2026-10-01). Kept as plain strings, rendered
/// through Blazor's encoder by the components, because attribute and Synchronisation Rule names are administrator data.
/// </summary>
public static class DerivedFlowWording
{
    /// <summary>
    /// "A", "A and B", "A, B and C" (or with "or").
    /// </summary>
    public static string JoinNames(IReadOnlyList<string> names, string conjunction = "and")
    {
        ArgumentNullException.ThrowIfNull(names);

        return names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} {conjunction} {names[^1]}"
        };
    }

    /// <summary>
    /// The Derived chip's label: "Derived · step 2", or "Derived" for a flow that cannot be ordered (it is on a loop).
    /// </summary>
    public static string ChipLabel(DerivedFlowStepInfo stepInfo)
    {
        ArgumentNullException.ThrowIfNull(stepInfo);
        return stepInfo.Step is { } step ? $"Derived · step {step}" : "Derived";
    }

    /// <summary>
    /// The Derived chip's tooltip: what the flow reads from the Metaverse, and when it runs. Two sentences, so the
    /// tooltip renders them one per line.
    /// </summary>
    public static string ChipTooltip(DerivedFlowStepInfo stepInfo)
    {
        ArgumentNullException.ThrowIfNull(stepInfo);

        var reads = $"Reads {JoinNames(stepInfo.MetaverseInputs)} from the Metaverse.";
        return stepInfo.Step is { } step
            ? $"{reads} Runs in step {step} of {stepInfo.StepCount}, after {ResolvedPhrase(stepInfo.MetaverseInputs)}."
            : $"{reads} It has no step because it is part of a loop of Attribute Flows that read each other.";
    }

    /// <summary>
    /// "Account Name is resolved", "First Name and Last Name are resolved".
    /// </summary>
    public static string ResolvedPhrase(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return $"{JoinNames(names)} {(names.Count == 1 ? "is" : "are")} resolved";
    }

    /// <summary>
    /// A loop's links in the order an administrator reads it: the edited flow first, then each flow worked out from the
    /// one before it. The server lists each link before the one whose attribute it reads, so after the first link the
    /// rest are reversed.
    /// </summary>
    public static IReadOnlyList<DerivedFlowAnalysisCycleLink> CycleLinksInReadingOrder(DerivedFlowAnalysisCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        return cycle.Links.Count <= 1
            ? cycle.Links
            : [cycle.Links[0], .. cycle.Links.Skip(1).Reverse()];
    }

    /// <summary>
    /// "Account Name, from User Principal Name (this flow, HR Inbound)".
    /// </summary>
    public static string DescribeCycleLink(DerivedFlowAnalysisCycleLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        var where = link.IsAnalysedFlow ? $"this flow, {link.SyncRuleName}" : link.SyncRuleName;
        return $"{link.MetaverseAttributeName}, from {link.ReadsMetaverseAttributeName} ({where})";
    }

    /// <summary>
    /// What a dependant reads that it will lose: the attribute itself when read directly, otherwise the attribute it
    /// reads and the chain down to the lost one ("Email (via Account Name)").
    /// </summary>
    public static string DescribeReads(DependentDerivedFlowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.Indirect || input.Via.Count == 0)
            return input.MetaverseAttributeName;

        return $"{input.Via[0]} (via {string.Join(", ", input.Via.Skip(1).Append(input.MetaverseAttributeName))})";
    }

    /// <summary>
    /// The sentence above the dependants table: "After you remove the Attribute Flow to Account Name, nothing else
    /// contributes Account Name. These Derived Attribute Flows read it, so they will have no input:".
    /// </summary>
    /// <param name="change">The change, as the rest of the sentence after "After you", for example "remove the
    /// Attribute Flow to Account Name" or "disable this Synchronisation Rule".</param>
    /// <param name="dependants">The dependants the change would leave with a missing input.</param>
    public static string DependantsIntro(string change, IReadOnlyList<DependentDerivedFlow> dependants)
    {
        ArgumentNullException.ThrowIfNull(dependants);

        var lost = dependants
            .SelectMany(dependant => dependant.MissingInputs)
            .Select(input => input.MetaverseAttributeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (subject, verb, pronoun, outcome) = dependants.Count == 1
            ? ("This Derived Attribute Flow", "reads", lost.Count == 1 ? "it" : "them", "it will have no input")
            : ("These Derived Attribute Flows", "read", lost.Count == 1 ? "it" : "them", "they will have no input");

        return $"After you {change}, nothing else contributes {JoinNames(lost, "or")}. {subject} {verb} {pronoun}, so {outcome}:";
    }
}
