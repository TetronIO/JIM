// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for ConnectedSystemObjectAttributeValueDto mapping, covering the Attribute Data Type
/// gaps fixed for issue #1046: LongNumber, Decimal and Binary values were previously dropped
/// by the mapper, so the REST API returned them as absent.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectAttributeValueDtoTests
{
    private static ConnectedSystemObjectTypeAttribute CreateAttribute(int id, string name, AttributeDataType type) => new()
    {
        Id = id,
        Name = name,
        Type = type
    };

    [Test]
    public void FromEntity_WithLongValue_MapsLongValue()
    {
        // A value beyond int range, proving the mapping never routes through int.
        const long beyondIntRange = 9_999_999_999L;
        var entity = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = CreateAttribute(10, "employeeNumber", AttributeDataType.LongNumber),
            AttributeId = 10,
            LongValue = beyondIntRange
        };

        var dto = ConnectedSystemObjectAttributeValueDto.FromEntity(entity);

        Assert.That(dto.LongValue, Is.EqualTo(beyondIntRange));
    }

    [Test]
    public void FromEntity_WithDecimalValue_MapsDecimalValue()
    {
        // A high-precision value a double cannot represent exactly, proving the mapping
        // never routes the value through double/float.
        const decimal highPrecisionValue = 12345678901234567.89m;
        var entity = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = CreateAttribute(11, "annualSalary", AttributeDataType.Decimal),
            AttributeId = 11,
            DecimalValue = highPrecisionValue
        };

        var dto = ConnectedSystemObjectAttributeValueDto.FromEntity(entity);

        Assert.That(dto.DecimalValue, Is.EqualTo(highPrecisionValue));
    }

    [Test]
    public void FromEntity_WithByteValue_MapsByteValue()
    {
        var byteValue = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var entity = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = CreateAttribute(12, "thumbnailPhoto", AttributeDataType.Binary),
            AttributeId = 12,
            ByteValue = byteValue
        };

        var dto = ConnectedSystemObjectAttributeValueDto.FromEntity(entity);

        Assert.That(dto.ByteValue, Is.EqualTo(byteValue));
    }
}
