// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Search;
using NUnit.Framework;

namespace JIM.Models.Tests.Search;

[TestFixture]
public class PredefinedSearchCriteriaTests
{
    private static PredefinedSearchCriteria CriterionFor(AttributeDataType type) => new()
    {
        MetaverseAttribute = new MetaverseAttribute { Id = 1, Name = "Attr", Type = type }
    };

    [Test]
    public void GetAttributeDataType_WithAttribute_ReturnsItsType()
    {
        var criterion = CriterionFor(AttributeDataType.DateTime);
        Assert.That(criterion.GetAttributeDataType(), Is.EqualTo(AttributeDataType.DateTime));
    }

    [Test]
    public void GetAttributeName_WithAttribute_ReturnsItsName()
    {
        var criterion = CriterionFor(AttributeDataType.Text);
        Assert.That(criterion.GetAttributeName(), Is.EqualTo("Attr"));
    }

    [Test]
    public void ToString_WithNoAttribute_ReportsNoAttributeSet()
    {
        var criterion = new PredefinedSearchCriteria();
        Assert.That(criterion.ToString(), Is.EqualTo("No attribute set"));
    }

    [Test]
    public void ToString_ForText_RendersStringValue()
    {
        var criterion = CriterionFor(AttributeDataType.Text);
        criterion.StringValue = "Finance";
        Assert.That(criterion.ToString(), Is.EqualTo("Text: Finance"));
    }

    [Test]
    public void ToString_ForNumber_RendersIntValue()
    {
        var criterion = CriterionFor(AttributeDataType.Number);
        criterion.IntValue = 42;
        Assert.That(criterion.ToString(), Is.EqualTo("Number: 42"));
    }

    [Test]
    public void ToString_ForLongNumber_RendersLongValue()
    {
        var criterion = CriterionFor(AttributeDataType.LongNumber);
        criterion.LongValue = 9000000000;
        Assert.That(criterion.ToString(), Is.EqualTo("LongNumber: 9000000000"));
    }

    [Test]
    public void ToString_ForDecimal_RendersDecimalValue()
    {
        var criterion = CriterionFor(AttributeDataType.Decimal);
        criterion.DecimalValue = 12345.678m;
        Assert.That(criterion.ToString(), Is.EqualTo("Decimal: 12345.678"));
    }

    [Test]
    public void ToString_ForDecimal_UnderCommaDecimalCulture_RendersInvariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var criterion = CriterionFor(AttributeDataType.Decimal);
            criterion.DecimalValue = 12345.678m;
            Assert.That(criterion.ToString(), Is.EqualTo("Decimal: 12345.678"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void ToString_ForBoolean_RendersBoolValue()
    {
        var criterion = CriterionFor(AttributeDataType.Boolean);
        criterion.BoolValue = true;
        Assert.That(criterion.ToString(), Is.EqualTo("Boolean: True"));
    }

    [Test]
    public void ToString_ForGuid_RendersGuidValue()
    {
        var criterion = CriterionFor(AttributeDataType.Guid);
        var id = Guid.NewGuid();
        criterion.GuidValue = id;
        Assert.That(criterion.ToString(), Is.EqualTo("Guid: " + id));
    }

    [Test]
    public void CaseSensitive_DefaultsToTrue()
    {
        Assert.That(new PredefinedSearchCriteria().CaseSensitive, Is.True);
    }
}
