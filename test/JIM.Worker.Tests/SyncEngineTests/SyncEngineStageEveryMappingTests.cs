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
/// A rule that starts covering an account it did not create sends every attribute it writes (#2826), not only those whose
/// source changed: an Update that selects mappings as a Create would, while still dropping what the account already holds.
/// </summary>
[TestFixture]
public class SyncEngineStageEveryMappingTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    [Test]
    public void ComputeAttributeValueChanges_UpdateStagingEveryMappingWithNothingChanged_StagesTheAttribute()
    {
        var (mvo, rule, account, _) = Arrange("CC-4410");

        var changes = _engine.ComputeAttributeValueChanges(mvo, rule, changedAttributes: [], PendingExportChangeType.Update,
            account, csoAttributeCache: EmptyCache(), out _, stageEveryMapping: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Select(c => c.StringValue), Is.EqualTo(new[] { "CC-4410" }));
            Assert.That(changes.Single().ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update));
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_UpdateStagingEveryMappingWhenTheAccountHoldsTheValue_StagesNothing()
    {
        var (mvo, rule, account, target) = Arrange("CC-4410");
        var held = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = target.Id, Attribute = target, StringValue = "CC-4410" };
        var cache = new[] { held }.ToLookup(v => (account.Id, v.AttributeId));

        var changes = _engine.ComputeAttributeValueChanges(mvo, rule, changedAttributes: [], PendingExportChangeType.Update,
            account, cache, out var alreadyCurrent, stageEveryMapping: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.Empty, "no-net-change detection still drops a value the account holds");
            Assert.That(alreadyCurrent, Is.EqualTo(1));
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_UpdateWithNothingChanged_StagesNothing()
    {
        var (mvo, rule, account, _) = Arrange("CC-4410");

        var changes = _engine.ComputeAttributeValueChanges(mvo, rule, changedAttributes: [], PendingExportChangeType.Update,
            account, csoAttributeCache: EmptyCache(), out _);

        Assert.That(changes, Is.Empty, "an ordinary Update stages only what changed");
    }

    private static (MetaverseObject Mvo, SyncRule Rule, ConnectedSystemObject Account, ConnectedSystemObjectTypeAttribute Target) Arrange(string value)
    {
        var source = new MetaverseAttribute { Id = 1, Name = "Cost Centre", Type = AttributeDataType.Text };
        var target = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "departmentNumber", Type = AttributeDataType.Text };
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 100, Name = "Person" } };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = source.Id, Attribute = source, StringValue = value, MetaverseObject = mvo });
        var rule = new SyncRule { Id = 11, Name = "Manager attributes", Direction = SyncRuleDirection.Export, ConnectedSystemId = 1, ConnectedSystemObjectTypeId = 5 };
        var mapping = new SyncRuleMapping { SyncRule = rule, TargetConnectedSystemAttribute = target, TargetConnectedSystemAttributeId = target.Id };
        mapping.Sources.Add(new SyncRuleMappingSource { MetaverseAttribute = source, MetaverseAttributeId = source.Id });
        rule.AttributeFlowRules.Add(mapping);
        var account = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = 1, TypeId = 5, MetaverseObjectId = mvo.Id };
        return (mvo, rule, account, target);
    }

    private static ILookup<(Guid CsoId, int AttributeId), ConnectedSystemObjectAttributeValue> EmptyCache() =>
        Array.Empty<ConnectedSystemObjectAttributeValue>().ToLookup(v => (Guid.Empty, v.AttributeId));
}
