// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Globalization;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.Enums;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// Guards lossless Decimal change recording on <see cref="MetaverseObjectChange.AddAttributeValueChange"/>.
/// Unlike the LongNumber path (which narrows to int, tracked by #871), the Decimal path must record the
/// value exactly, with no narrowing or rounding of any kind.
/// </summary>
[TestFixture]
public class MetaverseObjectChangeTests
{
    private static MetaverseObjectAttributeValue CreateDecimalValue(decimal? value) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = new MetaverseAttribute { Id = 1, Name = "Salary", Type = AttributeDataType.Decimal },
        AttributeId = 1,
        DecimalValue = value
    };

    private static readonly decimal[] ExactRecordingCases =
    [
        decimal.MaxValue,                          // 79228162514264337593543950335
        1.234567890123456789012345678m,            // 28 significant digits
        -0.0000000000000000000000000001m           // negative, maximum scale
    ];

    [Test]
    public void AddAttributeValueChange_DecimalValues_RecordsExactValueWithNoNarrowing(
        [ValueSource(nameof(ExactRecordingCases))] decimal input)
    {
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(input);

        change.AddAttributeValueChange(value, ValueChangeType.Add);

        var recorded = change.AttributeChanges.Single().ValueChanges.Single();
        Assert.That(recorded.DecimalValue, Is.EqualTo(input));
        Assert.That(recorded.ValueChangeType, Is.EqualTo(ValueChangeType.Add));
    }

    [Test]
    public void AddAttributeValueChange_DecimalScalePreserved_RecordsStoredScale()
    {
        // decimal equality is scale-insensitive (5.00m == 5m), so also assert the stored scale survives.
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(5.00m);

        change.AddAttributeValueChange(value, ValueChangeType.Add);

        var recorded = change.AttributeChanges.Single().ValueChanges.Single();
        Assert.That(recorded.DecimalValue, Is.EqualTo(5.00m));
        Assert.That(recorded.DecimalValue!.Value.ToString(CultureInfo.InvariantCulture), Is.EqualTo("5.00"));
    }

    [Test]
    public void AddAttributeValueChange_DecimalAssertedNullMarker_RecordsNothing()
    {
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(null);
        value.NullValue = true;

        change.AddAttributeValueChange(value, ValueChangeType.Remove);

        Assert.That(change.AttributeChanges, Is.Empty);
    }

    [Test]
    public void AddAttributeValueChange_DecimalNullHolderNotAssertedNull_ThrowsInvalidOperationException()
    {
        // A Decimal-typed row with no value that is not an asserted-null marker is corrupt; the
        // default arm must fail fast rather than record nothing.
        var change = new MetaverseObjectChange();
        var value = CreateDecimalValue(null);

        Assert.That(
            () => change.AddAttributeValueChange(value, ValueChangeType.Add),
            Throws.InvalidOperationException);
    }

    [Test]
    public void MetaverseObjectChangeAttributeValue_DecimalConstructor_SetsAllProperties()
    {
        var attributeChange = new MetaverseObjectChangeAttribute { AttributeName = "Salary" };

        var value = new MetaverseObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, 123.45m);

        Assert.That(value.DecimalValue, Is.EqualTo(123.45m));
        Assert.That(value.MetaverseObjectChangeAttribute, Is.SameAs(attributeChange));
        Assert.That(value.ValueChangeType, Is.EqualTo(ValueChangeType.Add));
    }

    [Test]
    public void MetaverseObjectChangeAttributeValue_ToString_WithDecimalValue_RendersInvariantCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var attributeChange = new MetaverseObjectChangeAttribute { AttributeName = "Salary" };
            var value = new MetaverseObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, 123.45m);

            Assert.That(value.ToString(), Is.EqualTo("123.45"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
