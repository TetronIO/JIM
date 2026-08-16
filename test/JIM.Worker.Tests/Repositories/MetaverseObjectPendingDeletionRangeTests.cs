// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the offset/count pending-deletion range read (<c>GetMetaverseObjectsPendingDeletionRangeAsync</c>) that
/// backs the virtualised (infinite-scroll) Pending Deletions page: window correctness at absolute offsets, the
/// skip-the-count contract (a null total, never zero, when the caller already holds the count), the window-size
/// cap, the search and sort semantics, and that the deletion-rule filter shared with the paged read applies
/// through the range entry point too.
/// </summary>
[TestFixture]
public class MetaverseObjectPendingDeletionRangeTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
    /// Builds a Metaverse Object marked for deletion: projected, carrying a disconnection date staggered by
    /// <paramref name="ordinal"/> (so the default soonest-scheduled-first order yields ascending numeric label
    /// order), with a cached display name that doubles as the search target.
    /// </summary>
    private static MetaverseObject NewPendingMvo(MetaverseObjectType type, int ordinal, string? displayNamePrefix = "Pending User")
    {
        return new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Origin = MetaverseObjectOrigin.Projected,
            Type = type,
            LastConnectorDisconnectedDate = BaseTime.AddSeconds(ordinal),
            CachedDisplayName = $"{displayNamePrefix} {ordinal:D3}"
        };
    }

    private async Task SeedPendingMvosAsync(int count)
    {
        var type = await SeedTypeAsync();
        for (var i = 1; i <= count; i++)
            _dbContext.MetaverseObjects.Add(NewPendingMvo(type, i));
        await _dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        await SeedPendingMvosAsync(10);

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(m => m.CachedDisplayName),
                Is.EqualTo(new[] { "Pending User 001", "Pending User 002", "Pending User 003" }));
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        await SeedPendingMvosAsync(10);

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(m => m.CachedDisplayName),
                Is.EqualTo(new[] { "Pending User 004", "Pending User 005", "Pending User 006" }));
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        await SeedPendingMvosAsync(10);

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 100, count: 10);

        using (Assert.EnterMultipleScope())
        {
            // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results, Is.Empty);
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        await SeedPendingMvosAsync(10);

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches",
            // which is the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(m => m.CachedDisplayName),
                Is.EqualTo(new[] { "Pending User 004", "Pending User 005", "Pending User 006" }));
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        await SeedPendingMvosAsync(10);

        // Skipping the count must change what the caller is told about the total and nothing else; the window
        // itself comes from the same filtered, sorted query either way.
        var counted = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 5, count: 4);
        var uncounted = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 5, count: 4, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(m => m.CachedDisplayName),
                Is.EqualTo(counted.Results.Select(m => m.CachedDisplayName)));
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_CountAboveCap_ClampsToFiveHundredAsync()
    {
        await SeedPendingMvosAsync(505);

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 0, count: 1000);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every matching object. The
            // cap is 500, matching the header range reads: nothing here is a person choosing a page size, and a
            // clamp a viewport could reach would silently render the shortfall as blank rows. See
            // MaxHeaderWindowSize in MetaverseRepository for how 500 was derived.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public void GetMetaverseObjectsPendingDeletionRangeAsync_CountBelowOne_Throws()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 0, count: 0));
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_NegativeOffset_IsTreatedAsTheTopOfTheListAsync()
    {
        await SeedPendingMvosAsync(5);

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: -10, count: 2);

        Assert.That(result.Results.Select(m => m.CachedDisplayName),
            Is.EqualTo(new[] { "Pending User 001", "Pending User 002" }));
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_Search_MatchesDisplayNameCaseInsensitivelyAndRestrictsTotalAsync()
    {
        await SeedPendingMvosAsync(10);

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 0, count: 10, searchQuery: "PENDING USER 004");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(m => m.CachedDisplayName), Is.EqualTo(new[] { "Pending User 004" }));
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_Search_MatchesTriggeringSystemNameAsync()
    {
        var type = await SeedTypeAsync();
        var triggered = NewPendingMvo(type, 1);
        triggered.DeletionTriggeredBySystemName = "HR System";
        _dbContext.MetaverseObjects.Add(triggered);
        _dbContext.MetaverseObjects.Add(NewPendingMvo(type, 2));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 0, count: 10, searchQuery: "hr sys");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(m => m.Id), Is.EqualTo(new[] { triggered.Id }));
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_SortByDisplayNameDescending_OrdersByCachedDisplayNameAsync()
    {
        await SeedPendingMvosAsync(3);

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 0, count: 10, sortBy: "displayname", sortDescending: true);

        Assert.That(result.Results.Select(m => m.CachedDisplayName),
            Is.EqualTo(new[] { "Pending User 003", "Pending User 002", "Pending User 001" }));
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_SortByType_OrdersByTypeNameAsync()
    {
        var betaType = await SeedTypeAsync(name: "Beta");
        var alphaType = await SeedTypeAsync(name: "Alpha");
        _dbContext.MetaverseObjects.Add(NewPendingMvo(betaType, 1, displayNamePrefix: "Beta Object"));
        _dbContext.MetaverseObjects.Add(NewPendingMvo(alphaType, 2, displayNamePrefix: "Alpha Object"));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 0, count: 10, sortBy: "type", sortDescending: false);

        Assert.That(result.Results.Select(m => m.Type.Name), Is.EqualTo(new[] { "Alpha", "Beta" }));
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_SortByEligible_OrdersByDisconnectedDatePlusGracePeriodAsync()
    {
        // Three objects whose deletion-eligible order is the reverse of their disconnection order, so this test
        // fails if the eligible sort falls back to the disconnected date: A disconnects first but has the longest
        // grace period, C disconnects last but has none (eligible immediately, at its disconnection time).
        var longGraceType = await SeedTypeAsync(name: "LongGrace", gracePeriod: TimeSpan.FromDays(30));
        var shortGraceType = await SeedTypeAsync(name: "ShortGrace", gracePeriod: TimeSpan.FromDays(1));
        var noGraceType = await SeedTypeAsync(name: "NoGrace");
        _dbContext.MetaverseObjects.Add(NewPendingMvo(longGraceType, 1, displayNamePrefix: "Long Grace"));
        _dbContext.MetaverseObjects.Add(NewPendingMvo(shortGraceType, 2, displayNamePrefix: "Short Grace"));
        _dbContext.MetaverseObjects.Add(NewPendingMvo(noGraceType, 3, displayNamePrefix: "No Grace"));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 0, count: 10, sortBy: "eligible", sortDescending: false);

        Assert.That(result.Results.Select(m => m.CachedDisplayName),
            Is.EqualTo(new[] { "No Grace 003", "Short Grace 002", "Long Grace 001" }));
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_ObjectTypeFilter_RestrictsWindowAndTotalAsync()
    {
        var userType = await SeedTypeAsync(name: "User");
        var groupType = await SeedTypeAsync(name: "Group");
        for (var i = 1; i <= 3; i++)
            _dbContext.MetaverseObjects.Add(NewPendingMvo(userType, i));
        for (var i = 1; i <= 2; i++)
            _dbContext.MetaverseObjects.Add(NewPendingMvo(groupType, i, displayNamePrefix: "Pending Group"));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 0, count: 10, objectTypeId: groupType.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(m => m.CachedDisplayName),
                Is.EqualTo(new[] { "Pending Group 001", "Pending Group 002" }));
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_ManualRuleMvoWithMarker_IsNotListedAsync()
    {
        // Manual-rule objects are never automatically deleted, so a stray disconnection marker must not list
        // them; the rule filter shared with the paged read applies through the range entry point too.
        var manualType = await SeedTypeAsync(name: "ServiceAccount", deletionRule: MetaverseObjectDeletionRule.Manual);
        var autoType = await SeedTypeAsync(name: "User");
        _dbContext.MetaverseObjects.Add(NewPendingMvo(manualType, 1, displayNamePrefix: "Manual Object"));
        var listed = NewPendingMvo(autoType, 2);
        _dbContext.MetaverseObjects.Add(listed);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(m => m.Id), Is.EqualTo(new[] { listed.Id }));
        }
    }

    [Test]
    public async Task GetMetaverseObjectsPendingDeletionRangeAsync_FullWindow_MatchesPagedReaderAsync()
    {
        await SeedPendingMvosAsync(10);

        var range = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 0, count: 10);
        var paged = await _repository.Metaverse.GetMetaverseObjectsPendingDeletionAsync(page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(m => m.Id), Is.EqualTo(paged.Results.Select(m => m.Id)));
        }
    }
}
