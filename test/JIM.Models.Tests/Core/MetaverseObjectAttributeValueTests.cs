// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
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
}
