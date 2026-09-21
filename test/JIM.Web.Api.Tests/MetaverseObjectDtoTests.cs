// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
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

    [Test]
    public void FromEntity_WithByteValue_MapsByteValue()
    {
        var byteValue = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var entity = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = new MetaverseAttribute
            {
                Id = 13,
                Name = "Photo",
                Type = AttributeDataType.Binary,
                AttributePlurality = AttributePlurality.SingleValued
            },
            AttributeId = 13,
            ByteValue = byteValue
        };

        var dto = MetaverseObjectAttributeValueDto.FromEntity(entity);

        Assert.That(dto.ByteValue, Is.EqualTo(byteValue));
    }

    #region Connections mapping (#1519 parity)

    /// <summary>
    /// The detail response's joined-object rows carry the same derived data the portal's Connections
    /// tab shows: role, join type, State and the join dates. Before this, the rows named the Connected
    /// System and nothing about the join itself, so a script could see that an object was joined but
    /// not whether it was a source or a target, nor that its export had failed.
    /// </summary>
    [Test]
    public void FromEntity_WithConnections_MapsRoleJoinTypeStateAndDates()
    {
        var csoId = Guid.NewGuid();
        var joined = DateTime.UtcNow.AddDays(-30);
        var lastSynchronised = DateTime.UtcNow.AddHours(-2);
        var entity = CreateMetaverseObject(csoId, joined);
        var connections = new List<MetaverseObjectConnection>
        {
            new()
            {
                ConnectedSystemObjectId = csoId,
                ConnectedSystemId = 3,
                ConnectedSystemName = "Primary Directory",
                DisplayName = "CN=jsmith,OU=Users,DC=example,DC=com",
                ObjectTypeName = "user",
                JoinType = ConnectedSystemObjectJoinType.Joined,
                IsSource = true,
                IsTarget = true,
                State = ConnectedSystemObjectConnectionState.ExportFailed,
                PendingAttributeChangeCount = 2,
                LastSynchronised = lastSynchronised
            }
        };

        var dto = MetaverseObjectDto.FromEntity(entity, connections);

        Assert.That(dto.ConnectedSystemObjects.Count, Is.EqualTo(1));
        var row = dto.ConnectedSystemObjects[0];
        Assert.Multiple(() =>
        {
            Assert.That(row.Id, Is.EqualTo(csoId));
            Assert.That(row.ConnectedSystemId, Is.EqualTo(3));
            Assert.That(row.ConnectedSystemName, Is.EqualTo("Primary Directory"));
            Assert.That(row.ObjectTypeName, Is.EqualTo("user"));
            Assert.That(row.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined));
            Assert.That(row.IsSource, Is.True);
            Assert.That(row.IsTarget, Is.True);
            Assert.That(row.State, Is.EqualTo(ConnectedSystemObjectConnectionState.ExportFailed));
            Assert.That(row.PendingAttributeChangeCount, Is.EqualTo(2));
            Assert.That(row.LastSynchronised, Is.EqualTo(lastSynchronised));
            Assert.That(row.DateJoined, Is.EqualTo(joined));
            Assert.That(row.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
        });
    }

    /// <summary>
    /// The connections derivation names a row by its external id (the Connections tab's Object column);
    /// the detail response has always named it by the object's best-ranked name. Keep the richer name
    /// where the entity has one, so enriching the row does not regress what callers already read.
    /// </summary>
    [Test]
    public void FromEntity_WithConnections_KeepsTheEntitysBestRankedName()
    {
        var csoId = Guid.NewGuid();
        var entity = CreateMetaverseObject(csoId, DateTime.UtcNow.AddDays(-1));
        var connections = new List<MetaverseObjectConnection>
        {
            new()
            {
                ConnectedSystemObjectId = csoId,
                ConnectedSystemId = 3,
                ConnectedSystemName = "Primary Directory",
                DisplayName = "CN=jsmith,OU=Users,DC=example,DC=com",
                State = ConnectedSystemObjectConnectionState.InSync
            }
        };

        var dto = MetaverseObjectDto.FromEntity(entity, connections);

        Assert.That(dto.ConnectedSystemObjects[0].DisplayName, Is.EqualTo("John Smith"));
    }

    /// <summary>
    /// The connections read runs after the object read, so it is the fresher view of what is joined:
    /// a row it does not carry is not rendered from the staler entity graph.
    /// </summary>
    [Test]
    public void FromEntity_WithNoConnections_ReturnsNoJoinedObjectRows()
    {
        var entity = CreateMetaverseObject(Guid.NewGuid(), DateTime.UtcNow.AddDays(-1));

        var dto = MetaverseObjectDto.FromEntity(entity, []);

        Assert.That(dto.ConnectedSystemObjects, Is.Empty);
    }

    private static MetaverseObject CreateMetaverseObject(Guid connectedSystemObjectId, DateTime dateJoined)
    {
        var nameAttribute = new ConnectedSystemObjectTypeAttribute { Id = 55, Name = "displayName" };
        return new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "User" },
            AttributeValues = [],
            ConnectedSystemObjects =
            [
                new ConnectedSystemObject
                {
                    Id = connectedSystemObjectId,
                    ConnectedSystemId = 3,
                    ConnectedSystem = new ConnectedSystem { Id = 3, Name = "Primary Directory" },
                    Status = ConnectedSystemObjectStatus.Normal,
                    JoinType = ConnectedSystemObjectJoinType.Joined,
                    DateJoined = dateJoined,
                    AttributeValues =
                    [
                        new ConnectedSystemObjectAttributeValue { Attribute = nameAttribute, AttributeId = 55, StringValue = "John Smith" }
                    ]
                }
            ]
        };
    }

    #endregion
}
