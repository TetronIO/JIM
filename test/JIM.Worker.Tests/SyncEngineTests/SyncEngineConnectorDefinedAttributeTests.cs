// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Exchange;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// How the engine treats a connector-defined attribute (an Exchange recipient's declared state, #2021): it is never
/// written to the directory, so it is staged and confirmed on terms of its own.
/// <list type="bullet">
/// <item>An empty declared value means "let Exchange decide" (Exchange chooses the database, Email Address Policies the
/// primary SMTP address, <c>mailNickname</c> gives the alias), never "clear it": no flow ever stages a clearing change
/// for one, and a change that sets no value is confirmed whatever the import shows.</item>
/// <item>A change Exchange still owes (<see cref="PendingExportAttributeValueChange.AwaitsExchange"/>) is held, neither
/// confirmed nor retried nor reported, while the object has a change on the Exchange queue, and judged as any other
/// change once it has none.</item>
/// </list>
/// </summary>
[TestFixture]
public class SyncEngineConnectorDefinedAttributeTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    #region An empty declared value lets Exchange decide

    [Test]
    public void ComputeAttributeValueChanges_ConnectorDefinedTargetWhoseSourceLosesItsValue_StagesNoClearingChange()
    {
        var source = MetaverseAttribute("Mailbox Database");
        var target = ConnectorDefined(ExchangeRecipientAttributes.Database);
        var removed = TextValue(source, "MDB-VIP", persisted: true);
        var mvo = MetaverseObjectHolding();

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [removed],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _, removedAttributes: [removed]);

        Assert.That(changes, Is.Empty, "an empty Exchange Database means Exchange chooses one, so the flow has nothing to say");
    }

    [Test]
    public void ComputeAttributeValueChanges_ConnectorDefinedTargetWithNothingToFlowOnCreate_StagesNoClearingChange()
    {
        var source = MetaverseAttribute("Primary Email");
        var target = ConnectorDefined(ExchangeRecipientAttributes.PrimarySmtpAddress);
        var mvo = MetaverseObjectHolding();

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [],
            PendingExportChangeType.Create, existingCso: null, csoAttributeCache: null, out _);

        Assert.That(changes, Is.Empty);
    }

    [Test]
    public void ComputeAttributeValueChanges_ConnectorDefinedTargetWithAnEmptyTextValue_StagesNoClearingChange()
    {
        var source = MetaverseAttribute("Alias");
        var target = ConnectorDefined(ExchangeRecipientAttributes.Alias);
        var blank = TextValue(source, string.Empty);
        var mvo = MetaverseObjectHolding(blank);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [blank],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _);

        Assert.That(changes, Is.Empty, "an empty alias means mailNickname is used");
    }

    [Test]
    public void ComputeAttributeValueChanges_ConnectorDefinedTargetWithAValue_StillStagesIt()
    {
        var source = MetaverseAttribute("Mailbox Database");
        var target = ConnectorDefined(ExchangeRecipientAttributes.Database);
        var value = TextValue(source, "MDB-VIP");
        var mvo = MetaverseObjectHolding(value);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [value],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _);

        Assert.That(changes.Select(c => (c.ChangeType, c.StringValue)), Is.EqualTo(new[] { (PendingExportAttributeChangeType.Update, (string?)"MDB-VIP") }));
    }

    [Test]
    public void ComputeAttributeValueChanges_DirectoryAttributeWhoseSourceLosesItsValue_StillStagesTheClear()
    {
        var source = MetaverseAttribute("Description");
        var target = TargetAttribute("description");
        var removed = TextValue(source, "Old", persisted: true);
        var mvo = MetaverseObjectHolding();

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [removed],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _, removedAttributes: [removed]);

        Assert.That(changes.Count(SyncEngine.IsPendingChangeEmpty), Is.EqualTo(1), "only connector-defined attributes are exempt");
    }

    [TestCase(null)]
    [TestCase("")]
    public void ReconcileCsoAgainstPendingExport_ConnectorDefinedChangeThatSetsNoValue_IsConfirmedWhateverTheImportShows(string? declared)
    {
        var target = ConnectorDefined(ExchangeRecipientAttributes.Database);
        var change = SentChange(target, declared);
        var pendingExport = ExportedWith(change);
        var cso = ConnectedSystemObjectHolding(Value(target, "MDB-01"));
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, connectedSystemObjectIdsWithExchangeChange: new HashSet<Guid>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConfirmedChanges, Is.EqualTo(new[] { change }), "Exchange chose the database, which is what an empty value asks for");
            Assert.That(result.RetryChanges, Is.Empty);
            Assert.That(result.PendingExportDeleted, Is.True);
        }
    }

    #endregion

    #region Held while Exchange owes the change

    [Test]
    public void ReconcileCsoAgainstPendingExport_ChangeAwaitingExchangeWithAQueuedChange_IsHeldNeitherConfirmedNorRetried()
    {
        var target = ConnectorDefined(ExchangeRecipientAttributes.RecipientType);
        var change = AwaitingExchange(target, ExchangeRecipientTypes.Mailbox);
        var pendingExport = ExportedWith(change);
        var cso = ConnectedSystemObjectHolding(Value(target, ExchangeRecipientTypes.None));
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, connectedSystemObjectIdsWithExchangeChange: new HashSet<Guid> { cso.Id });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HeldForExchangeChanges, Is.EqualTo(new[] { change }));
            Assert.That(result.ConfirmedChanges, Is.Empty);
            Assert.That(result.RetryChanges, Is.Empty, "Exchange has not acted yet, which is not a failed export");
            Assert.That(result.HasChanges, Is.False, "a hold changes nothing, so nothing is persisted or reported");
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(change.AwaitsExchange, Is.True);
            Assert.That(change.LastImportedValue, Is.Null);
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Exported), "still outstanding, and nothing to send");
            Assert.That(result.PendingExportDeleted, Is.False);
            Assert.That(pendingExport.AttributeValueChanges, Is.EqualTo(new[] { change }));
        }
    }

    /// <summary>
    /// Every status holds: Pending and Delivering are on their way, Parked waits for a person, Cancelled and Refused hold
    /// until the declared state changes. The queue row's status is the lane's business; reconciliation only asks
    /// whether one exists.
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_ChangeAwaitingExchangeWithNoQueueLookup_IsHeld()
    {
        var target = ConnectorDefined(ExchangeRecipientAttributes.RecipientType);
        var change = AwaitingExchange(target, ExchangeRecipientTypes.Mailbox);
        var pendingExport = ExportedWith(change);
        var cso = ConnectedSystemObjectHolding();
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, connectedSystemObjectIdsWithExchangeChange: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HeldForExchangeChanges, Is.EqualTo(new[] { change }), "not knowing whether Exchange still owes it, the safe answer is to wait");
            Assert.That(result.RetryChanges, Is.Empty);
        }
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_ChangeAwaitingExchangeThatTheImportShows_IsConfirmedEvenWithAQueuedChange()
    {
        var target = ConnectorDefined(ExchangeRecipientAttributes.RecipientType);
        var change = AwaitingExchange(target, ExchangeRecipientTypes.Mailbox);
        var pendingExport = ExportedWith(change);
        var cso = ConnectedSystemObjectHolding(Value(target, ExchangeRecipientTypes.Mailbox));
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, connectedSystemObjectIdsWithExchangeChange: new HashSet<Guid> { cso.Id });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConfirmedChanges, Is.EqualTo(new[] { change }));
            Assert.That(result.HeldForExchangeChanges, Is.Empty);
            Assert.That(result.PendingExportDeleted, Is.True);
        }
    }

    /// <summary>
    /// Once the object has no queued change (the lane deletes a delivered one), the change is judged against what the
    /// import shows, as any change is: here Exchange delivered something other than what was declared, so it is not
    /// confirmed, reported, and sent again, which re-stages it.
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_ChangeAwaitingExchangeWithNoQueuedChangeAndNotShown_IsReleasedToBeRetried()
    {
        var target = ConnectorDefined(ExchangeRecipientAttributes.Alias);
        var change = AwaitingExchange(target, "alice.smith");
        var pendingExport = ExportedWith(change);
        var cso = ConnectedSystemObjectHolding(Value(target, "asmith"));
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, connectedSystemObjectIdsWithExchangeChange: new HashSet<Guid>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetryChanges, Is.EqualTo(new[] { change }));
            Assert.That(result.HeldForExchangeChanges, Is.Empty);
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));
            Assert.That(change.AwaitsExchange, Is.False, "released: from here it is an ordinary unconfirmed change");
            Assert.That(change.LastImportedValue, Is.EqualTo("asmith"));
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
        }
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_ChangeAwaitingExchangeWithNoQueuedChangeAndShown_IsConfirmed()
    {
        var target = ConnectorDefined(ExchangeRecipientAttributes.Alias);
        var change = AwaitingExchange(target, "alice.smith");
        var pendingExport = ExportedWith(change);
        var cso = ConnectedSystemObjectHolding(Value(target, "alice.smith"));
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, connectedSystemObjectIdsWithExchangeChange: new HashSet<Guid>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConfirmedChanges, Is.EqualTo(new[] { change }));
            Assert.That(result.PendingExportDeleted, Is.True);
        }
    }

    /// <summary>
    /// A held change does not block the object's other changes: a confirmed one is confirmed and removed, and the
    /// Pending Export stays outstanding for the held one alone, with nothing to send.
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_HeldChangeBesideAConfirmedDirectoryChange_ConfirmsTheOtherAndKeepsTheExportOutstanding()
    {
        var recipientType = ConnectorDefined(ExchangeRecipientAttributes.RecipientType);
        var description = TargetAttribute("description");
        var held = AwaitingExchange(recipientType, ExchangeRecipientTypes.Mailbox);
        var ordinary = SentChange(description, "Finance");
        var pendingExport = ExportedWith(held, ordinary);
        var cso = ConnectedSystemObjectHolding(Value(recipientType, ExchangeRecipientTypes.None), Value(description, "Finance"));
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, connectedSystemObjectIdsWithExchangeChange: new HashSet<Guid> { cso.Id });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConfirmedChanges, Is.EqualTo(new[] { ordinary }));
            Assert.That(result.HeldForExchangeChanges, Is.EqualTo(new[] { held }));
            Assert.That(result.RetryChanges, Is.Empty);
            Assert.That(result.PendingExportDeleted, Is.False);
            Assert.That(result.PendingExportToUpdate, Is.SameAs(pendingExport));
            Assert.That(pendingExport.AttributeValueChanges, Is.EqualTo(new[] { held }));
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(ExportExecutionServer.CanWriteInPart(pendingExport), Is.False, "the held change is never sent again while it waits");
        }
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_HeldChangeBesideAnUnconfirmedDirectoryChange_RetriesOnlyTheOther()
    {
        var recipientType = ConnectorDefined(ExchangeRecipientAttributes.RecipientType);
        var description = TargetAttribute("description");
        var held = AwaitingExchange(recipientType, ExchangeRecipientTypes.Mailbox);
        var ordinary = SentChange(description, "Finance");
        var pendingExport = ExportedWith(held, ordinary);
        var cso = ConnectedSystemObjectHolding(Value(description, "Sales"));
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, connectedSystemObjectIdsWithExchangeChange: new HashSet<Guid> { cso.Id });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetryChanges, Is.EqualTo(new[] { ordinary }));
            Assert.That(result.HeldForExchangeChanges, Is.EqualTo(new[] { held }));
            Assert.That(held.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
            Assert.That(ExportExecutionServer.WritableChanges(pendingExport), Is.EqualTo(new[] { ordinary }),
                "the re-send carries the directory change only");
        }
    }

    /// <summary>
    /// The hold applies only while the change awaits its confirming import. A flagged change already judged not
    /// confirmed is an ordinary change.
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_FlaggedChangeNoLongerAwaitingConfirmation_IsJudgedAsAnyOther()
    {
        var target = ConnectorDefined(ExchangeRecipientAttributes.RecipientType);
        var change = AwaitingExchange(target, ExchangeRecipientTypes.Mailbox);
        change.Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed;
        var pendingExport = ExportedWith(change);
        pendingExport.Status = PendingExportStatus.ExportNotConfirmed;
        var cso = ConnectedSystemObjectHolding(Value(target, ExchangeRecipientTypes.None));
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, connectedSystemObjectIdsWithExchangeChange: new HashSet<Guid> { cso.Id });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncEngine.IsAwaitingExchange(change), Is.False);
            Assert.That(result.RetryChanges, Is.EqualTo(new[] { change }));
            Assert.That(result.HeldForExchangeChanges, Is.Empty);
        }
    }

    #endregion

    #region Export evaluation and Drift Detection leave a held change alone

    /// <summary>
    /// The declared value is never recorded on the object at export, so every re-evaluation of the person until Exchange
    /// acts finds the attribute "changed" and restates it. Merged in, the restatement would supersede the held change
    /// and go out again as a Pending write, re-staging the same declared state on every synchronisation.
    /// </summary>
    [Test]
    public void WithoutChangesRestatingAHeldChange_RestatementOfAChangeAwaitingExchange_IsDropped()
    {
        var target = ConnectorDefined(ExchangeRecipientAttributes.RecipientType);
        var held = AwaitingExchange(target, ExchangeRecipientTypes.Mailbox);
        var restatement = SentChange(target, ExchangeRecipientTypes.Mailbox);
        restatement.Status = PendingExportAttributeChangeStatus.Pending;
        var genuine = SentChange(ConnectorDefined(ExchangeRecipientAttributes.Alias), "ada");
        genuine.Status = PendingExportAttributeChangeStatus.Pending;

        var kept = SyncEngine.WithoutChangesRestatingAHeldChange(new List<PendingExportAttributeValueChange> { restatement, genuine }, [held]);

        Assert.That(kept, Is.EqualTo(new[] { genuine }));
    }

    [Test]
    public void WithoutChangesRestatingAHeldChange_DifferentValueForAChangeAwaitingExchange_IsKept()
    {
        var target = ConnectorDefined(ExchangeRecipientAttributes.RecipientType);
        var held = AwaitingExchange(target, ExchangeRecipientTypes.Mailbox);
        var newer = SentChange(target, ExchangeRecipientTypes.SharedMailbox);
        newer.Status = PendingExportAttributeChangeStatus.Pending;

        var kept = SyncEngine.WithoutChangesRestatingAHeldChange(new List<PendingExportAttributeValueChange> { newer }, [held]);

        Assert.That(kept, Is.EqualTo(new[] { newer }), "a genuinely new declared state supersedes the held one");
    }

    [Test]
    public void CarryOverSurvivingChange_ChangeAwaitingExchange_StaysAwaitingExchange()
    {
        var held = AwaitingExchange(ConnectorDefined(ExchangeRecipientAttributes.RecipientType), ExchangeRecipientTypes.Mailbox);

        var carried = SyncEngine.CarryOverSurvivingChange(held);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(carried.AwaitsExchange, Is.True);
            Assert.That(carried.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        }
    }

    #endregion

    #region Judged when the import did not return the object

    /// <summary>
    /// A queued change withdrawn or deleted without Exchange touching the object leaves nothing a Delta Import would
    /// report, so the hold is judged after reconciliation against what the last import read, which for a
    /// connector-defined attribute is all JIM records of it: optimistic apply never writes one.
    /// </summary>
    [Test]
    public void JudgeChangesAwaitingExchangeNotReconciled_StoredValueShowsIt_ConfirmsAndRemovesTheExport()
    {
        var target = ConnectorDefined(ExchangeRecipientAttributes.RecipientType);
        var change = AwaitingExchange(target, ExchangeRecipientTypes.Mailbox);
        var pendingExport = ExportedWith(change);
        var cso = ConnectedSystemObjectHolding(Value(target, ExchangeRecipientTypes.Mailbox));
        var result = new PendingExportReconciliationResult();

        _engine.JudgeChangesAwaitingExchangeNotReconciled(cso, pendingExport, result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConfirmedChanges, Is.EqualTo(new[] { change }));
            Assert.That(result.PendingExportToDelete, Is.SameAs(pendingExport));
            Assert.That(result.PendingExportDeleted, Is.True);
        }
    }

    [Test]
    public void JudgeChangesAwaitingExchangeNotReconciled_StoredValueDoesNotShowIt_ReleasesItToBeSentAgain()
    {
        var recipientType = ConnectorDefined(ExchangeRecipientAttributes.RecipientType);
        var description = TargetAttribute("description");
        var held = AwaitingExchange(recipientType, ExchangeRecipientTypes.Mailbox);
        var ordinary = SentChange(description, "Finance");
        var pendingExport = ExportedWith(held, ordinary);
        var cso = ConnectedSystemObjectHolding(Value(recipientType, ExchangeRecipientTypes.None), Value(description, "Finance"));
        var result = new PendingExportReconciliationResult();

        _engine.JudgeChangesAwaitingExchangeNotReconciled(cso, pendingExport, result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetryChanges, Is.EqualTo(new[] { held }));
            Assert.That(held.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));
            Assert.That(held.AwaitsExchange, Is.False);
            Assert.That(ordinary.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
                "the import did not read the object, so nothing but the hold is judged");
            Assert.That(result.ConfirmedChanges, Is.Empty);
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
            Assert.That(result.PendingExportToUpdate, Is.SameAs(pendingExport));
        }
    }

    /// <summary>
    /// Confirming a Create is what turns it into an Update; with no import showing the object, a Create re-sent would
    /// create it a second time. It is left exactly as it is for the import that confirms it.
    /// </summary>
    [TestCase(PendingExportChangeType.Create, PendingExportStatus.Exported)]
    [TestCase(PendingExportChangeType.Update, PendingExportStatus.Parked)]
    [TestCase(PendingExportChangeType.Update, PendingExportStatus.Executing)]
    public void JudgeChangesAwaitingExchangeNotReconciled_ExportReconciliationLeavesAlone_IsLeftAsItIs(PendingExportChangeType changeType, PendingExportStatus status)
    {
        var target = ConnectorDefined(ExchangeRecipientAttributes.RecipientType);
        var change = AwaitingExchange(target, ExchangeRecipientTypes.Mailbox);
        var pendingExport = ExportedWith(change);
        pendingExport.ChangeType = changeType;
        pendingExport.Status = status;
        var cso = ConnectedSystemObjectHolding();
        var result = new PendingExportReconciliationResult();

        _engine.JudgeChangesAwaitingExchangeNotReconciled(cso, pendingExport, result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasChanges, Is.False);
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(change.AwaitsExchange, Is.True);
            Assert.That(pendingExport.Status, Is.EqualTo(status));
        }
    }

    #endregion

    #region Helpers

    private static int _nextId = 5000;

    private static MetaverseAttribute MetaverseAttribute(string name) => new()
    {
        Id = ++_nextId,
        Name = name,
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued
    };

    private static ConnectedSystemObjectTypeAttribute TargetAttribute(string name) => new()
    {
        Id = ++_nextId,
        Name = name,
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued
    };

    private static ConnectedSystemObjectTypeAttribute ConnectorDefined(string name)
    {
        var attribute = TargetAttribute(name);
        attribute.IsConnectorDefined = true;
        return attribute;
    }

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

    /// <summary>A change sent and awaiting its confirming import.</summary>
    private static PendingExportAttributeValueChange SentChange(ConnectedSystemObjectTypeAttribute target, string? value) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = target,
        AttributeId = target.Id,
        ChangeType = PendingExportAttributeChangeType.Update,
        StringValue = value,
        Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
        ExportAttemptCount = 1
    };

    private static PendingExportAttributeValueChange AwaitingExchange(ConnectedSystemObjectTypeAttribute target, string value)
    {
        var change = SentChange(target, value);
        change.AwaitsExchange = true;
        return change;
    }

    private static ConnectedSystemObjectAttributeValue Value(ConnectedSystemObjectTypeAttribute attribute, string value) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = attribute,
        AttributeId = attribute.Id,
        StringValue = value
    };

    private static ConnectedSystemObject ConnectedSystemObjectHolding(params ConnectedSystemObjectAttributeValue[] values)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObjectId = Guid.NewGuid() };
        foreach (var value in values)
        {
            value.ConnectedSystemObject = cso;
            cso.AttributeValues.Add(value);
        }

        return cso;
    }

    private static PendingExport ExportedWith(params PendingExportAttributeValueChange[] changes) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = 1,
        ConnectedSystemObjectId = Guid.NewGuid(),
        ChangeType = PendingExportChangeType.Update,
        Status = PendingExportStatus.Exported,
        AttributeValueChanges = [.. changes]
    };

    #endregion
}
