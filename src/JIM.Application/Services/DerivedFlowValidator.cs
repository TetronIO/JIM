// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Application.Services;

/// <summary>
/// Save-time validation of Metaverse-Derived Attribute Flows (#1750, FR 2 and FR 12). Pure: it reads a
/// <see cref="DerivedFlowGraph"/> the caller built over every import Synchronisation Rule of the Metaverse Object
/// Type, with the proposal substituted and disabled mappings included (<see cref="DerivedFlowGraphScope.AllMappings"/>,
/// plan decision 2).
/// </summary>
/// <remarks>
/// Only problems that involve a proposed mapping are reported: a save is refused for what it would create, not for
/// someone else's configuration. Errors: a dependency cycle or self-reference (naming every attribute and
/// Synchronisation Rule on it), an <c>mv["..."]</c> name that is not an attribute of the type, and a Reference-typed
/// input or target (decision 14, tracked by #1861). Warning, never blocking: a derived expression calling a function
/// that returns a different value on every evaluation (decision 15).
/// </remarks>
public static class DerivedFlowValidator
{
    /// <summary>
    /// Validates the proposed mappings against the graph.
    /// </summary>
    /// <param name="graph">The graph over every import rule of the type, proposal substituted, disabled included.</param>
    /// <param name="proposedMappings">The mappings being saved (the single mapping, or every mapping of the rule being
    /// saved). Compared by reference.</param>
    public static DerivedFlowValidationResult Validate(DerivedFlowGraph graph, IReadOnlyCollection<SyncRuleMapping> proposedMappings)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(proposedMappings);

        var proposed = new HashSet<SyncRuleMapping>(proposedMappings, ReferenceEqualityComparer.Instance);
        var errors = new List<string>();

        // A cycle blocks a save only through a proposed mapping that is enabled. A cycle can already exist (saved with
        // the flag off, when nothing is validated, or by two concurrent saves), and disabling one of its mappings is how
        // an administrator breaks it, so that save must go through. Re-enabling the mapping is validated like any other
        // save, and the graph includes disabled mappings (decision 2), so it cannot slip a cycle back in. Deliberately
        // keyed on the mapping, not its rule: a rule's own Enabled switch has save paths of its own, and exempting a
        // disabled rule here would let one of those bring a cycle live unvalidated.
        errors.AddRange(graph.Cycles
            .Where(cycle => cycle.Members.Concat(cycle.AdditionalMembers).Any(member =>
                proposed.Contains(member.Mapping) && member.Mapping.Enabled))
            .Select(cycle => DescribeCycle(cycle, proposed)));

        errors.AddRange(graph.UnknownInputs
            .Where(unknown => proposed.Contains(unknown.Mapping))
            .Select(unknown => DescribeUnknownInput(graph, unknown)));

        var proposedFlows = proposedMappings
            .Select(graph.GetDerivedFlow)
            .Where(flow => flow != null)
            .Select(flow => flow!)
            .ToList();

        foreach (var flow in proposedFlows)
        {
            errors.AddRange(flow.Inputs
                .Where(input => input.Type == AttributeDataType.Reference)
                .Select(input =>
                    $"{DescribeFlow(flow)} reads {input.Name}, a Reference attribute; reading a Reference attribute in an " +
                    "Attribute Flow that derives a Metaverse attribute is not supported yet."));

            if (flow.TargetAttributeType == AttributeDataType.Reference)
            {
                errors.Add(
                    $"{DescribeFlow(flow)} reads Metaverse attributes, but {flow.TargetAttributeName} is a Reference attribute; " +
                    "deriving a Reference attribute from Metaverse attributes is not supported yet.");
            }
        }

        var warnings = proposedFlows
            .Select(flow => (Flow: flow, Functions: FindNonRepeatableFunctions(flow.Mapping)))
            .Where(candidate => candidate.Functions.Count > 0)
            .Select(candidate => new DerivedFlowWarning(candidate.Flow.Mapping, DescribeNonRepeatable(candidate.Flow, candidate.Functions)))
            .ToList();

