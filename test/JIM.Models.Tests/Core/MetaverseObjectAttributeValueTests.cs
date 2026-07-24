// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// Truth table for <see cref="MetaverseObjectAttributeValue.IsValuelessReferenceRow"/>, the shared
/// predicate deciding whether a reference attribute value row carries no information once its
/// ReferenceValueId is discounted (#1019). Must stay in step with the SQL predicates in
/// SyncRepository.MvoOperations.DeleteMetaverseObjectsAsync, MetaverseRepository.DeleteMetaverseObjectAsync
/// and the ghost-row cleanup migration.
/// </summary>
[TestFixture]
public class MetaverseObjectAttributeValueTests
{
    private static MetaverseObjectAttributeValue CreateBareRow() => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = 1
    };

    [Test]
    public void IsValuelessReferenceRow_AllPayloadNull_ReturnsTrue()
    {
        var row = CreateBareRow();

        Assert.That(row.IsValuelessReferenceRow(), Is.True);
    }

    [Test]
    public void IsValuelessReferenceRow_ReferenceValueIdSet_StillReturnsTrue()
    {
        // The predicate deliberately ignores ReferenceValueId; callers pair it with their own
        // "references a deleted object" membership test.
        var row = CreateBareRow();
        row.ReferenceValueId = Guid.NewGuid();

        Assert.That(row.IsValuelessReferenceRow(), Is.True);
    }

    [Test]
    public void IsValuelessReferenceRow_WithStringValue_ReturnsFalse()
    {
        var row = CreateBareRow();
        row.StringValue = "value";

        Assert.That(row.IsValuelessReferenceRow(), Is.False);
    }

    [Test]
    public void IsValuelessReferenceRow_WithDateTimeValue_ReturnsFalse()
    {
        var row = CreateBareRow();
        row.DateTimeValue = DateTime.UtcNow;

        Assert.That(row.IsValuelessReferenceRow(), Is.False);
    }

    [Test]
    public void IsValuelessReferenceRow_WithIntValue_ReturnsFalse()
    {
        var row = CreateBareRow();
        row.IntValue = 1;

        Assert.That(row.IsValuelessReferenceRow(), Is.False);
    }

    [Test]
    public void IsValuelessReferenceRow_WithLongValue_ReturnsFalse()
    {
        var row = CreateBareRow();
        row.LongValue = 1L;

        Assert.That(row.IsValuelessReferenceRow(), Is.False);
    }

    [Test]
    public void IsValuelessReferenceRow_WithDecimalValue_ReturnsFalse()
    {
        var row = CreateBareRow();
        row.DecimalValue = 1.5m;

        Assert.That(row.IsValuelessReferenceRow(), Is.False);
    }

    [Test]
    public void IsValuelessReferenceRow_WithByteValue_ReturnsFalse()
    {
        var row = CreateBareRow();
        row.ByteValue = [0x01];

        Assert.That(row.IsValuelessReferenceRow(), Is.False);
    }

    [Test]
    public void IsValuelessReferenceRow_WithGuidValue_ReturnsFalse()
    {
        var row = CreateBareRow();
        row.GuidValue = Guid.NewGuid();

        Assert.That(row.IsValuelessReferenceRow(), Is.False);
    }

    [Test]
    public void IsValuelessReferenceRow_WithBoolValue_ReturnsFalse()
    {
        var row = CreateBareRow();
        row.BoolValue = false;

        Assert.That(row.IsValuelessReferenceRow(), Is.False);
    }

    [Test]
    public void IsValuelessReferenceRow_WithUnresolvedReferenceValueId_ReturnsFalse()
    {
        var row = CreateBareRow();
        row.UnresolvedReferenceValueId = Guid.NewGuid();

        Assert.That(row.IsValuelessReferenceRow(), Is.False);
    }

    [Test]
    public void IsValuelessReferenceRow_WithUnresolvedReferenceValueNavigationOnly_ReturnsFalse()
    {
        // A staged unresolved reference may exist as a navigation before the FK scalar is fixed up.
        var row = CreateBareRow();
        row.UnresolvedReferenceValue = new ConnectedSystemObject { Id = Guid.NewGuid() };

        Assert.That(row.IsValuelessReferenceRow(), Is.False);
    }

    [Test]
    public void IsValuelessReferenceRow_AssertedNullMarkerRow_ReturnsFalse()
    {
        // An asserted-null marker row positively asserts "no value" and must survive deletion clean-up.
        var row = CreateBareRow();
        row.NullValue = true;

        Assert.That(row.IsValuelessReferenceRow(), Is.False);
    }

    [Test]
    public void ToString_WithDecimalValue_RendersInvariantCulture()
    {
        // A comma-decimal culture must not leak into the rendered value ("1234,56").
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var row = CreateBareRow();
            row.DecimalValue = 1234.56m;

            Assert.That(row.ToString(), Is.EqualTo("1234.56"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void ToString_WithDecimalValue_PreservesStoredScale()
    {
        // Display paths intentionally preserve the stored scale (unlike canonical keys, which use G29).
        var row = CreateBareRow();
        row.DecimalValue = 5.00m;

        Assert.That(row.ToString(), Is.EqualTo("5.00"));
    }

    [Test]
    public void ToString_WithLongAndDecimalValues_RendersLongValueFirst()
    {
        // The DecimalValue branch sits immediately after the LongValue branch.
        var row = CreateBareRow();
        row.LongValue = 42L;
        row.DecimalValue = 1.5m;

        Assert.That(row.ToString(), Is.EqualTo("42"));
    }
}
