// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Transactional;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// #2153 S5: a Pending Export value is shown as what it is, whatever its type. Three pages each carried a copy of this
/// that fell through to "[null]" for a true or false value, a Long Number, a Decimal or a GUID.
/// </summary>
[TestFixture]
public class PendingExportValueDisplayTests
{
    [Test]
    public void Text_BooleanLongNumberDecimalAndGuid_ShowTheValueNotNull()
    {
        var guid = Guid.NewGuid();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingExportValueDisplay.Text(new PendingExportAttributeValueChange { BoolValue = false }), Is.EqualTo("False"));
            Assert.That(PendingExportValueDisplay.Text(new PendingExportAttributeValueChange { LongValue = 9_000_000_000 }), Is.EqualTo("9000000000"));
            Assert.That(PendingExportValueDisplay.Text(new PendingExportAttributeValueChange { DecimalValue = 1.50m }), Is.EqualTo("1.5"));
            Assert.That(PendingExportValueDisplay.Text(new PendingExportAttributeValueChange { GuidValue = guid }), Is.EqualTo(guid.ToString()));
        }
    }

    [Test]
    public void Text_DateWithAFraction_ShowsTheFraction()
    {
        var change = new PendingExportAttributeValueChange { DateTimeValue = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560) };

        Assert.That(PendingExportValueDisplay.Text(change), Does.EndWith(".123456"));
    }

    [Test]
    public void Text_ChangeHoldingNothing_IsNull()
    {
        Assert.That(PendingExportValueDisplay.Text(new PendingExportAttributeValueChange()), Is.Null,
            "No value; the page shows its empty-value marker.");
    }
}
