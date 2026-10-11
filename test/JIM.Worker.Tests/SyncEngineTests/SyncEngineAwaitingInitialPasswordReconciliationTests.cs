// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// The confirming-import rule for a change the Connector wrote with an interim value because an Initial Password
/// follows (#2039 Phase 2): Active Directory is given an account created disabled, the Pending Export still asserts
/// the enabled value the Synchronisation Rule asked for, and the delivery of the password is what enables it. Until
/// then the imported value differs from the asserted one, and reconciliation must neither retry the change (AD
/// refuses to enable an account with no password) nor report it as unconfirmed.
/// <para>
/// Pure engine tests: the set of Metaverse Objects still owed a password is the caller's to supply.
/// </para>
/// </summary>
[TestFixture]
public class SyncEngineAwaitingInitialPasswordReconciliationTests
{
    private const int UserAccountControlAttributeId = 7;
    private const int Enabled = 512;
    private const int Disabled = 514;

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new SyncEngine();
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_AwaitingChangeImportedWithTheAssertedValue_ConfirmsIt()
    {
        var (cso, pendingExport, change) = Arrange(importedValue: Enabled);
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, OwedTo(cso));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConfirmedChanges, Does.Contain(change));
            Assert.That(result.HeldChanges, Is.Empty);
            Assert.That(result.ReleasedChanges, Is.Empty);
            Assert.That(result.PendingExportDeleted, Is.True, "the only change is confirmed, so the Pending Export is done");
        }
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_AwaitingChangeWithInterimValueAndPasswordOwed_HoldsItUnchanged()
    {
        var (cso, pendingExport, change) = Arrange(importedValue: Disabled);
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, OwedTo(cso));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HeldChanges, Does.Contain(change));
            Assert.That(result.RetryChanges, Is.Empty, "a held change must never be retried: Active Directory would refuse it");
            Assert.That(result.FailedChanges, Is.Empty);
            Assert.That(result.ConfirmedChanges, Is.Empty);
            Assert.That(result.ReleasedChanges, Is.Empty);
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(change.AwaitsInitialPassword, Is.True);
            Assert.That(change.ExportAttemptCount, Is.EqualTo(1), "a hold consumes no attempt");
            Assert.That(change.LastImportedValue, Is.Null, "nothing about a held change is rewritten");
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Exported), "the export is not queued to be sent again");
            Assert.That(pendingExport.AttributeValueChanges, Does.Contain(change));
            Assert.That(result.PendingExportDeleted, Is.False);
        }
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_AwaitingChangeWithInterimValueAndNoPasswordOwed_ReleasesIt()
    {
        var (cso, pendingExport, change) = Arrange(importedValue: Disabled);
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, new HashSet<Guid>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ReleasedChanges, Does.Contain(change));
            Assert.That(result.RetryChanges, Is.Empty, "a release is not a failed confirmation and is not reported as one");
            Assert.That(result.FailedChanges, Is.Empty);
            Assert.That(result.HeldChanges, Is.Empty);
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending),
                "the next export writes the Synchronisation Rule's value as an ordinary Update");
            Assert.That(change.AwaitsInitialPassword, Is.False);
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Pending));
            Assert.That(result.PendingExportToUpdate, Is.SameAs(pendingExport), "the release must be persisted");
            Assert.That(result.HasChanges, Is.True);
        }
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_AwaitingChangeOnAnObjectWithNoMetaverseObject_ReleasesIt()
    {
        // A password is owed to a Metaverse Object; an account joined to none can be owed nothing.
        var (cso, pendingExport, change) = Arrange(importedValue: Disabled);
        var owed = OwedTo(cso);
        cso.MetaverseObjectId = null;
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, owed);

        Assert.That(result.ReleasedChanges, Does.Contain(change));
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_AwaitingChangeWhenTheCallerDidNotLookUpOwedPasswords_HoldsIt()
    {
        // A caller that did not ask which passwords are owed cannot tell a hold from a release; holding keeps the
        // account disabled, which is safe, where releasing would re-send a value Active Directory refuses.
        var (cso, pendingExport, change) = Arrange(importedValue: Disabled);
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HeldChanges, Does.Contain(change));
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        }
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_UnflaggedChangeWithDifferentValueAndPasswordOwed_RetriesAsToday()
    {
        var (cso, pendingExport, change) = Arrange(importedValue: Disabled);
        change.AwaitsInitialPassword = false;
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, OwedTo(cso));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetryChanges, Does.Contain(change), "the hold applies only to a flagged change");
            Assert.That(result.HeldChanges, Is.Empty);
            Assert.That(result.ReleasedChanges, Is.Empty);
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));
        }
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_HeldChangeBesideAnUnflaggedUnconfirmedChange_HoldsOneAndRetriesTheOther()
    {
        var (cso, pendingExport, held) = Arrange(importedValue: Disabled);
        var descriptionAttribute = new ConnectedSystemObjectTypeAttribute { Id = 8, Name = "description", Type = AttributeDataType.Text };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 8, Attribute = descriptionAttribute, StringValue = "old" });
        var other = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = 8, Attribute = descriptionAttribute, ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "new", Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation, ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(other);
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, OwedTo(cso));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HeldChanges, Is.EquivalentTo(new[] { held }));
            Assert.That(result.RetryChanges, Is.EquivalentTo(new[] { other }));
            Assert.That(held.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        }
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_HeldChangeOnAConfirmedCreate_TransitionsToUpdateAndKeepsTheHold()
    {
        // The Create's other attributes are confirmed, so the object exists: what remains is an Update, and the held
        // change stays on it, not sent, until the password arrives.
        var (cso, pendingExport, held) = Arrange(importedValue: Disabled);
        pendingExport.ChangeType = PendingExportChangeType.Create;
        var nameAttribute = new ConnectedSystemObjectTypeAttribute { Id = 9, Name = "sAMAccountName", Type = AttributeDataType.Text };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 9, Attribute = nameAttribute, StringValue = "jbloggs" });
        var confirmed = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = 9, Attribute = nameAttribute, ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "jbloggs", Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation, ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(confirmed);
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, OwedTo(cso));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConfirmedChanges, Is.EquivalentTo(new[] { confirmed }));
            Assert.That(result.HeldChanges, Is.EquivalentTo(new[] { held }));
            Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Exported),
                "nothing is left to send while the change is held");
            Assert.That(pendingExport.AttributeValueChanges, Is.EquivalentTo(new[] { held }));
            Assert.That(result.PendingExportToUpdate, Is.SameAs(pendingExport));
        }
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_HoldAlone_ReportsNothingToPersist()
    {
        // A hold changes nothing, so a confirming import that only holds has nothing to write.
        var (cso, pendingExport, _) = Arrange(importedValue: Disabled);
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result, OwedTo(cso));

        Assert.That(result.HasChanges, Is.False);
    }

    // ---- A hold the import did not reconcile (the account was not returned) ----

    [Test]
    public void JudgeChangesAwaitingInitialPasswordNotReconciled_NoPasswordOwed_ReleasesTheHeldChangeAndNothingElse()
    {
        var (cso, pendingExport, held) = Arrange(importedValue: Disabled);
        var unjudged = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = 99, ChangeType = PendingExportAttributeChangeType.Update, StringValue = "Jane",
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation, ExportAttemptCount = 1
        };
        pendingExport.AttributeValueChanges.Add(unjudged);
        var result = new PendingExportReconciliationResult();

        _engine.JudgeChangesAwaitingInitialPasswordNotReconciled(cso.Id, cso.MetaverseObjectId, pendingExport, result, new HashSet<Guid>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ReleasedChanges, Is.EqualTo(new[] { held }));
            Assert.That(held.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending));
            Assert.That(held.AwaitsInitialPassword, Is.False);
            Assert.That(unjudged.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
                "the import did not read the account, so nothing else is confirmed or retried");
            Assert.That(result.ConfirmedChanges.Concat(result.RetryChanges).Concat(result.FailedChanges), Is.Empty);
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Pending), "the released change goes out on the next export");
            Assert.That(result.PendingExportToUpdate, Is.SameAs(pendingExport));
        }
    }

    [Test]
    public void JudgeChangesAwaitingInitialPasswordNotReconciled_PasswordOwed_HoldsItWithNothingToPersist()
    {
        var (cso, pendingExport, held) = Arrange(importedValue: Disabled);
        var result = new PendingExportReconciliationResult();

        _engine.JudgeChangesAwaitingInitialPasswordNotReconciled(cso.Id, cso.MetaverseObjectId, pendingExport, result, OwedTo(cso));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HeldChanges, Is.EqualTo(new[] { held }));
            Assert.That(held.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(held.AwaitsInitialPassword, Is.True);
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(result.PendingExportToUpdate, Is.Null);
        }
    }

    [Test]
    public void JudgeChangesAwaitingInitialPasswordNotReconciled_CreateNoImportHasConfirmed_ReleasesTheChangeButKeepsTheCreateUnsent()
    {
        // Confirming a Create is what turns it into an Update. With no import to prove the object exists, a Pending
        // Create would be sent again; it keeps its status, and the released change goes out once an import confirms it.
        var (cso, pendingExport, held) = Arrange(importedValue: Disabled);
        pendingExport.ChangeType = PendingExportChangeType.Create;
        var result = new PendingExportReconciliationResult();

        _engine.JudgeChangesAwaitingInitialPasswordNotReconciled(cso.Id, cso.MetaverseObjectId, pendingExport, result, new HashSet<Guid>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending));
            Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Exported));
            Assert.That(result.PendingExportToUpdate, Is.SameAs(pendingExport), "the release itself is persisted");
        }
    }

    [TestCase(PendingExportStatus.Parked)]
    [TestCase(PendingExportStatus.Executing)]
    [TestCase(PendingExportStatus.Failed)]
    public void JudgeChangesAwaitingInitialPasswordNotReconciled_ExportReconciliationLeavesAlone_IsLeftAlone(PendingExportStatus status)
    {
        var (cso, pendingExport, held) = Arrange(importedValue: Disabled);
        pendingExport.Status = status;
        var result = new PendingExportReconciliationResult();

        _engine.JudgeChangesAwaitingInitialPasswordNotReconciled(cso.Id, cso.MetaverseObjectId, pendingExport, result, new HashSet<Guid>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(held.AwaitsInitialPassword, Is.True);
            Assert.That(result.PendingExportToUpdate, Is.Null);
        }
    }

    private static HashSet<Guid> OwedTo(ConnectedSystemObject cso) => [cso.MetaverseObjectId!.Value];

    /// <summary>
    /// A Connected System Object whose confirming import returned <paramref name="importedValue"/> for
    /// <c>userAccountControl</c>, and its Pending Export asserting the enabled value with the change flagged as
    /// written with an interim value.
    /// </summary>
    private static (ConnectedSystemObject Cso, PendingExport PendingExport, PendingExportAttributeValueChange Change) Arrange(int importedValue)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = UserAccountControlAttributeId, Name = "userAccountControl", Type = AttributeDataType.Number,
            AttributePlurality = AttributePlurality.SingleValued
        };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = 1, MetaverseObjectId = Guid.NewGuid() };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = UserAccountControlAttributeId, Attribute = attribute, IntValue = importedValue
        });

        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = UserAccountControlAttributeId,
            Attribute = attribute,
            ChangeType = PendingExportAttributeChangeType.Update,
            IntValue = Enabled,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ExportAttemptCount = 1,
            AwaitsInitialPassword = true
        };
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            ConnectedSystemObjectId = cso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Exported,
            AttributeValueChanges = [change]
        };
        return (cso, pendingExport, change);
    }
}
