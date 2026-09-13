// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Logic;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// <see cref="SyncRuleNameResolverCache"/> backs the resolver <see cref="JIM.Models.Core.MetaverseObjectChange.AddAttributeValueChange"/>
/// uses to name a contributing Synchronisation Rule when a value's own navigation was never loaded
/// (#1519 follow-up). These tests pin the two things that matter for a hot sync loop: a rule the caller
/// already has in memory resolves without touching the database at all, and an id that does need the
/// database costs at most one repository round trip however many times it (or other unresolved ids in the
/// same batch) are looked up.
/// </summary>
[TestFixture]
public class SyncRuleNameResolverCacheTests
{
    [Test]
    public void Resolve_IdSeededFromKnownRules_ReturnsNameWithoutRepositoryCall()
    {
        var syncRepo = new Mock<ISyncRepository>(MockBehavior.Strict);
        var cache = new SyncRuleNameResolverCache(syncRepo.Object, [new SyncRule { Id = 7, Name = "HR Import" }]);

        var resolved = cache.Resolve(7);

        Assert.That(resolved, Is.EqualTo("HR Import"));
        syncRepo.VerifyNoOtherCalls();
    }

    [Test]
    public void Resolve_IdNeverSeededOrWarmed_ReturnsNull()
    {
        var syncRepo = new Mock<ISyncRepository>(MockBehavior.Strict);
        var cache = new SyncRuleNameResolverCache(syncRepo.Object);

        Assert.That(cache.Resolve(7), Is.Null);
        syncRepo.VerifyNoOtherCalls();
    }

    [Test]
    public async Task WarmAsync_UnresolvedId_QueriesRepositoryAndCachesTheResultAsync()
    {
        var syncRepo = new Mock<ISyncRepository>();
        syncRepo
            .Setup(r => r.GetSyncRuleNamesByIdsAsync(It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 7 }))))
            .ReturnsAsync(new Dictionary<int, string> { [7] = "HR Import" });
        var cache = new SyncRuleNameResolverCache(syncRepo.Object);

        await cache.WarmAsync([7]);

        Assert.That(cache.Resolve(7), Is.EqualTo("HR Import"));
        syncRepo.Verify(r => r.GetSyncRuleNamesByIdsAsync(It.IsAny<IReadOnlyCollection<int>>()), Times.Once);
    }

    [Test]
    public async Task WarmAsync_IdNotReturnedByRepository_ResolvesNullAndDoesNotReQueryAsync()
    {
        // A deleted Synchronisation Rule: the repository's batched lookup simply omits it.
        var syncRepo = new Mock<ISyncRepository>();
        syncRepo
            .Setup(r => r.GetSyncRuleNamesByIdsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new Dictionary<int, string>());
        var cache = new SyncRuleNameResolverCache(syncRepo.Object);

        await cache.WarmAsync([7]);
        await cache.WarmAsync([7]); // same id again, still within the same batch

        Assert.That(cache.Resolve(7), Is.Null);
        syncRepo.Verify(r => r.GetSyncRuleNamesByIdsAsync(It.IsAny<IReadOnlyCollection<int>>()), Times.Once,
            "a miss must be cached too, so a repeated lookup for a deleted rule never re-queries");
    }

    [Test]
    public async Task WarmAsync_CalledRepeatedlyForSameId_MakesOnlyOneRepositoryCallInTotalAsync()
    {
        var syncRepo = new Mock<ISyncRepository>();
        syncRepo
            .Setup(r => r.GetSyncRuleNamesByIdsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new Dictionary<int, string> { [7] = "HR Import" });
        var cache = new SyncRuleNameResolverCache(syncRepo.Object);

        // Simulates many attribute values across a batch all contributed by the same Synchronisation Rule.
        for (var i = 0; i < 5; i++)
            await cache.WarmAsync([7]);

        Assert.That(cache.Resolve(7), Is.EqualTo("HR Import"));
        syncRepo.Verify(r => r.GetSyncRuleNamesByIdsAsync(It.IsAny<IReadOnlyCollection<int>>()), Times.Once);
    }

    [Test]
    public async Task WarmAsync_MixOfSeededAndUnseededIds_QueriesOnlyTheUnseededOnesInOneCallAsync()
    {
        var syncRepo = new Mock<ISyncRepository>();
        syncRepo
            .Setup(r => r.GetSyncRuleNamesByIdsAsync(It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 8 }))))
            .ReturnsAsync(new Dictionary<int, string> { [8] = "AD Export" });
        var cache = new SyncRuleNameResolverCache(syncRepo.Object, [new SyncRule { Id = 7, Name = "HR Import" }]);

        // 7 is already known from the seed; only 8 should reach the repository.
        await cache.WarmAsync([7, 8]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Resolve(7), Is.EqualTo("HR Import"));
            Assert.That(cache.Resolve(8), Is.EqualTo("AD Export"));
        }
        syncRepo.Verify(r => r.GetSyncRuleNamesByIdsAsync(It.IsAny<IReadOnlyCollection<int>>()), Times.Once);
    }
}
