// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Sync;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for <see cref="Application.Servers.SyncEngine.ApplyGeneratedValue"/> (Unique Value
/// Generation, #242, Phase 2 work package G): the worker's inline resolution calls this once it has a real
/// value for a <see cref="PendingGeneratedValue"/>, and it stages that value exactly as
/// <c>ProcessExpressionMapping</c>'s scalar path stages an ordinary expression result (diff against the
/// object's effective current value, stage removal/addition, take over provenance).
/// </summary>
public class SyncEngineApplyGeneratedValueTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    private static PendingGeneratedValue Pending(MetaverseAttribute target, int? systemId = 5, int? syncRuleId = 1) => new()
    {
        Mapping = new SyncRuleMapping
        {
            Id = syncRuleId ?? 1,
            SyncRuleId = syncRuleId ?? 1,
            TargetMetaverseAttribute = target,
            Generation = new SyncRuleMappingGeneration()
        },
        AttributeId = target.Id,
        ContributedBySystemId = systemId,
        ContributedBySyncRuleId = syncRuleId,
        SourceConnectedSystemObjectId = Guid.NewGuid(),
        BaseValue = "ada"
    };

    [Test]
    public void ApplyGeneratedValue_TextTargetNoExistingValue_StagesAdditionWithPendingProvenance()
    {
        var target = new MetaverseAttribute { Id = 100, Name = "Account Name", Type = AttributeDataType.Text };
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var pending = Pending(target);

        _engine.ApplyGeneratedValue(mvo, pending, "joe.bloggs", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
            var added = mvo.PendingAttributeValueAdditions.Single();
            Assert.That(added.StringValue, Is.EqualTo("joe.bloggs"));
            Assert.That(added.AttributeId, Is.EqualTo(target.Id));
            Assert.That(added.ContributedBySystemId, Is.EqualTo(pending.ContributedBySystemId));
            Assert.That(added.ContributedBySyncRuleId, Is.EqualTo(pending.ContributedBySyncRuleId));
        }
    }

    [Test]
    public void ApplyGeneratedValue_TextTargetChangedValue_StagesRemovalOfOldAndAdditionOfNew()
    {
        var target = new MetaverseAttribute { Id = 100, Name = "Account Name", Type = AttributeDataType.Text };
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var existing = new MetaverseObjectAttributeValue { Attribute = target, AttributeId = target.Id, StringValue = "joe.bloggs" };
        mvo.AttributeValues.Add(existing);
        var pending = Pending(target);

        _engine.ApplyGeneratedValue(mvo, pending, "joe.bloggs1", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Contains.Item(existing));
            Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("joe.bloggs1"));
        }
    }

    [Test]
    public void ApplyGeneratedValue_TextTargetUnchangedValue_StagesNoChangeButTakesOverProvenance()
    {
        var target = new MetaverseAttribute { Id = 100, Name = "Account Name", Type = AttributeDataType.Text };
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var existing = new MetaverseObjectAttributeValue
        {
            Attribute = target,
            AttributeId = target.Id,
            StringValue = "joe.bloggs",
            ContributedBySystemId = 99,
            ContributedBySyncRuleId = 99
        };
        mvo.AttributeValues.Add(existing);
        var pending = Pending(target, systemId: 5, syncRuleId: 1);

        _engine.ApplyGeneratedValue(mvo, pending, "joe.bloggs", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "an unchanged value stages no removal");
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "an unchanged value stages no addition");
            Assert.That(existing.ContributedBySystemId, Is.EqualTo(5), "provenance must still be taken over on the surviving row");
            Assert.That(existing.ContributedBySyncRuleId, Is.EqualTo(1));
        }
    }

    [Test]
    public void ApplyGeneratedValue_NumberTarget_SetsIntValue()
    {
        var target = new MetaverseAttribute { Id = 101, Name = "Employee Number", Type = AttributeDataType.Number };
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var pending = Pending(target);

        _engine.ApplyGeneratedValue(mvo, pending, "100456", 100456);

        var added = mvo.PendingAttributeValueAdditions.Single();
        Assert.That(added.IntValue, Is.EqualTo(100456));
    }

    [Test]
    public void ApplyGeneratedValue_NumberTargetOverflow_ThrowsInvalidOperationExceptionRatherThanTruncating()
    {
        var target = new MetaverseAttribute { Id = 101, Name = "Employee Number", Type = AttributeDataType.Number };
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var pending = Pending(target);

        Assert.That(() => _engine.ApplyGeneratedValue(mvo, pending, "9999999999", 9999999999L),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void ApplyGeneratedValue_LongNumberTarget_SetsLongValue()
    {
        var target = new MetaverseAttribute { Id = 102, Name = "Correlation Id", Type = AttributeDataType.LongNumber };
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var pending = Pending(target);

        _engine.ApplyGeneratedValue(mvo, pending, "9999999999", 9999999999L);

        var added = mvo.PendingAttributeValueAdditions.Single();
        Assert.That(added.LongValue, Is.EqualTo(9999999999L));
    }

    [Test]
    public void ApplyGeneratedValue_AnotherRulesPendingAdditionForTheAttribute_IsRemoved()
    {
        // Winner takes the attribute: any other rule's pending addition for this attribute staged earlier in
        // the same pass must not survive alongside the generated value.
        var target = new MetaverseAttribute { Id = 100, Name = "Account Name", Type = AttributeDataType.Text };
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
        {
            Attribute = target,
            AttributeId = target.Id,
            StringValue = "someone.else",
            ContributedBySystemId = 42,
            ContributedBySyncRuleId = 42
        });
        var pending = Pending(target);

        _engine.ApplyGeneratedValue(mvo, pending, "joe.bloggs", null);

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("joe.bloggs"));
    }
}
