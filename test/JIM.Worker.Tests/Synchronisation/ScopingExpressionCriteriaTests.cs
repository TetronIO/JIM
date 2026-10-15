// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Scoping criteria whose left-hand side is an expression (#2054, PRD Phase 3): the computed value is compared as the
/// type the administrator chose, through the same comparison an attribute's values go through, and a criterion that
/// cannot be evaluated for an object stops that object rather than deciding its scope on a guess.
/// </summary>
[TestFixture]
public class ScopingExpressionCriteriaTests
{
    private ScopingEvaluationServer _scoping = null!;

    private static readonly ConnectedSystemObjectTypeAttribute Department = new() { Id = 10, Name = "department", Type = AttributeDataType.Text };
    private static readonly ConnectedSystemObjectTypeAttribute StartDate = new() { Id = 11, Name = "startDate", Type = AttributeDataType.Text };
    private static readonly ConnectedSystemObjectTypeAttribute Groups = new()
    {
        Id = 12, Name = "groups", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued
    };
    private static readonly MetaverseAttribute MvDepartment = new() { Id = 20, Name = "Department", Type = AttributeDataType.Text };

    [SetUp]
    public void SetUp() => _scoping = new ScopingEvaluationServer();

    #region Import (cs["..."])

    [TestCase("Finance", true)]
    [TestCase(" FINANCE ", true)]
    [TestCase("Facilities", false)]
    public void ImportRule_PrdScenario10_ComparesTheReshapedValue(string department, bool expected)
    {
        var rule = ImportRule(Criterion("Lower(Trim(cs[\"department\"]))", AttributeDataType.Text, SearchComparisonType.StartsWith, stringValue: "fin"));

        Assert.That(_scoping.IsCsoInScopeForImportRule(Cso((Department, department)), rule), Is.EqualTo(expected));
    }

    [Test]
    public void ImportRule_ExpressionComputesNoValue_HasNoValueIsMetAndValueOperatorsAreNot()
    {
        var hasNoValue = ImportRule(Criterion("Trim(cs[\"department\"])", AttributeDataType.Text, SearchComparisonType.HasNoValue));
        var startsWith = ImportRule(Criterion("Trim(cs[\"department\"])", AttributeDataType.Text, SearchComparisonType.StartsWith, stringValue: "f"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_scoping.IsCsoInScopeForImportRule(Cso(), hasNoValue), Is.True);
            Assert.That(_scoping.IsCsoInScopeForImportRule(Cso(), startsWith), Is.False);
        }
    }

    [Test]
    public void ImportRule_ExpressionComputesSeveralValues_IsMetWhenAnyValueIs()
    {
        var rule = ImportRule(Criterion("Lower(cs[\"groups\"])", AttributeDataType.Text, SearchComparisonType.Equals, stringValue: "finance readers"));

        Assert.That(_scoping.IsCsoInScopeForImportRule(Cso((Groups, "All Staff"), (Groups, "Finance Readers")), rule), Is.True);
    }

    [TestCase("15", true)]
    [TestCase("3", false)]
    public void ImportRule_ComparedAsANumber_ComparesNumerically(string level, bool expected)
    {
        // As text "15" < "3"; compared as a number it is the other way round.
        var rule = ImportRule(Criterion("cs[\"department\"]", AttributeDataType.Number, SearchComparisonType.GreaterThan, intValue: 5));

        Assert.That(_scoping.IsCsoInScopeForImportRule(Cso((Department, level)), rule), Is.EqualTo(expected));
    }

