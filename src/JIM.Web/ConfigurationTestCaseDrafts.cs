// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.ConfigurationTests;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Web.Models;

namespace JIM.Web;

/// <summary>
/// "Save as test case" in the portal's Attribute Flow and Scope testers (#2055 Phase 4): the configuration test case a
/// test becomes, with the values tested as its inputs and the result as its expectation. The Object Matching Rule
/// editor's Try it tests a rule before it is added, so it has nothing saved for a case to cover. Null when there is nothing to save: the test had
/// input errors, gave no result to expect, or covers something not yet saved. A case's name never quotes a value,
/// because it is written to the Activity recording the case.
/// </summary>
public static class ConfigurationTestCaseDrafts
{
    /// <summary>
    /// A test of an Attribute Flow, or of a Generated Value's base.
    /// </summary>
    /// <param name="mapping">The Attribute Flow tested, as saved.</param>
    /// <param name="source">The source tested, whose Expression decides whether the case needs a run-as-of time.</param>
    /// <param name="inputs">The inputs the tester showed, with what each held.</param>
    /// <param name="result">What the test gave.</param>
    /// <param name="asOfUtc">The instant the test was run as of, or null for the time it ran.</param>
    /// <param name="ranAtUtc">The time it ran: the case runs as of it when the Expression reads the clock and no
    /// instant was chosen, so the result it expects stays the result it gets.</param>
    public static ConfigurationTestCase? FromAttributeFlowTest(SyncRuleMapping mapping, SyncRuleMappingSource? source,
        IReadOnlyList<(AttributeFlowTestInputDescriptor Input, TestInputState State)> inputs, AttributeFlowTestResult result,
        DateTime? asOfUtc, DateTime ranAtUtc)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(result);
        if (mapping.Id <= 0 || result.InputErrors.Count > 0)
            return null;

        ConfigurationTestExpectation expectation;
        if (mapping.Generation != null)
        {
            if (result.GeneratedBase is not { } generated || generated.Outcome == GeneratedBaseOutcome.NoBaseExpression)
                return null;
            expectation = generated.Outcome switch
            {
                GeneratedBaseOutcome.Base => Values([generated.BaseValue ?? string.Empty]),
                GeneratedBaseOutcome.Waits => new ConfigurationTestExpectation(ConfigurationTestExpectationKind.NoValue),
                _ => new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Error)
            };
        }
        else
        {
            if (result.Evaluation is not { } evaluation)
                return null;
            expectation = evaluation.Outcome switch
            {
                AttributeFlowOutcome.Values => Values(evaluation.Values.Select(ConfigurationTestValues.Canonical)),
                AttributeFlowOutcome.NoValue => new ConfigurationTestExpectation(ConfigurationTestExpectationKind.NoValue),
                _ => new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Error)
            };
        }

        var target = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "its target";
        return new ConfigurationTestCase
        {
            Name = mapping.Generation != null ? $"Generated Value base for {target}" : $"Attribute Flow to {target}",
            Kind = mapping.Generation != null ? ConfigurationTestCaseKind.GeneratedValue : ConfigurationTestCaseKind.AttributeFlow,
            SyncRuleMappingId = mapping.Id,
            Inputs = inputs
                .Where(input => input.Input.InSchema)
                .Select(input => new ConfigurationTestInput(input.Input.Source, input.Input.AttributeName, ValuesOf(input.State)))
                .ToList(),
            RunAsOfUtc = asOfUtc ?? (ConfigurationTestRunner.ReadsTheClock(source) ? ranAtUtc : null),
            Expectation = expectation
        };
    }

    /// <summary>
    /// A test of a Synchronisation Rule's Scope. Nothing to save when synchronisation could not tell whether the
    /// object is in scope: the criteria need fixing first.
    /// </summary>
    public static ConfigurationTestCase? FromScopeTest(SyncRule syncRule, IReadOnlyList<(string AttributeName, TestInputState State)> inputs,
        ScopeTestResult result, DateTime? asOfUtc, DateTime ranAtUtc)
    {
        ArgumentNullException.ThrowIfNull(syncRule);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(result);
        if (syncRule.Id <= 0 || result.InputErrors.Count > 0 || result.Explanation is not { } explanation ||
            explanation.Outcome == ScopingRuleOutcome.Undetermined)
            return null;

        var inScope = explanation.Outcome == ScopingRuleOutcome.InScope;
        return new ConfigurationTestCase
        {
            Name = inScope ? "In scope" : "Out of scope",
            Kind = ConfigurationTestCaseKind.Scope,
            SyncRuleId = syncRule.Id,
            Inputs = inputs.Select(input => new ConfigurationTestInput(null, input.AttributeName, ValuesOf(input.State))).ToList(),
            RunAsOfUtc = asOfUtc ?? (ConfigurationTestRunner.ScopeReadsTheClock(syncRule) ? ranAtUtc : null),
            Expectation = new ConfigurationTestExpectation(inScope ? ConfigurationTestExpectationKind.InScope : ConfigurationTestExpectationKind.OutOfScope)
        };
    }

    private static List<string> ValuesOf(TestInputState state) => state.NoValue ? [] : state.Values.ToList();

    private static ConfigurationTestExpectation Values(IEnumerable<string> values) =>
        new(ConfigurationTestExpectationKind.Values, values.ToList());
}
