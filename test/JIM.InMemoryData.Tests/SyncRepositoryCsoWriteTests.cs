// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;

namespace JIM.InMemoryData.Tests;

[TestFixture]
public class SyncRepositoryCsoWriteTests
{
    private SyncRepository _repo = null!;
    private const int CsId = 1;
    private const int AttrId = 10;
    private const int ObjectTypeId = 100;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private ConnectedSystemObject CreateCso(Guid? id = null, int connectedSystemId = CsId)
    {
        return new ConnectedSystemObject
        {
            Id = id ?? Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            TypeId = ObjectTypeId,
            ExternalIdAttributeId = AttrId,
            Created = DateTime.UtcNow,
            AttributeValues = new()
        };
    }

    [Test]
    public async Task CreateConnectedSystemObjectsAsync_AddsCsosToStoreAsync()
    {
        var cso = CreateCso();
        await _repo.CreateConnectedSystemObjectsAsync(new List<ConnectedSystemObject> { cso });

        var count = await _repo.GetConnectedSystemObjectCountAsync(CsId);
        Assert.That(count, Is.EqualTo(1));

        var retrieved = await _repo.GetConnectedSystemObjectAsync(CsId, cso.Id);
        Assert.That(retrieved, Is.Not.Null);
    }

    [Test]
    public async Task CreateConnectedSystemObjectsAsync_GeneratesIdWhenEmptyAsync()
    {
        var cso = CreateCso(id: Guid.Empty);
        await _repo.CreateConnectedSystemObjectsAsync(new List<ConnectedSystemObject> { cso });

        Assert.That(cso.Id, Is.Not.EqualTo(Guid.Empty));
        var count = await _repo.GetConnectedSystemObjectCountAsync(CsId);
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task CreateConnectedSystemObjectsAsync_WithRpeis_CallsCallbackAsync()
    {
        var cso = CreateCso();
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() };
        var callbackCalled = false;
        var callbackCount = 0;

        await _repo.CreateConnectedSystemObjectsAsync(
            new List<ConnectedSystemObject> { cso },
            new List<ActivityRunProfileExecutionItem> { rpei },
            count =>
            {
                callbackCalled = true;
                callbackCount = count;
                return Task.CompletedTask;
            });

        Assert.That(callbackCalled, Is.True);
        Assert.That(callbackCount, Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateConnectedSystemObjectsAsync_UpdatesInStoreAsync()
    {
        var cso = CreateCso();
        _repo.SeedConnectedSystemObject(cso);

        cso.LastUpdated = DateTime.UtcNow;
        await _repo.UpdateConnectedSystemObjectsAsync(new List<ConnectedSystemObject> { cso });

        var retrieved = await _repo.GetConnectedSystemObjectAsync(CsId, cso.Id);
        Assert.That(retrieved!.LastUpdated, Is.Not.Null);
    }

    [Test]
    public async Task UpdateConnectedSystemObjectsAsync_GivenDifferentInstanceSameId_AppliesFieldsWithoutAliasingStoreAsync()
    {
        // Guards against #1079 Regression B: a caller (SyncImportTaskProcessor) that hydrates a
        // working copy of a CSO via GetConnectedSystemObjectsByIdsAsync (now an independent clone,
        // not the store's live instance), applies a diff, and calls UpdateConnectedSystemObjectsAsync
        // with that clone. The store must apply the clone's field values onto its OWN canonical
        // instance (by Id) rather than aliasing its dictionary slot to the clone - otherwise, when
        // the caller later releases the clone's AttributeValues to bound memory, the store's copy
        // would be emptied too.
        var seeded = CreateCso();
        seeded.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = AttrId,
            StringValue = "original-value"
        });
        _repo.SeedConnectedSystemObject(seeded);

        // A different instance, same Id - simulating a hydrated clone, not the store's own object.
        var workingCopy = new ConnectedSystemObject
        {
            Id = seeded.Id,
            ConnectedSystemId = CsId,
            TypeId = ObjectTypeId,
            ExternalIdAttributeId = AttrId,
            Created = seeded.Created,
            LastUpdated = DateTime.UtcNow,
            AttributeValues = new(seeded.AttributeValues)
            {
                new() { Id = Guid.NewGuid(), AttributeId = AttrId, StringValue = "added-value" }
            }
        };

        await _repo.UpdateConnectedSystemObjectsAsync(new List<ConnectedSystemObject> { workingCopy });

        var afterUpdate = await _repo.GetConnectedSystemObjectAsync(CsId, seeded.Id);
        Assert.That(afterUpdate!.AttributeValues, Has.Count.EqualTo(2),
            "The store must reflect the working copy's changes.");

        // The caller now releases its own working copy - the store must not be aliased to it.
        workingCopy.AttributeValues = new();

        var afterRelease = await _repo.GetConnectedSystemObjectAsync(CsId, seeded.Id);
        Assert.That(afterRelease!.AttributeValues, Has.Count.EqualTo(2),
            "The store's canonical instance must not alias the caller's working copy.");
    }

