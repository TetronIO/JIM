// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Utilities;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// One test per <see cref="ConnectedSystemObjectConnectionState"/> value, proving
/// <see cref="ConnectedSystemObjectConnectionStateResolver.Resolve"/> derives each state from the
/// combination of <see cref="ConnectedSystemObjectStatus"/> and Pending Export it is documented to
/// (see the resolver's remarks for why a Create Pending Export never reaches this method paired with
/// a Normal status, and vice versa for Update).
/// </summary>
[TestFixture]
public class ConnectedSystemObjectConnectionStateResolverTests
{
    [Test]
    public void Resolve_NormalStatusNoPendingExport_ReturnsInSync()
    {
        var result = ConnectedSystemObjectConnectionStateResolver.Resolve(ConnectedSystemObjectStatus.Normal, null);

        Assert.That(result, Is.EqualTo(ConnectedSystemObjectConnectionState.InSync));
    }

    [Test]
    public void Resolve_NormalStatusWithUpdatePendingExport_ReturnsUpdatePending()
    {
        var pendingExport = new PendingExport
        {
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending
        };

        var result = ConnectedSystemObjectConnectionStateResolver.Resolve(ConnectedSystemObjectStatus.Normal, pendingExport);

        Assert.That(result, Is.EqualTo(ConnectedSystemObjectConnectionState.UpdatePending));
    }

    [Test]
    public void Resolve_PendingProvisioningWithUnexportedCreate_ReturnsProvisioningExportPending()
    {
        var pendingExport = new PendingExport
        {
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending
        };

        var result = ConnectedSystemObjectConnectionStateResolver.Resolve(ConnectedSystemObjectStatus.PendingProvisioning, pendingExport);

        Assert.That(result, Is.EqualTo(ConnectedSystemObjectConnectionState.ProvisioningExportPending));
    }

    [Test]
    public void Resolve_PendingProvisioningWithExportedCreate_ReturnsProvisioningAwaitingConfirmation()
    {
        var pendingExport = new PendingExport
        {
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Exported
        };

        var result = ConnectedSystemObjectConnectionStateResolver.Resolve(ConnectedSystemObjectStatus.PendingProvisioning, pendingExport);

        Assert.That(result, Is.EqualTo(ConnectedSystemObjectConnectionState.ProvisioningAwaitingConfirmation));
    }

    [Test]
    public void Resolve_UpdatePendingExportNotConfirmed_ReturnsExportNotConfirmed()
    {
        var pendingExport = new PendingExport
        {
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.ExportNotConfirmed
        };

        var result = ConnectedSystemObjectConnectionStateResolver.Resolve(ConnectedSystemObjectStatus.Normal, pendingExport);

        Assert.That(result, Is.EqualTo(ConnectedSystemObjectConnectionState.ExportNotConfirmed));
    }

    [Test]
    public void Resolve_PendingExportFailed_ReturnsExportFailedRegardlessOfChangeType()
    {
        var pendingExport = new PendingExport
        {
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Failed
        };

        var result = ConnectedSystemObjectConnectionStateResolver.Resolve(ConnectedSystemObjectStatus.Normal, pendingExport);

        Assert.That(result, Is.EqualTo(ConnectedSystemObjectConnectionState.ExportFailed));
    }

    [Test]
    public void Resolve_DeletePendingExportPendingOrExecuting_ReturnsDeletePending()
    {
        using (Assert.EnterMultipleScope())
        {
            var pending = new PendingExport { ChangeType = PendingExportChangeType.Delete, Status = PendingExportStatus.Pending };
            Assert.That(
                ConnectedSystemObjectConnectionStateResolver.Resolve(ConnectedSystemObjectStatus.Normal, pending),
                Is.EqualTo(ConnectedSystemObjectConnectionState.DeletePending));

            var executing = new PendingExport { ChangeType = PendingExportChangeType.Delete, Status = PendingExportStatus.Executing };
            Assert.That(
                ConnectedSystemObjectConnectionStateResolver.Resolve(ConnectedSystemObjectStatus.Normal, executing),
                Is.EqualTo(ConnectedSystemObjectConnectionState.DeletePending));
        }
    }

    [Test]
    public void Resolve_ObsoleteStatusNoPendingExport_ReturnsObsolete()
    {
        var result = ConnectedSystemObjectConnectionStateResolver.Resolve(ConnectedSystemObjectStatus.Obsolete, null);

        Assert.That(result, Is.EqualTo(ConnectedSystemObjectConnectionState.Obsolete));
    }

    [Test]
    public void Resolve_ObsoleteStatusWithLeftoverUpdatePendingExport_ReturnsObsolete()
    {
        // The object's own status says it has vanished from the Connected System; a leftover Update
        // Pending Export that raced the deletion cannot change that headline fact.
        var pendingExport = new PendingExport
        {
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending
        };

        var result = ConnectedSystemObjectConnectionStateResolver.Resolve(ConnectedSystemObjectStatus.Obsolete, pendingExport);

        Assert.That(result, Is.EqualTo(ConnectedSystemObjectConnectionState.Obsolete));
    }
}
