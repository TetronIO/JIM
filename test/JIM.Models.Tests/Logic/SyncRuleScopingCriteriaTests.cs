// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

[TestFixture]
public class SyncRuleScopingCriteriaTests
{
    #region Expression value sources (#2054)

    [Test]
    public void GetAttributeDataType_ExpressionCriterion_IsTheTypeItIsComparedAs()
    {
        var criterion = new SyncRuleScopingCriteria { Expression = "Lower(Trim(cs[\"department\"]))", CompareAs = AttributeDataType.Text };

        Assert.That(criterion.GetAttributeDataType(), Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetAttributeName_ExpressionCriterion_IsTheExpression()
    {
        var criterion = new SyncRuleScopingCriteria { Expression = "Lower(cs[\"department\"])", CompareAs = AttributeDataType.Text };

        Assert.That(criterion.GetAttributeName(), Is.EqualTo("Lower(cs[\"department\"])"));
    }

    [Test]
    public void IsExpression_IsTrueOnlyForAnExpressionCriterion()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new SyncRuleScopingCriteria { Expression = "cs[\"x\"]", CompareAs = AttributeDataType.Text }.IsExpression, Is.True);
            Assert.That(new SyncRuleScopingCriteria { ConnectedSystemAttributeId = 1 }.IsExpression, Is.False);
            Assert.That(new SyncRuleScopingCriteria { Expression = "  " }.IsExpression, Is.False);
        }
    }

    [TestCase(1, null, null, null, true, TestName = "An attribute alone")]
    [TestCase(null, 2, null, null, true, TestName = "A Metaverse attribute alone")]
    [TestCase(null, null, "cs[\"x\"]", AttributeDataType.Text, true, TestName = "An expression compared as a type")]
    [TestCase(null, null, "cs[\"x\"]", null, false, TestName = "An expression with no type to compare it as")]
    [TestCase(1, null, "cs[\"x\"]", AttributeDataType.Text, false, TestName = "An attribute and an expression")]
    [TestCase(1, null, null, AttributeDataType.Text, false, TestName = "An attribute with a compare-as type")]
    [TestCase(null, null, null, null, false, TestName = "Neither")]
    public void IsValid_HoldsExactlyOneLeftHandSide(int? csAttributeId, int? mvAttributeId, string? expression,
        AttributeDataType? compareAs, bool expected)
    {
        var criterion = new SyncRuleScopingCriteria
        {
            ConnectedSystemAttributeId = csAttributeId, MetaverseAttributeId = mvAttributeId, Expression = expression, CompareAs = compareAs
        };

        Assert.That(criterion.IsValid(), Is.EqualTo(expected));
    }

    [Test]
    public void ToString_ExpressionCriterion_RendersByTheTypeItIsComparedAs()
    {
        var criterion = new SyncRuleScopingCriteria { Expression = "cs[\"x\"]", CompareAs = AttributeDataType.Text, StringValue = "fin" };

        Assert.That(criterion.ToString(), Is.EqualTo("Text: fin"));
    }

    #endregion

    [Test]
    public void GetAttributeDataType_WithDecimalMetaverseAttribute_ReturnsDecimal()
    {
        var criterion = new SyncRuleScopingCriteria
        {
            MetaverseAttribute = new MetaverseAttribute { Id = 1, Name = "Salary", Type = AttributeDataType.Decimal }
        };

        Assert.That(criterion.GetAttributeDataType(), Is.EqualTo(AttributeDataType.Decimal));
    }

    [Test]
    public void GetAttributeDataType_WithDecimalConnectedSystemAttribute_ReturnsDecimal()
    {
        var criterion = new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "salary", Type = AttributeDataType.Decimal }
        };

        Assert.That(criterion.GetAttributeDataType(), Is.EqualTo(AttributeDataType.Decimal));
    }

    [Test]
    public void ToString_ForDecimalMetaverseAttribute_RendersDecimalValue()
    {
        var criterion = new SyncRuleScopingCriteria
        {
            MetaverseAttribute = new MetaverseAttribute { Id = 1, Name = "Salary", Type = AttributeDataType.Decimal },
            DecimalValue = 12345.678m
        };

        Assert.That(criterion.ToString(), Is.EqualTo("Decimal: 12345.678"));
    }

    [Test]
    public void ToString_ForDecimalConnectedSystemAttribute_RendersDecimalValue()
    {
        var criterion = new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "salary", Type = AttributeDataType.Decimal },
            DecimalValue = 0.5m
        };

        Assert.That(criterion.ToString(), Is.EqualTo("Decimal: 0.5"));
    }

    [Test]
    public void ToString_ForDecimal_UnderCommaDecimalCulture_RendersInvariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var criterion = new SyncRuleScopingCriteria
            {
                MetaverseAttribute = new MetaverseAttribute { Id = 1, Name = "Salary", Type = AttributeDataType.Decimal },
                DecimalValue = 12345.678m
            };

            Assert.That(criterion.ToString(), Is.EqualTo("Decimal: 12345.678"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [TestCase(AttributeDataType.DateTime, DateCriteriaValueMode.Relative, true)]
    [TestCase(AttributeDataType.DateTime, DateCriteriaValueMode.Absolute, false)]
    [TestCase(AttributeDataType.Text, DateCriteriaValueMode.Relative, false)]
    public void NeedsFullReconciliationPass_OnlyForAnExpressionComparedAsADateAgainstARelativeDate(AttributeDataType compareAs,
        DateCriteriaValueMode mode, bool expected)
    {
        // A value mode left at Relative after Compare As moved off Date Time compares nothing against the clock.
        var criterion = new SyncRuleScopingCriteria { Expression = "cs[\"startDate\"]", CompareAs = compareAs, ValueMode = mode };

        Assert.That(criterion.NeedsFullReconciliationPass, Is.EqualTo(expected));
    }
}
