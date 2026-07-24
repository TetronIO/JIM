// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Enums;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

[TestFixture]
public class ConnectedSystemObjectChangeAttributeValueTests
{
    [Test]
    public void Constructor_WithDecimalValue_SetsAllProperties()
    {
        var attributeChange = new ConnectedSystemObjectChangeAttribute();

        var value = new ConnectedSystemObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, 123.45m);

        Assert.That(value.DecimalValue, Is.EqualTo(123.45m));
        Assert.That(value.ConnectedSystemObjectChangeAttribute, Is.SameAs(attributeChange));
        Assert.That(value.ValueChangeType, Is.EqualTo(ValueChangeType.Add));
    }

    [Test]
    public void ToString_WithDecimalValue_RendersInvariantCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var value = new ConnectedSystemObjectChangeAttributeValue
            {
                DecimalValue = 123.45m
            };

            Assert.That(value.ToString(), Is.EqualTo("123.45"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void ToString_WithLongAndDecimalValues_RendersLongValueFirst()
    {
        // The DecimalValue branch sits immediately after the LongValue branch.
        var value = new ConnectedSystemObjectChangeAttributeValue
        {
            LongValue = 42L,
            DecimalValue = 1.5m
        };

        Assert.That(value.ToString(), Is.EqualTo("42"));
    }
}
