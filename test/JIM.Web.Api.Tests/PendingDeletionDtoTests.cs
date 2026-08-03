// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using JIM.Models.Core;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for PendingDeletionDto mapping, including the nested object type shape (issue #813).
/// </summary>
[TestFixture]
public class PendingDeletionDtoTests
{
    [Test]
    public void FromEntity_MapsObjectTypeAsNestedType()
    {
        // Arrange
        var objectType = new MetaverseObjectType { Id = 7, Name = "User" };
        var entity = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            CachedDisplayName = "Alice",
            Type = objectType,
            LastConnectorDisconnectedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ConnectedSystemObjects = new List<JIM.Models.Staging.ConnectedSystemObject>()
        };

        // Act
        var dto = PendingDeletionDto.FromEntity(entity);

        // Assert - object type is nested to match the single-object response shape.
        Assert.That(dto.Type, Is.Not.Null);
        Assert.That(dto.Type!.Id, Is.EqualTo(7));
        Assert.That(dto.Type.Name, Is.EqualTo("User"));
    }

    [Test]
    public void FromEntity_WithNullType_FallsBackToUnknown()
    {
        // Arrange
        var entity = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            CachedDisplayName = "Bob",
            Type = null!,
            LastConnectorDisconnectedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ConnectedSystemObjects = new List<JIM.Models.Staging.ConnectedSystemObject>()
        };

        // Act
        var dto = PendingDeletionDto.FromEntity(entity);

        // Assert - preserves the previous flat-field fallback semantics.
        Assert.That(dto.Type, Is.Not.Null);
        Assert.That(dto.Type!.Id, Is.EqualTo(0));
        Assert.That(dto.Type.Name, Is.EqualTo("Unknown"));
    }

    [Test]
    public void FromEntity_MapsDeletionTriggeringSystemFields()
    {
        // The portal's Pending Deletions page shows what triggered each scheduled deletion (#119);
        // the REST surface must expose the same read (surface parity).
        var entity = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            CachedDisplayName = "Alice",
            Type = new MetaverseObjectType { Id = 7, Name = "User" },
            LastConnectorDisconnectedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ConnectedSystemObjects = new List<JIM.Models.Staging.ConnectedSystemObject>(),
            DeletionTriggeredBySystemId = 3,
            DeletionTriggeredBySystemName = "HR (Workday)"
        };

        var dto = PendingDeletionDto.FromEntity(entity);

        Assert.That(dto.DeletionTriggeredBySystemId, Is.EqualTo(3));
        Assert.That(dto.DeletionTriggeredBySystemName, Is.EqualTo("HR (Workday)"));
    }

    [Test]
    public void FromEntity_NoTriggeringSystemRecorded_MapsNulls()
    {
        // Rows marked before trigger recording existed carry no triggering system.
        var entity = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            CachedDisplayName = "Bob",
            Type = new MetaverseObjectType { Id = 7, Name = "User" },
            LastConnectorDisconnectedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ConnectedSystemObjects = new List<JIM.Models.Staging.ConnectedSystemObject>()
        };

        var dto = PendingDeletionDto.FromEntity(entity);

        Assert.That(dto.DeletionTriggeredBySystemId, Is.Null);
        Assert.That(dto.DeletionTriggeredBySystemName, Is.Null);
    }
}
