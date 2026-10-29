// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the configuration drift status carried on the Connected System detail response: whether the
/// configuration has changed in a way that needs a Full Synchronisation to take effect.
///
/// The behaviour that matters to an API consumer is that a false <c>HasPendingChanges</c> is not by itself a claim
/// that the configuration is settled: it is also false when JIM cannot tell. Those cases must reach the wire
/// distinguishable, or a caller gating a run on the flag will silently skip the systems that most need attention.
/// </summary>
[TestFixture]
public class ConnectedSystemConfigurationDriftDtoTests
{
    [Test]
    public void FromEntity_NoDriftSupplied_OmitsConfigurationDrift()
    {
        // Create and update responses describe the write that just happened, not the system's readiness.
        var dto = ConnectedSystemDetailDto.FromEntity(CreateConnectedSystemEntity());

        Assert.That(dto.ConfigurationDrift, Is.Null);
    }

    [Test]
    public void FromEntity_PendingSyncAffectingChanges_MapsCountAndClass()
    {
        var lastSync = new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc);
        var mostRecent = lastSync.AddHours(4);
        var status = new ConfigurationDriftStatus
        {
            ConnectedSystemId = 1,
            HasPendingChanges = true,
            LastFullSynchronisation = lastSync,
            MostRecentChange = mostRecent,
            ChangeCount = 3,
            HighestChangeClass = ConfigurationChangeClass.SyncAffecting
        };

        var dto = ConnectedSystemDetailDto.FromEntity(CreateConnectedSystemEntity(), configurationDrift: status);

