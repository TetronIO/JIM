// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Saved configuration test cases in Configuration History (#2055 Phase 4): recorded in the snapshot of what owns
/// them (a Synchronisation Rule, or a Connected System for a Simple mode Object Matching Rule) as Class C, with their
/// inputs and expected values as keyed hashes so history says a case changed without holding its values, and listed
/// when a cascade removes them with what they cover.
/// </summary>
[TestFixture]
public class ConfigurationTestCaseHistoryTests
{
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private JimApplication _jim = null!;
    private ConfigurationSnapshotService _snapshots = null!;
    private ConfigurationDiffService _diffs = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _jim = new JimApplication(new Mock<IRepository>().Object);
        _snapshots = _jim.ConfigurationSnapshots;
        _diffs = _jim.ConfigurationDiffs;
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public void SyncRuleSnapshot_WithTestCases_RecordsEachInATestCasesCollection()
    {
        var rule = Rule();

        var snapshot = _snapshots.CreateSnapshot(rule, HashKey, [FlowCase(1, "Surname flows"), ScopeCase(2, "Sales in scope")]);

        var testCases = snapshot.Root.Children!.Single(c => c.Key == "testCases");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(testCases.NodeType, Is.EqualTo(ConfigurationSnapshotNodeType.Collection));
            Assert.That(testCases.Children!.Select(c => c.ItemId), Is.EqualTo(new int?[] { 1, 2 }));
            Assert.That(testCases.Children!.Select(c => c.Key), Is.All.EqualTo("testCase"));
        }
    }

    [Test]
    public void SyncRuleSnapshot_WithNoTestCases_RecordsNoTestCasesNode()
    {
        // So snapshots captured before test cases existed compare as unchanged.
        var snapshot = _snapshots.CreateSnapshot(Rule(), HashKey, []);

        Assert.That(snapshot.Root.Children!.Select(c => c.Key), Has.None.EqualTo("testCases"));
    }

    [Test]
    public void SyncRuleSnapshot_TestCase_NeverHoldsItsInputOrExpectedValues()
    {
        var testCase = FlowCase(1, "Surname flows");
        testCase.Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", ["Bloggs-input-value"])];
        testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["Bloggs-expected-value"]);

        var json = ConfigurationSnapshotService.Serialise(_snapshots.CreateSnapshot(Rule(), HashKey, [testCase]));

        Assert.That(json, Does.Not.Contain("Bloggs-input-value").And.Not.Contain("Bloggs-expected-value"));
    }

    [Test]
    public void Diff_AddingATestCase_IsCosmetic()
    {
        var rule = Rule();
        var before = _snapshots.CreateSnapshot(rule, HashKey, []);
        var after = _snapshots.CreateSnapshot(rule, HashKey, [FlowCase(1, "Surname flows")]);

        Assert.That(ConfigurationChangeClassifier.Classify(_diffs.Diff(before, after)), Is.EqualTo(ConfigurationChangeClass.Cosmetic));
    }

    [Test]
    public void Diff_ChangingOnlyATestCasesInputs_IsDetectedAndCosmetic()
    {
        var rule = Rule();
        var testCase = FlowCase(1, "Surname flows");
        var before = _snapshots.CreateSnapshot(rule, HashKey, [testCase]);
        testCase.Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", ["Smith"])];
        var after = _snapshots.CreateSnapshot(rule, HashKey, [testCase]);

        var diff = _diffs.Diff(before, after);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff.HasChanges, Is.True);
            Assert.That(ConfigurationChangeClassifier.Classify(diff), Is.EqualTo(ConfigurationChangeClass.Cosmetic));
        }
    }

    [Test]
    public void Diff_RemovingAnAttributeFlowAndTheCasesItCascadedTo_ListsTheRemovedCases()
    {
        var withFlow = Rule();
        var before = _snapshots.CreateSnapshot(withFlow, HashKey, [FlowCase(1, "Surname flows"), ScopeCase(2, "Sales in scope")]);
        var withoutFlow = Rule();
        withoutFlow.AttributeFlowRules.Clear();
        var after = _snapshots.CreateSnapshot(withoutFlow, HashKey, [ScopeCase(2, "Sales in scope")]);

        var diff = _diffs.Diff(before, after);

        var removed = diff.Root!.Children!.Single(c => c.Key == "testCases").Children!
            .Where(c => c.ChangeType == ConfigurationDiffChangeType.Removed)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed.Select(c => c.ItemId), Is.EqualTo(new int?[] { 1 }));
            Assert.That(removed.Single().Label, Does.Contain("Surname flows"));
        }
    }

    [Test]
    public void Diff_RemovingTheLastTestCase_ListsIt()
    {
        var rule = Rule();
        var before = _snapshots.CreateSnapshot(rule, HashKey, [FlowCase(1, "Surname flows")]);
        var after = _snapshots.CreateSnapshot(rule, HashKey, []);

        var diff = _diffs.Diff(before, after);

        var testCases = diff.Root!.Children!.Single(c => c.Key == "testCases");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(testCases.ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Removed));
            Assert.That(testCases.Children!.Single().ItemId, Is.EqualTo(1));
        }
    }

    [Test]
    public void ConnectedSystemSnapshot_WithSimpleModeMatchingCases_RecordsThem()
    {
        var connectedSystem = new ConnectedSystem { Id = 5, Name = "HR" };
        var testCase = new ConfigurationTestCase
        {
            Id = 3, Name = "Matches on employee number", Kind = ConfigurationTestCaseKind.ObjectMatchingRule, ObjectMatchingRuleId = 9,
            Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.AnyValue)
        };

        var before = _snapshots.CreateSnapshot(connectedSystem, HashKey, []);
        var after = _snapshots.CreateSnapshot(connectedSystem, HashKey, [testCase]);
        var diff = _diffs.Diff(before, after);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Root.Children!.Single(c => c.Key == "testCases").Children!.Single().ItemId, Is.EqualTo(3));
            Assert.That(ConfigurationChangeClassifier.Classify(diff), Is.EqualTo(ConfigurationChangeClass.Cosmetic));
        }
    }

    private static SyncRule Rule()
    {
        var rule = new SyncRule { Id = 7, Name = "HR Import", Direction = SyncRuleDirection.Import };
        var mapping = new SyncRuleMapping
        {
            Id = 3, TargetMetaverseAttributeId = 21, TargetMetaverseAttribute = new MetaverseAttribute { Id = 21, Name = "Last Name" }
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Id = 30, Expression = "cs[\"sn\"]" });
        rule.AttributeFlowRules.Add(mapping);
        return rule;
    }

    private static ConfigurationTestCase FlowCase(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Kind = ConfigurationTestCaseKind.AttributeFlow,
        SyncRuleMappingId = 3,
        Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", ["Bloggs"])],
        Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["Bloggs"])
    };

    private static ConfigurationTestCase ScopeCase(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Kind = ConfigurationTestCaseKind.Scope,
        SyncRuleId = 7,
        Inputs = [new ConfigurationTestInput(null, "department", ["Sales"])],
        Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.InScope)
    };
}
