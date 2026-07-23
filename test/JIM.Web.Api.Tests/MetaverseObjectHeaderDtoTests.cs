// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for MetaverseObjectHeaderDto.FromHeader, covering the Attributes dictionary surfacing
/// every Attribute Data Type in its natural type rather than only Text values (issue #1046):
/// previously every non-Text attribute mapped StringValue and therefore always returned null.
/// </summary>
[TestFixture]
public class MetaverseObjectHeaderDtoTests
{
    private static MetaverseObjectHeader CreateHeader(params MetaverseObjectAttributeValue[] attributeValues) => new()
    {
        Id = Guid.NewGuid(),
        Created = DateTime.UtcNow,
        TypeId = 1,
        TypeName = "User",
        TypePluralName = "Users",
        Status = MetaverseObjectStatus.Normal,
        AttributeValues = attributeValues.ToList()
    };

    private static MetaverseObjectAttributeValue CreateAttributeValue(int attributeId, string name, AttributeDataType type) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attributeId,
        Attribute = new MetaverseAttribute
        {
            Id = attributeId,
            Name = name,
            Type = type,
            AttributePlurality = AttributePlurality.SingleValued
        }
    };

    [Test]
    public void FromHeader_WithTextAttribute_SurfacesStringValueUnchanged()
    {
        var av = CreateAttributeValue(10, "Job Title", AttributeDataType.Text);
        av.StringValue = "Engineer";

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(av));

        Assert.That(dto.Attributes["Job Title"], Is.EqualTo("Engineer"));
    }

    [Test]
    public void FromHeader_WithNumberAttribute_SurfacesIntValue()
    {
        var av = CreateAttributeValue(11, "Grade", AttributeDataType.Number);
        av.IntValue = 42;

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(av));

        Assert.That(dto.Attributes["Grade"], Is.EqualTo(42));
    }

    [Test]
    public void FromHeader_WithLongNumberAttribute_SurfacesLongValue()
    {
        var av = CreateAttributeValue(12, "Employee Number", AttributeDataType.LongNumber);
        av.LongValue = 9_999_999_999L;

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(av));

        Assert.That(dto.Attributes["Employee Number"], Is.EqualTo(9_999_999_999L));
    }

    [Test]
    public void FromHeader_WithDecimalAttribute_SurfacesDecimalValue()
    {
        // A high-precision value a double cannot represent exactly, proving the mapping
        // never routes the value through double/float.
        const decimal highPrecisionValue = 12345678901234567.89m;
        var av = CreateAttributeValue(13, "Annual Salary", AttributeDataType.Decimal);
        av.DecimalValue = highPrecisionValue;

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(av));

        Assert.That(dto.Attributes["Annual Salary"], Is.EqualTo(highPrecisionValue));
    }

    [Test]
    public void FromHeader_WithDateTimeAttribute_SurfacesDateTimeValue()
    {
        var startDate = new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc);
        var av = CreateAttributeValue(14, "Start Date", AttributeDataType.DateTime);
        av.DateTimeValue = startDate;

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(av));

        Assert.That(dto.Attributes["Start Date"], Is.EqualTo(startDate));
    }

    [Test]
    public void FromHeader_WithGuidAttribute_SurfacesGuidValue()
    {
        var guidValue = Guid.NewGuid();
        var av = CreateAttributeValue(15, "Object Identifier", AttributeDataType.Guid);
        av.GuidValue = guidValue;

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(av));

        Assert.That(dto.Attributes["Object Identifier"], Is.EqualTo(guidValue));
    }

    [Test]
    public void FromHeader_WithBooleanAttribute_SurfacesBoolValue()
    {
        var av = CreateAttributeValue(16, "Account Enabled", AttributeDataType.Boolean);
        av.BoolValue = true;

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(av));

        Assert.That(dto.Attributes["Account Enabled"], Is.EqualTo(true));
    }

    [Test]
    public void FromHeader_WithBooleanAttributeFalse_SurfacesFalseNotNull()
    {
        var av = CreateAttributeValue(16, "Account Enabled", AttributeDataType.Boolean);
        av.BoolValue = false;

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(av));

        Assert.That(dto.Attributes["Account Enabled"], Is.EqualTo(false));
    }

    [Test]
    public void FromHeader_WithReferenceAttribute_SurfacesReferencedObjectId()
    {
        // Consistent with MetaverseObjectAttributeValueDto, which represents a reference
        // by the referenced Metaverse Object's id (ReferenceValueId).
        var referencedObjectId = Guid.NewGuid();
        var av = CreateAttributeValue(17, "Manager", AttributeDataType.Reference);
        av.ReferenceValueId = referencedObjectId;

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(av));

        Assert.That(dto.Attributes["Manager"], Is.EqualTo(referencedObjectId));
    }

    [Test]
    public void FromHeader_WithBinaryAttribute_SurfacesByteValue()
    {
        var byteValue = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var av = CreateAttributeValue(18, "Photo", AttributeDataType.Binary);
        av.ByteValue = byteValue;

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(av));

        Assert.That(dto.Attributes["Photo"], Is.EqualTo(byteValue));
    }

    [Test]
    public void FromHeader_WithValuelessAttributeValue_SurfacesNull()
    {
        // An asserted-null row (or a row whose value fields are all null) surfaces as null,
        // matching the previous behaviour for Text.
        var av = CreateAttributeValue(19, "Job Title", AttributeDataType.Text);
        av.NullValue = true;

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(av));

        Assert.That(dto.Attributes.ContainsKey("Job Title"), Is.True);
        Assert.That(dto.Attributes["Job Title"], Is.Null);
    }

    [Test]
    public void FromHeader_WithMultiValuedAttribute_LastValueWins()
    {
        // Existing behaviour for multi-valued attributes: the dictionary holds a single
        // entry per attribute name and the last value encountered wins.
        var first = CreateAttributeValue(20, "Proxy Address", AttributeDataType.Text);
        first.StringValue = "smtp:first@example.com";
        var second = CreateAttributeValue(20, "Proxy Address", AttributeDataType.Text);
        second.StringValue = "smtp:second@example.com";

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(first, second));

        Assert.That(dto.Attributes["Proxy Address"], Is.EqualTo("smtp:second@example.com"));
    }

    [Test]
    public void FromHeader_WithDisplayNameAttribute_ExcludesItFromAttributes()
    {
        var av = CreateAttributeValue(21, Constants.BuiltInAttributes.DisplayName, AttributeDataType.Text);
        av.StringValue = "John Smith";

        var dto = MetaverseObjectHeaderDto.FromHeader(CreateHeader(av));

        Assert.That(dto.Attributes.ContainsKey(Constants.BuiltInAttributes.DisplayName), Is.False);
    }
}
