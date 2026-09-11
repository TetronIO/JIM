// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.InMemoryData.Tests;

[TestFixture]
public class SyncRepositoryPendingExportTests
{
    private SyncRepository _repo = null!;
    private const int CsId = 1;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private PendingExport CreatePe(Guid? csoId = null, int connectedSystemId = CsId)
    {
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectId = csoId,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
    }

    /// <summary>
    /// The executable-export predicate requires an Update-type export to carry at least one Pending
    /// or ExportedNotConfirmed attribute change; Create and Delete exports need none. Call this for
    /// any Update-type export a test needs the predicate to treat as executable.
    /// </summary>
    private static void AddPendingAttributeChange(PendingExport pe) => pe.AttributeValueChanges.Add(new PendingExportAttributeValueChange
    {
        Id = Guid.NewGuid(),
        PendingExportId = pe.Id,
        AttributeId = 1,
        Status = PendingExportAttributeChangeStatus.Pending
    });

    [Test]
    public async Task GetPendingExportsAsync_ReturnsForSystemAsync()
    {
        _repo.SeedPendingExport(CreatePe());
        _repo.SeedPendingExport(CreatePe(connectedSystemId: 2));

        var result = await _repo.GetPendingExportsAsync(CsId);
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetPendingExportsCountAsync_ReturnsCountAsync()
    {
        _repo.SeedPendingExport(CreatePe());
        _repo.SeedPendingExport(CreatePe());

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task CreatePendingExportsAsync_AddsToStoreAsync()
    {
        var pe = CreatePe();
        await _repo.CreatePendingExportsAsync(new[] { pe });

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task DeletePendingExportsAsync_RemovesFromStoreAsync()
    {
        var pe = CreatePe();
        _repo.SeedPendingExport(pe);

        await _repo.DeletePendingExportsAsync(new[] { pe });

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task UpdatePendingExportsAsync_UpdatesInStoreAsync()
    {
        var pe = CreatePe();
        _repo.SeedPendingExport(pe);

        pe.ErrorCount = 3;
        await _repo.UpdatePendingExportsAsync(new[] { pe });

        var result = await _repo.GetPendingExportsAsync(CsId);
        Assert.That(result[0].ErrorCount, Is.EqualTo(3));
    }

    [Test]
    public async Task DeletePendingExportsByConnectedSystemObjectIdsAsync_DeletesAndReturnsCountAsync()
    {
        var csoId = Guid.NewGuid();
        _repo.SeedPendingExport(CreatePe(csoId: csoId));

        var deleted = await _repo.DeletePendingExportsByConnectedSystemObjectIdsAsync(new[] { csoId });
        Assert.That(deleted, Is.EqualTo(1));

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetPendingExportByConnectedSystemObjectIdAsync_FindsPeAsync()
    {
        var csoId = Guid.NewGuid();
        var pe = CreatePe(csoId: csoId);
        _repo.SeedPendingExport(pe);

        var result = await _repo.GetPendingExportByConnectedSystemObjectIdAsync(csoId);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(pe.Id));
    }

    [Test]
    public async Task GetPendingExportByConnectedSystemObjectIdAsync_NotFound_ReturnsNullAsync()
    {
        var result = await _repo.GetPendingExportByConnectedSystemObjectIdAsync(Guid.NewGuid());
        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// The lean merge-fetch variant (issue #986) has no Include-shape distinction in this fake store -
    /// every seeded object is already a fully wired-up graph in memory - so it must behave identically
    /// to the heavy fetch here. The fetch-shape distinction itself is proven against real PostgreSQL in
    /// JIM.Worker.Tests PendingExportMergeFetchDatabaseTests.
    /// </summary>
    [Test]
    public async Task GetPendingExportLightweightByConnectedSystemObjectIdAsync_FindsPeAsync()
    {
        var csoId = Guid.NewGuid();
        var pe = CreatePe(csoId: csoId);
        _repo.SeedPendingExport(pe);

        var result = await _repo.GetPendingExportLightweightByConnectedSystemObjectIdAsync(csoId);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(pe.Id));
    }

    [Test]
    public async Task GetPendingExportLightweightByConnectedSystemObjectIdAsync_NotFound_ReturnsNullAsync()
    {
        var result = await _repo.GetPendingExportLightweightByConnectedSystemObjectIdAsync(Guid.NewGuid());
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetPendingExportsLightweightByConnectedSystemObjectIdsAsync_ReturnsDictionaryAsync()
    {
        var csoId1 = Guid.NewGuid();
        var csoId2 = Guid.NewGuid();
        _repo.SeedPendingExport(CreatePe(csoId: csoId1));
        _repo.SeedPendingExport(CreatePe(csoId: csoId2));

        var result = await _repo.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(new[] { csoId1, Guid.NewGuid() });
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.ContainsKey(csoId1), Is.True);
    }

    [Test]
    public async Task GetCsoIdsWithPendingExportsByConnectedSystemAsync_ReturnsHashSetAsync()
    {
        var csoId = Guid.NewGuid();
        _repo.SeedPendingExport(CreatePe(csoId: csoId));
        _repo.SeedPendingExport(CreatePe()); // No CSO ID

        var result = await _repo.GetCsoIdsWithPendingExportsByConnectedSystemAsync(CsId);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.Contains(csoId), Is.True);
    }

    [Test]
    public async Task DeleteUntrackedPendingExportsAsync_BehavesLikeDeleteAsync()
    {
        var pe = CreatePe();
        _repo.SeedPendingExport(pe);

        await _repo.DeleteUntrackedPendingExportsAsync(new[] { pe });

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task UpdateUntrackedPendingExportsAsync_BehavesLikeUpdateAsync()
    {
        var pe = CreatePe();
        _repo.SeedPendingExport(pe);

        pe.ErrorCount = 5;
        await _repo.UpdateUntrackedPendingExportsAsync(new[] { pe });

        var result = await _repo.GetPendingExportsAsync(CsId);
        Assert.That(result[0].ErrorCount, Is.EqualTo(5));
    }

    [Test]
    public async Task DeleteUntrackedPendingExportAttributeValueChangesAsync_RemovesChangesAsync()
    {
        var pe = CreatePe();
        var avc = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            PendingExportId = pe.Id,
            AttributeId = 10
        };
        pe.AttributeValueChanges.Add(avc);
        _repo.SeedPendingExport(pe);

        await _repo.DeleteUntrackedPendingExportAttributeValueChangesAsync(new[] { avc });

        var result = await _repo.GetPendingExportsAsync(CsId);
        Assert.That(result[0].AttributeValueChanges, Is.Empty);
    }

    /// <summary>
    /// <see cref="SyncRepository.GetPendingExportsWithUnresolvedReferencesAsync"/> must return only
    /// rows that are Pending status AND have unresolved references, for the requested Connected
    /// System (#1102). Verifies against a filter matrix: a resolved Pending row, unresolved rows in
    /// non-Pending statuses (Exported, Failed), and an unresolved Pending row for another Connected
    /// System are all excluded.
    /// </summary>
    [Test]
    public async Task GetPendingExportsWithUnresolvedReferencesAsync_ReturnsOnlyPendingUnresolvedForSystemAsync()
    {
        var matchingPe = CreatePe();
        matchingPe.Status = PendingExportStatus.Pending;
        matchingPe.HasUnresolvedReferences = true;
        _repo.SeedPendingExport(matchingPe);

        var resolvedPe = CreatePe();
        resolvedPe.Status = PendingExportStatus.Pending;
        resolvedPe.HasUnresolvedReferences = false;
        _repo.SeedPendingExport(resolvedPe);

        var exportedPe = CreatePe();
        exportedPe.Status = PendingExportStatus.Exported;
        exportedPe.HasUnresolvedReferences = true;
        _repo.SeedPendingExport(exportedPe);

        var failedPe = CreatePe();
        failedPe.Status = PendingExportStatus.Failed;
        failedPe.HasUnresolvedReferences = true;
        _repo.SeedPendingExport(failedPe);

        var otherSystemPe = CreatePe(connectedSystemId: 2);
        otherSystemPe.Status = PendingExportStatus.Pending;
        otherSystemPe.HasUnresolvedReferences = true;
        _repo.SeedPendingExport(otherSystemPe);

        var result = await _repo.GetPendingExportsWithUnresolvedReferencesAsync(CsId);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(matchingPe.Id));
    }

    #region GetExecutableExportCountsByChangeTypeAsync (Run Profile Safeguards, #1618)

    [Test]
    public async Task GetExecutableExportCountsByChangeTypeAsync_NoPendingExports_ReturnsEmptyDictionaryAsync()
    {
        var result = await _repo.GetExecutableExportCountsByChangeTypeAsync(CsId);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExecutableExportCountsByChangeTypeAsync_MixedChangeTypes_CountsEachTypeSeparatelyAsync()
    {
        var create1 = CreatePe();
        create1.ChangeType = PendingExportChangeType.Create;
        _repo.SeedPendingExport(create1);

        var create2 = CreatePe();
        create2.ChangeType = PendingExportChangeType.Create;
        _repo.SeedPendingExport(create2);

        var delete1 = CreatePe();
        delete1.ChangeType = PendingExportChangeType.Delete;
        _repo.SeedPendingExport(delete1);

        var result = await _repo.GetExecutableExportCountsByChangeTypeAsync(CsId);

        Assert.That(result[PendingExportChangeType.Create], Is.EqualTo(2));
        Assert.That(result[PendingExportChangeType.Delete], Is.EqualTo(1));
        Assert.That(result.ContainsKey(PendingExportChangeType.Update), Is.False, "a type with nothing pending is absent from the dictionary");
    }

    [Test]
    public async Task GetExecutableExportCountsByChangeTypeAsync_AppliesTheSameExecutablePredicateAsTheTotalCountAsync()
    {
        // Matches GetExecutableExportCountAsync's own filtering: excludes already-Exported
        // Create/Delete exports, exports over max retries, and exports for another system.
        var executableCreate = CreatePe();
        executableCreate.ChangeType = PendingExportChangeType.Create;
        _repo.SeedPendingExport(executableCreate);

        var alreadyExportedDelete = CreatePe();
        alreadyExportedDelete.ChangeType = PendingExportChangeType.Delete;
        alreadyExportedDelete.Status = PendingExportStatus.Exported;
        _repo.SeedPendingExport(alreadyExportedDelete);

        var overMaxRetriesDelete = CreatePe();
        overMaxRetriesDelete.ChangeType = PendingExportChangeType.Delete;
        overMaxRetriesDelete.ErrorCount = overMaxRetriesDelete.MaxRetries;
        _repo.SeedPendingExport(overMaxRetriesDelete);

        var otherSystemCreate = CreatePe(connectedSystemId: 2);
        otherSystemCreate.ChangeType = PendingExportChangeType.Create;
        _repo.SeedPendingExport(otherSystemCreate);

        var result = await _repo.GetExecutableExportCountsByChangeTypeAsync(CsId);

        Assert.That(result[PendingExportChangeType.Create], Is.EqualTo(1));
        Assert.That(result.ContainsKey(PendingExportChangeType.Delete), Is.False,
            "both Delete exports are excluded: one already Exported, one over its retry limit");

        var totalCount = await _repo.GetExecutableExportCountAsync(CsId);
        Assert.That(result.Values.Sum(), Is.EqualTo(totalCount), "the grouped counts must sum to the same total the ungrouped count query returns");
    }

    #endregion

    #region GetExecutableExportBatchAsync excludedChangeTypes (Run Profile Safeguards, #1618)

    [Test]
    public async Task GetExecutableExportBatchAsync_ExcludedChangeTypes_OmitsThoseTypesFromTheBatchAsync()
    {
        var create = CreatePe();
        create.ChangeType = PendingExportChangeType.Create;
        create.CreatedAt = DateTime.UtcNow.AddMinutes(-2);
        _repo.SeedPendingExport(create);

        var update = CreatePe();
        update.ChangeType = PendingExportChangeType.Update;
        update.CreatedAt = DateTime.UtcNow.AddMinutes(-1);
        AddPendingAttributeChange(update);
        _repo.SeedPendingExport(update);

        var delete = CreatePe();
        delete.ChangeType = PendingExportChangeType.Delete;
        delete.CreatedAt = DateTime.UtcNow;
        _repo.SeedPendingExport(delete);

        var result = await _repo.GetExecutableExportBatchAsync(CsId, take: 10, afterCreatedAt: null, afterId: null,
            excludedChangeTypes: [PendingExportChangeType.Create, PendingExportChangeType.Delete]);

        Assert.That(result.Select(pe => pe.Id), Is.EquivalentTo(new[] { update.Id }));
    }

    [Test]
    public async Task GetExecutableExportBatchAsync_NoExcludedChangeTypes_ReturnsEveryTypeAsync()
    {
        var create = CreatePe();
        create.ChangeType = PendingExportChangeType.Create;
        _repo.SeedPendingExport(create);

        var delete = CreatePe();
        delete.ChangeType = PendingExportChangeType.Delete;
        _repo.SeedPendingExport(delete);

        var result = await _repo.GetExecutableExportBatchAsync(CsId, take: 10, afterCreatedAt: null, afterId: null);

        Assert.That(result, Has.Count.EqualTo(2), "omitting excludedChangeTypes must exclude nothing, matching the pre-#1618 signature's behaviour");
    }

    [Test]
    public async Task GetExecutableExportBatchAsync_EmptyExcludedChangeTypes_ExcludesNothingAsync()
    {
        var create = CreatePe();
        create.ChangeType = PendingExportChangeType.Create;
        _repo.SeedPendingExport(create);

        var result = await _repo.GetExecutableExportBatchAsync(CsId, take: 10, afterCreatedAt: null, afterId: null,
            excludedChangeTypes: []);

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetExecutableExportBatchAsync_ExcludedTypeAlongsideKeysetPaging_CursorStillAdvancesCorrectlyAsync()
    {
        var baseTime = DateTime.UtcNow.AddMinutes(-10);

        var withheldCreate = CreatePe();
        withheldCreate.ChangeType = PendingExportChangeType.Create;
        withheldCreate.CreatedAt = baseTime.AddSeconds(1);
        _repo.SeedPendingExport(withheldCreate);

        var allowedUpdate = CreatePe();
        allowedUpdate.ChangeType = PendingExportChangeType.Update;
        allowedUpdate.CreatedAt = baseTime.AddSeconds(2);
        AddPendingAttributeChange(allowedUpdate);
        _repo.SeedPendingExport(allowedUpdate);

        // First page: excluding Create must skip straight to the Update, not stop at the excluded row.
        var firstPage = await _repo.GetExecutableExportBatchAsync(CsId, take: 10, afterCreatedAt: null, afterId: null,
            excludedChangeTypes: [PendingExportChangeType.Create]);

        Assert.That(firstPage.Select(pe => pe.Id), Is.EquivalentTo(new[] { allowedUpdate.Id }));

        // Paging strictly after the Update's own cursor must find nothing further, including no
        // re-read of the excluded Create.
        var lastRow = firstPage[0];
        var secondPage = await _repo.GetExecutableExportBatchAsync(CsId, take: 10, afterCreatedAt: lastRow.CreatedAt, afterId: lastRow.Id,
            excludedChangeTypes: [PendingExportChangeType.Create]);

        Assert.That(secondPage, Is.Empty);
    }

    #endregion
}
