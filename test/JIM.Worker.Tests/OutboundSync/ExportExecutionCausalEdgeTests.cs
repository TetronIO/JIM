// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Proves the queueing-to-executing causal seam (#1223): an export run item saying why it exported anything.
///
/// This is the hop the PRD expected to get for free from <c>ActivityRunProfileExecutionItem.PendingExportId</c>.
/// That column is populated only on a <c>PendingExport</c>-type item and is null on every ordinary
/// <c>Exported</c> item, so an export item had no cause of any kind: the Causality panel rendered no "Caused by"
/// at all, which is the "this change has no cause whatsoever" defect the whole feature exists to fix.
///
/// The cause cannot be reconstructed later either. The Pending Export row is deleted the moment the export
/// succeeds, so the link is recorded here or never.
/// </summary>
[TestFixture]
public class ExportExecutionCausalEdgeTests
{
    private const int TargetSystemId = 4;
    private static readonly Guid QueueingItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PendingExportId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SourceMvoId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>
    /// The base case: the synchronisation that staged the Pending Export is named as the cause of the export
    /// that carried it out, along with the export cycle and the identity behind it.
    /// </summary>
    [Test]
    public void RecordQueueingCause_ExportItemNamingItsQueueingItem_WritesAnEdgeNamingIt()
    {
        var executionItem = new ActivityRunProfileExecutionItem { ObjectChangeType = ObjectChangeType.Exported };
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Exported
        };

        ExportCausalEdgeBuilder.RecordQueueingCause(executionItem, ExportItem(), outcome, TargetSystem());

