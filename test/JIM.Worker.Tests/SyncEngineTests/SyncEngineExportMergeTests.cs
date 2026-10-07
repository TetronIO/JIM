// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// The in-memory Pending Export merge, extracted from
/// <c>CreateOrUpdatePendingExportWithNoNetChangeAsync</c> into the pure engine (#288 plan Phase 1b). When a
/// page has already staged a Pending Export for a CSO (typically drift detection), a subsequent export
/// evaluation merges its attribute changes into that staged export rather than creating a duplicate; export
/// evaluation wins a collision because it derives from the latest Metaverse Object state, and #1199's
/// whole-attribute supersede drops staged per-value changes an incoming replace makes moot. These tests pin
/// the extracted merge to the braided implementation's behaviour.
/// </summary>
[TestFixture]
public class SyncEngineExportMergeTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    [Test]
    public void MergeAttributeChangesIntoPendingExport_ANewAttribute_IsAdded()
    {
        var pe = PendingExportWith(SingleValuedChange(attributeId: 1, "old"));
        var incoming = new List<PendingExportAttributeValueChange> { SingleValuedChange(attributeId: 2, "new") };

        var result = _engine.MergeAttributeChangesIntoPendingExport(pe, incoming);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AddedCount, Is.EqualTo(1));
            Assert.That(result.ReplacedCount, Is.EqualTo(0));
            Assert.That(pe.AttributeValueChanges, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void MergeAttributeChangesIntoPendingExport_TheSameSingleValuedAttribute_IsReplacedByTheNewerValue()
    {
        // A single-valued attribute keys by attribute id alone: if both survived, the connector would emit
        // "SINGLE-VALUE attribute specified more than once" and the export would never apply. The incoming
        // Update is a whole-attribute replace, so the #1199 supersede removes the staged change before the
        // key-based merge runs; the counts therefore report an add, and the end state is what matters.
        var pe = PendingExportWith(SingleValuedChange(attributeId: 1, "stale"));
        var newer = SingleValuedChange(attributeId: 1, "current");

        var result = _engine.MergeAttributeChangesIntoPendingExport(pe, [newer]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AddedCount, Is.EqualTo(1));
            Assert.That(pe.AttributeValueChanges, Has.Count.EqualTo(1));
            Assert.That(pe.AttributeValueChanges[0], Is.SameAs(newer));
        }
    }

    [Test]
    public void MergeAttributeChangesIntoPendingExport_DistinctMultiValuedValues_AreBothKept()
    {
        // Multi-valued attributes key by attribute and value: drift can stage one member while export
        // evaluation stages another, and both belong on the merged export.
        var pe = PendingExportWith(MultiValuedAdd(attributeId: 7, "cn=alice"));

        var result = _engine.MergeAttributeChangesIntoPendingExport(pe, [MultiValuedAdd(attributeId: 7, "cn=bob")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AddedCount, Is.EqualTo(1));
            Assert.That(pe.AttributeValueChanges, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void MergeAttributeChangesIntoPendingExport_TheSameMultiValuedValue_IsReplacedNotDuplicated()
    {
        var pe = PendingExportWith(MultiValuedAdd(attributeId: 7, "cn=alice"));
        var newer = MultiValuedAdd(attributeId: 7, "cn=alice");

        var result = _engine.MergeAttributeChangesIntoPendingExport(pe, [newer]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ReplacedCount, Is.EqualTo(1));
            Assert.That(pe.AttributeValueChanges, Has.Count.EqualTo(1));
            Assert.That(pe.AttributeValueChanges[0], Is.SameAs(newer));
        }
    }

    [Test]
    public void MergeAttributeChangesIntoPendingExport_AWholeAttributeReplace_SupersedesStagedPerValueChanges()
    {
        // #1199: an incoming Update sets the attribute's entire value set, so a staged per-value Remove for
        // the same attribute is void whatever its value; left in place, the connector would emit the replace
        // followed by a delete of a value the replace already removed, and LDAP rejects the modify atomically.
        var staleRemove = MultiValuedRemove(attributeId: 7, "cn=old-title");
        var pe = PendingExportWith(staleRemove);
        var incomingReplace = SingleValuedChange(attributeId: 7, "new-title");

        _engine.MergeAttributeChangesIntoPendingExport(pe, [incomingReplace]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pe.AttributeValueChanges, Does.Not.Contain(staleRemove));
            Assert.That(pe.AttributeValueChanges, Does.Contain(incomingReplace));
        }
    }

    [Test]
    public void MergeAttributeChangesIntoPendingExport_AnIncomingUnresolvedReference_FlagsThePendingExport()
    {
        var pe = PendingExportWith(SingleValuedChange(attributeId: 1, "value"));
        var reference = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = 9,
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 9, Name = "manager", AttributePlurality = AttributePlurality.SingleValued
            },
            ChangeType = PendingExportAttributeChangeType.Update,
            UnresolvedReferenceValue = Guid.NewGuid().ToString()
        };

        _engine.MergeAttributeChangesIntoPendingExport(pe, [reference]);

        Assert.That(pe.HasUnresolvedReferences, Is.True);
    }

    [Test]
    public void MergeAttributeChangesIntoPendingExport_NothingIncoming_LeavesThePendingExportUntouched()
    {
        var existing = SingleValuedChange(attributeId: 1, "value");
        var pe = PendingExportWith(existing);

        var result = _engine.MergeAttributeChangesIntoPendingExport(pe, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AddedCount, Is.EqualTo(0));
            Assert.That(result.ReplacedCount, Is.EqualTo(0));
            Assert.That(pe.AttributeValueChanges, Has.Count.EqualTo(1));
            Assert.That(pe.AttributeValueChanges[0], Is.SameAs(existing));
            Assert.That(pe.HasUnresolvedReferences, Is.False);
        }
    }

    // ---- Withdrawing staged changes an evaluation found already current ----

    [Test]
    public void WithdrawChangesAlreadyCurrent_SingleValuedAttributeNowCurrent_WithdrawsTheStagedChange()
    {
        // A staged "set B" is stale once the target already holds the value the Metaverse now wants ("A").
        var pe = PendingExportWith(SingleValuedChange(attributeId: 1, "B"));

        var withdrawn = _engine.WithdrawChangesAlreadyCurrent(pe, [SingleValuedChange(attributeId: 1, "A")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawn, Has.Count.EqualTo(1));
            Assert.That(pe.AttributeValueChanges, Is.Empty);
        }
    }

    [Test]
    public void WithdrawChangesAlreadyCurrent_OtherAttributes_AreKept()
    {
        var keep = SingleValuedChange(attributeId: 2, "Architect");
        var pe = PendingExportWith(SingleValuedChange(attributeId: 1, "B"), keep);

        var withdrawn = _engine.WithdrawChangesAlreadyCurrent(pe, [SingleValuedChange(attributeId: 1, "A")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawn, Has.Count.EqualTo(1));
            Assert.That(pe.AttributeValueChanges, Is.EqualTo(new[] { keep }));
        }
    }

    [Test]
    public void WithdrawChangesAlreadyCurrent_StagedRemoveOfAValueTheTargetShouldKeep_IsWithdrawn()
    {
        // A staged "remove cn=alice" (from drift) is stale once the Metaverse wants cn=alice again and the target
        // still holds it: the evaluation's "add cn=alice" is skipped as already current, and must withdraw the
        // remove, or the export would take cn=alice out of the group.
        var pe = PendingExportWith(MultiValuedRemove(attributeId: 7, "cn=alice"));

        var withdrawn = _engine.WithdrawChangesAlreadyCurrent(pe, [MultiValuedAdd(attributeId: 7, "cn=alice")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawn, Has.Count.EqualTo(1));
            Assert.That(pe.AttributeValueChanges, Is.Empty);
        }
    }

    [Test]
    public void WithdrawChangesAlreadyCurrent_StagedChangeForAnotherValueOfTheSameAttribute_IsKept()
    {
        var keep = MultiValuedAdd(attributeId: 7, "cn=bob");
        var pe = PendingExportWith(keep);

        var withdrawn = _engine.WithdrawChangesAlreadyCurrent(pe, [MultiValuedAdd(attributeId: 7, "cn=alice")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawn, Is.Empty);
            Assert.That(pe.AttributeValueChanges, Is.EqualTo(new[] { keep }));
        }
    }

    [Test]
    public void WithdrawChangesAlreadyCurrent_WholeAttributeAlreadyCurrent_WithdrawsEveryStagedChangeForIt()
    {
        // A RemoveAll already current (the target holds no values, and the Metaverse wants none) makes every staged
        // per-value change for the attribute stale, whatever its value (#1199's whole-attribute rule).
        var removeAll = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = 7, ChangeType = PendingExportAttributeChangeType.RemoveAll,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = 7, Name = "attr7", AttributePlurality = AttributePlurality.MultiValued }
        };
        var pe = PendingExportWith(MultiValuedAdd(attributeId: 7, "cn=alice"), MultiValuedAdd(attributeId: 7, "cn=bob"));

        var withdrawn = _engine.WithdrawChangesAlreadyCurrent(pe, [removeAll]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawn, Has.Count.EqualTo(2));
            Assert.That(pe.AttributeValueChanges, Is.Empty);
        }
    }

    [Test]
    public void WithdrawChangesAlreadyCurrent_ChangeAlreadySentAndAwaitingConfirmation_IsKept()
    {
        // Sent, applied optimistically, and awaiting the confirming import: that is what makes the target "already
        // current", so it is the confirmation of the change, not a stale instruction.
        var sent = SingleValuedChange(attributeId: 1, "A");
        sent.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = PendingExportWith(sent);

        var withdrawn = _engine.WithdrawChangesAlreadyCurrent(pe, [SingleValuedChange(attributeId: 1, "A")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawn, Is.Empty);
            Assert.That(pe.AttributeValueChanges, Is.EqualTo(new[] { sent }));
        }
    }

    [Test]
    public void WithdrawChangesAlreadyCurrent_NothingAlreadyCurrent_ChangesNothing()
    {
        var pe = PendingExportWith(SingleValuedChange(attributeId: 1, "B"));

        var withdrawn = _engine.WithdrawChangesAlreadyCurrent(pe, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawn, Is.Empty);
            Assert.That(pe.AttributeValueChanges, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void WithdrawChangesAlreadyCurrent_ReturnsTheWithdrawnChangesThemselves()
    {
        // The run records what it withdrew on the object's execution item (#2001), so it needs the changes, not a count.
        var stale = SingleValuedChange(attributeId: 1, "B");
        var pe = PendingExportWith(stale, SingleValuedChange(attributeId: 2, "Architect"));

        var withdrawn = _engine.WithdrawChangesAlreadyCurrent(pe, [SingleValuedChange(attributeId: 1, "A")]);

        Assert.That(withdrawn, Is.EqualTo(new[] { stale }));
    }

    // ---- Selecting the queued changes an evaluation would withdraw (#2001) ----

    [Test]
    public void SelectChangesWithdrawnAsAlreadyCurrent_QueuedChangeForAValueTheTargetAlreadyHolds_IsWithdrawn()
    {
        var stale = SingleValuedChange(attributeId: 1, "ANALYST");
        var keep = SingleValuedChange(attributeId: 2, "Architect");

        var withdrawn = SyncEngine.SelectChangesWithdrawnAsAlreadyCurrent([], [stale, keep], [SingleValuedChange(attributeId: 1, "Analyst")]);

        Assert.That(withdrawn, Is.EqualTo(new[] { stale }));
    }

    [Test]
    public void SelectChangesWithdrawnAsAlreadyCurrent_QueuedChangeReplacedByANewlyStagedOne_IsNotWithdrawn()
    {
        // A queued change superseded by the evaluation's own new change is replaced, not withdrawn: the new change goes
        // out instead, and the preview states that as an update. Only the change the target already makes unnecessary
        // is withdrawn.
        var stale = SingleValuedChange(attributeId: 1, "ANALYST");
        var replaced = SingleValuedChange(attributeId: 2, "Sales");

        var withdrawn = SyncEngine.SelectChangesWithdrawnAsAlreadyCurrent(
            [SingleValuedChange(attributeId: 2, "Commercial")], [stale, replaced], [SingleValuedChange(attributeId: 1, "Analyst")]);

        Assert.That(withdrawn, Is.EqualTo(new[] { stale }));
    }

    [Test]
    public void SelectChangesWithdrawnAsAlreadyCurrent_MultiValuedRemoveOfAValueTheMetaverseWantsAgain_IsWithdrawn()
    {
        var staleRemove = MultiValuedRemove(attributeId: 7, "cn=alice");
        var otherValue = MultiValuedAdd(attributeId: 7, "cn=bob");

        var withdrawn = SyncEngine.SelectChangesWithdrawnAsAlreadyCurrent([], [staleRemove, otherValue], [MultiValuedAdd(attributeId: 7, "cn=alice")]);

        Assert.That(withdrawn, Is.EqualTo(new[] { staleRemove }));
    }

    [Test]
    public void SelectChangesWithdrawnAsAlreadyCurrent_ChangeAlreadySentAndAwaitingConfirmation_IsNotWithdrawn()
    {
        var sent = SingleValuedChange(attributeId: 1, "Analyst");
        sent.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;

        var withdrawn = SyncEngine.SelectChangesWithdrawnAsAlreadyCurrent([], [sent], [SingleValuedChange(attributeId: 1, "Analyst")]);

        Assert.That(withdrawn, Is.Empty);
    }

    [Test]
    public void SelectChangesWithdrawnAsAlreadyCurrent_NothingAlreadyCurrent_WithdrawsNothing()
    {
        var withdrawn = SyncEngine.SelectChangesWithdrawnAsAlreadyCurrent(
            [SingleValuedChange(attributeId: 1, "C")], [SingleValuedChange(attributeId: 1, "B")], []);

        Assert.That(withdrawn, Is.Empty, "a change replaced by a new one is not a withdrawal");
    }

    [Test]
    public void SelectSurvivingDriftChanges_ChangeTheEvaluationFoundAlreadyCurrent_DoesNotSurvive()
    {
        // The database-merge counterpart: a persisted Pending Export's change for an attribute the evaluation found
        // already current is dropped when the export is rebuilt, exactly as a newly evaluated change would drop it.
        var stale = SingleValuedChange(attributeId: 1, "B");
        var keep = SingleValuedChange(attributeId: 2, "Architect");

        var survivors = SyncEngine.SelectSurvivingDriftChanges([], [stale, keep], alreadyCurrentChanges: [SingleValuedChange(attributeId: 1, "A")]);

        Assert.That(survivors, Is.EqualTo(new[] { keep }));
    }

    private static PendingExport PendingExportWith(params PendingExportAttributeValueChange[] changes)
    {
        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            ConnectedSystemObjectId = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending
        };
        foreach (var change in changes)
            pe.AttributeValueChanges.Add(change);
        return pe;
    }

    private static PendingExportAttributeValueChange SingleValuedChange(int attributeId, string value) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attributeId,
        Attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = attributeId, Name = $"attr{attributeId}", AttributePlurality = AttributePlurality.SingleValued
        },
        ChangeType = PendingExportAttributeChangeType.Update,
        StringValue = value
    };

    private static PendingExportAttributeValueChange MultiValuedAdd(int attributeId, string value) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attributeId,
        Attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = attributeId, Name = $"attr{attributeId}", AttributePlurality = AttributePlurality.MultiValued
        },
        ChangeType = PendingExportAttributeChangeType.Add,
        StringValue = value
    };

    private static PendingExportAttributeValueChange MultiValuedRemove(int attributeId, string value) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attributeId,
        Attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = attributeId, Name = $"attr{attributeId}", AttributePlurality = AttributePlurality.MultiValued
        },
        ChangeType = PendingExportAttributeChangeType.Remove,
        StringValue = value
    };
}
