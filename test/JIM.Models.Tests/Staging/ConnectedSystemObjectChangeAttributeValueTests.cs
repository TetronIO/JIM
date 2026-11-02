// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
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
    public void SetUnresolvedReferenceMark_ReferenceDnWrittenWithoutItsObjectsId_MarksTheValue()
    {
        var value = new ConnectedSystemObjectChangeAttributeValue { StringValue = "CN=Mo Manager,DC=glitterband,DC=local" };

        value.SetUnresolvedReferenceMark(AttributeDataType.Reference, referenceIdWritten: false);

        Assert.That(value.IsUnresolvedReference, Is.True);
    }

    [Test]
    public void SetUnresolvedReferenceMark_ReferenceDnWrittenWithItsObjectsId_LeavesTheValueUnmarked()
    {
        var value = new ConnectedSystemObjectChangeAttributeValue { StringValue = "CN=Mo Manager,DC=glitterband,DC=local" };

        value.SetUnresolvedReferenceMark(AttributeDataType.Reference, referenceIdWritten: true);

        Assert.That(value.IsUnresolvedReference, Is.False);
    }

    [Test]
    public void SetUnresolvedReferenceMark_ReferenceHoldingNoString_LeavesTheValueUnmarked()
    {
        var value = new ConnectedSystemObjectChangeAttributeValue();

        value.SetUnresolvedReferenceMark(AttributeDataType.Reference, referenceIdWritten: false);

        Assert.That(value.IsUnresolvedReference, Is.False, "with no string there is nothing to resolve it by");
    }

    [Test]
    public void SetUnresolvedReferenceMark_TextValueHoldingADn_LeavesTheValueUnmarked()
    {
        var value = new ConnectedSystemObjectChangeAttributeValue { StringValue = "CN=Mo Manager,DC=glitterband,DC=local" };

        value.SetUnresolvedReferenceMark(AttributeDataType.Text, referenceIdWritten: false);

        Assert.That(value.IsUnresolvedReference, Is.False, "a value of a Text attribute is never a reference");
    }

    [Test]
    public void SetUnresolvedReferenceMark_MarkedValueWrittenAgainWithItsObjectsId_ClearsTheMark()
    {
        var value = new ConnectedSystemObjectChangeAttributeValue { StringValue = "CN=Mo Manager,DC=glitterband,DC=local" };
        value.SetUnresolvedReferenceMark(AttributeDataType.Reference, referenceIdWritten: false);

        value.SetUnresolvedReferenceMark(AttributeDataType.Reference, referenceIdWritten: true);

        Assert.That(value.IsUnresolvedReference, Is.False);
    }

    /// <summary>
    /// A reference named by display text (the referenced object's display name, or the Metaverse Object id it was staged
    /// with) can never be matched to an object by identifier, so the change record reference fix-up must never read it
    /// (#2700). Whoever built the value says so; the text's shape does not, so even text shaped like a DN stays unmarked.
    /// </summary>
    [TestCase("Alice Smith (S8-0)")]
    [TestCase("1f2c9a52-7e0b-4a8e-9a0d-6d1f3b2c4e5f")]
    [TestCase("CN=Alice Smith,OU=People,DC=glitterband,DC=local")]
    public void SetUnresolvedReferenceMark_ReferenceNamingItsObjectByDisplayText_LeavesTheValueUnmarked(string displayText)
    {
        var value = new ConnectedSystemObjectChangeAttributeValue { StringValue = displayText, NamesReferencedObjectByDisplayText = true };

        value.SetUnresolvedReferenceMark(AttributeDataType.Reference, referenceIdWritten: false);

        Assert.That(value.IsUnresolvedReference, Is.False, "display text never resolves to an object by identifier");
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
