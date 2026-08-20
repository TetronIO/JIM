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
