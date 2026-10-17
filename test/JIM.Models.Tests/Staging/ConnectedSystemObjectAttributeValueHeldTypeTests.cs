// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;
using System;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// Tests for <see cref="ConnectedSystemObjectAttributeValue.HoldsValueOfType"/> and the two members built on it, for the
/// Reference cases (#2330). This is the one rule for whether a row holds a value of a type; the sync engine and the
/// provenance inspector use it too, so a Reference row is judged held or empty the same way everywhere.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectAttributeValueHeldTypeTests
{
    [Test]
    public void HoldsValueOfType_UnresolvedReferenceOnly_HoldsAReference()
    {
        var value = new ConnectedSystemObjectAttributeValue { UnresolvedReferenceValue = "CN=Ada Ashcroft,OU=Corp" };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.HoldsValueOfType(AttributeDataType.Reference), Is.True);
            Assert.That(value.HeldDataType(), Is.EqualTo(AttributeDataType.Reference));
            Assert.That(value.IsHeldUnderAnotherDataType(AttributeDataType.Text), Is.True,
                "an unresolved reference left behind when the attribute changed to Text is a held value, not no value");
        }
    }

    [Test]
    public void HoldsValueOfType_ReferenceValueIdOnly_HoldsAReference()
    {
        var value = new ConnectedSystemObjectAttributeValue { ReferenceValueId = Guid.NewGuid() };

        Assert.That(value.HoldsValueOfType(AttributeDataType.Reference), Is.True);
    }

    [Test]
    public void HoldsValueOfType_ReferenceNavigationOnly_HoldsAReference()
    {
        var value = new ConnectedSystemObjectAttributeValue { ReferenceValue = new ConnectedSystemObject { Id = Guid.NewGuid() } };

        Assert.That(value.HoldsValueOfType(AttributeDataType.Reference), Is.True);
    }

    [Test]
    public void HoldsValueOfType_TextOnly_DoesNotHoldAReference()
    {
        var value = new ConnectedSystemObjectAttributeValue { StringValue = "42" };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.HoldsValueOfType(AttributeDataType.Reference), Is.False,
                "a value held as Text after the attribute changed to Reference is not a reference");
            Assert.That(value.IsHeldUnderAnotherDataType(AttributeDataType.Reference), Is.True);
        }
    }

    [Test]
    public void HoldsValueOfType_NothingHeld_DoesNotHoldAReference()
    {
        var value = new ConnectedSystemObjectAttributeValue();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.HoldsValueOfType(AttributeDataType.Reference), Is.False);
            Assert.That(value.HeldDataType(), Is.Null);
            Assert.That(value.IsHeldUnderAnotherDataType(AttributeDataType.Reference), Is.False);
        }
    }
}
