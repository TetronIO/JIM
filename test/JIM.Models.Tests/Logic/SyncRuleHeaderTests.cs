// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

[TestFixture]
public class SyncRuleHeaderTests
{
    [Test]
    public void FromEntity_WithPopulatedEntity_MapsAllHeaderProperties()
    {
        // Arrange
        var created = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var entity = new SyncRule
        {
            Id = 42,
            Name = "HR Inbound",
            Description = "Flows joiner data from HR into the Metaverse.",
            Created = created,
            ConnectedSystemId = 3,
            ConnectedSystem = new ConnectedSystem { Id = 3, Name = "HR System" },
            ConnectedSystemObjectTypeId = 7,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 7, Name = "Employee" },
            MetaverseObjectTypeId = 1,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" },
            Direction = SyncRuleDirection.Import,
            ProvisionToConnectedSystem = false,
            ProjectToMetaverse = true,
            Enabled = true,
            EnforceState = false
        };

        // Act
        var header = SyncRuleHeader.FromEntity(entity);

        // Assert
        Assert.That(header.Id, Is.EqualTo(42));
        Assert.That(header.Name, Is.EqualTo("HR Inbound"));
        Assert.That(header.Description, Is.EqualTo("Flows joiner data from HR into the Metaverse."));
        Assert.That(header.Created, Is.EqualTo(created));
        Assert.That(header.ConnectedSystemId, Is.EqualTo(3));
        Assert.That(header.ConnectedSystemName, Is.EqualTo("HR System"));
        Assert.That(header.ConnectedSystemObjectTypeId, Is.EqualTo(7));
        Assert.That(header.ConnectedSystemObjectTypeName, Is.EqualTo("Employee"));
        Assert.That(header.MetaverseObjectTypeId, Is.EqualTo(1));
        Assert.That(header.MetaverseObjectTypeName, Is.EqualTo("Person"));
        Assert.That(header.Direction, Is.EqualTo(SyncRuleDirection.Import));
        Assert.That(header.ProvisionToConnectedSystem, Is.False);
        Assert.That(header.ProjectToMetaverse, Is.True);
        Assert.That(header.Enabled, Is.True);
        Assert.That(header.EnforceState, Is.False);
    }

    [Test]
    public void FromEntity_WithoutDescription_MapsNullDescription()
    {
        // Arrange
        var entity = new SyncRule
        {
            Id = 1,
            Name = "Rule",
            Direction = SyncRuleDirection.Export
        };

        // Act
        var header = SyncRuleHeader.FromEntity(entity);

        // Assert
        Assert.That(header.Description, Is.Null);
    }
}