    [Test]
    public void ImportRule_ComparedAsADateWithARelativeValue_ResolvesAgainstTheEvaluationInstant()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        var rule = ImportRule(new SyncRuleScopingCriteria
        {
            Expression = "ParseDate(cs[\"startDate\"], \"dd/MM/yyyy\")", CompareAs = AttributeDataType.DateTime,
            ComparisonType = SearchComparisonType.GreaterThan, ValueMode = DateCriteriaValueMode.Relative,
            RelativeCount = 30, RelativeUnit = RelativeDateUnit.Days, RelativeDirection = RelativeDateDirection.Ago
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_scoping.IsCsoInScopeForImportRule(Cso((StartDate, "01/10/2026")), rule, now), Is.True, "started 13 days ago");
            Assert.That(_scoping.IsCsoInScopeForImportRule(Cso((StartDate, "01/06/2026")), rule, now), Is.False, "started months ago");
        }
    }

    [Test]
    public void ImportRule_ComputedValueNotOfTheCompareAsType_StopsTheObject()
    {
        var rule = ImportRule(Criterion("cs[\"department\"]", AttributeDataType.Number, SearchComparisonType.GreaterThan, intValue: 5));

        var ex = Assert.Throws<ScopingCriterionFailedException>(() => _scoping.IsCsoInScopeForImportRule(Cso((Department, "Finance")), rule));

        Assert.That(ex!.Message, Does.Contain("Finance").And.Contain("Number").And.Contain("HR Import"));
    }

    [Test]
    public void ImportRule_ExpressionFailsToEvaluate_StopsTheObject()
    {
        // ParseDate refuses text that does not match its format: the object's scope cannot be decided.
        var rule = ImportRule(new SyncRuleScopingCriteria
        {
            Expression = "ParseDate(cs[\"startDate\"], \"dd/MM/yyyy\")", CompareAs = AttributeDataType.DateTime,
            ComparisonType = SearchComparisonType.HasValue
        });

        var ex = Assert.Throws<ScopingCriterionFailedException>(() => _scoping.IsCsoInScopeForImportRule(Cso((StartDate, "not a date")), rule));

        Assert.That(ex!.Message, Does.Contain("could not be evaluated"));
    }

    [Test]
    public void ImportRule_ExpressionReadsAnAttributeTheTypeDoesNotHave_StopsTheObjectRatherThanReadingNoValue()
    {
        // costCentre was renamed or removed after the rule was saved. Read as no value it would meet Has No Value and
        // scope the object on a name that no longer means anything; it must stop the object instead.
        var rule = ImportRule(Criterion("Trim(cs[\"costCentre\"])", AttributeDataType.Text, SearchComparisonType.HasNoValue));

        var ex = Assert.Throws<ScopingCriterionFailedException>(() => _scoping.IsCsoInScopeForImportRule(Cso((Department, "Finance")), rule));

        Assert.That(ex!.Message, Does.Contain("cs[\"costCentre\"]"));
    }

    [Test]
    public void ImportRule_SchemaComesFromTheRulesObjectTypeWhenTheObjectCarriesNone()
    {
        // On the synchronisation path the rule's object type is loaded with its attributes; the object's may not be.
        var rule = ImportRule(Criterion("Lower(cs[\"department\"])", AttributeDataType.Text, SearchComparisonType.Equals, stringValue: "finance"));
        rule.ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "person", Attributes = [Department, StartDate, Groups] };
        var cso = Cso((Department, "Finance"));
        cso.Type = null!;

        Assert.That(_scoping.IsCsoInScopeForImportRule(cso, rule), Is.True);
    }

    [Test]
    public void ImportRule_NoSchemaAvailableAnywhere_StopsTheObject()
    {
        // Without a schema an absent attribute and a renamed one look the same, so the criterion cannot be trusted.
        var rule = ImportRule(Criterion("Trim(cs[\"department\"])", AttributeDataType.Text, SearchComparisonType.HasNoValue));
        var cso = Cso();
        cso.Type = null!;

        Assert.Throws<ScopingCriterionFailedException>(() => _scoping.IsCsoInScopeForImportRule(cso, rule));
    }

    #endregion

    #region Export (mv["..."])

    [TestCase("Finance", true)]
    [TestCase("Facilities", false)]
    public void ExportRule_ReadsTheMetaverseObject(string department, bool expected)
    {
        var rule = new SyncRule { Name = "AD Export", Direction = SyncRuleDirection.Export };
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = [Criterion("Upper(mv[\"Department\"])", AttributeDataType.Text, SearchComparisonType.Equals, stringValue: "FINANCE")]
        });
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 1, Name = "User", Attributes = [MvDepartment] } };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = MvDepartment, AttributeId = MvDepartment.Id, StringValue = department, MetaverseObject = mvo });

        Assert.That(_scoping.IsMvoInScopeForExportRule(mvo, rule), Is.EqualTo(expected));
    }

    [Test]
    public void ExportRule_ExpressionReadsAnAttributeTheTypeDoesNotHave_StopsTheObject()
    {
        var rule = new SyncRule { Name = "AD Export", Direction = SyncRuleDirection.Export };
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = [Criterion("mv[\"Cost Centre\"]", AttributeDataType.Text, SearchComparisonType.HasNoValue)]
        });
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 1, Name = "User", Attributes = [MvDepartment] } };

        var ex = Assert.Throws<ScopingCriterionFailedException>(() => _scoping.IsMvoInScopeForExportRule(mvo, rule));

        Assert.That(ex!.Message, Does.Contain("mv[\"Cost Centre\"]"));
    }

    #endregion

    #region Explanations and the can-fail predicate

    [Test]
    public void Explain_ExpressionCriterionThatFails_IsShownAsUndecidableRatherThanThrown()
    {
        var rule = ImportRule(Criterion("cs[\"department\"]", AttributeDataType.Number, SearchComparisonType.GreaterThan, intValue: 5));

        var explanation = _scoping.ExplainCsoForImportRule(Cso((Department, "Finance")), rule);

        Assert.That(explanation.Groups.Single().Criteria.Single().Outcome, Is.EqualTo(ScopingCriterionOutcome.Invalid));
    }

    [Test]
    public void CanFailToEvaluate_RuleWithAnExpressionCriterion_IsTrue()
    {
        var rule = ImportRule(Criterion("cs[\"department\"]", AttributeDataType.Text, SearchComparisonType.Equals, stringValue: "x"));

        Assert.That(ScopingEvaluationServer.CanFailToEvaluate(rule), Is.True);
    }

    #endregion

    private static SyncRuleScopingCriteria Criterion(string expression, AttributeDataType compareAs, SearchComparisonType op,
        string? stringValue = null, int? intValue = null) => new()
    {
        Expression = expression, CompareAs = compareAs, ComparisonType = op, StringValue = stringValue, IntValue = intValue
    };

    private static SyncRule ImportRule(SyncRuleScopingCriteria criterion)
    {
        var rule = new SyncRule { Name = "HR Import", Direction = SyncRuleDirection.Import };
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All, Criteria = [criterion] });
        return rule;
    }

    private static ConnectedSystemObject Cso(params (ConnectedSystemObjectTypeAttribute Attribute, string Value)[] values)
    {
        var type = new ConnectedSystemObjectType { Id = 1, Name = "person", Attributes = [Department, StartDate, Groups] };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = type, TypeId = type.Id };
        foreach (var (attribute, value) in values)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Attribute = attribute, AttributeId = attribute.Id, StringValue = value, ConnectedSystemObject = cso
            });
        return cso;
    }
}
