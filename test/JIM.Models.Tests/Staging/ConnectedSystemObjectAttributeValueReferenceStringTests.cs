// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using NUnit.Framework;
using System;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// Tests for <see cref="ConnectedSystemObjectAttributeValue.ToReferenceValueString"/>: the one string
/// form every reference-resolution site (export execution's deferred pass, export evaluation's
/// reference recall) writes into a target system. Introduced for #1398, where two hand-rolled
/// coalesces omitted the LongValue and DecimalValue slots and a resolved reference reached the
/// connector carrying no anchor value.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectAttributeValueReferenceStringTests
{
    [Test]
    public void ToReferenceValueString_StringAnchor_PreservesCase()
    {
        var value = new ConnectedSystemObjectAttributeValue { StringValue = "CN=Ada Ashcroft,OU=Corp" };

        Assert.That(value.ToReferenceValueString(), Is.EqualTo("CN=Ada Ashcroft,OU=Corp"),
            "The target system receives the anchor as stored; this is not a lookup key, so no lowercasing.");
    }

    [Test]
    public void ToReferenceValueString_GuidAnchor_ReturnsTheGuidString()
    {
        var guid = Guid.NewGuid();
        var value = new ConnectedSystemObjectAttributeValue { GuidValue = guid };

        Assert.That(value.ToReferenceValueString(), Is.EqualTo(guid.ToString()));
    }

    [Test]
    public void ToReferenceValueString_NumberAnchor_ReturnsTheIntString()
    {
        var value = new ConnectedSystemObjectAttributeValue { IntValue = 1000039 };

        Assert.That(value.ToReferenceValueString(), Is.EqualTo("1000039"));
    }

    [Test]
    public void ToReferenceValueString_LongNumberAnchor_ReturnsTheLongString()
    {
        var value = new ConnectedSystemObjectAttributeValue { LongValue = 5000000123L };

        Assert.That(value.ToReferenceValueString(), Is.EqualTo("5000000123"));
    }

    [Test]
    public void ToReferenceValueString_DecimalAnchor_ReturnsTheCanonicalForm()
    {
        var value = new ConnectedSystemObjectAttributeValue { DecimalValue = 4200.00m };

        Assert.That(value.ToReferenceValueString(), Is.EqualTo("4200"),
            "Scale is representation, not identity (#1283): 4200.00 and 4200 must write the same anchor.");
    }

    [Test]
    public void ToReferenceValueString_NoAnchorCapableSlotHoldsAValue_ReturnsNull()
    {
        // BoolValue, DateTimeValue and ByteValue are not anchor-capable; a value carrying only those
        // must still read as "no anchor", so callers defer rather than write a nonsense reference.
        var value = new ConnectedSystemObjectAttributeValue { BoolValue = true, DateTimeValue = DateTime.UtcNow };

        Assert.That(value.ToReferenceValueString(), Is.Null);
    }
}
