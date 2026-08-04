// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Activities;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// Tests for <see cref="SyncOutcomeTypes"/>: which outcome type reports a staged Pending Export, and which
/// types count as one. Shared by the worker (writes outcomes), the repository (counts them) and the portal
/// (renders them), so a divergence here shows up as a wrong causality narrative or a dropped Activity total.
/// </summary>
[TestFixture]
public class SyncOutcomeTypesTests
{
    /// <summary>
    /// A delete Pending Export carries the target's secondary external ID (the DN, for LDAP) as an attribute
    /// value change so the connector can still find the entry after the Connected System Object is
    /// disconnected. Reported as a plain PendingExportCreated, a deprovisioning cascade was therefore
    /// indistinguishable from an ordinary one-attribute update in the causality views.
    /// </summary>
    [Test]
    public void ForPendingExport_DeleteChangeType_IsDeprovisionQueued()
    {
        var pendingExport = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Delete };

        Assert.That(SyncOutcomeTypes.ForPendingExport(pendingExport),
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued));
    }

    [TestCase(PendingExportChangeType.Create)]
    [TestCase(PendingExportChangeType.Update)]
    public void ForPendingExport_NonDeleteChangeType_IsPendingExportCreated(PendingExportChangeType changeType)
    {
        var pendingExport = new PendingExport { Id = Guid.NewGuid(), ChangeType = changeType };

        Assert.That(SyncOutcomeTypes.ForPendingExport(pendingExport),
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
    }

    /// <summary>
    /// A queued deprovision IS a Pending Export; only its intent differs. Anything counting Pending Exports
    /// must accept both, or an Activity's Pending Export total silently drops on a deprovisioning run.
    /// </summary>
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, true)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued, true)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned, false)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Exported, false)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, false)]
    public void IsPendingExport_ClassifiesTheStagingOutcomeTypes(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType,
        bool expected)
    {
        Assert.That(SyncOutcomeTypes.IsPendingExport(outcomeType), Is.EqualTo(expected));
    }
}
