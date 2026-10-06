// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// The explained scoping evaluation (#348) against the boolean evaluation synchronisation uses. The explanation must
/// never disagree with synchronisation, so most of this fixture compares the two across every operator, data type,
/// value state, group shape and nesting depth, on both the export (Metaverse Object) and import (Connected System
/// Object) sides; the rest pins what the explanation records and how it renders values.
/// </summary>
[TestFixture]
public class ScopingExplanationTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 10, 41, 0, DateTimeKind.Utc);
    private static readonly Guid GuidA = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    private static readonly Guid GuidB = Guid.Parse("0c2a7a3e-1d2b-4c5e-9f60-718293a4b5c6");

    private ScopingEvaluationServer _scoping = null!;

    [SetUp]
    public void SetUp()
    {
        _scoping = new ScopingEvaluationServer();
    }

    #region Building rules and objects

    public enum Side { Metaverse, ConnectedSystem }

    /// <summary>One value an object holds; <see cref="AssertedNull"/> is the Metaverse-only asserted-null marker (#91).</summary>
    private sealed record HeldValue(int AttributeId, AttributeDataType Type, object? Value, bool AssertedNull = false);

    private sealed record ScopedAttribute(int Id, string Name, AttributeDataType Type);

    private static readonly ScopedAttribute Department = new(1, "Department", AttributeDataType.Text);
    private static readonly ScopedAttribute Grade = new(2, "Grade", AttributeDataType.Number);
    private static readonly ScopedAttribute CostCentre = new(3, "Cost Centre", AttributeDataType.Text);
    private static readonly ScopedAttribute JobTitle = new(4, "Job Title", AttributeDataType.Text);
    private static readonly ScopedAttribute Active = new(5, "Active", AttributeDataType.Boolean);
    private static readonly ScopedAttribute StartDate = new(6, "Start Date", AttributeDataType.DateTime);

    private static SyncRule Rule(Side side, params SyncRuleScopingCriteriaGroup[] groups)
    {
        var rule = new SyncRule
        {
            Id = 7,
            Name = side == Side.Metaverse ? "Finance App Users" : "HR Import",
            Direction = side == Side.Metaverse ? SyncRuleDirection.Export : SyncRuleDirection.Import
        };
        rule.ObjectScopingCriteriaGroups.AddRange(groups);
        return rule;
    }

    /// <summary>
    /// A group whose children are given in evaluation order: criteria first, then child groups, as the evaluator takes
    /// them. Ids are assigned from a counter so each node is distinguishable.
    /// </summary>
    private static SyncRuleScopingCriteriaGroup Group(SearchGroupType type, params object[] children)
    {
        var group = new SyncRuleScopingCriteriaGroup { Id = _nextId++, Type = type };
        foreach (var child in children)
        {
            switch (child)
            {
                case SyncRuleScopingCriteria criterion:
                    group.Criteria.Add(criterion);
                    break;
                case SyncRuleScopingCriteriaGroup childGroup:
                    group.ChildGroups.Add(childGroup);
                    break;
                default:
                    throw new ArgumentException($"Unexpected child {child}");
            }
        }
        return group;
    }

    private static int _nextId = 100;

    private static SyncRuleScopingCriteria Criterion(Side side, ScopedAttribute? attribute, SearchComparisonType op, object? expected, bool caseSensitive = true)
    {
        var criterion = new SyncRuleScopingCriteria { Id = _nextId++, ComparisonType = op, CaseSensitive = caseSensitive };
        if (attribute != null)
        {
            if (side == Side.Metaverse)
                criterion.MetaverseAttribute = new MetaverseAttribute { Id = attribute.Id, Name = attribute.Name, Type = attribute.Type };
            else
                criterion.ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = attribute.Id, Name = attribute.Name, Type = attribute.Type };
        }

        SetExpected(criterion, attribute?.Type ?? AttributeDataType.Text, expected);
        return criterion;
    }

    private static SyncRuleScopingCriteria RelativeCriterion(Side side, ScopedAttribute attribute, SearchComparisonType op, int count, RelativeDateUnit unit, RelativeDateDirection direction)
    {
        var criterion = Criterion(side, attribute, op, null);
        criterion.ValueMode = DateCriteriaValueMode.Relative;
        criterion.RelativeCount = count;
        criterion.RelativeUnit = unit;
        criterion.RelativeDirection = direction;
        return criterion;
    }

    private static void SetExpected(SyncRuleScopingCriteria criterion, AttributeDataType type, object? expected)
    {
        switch (type)
        {
            case AttributeDataType.Text: criterion.StringValue = (string?)expected; break;
            case AttributeDataType.Number: criterion.IntValue = (int?)expected; break;
            case AttributeDataType.LongNumber: criterion.LongValue = (long?)expected; break;
            case AttributeDataType.Decimal: criterion.DecimalValue = (decimal?)expected; break;
            case AttributeDataType.DateTime: criterion.DateTimeValue = (DateTime?)expected; break;
            case AttributeDataType.Boolean: criterion.BoolValue = (bool?)expected; break;
            case AttributeDataType.Guid: criterion.GuidValue = (Guid?)expected; break;
        }
    }

    private static MetaverseObject Mvo(IEnumerable<HeldValue> values)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        foreach (var held in values)
        {
            var value = new MetaverseObjectAttributeValue { AttributeId = held.AttributeId, NullValue = held.AssertedNull };
            Assign(held, value);
            mvo.AttributeValues.Add(value);
        }
        return mvo;
    }

    private static ConnectedSystemObject Cso(IEnumerable<HeldValue> values)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = 1 };
        foreach (var held in values.Where(v => !v.AssertedNull))
        {
            var value = new ConnectedSystemObjectAttributeValue { AttributeId = held.AttributeId };
            Assign(held, value);
            cso.AttributeValues.Add(value);
        }
        return cso;
    }

    private static void Assign(HeldValue held, object target)
    {
        switch (target)
        {
            case MetaverseObjectAttributeValue mv:
                mv.StringValue = held.Value as string;
                mv.IntValue = held.Value as int?;
                mv.LongValue = held.Value as long?;
                mv.DecimalValue = held.Value as decimal?;
                mv.DateTimeValue = held.Value as DateTime?;
                mv.BoolValue = held.Value as bool?;
                mv.GuidValue = held.Value as Guid?;
                break;
            case ConnectedSystemObjectAttributeValue cs:
                cs.StringValue = held.Value as string;
                cs.IntValue = held.Value as int?;
                cs.LongValue = held.Value as long?;
                cs.DecimalValue = held.Value as decimal?;
                cs.DateTimeValue = held.Value as DateTime?;
                cs.BoolValue = held.Value as bool?;
                cs.GuidValue = held.Value as Guid?;
                break;
        }
    }

    /// <summary>
    /// Both evaluations of one rule against one object. <c>InScope</c> is null when the boolean evaluation threw, as it
    /// must on reaching an invalid criterion.
    /// </summary>
    private (bool? InScope, ScopingExplanation Explanation) Evaluate(Side side, SyncRule rule, IEnumerable<HeldValue> values)
    {
        var held = values.ToList();
        bool? inScope;
        ScopingExplanation explanation;
        if (side == Side.Metaverse)
        {
            var mvo = Mvo(held);
            try { inScope = _scoping.IsMvoInScopeForExportRule(mvo, rule, Now); }
            catch (InvalidOperationException) { inScope = null; }
            explanation = _scoping.ExplainMvoForExportRule(mvo, rule, Now);
        }
        else
        {
            var cso = Cso(held);
            try { inScope = _scoping.IsCsoInScopeForImportRule(cso, rule, Now); }
            catch (InvalidOperationException) { inScope = null; }
            explanation = _scoping.ExplainCsoForImportRule(cso, rule, Now);
        }
        return (inScope, explanation);
    }

    private static ScopingRuleOutcome Expected(bool? inScope) => inScope switch
    {
        true => ScopingRuleOutcome.InScope,
        false => ScopingRuleOutcome.OutOfScope,
        null => ScopingRuleOutcome.Undetermined
    };

    #endregion

    #region Agreement with the boolean evaluation

    /// <summary>A sample value an object may hold: a real value, an absent value, or (Metaverse only) an asserted null.</summary>
    private sealed record Sample(string Label, object? Value, bool Absent = false, bool AssertedNull = false);

    private static IEnumerable<(AttributeDataType Type, object?[] Expected, Sample[] Held)> TypeSamples()
    {
        yield return (AttributeDataType.Text, ["Finance", null], [
            new("exact", "Finance"), new("other case", "finance"), new("prefix", "Fin"), new("longer", "Finance Team"),
            new("suffix", "Corporate Finance"), new("different", "Sales"), new("empty", ""),
            new("absent", null, Absent: true), new("asserted null", null, AssertedNull: true)]);
        yield return (AttributeDataType.Number, [10, null], [
            new("less", 5), new("equal", 10), new("greater", 15), new("absent", null, Absent: true), new("asserted null", null, AssertedNull: true)]);
        yield return (AttributeDataType.LongNumber, [10L, null], [
            new("less", 5L), new("equal", 10L), new("greater", 15L), new("absent", null, Absent: true), new("asserted null", null, AssertedNull: true)]);
        yield return (AttributeDataType.Decimal, [10.0m, null], [
            new("less", 5.5m), new("equal, other scale", 10.00m), new("greater", 15m), new("absent", null, Absent: true), new("asserted null", null, AssertedNull: true)]);
        var march = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        yield return (AttributeDataType.DateTime, [march, null], [
            new("before", march.AddDays(-1)), new("equal", march), new("after", march.AddHours(1)),
            new("absent", null, Absent: true), new("asserted null", null, AssertedNull: true)]);
        yield return (AttributeDataType.Boolean, [true, null], [
            new("true", true), new("false", false), new("absent", null, Absent: true), new("asserted null", null, AssertedNull: true)]);
        yield return (AttributeDataType.Guid, [GuidA, null], [
            new("same", GuidA), new("different", GuidB), new("absent", null, Absent: true), new("asserted null", null, AssertedNull: true)]);
    }

    private static IEnumerable<HeldValue> Holding(ScopedAttribute attribute, Sample sample) =>
        sample.Absent ? [] : [new HeldValue(attribute.Id, attribute.Type, sample.Value, sample.AssertedNull)];

    [Test]
    public void Explain_EveryOperatorTypeAndValueState_AgreesWithBooleanEvaluation(
        [Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var mismatches = new List<string>();
        var evaluated = 0;
        foreach (var (type, expectedValues, samples) in TypeSamples())
        {
            var attribute = new ScopedAttribute(10, $"{type} attribute", type);
            foreach (var op in SearchComparisonOperators.ValidOperatorsFor(type))
            foreach (var expected in expectedValues)
            foreach (var sample in samples.Where(s => side == Side.Metaverse || !s.AssertedNull))
            foreach (var caseSensitive in type == AttributeDataType.Text ? new[] { true, false } : [true])
            {
                var rule = Rule(side, Group(SearchGroupType.All, Criterion(side, attribute, op, expected, caseSensitive)));
                var (inScope, explanation) = Evaluate(side, rule, Holding(attribute, sample));
                evaluated++;

                var criterion = explanation.Groups.Single().Criteria.Single();
                var expectedCriterionOutcome = inScope == true
                    ? ScopingCriterionOutcome.Met
                    : sample.Absent || sample.AssertedNull ? ScopingCriterionOutcome.NoValue : ScopingCriterionOutcome.NotMet;

                if (explanation.Outcome != Expected(inScope) || criterion.Outcome != expectedCriterionOutcome)
                    mismatches.Add($"{type} {op} expected={expected ?? "null"} held={sample.Label} caseSensitive={caseSensitive}: " +
                                   $"boolean={inScope}, explanation={explanation.Outcome}/{criterion.Outcome}");
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evaluated, Is.GreaterThan(400), "the sweep must actually cover the matrix");
            Assert.That(mismatches, Is.Empty, string.Join(Environment.NewLine, mismatches));
        }
    }

    [Test]
    public void Explain_EveryRelativeDateCriterion_AgreesWithBooleanEvaluation(
        [Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var mismatches = new List<string>();
        foreach (var op in SearchComparisonOperators.ValidOperatorsFor(AttributeDataType.DateTime))
        foreach (var unit in Enum.GetValues<RelativeDateUnit>())
        foreach (var direction in Enum.GetValues<RelativeDateDirection>())
        {
            var boundary = RelativeDateResolver.Resolve(3, unit, direction, Now);
            Sample[] samples =
            [
                new("before boundary", boundary.AddMinutes(-1)), new("at boundary", boundary), new("after boundary", boundary.AddMinutes(1)),
                new("absent", null, Absent: true), new("asserted null", null, AssertedNull: true)
            ];
            foreach (var sample in samples.Where(s => side == Side.Metaverse || !s.AssertedNull))
            {
                var rule = Rule(side, Group(SearchGroupType.All, RelativeCriterion(side, StartDate, op, 3, unit, direction)));
                var (inScope, explanation) = Evaluate(side, rule, Holding(StartDate, sample));
                if (explanation.Outcome != Expected(inScope))
                    mismatches.Add($"{op} 3 {unit} {direction} held={sample.Label}: boolean={inScope}, explanation={explanation.Outcome}");
            }
        }

        Assert.That(mismatches, Is.Empty, string.Join(Environment.NewLine, mismatches));
    }

    /// <summary>
    /// Random trees, values and (occasionally) invalid criteria, seeded so a failure reproduces: the explanation's
    /// outcome must be the boolean evaluation's for every one, and each group's recorded outcome must follow from its
    /// own children.
    /// </summary>
    [Test]
    public void Explain_RandomTreesAndValues_AgreeWithBooleanEvaluationAndAreInternallyConsistent(
        [Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var random = new Random(348);
        ScopedAttribute[] attributes = [Department, Grade, Active, StartDate];
        var mismatches = new List<string>();
        var outcomes = new Dictionary<ScopingRuleOutcome, int>();

        for (var iteration = 0; iteration < 2500; iteration++)
        {
            var topLevelGroups = Enumerable.Range(0, random.Next(0, 4)).Select(_ => RandomGroup(random, side, attributes, depth: 1)).ToArray();
            var rule = Rule(side, topLevelGroups);
            var held = attributes.SelectMany(a => RandomHeld(random, side, a)).ToList();

            var (inScope, explanation) = Evaluate(side, rule, held);
            outcomes[explanation.Outcome] = outcomes.GetValueOrDefault(explanation.Outcome) + 1;

            if (explanation.Outcome != Expected(inScope))
                mismatches.Add($"iteration {iteration}: boolean={inScope}, explanation={explanation.Outcome}");

            for (var i = 0; i < explanation.Groups.Count; i++)
                CheckGroupConsistency(explanation.Groups[i], (i + 1).ToString(CultureInfo.InvariantCulture), mismatches, iteration);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches, Is.Empty, string.Join(Environment.NewLine, mismatches.Take(20)));
            Assert.That(outcomes.Keys, Is.EquivalentTo(Enum.GetValues<ScopingRuleOutcome>()),
                "the generator must exercise every outcome, or the comparison proves less than it claims");
        }
    }

    private static SyncRuleScopingCriteriaGroup RandomGroup(Random random, Side side, ScopedAttribute[] attributes, int depth)
    {
        var children = new List<object>();
        var criterionCount = random.Next(0, 4);
        for (var i = 0; i < criterionCount; i++)
            children.Add(RandomCriterion(random, side, attributes));
        if (depth < 4)
        {
            var childGroupCount = random.Next(0, depth == 1 ? 3 : 2);
            for (var i = 0; i < childGroupCount; i++)
                children.Add(RandomGroup(random, side, attributes, depth + 1));
        }
        return Group(random.Next(2) == 0 ? SearchGroupType.All : SearchGroupType.Any, children.ToArray());
    }

    private static SyncRuleScopingCriteria RandomCriterion(Random random, Side side, ScopedAttribute[] attributes)
    {
        // About one criterion in a hundred has no attribute, and about one in sixty an operator its type cannot take.
        if (random.Next(100) == 0)
            return Criterion(side, null, SearchComparisonType.Equals, "x");

        var attribute = attributes[random.Next(attributes.Length)];
        var validOperators = SearchComparisonOperators.ValidOperatorsFor(attribute.Type);
        var op = random.Next(60) == 0
            ? Enum.GetValues<SearchComparisonType>().First(o => o != SearchComparisonType.NotSet && !validOperators.Contains(o))
            : validOperators[random.Next(validOperators.Count)];

        if (attribute.Type == AttributeDataType.DateTime && random.Next(3) == 0)
            return RelativeCriterion(side, attribute, op, random.Next(1, 40), (RelativeDateUnit)random.Next(5), (RelativeDateDirection)random.Next(2));

        return Criterion(side, attribute, op, RandomValue(random, attribute.Type, allowNull: true), caseSensitive: random.Next(2) == 0);
    }

    private static IEnumerable<HeldValue> RandomHeld(Random random, Side side, ScopedAttribute attribute)
    {
        var roll = random.Next(10);
        if (roll == 0)
            return [];
        if (roll == 1 && side == Side.Metaverse)
            return [new HeldValue(attribute.Id, attribute.Type, null, AssertedNull: true)];

        // About one attribute in five holds several values (#1923), on the Metaverse side sometimes beside a marker.
        if (roll is 2 or 3)
        {
            var several = new List<HeldValue>();
            if (side == Side.Metaverse && random.Next(3) == 0)
                several.Add(new HeldValue(attribute.Id, attribute.Type, null, AssertedNull: true));
            var count = random.Next(2, 4);
            for (var i = 0; i < count; i++)
                several.Add(new HeldValue(attribute.Id, attribute.Type, RandomValue(random, attribute.Type, allowNull: false)));
            return several;
        }

        return [new HeldValue(attribute.Id, attribute.Type, RandomValue(random, attribute.Type, allowNull: false))];
    }

    private static object? RandomValue(Random random, AttributeDataType type, bool allowNull)
    {
        if (allowNull && random.Next(8) == 0)
            return null;
        return type switch
        {
            AttributeDataType.Text => new[] { "Finance", "finance", "Sales", "Fin", "Finance Team", "" }[random.Next(6)],
            AttributeDataType.Number => random.Next(0, 12),
            AttributeDataType.Boolean => random.Next(2) == 0,
            AttributeDataType.DateTime => Now.Date.AddDays(random.Next(-60, 60)),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private static void CheckGroupConsistency(ScopingGroupExplanation group, string expectedPath, List<string> mismatches, int iteration)
    {
        if (group.Path != expectedPath)
            mismatches.Add($"iteration {iteration}: group path {group.Path}, expected {expectedPath}");

        var childOutcomes = group.Criteria.Select(c => c.Outcome == ScopingCriterionOutcome.Invalid ? (bool?)null : c.Outcome == ScopingCriterionOutcome.Met)
            .Concat(group.ChildGroups.Select(g => g.Met))
            .ToList();
        bool? expectedMet = childOutcomes.Any(o => o == null)
            ? null
            : childOutcomes.Count == 0 || (group.Type == SearchGroupType.All ? childOutcomes.All(o => o == true) : childOutcomes.Any(o => o == true));

        if (group.Met != expectedMet || group.ChildCount != childOutcomes.Count || group.MetCount != childOutcomes.Count(o => o == true))
            mismatches.Add($"iteration {iteration}: group {group.Path} recorded Met={group.Met} {group.MetCount}/{group.ChildCount}, " +
                           $"children give Met={expectedMet} {childOutcomes.Count(o => o == true)}/{childOutcomes.Count}");

        var position = 1;
        foreach (var criterion in group.Criteria)
        {
            var path = $"{expectedPath}.{position++}";
            if (criterion.Path != path)
                mismatches.Add($"iteration {iteration}: criterion path {criterion.Path}, expected {path}");
        }
        foreach (var childGroup in group.ChildGroups)
            CheckGroupConsistency(childGroup, $"{expectedPath}.{position++}", mismatches, iteration);
    }

    #endregion

    #region Tree shape

    /// <summary>
    /// The PRD's worked example extended to four levels: one top-level All group that fails on a nested Any branch,
    /// and a second top-level group that fails outright.
    /// </summary>
    private static SyncRule NestedRule(Side side, string secondGroupDepartment) => Rule(side,
        Group(SearchGroupType.All,
            Criterion(side, Department, SearchComparisonType.Equals, "Finance"),                         // 1.1 met
            Criterion(side, Grade, SearchComparisonType.GreaterThan, 5),                                // 1.2 met
            Group(SearchGroupType.Any,                                                                   // 1.3 not met
                Criterion(side, CostCentre, SearchComparisonType.StartsWith, "FIN"),                    // 1.3.1 no value
                Criterion(side, JobTitle, SearchComparisonType.Contains, "Accountant"),                 // 1.3.2 not met
                Group(SearchGroupType.All,                                                               // 1.3.3 not met
                    Criterion(side, Active, SearchComparisonType.Equals, true),                          // 1.3.3.1 met
                    Group(SearchGroupType.Any,                                                           // 1.3.3.2 not met
                        Criterion(side, Grade, SearchComparisonType.LessThan, 2))))),                   // 1.3.3.2.1 not met
        Group(SearchGroupType.Any,
            Criterion(side, Department, SearchComparisonType.Equals, secondGroupDepartment)));          // 2.1

    private static HeldValue[] Jane =>
    [
        new(Department.Id, Department.Type, "Finance"),
        new(Grade.Id, Grade.Type, 7),
        new(JobTitle.Id, JobTitle.Type, "Software Engineer"),
        new(Active.Id, Active.Type, true)
    ];

    [Test]
    public void Explain_NestedTreeFourLevelsDeep_RecordsEveryNodeWithPathsAndCounts([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var (inScope, explanation) = Evaluate(side, NestedRule(side, "Sales"), Jane);

        var first = explanation.Groups[0];
        var anyBranch = first.ChildGroups.Single();
        var allBranch = anyBranch.ChildGroups.Single();
        var deepest = allBranch.ChildGroups.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.False);
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.OutOfScope));
            Assert.That(explanation.HasCriteria, Is.True);
            Assert.That(explanation.Groups.Select(g => g.Path), Is.EqualTo(new[] { "1", "2" }));

            Assert.That(first.Criteria.Select(c => (c.Path, c.Outcome)), Is.EqualTo(new[]
            {
                ("1.1", ScopingCriterionOutcome.Met), ("1.2", ScopingCriterionOutcome.Met)
            }));
            Assert.That((first.Met, first.MetCount, first.ChildCount), Is.EqualTo(((bool?)false, 2, 3)));

            Assert.That(anyBranch.Path, Is.EqualTo("1.3"));
            Assert.That(anyBranch.Criteria.Select(c => (c.Path, c.Outcome)), Is.EqualTo(new[]
            {
                ("1.3.1", ScopingCriterionOutcome.NoValue), ("1.3.2", ScopingCriterionOutcome.NotMet)
            }));
            Assert.That((anyBranch.Met, anyBranch.MetCount, anyBranch.ChildCount), Is.EqualTo(((bool?)false, 0, 3)));

            Assert.That(allBranch.Path, Is.EqualTo("1.3.3"));
            Assert.That((allBranch.Met, allBranch.MetCount, allBranch.ChildCount), Is.EqualTo(((bool?)false, 1, 2)));
            Assert.That(allBranch.Criteria.Single().Path, Is.EqualTo("1.3.3.1"));

            Assert.That(deepest.Path, Is.EqualTo("1.3.3.2"));
            Assert.That(deepest.Criteria.Single().Path, Is.EqualTo("1.3.3.2.1"));
            Assert.That(deepest.Met, Is.False);

            Assert.That(explanation.Groups[1].Met, Is.False);
        }
    }

    [Test]
    public void Explain_LaterTopLevelGroupMet_InScopeAndEveryGroupStillExplained([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var (inScope, explanation) = Evaluate(side, NestedRule(side, "Finance"), Jane);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.True);
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
            Assert.That(explanation.Groups.Select(g => g.Met), Is.EqualTo(new bool?[] { false, true }));
            Assert.That(explanation.Groups[0].ChildGroups.Single().ChildGroups.Single().ChildGroups.Single().Criteria, Has.Count.EqualTo(1),
                "a failed group before the met one is still explained in full");
        }
    }

    [Test]
    public void Explain_EarlierTopLevelGroupMet_LaterGroupsStillExplained([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var rule = Rule(side,
            Group(SearchGroupType.All, Criterion(side, Department, SearchComparisonType.Equals, "Finance")),
            Group(SearchGroupType.All, Criterion(side, Grade, SearchComparisonType.LessThan, 2)));

        var (inScope, explanation) = Evaluate(side, rule, Jane);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.True);
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
            Assert.That(explanation.Groups.Select(g => g.Met), Is.EqualTo(new bool?[] { true, false }));
            Assert.That(explanation.Groups[1].Criteria.Single().ActualDisplay, Is.EqualTo("7"));
        }
    }

    [Test]
    public void Explain_NoScopingCriteria_InScopeWithNoGroups([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var (inScope, explanation) = Evaluate(side, Rule(side), Jane);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.True);
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
            Assert.That(explanation.HasCriteria, Is.False);
            Assert.That(explanation.Groups, Is.Empty);
        }
    }

    [Test]
    public void Explain_EmptyGroup_MetWithNoChildren([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var (inScope, explanation) = Evaluate(side, Rule(side, Group(SearchGroupType.Any)), Jane);

        var group = explanation.Groups.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.True);
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
            Assert.That(explanation.HasCriteria, Is.True);
            Assert.That((group.Met, group.MetCount, group.ChildCount), Is.EqualTo(((bool?)true, 0, 0)));
        }
    }

    [Test]
    public void Explain_RuleIdentityAndEvaluationTime_AreRecorded()
    {
        var rule = Rule(Side.Metaverse, Group(SearchGroupType.All, Criterion(Side.Metaverse, Department, SearchComparisonType.Equals, "Finance")));

        var explanation = _scoping.ExplainMvoForExportRule(Mvo(Jane), rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(explanation.SyncRuleId, Is.EqualTo(7));
            Assert.That(explanation.SyncRuleName, Is.EqualTo("Finance App Users"));
            Assert.That(explanation.Direction, Is.EqualTo(SyncRuleDirection.Export));
            Assert.That(explanation.EvaluatedAt, Is.EqualTo(Now));
            Assert.That(explanation.Groups.Single().GroupId, Is.EqualTo(rule.ObjectScopingCriteriaGroups[0].Id));
            Assert.That(explanation.Groups.Single().Criteria.Single().CriterionId, Is.EqualTo(rule.ObjectScopingCriteriaGroups[0].Criteria[0].Id));
        }
    }

    [Test]
    public void ExplainMvoForExportRule_ImportRule_Throws()
    {
        Assert.That(() => _scoping.ExplainMvoForExportRule(Mvo(Jane), Rule(Side.ConnectedSystem), Now), Throws.ArgumentException);
    }

    [Test]
    public void ExplainCsoForImportRule_ExportRule_Throws()
    {
        Assert.That(() => _scoping.ExplainCsoForImportRule(Cso(Jane), Rule(Side.Metaverse), Now), Throws.ArgumentException);
    }

    #endregion

    #region Invalid and missing criteria

    private static SyncRuleScopingCriteria InvalidCriterion(Side side) =>
        Criterion(side, StartDate, SearchComparisonType.StartsWith, null);

    [Test]
    public void Explain_InvalidCriterion_UndeterminedWithTheRestOfTheTreeExplained([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var rule = Rule(side, Group(SearchGroupType.Any,
            Criterion(side, Department, SearchComparisonType.Equals, "Finance"),
            InvalidCriterion(side),
            Group(SearchGroupType.All, Criterion(side, Grade, SearchComparisonType.GreaterThan, 5))));

        var (inScope, explanation) = Evaluate(side, rule, Jane);

        var group = explanation.Groups.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.Null, "synchronisation fails on an invalid criterion");
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.Undetermined));
            Assert.That(group.Criteria.Select(c => c.Outcome), Is.EqualTo(new[] { ScopingCriterionOutcome.Met, ScopingCriterionOutcome.Invalid }));
            Assert.That(group.Met, Is.Null);
            Assert.That(group.ChildGroups.Single().Met, Is.True, "the rest of the tree is still evaluated");
            Assert.That(group.Criteria[1].AttributeName, Is.EqualTo("Start Date"));
            Assert.That(group.Criteria[1].ComparisonType, Is.EqualTo(SearchComparisonType.StartsWith));
        }
    }

    [Test]
    public void Explain_InvalidCriterionNestedDeep_UndeterminedUpTheBranch([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var rule = Rule(side, Group(SearchGroupType.All,
            Criterion(side, Department, SearchComparisonType.Equals, "Finance"),
            Group(SearchGroupType.Any,
                Criterion(side, Grade, SearchComparisonType.GreaterThan, 5),
                Group(SearchGroupType.All, InvalidCriterion(side)))));

        var (inScope, explanation) = Evaluate(side, rule, Jane);

        var top = explanation.Groups.Single();
        var any = top.ChildGroups.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.Null);
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.Undetermined));
            Assert.That(any.ChildGroups.Single().Met, Is.Null);
            Assert.That(any.Met, Is.Null, "an Any group with a met child still fails in synchronisation: every child is evaluated");
            Assert.That(any.MetCount, Is.EqualTo(1));
            Assert.That(top.Met, Is.Null);
        }
    }

    [Test]
    public void Explain_InvalidCriterionInGroupAfterAMetGroup_InScopeAsSynchronisationDecides([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var rule = Rule(side,
            Group(SearchGroupType.All, Criterion(side, Department, SearchComparisonType.Equals, "Finance")),
            Group(SearchGroupType.All, InvalidCriterion(side)));

        var (inScope, explanation) = Evaluate(side, rule, Jane);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.True, "synchronisation stops at the first met top-level group and never reaches the invalid one");
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
            Assert.That(explanation.Groups[1].Met, Is.Null);
            Assert.That(explanation.Groups[1].Criteria.Single().Outcome, Is.EqualTo(ScopingCriterionOutcome.Invalid));
        }
    }

    [Test]
    public void Explain_InvalidCriterionInGroupBeforeAMetGroup_Undetermined([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var rule = Rule(side,
            Group(SearchGroupType.All, InvalidCriterion(side)),
            Group(SearchGroupType.All, Criterion(side, Department, SearchComparisonType.Equals, "Finance")));

        var (inScope, explanation) = Evaluate(side, rule, Jane);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.Null);
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.Undetermined));
            Assert.That(explanation.Groups.Select(g => g.Met), Is.EqualTo(new bool?[] { null, true }));
        }
    }

    [Test]
    public void Explain_CriterionWithNoAttribute_AttributeMissingAndNotMet([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var rule = Rule(side, Group(SearchGroupType.All, Criterion(side, null, SearchComparisonType.Equals, null)));

        var (inScope, explanation) = Evaluate(side, rule, Jane);

        var criterion = explanation.Groups.Single().Criteria.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.False);
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.OutOfScope));
            Assert.That(criterion.Outcome, Is.EqualTo(ScopingCriterionOutcome.AttributeMissing));
            Assert.That(criterion.AttributeId, Is.Null);
            Assert.That(criterion.AttributeName, Is.Null);
        }
    }

    #endregion

    #region Values

    [Test]
    public void Explain_ValuesRenderedCultureInvariantly([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var decimalAttribute = new ScopedAttribute(20, "Allowance", AttributeDataType.Decimal);
            var longAttribute = new ScopedAttribute(21, "Employee Number", AttributeDataType.LongNumber);
            var guidAttribute = new ScopedAttribute(22, "Object Guid", AttributeDataType.Guid);
            var midnightAttribute = new ScopedAttribute(23, "Leave Date", AttributeDataType.DateTime);
            var secondsAttribute = new ScopedAttribute(24, "Last Logon", AttributeDataType.DateTime);
            var rule = Rule(side, Group(SearchGroupType.All,
                Criterion(side, decimalAttribute, SearchComparisonType.GreaterThan, 1234.5m),
                Criterion(side, longAttribute, SearchComparisonType.Equals, 9876543210L),
                Criterion(side, guidAttribute, SearchComparisonType.Equals, GuidA),
                Criterion(side, midnightAttribute, SearchComparisonType.LessThan, new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc)),
                Criterion(side, secondsAttribute, SearchComparisonType.GreaterThan, new DateTime(2026, 10, 4, 10, 41, 0, DateTimeKind.Utc)),
                Criterion(side, Active, SearchComparisonType.Equals, true)));
            HeldValue[] held =
            [
                new(decimalAttribute.Id, decimalAttribute.Type, 1234.50m),
                new(longAttribute.Id, longAttribute.Type, 9876543210L),
                new(guidAttribute.Id, guidAttribute.Type, GuidB),
                new(midnightAttribute.Id, midnightAttribute.Type, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
                new(secondsAttribute.Id, secondsAttribute.Type, new DateTime(2026, 10, 4, 10, 41, 30, DateTimeKind.Utc)),
                new(Active.Id, Active.Type, false)
            ];

            var (_, explanation) = Evaluate(side, rule, held);

            var criteria = explanation.Groups.Single().Criteria;
            Assert.That(criteria.Select(c => (c.ExpectedDisplay, c.ActualDisplay)), Is.EqualTo(new (string?, string?)[]
            {
                ("1234.5", "1234.50"),
                ("9876543210", "9876543210"),
                ("6f9619ff-8b86-d011-b42d-00c04fc964ff", "0c2a7a3e-1d2b-4c5e-9f60-718293a4b5c6"),
                ("4 Oct 2026", "1 Mar 2026"),
                ("4 Oct 2026 10:41 UTC", "4 Oct 2026 10:41:30 UTC"),
                ("True", "False")
            }));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void Explain_TextCriterion_RecordsAttributeComparisonAndCaseSensitivity([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var rule = Rule(side, Group(SearchGroupType.All, Criterion(side, JobTitle, SearchComparisonType.Contains, "Accountant", caseSensitive: false)));

        var (_, explanation) = Evaluate(side, rule, Jane);

        var criterion = explanation.Groups.Single().Criteria.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.AttributeId, Is.EqualTo(JobTitle.Id));
            Assert.That(criterion.AttributeName, Is.EqualTo("Job Title"));
            Assert.That(criterion.AttributeType, Is.EqualTo(AttributeDataType.Text));
            Assert.That(criterion.ComparisonType, Is.EqualTo(SearchComparisonType.Contains));
            Assert.That(criterion.CaseSensitive, Is.False);
            Assert.That(criterion.ExpectedDisplay, Is.EqualTo("Accountant"));
            Assert.That(criterion.ActualDisplay, Is.EqualTo("Software Engineer"));
            Assert.That(criterion.ValueMode, Is.EqualTo(DateCriteriaValueMode.Absolute));
            Assert.That(criterion.RelativeDisplay, Is.Null);
            Assert.That(criterion.ResolvedDate, Is.Null);
        }
    }

    [Test]
    public void Explain_EmptyExpectedValueAndNoValueHeld_BothRenderAsNull([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var rule = Rule(side, Group(SearchGroupType.All, Criterion(side, CostCentre, SearchComparisonType.Equals, null)));

        var (inScope, explanation) = Evaluate(side, rule, Jane);

        var criterion = explanation.Groups.Single().Criteria.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.True, "Equals an empty value matches an object holding none");
            Assert.That(criterion.Outcome, Is.EqualTo(ScopingCriterionOutcome.Met));
            Assert.That(criterion.ExpectedDisplay, Is.Null);
            Assert.That(criterion.ActualDisplay, Is.Null);
        }
    }

    [Test]
    public void Explain_RelativeDateCriterion_RecordsOffsetAndResolvedBoundary([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var rule = Rule(side, Group(SearchGroupType.All,
            RelativeCriterion(side, StartDate, SearchComparisonType.GreaterThan, 30, RelativeDateUnit.Days, RelativeDateDirection.Ago)));
        HeldValue[] held = [new(StartDate.Id, StartDate.Type, new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc))];

        var (inScope, explanation) = Evaluate(side, rule, held);

        var criterion = explanation.Groups.Single().Criteria.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.True);
            Assert.That(criterion.ValueMode, Is.EqualTo(DateCriteriaValueMode.Relative));
            Assert.That(criterion.RelativeDisplay, Is.EqualTo("30 days ago"));
            Assert.That(criterion.ResolvedDate, Is.EqualTo(RelativeDateResolver.Resolve(30, RelativeDateUnit.Days, RelativeDateDirection.Ago, Now)));
            Assert.That(criterion.ExpectedDisplay, Is.EqualTo("4 Sep 2026"));
            Assert.That(criterion.ActualDisplay, Is.EqualTo("20 Sep 2026"));
        }
    }

    [Test]
    public void ExplainMvoForExportRule_MultipleValues_ShowsTheValueThatMatchedAndCountsOnlyRealValues()
    {
        var rule = Rule(Side.Metaverse, Group(SearchGroupType.All, Criterion(Side.Metaverse, Department, SearchComparisonType.Equals, "Finance")));
        HeldValue[] held =
        [
            new(Department.Id, Department.Type, null, AssertedNull: true),
            new(Department.Id, Department.Type, "Sales"),
            new(Department.Id, Department.Type, "Finance"),
            new(Department.Id, Department.Type, "Legal")
        ];

        var (inScope, explanation) = Evaluate(Side.Metaverse, rule, held);

        var criterion = explanation.Groups.Single().Criteria.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(inScope, Is.True, "a positive operator is met by any value (#1923)");
            Assert.That(criterion.Outcome, Is.EqualTo(ScopingCriterionOutcome.Met));
            Assert.That(criterion.ActualDisplay, Is.EqualTo("Finance"), "the value that met it, not whichever loaded first");
            Assert.That(criterion.ValueCount, Is.EqualTo(3), "the asserted-null marker is not a value");
        }
    }

    [Test]
    public void Explain_MultipleValues_PositiveOperatorThatNoValueMeets_NamesNoSingleValue()
    {
        foreach (var side in new[] { Side.Metaverse, Side.ConnectedSystem })
        {
            var rule = Rule(side, Group(SearchGroupType.All, Criterion(side, Department, SearchComparisonType.Equals, "Finance")));
            HeldValue[] held =
            [
                new(Department.Id, Department.Type, "Sales"),
                new(Department.Id, Department.Type, "Legal"),
                new(Department.Id, Department.Type, "HR")
            ];

            var (inScope, explanation) = Evaluate(side, rule, held);

            var criterion = explanation.Groups.Single().Criteria.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(inScope, Is.False, side.ToString());
                Assert.That(criterion.Outcome, Is.EqualTo(ScopingCriterionOutcome.NotMet), side.ToString());
                Assert.That(criterion.ActualDisplay, Is.Null, $"{side}: every value failed, so no one value decided it");
                Assert.That(criterion.ValueCount, Is.EqualTo(3), side.ToString());
            }
        }
    }

    [Test]
    public void Explain_MultipleValues_NegatedOperatorThatOneValueBreaks_ShowsThatValue()
    {
        foreach (var side in new[] { Side.Metaverse, Side.ConnectedSystem })
        {
            var rule = Rule(side, Group(SearchGroupType.All, Criterion(side, Department, SearchComparisonType.NotEquals, "Finance")));
            HeldValue[] held =
            [
                new(Department.Id, Department.Type, "Sales"),
                new(Department.Id, Department.Type, "Finance")
            ];

            var (inScope, explanation) = Evaluate(side, rule, held);

            var criterion = explanation.Groups.Single().Criteria.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(inScope, Is.False, $"{side}: a negated operator needs no value to match what it negates (#1923)");
                Assert.That(criterion.Outcome, Is.EqualTo(ScopingCriterionOutcome.NotMet), side.ToString());
                Assert.That(criterion.ActualDisplay, Is.EqualTo("Finance"), side.ToString());
                Assert.That(criterion.ValueCount, Is.EqualTo(2), side.ToString());
            }
        }
    }

    [Test]
    public void Explain_MultipleValues_NegatedOperatorEveryValueMeets_NamesNoSingleValue()
    {
        foreach (var side in new[] { Side.Metaverse, Side.ConnectedSystem })
        {
            var rule = Rule(side, Group(SearchGroupType.All, Criterion(side, Department, SearchComparisonType.NotEquals, "Finance")));
            HeldValue[] held =
            [
                new(Department.Id, Department.Type, "Sales"),
                new(Department.Id, Department.Type, "Legal")
            ];

            var (inScope, explanation) = Evaluate(side, rule, held);

            var criterion = explanation.Groups.Single().Criteria.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(inScope, Is.True, side.ToString());
                Assert.That(criterion.Outcome, Is.EqualTo(ScopingCriterionOutcome.Met), side.ToString());
                Assert.That(criterion.ActualDisplay, Is.Null, side.ToString());
                Assert.That(criterion.ValueCount, Is.EqualTo(2), side.ToString());
            }
        }
    }

    [Test]
    public void Explain_SingleValue_ShowsItAndCountsOne()
    {
        var rule = Rule(Side.Metaverse, Group(SearchGroupType.All, Criterion(Side.Metaverse, Department, SearchComparisonType.Equals, "Finance")));

        var (_, explanation) = Evaluate(Side.Metaverse, rule, [new(Department.Id, Department.Type, "Sales")]);

        var criterion = explanation.Groups.Single().Criteria.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.ActualDisplay, Is.EqualTo("Sales"), "a lone value is what the outcome turned on, met or not");
            Assert.That(criterion.ValueCount, Is.EqualTo(1));
        }
    }

    [TestCase("userPassword", AttributeDataType.Text)]
    [TestCase("unicodePwd", AttributeDataType.Binary)]
    [TestCase("customPasswordField", AttributeDataType.Text)]
    [TestCase("applicationSecret", AttributeDataType.Text)]
    public void Explain_CredentialAttribute_ValuesMaskedEverywhere(string attributeName, AttributeDataType type)
    {
        const string secret = "Tr0ub4dor&3";
        foreach (var side in new[] { Side.Metaverse, Side.ConnectedSystem })
        {
            var credential = new ScopedAttribute(30, attributeName, type);
            var rule = Rule(side, Group(SearchGroupType.All,
                Criterion(side, credential, SearchComparisonType.Equals, type == AttributeDataType.Text ? secret : null)));
            HeldValue[] held = [new(credential.Id, type, type == AttributeDataType.Text ? secret : null)];

            var (_, explanation) = Evaluate(side, rule, held);

            var criterion = explanation.Groups.Single().Criteria.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(criterion.Masked, Is.True, $"{side}: {attributeName}");
                Assert.That(criterion.ExpectedDisplay, Is.Null);
                Assert.That(criterion.ActualDisplay, Is.Null);
                Assert.That(JsonSerializer.Serialize(explanation), Does.Not.Contain(secret), "the value must not appear anywhere in the explanation");
            }
        }
    }

    [Test]
    public void Explain_CredentialLikeNameOnANonTextAttribute_NotMasked([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        // pwdLastSet looks like a credential by name, but a date cannot hold one, and its value is the reason a
        // "password set within 90 days" rule scopes someone out.
        var pwdLastSet = new ScopedAttribute(31, "pwdLastSet", AttributeDataType.DateTime);
        var rule = Rule(side, Group(SearchGroupType.All,
            Criterion(side, pwdLastSet, SearchComparisonType.GreaterThan, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc))));
        HeldValue[] held = [new(pwdLastSet.Id, pwdLastSet.Type, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc))];

        var (_, explanation) = Evaluate(side, rule, held);

        var criterion = explanation.Groups.Single().Criteria.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.Masked, Is.False);
            Assert.That(criterion.ExpectedDisplay, Is.EqualTo("1 Jul 2026"));
            Assert.That(criterion.ActualDisplay, Is.EqualTo("1 May 2026"));
        }
    }

    #endregion

    #region Synchronisation cost

    /// <summary>
    /// The boolean path is synchronisation's hot path and must allocate nothing per evaluation (#348): the shared
    /// evaluator's trace is only built when an explanation is asked for. Before the shared evaluator, this rule cost
    /// 392 bytes per evaluation (a list per group, a closure per criterion).
    /// </summary>
    [Test]
    public void IsInScope_WarmedUp_AllocatesNothingPerEvaluation([Values(Side.Metaverse, Side.ConnectedSystem)] Side side)
    {
        var rule = Rule(side, Group(SearchGroupType.All,
            Criterion(side, Department, SearchComparisonType.Equals, "Finance", caseSensitive: false),
            RelativeCriterion(side, StartDate, SearchComparisonType.LessThan, 30, RelativeDateUnit.Days, RelativeDateDirection.FromNow),
            Group(SearchGroupType.Any,
                Criterion(side, Grade, SearchComparisonType.GreaterThan, 5),
                Criterion(side, CostCentre, SearchComparisonType.StartsWith, "FIN"))));
        HeldValue[] held = [.. Jane, new(StartDate.Id, StartDate.Type, Now.AddDays(-3))];
        var mvo = Mvo(held);
        var cso = Cso(held);
        bool Run() => side == Side.Metaverse
            ? _scoping.IsMvoInScopeForExportRule(mvo, rule, Now)
            : _scoping.IsCsoInScopeForImportRule(cso, rule, Now);

        Assert.That(Run(), Is.True, "the measured rule must take the full path, not an early exit");
        for (var i = 0; i < 2000; i++)
            Run();

        // The least of several rounds, so a one-off allocation by the runtime itself (tiered compilation) cannot fail it.
        var fewest = long.MaxValue;
        for (var round = 0; round < 5; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                Run();
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.That(fewest, Is.Zero);
    }

    #endregion
}
