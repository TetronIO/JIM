// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// What the portal says about Metaverse-Derived Attribute Flows (#1750), in one place: the Derived chip and its
/// tooltip, the loop list in the Attribute Flow dialog, and the dependants confirmation. Administrators see "step N of
/// M", never the dependency graph's underlying level (product owner, 2026-10-01). Kept as plain strings, rendered
/// through Blazor's encoder by the components, because attribute and Synchronisation Rule names are administrator data.
/// <para>
/// Every word comes from <c>SyncRuleText</c> in the viewer's language (#3030). Each sentence is one whole template, so
/// no English is assembled from translated fragments.
/// </para>
/// </summary>
public static class DerivedFlowWording
{
    /// <summary>
    /// "A", "A and B", "A, B and C", or with "or" in place of "and" (<paramref name="conjunction"/> "or").
    /// </summary>
    public static string JoinNames(IReadOnlyList<string> names, string conjunction = "and")
    {
        ArgumentNullException.ThrowIfNull(names);

        return names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            _ => string.Join(SyncRuleText.ListSeparator, names.Take(names.Count - 1))
                 + (conjunction == "or" ? SyncRuleText.ListOr : SyncRuleText.ListAnd)
                 + names[^1]
        };
    }

    /// <summary>
    /// The Derived chip's label: "Derived · step 2", or "Derived" for a flow that cannot be ordered (it is on a loop).
    /// </summary>
    public static string ChipLabel(DerivedFlowStepInfo stepInfo)
    {
        ArgumentNullException.ThrowIfNull(stepInfo);
        return stepInfo.Step is { } step
            ? string.Format(CultureInfo.CurrentCulture, SyncRuleText.DerivedChipStep, step)
            : SyncRuleText.DerivedChipPlain;
    }

    /// <summary>
    /// The Derived chip's tooltip: what the flow reads from the Metaverse, and when it runs. Two sentences, so the
    /// tooltip renders them one per line.
    /// </summary>
    public static string ChipTooltip(DerivedFlowStepInfo stepInfo)
    {
        ArgumentNullException.ThrowIfNull(stepInfo);

        var reads = string.Format(CultureInfo.CurrentCulture, SyncRuleText.DerivedReads, JoinNames(stepInfo.MetaverseInputs));
        return stepInfo.Step is { } step
            ? string.Format(CultureInfo.CurrentCulture, SyncRuleText.SentenceAndSentence, reads,
                string.Format(CultureInfo.CurrentCulture, SyncRuleText.DerivedRunsInStep, step, stepInfo.StepCount,
                    ResolvedPhrase(stepInfo.MetaverseInputs)))
            : string.Format(CultureInfo.CurrentCulture, SyncRuleText.DerivedNoStep, reads);
    }

    /// <summary>
    /// "Account Name is resolved", "First Name and Last Name are resolved".
    /// </summary>
    public static string ResolvedPhrase(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return string.Format(CultureInfo.CurrentCulture,
            names.Count == 1 ? SyncRuleText.DerivedResolvedOne : SyncRuleText.DerivedResolvedOther,
            JoinNames(names));
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

        var where = link.IsAnalysedFlow
            ? string.Format(CultureInfo.CurrentCulture, SyncRuleText.DerivedCycleThisFlow, link.SyncRuleName)
            : link.SyncRuleName;
        return string.Format(CultureInfo.CurrentCulture, SyncRuleText.DerivedCycleLink,
            link.MetaverseAttributeName, link.ReadsMetaverseAttributeName, where);
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

        return string.Format(CultureInfo.CurrentCulture, SyncRuleText.DerivedReadsVia, input.Via[0],
            string.Join(SyncRuleText.ListSeparator, input.Via.Skip(1).Append(input.MetaverseAttributeName)));
    }

    /// <summary>
    /// The sentence above the dependants table: "After you remove the Attribute Flow to Account Name, nothing else
    /// contributes Account Name. These Derived Attribute Flows read it, so they will have no input:".
    /// </summary>
    /// <param name="change">The change, as the infinitive phrase after "After you", for example "remove the
    /// Attribute Flow to Account Name" or "disable this Synchronisation Rule". Already translated by the caller.</param>
    /// <param name="dependants">The dependants the change would leave with a missing input.</param>
    public static string DependantsIntro(string change, IReadOnlyList<DependentDerivedFlow> dependants)
    {
        ArgumentNullException.ThrowIfNull(dependants);

        var lost = dependants
            .SelectMany(dependant => dependant.MissingInputs)
            .Select(input => input.MetaverseAttributeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var template = (dependants.Count == 1, lost.Count == 1) switch
        {
            (true, true) => SyncRuleText.DependantsSingleFlowSingleInput,
            (true, false) => SyncRuleText.DependantsSingleFlowManyInputs,
            (false, true) => SyncRuleText.DependantsManyFlowsSingleInput,
            _ => SyncRuleText.DependantsManyFlowsManyInputs
        };

        return string.Format(CultureInfo.CurrentCulture, template, change, JoinNames(lost, "or"));
    }
}
