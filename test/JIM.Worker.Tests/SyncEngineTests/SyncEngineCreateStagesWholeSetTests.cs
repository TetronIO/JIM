// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// A Create provisions the whole object, so a multi-valued attribute it carries is staged with every value the Metaverse
/// Object holds, whatever the synchronisation that raised it changed (#2832). Synchronisation queues only the values a
/// pass changed for export evaluation; a group provisioned because one member was added must still be created with all
/// of its members. An Update, by contrast, stages exactly the values that changed.
/// </summary>
[TestFixture]
public class SyncEngineCreateStagesWholeSetTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    [Test]
    public void ComputeAttributeValueChanges_CreateWhenOnlyOneMemberWasAdded_StagesEveryMemberTheMetaverseObjectHolds()
    {
        var member = ReferenceAttribute();
        var target = TargetReferenceAttribute();
        var alice = Reference(member, persisted: true);
        var bob = Reference(member, persisted: true);
        var added = Reference(member);
        var group = MetaverseObjectHolding(alice, bob, added);

        var changes = _engine.ComputeAttributeValueChanges(group, ExportRule(member, target), [added],
            PendingExportChangeType.Create, existingCso: null, csoAttributeCache: null, out _);

        Assert.That(changes.Select(c => (c.ChangeType, c.UnresolvedReferenceValue)), Is.EquivalentTo(new[]
        {
            (PendingExportAttributeChangeType.Add, alice.ReferenceValueId.ToString()),
            (PendingExportAttributeChangeType.Add, bob.ReferenceValueId.ToString()),
            (PendingExportAttributeChangeType.Add, added.ReferenceValueId.ToString())
        }), "a Create provisions the whole membership, not only the member whose addition raised it");
    }

    [Test]
    public void ComputeAttributeValueChanges_CreateWhenOneMemberWasAddedAndAnotherRemoved_StagesTheWholeSetAndTheRemoval()
    {
        // The removal still travels: an unsent Create rebuilt from this change must supersede the Add it carried for the
        // departed member, which it does by the Remove's merge key.
        var member = ReferenceAttribute();
        var target = TargetReferenceAttribute();
        var alice = Reference(member, persisted: true);
        var added = Reference(member);
        var departed = Reference(member, persisted: true);
        var group = MetaverseObjectHolding(alice, added);

        var changes = _engine.ComputeAttributeValueChanges(group, ExportRule(member, target), [added, departed],
            PendingExportChangeType.Create, existingCso: null, csoAttributeCache: null, out _, removedAttributes: [departed]);

        Assert.That(changes.Select(c => (c.ChangeType, c.UnresolvedReferenceValue)), Is.EquivalentTo(new[]
        {
            (PendingExportAttributeChangeType.Add, alice.ReferenceValueId.ToString()),
            (PendingExportAttributeChangeType.Add, added.ReferenceValueId.ToString()),
            (PendingExportAttributeChangeType.Remove, departed.ReferenceValueId.ToString())
        }));
    }

    [Test]
    public void ComputeAttributeValueChanges_CreateWhenOneTextValueWasAdded_StagesEveryValueTheMetaverseObjectHolds()
    {
        // The rule is the attribute's plurality, not its type: a multi-valued text attribute is created whole too.
        var source = new MetaverseAttribute { Id = ++_nextId, Name = "Proxy Addresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var target = new ConnectedSystemObjectTypeAttribute { Id = ++_nextId, Name = "proxyAddresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var held = new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = source.Id, Attribute = source, StringValue = "smtp:a@x" };
        var added = new MetaverseObjectAttributeValue { AttributeId = source.Id, Attribute = source, StringValue = "smtp:b@x" };
        var mvo = MetaverseObjectHolding(held, added);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [added],
            PendingExportChangeType.Create, existingCso: null, csoAttributeCache: null, out _);

        Assert.That(changes.Select(c => c.StringValue), Is.EquivalentTo(new[] { "smtp:a@x", "smtp:b@x" }));
    }

    [Test]
    public void ComputeAttributeValueChanges_UpdateWhenOnlyOneMemberWasAdded_StagesOnlyThatMember()
    {
        var member = ReferenceAttribute();
        var target = TargetReferenceAttribute();
        var alice = Reference(member, persisted: true);
        var added = Reference(member);
        var group = MetaverseObjectHolding(alice, added);

        var changes = _engine.ComputeAttributeValueChanges(group, ExportRule(member, target), [added],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _);

        Assert.That(changes.Select(c => (c.ChangeType, c.UnresolvedReferenceValue)), Is.EqualTo(new[]
        {
            (PendingExportAttributeChangeType.Add, added.ReferenceValueId.ToString())
        }));
    }

    #region Helpers

    private static int _nextId = 9000;

    private static MetaverseAttribute ReferenceAttribute() => new()
    {
        Id = ++_nextId,
        Name = "Static Members",
        Type = AttributeDataType.Reference,
        AttributePlurality = AttributePlurality.MultiValued
    };

    private static ConnectedSystemObjectTypeAttribute TargetReferenceAttribute() => new()
    {
        Id = ++_nextId,
        Name = "member",
        Type = AttributeDataType.Reference,
        AttributePlurality = AttributePlurality.MultiValued
    };

    private static MetaverseObjectAttributeValue Reference(MetaverseAttribute attribute, bool persisted = false) => new()
    {
        Id = persisted ? Guid.NewGuid() : Guid.Empty,
        AttributeId = attribute.Id,
        Attribute = attribute,
        ReferenceValueId = Guid.NewGuid()
    };

    private static MetaverseObject MetaverseObjectHolding(params MetaverseObjectAttributeValue[] values)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        foreach (var value in values)
        {
            value.MetaverseObject = mvo;
            mvo.AttributeValues.Add(value);
        }

        return mvo;
    }

    private static SyncRule ExportRule(MetaverseAttribute source, ConnectedSystemObjectTypeAttribute target)
    {
        var rule = new SyncRule { Id = 7, Name = "Export", Direction = SyncRuleDirection.Export };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 70,
            SyncRule = rule,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Id = 700, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });
        return rule;
    }

    #endregion
}
