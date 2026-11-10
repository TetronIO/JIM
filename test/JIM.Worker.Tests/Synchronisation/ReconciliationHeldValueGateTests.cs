// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Which objects a confirming import's reconciliation may judge from the values it holds once their batch is saved, rather
/// than reload (#2908): only one loaded whole by the run's hydration, holding after the save exactly what the save wrote,
/// and whose values nothing else in the run can have changed in the database.
/// </summary>
[TestFixture]
public class ReconciliationHeldValueGateTests
{
    private static readonly ConnectedSystemObjectTypeAttribute Mail = new()
    {
        Id = 1, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued
    };

    private static readonly ConnectedSystemObjectTypeAttribute Manager = new()
    {
        Id = 2, Name = "manager", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued
    };

    private static ConnectedSystemObjectAttributeValue Value(ConnectedSystemObjectTypeAttribute attribute, string value, bool saved = true) => new()
    {
        Id = saved ? Guid.NewGuid() : Guid.Empty, AttributeId = attribute.Id, Attribute = attribute,
        StringValue = attribute.Type == AttributeDataType.Text ? value : null,
        UnresolvedReferenceValue = attribute.Type == AttributeDataType.Reference ? value : null
    };

    /// <summary>
    /// An object holding <paramref name="held"/> about to save <paramref name="additions"/> and <paramref name="removals"/>:
    /// what it held before the save, and the object as the save leaves it when it applies them as written.
    /// </summary>
    private static (ConnectedSystemObject Cso, SyncImportTaskProcessor.ValuesBeforeSave BeforeSave) Saved(
        List<ConnectedSystemObjectAttributeValue> held, List<ConnectedSystemObjectAttributeValue> additions,
        List<ConnectedSystemObjectAttributeValue> removals)
    {
        var beforeSave = new SyncImportTaskProcessor.ValuesBeforeSave([.. held], [.. additions], [.. removals]);
        var removedIds = removals.Select(r => r.Id).ToHashSet();
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), AttributeValues = [.. held.Where(v => !removedIds.Contains(v.Id)), .. additions] };
        return (cso, beforeSave);
    }

    [Test]
    public void WhyNotHeldAsSaved_ChangesAppliedAsWritten_IsHeldAsSaved()
    {
        var oldMail = Value(Mail, "ada@old.example");
        var (cso, beforeSave) = Saved([oldMail, Value(Manager, "grace")], [Value(Mail, "ada@new.example")], [oldMail]);

        Assert.That(SyncImportTaskProcessor.WhyNotHeldAsSaved(cso, beforeSave, referencesResolvedSinceHydration: false), Is.Null);
    }

    [Test]
    public void WhyNotHeldAsSaved_NotLoadedWholeByTheRun_IsReloaded()
    {
        var (cso, _) = Saved([Value(Mail, "ada@old.example")], [], []);

        Assert.That(SyncImportTaskProcessor.WhyNotHeldAsSaved(cso, beforeSave: null, referencesResolvedSinceHydration: false),
            Is.EqualTo(ReconciliationReloadReason.NotLoadedWholeByThisRun));
    }

    [Test]
    public void WhyNotHeldAsSaved_ChangesWrittenButNotAppliedInMemory_IsReloaded()
    {
        // The save writes an object's pending changes whether or not it applies them to the object: one with no execution
        // item keeps what it held, while the database takes the change.
        var oldMail = Value(Mail, "ada@old.example");
        var beforeSave = new SyncImportTaskProcessor.ValuesBeforeSave([oldMail], [Value(Mail, "ada@new.example")], [oldMail]);
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), AttributeValues = [oldMail] };

        Assert.That(SyncImportTaskProcessor.WhyNotHeldAsSaved(cso, beforeSave, referencesResolvedSinceHydration: false),
            Is.EqualTo(ReconciliationReloadReason.ChangesNotAppliedAsSaved));
    }

    [Test]
    public void WhyNotHeldAsSaved_RemovesAValueNeverSaved_IsReloaded()
    {
        var added = Value(Mail, "ada@new.example", saved: false);
        var (cso, beforeSave) = Saved([Value(Mail, "ada@old.example")], [added], [added]);

        Assert.That(SyncImportTaskProcessor.WhyNotHeldAsSaved(cso, beforeSave, referencesResolvedSinceHydration: false),
            Is.EqualTo(ReconciliationReloadReason.RemovesAValueNotSaved));
    }

    [Test]
    public void WhyNotHeldAsSaved_OutstandingReferenceWhenTheFixUpResolvedReferences_IsReloaded()
    {
        var (cso, beforeSave) = Saved([Value(Mail, "ada@old.example"), Value(Manager, "zelda")], [], []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncImportTaskProcessor.WhyNotHeldAsSaved(cso, beforeSave, referencesResolvedSinceHydration: true),
                Is.EqualTo(ReconciliationReloadReason.HeldAnOutstandingReference), "the fix-up may have resolved it in the database");
            Assert.That(SyncImportTaskProcessor.WhyNotHeldAsSaved(cso, beforeSave, referencesResolvedSinceHydration: false), Is.Null,
                "nothing was resolved, so the database holds it outstanding too");
        }
    }

    [Test]
    public void WhyNotHeldAsSaved_LoadedInPart_IsReloaded()
    {
        var (cso, beforeSave) = Saved([Value(Mail, "ada@old.example")], [], []);
        cso.PartiallyLoadedAttributes = new Dictionary<int, PartiallyLoadedAttribute>
        {
            [Manager.Id] = new SynchronisationPartiallyLoadedAttribute(Manager.Id, Manager.Name, HoldsAny: true)
        };

        Assert.That(SyncImportTaskProcessor.WhyNotHeldAsSaved(cso, beforeSave, referencesResolvedSinceHydration: false),
            Is.EqualTo(ReconciliationReloadReason.LoadedInPart));
    }

    [Test]
    public void WhyNotHeldAsSaved_ValueWithoutAnAttributeId_IsReloaded()
    {
        var unnamed = Value(Mail, "ada@new.example");
        unnamed.AttributeId = 0;
        var (cso, beforeSave) = Saved([Value(Manager, "grace")], [unnamed], []);

        Assert.That(SyncImportTaskProcessor.WhyNotHeldAsSaved(cso, beforeSave, referencesResolvedSinceHydration: false),
            Is.EqualTo(ReconciliationReloadReason.ValueWithoutAttributeId));
    }
}
