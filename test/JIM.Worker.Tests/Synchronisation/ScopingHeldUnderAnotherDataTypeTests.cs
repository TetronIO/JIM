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
/// An import rule's scoping criterion reading a value held under another data type (#2329). A schema refresh can change
/// an attribute's type while Connected System Objects hold values of it, and those values stay where the old type put
/// them until a Full Import reads the attribute again (#2128). Every other reader treats such a value as held, not
/// missing (#2184, #2319, #2323); scoping cannot decide on it either way, so the criterion is undecidable: synchronisation
/// stops that object (<see cref="ScopingCriterionFailedException"/>, #2250) and an explanation names the held value.
/// </summary>
[TestFixture]
public class ScopingHeldUnderAnotherDataTypeTests
{
    private ScopingEvaluationServer _scoping = null!;

    private static readonly ConnectedSystemObjectTypeAttribute Department = new() { Id = 10, Name = "department", Type = AttributeDataType.Text };
    private static readonly ConnectedSystemObjectTypeAttribute Title = new() { Id = 11, Name = "title", Type = AttributeDataType.Text };

    [SetUp]
    public void SetUp() => _scoping = new ScopingEvaluationServer();

    #region Attribute criteria

    [TestCase(SearchComparisonType.HasValue, null)]
    [TestCase(SearchComparisonType.HasNoValue, null)]
    [TestCase(SearchComparisonType.Equals, "42")]
    [TestCase(SearchComparisonType.NotEquals, "42")]
    [TestCase(SearchComparisonType.StartsWith, "4")]
    [TestCase(SearchComparisonType.MatchesPattern, "^4")]
    [TestCase(SearchComparisonType.NotMatchesPattern, "^4")]
    public void IsCsoInScopeForImportRule_AttributeHeldOnlyUnderAnotherType_StopsTheObject(SearchComparisonType op, string? value)
    {
        var rule = ImportRule(AttributeCriterion(Department, op, value));

        var ex = Assert.Throws<ScopingCriterionFailedException>(() => _scoping.IsCsoInScopeForImportRule(Cso(HeldAsNumber(Department, 42)), rule));

        Assert.That(ex!.Message, Does.Contain("HR Import").And.Contain("department").And.Contain("Number").And.Contain("Full Import"));
    }

    [TestCase(SearchComparisonType.Equals, "Finance", true)]
    [TestCase(SearchComparisonType.NotEquals, "Finance", false)]
    [TestCase(SearchComparisonType.HasValue, null, true)]
    [TestCase(SearchComparisonType.HasNoValue, null, false)]
    public void IsCsoInScopeForImportRule_ValueOfTheCurrentType_ScopesAsBefore(SearchComparisonType op, string? value, bool expected)
    {
        var rule = ImportRule(AttributeCriterion(Department, op, value));

        Assert.That(_scoping.IsCsoInScopeForImportRule(Cso(Text(Department, "Finance")), rule), Is.EqualTo(expected));
    }

    [TestCase(SearchComparisonType.HasNoValue, null, true)]
    [TestCase(SearchComparisonType.HasValue, null, false)]
    [TestCase(SearchComparisonType.Equals, "Finance", false)]
    [TestCase(SearchComparisonType.NotEquals, "Finance", false)]
    public void IsCsoInScopeForImportRule_NoValueAtAll_IsStillNoValue(SearchComparisonType op, string? value, bool expected)
    {
        var rule = ImportRule(AttributeCriterion(Department, op, value));

        Assert.That(_scoping.IsCsoInScopeForImportRule(Cso(), rule), Is.EqualTo(expected));
    }

    [TestCase(SearchComparisonType.Equals, "Finance", true)]
    [TestCase(SearchComparisonType.NotEquals, "Finance", false)]
    [TestCase(SearchComparisonType.NotEquals, "HR", true)]
    [TestCase(SearchComparisonType.HasNoValue, null, false)]
    public void IsCsoInScopeForImportRule_OneValueOfTheCurrentTypeAndOneHeldUnderAnother_ReadsTheCurrentTypeValue(
        SearchComparisonType op, string? value, bool expected)
    {
        // Held only under another type is what makes it undecidable; a value of the current type is what is read, as
        // Attribute Flow reads it.
        var rule = ImportRule(AttributeCriterion(Department, op, value));

        Assert.That(_scoping.IsCsoInScopeForImportRule(Cso(HeldAsNumber(Department, 42), Text(Department, "Finance")), rule),
            Is.EqualTo(expected));
    }

