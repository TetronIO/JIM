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

    /// <summary>
    /// Attribute value change provenance (#1519): the export Synchronisation Rule that produced this
    /// value must survive the entity-to-DTO mapping so REST clients can see who contributed it.
    /// </summary>
    [Test]
    public void FromEntity_WithSyncRuleAttribution_MapsSyncRuleIdAndName()
    {
        var entity = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 11,
                Name = "mail",
                Type = AttributeDataType.Text
            },
            AttributeId = 11,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "jsmith@example.com",
            SyncRuleId = 42,
            SyncRuleName = "HR to AD - Users"
        };

        var dto = PendingExportAttributeValueChangeDto.FromEntity(entity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.SyncRuleId, Is.EqualTo(42));
            Assert.That(dto.SyncRuleName, Is.EqualTo("HR to AD - Users"));
        }
    }

    /// <summary>
    /// A value with no contributing rule (or a since-deleted rule) must map to null on both fields,
    /// not throw or default to zero.
    /// </summary>
    [Test]
    public void FromEntity_WithNoSyncRuleAttribution_MapsNullSyncRuleFields()
    {
        var entity = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 12,
                Name = "sAMAccountName",
                Type = AttributeDataType.Text
            },
            AttributeId = 12,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "jsmith"
        };

        var dto = PendingExportAttributeValueChangeDto.FromEntity(entity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.SyncRuleId, Is.Null);
            Assert.That(dto.SyncRuleName, Is.Null);
        }
    }
}
