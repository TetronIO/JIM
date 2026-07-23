// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for MetaverseObjectAttributeValueDto mapping, covering the Attribute Priority provenance
/// and asserted-null exposure added for issue #931: ContributedBySyncRuleId/Name identify the
/// Synchronisation Rule that won attribute priority resolution, and NullValue distinguishes a
/// deliberate "Null is a value" marker row from a plain absence.
/// </summary>
[TestFixture]
public class MetaverseObjectDtoTests
{
    private static MetaverseAttribute CreateAttribute() => new()
    {
        Id = 10,
        Name = "Job Title",
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued
    };

    [Test]
    public void FromEntity_WithSyncRuleProvenance_MapsContributedBySyncRuleIdAndName()
    {
        var entity = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = CreateAttribute(),
            AttributeId = 10,
            StringValue = "Engineer",
            ContributedBySystemId = 3,
            ContributedBySystem = new ConnectedSystem { Id = 3, Name = "Primary Directory" },
            ContributedBySyncRuleId = 7,
            ContributedBySyncRule = new SyncRule { Id = 7, Name = "Primary Import Users" }
        };

        var dto = MetaverseObjectAttributeValueDto.FromEntity(entity);

        Assert.That(dto.ContributedBySyncRuleId, Is.EqualTo(7));
        Assert.That(dto.ContributedBySyncRuleName, Is.EqualTo("Primary Import Users"));
        Assert.That(dto.ContributedBySystemId, Is.EqualTo(3));
        Assert.That(dto.ContributedBySystemName, Is.EqualTo("Primary Directory"));
    }

    [Test]
    public void FromEntity_WithoutProvenance_MapsNullProvenanceFields()
    {
        var entity = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = CreateAttribute(),
            AttributeId = 10,
            StringValue = "Engineer"
        };

        var dto = MetaverseObjectAttributeValueDto.FromEntity(entity);

        Assert.That(dto.ContributedBySyncRuleId, Is.Null);
        Assert.That(dto.ContributedBySyncRuleName, Is.Null);
        Assert.That(dto.NullValue, Is.False);
    }

    [Test]
    public void FromEntity_WithSyncRuleIdButUnloadedNavigation_MapsIdAndNullName()
    {
        // The FK scalar survives even when the navigation was not eager-loaded (or the rule was
        // deleted and the FK is mid-set-null); the name must degrade to null, not throw.
        var entity = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = CreateAttribute(),
            AttributeId = 10,
            StringValue = "Engineer",
            ContributedBySyncRuleId = 7
        };

        var dto = MetaverseObjectAttributeValueDto.FromEntity(entity);

        Assert.That(dto.ContributedBySyncRuleId, Is.EqualTo(7));
        Assert.That(dto.ContributedBySyncRuleName, Is.Null);
    }

    [Test]
    public void FromEntity_AssertedNullMarkerRow_MapsNullValueTrueWithProvenance()
    {
        var entity = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = CreateAttribute(),
            AttributeId = 10,
            NullValue = true,
            ContributedBySystemId = 3,
            ContributedBySystem = new ConnectedSystem { Id = 3, Name = "Primary Directory" },
            ContributedBySyncRuleId = 7,
            ContributedBySyncRule = new SyncRule { Id = 7, Name = "Primary Import Users" }
        };

        var dto = MetaverseObjectAttributeValueDto.FromEntity(entity);

        Assert.That(dto.NullValue, Is.True);
        Assert.That(dto.StringValue, Is.Null);
        Assert.That(dto.ContributedBySyncRuleId, Is.EqualTo(7));
        Assert.That(dto.ContributedBySyncRuleName, Is.EqualTo("Primary Import Users"));
    }

    [Test]
    public void FromEntity_WithLongValue_MapsLongValue()
    {
        var entity = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new MetaverseAttribute
            {
                Id = 11,
                Name = "Employee Number",
                Type = AttributeDataType.LongNumber,
                AttributePlurality = AttributePlurality.SingleValued
            },
            AttributeId = 11,
            LongValue = 9_876_543_210L
        };

        var dto = MetaverseObjectAttributeValueDto.FromEntity(entity);

        Assert.That(dto.LongValue, Is.EqualTo(9_876_543_210L));
    }

    [Test]
    public void FromEntity_WithDecimalValue_MapsDecimalValue()
    {
        // A high-precision value that a double cannot represent exactly, proving the mapping
        // never routes the value through double/float.
        const decimal highPrecisionValue = 12345678901234567.89m;
        var entity = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new MetaverseAttribute
            {
                Id = 12,
                Name = "Annual Salary",
                Type = AttributeDataType.Decimal,
                AttributePlurality = AttributePlurality.SingleValued
            },
            AttributeId = 12,
            DecimalValue = highPrecisionValue
        };

        var dto = MetaverseObjectAttributeValueDto.FromEntity(entity);

        Assert.That(dto.DecimalValue, Is.EqualTo(highPrecisionValue));
    }
}
