// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Staging.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

[TestFixture]
public class CsoChangeHistoryDtoTests
{
    [Test]
    public void ToDisplayString_WithDecimalValue_RendersInvariantCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var value = new CsoValueChangeDto { DecimalValue = 1234.56m };

            Assert.That(value.ToDisplayString(), Is.EqualTo("1234.56"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void ToDisplayString_WithDecimalValue_PreservesStoredScale()
    {
        var value = new CsoValueChangeDto { DecimalValue = 5.00m };

        Assert.That(value.ToDisplayString(), Is.EqualTo("5.00"));
    }

    [Test]
    public void ToDisplayString_WithLongAndDecimalValues_RendersLongValueFirst()
    {
        // The DecimalValue branch sits after the LongValue branch, before ByteValueLength.
        var value = new CsoValueChangeDto { LongValue = 42L, DecimalValue = 1.5m };

        Assert.That(value.ToDisplayString(), Is.EqualTo("42"));
    }

    [Test]
    public void ToDisplayString_WithDecimalAndByteValueLength_RendersDecimalValueFirst()
    {
        var value = new CsoValueChangeDto { DecimalValue = 1.5m, ByteValueLength = 16 };

        Assert.That(value.ToDisplayString(), Is.EqualTo("1.5"));
    }
}
