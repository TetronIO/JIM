// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

[TestFixture]
public class AttributeDataTypeCapabilitiesTests
{
    [Test]
    public void SupportsMultiValued_EveryDataType_IsFalseOnlyForBoolean()
    {
        // Completeness: a data type added to the enum must be classified here deliberately, so the test walks every
        // member rather than a hand-picked list.
        foreach (var type in Enum.GetValues<AttributeDataType>())
        {
            var expected = type != AttributeDataType.Boolean;
            Assert.That(AttributeDataTypeCapabilities.SupportsMultiValued(type), Is.EqualTo(expected), $"{type}");
        }
    }

    [Test]
    public void SupportsMultiValued_Boolean_IsFalse()
    {
        Assert.That(AttributeDataTypeCapabilities.SupportsMultiValued(AttributeDataType.Boolean), Is.False);
    }

    [Test]
    public void IsPermitted_MultiValuedBoolean_IsFalse()
    {
        Assert.That(AttributeDataTypeCapabilities.IsPermitted(AttributeDataType.Boolean, AttributePlurality.MultiValued), Is.False);
    }

    [Test]
    public void IsPermitted_SingleValuedBooleanAndMultiValuedText_AreTrue()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AttributeDataTypeCapabilities.IsPermitted(AttributeDataType.Boolean, AttributePlurality.SingleValued), Is.True);
            Assert.That(AttributeDataTypeCapabilities.IsPermitted(AttributeDataType.Text, AttributePlurality.MultiValued), Is.True);
        }
    }

    [Test]
    public void Constrain_MultiValuedBoolean_IsSingleValuedAndOtherTypesKeepTheirPlurality()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AttributeDataTypeCapabilities.Constrain(AttributeDataType.Boolean, AttributePlurality.MultiValued), Is.EqualTo(AttributePlurality.SingleValued));
            Assert.That(AttributeDataTypeCapabilities.Constrain(AttributeDataType.DateTime, AttributePlurality.MultiValued), Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(AttributeDataTypeCapabilities.Constrain(AttributeDataType.Text, AttributePlurality.SingleValued), Is.EqualTo(AttributePlurality.SingleValued));
        }
    }

    [Test]
    public void BuiltInMetaverseSchema_EveryAttribute_ObeysTheCapabilityRule()
    {
        var offenders = BuiltInMetaverseSchema.Attributes
            .Where(a => !AttributeDataTypeCapabilities.IsPermitted(a.Type, a.Plurality))
            .Select(a => a.Name)
            .ToList();

        Assert.That(offenders, Is.Empty);
    }
}