    [Test]
    public async Task UpdateConnectedSystemObjectJoinStatesAsync_UpdatesJoinFieldsAsync()
    {
        var mvoId = Guid.NewGuid();
        var cso = CreateCso();
        _repo.SeedConnectedSystemObject(cso);

        cso.MetaverseObjectId = mvoId;
        cso.JoinType = ConnectedSystemObjectJoinType.Joined;
        await _repo.UpdateConnectedSystemObjectJoinStatesAsync(new List<ConnectedSystemObject> { cso });

        var joinedSystemIds = await _repo.GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(mvoId);
        Assert.That(joinedSystemIds, Is.EqualTo(new[] { CsId }));
    }

    [Test]
    public async Task UpdateConnectedSystemObjectsWithNewAttributeValuesAsync_AddsValuesAsync()
    {
        var cso = CreateCso();
        _repo.SeedConnectedSystemObject(cso);

        var newAv = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = 99,
            StringValue = "new-value"
        };

        await _repo.UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(
            new List<(ConnectedSystemObject, List<ConnectedSystemObjectAttributeValue>)>
            {
                (cso, new List<ConnectedSystemObjectAttributeValue> { newAv })
            });

        var retrieved = await _repo.GetConnectedSystemObjectAsync(CsId, cso.Id);
        Assert.That(retrieved!.AttributeValues, Has.Count.EqualTo(1));
        Assert.That(retrieved.AttributeValues[0].StringValue, Is.EqualTo("new-value"));
    }

    [Test]
    public async Task DeleteConnectedSystemObjectsAsync_RemovesFromStoreAsync()
    {
        var cso = CreateCso();
        _repo.SeedConnectedSystemObject(cso);

        await _repo.DeleteConnectedSystemObjectsAsync(new List<ConnectedSystemObject> { cso });

        var count = await _repo.GetConnectedSystemObjectCountAsync(CsId);
        Assert.That(count, Is.EqualTo(0));
    }

    /// <summary>
    /// Mirrors the Postgres fixup: an unresolved reference resolves to the object whose Secondary External ID it names,
    /// ignoring case unless that attribute is case-exact (#2051).
    /// </summary>
    [TestCase("CN=Target,DC=test", false, true)]
    [TestCase("cn=target,dc=test", false, true)]
    [TestCase("cn=target,dc=test", true, false)]
    [TestCase("CN=Target,DC=test", true, true)]
    public async Task FixupCrossBatchReferenceIdsAsync_ResolvesReferencesBySecondaryExternalIdAsync(string reference, bool caseExact, bool expectResolved)
    {
        const int secondaryAttrId = 11;
        _repo.SeedObjectType(new ConnectedSystemObjectType
        {
            Id = ObjectTypeId,
            Name = "user",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = secondaryAttrId, Name = "dn", Type = AttributeDataType.Text, IsSecondaryExternalId = true, CaseExact = caseExact }
            ]
        });

        var targetCso = CreateCso();
        targetCso.SecondaryExternalIdAttributeId = secondaryAttrId;
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = secondaryAttrId,
            StringValue = "CN=Target,DC=test"
        });
        _repo.SeedConnectedSystemObject(targetCso);

        var sourceCso = CreateCso();
        sourceCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = 20,
            UnresolvedReferenceValue = reference
        });
        _repo.SeedConnectedSystemObject(sourceCso);

        var resolved = await _repo.FixupCrossBatchReferenceIdsAsync(CsId);

        var updatedSource = await _repo.GetConnectedSystemObjectAsync(CsId, sourceCso.Id);
        var refAv = updatedSource!.AttributeValues.First(av => av.AttributeId == 20);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.EqualTo(expectResolved ? 1 : 0));
            Assert.That(refAv.ReferenceValueId, Is.EqualTo(expectResolved ? targetCso.Id : null));
        }
    }

    #region Change record references (#2690)

    private const string ManagerDn = "CN=Mo Manager,OU=People,DC=glitterband,DC=local";

    /// <summary>
    /// Mirrors the PostgreSQL change record writer: a Reference value holding a DN is marked unresolved unless the
    /// object it refers to is already stored; nothing else is marked.
    /// </summary>
    [Test]
    public async Task PersistRpeiCsoChangesAsync_ReferenceValues_MarksOnlyThoseWithoutAStoredObjectAsync()
    {
        var stored = SeedPersonWithDn(ManagerDn);
        var (rpei, managerChange) = NewChangeRecord(AttributeDataType.Reference);
        var noObject = AddValue(managerChange, ManagerDn);
        var storedObject = AddValue(managerChange, ManagerDn, stored);
        var objectNotStoredYet = AddValue(managerChange, ManagerDn, new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = CsId });
        var descriptionChange = AddAttributeChange(rpei.ConnectedSystemObjectChange!, AttributeDataType.Text, "description");
        var text = AddValue(descriptionChange, ManagerDn);

        await PersistAsync(rpei);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(noObject.IsUnresolvedReference, Is.True, "a reference that named no object");
            Assert.That(storedObject.IsUnresolvedReference, Is.False, "a reference to a stored object is resolved");
            Assert.That(objectNotStoredYet.IsUnresolvedReference, Is.True, "a reference to an object a later batch stores");
            Assert.That(text.IsUnresolvedReference, Is.False, "a Text value is never a reference");
        }
    }

    [Test]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_MarkedReference_ResolvesItAndClearsTheMarkAsync()
    {
        var (rpei, managerChange) = NewChangeRecord(AttributeDataType.Reference);
        var value = AddValue(managerChange, ManagerDn.ToLowerInvariant());
        await PersistAsync(rpei);
        var manager = SeedPersonWithDn(ManagerDn);

        var resolved = await _repo.FixupCrossBatchChangeRecordReferenceIdsAsync(CsId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.EqualTo(1));
            Assert.That(value.ReferenceValue, Is.SameAs(manager));
            Assert.That(value.IsUnresolvedReference, Is.False);
        }
    }

    [Test]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_MarkedReferenceNamingNoObject_StaysMarkedAsync()
    {
        SeedPersonWithDn(ManagerDn);
        var (rpei, managerChange) = NewChangeRecord(AttributeDataType.Reference);
        var value = AddValue(managerChange, "CN=Nobody,OU=People,DC=glitterband,DC=local");
        await PersistAsync(rpei);

        var resolved = await _repo.FixupCrossBatchChangeRecordReferenceIdsAsync(CsId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.Zero);
            Assert.That(value.ReferenceValue, Is.Null);
            Assert.That(value.IsUnresolvedReference, Is.True);
        }
    }

    /// <summary>
    /// Mirrors the PostgreSQL fix-up reading only marked rows: a value holding a DN that was never marked (a reference
    /// once written with its object, whose object has since gone) is not re-pointed at whichever object now has the DN.
    /// </summary>
    [Test]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_UnmarkedValueHoldingADn_IsLeftAloneAsync()
    {
        SeedPersonWithDn(ManagerDn);
        var (rpei, managerChange) = NewChangeRecord(AttributeDataType.Reference);
        var value = AddValue(managerChange, ManagerDn);
        await _repo.BulkInsertRpeisAsync([rpei]);

        var resolved = await _repo.FixupCrossBatchChangeRecordReferenceIdsAsync(CsId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.Zero);
            Assert.That(value.ReferenceValue, Is.Null);
        }
    }

    /// <summary>
    /// A person whose Secondary External ID is the given DN.
    /// </summary>
    private ConnectedSystemObject SeedPersonWithDn(string dn)
    {
        var dnAttribute = new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true };
        var person = CreateCso();
        person.SecondaryExternalIdAttributeId = dnAttribute.Id;
        person.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), Attribute = dnAttribute, AttributeId = dnAttribute.Id, StringValue = dn, ConnectedSystemObject = person
        });
        _repo.SeedConnectedSystemObject(person);
        return person;
    }

    private static (ActivityRunProfileExecutionItem Rpei, ConnectedSystemObjectChangeAttribute AttributeChange) NewChangeRecord(AttributeDataType attributeType)
    {
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = CsId, ChangeType = ObjectChangeType.Added, ChangeTime = DateTime.UtcNow, InitiatedByType = ActivityInitiatorType.System
        };
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid(), ConnectedSystemObjectChange = change };
        return (rpei, AddAttributeChange(change, attributeType, "manager"));
    }

    private static ConnectedSystemObjectChangeAttribute AddAttributeChange(ConnectedSystemObjectChange change, AttributeDataType attributeType, string name)
    {
        var attributeChange = new ConnectedSystemObjectChangeAttribute { AttributeName = name, AttributeType = attributeType, ConnectedSystemChange = change };
        change.AttributeChanges.Add(attributeChange);
        return attributeChange;
    }

    private static ConnectedSystemObjectChangeAttributeValue AddValue(
        ConnectedSystemObjectChangeAttribute attributeChange, string stringValue, ConnectedSystemObject? referenceValue = null)
    {
        var value = new ConnectedSystemObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, stringValue) { ReferenceValue = referenceValue };
        attributeChange.ValueChanges.Add(value);
        return value;
    }

    /// <summary>
    /// Stores an execution item and its change record as an import's flush does.
    /// </summary>
    private async Task PersistAsync(ActivityRunProfileExecutionItem rpei)
    {
        await _repo.BulkInsertRpeisAsync([rpei]);
        await _repo.PersistRpeiCsoChangesAsync([rpei]);
    }

    #endregion
}