        Assert.That(dto.ConfigurationDrift, Is.Not.Null);
        var drift = dto.ConfigurationDrift!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drift.HasPendingChanges, Is.True);
            Assert.That(drift.IsDeterminable, Is.True);
            Assert.That(drift.ChangeCount, Is.EqualTo(3));
            Assert.That(drift.HighestChangeClass, Is.EqualTo(ConfigurationChangeClass.SyncAffecting));
            Assert.That(drift.LastFullSynchronisation, Is.EqualTo(lastSync));
            Assert.That(drift.MostRecentChange, Is.EqualTo(mostRecent));
        }
    }

    [Test]
    public void FromEntity_DestructiveChangePending_SurfacesDestructiveAsHighestClass()
    {
        // The class is what tells a caller apart a scoping tweak from a change that can cascade deletions.
        var status = new ConfigurationDriftStatus
        {
            ConnectedSystemId = 1,
            HasPendingChanges = true,
            LastFullSynchronisation = new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc),
            ChangeCount = 7,
            HighestChangeClass = ConfigurationChangeClass.Destructive
        };

        var dto = ConnectedSystemDetailDto.FromEntity(CreateConnectedSystemEntity(), configurationDrift: status);

        Assert.That(dto.ConfigurationDrift!.HighestChangeClass, Is.EqualTo(ConfigurationChangeClass.Destructive));
    }

    [Test]
    public void FromEntity_NeverFullySynchronised_IsNotDeterminableDespiteNoPendingChanges()
    {
        var status = new ConfigurationDriftStatus { ConnectedSystemId = 1, NeverFullySynchronised = true };

        var dto = ConnectedSystemDetailDto.FromEntity(CreateConnectedSystemEntity(), configurationDrift: status);

        var drift = dto.ConfigurationDrift!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drift.NeverFullySynchronised, Is.True);
            Assert.That(drift.HasPendingChanges, Is.False);
            Assert.That(drift.IsDeterminable, Is.False, "a caller must be able to tell this apart from a settled configuration");
            Assert.That(drift.LastFullSynchronisation, Is.Null);
        }
    }

    [Test]
    public void FromEntity_TrackingDisabled_IsNotDeterminableDespiteNoPendingChanges()
    {
        var status = new ConfigurationDriftStatus { ConnectedSystemId = 1, TrackingDisabled = true };

        var dto = ConnectedSystemDetailDto.FromEntity(CreateConnectedSystemEntity(), configurationDrift: status);

        var drift = dto.ConfigurationDrift!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drift.TrackingDisabled, Is.True);
            Assert.That(drift.HasPendingChanges, Is.False);
            Assert.That(drift.IsDeterminable, Is.False, "a caller must be able to tell this apart from a settled configuration");
        }
    }

    [Test]
    public void FromEntity_SettledConfiguration_IsDeterminableWithNoPendingChanges()
    {
        var status = new ConfigurationDriftStatus
        {
            ConnectedSystemId = 1,
            LastFullSynchronisation = new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc)
        };

        var dto = ConnectedSystemDetailDto.FromEntity(CreateConnectedSystemEntity(), configurationDrift: status);

        var drift = dto.ConfigurationDrift!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drift.HasPendingChanges, Is.False);
            Assert.That(drift.IsDeterminable, Is.True);
            Assert.That(drift.ChangeCount, Is.EqualTo(0));
            Assert.That(drift.HighestChangeClass, Is.EqualTo(ConfigurationChangeClass.NotClassified));
        }
    }

    [Test]
    public void FromEntity_ChangesAwaitingSynchronisation_ListsEachTypeWithItsChanges()
    {
        var activityId = Guid.NewGuid();
        var saved = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
        var status = new ConfigurationDriftStatus
        {
            ConnectedSystemId = 1,
            LastFullSynchronisation = saved.AddDays(-3),
            AwaitingSynchronisation =
            [
                new ObjectTypeAwaitingSynchronisation
                {
                    ConnectedSystemId = 1, ConnectedSystemObjectTypeId = 7, ObjectTypeName = "user", ChangeCount = 4,
                    EarliestRequestedAt = saved.AddHours(-5), ObjectCount = 12_500,
                    Changes =
                    [
                        new ConfigurationChangeSynchronisationSummary
                        {
                            Id = 1, Reason = "HR Inbound: Scope changed", RequestedAt = saved, ConfigurationChangeActivityId = activityId,
                            InitiatedByType = ActivityInitiatorType.User, InitiatedByName = "Alice Adams"
                        }
                    ]
                }
            ]
        };

        var drift = ConnectedSystemDetailDto.FromEntity(CreateConnectedSystemEntity(), configurationDrift: status).ConfigurationDrift!;

        var type = drift.AwaitingSynchronisation.Single();
        var change = type.Changes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(drift.HasPendingChanges, Is.False, "the changes awaiting synchronisation are not drift");
            Assert.That(type.ConnectedSystemObjectTypeId, Is.EqualTo(7));
            Assert.That(type.ObjectTypeName, Is.EqualTo("user"));
            Assert.That(type.ChangeCount, Is.EqualTo(4), "the true number, whatever the list holds");
            Assert.That(type.EarliestSavedAt, Is.EqualTo(saved.AddHours(-5)));
            Assert.That(type.ObjectCount, Is.EqualTo(12_500));
            Assert.That(change.Reason, Is.EqualTo("HR Inbound: Scope changed"));
            Assert.That(change.SavedAt, Is.EqualTo(saved));
            Assert.That(change.ConfigurationChangeActivityId, Is.EqualTo(activityId));
            Assert.That(change.InitiatedByName, Is.EqualTo("Alice Adams"));
            Assert.That(change.AppliedAt, Is.Null);
        }
    }

    [Test]
    public void FromEntity_NothingAwaitingSynchronisation_ListsNoTypes()
    {
        var status = new ConfigurationDriftStatus { ConnectedSystemId = 1, NeverFullySynchronised = true };

        var drift = ConnectedSystemDetailDto.FromEntity(CreateConnectedSystemEntity(), configurationDrift: status).ConfigurationDrift!;

        Assert.That(drift.AwaitingSynchronisation, Is.Empty);
    }

    private static ConnectedSystem CreateConnectedSystemEntity()
    {
        return new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Description = "Test Description",
            ConnectorDefinition = new ConnectorDefinition
            {
                Id = 1,
                Name = "Test Connector"
            },
            ObjectTypes = new List<ConnectedSystemObjectType>(),
            Objects = new List<ConnectedSystemObject>(),
            PendingExports = new List<PendingExport>(),
            SettingValues = new List<ConnectedSystemSettingValue>()
        };
    }
}
