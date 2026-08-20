// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the pending-deletion summary read (<c>GetMetaverseObjectsPendingDeletionStateCountsAsync</c>) that backs the
/// four cards above the Pending Deletions list: that each state is counted across the whole match set rather than
/// a window of it (the fault these replaced counted only the first hundred objects), that the three states
/// partition the total, and that the Metaverse Object Type filter and the deletion-rule filter shared with the
/// list both apply.
/// </summary>
[TestFixture]
public class MetaverseObjectPendingDeletionStateCountsTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .EnableSensitiveDataLogging()
            .Options;

        _dbContext = new JimDbContext(options);
        _repository = new PostgresDataRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
        _dbContext?.Dispose();
    }

    private async Task<MetaverseObjectType> SeedTypeAsync(
        string name = "User",
        MetaverseObjectDeletionRule deletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
        TimeSpan? gracePeriod = null)
    {
        var type = new MetaverseObjectType
        {
            Name = name,
            PluralName = name + "s",
            DeletionRule = deletionRule,
            DeletionGracePeriod = gracePeriod
        };
        _dbContext.MetaverseObjectTypes.Add(type);
        await _dbContext.SaveChangesAsync();
        return type;
    }

    /// <summary>
    /// Adds a projected Metaverse Object disconnected <paramref name="disconnectedDaysAgo"/> days before now, so a
    /// type's grace period can be made to have elapsed or not by choosing the two together. A connected object
    /// carries one Connected System Object, which is what puts it in the deprovisioning state.
    /// </summary>
    private async Task SeedPendingMvoAsync(MetaverseObjectType type, double disconnectedDaysAgo, bool stillConnected = false)
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Origin = MetaverseObjectOrigin.Projected,
            Type = type,
            LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-disconnectedDaysAgo),
            CachedDisplayName = $"Pending {Guid.NewGuid():N}"
        };

        if (stillConnected)
            mvo.ConnectedSystemObjects.Add(new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObject = mvo });

        _dbContext.MetaverseObjects.Add(mvo);
        await _dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionStateCountsAsync_MoreObjectsThanAPageOf100_CountsAllOfThemAsync()
    {
        // 120 objects, past a ten-day grace period and disconnected, so every one is ready for deletion. A summary
        // computed from the first hundred rows reports 100; the whole match set is 120.
        var type = await SeedTypeAsync(gracePeriod: TimeSpan.FromDays(10));
        for (var i = 0; i < 120; i++)
            await SeedPendingMvoAsync(type, disconnectedDaysAgo: 20);

        var summary = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionStateCountsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Total, Is.EqualTo(120));
            Assert.That(summary.ReadyForDeletion, Is.EqualTo(120));
            Assert.That(summary.AwaitingGracePeriod, Is.Zero);
            Assert.That(summary.Deprovisioning, Is.Zero);
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionStateCountsAsync_MixedStates_PartitionsTheTotalAsync()
    {
        var type = await SeedTypeAsync(gracePeriod: TimeSpan.FromDays(30));

        // Three still connected: being deprovisioned, whatever their grace period says.
        for (var i = 0; i < 3; i++)
            await SeedPendingMvoAsync(type, disconnectedDaysAgo: 40, stillConnected: true);

        // Five disconnected two days ago, so 28 days of their grace period remain.
        for (var i = 0; i < 5; i++)
            await SeedPendingMvoAsync(type, disconnectedDaysAgo: 2);

        // Seven disconnected 40 days ago: their grace period has elapsed.
        for (var i = 0; i < 7; i++)
            await SeedPendingMvoAsync(type, disconnectedDaysAgo: 40);

        var summary = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionStateCountsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Total, Is.EqualTo(15));
            Assert.That(summary.Deprovisioning, Is.EqualTo(3));
            Assert.That(summary.AwaitingGracePeriod, Is.EqualTo(5));
            Assert.That(summary.ReadyForDeletion, Is.EqualTo(7));
            Assert.That(summary.Deprovisioning + summary.AwaitingGracePeriod + summary.ReadyForDeletion,
                Is.EqualTo(summary.Total), "the three states must partition the total");
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionStateCountsAsync_TypeWithNoGracePeriod_CountsAsReadyForDeletionAsync()
    {
        // No grace period configured means eligible at the moment of disconnection, so even an object disconnected
        // seconds ago is ready rather than waiting.
        var type = await SeedTypeAsync(gracePeriod: null);
        await SeedPendingMvoAsync(type, disconnectedDaysAgo: 0);

        var summary = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionStateCountsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Total, Is.EqualTo(1));
            Assert.That(summary.ReadyForDeletion, Is.EqualTo(1));
            Assert.That(summary.AwaitingGracePeriod, Is.Zero);
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionStateCountsAsync_ObjectTypeFilter_CountsOnlyThatTypeAsync()
    {
        var users = await SeedTypeAsync("User", gracePeriod: TimeSpan.FromDays(30));
        var groups = await SeedTypeAsync("Group", gracePeriod: TimeSpan.FromDays(30));

        await SeedPendingMvoAsync(users, disconnectedDaysAgo: 40);
        await SeedPendingMvoAsync(users, disconnectedDaysAgo: 1);
        await SeedPendingMvoAsync(groups, disconnectedDaysAgo: 40);

        var summary = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionStateCountsAsync(users.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Total, Is.EqualTo(2));
            Assert.That(summary.ReadyForDeletion, Is.EqualTo(1));
            Assert.That(summary.AwaitingGracePeriod, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionStateCountsAsync_TypeWithNoAutomaticDeletionRule_IsExcludedAsync()
    {
        // The list excludes types JIM will never delete automatically; the summary above it must agree, or the
        // cards describe a population the rows below them do not.
        var manual = await SeedTypeAsync("Contractor", MetaverseObjectDeletionRule.Manual, TimeSpan.FromDays(30));
        await SeedPendingMvoAsync(manual, disconnectedDaysAgo: 40);

        var summary = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionStateCountsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Total, Is.Zero);
            Assert.That(summary.Deprovisioning, Is.Zero);
            Assert.That(summary.AwaitingGracePeriod, Is.Zero);
            Assert.That(summary.ReadyForDeletion, Is.Zero);
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionStateCountsAsync_NothingPendingDeletion_ReturnsZeroesAsync()
    {
        await SeedTypeAsync();

        var summary = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionStateCountsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Total, Is.Zero);
            Assert.That(summary.ReadyForDeletion, Is.Zero);
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionStateCountsAsync_MatchesTheListItBelongsTo_TotalAgreesWithTheRangeReadAsync()
    {
        // The cards and the list are two reads of one population; the day they disagree, one of them is lying.
        var type = await SeedTypeAsync(gracePeriod: TimeSpan.FromDays(5));
        for (var i = 0; i < 12; i++)
            await SeedPendingMvoAsync(type, disconnectedDaysAgo: i);

        var summary = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionStateCountsAsync();
        var window = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(0, 5);

        Assert.That(summary.Total, Is.EqualTo(window.TotalResults));
    }
}
