// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

[TestFixture]
public class MvoChangeHistoryDtoTests
{
    [Test]
    public void ToDisplayString_WithDecimalValue_RendersInvariantCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var value = new MvoValueChangeDto { DecimalValue = 1234.56m };

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
        var value = new MvoValueChangeDto { DecimalValue = 5.00m };

        Assert.That(value.ToDisplayString(), Is.EqualTo("5.00"));
    }

    [Test]
    public void ToDisplayString_WithIntAndDecimalValues_RendersIntValueFirst()
    {
        // The DecimalValue branch sits after the IntValue branch (there is no LongValue member here, #871).
        var value = new MvoValueChangeDto { IntValue = 42, DecimalValue = 1.5m };

        Assert.That(value.ToDisplayString(), Is.EqualTo("42"));
    }

    [Test]
    public void ToDisplayString_WithDecimalAndByteValueLength_RendersDecimalValueFirst()
    {
        var value = new MvoValueChangeDto { DecimalValue = 1.5m, ByteValueLength = 16 };

        Assert.That(value.ToDisplayString(), Is.EqualTo("1.5"));
    }
}
