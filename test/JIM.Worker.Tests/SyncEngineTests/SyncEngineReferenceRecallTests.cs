// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// The reference recall decisions (#908/#1003), extracted from <c>StageRecallFastPathAsync</c> and the recall
/// fallback into the pure engine as the fourth slice of the #288 unbraiding (plan Phase 1d). Three decisions
/// are under test: what removal change a matched target row synthesises (a value-carrying Remove for a
/// multi-valued source, a null-clearing Update for a single-valued one, or nothing when a multi-valued removal
/// has no resolvable value); how recall changes merge with a Pending Export already attached to the CSO (an
/// existing Delete wins, a Create is protected, an Update merges with recall winning key collisions and
/// deleted-object references purged); and the fallback's purge of changes whose unresolved reference is a
/// deleted object. These pin the extracted decisions to the braided implementation's behaviour.
/// </summary>
[TestFixture]
public class SyncEngineReferenceRecallTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    [Test]
    public void DecideRecallRemovalChange_MultiValuedSourceWithAResolvedValue_SynthesisesARemoveCarryingIt()
    {
        // The connector must be told which value to remove from the multi-valued attribute (which member DN).
        var flow = Flow(AttributePlurality.MultiValued);

        var change = _engine.DecideRecallRemovalChange(flow, "cn=deleted-user,dc=corp");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change, Is.Not.Null);
            Assert.That(change!.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Remove));
            Assert.That(change.StringValue, Is.EqualTo("cn=deleted-user,dc=corp"));
            Assert.That(change.AttributeId, Is.EqualTo(flow.TargetAttribute.Id));
            Assert.That(change.Attribute, Is.SameAs(flow.TargetAttribute));
        }
    }

    [Test]
    public void DecideRecallRemovalChange_MultiValuedSourceWithNoResolvableValue_StagesNothing()
    {
        // A multi-valued removal must name the value to remove; a row matched by resolved reference only,
        // with no captured value, cannot be staged and is counted as dropped by the orchestrator.
        var change = _engine.DecideRecallRemovalChange(Flow(AttributePlurality.MultiValued), resolvedRemovalValue: null);

        Assert.That(change, Is.Null);
    }

    [Test]
    public void DecideRecallRemovalChange_SingleValuedSource_SynthesisesANullClearingUpdate()
    {
        // The same shape full evaluation produces for a single-valued removal: an Update carrying no value,
        // which tells the target system to clear the attribute.
        var change = _engine.DecideRecallRemovalChange(Flow(AttributePlurality.SingleValued), "cn=deleted-user,dc=corp");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change, Is.Not.Null);
            Assert.That(change!.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update));
            Assert.That(change.StringValue, Is.Null);
        }
    }

    [Test]
    public void MergeRecallChangesWithExistingPendingExport_NoExistingPendingExport_Proceeds()
    {
        var changes = RecallChanges(MultiValuedRemove(7, "cn=deleted"));

        var result = _engine.MergeRecallChangesWithExistingPendingExport(changes, existingPendingExport: null, deletedMvoIds: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(RecallPendingExportMergeOutcome.Proceed));
            Assert.That(changes, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void MergeRecallChangesWithExistingPendingExport_AnExistingDeleteExport_Skips()
    {
        // #1003: the object is being deprovisioned from the target, so a membership removal is moot; merging
        // would replace the Delete with an Update and leave the object alive in the target forever.
        var existing = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Delete };

        var result = _engine.MergeRecallChangesWithExistingPendingExport(
            RecallChanges(MultiValuedRemove(7, "cn=deleted")), existing, deletedMvoIds: []);

        Assert.That(result.Outcome, Is.EqualTo(RecallPendingExportMergeOutcome.SkippedDeleteSupersedes));
    }

    [Test]
    public void MergeRecallChangesWithExistingPendingExport_AnExistingCreateExport_SkipsToProtectProvisioning()
    {
        // Unreachable after the PendingProvisioning filter; kept as a defensive guard so a provisioning
        // export can never be replaced by a recall Update.
        var existing = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Create };

        var result = _engine.MergeRecallChangesWithExistingPendingExport(
            RecallChanges(MultiValuedRemove(7, "cn=deleted")), existing, deletedMvoIds: []);

        Assert.That(result.Outcome, Is.EqualTo(RecallPendingExportMergeOutcome.SkippedCreateProtected));
    }

    [Test]
    public void MergeRecallChangesWithExistingPendingExport_AnExistingUpdateExport_MergesItsChangesInWithFreshIds()
    {
        // The delete-then-create persistence removes the old rows, so surviving changes are cloned with new
        // ids; recall changes win a merge-key collision because they assert the latest state.
        var existingChange = MultiValuedRemove(8, "cn=other-member");
        var existing = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Update };
        existing.AttributeValueChanges.Add(existingChange);
        var changes = RecallChanges(MultiValuedRemove(7, "cn=deleted"));

        var result = _engine.MergeRecallChangesWithExistingPendingExport(changes, existing, deletedMvoIds: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(RecallPendingExportMergeOutcome.Proceed));
            Assert.That(changes, Has.Count.EqualTo(2));
            var mergedIn = changes.Values.Single(c => c.AttributeId == 8);
            Assert.That(mergedIn.Id, Is.Not.EqualTo(existingChange.Id));
            Assert.That(mergedIn.StringValue, Is.EqualTo("cn=other-member"));
        }
    }

    [Test]
    public void MergeRecallChangesWithExistingPendingExport_ARecallChangeCollidingOnMergeKey_Wins()
    {
        var existingChange = MultiValuedRemove(7, "cn=deleted");
        var existing = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Update };
        existing.AttributeValueChanges.Add(existingChange);
        var recallChange = MultiValuedRemove(7, "cn=deleted");
        var changes = RecallChanges(recallChange);

        var result = _engine.MergeRecallChangesWithExistingPendingExport(changes, existing, deletedMvoIds: []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(RecallPendingExportMergeOutcome.Proceed));
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes.Values.Single(), Is.SameAs(recallChange));
        }
    }

    [Test]
    public void MergeRecallChangesWithExistingPendingExport_AnExistingChangeReferencingADeletedObject_IsPurged()
    {
        // A change whose unresolved reference is a deleted Metaverse Object can never resolve; merged in, it
        // would wedge the export in deferred-resolution limbo.
        var deletedMvoId = Guid.NewGuid();
        var doomedChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = 8,
            Attribute = MultiValuedAttribute(8),
            ChangeType = PendingExportAttributeChangeType.Add,
            UnresolvedReferenceValue = deletedMvoId.ToString()
        };
        var existing = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Update };
        existing.AttributeValueChanges.Add(doomedChange);
        var changes = RecallChanges(MultiValuedRemove(7, "cn=deleted"));

        var result = _engine.MergeRecallChangesWithExistingPendingExport(changes, existing, deletedMvoIds: [deletedMvoId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(RecallPendingExportMergeOutcome.Proceed));
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(result.PurgedChangeCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void PurgeChangesReferencingDeletedObjects_MatchesCaseInsensitivelyAndCounts()
    {
        // The fallback's changes carry the deleted object's id as an unresolved reference string; the
        // comparison is case-insensitive because Guid.ToString casing is not guaranteed at every producer.
        var deletedMvoId = Guid.NewGuid();
        var pe = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Update };
        pe.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = 7,
            ChangeType = PendingExportAttributeChangeType.Remove,
            UnresolvedReferenceValue = deletedMvoId.ToString().ToUpperInvariant()
        });
        var survivor = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = 7,
            ChangeType = PendingExportAttributeChangeType.Remove,
            UnresolvedReferenceValue = Guid.NewGuid().ToString()
        };
        pe.AttributeValueChanges.Add(survivor);

        var purged = _engine.PurgeChangesReferencingDeletedObjects(pe, [deletedMvoId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(purged, Is.EqualTo(1));
            Assert.That(pe.AttributeValueChanges, Has.Count.EqualTo(1));
            Assert.That(pe.AttributeValueChanges[0], Is.SameAs(survivor));
        }
    }

    private static ConnectedSystemObjectTypeAttribute MultiValuedAttribute(int id) => new()
    {
        Id = id,
        Name = $"attr{id}",
        AttributePlurality = AttributePlurality.MultiValued
    };

    private static ReferenceRecallDirectFlow Flow(AttributePlurality sourcePlurality) => new()
    {
        ExportRule = new SyncRule { Name = "Export rule", ConnectedSystemId = 1, Direction = SyncRuleDirection.Export },
        TargetAttribute = MultiValuedAttribute(7),
        SourcePlurality = sourcePlurality
    };

    private static PendingExportAttributeValueChange MultiValuedRemove(int attributeId, string value) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attributeId,
        Attribute = MultiValuedAttribute(attributeId),
        ChangeType = PendingExportAttributeChangeType.Remove,
        StringValue = value
    };

    private static Dictionary<string, PendingExportAttributeValueChange> RecallChanges(
        params PendingExportAttributeValueChange[] changes)
    {
        var byMergeKey = new Dictionary<string, PendingExportAttributeValueChange>();
        foreach (var change in changes)
            byMergeKey[SyncEngine.GetAttributeChangeMergeKey(change)] = change;
        return byMergeKey;
    }
}
