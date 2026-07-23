// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

[TestFixture]
public class SyncRuleScopingCriteriaTests
{
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
}
