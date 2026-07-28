// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for PendingExportAttributeValueChangeDto mapping, covering the Binary value gap fixed
/// for issue #1046: ByteValue was previously dropped by the mapper, so a Binary attribute change
/// on a Pending Export appeared valueless via the REST API.
/// </summary>
[TestFixture]
public class PendingExportAttributeValueChangeDtoTests
{
    [Test]
    public void FromEntity_WithByteValue_MapsByteValue()
    {
        var byteValue = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var entity = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 10,
                Name = "thumbnailPhoto",
                Type = AttributeDataType.Binary
            },
            AttributeId = 10,
            ChangeType = PendingExportAttributeChangeType.Add,
            ByteValue = byteValue
        };

        var dto = PendingExportAttributeValueChangeDto.FromEntity(entity);

        Assert.That(dto.ByteValue, Is.EqualTo(byteValue));
    }
}
