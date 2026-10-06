// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Scoping criteria over multi-valued attributes (#1923). A criterion is evaluated against every value the attribute
/// holds, never just whichever value happens to load first: a positive operator is met when any value matches, and a
/// negated operator when no value matches. With no values at all the missing-value rule applies, unchanged.
/// Every case runs against both evaluation paths (export over a Metaverse Object, import over a Connected System
/// Object) and against every ordering of the values, because value order is not defined and must not decide scope.
/// </summary>
[TestFixture]
public class ScopingEvaluationMultiValuedTests
{
    private const int AttributeId = 40;
    private static readonly DateTime BaseDate = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid GuidA = new("6f1c6a52-1f0e-4c43-9a51-0d0f4f1b0a01");
    private static readonly Guid GuidB = new("6f1c6a52-1f0e-4c43-9a51-0d0f4f1b0a02");
    private static readonly Guid GuidC = new("6f1c6a52-1f0e-4c43-9a51-0d0f4f1b0a03");

    private ScopingEvaluationServer _scopingEvaluation = null!;

    [SetUp]
    public void SetUp()
    {
        _scopingEvaluation = new ScopingEvaluationServer();
    }

    #region Every operator, every data type, every value shape

    /// <summary>
    /// One row per operator and data type: the criterion value, two values that satisfy the operator's positive
    /// form ("hits") and two that do not ("misses"). For a negated operator the positive form is the operator it
    /// negates, so a hit is a value the negated operator must reject. Each row expands into six value shapes.
    /// </summary>
    private static IEnumerable<(AttributeDataType Type, SearchComparisonType Operator, object Criterion, object[] Hits, object[] Misses)> OperatorRows()
    {
        // Text
        foreach (var op in new[] { SearchComparisonType.Equals, SearchComparisonType.NotEquals })
            yield return (AttributeDataType.Text, op, "Finance", ["Finance", "Finance"], ["All Staff", "Sales"]);
        foreach (var op in new[] { SearchComparisonType.StartsWith, SearchComparisonType.NotStartsWith })
            yield return (AttributeDataType.Text, op, "Fin", ["Finance", "Finance Readers"], ["All Staff", "Sales"]);
        foreach (var op in new[] { SearchComparisonType.EndsWith, SearchComparisonType.NotEndsWith })
            yield return (AttributeDataType.Text, op, "Readers", ["Finance Readers", "HR Readers"], ["All Staff", "Readers Group"]);
        foreach (var op in new[] { SearchComparisonType.Contains, SearchComparisonType.NotContains })
            yield return (AttributeDataType.Text, op, "Finance", ["Finance Readers", "Global Finance"], ["All Staff", "Sales"]);

        // Number
        foreach (var op in new[] { SearchComparisonType.Equals, SearchComparisonType.NotEquals })
            yield return (AttributeDataType.Number, op, 10, [10, 10], [3, 20]);
        yield return (AttributeDataType.Number, SearchComparisonType.LessThan, 10, [3, 9], [10, 20]);
        yield return (AttributeDataType.Number, SearchComparisonType.LessThanOrEquals, 10, [3, 10], [11, 20]);
        yield return (AttributeDataType.Number, SearchComparisonType.GreaterThan, 10, [11, 20], [3, 10]);
        yield return (AttributeDataType.Number, SearchComparisonType.GreaterThanOrEquals, 10, [10, 20], [3, 9]);

        // Long Number
        const long big = 10_000_000_000L;
        foreach (var op in new[] { SearchComparisonType.Equals, SearchComparisonType.NotEquals })
            yield return (AttributeDataType.LongNumber, op, big, [big, big], [big - 7, big + 10]);
        yield return (AttributeDataType.LongNumber, SearchComparisonType.LessThan, big, [big - 7, big - 1], [big, big + 10]);
        yield return (AttributeDataType.LongNumber, SearchComparisonType.LessThanOrEquals, big, [big - 7, big], [big + 1, big + 10]);
        yield return (AttributeDataType.LongNumber, SearchComparisonType.GreaterThan, big, [big + 1, big + 10], [big - 7, big]);
        yield return (AttributeDataType.LongNumber, SearchComparisonType.GreaterThanOrEquals, big, [big, big + 10], [big - 7, big - 1]);

        // Decimal
        foreach (var op in new[] { SearchComparisonType.Equals, SearchComparisonType.NotEquals })
            yield return (AttributeDataType.Decimal, op, 10.5m, [10.5m, 10.50m], [3.25m, 20m]);
        yield return (AttributeDataType.Decimal, SearchComparisonType.LessThan, 10.5m, [3.25m, 10.49m], [10.5m, 20m]);
        yield return (AttributeDataType.Decimal, SearchComparisonType.LessThanOrEquals, 10.5m, [3.25m, 10.5m], [10.51m, 20m]);
        yield return (AttributeDataType.Decimal, SearchComparisonType.GreaterThan, 10.5m, [10.51m, 20m], [3.25m, 10.5m]);
        yield return (AttributeDataType.Decimal, SearchComparisonType.GreaterThanOrEquals, 10.5m, [10.5m, 20m], [3.25m, 10.49m]);

        // Date/Time
        foreach (var op in new[] { SearchComparisonType.Equals, SearchComparisonType.NotEquals })
            yield return (AttributeDataType.DateTime, op, BaseDate, [BaseDate, BaseDate], [BaseDate.AddDays(-30), BaseDate.AddDays(30)]);
        yield return (AttributeDataType.DateTime, SearchComparisonType.LessThan, BaseDate, [BaseDate.AddDays(-30), BaseDate.AddHours(-1)], [BaseDate, BaseDate.AddDays(30)]);
        yield return (AttributeDataType.DateTime, SearchComparisonType.LessThanOrEquals, BaseDate, [BaseDate.AddDays(-30), BaseDate], [BaseDate.AddHours(1), BaseDate.AddDays(30)]);
        yield return (AttributeDataType.DateTime, SearchComparisonType.GreaterThan, BaseDate, [BaseDate.AddHours(1), BaseDate.AddDays(30)], [BaseDate.AddDays(-30), BaseDate]);
        yield return (AttributeDataType.DateTime, SearchComparisonType.GreaterThanOrEquals, BaseDate, [BaseDate, BaseDate.AddDays(30)], [BaseDate.AddDays(-30), BaseDate.AddHours(-1)]);

        // Boolean
        foreach (var op in new[] { SearchComparisonType.Equals, SearchComparisonType.NotEquals })
            yield return (AttributeDataType.Boolean, op, true, [true, true], [false, false]);

        // Guid
        foreach (var op in new[] { SearchComparisonType.Equals, SearchComparisonType.NotEquals })
            yield return (AttributeDataType.Guid, op, GuidA, [GuidA, GuidA], [GuidB, GuidC]);
    }