        return new DerivedFlowValidationResult(errors, warnings);
    }

    private static string DescribeFlow(DerivedFlow flow) =>
        $"The Attribute Flow to {flow.TargetAttributeName} (Synchronisation Rule '{flow.SyncRule.Name}')";

    private static string DescribeUnknownInput(DerivedFlowGraph graph, DerivedFlowUnknownInput unknown)
    {
        var targetName = graph.GetDerivedFlow(unknown.Mapping)?.TargetAttributeName
            ?? unknown.Mapping.TargetMetaverseAttribute?.Name
            ?? $"ID {unknown.Mapping.TargetMetaverseAttributeId}";
        return $"The Attribute Flow to {targetName} (Synchronisation Rule '{unknown.SyncRule.Name}') reads mv[\"{unknown.AttributeName}\"], " +
               $"but '{unknown.AttributeName}' is not an attribute of the Metaverse Object Type '{unknown.MetaverseObjectTypeName}'.";
    }

    /// <summary>
    /// "Saving would create a dependency cycle: A (Synchronisation Rule 'R1') reads B, which (Synchronisation Rule 'R2')
    /// reads A." The path starts from the first proposed mapping on it, so the administrator reads it from the flow
    /// they are saving.
    /// </summary>
    private static string DescribeCycle(DerivedFlowCycle cycle, HashSet<SyncRuleMapping> proposed)
    {
        var startIndex = cycle.Members.ToList().FindIndex(member => proposed.Contains(member.Mapping));
        return "Saving would create a dependency cycle: " + DescribeCyclePath(cycle, Math.Max(startIndex, 0));
    }

    /// <summary>
    /// "A (Synchronisation Rule 'R1') reads B, which (Synchronisation Rule 'R2') reads A.", naming every attribute and
    /// Synchronisation Rule on <paramref name="cycle"/>, read from the member at <paramref name="startIndex"/>, followed
    /// by any further flows caught in the same knot of dependencies. Shared by save-time validation and the run-start
    /// refusal (<see cref="DerivedFlowGraphFactory"/>), so a cycle reads the same wherever it is reported.
    /// </summary>
    internal static string DescribeCyclePath(DerivedFlowCycle cycle, int startIndex = 0)
    {
        var members = cycle.Members;
        if (startIndex > 0)
            members = members.Skip(startIndex).Concat(members.Take(startIndex)).ToList();

        var message = new StringBuilder();
        message.Append($"{members[0].MetaverseAttributeName} (Synchronisation Rule '{members[0].SyncRule.Name}') reads {members[0].ReadsMetaverseAttributeName}");
        foreach (var member in members.Skip(1))
            message.Append($", which (Synchronisation Rule '{member.SyncRule.Name}') reads {member.ReadsMetaverseAttributeName}");
        message.Append('.');

        if (cycle.AdditionalMembers.Count > 0)
        {
            message.Append(" The same dependencies also involve ");
            message.Append(string.Join("; ", cycle.AdditionalMembers.Select(member =>
                $"{member.MetaverseAttributeName} (Synchronisation Rule '{member.SyncRule.Name}') reading {member.ReadsMetaverseAttributeName}")));
            message.Append('.');
        }

        return message.ToString();
    }

    private static List<string> FindNonRepeatableFunctions(SyncRuleMapping mapping) =>
        mapping.Sources
            .OrderBy(source => source.Order)
            .SelectMany(source => NonRepeatableFunctionDetector.Find(source.Expression))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string DescribeNonRepeatable(DerivedFlow flow, List<string> functions)
    {
        var named = functions.Count == 1
            ? functions[0]
            : $"{string.Join(", ", functions.Take(functions.Count - 1))} and {functions[^1]}";
        var (returns, it) = functions.Count == 1 ? ("returns", "it is") : ("return", "they are");

        return $"{DescribeFlow(flow)} derives its value from Metaverse attributes and calls {named}, which {returns} a different " +
               $"value each time {it} evaluated; the value will change on every synchronisation and can cause repeated exports.";
    }
}
