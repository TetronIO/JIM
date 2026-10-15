// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers.Scoping;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;

namespace JIM.Application.Servers.ConfigurationTests;

/// <summary>
/// Runs saved configuration test cases (#2055 Phase 4) through the testers the portal, REST API and PowerShell use,
/// so a saved case and a tried one give the same result for the same inputs. It reads nothing from the database:
/// the caller loads what each case covers. Nothing it compares or reports is logged, because a case's inputs and
/// results can hold personal data.
/// </summary>
public sealed class ConfigurationTestRunner
{
    private readonly IExpressionEvaluator _expressionEvaluator;
    private readonly MatchValueResolver _matchValueResolver;

    public ConfigurationTestRunner(IExpressionEvaluator expressionEvaluator)
    {
        _expressionEvaluator = expressionEvaluator ?? throw new ArgumentNullException(nameof(expressionEvaluator));
        _matchValueResolver = new MatchValueResolver(expressionEvaluator);
    }

    /// <summary>
    /// Runs an <see cref="ConfigurationTestCaseKind.AttributeFlow"/> or <see cref="ConfigurationTestCaseKind.GeneratedValue"/>
    /// case against its Attribute Flow on <paramref name="syncRule"/>.
    /// </summary>
    /// <param name="syncRule">The Synchronisation Rule holding the case's Attribute Flow, with its Attribute Flows and
    /// both object types' attributes.</param>
    /// <param name="nowUtc">The time the case runs at: what it runs as of when it has no run-as-of time of its own.</param>
    public ConfigurationTestRunResult RunAttributeFlow(ConfigurationTestCase testCase, SyncRule syncRule, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(syncRule);
        if (testCase.Kind is not (ConfigurationTestCaseKind.AttributeFlow or ConfigurationTestCaseKind.GeneratedValue))
            throw new ArgumentException($"A {testCase.Kind} test case does not test an Attribute Flow.", nameof(testCase));

        var mapping = syncRule.AttributeFlowRules.FirstOrDefault(m => m.Id == testCase.SyncRuleMappingId)
            ?? throw new ArgumentException($"Synchronisation Rule {syncRule.Id} has no Attribute Flow {testCase.SyncRuleMappingId}.", nameof(syncRule));
        var asOf = testCase.RunAsOfUtc ?? nowUtc;
        var generated = testCase.Kind == ConfigurationTestCaseKind.GeneratedValue;
        var source = generated
            ? AttributeFlowPipeline.GeneratedBaseSource(mapping)
            : mapping.Sources.OrderBy(s => s.Order).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Expression));
        var ranAsOfNow = testCase.RunAsOfUtc == null && NonRepeatableFunctionDetector.Find(source?.Expression).Count > 0;

        var problems = new List<string>();
        if (generated && mapping.Generation == null)
            problems.Add("This Generated Value test case covers an Attribute Flow that no longer generates a value; test it as an Attribute Flow instead.");
        else if (!generated && mapping.Generation != null)
            problems.Add("This Attribute Flow now generates a Generated Value, so its base is what to test; test it as a Generated Value instead.");
        else if (source == null)
            problems.Add(generated
                ? "The Generated Value has no base expression to test: its token alone is the value."
                : "The Attribute Flow has no Expression to test: it flows an attribute as it is.");
        else
            problems.AddRange(MissingInputs(testCase.Inputs, AttributeFlowTester.DescribeInputs(syncRule, source.Expression)
                .Where(input => input.InSchema)
                .Select(input => ((ExpressionInputSource?)input.Source, input.AttributeName, input.Accessor))));

        if (problems.Count > 0)
            return NeedsReview(testCase, problems, asOf, ranAsOfNow);

        var inputs = testCase.Inputs
            .Select(input => new AttributeFlowTestInput(input.Source ?? ExpressionInputSource.ConnectedSystem, input.AttributeName,
                input.Values.Cast<object>().ToList()))
            .ToList();
        var tested = AttributeFlowTester.Test(syncRule, mapping, source, inputs, _expressionEvaluator, asOf);
        if (tested.InputErrors.Count > 0)
            return NeedsReview(testCase, tested.InputErrors, asOf, ranAsOfNow);

        var target = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "the target";
        ConfigurationTestActual actual;
        AttributeDataType valueType;
        if (generated)
        {
            var generatedBase = tested.GeneratedBase!;
            valueType = AttributeDataType.Text;
            actual = generatedBase.Outcome switch
            {
                GeneratedBaseOutcome.Base => new ConfigurationTestActual(ConfigurationTestOutcome.Values, [generatedBase.BaseValue ?? string.Empty]),
                GeneratedBaseOutcome.Waits => new ConfigurationTestActual(ConfigurationTestOutcome.NoValue, []),
                _ => new ConfigurationTestActual(ConfigurationTestOutcome.Error, [],
                    AttributeFlowTester.DescribeGeneratedBase(generatedBase, mapping.Generation!, target))
            };
        }
        else
        {
            var evaluation = tested.Evaluation!;
            valueType = evaluation.TargetType;
            actual = evaluation.Outcome switch
            {
                AttributeFlowOutcome.Values => new ConfigurationTestActual(ConfigurationTestOutcome.Values,
                    evaluation.Values.Select(ConfigurationTestValues.Canonical).ToList()),
                AttributeFlowOutcome.NoValue => new ConfigurationTestActual(ConfigurationTestOutcome.NoValue, []),
                _ => new ConfigurationTestActual(ConfigurationTestOutcome.Error, [], AttributeFlowTester.Describe(evaluation, target))
            };
        }

        return Compare(testCase, actual, valueType, asOf, ranAsOfNow);
    }

    /// <summary>
    /// Runs a <see cref="ConfigurationTestCaseKind.Scope"/> case against <paramref name="syncRule"/>'s scoping criteria.
    /// </summary>
    /// <param name="syncRule">The Synchronisation Rule, with its scoping criteria and their attributes.</param>
    /// <param name="nowUtc">The time the case runs at: what it runs as of when it has no run-as-of time of its own.</param>
    public ConfigurationTestRunResult RunScope(ConfigurationTestCase testCase, SyncRule syncRule, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(syncRule);
        if (testCase.Kind != ConfigurationTestCaseKind.Scope)
            throw new ArgumentException($"A {testCase.Kind} test case does not test a Scope.", nameof(testCase));

        var asOf = testCase.RunAsOfUtc ?? nowUtc;
        var ranAsOfNow = testCase.RunAsOfUtc == null && ScopeReadsTheClock(syncRule.ObjectScopingCriteriaGroups);

        var problems = MissingInputs(testCase.Inputs, ScopeTester.DescribeInputs(syncRule)
            .Select(input => ((ExpressionInputSource?)null, input.AttributeName, $"'{input.AttributeName}'"))).ToList();
        if (problems.Count > 0)
            return NeedsReview(testCase, problems, asOf, ranAsOfNow);

        var tested = ScopeTester.Test(syncRule, Samples(testCase), asOf);
        if (tested.InputErrors.Count > 0)
            return NeedsReview(testCase, tested.InputErrors, asOf, ranAsOfNow);

        var explanation = tested.Explanation!;
        var actual = explanation.Outcome switch
        {
            ScopingRuleOutcome.InScope => new ConfigurationTestActual(ConfigurationTestOutcome.InScope, []),
            ScopingRuleOutcome.OutOfScope => new ConfigurationTestActual(ConfigurationTestOutcome.OutOfScope, []),
            _ => new ConfigurationTestActual(ConfigurationTestOutcome.Undetermined, [], explanation.Hint)
        };

        return Compare(testCase, actual, valueType: null, asOf, ranAsOfNow);
    }

    /// <summary>
    /// Runs an <see cref="ConfigurationTestCaseKind.ObjectMatchingRule"/> case: the rule's match values for the inputs,
    /// with no Metaverse lookup, so the result does not depend on what the Metaverse holds.
    /// </summary>
    /// <param name="rule">The Object Matching Rule, with its source and Target Metaverse Attribute.</param>
    /// <param name="objectType">The Object Type the rule reads, with its attributes.</param>
    /// <param name="nowUtc">The time the case runs at: what it runs as of when it has no run-as-of time of its own.</param>
    public ConfigurationTestRunResult RunObjectMatching(ConfigurationTestCase testCase, ObjectMatchingRule rule,
        ConnectedSystemObjectType objectType, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(objectType);
        if (testCase.Kind != ConfigurationTestCaseKind.ObjectMatchingRule)
            throw new ArgumentException($"A {testCase.Kind} test case does not test an Object Matching Rule.", nameof(testCase));

        // Saving refuses a matching expression that reads the clock, so a case only runs as of now in name.
        var asOf = testCase.RunAsOfUtc ?? nowUtc;
        var ranAsOfNow = testCase.RunAsOfUtc == null && rule.Sources.Any(s => NonRepeatableFunctionDetector.Find(s.Expression).Count > 0);

        var problems = new List<string>();
        if (rule.DescribeInvalidity() is { } invalid)
            problems.Add($"The Object Matching Rule cannot be tested as it is: {invalid}");
        else
            problems.AddRange(MissingInputs(testCase.Inputs, ReadAttributes(rule, objectType)
                .Select(name => ((ExpressionInputSource?)null, name, $"cs[\"{name}\"]"))));
        if (problems.Count > 0)
            return NeedsReview(testCase, problems, asOf, ranAsOfNow);

        var tested = ObjectMatchingServer.TestMatchValues(_matchValueResolver, rule, objectType, Samples(testCase), out _);
        if (tested.InputErrors.Count > 0)
            return NeedsReview(testCase, tested.InputErrors, asOf, ranAsOfNow);

        var resolution = tested.Resolution!;
        var actual = resolution.Kind switch
        {
            MatchValueResolutionKind.Values => new ConfigurationTestActual(ConfigurationTestOutcome.Values,
                resolution.Values.Select(ConfigurationTestValues.Canonical).ToList()),
            MatchValueResolutionKind.NoValue => new ConfigurationTestActual(ConfigurationTestOutcome.NoValue, []),
            _ => new ConfigurationTestActual(ConfigurationTestOutcome.Error, [], resolution.FailureMessage)
        };

        return Compare(testCase, actual, resolution.ValueType, asOf, ranAsOfNow);
    }

    /// <summary>
    /// The Error result for a case that could not be run at all. Its message is the exception's type, never its text,
    /// which can quote a case's input values.
    /// </summary>
    public static ConfigurationTestRunResult CouldNotRun(ConfigurationTestCase testCase, string reason, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        return new ConfigurationTestRunResult
        {
            TestCaseId = testCase.Id,
            TestCaseName = testCase.Name,
            Kind = testCase.Kind,
            Status = ConfigurationTestStatus.Error,
            Summary = $"The test case could not be run: {reason}",
            Expected = testCase.Expectation,
            RanAsOfUtc = testCase.RunAsOfUtc ?? nowUtc
        };
    }

    private static ConfigurationTestRunResult Compare(ConfigurationTestCase testCase, ConfigurationTestActual actual,
        AttributeDataType? valueType, DateTime asOf, bool ranAsOfNow)
    {
        var expected = testCase.Expectation;
        var failure = expected.Kind switch
        {
            ConfigurationTestExpectationKind.Values => CompareValues(expected, actual, valueType),
            ConfigurationTestExpectationKind.AnyValue => actual.Outcome == ConfigurationTestOutcome.Values
                ? null
                : $"Expected a value, but {Describe(actual)}.",
            _ => Matches(expected.Kind, actual.Outcome) ? null : $"Expected {Describe(expected)}, but {Describe(actual)}."
        };

        return new ConfigurationTestRunResult
        {
            TestCaseId = testCase.Id,
            TestCaseName = testCase.Name,
            Kind = testCase.Kind,
            Status = failure == null ? ConfigurationTestStatus.Passed : ConfigurationTestStatus.Failed,
            Summary = failure ?? $"Passed: {Describe(actual)}, as expected.",
            Expected = expected,
            Actual = actual,
            RanAsOfUtc = asOf,
            RanAsOfNow = ranAsOfNow
        };
    }

    private static string? CompareValues(ConfigurationTestExpectation expected, ConfigurationTestActual actual, AttributeDataType? valueType)
    {
        if (actual.Outcome != ConfigurationTestOutcome.Values)
            return $"Expected {Describe(expected)}, but {Describe(actual)}.";

        var expectedValues = new List<string>();
        foreach (var value in expected.Values)
        {
            if (ConfigurationTestValues.CanonicalExpected(value, valueType) is not { } canonical)
                return $"Expected '{value}', which is not a {valueType} value, so nothing the configuration gives can match it.";
            expectedValues.Add(canonical);
        }

        if (expected.InOrder)
            return expectedValues.SequenceEqual(actual.Values, StringComparer.Ordinal)
                ? null
                : actual.Values.Order(StringComparer.Ordinal).SequenceEqual(expectedValues.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                    ? $"Expected {Describe(expected)} in that order, but they came in this order: {Quote(actual.Values)}."
                    : $"Expected {Describe(expected)}, but {Describe(actual)}.";

        return expectedValues.Order(StringComparer.Ordinal).SequenceEqual(actual.Values.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            ? null
            : $"Expected {Describe(expected)}, but {Describe(actual)}.";
    }

    private static bool Matches(ConfigurationTestExpectationKind expected, ConfigurationTestOutcome actual) => (expected, actual) switch
    {
        (ConfigurationTestExpectationKind.NoValue, ConfigurationTestOutcome.NoValue) => true,
        (ConfigurationTestExpectationKind.Error, ConfigurationTestOutcome.Error) => true,
        (ConfigurationTestExpectationKind.InScope, ConfigurationTestOutcome.InScope) => true,
        (ConfigurationTestExpectationKind.OutOfScope, ConfigurationTestOutcome.OutOfScope) => true,
        _ => false
    };

    private static string Describe(ConfigurationTestExpectation expected) => expected.Kind switch
    {
        ConfigurationTestExpectationKind.Values => expected.Values.Count == 1 ? Quote(expected.Values) : $"the values {Quote(expected.Values)}",
        ConfigurationTestExpectationKind.NoValue => "no value",
        ConfigurationTestExpectationKind.Error => "an error",
        ConfigurationTestExpectationKind.AnyValue => "a value",
        ConfigurationTestExpectationKind.InScope => "the object in scope",
        _ => "the object out of scope"
    };

    private static string Describe(ConfigurationTestActual actual) => actual.Outcome switch
    {
        ConfigurationTestOutcome.Values => actual.Values.Count == 1 ? $"it gave {Quote(actual.Values)}" : $"it gave {Quote(actual.Values)}",
        ConfigurationTestOutcome.NoValue => "it gave no value",
        ConfigurationTestOutcome.Error => $"it gave an error: {actual.ErrorMessage}",
        ConfigurationTestOutcome.InScope => "the object is in scope",
        ConfigurationTestOutcome.OutOfScope => "the object is out of scope",
        _ => $"synchronisation could not tell whether the object is in scope: {actual.ErrorMessage}"
    };

    private static string Quote(IReadOnlyList<string> values) => string.Join(", ", values.Select(value => $"'{value}'"));

    private static ConfigurationTestRunResult NeedsReview(ConfigurationTestCase testCase, IReadOnlyList<string> problems, DateTime asOf, bool ranAsOfNow) => new()
    {
        TestCaseId = testCase.Id,
        TestCaseName = testCase.Name,
        Kind = testCase.Kind,
        Status = ConfigurationTestStatus.NeedsReview,
        Summary = $"Needs review: the test case no longer fits what it tests. {problems[0]}",
        Problems = problems,
        Expected = testCase.Expectation,
        RanAsOfUtc = asOf,
        RanAsOfNow = ranAsOfNow
    };

    /// <summary>
    /// A problem for each input the configuration reads that the case gives no input for. An input given with no
    /// values counts as given: it says the attribute has no value.
    /// </summary>
    private static IEnumerable<string> MissingInputs(IReadOnlyList<ConfigurationTestInput> given,
        IEnumerable<(ExpressionInputSource? Source, string AttributeName, string Label)> read) =>
        read
            .Where(input => !given.Any(g => g.Source == input.Source && string.Equals(g.AttributeName, input.AttributeName, StringComparison.OrdinalIgnoreCase)))
            .Select(input => input.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(label => $"The configuration reads {label}, which this test case gives no input for. Add it, with no values if it should have none.");

    private static List<AttributeSample> Samples(ConfigurationTestCase testCase) =>
        testCase.Inputs.Select(input => new AttributeSample(input.AttributeName, input.Values.Cast<object>().ToList())).ToList();

    private static IEnumerable<string> ReadAttributes(ObjectMatchingRule rule, ConnectedSystemObjectType objectType)
    {
        var source = rule.Sources.Count == 1 ? rule.Sources[0] : null;
        if (source == null)
            return [];

        if (string.IsNullOrWhiteSpace(source.Expression))
        {
            var attribute = source.ConnectedSystemAttribute ?? objectType.Attributes.FirstOrDefault(a => a.Id == source.ConnectedSystemAttributeId);
            return attribute == null ? [] : [attribute.Name];
        }

        return ExpressionInputResolver.Resolve(source.Expression)
            .Where(input => input.Source == ExpressionInputSource.ConnectedSystem &&
                            objectType.Attributes.Any(a => string.Equals(a.Name, input.AttributeName, StringComparison.OrdinalIgnoreCase)))
            .Select(input => input.AttributeName);
    }

    private static bool ScopeReadsTheClock(IEnumerable<SyncRuleScopingCriteriaGroup> groups) =>
        groups.Any(group => group.Criteria.Any(criterion => criterion.ValueMode == DateCriteriaValueMode.Relative ||
                                                             NonRepeatableFunctionDetector.Find(criterion.Expression).Count > 0) ||
                            ScopeReadsTheClock(group.ChildGroups));
}
