// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
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
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
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
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>(seeded.AttributeValues)
            {
                new() { Id = Guid.NewGuid(), AttributeId = AttrId, StringValue = "added-value" }
            }
        };

        await _repo.UpdateConnectedSystemObjectsAsync(new List<ConnectedSystemObject> { workingCopy });

        var afterUpdate = await _repo.GetConnectedSystemObjectAsync(CsId, seeded.Id);
        Assert.That(afterUpdate!.AttributeValues, Has.Count.EqualTo(2),
            "The store must reflect the working copy's changes.");

        // The caller now releases its own working copy - the store must not be aliased to it.
        workingCopy.AttributeValues = new List<ConnectedSystemObjectAttributeValue>();

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

        var count = await _repo.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(mvoId);
        Assert.That(count, Is.EqualTo(1));
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

    [Test]
    public async Task FixupCrossBatchReferenceIdsAsync_ResolvesReferencesAsync()
    {
        var targetCso = CreateCso();
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = AttrId,
            StringValue = "target-ext-id"
        });
        _repo.SeedConnectedSystemObject(targetCso);

        var sourceCso = CreateCso();
        sourceCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = 20,
            UnresolvedReferenceValue = "target-ext-id"
        });
        _repo.SeedConnectedSystemObject(sourceCso);

        var resolved = await _repo.FixupCrossBatchReferenceIdsAsync(CsId);
        Assert.That(resolved, Is.EqualTo(1));

        var updatedSource = await _repo.GetConnectedSystemObjectAsync(CsId, sourceCso.Id);
        var refAv = updatedSource!.AttributeValues.First(av => av.AttributeId == 20);
        Assert.That(refAv.ReferenceValueId, Is.EqualTo(targetCso.Id));
    }
}
