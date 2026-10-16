// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Web.Models;

namespace JIM.Web;

/// <summary>
/// How the portal shows and edits a saved configuration test case (#2055 Phase 4).
/// </summary>
public static class ConfigurationTestCaseDisplay
{
    /// <summary>
    /// An input the configuration a case covers reads now: its side (null for a Scope or Object Matching Rule case)
    /// and its type, which choose its editor.
    /// </summary>
    public sealed record InputSlot(ExpressionInputSource? Source, string AttributeName, AttributeDataType Type, AttributePlurality Plurality);

    /// <summary>
    /// One input row of the test case editor.
    /// </summary>
    /// <param name="Source">Its side, or null for a Scope or Object Matching Rule case.</param>
    /// <param name="AttributeName">The attribute's name.</param>
    /// <param name="Type">Its type, which chooses its editor; null for one the configuration no longer reads.</param>
    /// <param name="Plurality">Whether it holds one value or several; null as for <paramref name="Type"/>.</param>
    /// <param name="IsRead">Whether the configuration reads it now. An input it no longer reads is kept unless removed,
    /// and has no type to edit it as.</param>
    /// <param name="Given">Whether the case gives this input. One the configuration reads that the case does not give
    /// makes the case need review until it is saved again.</param>
    /// <param name="State">What it holds while it is edited.</param>
    public sealed record InputRow(ExpressionInputSource? Source, string AttributeName, AttributeDataType? Type, AttributePlurality? Plurality,
        bool IsRead, bool Given, TestInputState State);

    /// <summary>
    /// What <paramref name="testCase"/> covers, named from the configuration: "Attribute Flow to Display Name",
    /// "Generated Value for Account Name", "Scope", "Object Matching Rule on Employee ID". Falls back to the id of what
    /// it covers where that is not among the configuration given.
    /// </summary>
    public static string DescribeCoverage(ConfigurationTestCase testCase, SyncRule? syncRule, IReadOnlyList<ObjectMatchingRule> matchingRules)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        switch (testCase.Kind)
        {
            case ConfigurationTestCaseKind.Scope:
                return "Scope";

            case ConfigurationTestCaseKind.ObjectMatchingRule:
            {
                var rule = matchingRules.FirstOrDefault(r => r.Id == testCase.ObjectMatchingRuleId)
                           ?? syncRule?.ObjectMatchingRules.FirstOrDefault(r => r.Id == testCase.ObjectMatchingRuleId);
                return rule?.TargetMetaverseAttribute?.Name is { } target
                    ? $"Object Matching Rule on {target}"
                    : $"Object Matching Rule {testCase.ObjectMatchingRuleId}";
            }

            default:
            {
                var mapping = syncRule?.AttributeFlowRules.FirstOrDefault(m => m.Id == testCase.SyncRuleMappingId);
                var target = mapping?.TargetMetaverseAttribute?.Name ?? mapping?.TargetConnectedSystemAttribute?.Name;
                if (target == null)
                    return $"{(testCase.Kind == ConfigurationTestCaseKind.GeneratedValue ? "Generated Value" : "Attribute Flow")} {testCase.SyncRuleMappingId}";
                return testCase.Kind == ConfigurationTestCaseKind.GeneratedValue ? $"Generated Value for {target}" : $"Attribute Flow to {target}";
            }
        }
    }

    /// <summary>
    /// What <paramref name="expectation"/> expects, in a few words.
    /// </summary>
    public static string DescribeExpectation(ConfigurationTestExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        return expectation.Kind switch
        {
            ConfigurationTestExpectationKind.Values =>
                string.Join(", ", expectation.Values.Select(value => $"'{value}'")) + (expectation.InOrder ? ", in this order" : string.Empty),
            ConfigurationTestExpectationKind.NoValue => "No value",
            ConfigurationTestExpectationKind.Error => "An error",
            ConfigurationTestExpectationKind.AnyValue => "Any value",
            ConfigurationTestExpectationKind.InScope => "In scope",
            _ => "Out of scope"
        };
    }

    /// <summary>
    /// The label an expectation is chosen by.
    /// </summary>
    public static string ExpectationLabel(ConfigurationTestExpectationKind kind) => kind switch
    {
        ConfigurationTestExpectationKind.Values => "These values",
        _ => DescribeExpectation(new ConfigurationTestExpectation(kind))
    };

    /// <summary>
    /// The editor's input rows: each input the configuration reads now, in the order it reads them, holding the case's
    /// values for it; then each input the case gives that the configuration no longer reads. Names match without regard
    /// to case, as a run matches them.
    /// </summary>
    public static List<InputRow> InputRows(ConfigurationTestCase testCase, IReadOnlyList<InputSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(slots);

        var given = testCase.Inputs.ToList();
        var rows = new List<InputRow>();
        foreach (var slot in slots)
        {
            var input = given.FirstOrDefault(i => i.Source == slot.Source && string.Equals(i.AttributeName, slot.AttributeName, StringComparison.OrdinalIgnoreCase));
            if (input != null)
                given.Remove(input);
            rows.Add(new InputRow(slot.Source, slot.AttributeName, slot.Type, slot.Plurality, IsRead: true, Given: input != null, StateOf(input)));
        }

        rows.AddRange(given.Select(input => new InputRow(input.Source, input.AttributeName, null, null, IsRead: false, Given: true, StateOf(input))));
        return rows;
    }

    /// <summary>
    /// The inputs <paramref name="rows"/> give: every row, an input with no value given as one with no values.
    /// </summary>
    public static List<ConfigurationTestInput> ToInputs(IEnumerable<InputRow> rows) =>
        rows.Select(row => new ConfigurationTestInput(row.Source, row.AttributeName, row.State.NoValue ? [] : row.State.Values.ToList())).ToList();

    private static TestInputState StateOf(ConfigurationTestInput? input)
    {
        var state = new TestInputState();
        if (input != null)
            state.Load(input.Values);
        return state;
    }
}
