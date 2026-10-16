// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

/// <summary>
/// The shape of what an Object Matching Rule compares (#2052): exactly one source, holding exactly one of a
/// Connected System attribute or an expression, compared with a Metaverse attribute of a type an equality lookup can
/// compare. The matching engine fails a rule of any other shape for every object it meets, so the shape is refused
/// when the rule is saved rather than discovered on the first synchronisation.
/// </summary>
[TestFixture]
public class ObjectMatchingRuleTests
{
    [Test]
    public void DescribeInvalidity_TwoSources_SaysARuleTakesOneSource()
    {
        var rule = SimpleModeRule();
        rule.Sources.Add(new ObjectMatchingRuleSource { Order = 1, ConnectedSystemAttributeId = 102 });

        Assert.That(rule.DescribeInvalidity(), Does.Contain("exactly one source").And.Contain("2"));
    }

    [Test]
    public void DescribeInvalidity_SourceWithAnAttributeAndAnExpression_SaysOneOrTheOther()
    {
        var rule = SimpleModeRule();
        rule.Sources[0].Expression = "cs[\"employeeId\"]";

        Assert.That(rule.DescribeInvalidity(), Does.Contain("either").And.Contain("not both"));
    }

    [Test]
    public void DescribeInvalidity_SourceWithNeitherAnAttributeNorAnExpression_SaysThereIsNothingToMatchOn()
    {
        var rule = SimpleModeRule();
        rule.Sources[0].ConnectedSystemAttributeId = null;

        Assert.That(rule.DescribeInvalidity(), Does.Contain("nothing to match on"));
    }

    [Test]
    public void DescribeInvalidity_SourceWithAWhitespaceExpressionAndNoAttribute_SaysThereIsNothingToMatchOn()
    {
        var rule = SimpleModeRule();
        rule.Sources[0].ConnectedSystemAttributeId = null;
        rule.Sources[0].Expression = "   ";

        Assert.That(rule.DescribeInvalidity(), Does.Contain("nothing to match on"));
    }

    [Test]
    public void DescribeInvalidity_AttributeSourceNamedOnlyByItsId_IsWorkable()
    {
        // The REST API and the configuration restore build sources from ids alone; the navigation is a convenience
        // of whichever caller happened to load it.
        Assert.That(SimpleModeRule().DescribeInvalidity(), Is.Null);
    }

    [Test]
    public void DescribeInvalidity_ExpressionSource_IsWorkable()
    {
        var rule = SimpleModeRule();
        rule.Sources[0].ConnectedSystemAttributeId = null;
        rule.Sources[0].Expression = "Trim(cs[\"employeeId\"])";

        Assert.That(rule.DescribeInvalidity(), Is.Null);
    }

    [Test]
    public void DescribeComparisonInvalidity_RuleWithNoOwner_IgnoresOwnershipAndReportsTheComparisonOnly()
    {
        // A rule being added to a Synchronisation Rule in the portal has no owner until the Synchronisation Rule is
        // saved; its comparison is checked all the same.
        var rule = SimpleModeRule();
        rule.ConnectedSystemObjectTypeId = null;
        rule.ConnectedSystemObjectType = null;
        rule.MetaverseObjectTypeId = null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rule.DescribeComparisonInvalidity(), Is.Null);
            Assert.That(rule.DescribeInvalidity(), Is.Not.Null, "ownership is still DescribeInvalidity's to check");
        }
    }

    [TestCase(AttributeDataType.Text, true)]
    [TestCase(AttributeDataType.Number, true)]
    [TestCase(AttributeDataType.LongNumber, true)]
    [TestCase(AttributeDataType.Decimal, true)]
    [TestCase(AttributeDataType.Guid, true)]
    [TestCase(AttributeDataType.Boolean, false)]
    [TestCase(AttributeDataType.DateTime, false)]
    [TestCase(AttributeDataType.Binary, false)]
    [TestCase(AttributeDataType.Reference, false)]
    [TestCase(AttributeDataType.NotSet, false)]
    public void IsMatchableType_EachType_IsMatchableOnlyWhereAnEqualityLookupCanCompareIt(AttributeDataType type, bool matchable)
    {
        Assert.That(ObjectMatchingRule.IsMatchableType(type), Is.EqualTo(matchable));
    }

    [Test]
    public void DescribeComparison_AttributeSource_NamesTheMetaverseAttributeAndTheConnectedSystemAttribute()
    {
        var rule = new ObjectMatchingRule
        {
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 201, Name = "Employee ID" },
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "employeeId" } }]
        };

        Assert.That(rule.DescribeComparison(), Is.EqualTo("Employee ID equals employeeId"));
    }

    [Test]
    public void DescribeComparison_ExpressionSource_NamesTheMetaverseAttributeAndTheExpression()
    {
        var rule = new ObjectMatchingRule
        {
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 201, Name = "Employee ID" },
            Sources = [new ObjectMatchingRuleSource { Order = 0, Expression = "\"E\" + Trim(cs[\"employeeId\"])" }]
        };

        Assert.That(rule.DescribeComparison(), Is.EqualTo("Employee ID equals \"E\" + Trim(cs[\"employeeId\"])"));
    }

    [Test]
    public void DescribeComparison_AttributesNotLoaded_FallsBackToTheirIds()
    {
        var rule = SimpleModeRule();

        Assert.That(rule.DescribeComparison(), Is.EqualTo("201 equals 101"));
    }

    private static ObjectMatchingRule SimpleModeRule() => new()
    {
        ConnectedSystemObjectTypeId = 7,
        MetaverseObjectTypeId = 3,
        TargetMetaverseAttributeId = 201,
        Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = 101 }]
    };
}