        Assert.That(executionItem.CausalEdges, Has.Count.EqualTo(1));
        var edge = executionItem.CausalEdges[0];
        Assert.Multiple(() =>
        {
            Assert.That(edge.EdgeType, Is.EqualTo(CausalEdgeType.PendingExportQueueingCausedExportExecution));
            Assert.That(edge.CauseRunProfileExecutionItemId, Is.EqualTo(QueueingItemId));
            Assert.That(edge.CausePendingExportId, Is.EqualTo(PendingExportId));
            Assert.That(edge.CauseMetaverseObjectId, Is.EqualTo(SourceMvoId));
            Assert.That(edge.ReasonCode, Is.EqualTo(CausalReasonCode.ExportUpdateStaged));
            Assert.That(edge.ConnectedSystemId, Is.EqualTo(TargetSystemId),
                "the sentence names the system exported to, so the edge must snapshot it");
            Assert.That(edge.ConnectedSystemName, Is.EqualTo("Glitterband EMEA"));
            Assert.That(edge.EffectSyncOutcome, Is.SameAs(outcome),
                "the edge must name the outcome it explains, or an item carrying several outcomes groups its causes under the wrong one");
        });
    }

    /// <summary>
    /// The cause reads back after the objects behind it are gone, so the display name is snapshotted from
    /// whatever named the object at export time.
    /// </summary>
    [Test]
    public void RecordQueueingCause_ExportItemWithADisplayName_SnapshotsItOntoTheEdge()
    {
        var executionItem = new ActivityRunProfileExecutionItem
        {
            ObjectChangeType = ObjectChangeType.Exported,
            DisplayNameSnapshot = "Project-AgileCore"
        };

        ExportCausalEdgeBuilder.RecordQueueingCause(executionItem, ExportItem(), null, TargetSystem());

        Assert.That(executionItem.CausalEdges[0].CauseDisplayName, Is.EqualTo("Project-AgileCore"));
    }

    /// <summary>
    /// A Pending Export staged before this seam existed names no queueing item, but still names the identity
    /// whose change produced it. That is a genuine, if shallower, answer to "why did this export happen", so
    /// the edge is still worth writing; the chain simply ends at the Metaverse Object rather than walking on.
    /// </summary>
    [Test]
    public void RecordQueueingCause_ExportItemNamingNoQueueingItem_StillWritesTheIdentityAsTheCause()
    {
        var executionItem = new ActivityRunProfileExecutionItem { ObjectChangeType = ObjectChangeType.Exported };
        var exportItem = ExportItem();
        exportItem.QueuedByRunProfileExecutionItemId = null;

        ExportCausalEdgeBuilder.RecordQueueingCause(executionItem, exportItem, null, TargetSystem());

        Assert.That(executionItem.CausalEdges, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(executionItem.CausalEdges[0].CauseRunProfileExecutionItemId, Is.Null);
            Assert.That(executionItem.CausalEdges[0].CauseMetaverseObjectId, Is.EqualTo(SourceMvoId));
        });
    }

    /// <summary>
    /// An export that can name neither the item that queued it nor the identity behind it has nothing to say,
    /// and an edge that names no cause is worse than none: the panel would render a "Caused by" heading whose
    /// one entry is unidentifiable.
    /// </summary>
    [Test]
    public void RecordQueueingCause_ExportItemNamingNeitherItemNorIdentity_WritesNoEdge()
    {
        var executionItem = new ActivityRunProfileExecutionItem { ObjectChangeType = ObjectChangeType.Exported };
        var exportItem = ExportItem();
        exportItem.QueuedByRunProfileExecutionItemId = null;
        exportItem.SourceMetaverseObjectId = null;

        ExportCausalEdgeBuilder.RecordQueueingCause(executionItem, exportItem, null, TargetSystem());

        Assert.That(executionItem.CausalEdges, Is.Empty);
    }

    /// <summary>
    /// A failed export changed nothing on the Connected System, so there is no effect for a cause to explain.
    /// Recording one anyway would put a "Caused by" on an item whose story is an error message.
    /// </summary>
    [Test]
    public void RecordQueueingCause_FailedExport_WritesNoEdge()
    {
        var executionItem = new ActivityRunProfileExecutionItem { ObjectChangeType = ObjectChangeType.Exported };
        var exportItem = ExportItem();
        exportItem.Succeeded = false;

        ExportCausalEdgeBuilder.RecordQueueingCause(executionItem, exportItem, null, TargetSystem());

        Assert.That(executionItem.CausalEdges, Is.Empty);
    }

    /// <summary>
    /// A deprovisioning export is the same seam: the synchronisation that decided the object should go is the
    /// cause of the run that removed it.
    /// </summary>
    [Test]
    public void RecordQueueingCause_DeprovisioningExport_WritesTheSameSeam()
    {
        var executionItem = new ActivityRunProfileExecutionItem { ObjectChangeType = ObjectChangeType.Deprovisioned };
        var exportItem = ExportItem();
        exportItem.ChangeType = PendingExportChangeType.Delete;

        ExportCausalEdgeBuilder.RecordQueueingCause(executionItem, exportItem, null, TargetSystem());

        Assert.That(executionItem.CausalEdges, Has.Count.EqualTo(1));
        Assert.That(executionItem.CausalEdges[0].EdgeType,
            Is.EqualTo(CausalEdgeType.PendingExportQueueingCausedExportExecution));
    }

    private static ProcessedExportItem ExportItem() => new()
    {
        ChangeType = PendingExportChangeType.Update,
        Succeeded = true,
        PendingExportId = PendingExportId,
        SourceMetaverseObjectId = SourceMvoId,
        QueuedByRunProfileExecutionItemId = QueueingItemId
    };

    private static ConnectedSystem TargetSystem() => new() { Id = TargetSystemId, Name = "Glitterband EMEA" };

    #region what the queueing synchronisation decided (#1223)

    /// <summary>
    /// A provisioning create records which Synchronisation Rule decided it, snapshotted so the chain reads
    /// after the rule is renamed or deleted. The Pending Export already carries the rule id (#1121); the name
    /// is resolved by the caller and handed in.
    /// </summary>
    [Test]
    public void RecordQueueingCause_ProvisioningCreate_RecordsTheRuleAndTheCreateReason()
    {
        var executionItem = new ActivityRunProfileExecutionItem { ObjectChangeType = ObjectChangeType.Exported };
        var exportItem = ExportItem();
        exportItem.ChangeType = PendingExportChangeType.Create;
        exportItem.ProvisioningSyncRuleId = 12;

        ExportCausalEdgeBuilder.RecordQueueingCause(executionItem, exportItem, null, TargetSystem(),
            provisioningSyncRuleName: "EMEA LDAP Export Users");

        var edge = executionItem.CausalEdges[0];
        Assert.Multiple(() =>
        {
            Assert.That(edge.ReasonCode, Is.EqualTo(CausalReasonCode.ExportCreateStaged));
            Assert.That(edge.SyncRuleId, Is.EqualTo(12));
            Assert.That(edge.SyncRuleName, Is.EqualTo("EMEA LDAP Export Users"));
        });
    }

    /// <summary>
    /// A deprovision carries the delete reason, and no rule: deletion is decided by the Deletion Rule and the
    /// deprovisioning action, not by a provisioning decision.
    /// </summary>
    [Test]
    public void RecordQueueingCause_Deprovision_RecordsTheDeleteReasonAndNoRule()
    {
        var executionItem = new ActivityRunProfileExecutionItem { ObjectChangeType = ObjectChangeType.Deprovisioned };
        var exportItem = ExportItem();
        exportItem.ChangeType = PendingExportChangeType.Delete;

        ExportCausalEdgeBuilder.RecordQueueingCause(executionItem, exportItem, null, TargetSystem());

        var edge = executionItem.CausalEdges[0];
        Assert.Multiple(() =>
        {
            Assert.That(edge.ReasonCode, Is.EqualTo(CausalReasonCode.ExportDeleteStaged));
            Assert.That(edge.SyncRuleId, Is.Null);
        });
    }

    #endregion
}
