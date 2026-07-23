// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using NUnit.Framework;

namespace JIM.Models.Tests;

/// <summary>
/// Reproduces #871: Metaverse Object change history narrowed Long Number values to int because
/// MetaverseObjectChangeAttributeValue had no long storage, so the audit record for values beyond
/// the 32-bit range (AD Large Integer attributes such as accountExpires or pwdLastSet) showed a
/// silently truncated, wrong number. Long Number values must be recorded at full fidelity.
/// </summary>
public class MetaverseObjectChangeLongNumberTests
{
    private static MetaverseObjectAttributeValue BuildLongNumberValue(long value)
    {
        var attribute = new MetaverseAttribute { Id = 1, Name = "accountExpires", Type = AttributeDataType.LongNumber };
        return new MetaverseObjectAttributeValue { Attribute = attribute, AttributeId = 1, LongValue = value };
    }

    [Test]
    public void AddAttributeValueChange_LongNumberBeyondIntRange_RecordsFullValue()
    {
        // Arrange: a value that cannot survive an (int) cast
        const long largeValue = 9999999999L;
        var change = new MetaverseObjectChange();

        // Act
        change.AddAttributeValueChange(BuildLongNumberValue(largeValue), ValueChangeType.Add);

        // Assert
        var recorded = change.AttributeChanges[0].ValueChanges[0];
        Assert.That(recorded.LongValue, Is.EqualTo(largeValue), "The change record must carry the full 64-bit value.");
        Assert.That(recorded.IntValue, Is.Null, "The value must not be narrowed into the int carrier.");
    }

    [Test]
    public void MetaverseObjectChangeAttributeValue_ToString_LongValue_RendersFullValue()
    {
        // Arrange
        var change = new MetaverseObjectChange();
        change.AddAttributeValueChange(BuildLongNumberValue(9999999999L), ValueChangeType.Add);

        // Act / Assert
        Assert.That(change.AttributeChanges[0].ValueChanges[0].ToString(), Is.EqualTo("9999999999"));
    }
}