    [Test]
    public void IsCsoInScopeForImportRule_HeldValueOfAnAttributeTheCriterionDoesNotRead_DoesNotAffectTheAnswer()
    {
        var rule = ImportRule(AttributeCriterion(Department, SearchComparisonType.Equals, "Finance"));

        Assert.That(_scoping.IsCsoInScopeForImportRule(Cso(Text(Department, "Finance"), HeldAsNumber(Title, 7)), rule), Is.True);
    }

    [Test]
    public void IsCsoInScopeForImportRule_HeldCriterionNotReachedBecauseAnEarlierGroupIsMet_IsInScope()
    {
        // Top-level groups are alternatives and synchronisation stops at the first met one, so a held value in a later
        // group is never read.
        var rule = ImportRule(AttributeCriterion(Title, SearchComparisonType.Equals, "Manager"));
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All, Criteria = [AttributeCriterion(Department, SearchComparisonType.HasValue, null)]
        });

        Assert.That(_scoping.IsCsoInScopeForImportRule(Cso(Text(Title, "Manager"), HeldAsNumber(Department, 42)), rule), Is.True);
    }

    #endregion

    #region Expression criteria

    [TestCase(SearchComparisonType.HasValue, null)]
    [TestCase(SearchComparisonType.HasNoValue, null)]
    [TestCase(SearchComparisonType.Equals, "42")]
    [TestCase(SearchComparisonType.NotEquals, "42")]
    [TestCase(SearchComparisonType.MatchesPattern, "^4")]
    public void IsCsoInScopeForImportRule_ExpressionInputHeldOnlyUnderAnotherType_StopsTheObject(SearchComparisonType op, string? value)
    {
        var rule = ImportRule(ExpressionCriterion("Trim(cs[\"department\"])", op, value));

        var ex = Assert.Throws<ScopingCriterionFailedException>(() => _scoping.IsCsoInScopeForImportRule(Cso(HeldAsNumber(Department, 42)), rule));

        Assert.That(ex!.Message, Does.Contain("cs[\"department\"]").And.Contain("Number").And.Contain("Full Import"));
    }

    [Test]
    public void IsCsoInScopeForImportRule_ExpressionDoesNotReadTheHeldAttribute_ScopesAsBefore()
    {
        var rule = ImportRule(ExpressionCriterion("Lower(cs[\"department\"])", SearchComparisonType.Equals, "finance"));

        Assert.That(_scoping.IsCsoInScopeForImportRule(Cso(Text(Department, "Finance"), HeldAsNumber(Title, 7)), rule), Is.True);
    }

    [Test]
    public void IsCsoInScopeForImportRule_ExpressionInputWithNoValueAtAll_IsStillNoValue()
    {
        var rule = ImportRule(ExpressionCriterion("Trim(cs[\"department\"])", SearchComparisonType.HasNoValue, null));

        Assert.That(_scoping.IsCsoInScopeForImportRule(Cso(), rule), Is.True);
    }

    #endregion

    #region Explanations

    [Test]
    public void ExplainCsoForImportRule_AttributeHeldOnlyUnderAnotherType_IsUndecidedAndNamesTheHeldValue()
    {
        var rule = ImportRule(AttributeCriterion(Department, SearchComparisonType.Equals, "Finance"));

        var explanation = _scoping.ExplainCsoForImportRule(Cso(HeldAsNumber(Department, 42)), rule);
        var criterion = explanation.Groups.Single().Criteria.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.Undetermined));
            Assert.That(criterion.Outcome, Is.EqualTo(ScopingCriterionOutcome.Invalid));
            Assert.That(criterion.FailureReason, Does.Contain("department").And.Contain("Number").And.Contain("Full Import"));
            Assert.That(criterion.ActualDescription, Does.Contain(criterion.FailureReason!));
            Assert.That(explanation.Hint, Does.Contain("department").And.Not.Contain("Invalid"),
                "the criterion is not misconfigured; the object's value is waiting for an import");
        }
    }

    [Test]
    public void ExplainCsoForImportRule_ExpressionInputHeldOnlyUnderAnotherType_IsUndecidedAndNamesTheHeldInput()
    {
        var rule = ImportRule(ExpressionCriterion("Trim(cs[\"department\"])", SearchComparisonType.HasValue, null));

        var explanation = _scoping.ExplainCsoForImportRule(Cso(HeldAsNumber(Department, 42)), rule);
        var criterion = explanation.Groups.Single().Criteria.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.Undetermined));
            Assert.That(criterion.Outcome, Is.EqualTo(ScopingCriterionOutcome.Invalid));
            Assert.That(criterion.FailureReason, Does.Contain("cs[\"department\"]").And.Contain("Number").And.Contain("Full Import"));
        }
    }

    [Test]
    public void ExplainCsoForImportRule_ValueOfTheCurrentType_IsExplainedAsBefore()
    {
        var rule = ImportRule(AttributeCriterion(Department, SearchComparisonType.Equals, "Finance"));

        var explanation = _scoping.ExplainCsoForImportRule(Cso(Text(Department, "Finance")), rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
            Assert.That(explanation.Groups.Single().Criteria.Single().FailureReason, Is.Null);
        }
    }

    #endregion

    #region Which rules can fail to evaluate

    [Test]
    public void CanFailToEvaluate_ImportRuleWithAnAttributeCriterion_IsTrue()
    {
        // Any object may hold the attribute under an earlier data type, so a Synchronised Deprovisioning survivor check
        // has to evaluate the rule rather than trust it to answer.
        Assert.That(ScopingEvaluationServer.CanFailToEvaluate(ImportRule(AttributeCriterion(Department, SearchComparisonType.HasValue, null))), Is.True);
    }

    [Test]
    public void CanFailToEvaluate_ExportRuleWithAnAttributeCriterion_IsFalse()
    {
        // A Metaverse Object never holds a value under another data type.
        var mvDepartment = new MetaverseAttribute { Id = 20, Name = "Department", Type = AttributeDataType.Text };
        var rule = new SyncRule { Name = "AD Export", Direction = SyncRuleDirection.Export };
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = [new SyncRuleScopingCriteria { MetaverseAttribute = mvDepartment, ComparisonType = SearchComparisonType.HasValue }]
        });

        Assert.That(ScopingEvaluationServer.CanFailToEvaluate(rule), Is.False);
    }

    #endregion

    private static SyncRuleScopingCriteria AttributeCriterion(ConnectedSystemObjectTypeAttribute attribute, SearchComparisonType op, string? value) => new()
    {
        ConnectedSystemAttribute = attribute,
        ConnectedSystemAttributeId = attribute.Id,
        ComparisonType = op,
        StringValue = value,
        CaseSensitive = false
    };

    private static SyncRuleScopingCriteria ExpressionCriterion(string expression, SearchComparisonType op, string? value) => new()
    {
        Expression = expression,
        CompareAs = AttributeDataType.Text,
        ComparisonType = op,
        StringValue = value,
        CaseSensitive = false
    };

    private static SyncRule ImportRule(SyncRuleScopingCriteria criterion)
    {
        var rule = new SyncRule { Name = "HR Import", Direction = SyncRuleDirection.Import };
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All, Criteria = [criterion] });
        return rule;
    }

    private static ConnectedSystemObjectAttributeValue Text(ConnectedSystemObjectTypeAttribute attribute, string value) =>
        new() { Attribute = attribute, AttributeId = attribute.Id, StringValue = value };

    /// <summary>A value imported while the attribute was a Number, which no import has read since it became Text.</summary>
    private static ConnectedSystemObjectAttributeValue HeldAsNumber(ConnectedSystemObjectTypeAttribute attribute, int value) =>
        new() { Attribute = attribute, AttributeId = attribute.Id, IntValue = value };

    private static ConnectedSystemObject Cso(params ConnectedSystemObjectAttributeValue[] values)
    {
        var type = new ConnectedSystemObjectType { Id = 1, Name = "person", Attributes = [Department, Title] };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = type, TypeId = type.Id };
        foreach (var value in values)
        {
            value.ConnectedSystemObject = cso;
            cso.AttributeValues.Add(value);
        }
        return cso;
    }
}
