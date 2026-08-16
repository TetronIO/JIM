// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the offset/count pending-deletion range read
/// (<see cref="JIM.Application.Servers.MetaverseServer.GetMetaverseObjectsPendingDeletionRangeAsync"/>) that
/// backs the virtualised (infinite-scroll) Pending Deletions page. The in-memory provider evaluates the search
/// and the deletion-eligible sort (a timestamp-plus-interval expression the provider runs in .NET) client-side,
/// so their SQL translation, along with the windowing and count-skipping semantics, is only verifiable against
/// a real database. The context is NoTracking, matching JIM.Web's configuration.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingDeletionRangeDatabaseTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL pending-deletion range tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
    }

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    /// <summary>
    /// Seeds a User type with an automatic deletion rule and <paramref name="count"/> Metaverse Objects marked
    /// for deletion, their disconnection dates staggered in creation order so the default soonest-scheduled-first
    /// order yields ascending numeric label order ("Pending User 001" first). Each object's cached display name
    /// doubles as the search target. Returns the type's id.
    /// </summary>
    private async Task<int> SeedAsync(int count)
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType
        {
            Name = "User",
            PluralName = "Users",
            BuiltIn = true,
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };
        ctx.MetaverseObjectTypes.Add(type);
        await ctx.SaveChangesAsync();

        for (var i = 1; i <= count; i++)
            ctx.MetaverseObjects.Add(BuildPendingMvo(type, i));

        await ctx.SaveChangesAsync();
        return type.Id;
    }

    private static MetaverseObject BuildPendingMvo(MetaverseObjectType type, int ordinal, string displayNamePrefix = "Pending User")
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

    [Test]
    public async Task Range_FirstWindow_ReturnsSoonestScheduledFirstSliceAndFullTotalAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(m => m.CachedDisplayName),
                Is.EqualTo(new[] { "Pending User 001", "Pending User 002", "Pending User 003" }));
        }
    }

    [Test]
    public async Task Range_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(m => m.CachedDisplayName),
                Is.EqualTo(new[] { "Pending User 004", "Pending User 005", "Pending User 006" }));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsWindowWithNoTotalAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var counted = await jim.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 3, count: 3);
        var uncounted = await jim.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches", which
            // is the one wrong answer a caller cannot distinguish from a real result. Skipping the count must
            // change nothing else, so the window is asserted against its counted sibling.
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.Results.Select(m => m.CachedDisplayName),
                Is.EqualTo(counted.Results.Select(m => m.CachedDisplayName)));
        }
    }

    [Test]
    public async Task Range_CountAboveCap_ClampsToFiveHundredAsync()
    {
        await SeedAsync(505);
        var jim = NewJim();

        var result = await jim.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 0, count: 1000);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every matching object. The
            // cap is 500, matching the header range reads; see MaxHeaderWindowSize in MetaverseRepository for how
            // 500 was derived.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public async Task Range_Search_MatchesDisplayNameCaseInsensitivelyAndRestrictsTotalAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 0, count: 10, searchQuery: "PENDING USER 004");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(m => m.CachedDisplayName), Is.EqualTo(new[] { "Pending User 004" }));
        }
    }

    [Test]
    public async Task Range_Search_MatchesTriggeringSystemNameAsync()
    {
        await using (var ctx = NewContext())
        {
            var type = new MetaverseObjectType
            {
                Name = "User",
                PluralName = "Users",
                BuiltIn = true,
                DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected
            };
            ctx.MetaverseObjectTypes.Add(type);
            await ctx.SaveChangesAsync();

            var triggered = BuildPendingMvo(type, 1);
            triggered.DeletionTriggeredBySystemName = "HR System";
            ctx.MetaverseObjects.Add(triggered);
            ctx.MetaverseObjects.Add(BuildPendingMvo(type, 2));
            await ctx.SaveChangesAsync();
        }

        var jim = NewJim();
        var result = await jim.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 0, count: 10, searchQuery: "hr sys");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(m => m.CachedDisplayName), Is.EqualTo(new[] { "Pending User 001" }));
        }
    }

    [Test]
    public async Task Range_ObjectTypeFilter_RestrictsWindowAndTotalAsync()
    {
        int groupTypeId;
        await using (var ctx = NewContext())
        {
            var userType = new MetaverseObjectType
            {
                Name = "User",
                PluralName = "Users",
                BuiltIn = true,
                DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected
            };
            var groupType = new MetaverseObjectType
            {
                Name = "Group",
                PluralName = "Groups",
                BuiltIn = true,
                DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected
            };
            ctx.MetaverseObjectTypes.AddRange(userType, groupType);
            await ctx.SaveChangesAsync();

            for (var i = 1; i <= 3; i++)
                ctx.MetaverseObjects.Add(BuildPendingMvo(userType, i));
            for (var i = 1; i <= 2; i++)
                ctx.MetaverseObjects.Add(BuildPendingMvo(groupType, i, displayNamePrefix: "Pending Group"));
            await ctx.SaveChangesAsync();
            groupTypeId = groupType.Id;
        }

        var jim = NewJim();
        var result = await jim.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 0, count: 10, objectTypeId: groupTypeId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(m => m.CachedDisplayName),
                Is.EqualTo(new[] { "Pending Group 001", "Pending Group 002" }));
        }
    }

    [Test]
    public async Task Range_SortByDisplayNameDescending_OrdersByCachedDisplayNameAsync()
    {
        await SeedAsync(3);
        var jim = NewJim();

        var result = await jim.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 0, count: 10, sortBy: "displayname", sortDescending: true);

        Assert.That(result.Results.Select(m => m.CachedDisplayName),
            Is.EqualTo(new[] { "Pending User 003", "Pending User 002", "Pending User 001" }));
    }

    [Test]
    public async Task Range_SortByEligible_OrdersByDisconnectedDatePlusGracePeriodAsync()
    {
        // Three objects whose deletion-eligible order is the reverse of their disconnection order, so this test
        // fails if the eligible sort's timestamp-plus-interval expression does not translate or falls back to
        // the disconnected date: the first to disconnect has the longest grace period, and the last to
        // disconnect has none (eligible immediately, at its disconnection time).
        await using (var ctx = NewContext())
        {
            var longGraceType = new MetaverseObjectType
            {
                Name = "LongGrace",
                PluralName = "LongGraces",
                BuiltIn = true,
                DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
                DeletionGracePeriod = TimeSpan.FromDays(30)
            };
            var shortGraceType = new MetaverseObjectType
            {
                Name = "ShortGrace",
                PluralName = "ShortGraces",
                BuiltIn = true,
                DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
                DeletionGracePeriod = TimeSpan.FromDays(1)
            };
            var noGraceType = new MetaverseObjectType
            {
                Name = "NoGrace",
                PluralName = "NoGraces",
                BuiltIn = true,
                DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected
            };
            ctx.MetaverseObjectTypes.AddRange(longGraceType, shortGraceType, noGraceType);
            await ctx.SaveChangesAsync();

            ctx.MetaverseObjects.Add(BuildPendingMvo(longGraceType, 1, displayNamePrefix: "Long Grace"));
            ctx.MetaverseObjects.Add(BuildPendingMvo(shortGraceType, 2, displayNamePrefix: "Short Grace"));
            ctx.MetaverseObjects.Add(BuildPendingMvo(noGraceType, 3, displayNamePrefix: "No Grace"));
            await ctx.SaveChangesAsync();
        }

        var jim = NewJim();
        var result = await jim.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(
            offset: 0, count: 10, sortBy: "eligible", sortDescending: false);

        Assert.That(result.Results.Select(m => m.CachedDisplayName),
            Is.EqualTo(new[] { "No Grace 003", "Short Grace 002", "Long Grace 001" }));
    }

    [Test]
    public async Task Range_FullWindow_MatchesPagedReaderAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var range = await jim.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 0, count: 10);
        var paged = await jim.Metaverse.GetMetaverseObjectsPendingDeletionAsync(page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(m => m.Id), Is.EqualTo(paged.Results.Select(m => m.Id)));
        }
    }
}
