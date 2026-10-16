// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Servers.ConfigurationTests;

/// <summary>
/// The configuration a set of test cases runs against, held in memory: the configuration as saved, or what a save
/// proposes. A case whose covered configuration it does not hold is not run.
/// </summary>
public sealed class ConfigurationTestSubject
{
    private readonly IReadOnlyList<SyncRule> _syncRules;
    private readonly IReadOnlyList<(ObjectMatchingRule Rule, ConnectedSystemObjectType ObjectType)> _simpleModeRules;

    private ConfigurationTestSubject(IReadOnlyList<SyncRule> syncRules,
        IReadOnlyList<(ObjectMatchingRule Rule, ConnectedSystemObjectType ObjectType)> simpleModeRules)
    {
        _syncRules = syncRules;
        _simpleModeRules = simpleModeRules;
    }

    /// <summary>A Synchronisation Rule, with its Attribute Flows, Scope and own Object Matching Rules.</summary>
    /// <param name="syncRule">The rule with both object types' attributes, as the testers read them.</param>
    public static ConfigurationTestSubject Of(SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(syncRule);
        return new ConfigurationTestSubject([syncRule], []);
    }

    /// <summary>A Connected System's Simple mode Object Matching Rule, and the Object Type it reads.</summary>
    public static ConfigurationTestSubject Of(ObjectMatchingRule simpleModeRule, ConnectedSystemObjectType objectType)
    {
        ArgumentNullException.ThrowIfNull(simpleModeRule);
        ArgumentNullException.ThrowIfNull(objectType);
        return new ConfigurationTestSubject([], [(simpleModeRule, objectType)]);
    }

    /// <summary>
    /// Runs <paramref name="testCase"/> against this configuration, or returns null when it does not hold what the case
    /// covers (the save removes it, or the case covers something else).
    /// </summary>
    internal ConfigurationTestRunResult? Run(ConfigurationTestRunner runner, ConfigurationTestCase testCase, DateTime nowUtc)
    {
        switch (testCase.Kind)
        {
            case ConfigurationTestCaseKind.AttributeFlow or ConfigurationTestCaseKind.GeneratedValue:
            {
                var rule = _syncRules.FirstOrDefault(r => r.AttributeFlowRules.Any(m => m.Id == testCase.SyncRuleMappingId));
                return rule == null ? null : runner.RunAttributeFlow(testCase, rule, nowUtc);
            }

            case ConfigurationTestCaseKind.Scope:
            {
                var rule = _syncRules.FirstOrDefault(r => r.Id == testCase.SyncRuleId);
                return rule == null ? null : runner.RunScope(testCase, rule, nowUtc);
            }

            default:
            {
                foreach (var syncRule in _syncRules)
                {
                    var matchingRule = syncRule.ObjectMatchingRules.FirstOrDefault(r => r.Id == testCase.ObjectMatchingRuleId);
                    if (matchingRule == null)
                        continue;
                    // In Synchronisation Rule mode the rule reads its Synchronisation Rule's Object Type.
                    return syncRule.ConnectedSystemObjectType == null
                        ? ConfigurationTestRunner.CouldNotRun(testCase, "the Object Type its Object Matching Rule reads no longer exists.", nowUtc)
                        : runner.RunObjectMatching(testCase, matchingRule, syncRule.ConnectedSystemObjectType, nowUtc);
                }

                var simpleMode = _simpleModeRules.FirstOrDefault(r => r.Rule.Id == testCase.ObjectMatchingRuleId);
                return simpleMode.Rule == null ? null : runner.RunObjectMatching(testCase, simpleMode.Rule, simpleMode.ObjectType, nowUtc);
            }
        }
    }
}

/// <summary>
/// The save gate (#2055 Phase 4b): which configuration test cases a save would stop passing. A case gates a save only
/// when it passes against the configuration as saved and does not against what the save proposes, so a case that was
/// already failing never stops anything, and no save can be held back by a case it did not break. Both runs are as of
/// one instant, so a case reading the clock cannot regress between them. Nothing it compares is logged.
/// </summary>
public sealed class ConfigurationTestGate
{
    private readonly ConfigurationTestRunner _runner;

    public ConfigurationTestGate(ConfigurationTestRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    /// <summary>
    /// The cases among <paramref name="testCases"/> that pass against <paramref name="stored"/> and do not against
    /// <paramref name="proposed"/>, each run as of its own run-as-of time or <paramref name="nowUtc"/>.
    /// </summary>
    public IReadOnlyList<ConfigurationTestRegression> FindRegressions(IReadOnlyList<ConfigurationTestCase> testCases,
        ConfigurationTestSubject stored, ConfigurationTestSubject proposed, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(testCases);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(proposed);

        var regressions = new List<ConfigurationTestRegression>();
        foreach (var testCase in testCases)
        {
            var before = TryRun(stored, testCase, nowUtc);
            if (before is not { Status: ConfigurationTestStatus.Passed })
                continue;

            // Null: the save removes what the case covers, and the case goes with it.
            var after = TryRun(proposed, testCase, nowUtc);
            if (after == null || after.Status == ConfigurationTestStatus.Passed)
                continue;

            regressions.Add(new ConfigurationTestRegression
            {
                TestCase = testCase,
                Before = before,
                After = after,
                AcceptedExpectation = AcceptedExpectation(testCase, after)
            });
        }

        return regressions;
    }

    private ConfigurationTestRunResult? TryRun(ConfigurationTestSubject subject, ConfigurationTestCase testCase, DateTime nowUtc)
    {
        try
        {
            return subject.Run(_runner, testCase, nowUtc);
        }
        catch (Exception ex)
        {
            // The exception's type only: its message can quote the case's input values.
            return ConfigurationTestRunner.CouldNotRun(testCase, $"{ex.GetType().Name}.", nowUtc);
        }
    }

    /// <summary>
    /// What the case would expect if the new result were accepted: the result itself, keeping the case's order
    /// requirement for values. Null when the case was not run, its Scope cannot be determined, or the result is more
    /// than a case can expect.
    /// </summary>
    private static ConfigurationTestExpectation? AcceptedExpectation(ConfigurationTestCase testCase, ConfigurationTestRunResult after)
    {
        if (after.Status != ConfigurationTestStatus.Failed || after.Actual is not { } actual)
            return null;

        return actual.Outcome switch
        {
            ConfigurationTestOutcome.Values when actual.Values.Count is > 0 and <= ConfigurationTestCase.MaxValues &&
                                                 actual.Values.All(v => v.Length <= ConfigurationTestCase.MaxValueLength) =>
                new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, actual.Values,
                    InOrder: testCase.Expectation is { Kind: ConfigurationTestExpectationKind.Values, InOrder: true } && actual.Values.Count > 1),
            ConfigurationTestOutcome.NoValue => new ConfigurationTestExpectation(ConfigurationTestExpectationKind.NoValue),
            ConfigurationTestOutcome.Error => new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Error),
            ConfigurationTestOutcome.InScope => new ConfigurationTestExpectation(ConfigurationTestExpectationKind.InScope),
            ConfigurationTestOutcome.OutOfScope => new ConfigurationTestExpectation(ConfigurationTestExpectationKind.OutOfScope),
            _ => null
        };
    }
}