    private static IEnumerable<TestCaseData> EveryOperatorOverEveryValueShape()
    {
        foreach (var (type, op, criterion, hits, misses) in OperatorRows())
        {
            var negated = op is SearchComparisonType.NotEquals or SearchComparisonType.NotStartsWith
                or SearchComparisonType.NotEndsWith or SearchComparisonType.NotContains;

            // Positive operators are met when ANY value matches; negated operators when NO value matches the
            // operator they negate. No values at all is never met here (the criterion value is populated, so the
            // missing-value rule, which only matches an empty Equals criterion, does not apply).
            var shapes = new (string Shape, object[] Values, bool PositiveExpected)[]
            {
                ("NoValues", [], false),
                ("OneMatchingValue", [hits[0]], true),
                ("OneNonMatchingValue", [misses[0]], false),
                ("SeveralAllMatching", [hits[0], hits[1]], true),
                ("SeveralNoneMatching", [misses[0], misses[1]], false),
                ("SeveralMixed", [misses[0], hits[0], misses[1]], true)
            };

            foreach (var (shape, values, positiveExpected) in shapes)
            {
                var expected = shape == "NoValues" ? false : negated ? !positiveExpected : positiveExpected;
                yield return new TestCaseData(type, op, criterion, values, expected)
                    .SetName($"IsInScope_{type}{op}_{shape}_{(expected ? "InScope" : "OutOfScope")}");
            }
        }
    }

