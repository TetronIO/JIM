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
/// The shape of an exported attribute change follows the plurality of the attribute it is written to, never the
/// plurality of the Metaverse Attribute it came from (#2182). A single-valued target always receives exactly one
/// Update carrying the value the Metaverse Object now holds, or an all-null Update when it holds none; per-value Add
/// and Remove are reserved for a multi-valued source flowing to a multi-valued target; and an Update against a
/// multi-valued target means "the set is exactly this value", which is what no-net-change detection, export
/// confirmation and optimistic apply must all agree on.
/// </summary>
[TestFixture]
public class SyncEngineExportChangeShapeTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    #region Staging

    [Test]
    public void ComputeAttributeValueChanges_MultiValuedSourceWithOneValueChangedIntoASingleValuedTarget_StagesOneUpdateCarryingTheNewValue()
    {
        // The Metaverse Object's only email changes from a@x to b@x. Additions come first and removals second, as the
        // synchronisation processor passes them; the Metaverse Object already holds the result.
        var source = MetaverseAttribute("Emails", AttributePlurality.MultiValued);
        var target = TargetAttribute("mail", AttributePlurality.SingleValued);
        var removed = TextValue(source, "a@x", persisted: true);
        var added = TextValue(source, "b@x");
        var mvo = MetaverseObjectHolding(added);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [added, removed],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _, removedAttributes: [removed]);

        Assert.That(changes, Has.Count.EqualTo(1), "a single-valued target holds one value, so it is told once what that value is");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes[0].ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update));
            Assert.That(changes[0].StringValue, Is.EqualTo("b@x"));
            Assert.That(changes[0].AttributeId, Is.EqualTo(target.Id));
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_MultiValuedSourceLosingItsOnlyValueIntoASingleValuedTarget_StagesOneClearingUpdate()
    {
        var source = MetaverseAttribute("Emails", AttributePlurality.MultiValued);
        var target = TargetAttribute("mail", AttributePlurality.SingleValued);
        var removed = TextValue(source, "a@x", persisted: true);
        var mvo = MetaverseObjectHolding();

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [removed],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _, removedAttributes: [removed]);

        Assert.That(changes, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes[0].ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update),
                "a lone Remove clears a single-valued attribute only where the target still holds that very value; a clear is an all-null Update");
            Assert.That(SyncEngine.IsPendingChangeEmpty(changes[0]), Is.True);
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_MultiValuedSourceWithOneValueIntoASingleValuedTargetOnCreate_StagesOneUpdate()
    {
        var source = MetaverseAttribute("Emails", AttributePlurality.MultiValued);
        var target = TargetAttribute("mail", AttributePlurality.SingleValued);
        var value = TextValue(source, "b@x");
        var mvo = MetaverseObjectHolding(value);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [value],
            PendingExportChangeType.Create, existingCso: null, csoAttributeCache: null, out _);

        Assert.That(changes, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes[0].ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update));
            Assert.That(changes[0].StringValue, Is.EqualTo("b@x"));
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_MultiValuedSourceIntoAMultiValuedTarget_StillStagesOneChangePerValue()
    {
        var source = MetaverseAttribute("Emails", AttributePlurality.MultiValued);
        var target = TargetAttribute("proxyAddresses", AttributePlurality.MultiValued);
        var removed = TextValue(source, "a@x", persisted: true);
        var added = TextValue(source, "b@x");
        var mvo = MetaverseObjectHolding(added);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [added, removed],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _, removedAttributes: [removed]);

        Assert.That(changes.Select(c => (c.ChangeType, c.StringValue)), Is.EquivalentTo(new[]
        {
            (PendingExportAttributeChangeType.Add, "b@x"),
            (PendingExportAttributeChangeType.Remove, "a@x")
        }));
    }

    [Test]
    public void ComputeAttributeValueChanges_SingleValuedSourceIntoAMultiValuedTarget_StagesOneUpdateMeaningTheWholeSet()
    {
        var source = MetaverseAttribute("Email", AttributePlurality.SingleValued);
        var target = TargetAttribute("mail", AttributePlurality.MultiValued);
        var removed = TextValue(source, "a@x", persisted: true);
        var added = TextValue(source, "b@x");
        var mvo = MetaverseObjectHolding(added);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [added, removed],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _, removedAttributes: [removed]);

        Assert.That(changes.Select(c => (c.ChangeType, c.StringValue)), Is.EqualTo(new[]
        {
            (PendingExportAttributeChangeType.Update, "b@x")
        }));
    }

    [Test]
    public void ComputeAttributeValueChanges_SingleValuedSourceChangedTwiceOnOnePage_StagesTheValueTheMetaverseObjectNowHolds()
    {
        // Two passes on one page merge into one evaluation: a@x became b@x, then b@x became c@x. The changed values
        // arrive as [b, a, c, b] (each pass's additions before its removals, the same instances) and the removals as
        // {a, b}. Taking the first changed value and clearing the target when it had been removed exported a clear,
        // though the Metaverse Object holds c@x.
        var source = MetaverseAttribute("Email", AttributePlurality.SingleValued);
        var target = TargetAttribute("mail", AttributePlurality.SingleValued);
        var a = TextValue(source, "a@x", persisted: true);
        var b = TextValue(source, "b@x");
        var c = TextValue(source, "c@x");
        var mvo = MetaverseObjectHolding(c);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [b, a, c, b],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _, removedAttributes: [a, b]);

        Assert.That(changes.Select(change => (change.ChangeType, change.StringValue)), Is.EqualTo(new[]
        {
            (PendingExportAttributeChangeType.Update, "c@x")
        }));
    }

    #endregion

    #region Reference recall

    [Test]
    public void DecideRecallRemovalChange_MultiValuedSourceIntoASingleValuedTarget_StagesAClearingUpdate()
    {
        var flow = new ReferenceRecallDirectFlow
        {
            ExportRule = new SyncRule { Id = 3, Name = "Export" },
            TargetAttribute = TargetAttribute("manager", AttributePlurality.SingleValued, AttributeDataType.Reference),
            SourcePlurality = AttributePlurality.MultiValued
        };

        var change = _engine.DecideRecallRemovalChange(flow, "CN=Departed,OU=Staff,DC=example,DC=com");

        Assert.That(change, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change!.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update));
            Assert.That(SyncEngine.IsPendingChangeEmpty(change), Is.True);
        }
    }

    [Test]
    public void DecideRecallRemovalChange_MultiValuedSourceIntoAMultiValuedTarget_StillRemovesTheOneValue()
    {
        var flow = new ReferenceRecallDirectFlow
        {
            ExportRule = new SyncRule { Id = 3, Name = "Export" },
            TargetAttribute = TargetAttribute("member", AttributePlurality.MultiValued, AttributeDataType.Reference),
            SourcePlurality = AttributePlurality.MultiValued
        };

        var change = _engine.DecideRecallRemovalChange(flow, "CN=Departed,OU=Staff,DC=example,DC=com");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change!.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Remove));
            Assert.That(change!.StringValue, Is.EqualTo("CN=Departed,OU=Staff,DC=example,DC=com"));
        }
    }

    #endregion

    #region Merge

    [Test]
    public void MergeAttributeChangesIntoPendingExport_AddAndRemoveForASingleValuedTarget_KeepsOneUpdateCarryingTheAddedValue()
    {
        // The pair the engine once staged for a multi-valued source flowing to a single-valued target. Keyed by the
        // attribute alone, the merge kept whichever came last, the Remove, and the target was cleared.
        var target = TargetAttribute("mail", AttributePlurality.SingleValued);
        var staged = PendingExportWith(PendingExportChangeType.Update, Change(target, PendingExportAttributeChangeType.Update, "a@x"));

        _engine.MergeAttributeChangesIntoPendingExport(staged,
            [Change(target, PendingExportAttributeChangeType.Add, "b@x"), Change(target, PendingExportAttributeChangeType.Remove, "a@x")]);

        Assert.That(staged.AttributeValueChanges.Select(c => (c.ChangeType, c.StringValue)), Is.EqualTo(new[]
        {
            (PendingExportAttributeChangeType.Update, "b@x")
        }));
    }

    [Test]
    public void MergeAttributeChangesIntoPendingExport_AddAndRemoveOntoASentCreateForASingleValuedTarget_LeavesOneUpdateCarryingTheAddedValue()
    {
        // A new user is provisioned with a@x and the source corrects it to b@x before the confirming import: the
        // change is appended onto the sent Create, which must end up asking for b@x, not for a@x to be removed.
        var target = TargetAttribute("mail", AttributePlurality.SingleValued);
        var sent = Change(target, PendingExportAttributeChangeType.Update, "a@x");
        sent.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var create = PendingExportWith(PendingExportChangeType.Create, sent);

        _engine.MergeAttributeChangesIntoPendingExport(create,
            [Change(target, PendingExportAttributeChangeType.Add, "b@x"), Change(target, PendingExportAttributeChangeType.Remove, "a@x")]);

        Assert.That(create.AttributeValueChanges.Select(c => (c.ChangeType, c.StringValue)), Is.EqualTo(new[]
        {
            (PendingExportAttributeChangeType.Update, "b@x")
        }));
    }

    [Test]
    public void MergeAttributeChangesIntoPendingExport_OnlyRemovalsForASingleValuedTarget_KeepsOneClearingUpdate()
    {
        var target = TargetAttribute("mail", AttributePlurality.SingleValued);
        var staged = PendingExportWith(PendingExportChangeType.Update);

        _engine.MergeAttributeChangesIntoPendingExport(staged,
            [Change(target, PendingExportAttributeChangeType.Remove, "a@x"), Change(target, PendingExportAttributeChangeType.Remove, "c@x")]);

        Assert.That(staged.AttributeValueChanges, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(staged.AttributeValueChanges[0].ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update));
            Assert.That(SyncEngine.IsPendingChangeEmpty(staged.AttributeValueChanges[0]), Is.True);
        }
    }

    #endregion

    #region An Update against a multi-valued target is the whole set

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateAgainstAMultiValuedTargetHoldingAStrayValueAfterIt_IsNotCurrent()
    {
        // Comparing with the target's first value alone made a stray value's correction depend on value order.
        var target = TargetAttribute("mail", AttributePlurality.MultiValued);
        var update = Change(target, PendingExportAttributeChangeType.Update, "a@x");

        Assert.That(SyncEngine.IsCsoAttributeAlreadyCurrent(update, [Value(target, "a@x"), Value(target, "stray@x")]), Is.False);
    }

    [Test]
    public void IsAttributeChangeConfirmed_UpdateAgainstAMultiValuedTargetHoldingItAmongOthers_IsNotConfirmed()
    {
        // A related table that appended the value rather than replacing the set holds it among the old ones, and
        // confirming that as a success is what let the accumulation go unnoticed.
        var target = TargetAttribute("PhoneNumbers", AttributePlurality.MultiValued);
        var update = Change(target, PendingExportAttributeChangeType.Update, "b");

        var held = new Dictionary<int, List<ConnectedSystemObjectAttributeValue>>
        {
            [target.Id] = [Value(target, "a"), Value(target, "b")]
        };

        Assert.That(_engine.IsAttributeChangeConfirmed(held, update), Is.False);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_UpdateAgainstAMultiValuedTargetHoldingItAmongOthers_IsRetriedNotConfirmed()
    {
        var target = TargetAttribute("PhoneNumbers", AttributePlurality.MultiValued);
        var update = Change(target, PendingExportAttributeChangeType.Update, "b");
        update.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        update.ExportAttemptCount = 1;
        var pendingExport = PendingExportWith(PendingExportChangeType.Update, update);
        pendingExport.Status = PendingExportStatus.Exported;
        var cso = ConnectedSystemObjectHolding(Value(target, "a"), Value(target, "b"));
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConfirmedChanges, Is.Empty);
            Assert.That(result.RetryChanges, Has.Count.EqualTo(1));
        }
    }

    /// <summary>
    /// Pins the four places that judge an Update against what a multi-valued target holds to one answer: whether the
    /// target holds exactly that value (or nothing, for a clear). No-net-change detection, both confirmation paths and
    /// optimistic apply each once answered it their own way.
    /// </summary>
    [TestCase(new[] { "a" }, "a", true)]
    [TestCase(new[] { "a", "stray" }, "a", false)]
    [TestCase(new[] { "stray", "a" }, "a", false)]
    [TestCase(new[] { "stray" }, "a", false)]
    [TestCase(new string[0], "a", false)]
    [TestCase(new string[0], null, true)]
    [TestCase(new[] { "a" }, null, false)]
    public void ExportJudges_UpdateAgainstAMultiValuedTarget_AgreeItIsDoneOnlyWhenTheTargetHoldsExactlyThatValue(string[] held, string? updateValue, bool done)
    {
        var target = TargetAttribute("PhoneNumbers", AttributePlurality.MultiValued);
        ConnectedSystemObjectAttributeValue[] Held() => [.. held.Select(value => Value(target, value))];

        var alreadyCurrent = SyncEngine.IsCsoAttributeAlreadyCurrent(Change(target, PendingExportAttributeChangeType.Update, updateValue), Held());

        var confirmedByList = _engine.IsAttributeChangeConfirmed(
            new Dictionary<int, List<ConnectedSystemObjectAttributeValue>> { [target.Id] = [.. Held()] },
            Change(target, PendingExportAttributeChangeType.Update, updateValue));

        var sent = Change(target, PendingExportAttributeChangeType.Update, updateValue);
        sent.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        sent.ExportAttemptCount = 1;
        var exported = PendingExportWith(PendingExportChangeType.Update, sent);
        exported.Status = PendingExportStatus.Exported;
        var reconciliation = new PendingExportReconciliationResult();
        _engine.ReconcileCsoAgainstPendingExport(ConnectedSystemObjectHolding(Held()), exported, reconciliation);
        var confirmedByIndex = reconciliation.ConfirmedChanges.Count == 1;

        var applying = PendingExportWith(PendingExportChangeType.Update, Change(target, PendingExportAttributeChangeType.Update, updateValue));
        applying.ConnectedSystemObject = ConnectedSystemObjectHolding(Held());
        var delta = OptimisticExportApplyCalculator.CalculateDelta([applying]);
        var appliedAsNoChange = delta.Additions.Count == 0 && delta.RemovalValueIds.Count == 0;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(alreadyCurrent, Is.EqualTo(done), "no-net-change detection");
            Assert.That(confirmedByList, Is.EqualTo(done), "export confirmation");
            Assert.That(confirmedByIndex, Is.EqualTo(done), "export confirmation, indexed");
            Assert.That(appliedAsNoChange, Is.EqualTo(done), "optimistic apply");
        }
    }

    #endregion

    #region Helpers

    private static int _nextId = 1000;

    private static MetaverseAttribute MetaverseAttribute(string name, AttributePlurality plurality) => new()
    {
        Id = ++_nextId,
        Name = name,
        Type = AttributeDataType.Text,
        AttributePlurality = plurality
    };

    private static ConnectedSystemObjectTypeAttribute TargetAttribute(string name, AttributePlurality plurality, AttributeDataType type = AttributeDataType.Text) => new()
    {
        Id = ++_nextId,
        Name = name,
        Type = type,
        AttributePlurality = plurality
    };

    private static MetaverseObjectAttributeValue TextValue(MetaverseAttribute attribute, string value, bool persisted = false) => new()
    {
        Id = persisted ? Guid.NewGuid() : Guid.Empty,
        AttributeId = attribute.Id,
        Attribute = attribute,
        StringValue = value
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

    private static PendingExportAttributeValueChange Change(ConnectedSystemObjectTypeAttribute target, PendingExportAttributeChangeType changeType, string? value) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = target,
        AttributeId = target.Id,
        ChangeType = changeType,
        StringValue = value
    };

    private static ConnectedSystemObjectAttributeValue Value(ConnectedSystemObjectTypeAttribute attribute, string value) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = attribute,
        AttributeId = attribute.Id,
        StringValue = value
    };

    private static ConnectedSystemObject ConnectedSystemObjectHolding(params ConnectedSystemObjectAttributeValue[] values)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        foreach (var value in values)
        {
            value.ConnectedSystemObject = cso;
            cso.AttributeValues.Add(value);
        }

        return cso;
    }

    private static PendingExport PendingExportWith(PendingExportChangeType changeType, params PendingExportAttributeValueChange[] changes) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = 1,
        ConnectedSystemObjectId = Guid.NewGuid(),
        ChangeType = changeType,
        Status = PendingExportStatus.Pending,
        AttributeValueChanges = [.. changes]
    };

    #endregion
}
