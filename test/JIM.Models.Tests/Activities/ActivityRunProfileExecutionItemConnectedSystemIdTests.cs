// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.History;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// Tests for <see cref="ActivityRunProfileExecutionItem.GetConnectedSystemId"/>: which Connected
/// System the item's record belongs to, after the record itself may have been deleted. The fallback
/// to the item-level change snapshot is only honest on record-side items; a synchronisation-side
/// item's change row describes a record the run created elsewhere (the provisioned stub in a target
/// system), and trusting it labelled a source record with its target's system (#1495).
/// </summary>
[TestFixture]
public class ActivityRunProfileExecutionItemConnectedSystemIdTests
{
    private const int RecordSystemId = 1;
    private const int StagingTargetSystemId = 2;

    /// <summary>
    /// An item and its Connected System Object as resolved from the id the item holds (#2669). The item keeps the id
    /// whether or not the object is still there, so a deleted object resolves from its deletion record, which also
    /// names the system; only a live object is the authority this reads.
    /// </summary>
    private static (ActivityRunProfileExecutionItem Item, ResolvedObjectReference Record) BuildItem(
        ObjectChangeType changeType, bool csoDeleted, int? changeRowSystemId)
    {
        var csoId = Guid.NewGuid();
        var item = new ActivityRunProfileExecutionItem { ObjectChangeType = changeType, ConnectedSystemObjectId = csoId };
        var record = new ResolvedObjectReference
        {
            Id = csoId,
            State = csoDeleted ? ObjectReferenceState.Deleted : ObjectReferenceState.Live,
            ConnectedSystemId = RecordSystemId
        };

        if (changeRowSystemId is { } systemId)
            item.ConnectedSystemObjectChange = new ConnectedSystemObjectChange { ConnectedSystemId = systemId };

        return (item, record);
    }

    [Test]
    public void GetConnectedSystemId_LiveCso_ReturnsItsOwnSystemWhateverTheChangeRowSays()
    {
        var (item, record) = BuildItem(ObjectChangeType.Projected, csoDeleted: false, StagingTargetSystemId);

        Assert.That(item.GetConnectedSystemId(record), Is.EqualTo(RecordSystemId));
    }

    [TestCase(ObjectChangeType.Added)]
    [TestCase(ObjectChangeType.Updated)]
    [TestCase(ObjectChangeType.Deleted)]
    [TestCase(ObjectChangeType.Exported)]
    [TestCase(ObjectChangeType.Deprovisioned)]
    [TestCase(ObjectChangeType.DriftCorrection)]
    [TestCase(ObjectChangeType.NoChange)]
    [TestCase(ObjectChangeType.PendingExport)]
    [TestCase(ObjectChangeType.PendingExportConfirmed)]
    public void GetConnectedSystemId_RecordSideItemWithDeletedCso_FallsBackToTheChangeRow(
        ObjectChangeType changeType)
    {
        // On a record-side item the change snapshot describes the item's own record, so it is the
        // honest answer once the record is gone.
        var (item, record) = BuildItem(changeType, csoDeleted: true, StagingTargetSystemId);

        Assert.That(item.GetConnectedSystemId(record), Is.EqualTo(StagingTargetSystemId));
    }

    [TestCase(ObjectChangeType.Projected)]
    [TestCase(ObjectChangeType.Joined)]
    [TestCase(ObjectChangeType.AttributeFlow)]
    [TestCase(ObjectChangeType.Disconnected)]
    [TestCase(ObjectChangeType.DisconnectedOutOfScope)]
    [TestCase(ObjectChangeType.OutOfScopeRetainJoin)]
    [TestCase(ObjectChangeType.Created)]
    [TestCase(ObjectChangeType.NotSet)]
    public void GetConnectedSystemId_SyncSideItemWithDeletedCso_ReturnsNullRatherThanAStagingTargetsSystem(
        ObjectChangeType changeType)
    {
        // The bug this pins: a projection item whose source record was later deleted fell back to
        // its change row, which belongs to the record the synchronisation provisioned in a target
        // system, so the page labelled the source record as living in the target system. No system
        // is honest; the wrong system is not.
        var (item, record) = BuildItem(changeType, csoDeleted: true, StagingTargetSystemId);

        Assert.That(item.GetConnectedSystemId(record), Is.Null);
    }

    [Test]
    public void GetConnectedSystemId_NothingToReadFrom_ReturnsNull()
    {
        var (item, record) = BuildItem(ObjectChangeType.Updated, csoDeleted: true, changeRowSystemId: null);

        Assert.That(item.GetConnectedSystemId(record), Is.Null);
    }
}