    [TestCaseSource(nameof(EveryOperatorOverEveryValueShape))]
    public void IsInScope_MultiValuedAttribute_EvaluatesEveryValueRegardlessOfOrder(
        AttributeDataType type, SearchComparisonType op, object criterionValue, object[] values, bool expected)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (var ordering in Permutations(values))
            {
                var orderDescription = ordering.Count == 0 ? "no values" : string.Join(", ", ordering);

                Assert.That(EvaluateMvo(type, op, criterionValue, ordering), Is.EqualTo(expected),
                    $"Export (Metaverse Object) evaluation with values in order [{orderDescription}]");
                Assert.That(EvaluateCso(type, op, criterionValue, ordering), Is.EqualTo(expected),
                    $"Import (Connected System Object) evaluation with values in order [{orderDescription}]");
            }
        }
    }

    #endregion

    #region Specific scenarios

    [Test]
    public void IsMvoInScopeForExportRule_GroupsContainsFinance_InScopeWhicheverGroupLoadsFirst()
    {
        // The example from #1923: Groups = [All Staff, Finance Readers], scoped to Groups Contains "Finance". Before
        // the fix only the first loaded value was compared, so loading "All Staff" first put the object out of scope.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EvaluateMvo(AttributeDataType.Text, SearchComparisonType.Contains, "Finance", ["All Staff", "Finance Readers"]), Is.True);
            Assert.That(EvaluateMvo(AttributeDataType.Text, SearchComparisonType.Contains, "Finance", ["Finance Readers", "All Staff"]), Is.True);
        }
    }

    [Test]
    public void IsCsoInScopeForImportRule_ObjectClassEqualsUser_InScopeWhenUserIsNotTheFirstValue()
    {
        // A directory's objectClass is multi-valued and conventionally lists the most generic class first.
        Assert.That(EvaluateCso(AttributeDataType.Text, SearchComparisonType.Equals, "user", ["top", "person", "organizationalPerson", "user"]), Is.True);
    }

    [Test]
    public void IsInScope_EmptyEqualsCriterion_MatchesOnlyAnAttributeWithNoValues()
    {
        // The missing-value rule is unchanged: an Equals criterion with no value matches an object holding no value,
        // and an object holding several values is not "missing" the attribute.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EvaluateMvo(AttributeDataType.Text, SearchComparisonType.Equals, null, []), Is.True);
            Assert.That(EvaluateCso(AttributeDataType.Text, SearchComparisonType.Equals, null, []), Is.True);
            Assert.That(EvaluateMvo(AttributeDataType.Text, SearchComparisonType.Equals, null, ["All Staff", "Finance Readers"]), Is.False);
            Assert.That(EvaluateCso(AttributeDataType.Text, SearchComparisonType.Equals, null, ["All Staff", "Finance Readers"]), Is.False);
        }
    }

    [Test]
    public void IsInScope_CaseInsensitiveCriterion_AppliesToEveryValue()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EvaluateMvo(AttributeDataType.Text, SearchComparisonType.Contains, "finance", ["ALL STAFF", "FINANCE READERS"], caseSensitive: false), Is.True);
            Assert.That(EvaluateCso(AttributeDataType.Text, SearchComparisonType.Contains, "finance", ["ALL STAFF", "FINANCE READERS"], caseSensitive: false), Is.True);
            Assert.That(EvaluateMvo(AttributeDataType.Text, SearchComparisonType.NotContains, "finance", ["ALL STAFF", "FINANCE READERS"], caseSensitive: false), Is.False);
            Assert.That(EvaluateCso(AttributeDataType.Text, SearchComparisonType.NotContains, "finance", ["ALL STAFF", "FINANCE READERS"], caseSensitive: false), Is.False);
        }
    }

    [Test]
    public void IsMvoInScopeForExportRule_AssertedNullMarkerAlongsideValues_IsIgnored()
    {
        // An asserted-null marker (#91) is not a value: alongside real values it neither matches a positive operator
        // nor satisfies a negated one, so the outcome is decided by the real values alone, in either order.
        var groups = new MetaverseAttribute { Id = AttributeId, Name = "Groups", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var marker = new MetaverseObjectAttributeValue { AttributeId = AttributeId, Attribute = groups, NullValue = true };
        var finance = new MetaverseObjectAttributeValue { AttributeId = AttributeId, Attribute = groups, StringValue = "Finance Readers" };

        using (Assert.EnterMultipleScope())
        {
            foreach (var ordering in new[] { new[] { marker, finance }, new[] { finance, marker } })
            {
                var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 1, Name = "Person" } };
                mvo.AttributeValues.AddRange(ordering);

                Assert.That(_scopingEvaluation.IsMvoInScopeForExportRule(mvo, BuildExportRule(groups, SearchComparisonType.Contains, "Finance")), Is.True);
                Assert.That(_scopingEvaluation.IsMvoInScopeForExportRule(mvo, BuildExportRule(groups, SearchComparisonType.NotContains, "Finance")), Is.False);
            }
        }
    }

    [Test]
    public void IsMvoInScopeForExportRule_RelativeDateCriterion_InScopeWhenAnyDateIsWithinTheWindow()
    {
        // "After 30 days ago" over [100 days ago, 5 days ago]: the recent date is within the window. The Temporal
        // Scope Reconciler's candidate pre-filter selects objects holding ANY date in the window, so evaluation must
        // agree with it.
        var now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var reviewDates = new MetaverseAttribute { Id = AttributeId, Name = "Review Dates", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.MultiValued };

        using (Assert.EnterMultipleScope())
        {
            foreach (var ordering in Permutations([now.AddDays(-100), now.AddDays(-5)]))
            {
                var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 1, Name = "Person" } };
                mvo.AttributeValues.AddRange(ordering.Select(d => new MetaverseObjectAttributeValue { AttributeId = AttributeId, Attribute = reviewDates, DateTimeValue = (DateTime)d }));

                var rule = BuildExportRule(reviewDates, SearchComparisonType.GreaterThan, null);
                var criterion = rule.ObjectScopingCriteriaGroups[0].Criteria[0];
                criterion.ValueMode = DateCriteriaValueMode.Relative;
                criterion.RelativeCount = 30;
                criterion.RelativeUnit = RelativeDateUnit.Days;
                criterion.RelativeDirection = RelativeDateDirection.Ago;

                Assert.That(_scopingEvaluation.IsMvoInScopeForExportRule(mvo, rule, now), Is.True);
            }
        }
    }

    #endregion

    #region Helpers

    private bool EvaluateMvo(AttributeDataType type, SearchComparisonType op, object? criterionValue, IEnumerable<object> values, bool caseSensitive = true)
    {
        var attribute = new MetaverseAttribute { Id = AttributeId, Name = "Multi", Type = type, AttributePlurality = AttributePlurality.MultiValued };
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 1, Name = "Person" } };
        foreach (var value in values)
        {
            var row = new MetaverseObjectAttributeValue { AttributeId = AttributeId, Attribute = attribute };
            SetTypedValue(type, value, s => row.StringValue = s, i => row.IntValue = i, l => row.LongValue = l, d => row.DecimalValue = d,
                dt => row.DateTimeValue = dt, b => row.BoolValue = b, g => row.GuidValue = g);
            mvo.AttributeValues.Add(row);
        }

        return _scopingEvaluation.IsMvoInScopeForExportRule(mvo, BuildExportRule(attribute, op, criterionValue, caseSensitive));
    }

    private bool EvaluateCso(AttributeDataType type, SearchComparisonType op, object? criterionValue, IEnumerable<object> values, bool caseSensitive = true)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = AttributeId, Name = "multi", Type = type, AttributePlurality = AttributePlurality.MultiValued };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = 1, Type = new ConnectedSystemObjectType { Id = 1, Name = "user" } };
        foreach (var value in values)
        {
            var row = new ConnectedSystemObjectAttributeValue { AttributeId = AttributeId, Attribute = attribute };
            SetTypedValue(type, value, s => row.StringValue = s, i => row.IntValue = i, l => row.LongValue = l, d => row.DecimalValue = d,
                dt => row.DateTimeValue = dt, b => row.BoolValue = b, g => row.GuidValue = g);
            cso.AttributeValues.Add(row);
        }

        var rule = new SyncRule
        {
            Id = 2,
            Name = "Test Import Rule",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" },
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "user" }
        };
        var criterion = new SyncRuleScopingCriteria { ConnectedSystemAttribute = attribute, ComparisonType = op, CaseSensitive = caseSensitive };
        SetCriterionValue(criterion, type, criterionValue);
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(criterion);
        rule.ObjectScopingCriteriaGroups.Add(group);

        return _scopingEvaluation.IsCsoInScopeForImportRule(cso, rule);
    }

    private static SyncRule BuildExportRule(MetaverseAttribute attribute, SearchComparisonType op, object? criterionValue, bool caseSensitive = true)
    {
        var rule = new SyncRule
        {
            Id = 1,
            Name = "Test Export Rule",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" },
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "user" }
        };
        var criterion = new SyncRuleScopingCriteria { MetaverseAttribute = attribute, ComparisonType = op, CaseSensitive = caseSensitive };
        SetCriterionValue(criterion, attribute.Type, criterionValue);
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(criterion);
        rule.ObjectScopingCriteriaGroups.Add(group);
        return rule;
    }

    private static void SetCriterionValue(SyncRuleScopingCriteria criterion, AttributeDataType type, object? value)
    {
        if (value == null)
            return;

        SetTypedValue(type, value, s => criterion.StringValue = s, i => criterion.IntValue = i, l => criterion.LongValue = l,
            d => criterion.DecimalValue = d, dt => criterion.DateTimeValue = dt, b => criterion.BoolValue = b, g => criterion.GuidValue = g);
    }

    private static void SetTypedValue(AttributeDataType type, object value, Action<string> setString, Action<int> setInt, Action<long> setLong,
        Action<decimal> setDecimal, Action<DateTime> setDateTime, Action<bool> setBool, Action<Guid> setGuid)
    {
        switch (type)
        {
            case AttributeDataType.Text: setString((string)value); break;
            case AttributeDataType.Number: setInt((int)value); break;
            case AttributeDataType.LongNumber: setLong((long)value); break;
            case AttributeDataType.Decimal: setDecimal((decimal)value); break;
            case AttributeDataType.DateTime: setDateTime((DateTime)value); break;
            case AttributeDataType.Boolean: setBool((bool)value); break;
            case AttributeDataType.Guid: setGuid((Guid)value); break;
            default: throw new ArgumentOutOfRangeException(nameof(type), type, "No scoping criteria support for this data type.");
        }
    }

    /// <summary>
    /// Every ordering of <paramref name="values"/>; a single empty ordering for no values.
    /// </summary>
    private static IEnumerable<IReadOnlyList<object>> Permutations(IReadOnlyList<object> values)
    {
        if (values.Count <= 1)
        {
            yield return values;
            yield break;
        }

        for (var i = 0; i < values.Count; i++)
        {
            var rest = values.Where((_, index) => index != i).ToList();
            foreach (var tail in Permutations(rest))
                yield return new[] { values[i] }.Concat(tail).ToList();
        }
    }

    #endregion
}
