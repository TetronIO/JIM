// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Worker.Tests.Workflows;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests that the import-hydration and reconciliation CSO queries do not populate the change
/// tracker (#917). The worker's DbContext uses TrackAll (identity fixup across overlapping
/// entity graphs), so these bulk read paths must opt out per query: at long-tail group scale
/// (~5k groups, ~1M membership attribute rows) tracked hydration graphs plus their
/// original-value snapshots account for gigabytes of the worker's peak memory, and each
/// successive hydration pays identity-resolution cost against an ever-growing tracker. The
/// save phase is raw SQL designed for untracked entities, so nothing needs the tracking.
/// </summary>
[TestFixture]
public class CsoHydrationTrackingTests : WorkflowTestBase
{
    private ConnectedSystem _system = null!;
    private ConnectedSystemObject _group = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _system = await CreateConnectedSystemAsync("Directory");

        var externalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var memberAttr = new ConnectedSystemObjectTypeAttribute { Name = "Member", Type = AttributeDataType.Reference, Selected = true };
        var csoType = await CreateCsoTypeAsync(_system.Id, "Group",
            new List<ConnectedSystemObjectTypeAttribute> { externalIdAttr, displayNameAttr, memberAttr });

        // A member CSO and a group CSO whose Member attribute references it: the shape the
        // delta membership import hydrates per changed group.
        var member = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = _system.Id,
            TypeId = csoType.Id,
            Created = DateTime.UtcNow
        };
        member.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = externalIdAttr.Id,
            GuidValue = Guid.NewGuid()
        });

        _group = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = _system.Id,
            TypeId = csoType.Id,
            Created = DateTime.UtcNow
        };
        _group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = externalIdAttr.Id,
            GuidValue = Guid.NewGuid()
        });
        _group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = memberAttr.Id,
            ReferenceValueId = member.Id,
            ReferenceValue = member
        });

        DbContext.ConnectedSystemObjects.AddRange(member, _group);
        await DbContext.SaveChangesAsync();

        // Start each test from a clean tracker so the assertions measure only what the
        // query under test attaches.
        DbContext.ChangeTracker.Clear();
    }

    [Test]
    public async Task GetConnectedSystemObjectsByIdsAsync_UnderTrackAllContext_LeavesChangeTrackerEmptyAsync()
    {
        var csos = await Repository.ConnectedSystems.GetConnectedSystemObjectsByIdsAsync(_system.Id, new[] { _group.Id });

        Assert.That(csos, Has.Count.EqualTo(1));
        Assert.That(csos[0].AttributeValues, Is.Not.Empty, "the hydrated graph must still include attribute values");
        Assert.That(csos[0].AttributeValues.Any(av => av.ReferenceValueId.HasValue), Is.True,
            "the reference FK scalar must survive hydration; the diff path matches on it via the SQL dictionary");
        Assert.That(DbContext.ChangeTracker.Entries().Count(), Is.Zero,
            "import hydration must not populate the change tracker; tracked graphs plus snapshots cost gigabytes at long-tail group scale");
    }

    [Test]
    public async Task GetConnectedSystemObjectsByIdsNoTrackingAsync_UnderTrackAllContext_LeavesChangeTrackerEmptyAsync()
    {
        var csos = await Repository.ConnectedSystems.GetConnectedSystemObjectsByIdsNoTrackingAsync(_system.Id, new[] { _group.Id });

        Assert.That(csos, Has.Count.EqualTo(1));
        Assert.That(csos[0].AttributeValues, Is.Not.Empty);
        Assert.That(DbContext.ChangeTracker.Entries().Count(), Is.Zero,
            "the method is named NoTracking and its callers (reconciliation, scope reconciler) rely on that being true");
    }
}
