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
/// Real-PostgreSQL verification of the offset/count deleted Metaverse Object change read
/// (<see cref="JIM.Application.Servers.MetaverseServer.GetDeletedMvoChangesRangeAsync"/>) that backs the
/// virtualised (infinite-scroll) Deleted Objects page. The query it shares with the paged reader leans on
/// EF.Functions.ILike, which the EF Core in-memory provider cannot execute; the windowing, count-skipping and
/// filter semantics are only verifiable against a real database.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class DeletedMvoChangeRangeDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL deleted Metaverse Object change range tests.");

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

    /// <summary>
    /// Seeds a User type and <paramref name="count"/> deletion change records whose ChangeTime timestamps are
    /// staggered in creation order, so the read's fixed newest-first ordering yields descending numeric label
    /// order ("Deleted User 010" first for a seed of 10). Each record's preserved display name doubles as the
    /// search target. One non-deletion (Update) change is seeded on top with the newest timestamp of all, so
    /// any window or total that failed to restrict to deletions is caught by the assertions.
    /// Returns the object type's id.
    /// </summary>
    private async Task<int> SeedAsync(int count)
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        ctx.MetaverseObjectTypes.Add(type);
        await ctx.SaveChangesAsync();

        for (var i = 1; i <= count; i++)
            ctx.Add(BuildDeletionChange(type.Id, i));

        ctx.Add(new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Updated,
            ChangeTime = BaseTime.AddSeconds(count + 1),
            DeletedObjectDisplayName = "Not A Deletion"
        });

        await ctx.SaveChangesAsync();
        return type.Id;
    }

    /// <summary>
    /// Seeds two object types with deletion changes of each, so the object type filter has something to include
    /// and something to exclude. Returns both types' ids; the first carries "Deleted User 001" to
    /// "Deleted User 003", the second "Deleted Group 001" to "Deleted Group 002".
    /// </summary>
    private async Task<(int UserTypeId, int GroupTypeId)> SeedTwoTypesAsync()
    {
        await using var ctx = NewContext();
        var userType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        ctx.MetaverseObjectTypes.AddRange(userType, groupType);
        await ctx.SaveChangesAsync();

        for (var i = 1; i <= 3; i++)
            ctx.Add(BuildDeletionChange(userType.Id, i));

        for (var i = 1; i <= 2; i++)
            ctx.Add(BuildDeletionChange(groupType.Id, i, displayNamePrefix: "Deleted Group"));

        await ctx.SaveChangesAsync();
        return (userType.Id, groupType.Id);
    }

    private static MetaverseObjectChange BuildDeletionChange(int objectTypeId, int ordinal, string displayNamePrefix = "Deleted User")
    {
        return new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = BaseTime.AddSeconds(ordinal),
            DeletedObjectTypeId = objectTypeId,
            DeletedObjectDisplayName = $"{displayNamePrefix} {ordinal:D3}",
            DeletedMetaverseObjectId = Guid.NewGuid()
        };
    }

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    [Test]
    public async Task Range_FirstWindow_ReturnsNewestFirstSliceAndFullTotalAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Metaverse.GetDeletedMvoChangesRangeAsync(offset: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(r => r.DeletedObjectDisplayName),
                Is.EqualTo(new[] { "Deleted User 010", "Deleted User 009", "Deleted User 008" }));
        }
    }

    [Test]
    public async Task Range_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Metaverse.GetDeletedMvoChangesRangeAsync(offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(r => r.DeletedObjectDisplayName),
                Is.EqualTo(new[] { "Deleted User 007", "Deleted User 006", "Deleted User 005" }));
        }
    }

    [Test]
    public async Task Range_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Metaverse.GetDeletedMvoChangesRangeAsync(offset: 100, count: 10);

        using (Assert.EnterMultipleScope())
        {
            // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results, Is.Empty);
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsWindowWithNoTotalAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Metaverse.GetDeletedMvoChangesRangeAsync(offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches", which is
            // the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(r => r.DeletedObjectDisplayName),
                Is.EqualTo(new[] { "Deleted User 007", "Deleted User 006", "Deleted User 005" }));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        // Skipping the count must change what the caller is told about the total and nothing else; the window itself
        // comes from the same filtered, sorted query either way.
        var counted = await jim.Metaverse.GetDeletedMvoChangesRangeAsync(offset: 5, count: 4);
        var uncounted = await jim.Metaverse.GetDeletedMvoChangesRangeAsync(offset: 5, count: 4, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(r => r.DeletedObjectDisplayName),
                Is.EqualTo(counted.Results.Select(r => r.DeletedObjectDisplayName)));
        }
    }

    [Test]
    public async Task Range_CountAboveCap_ClampsToFiveHundredAsync()
    {
        await SeedAsync(505);
        var jim = NewJim();

        var result = await jim.Metaverse.GetDeletedMvoChangesRangeAsync(offset: 0, count: 1000);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every matching record. The cap
            // is 500, matching the header range reads: nothing here is a person choosing a page size, and a clamp a
            // viewport could reach would silently render the shortfall as blank rows. See the constant's own comment
            // in MetaverseRepository for how 500 was derived.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public async Task Range_ObjectTypeFilter_RestrictsWindowAndTotalAsync()
    {
        var (_, groupTypeId) = await SeedTwoTypesAsync();
        var jim = NewJim();

        var result = await jim.Metaverse.GetDeletedMvoChangesRangeAsync(offset: 0, count: 10, objectTypeId: groupTypeId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(r => r.DeletedObjectDisplayName),
                Is.EqualTo(new[] { "Deleted Group 002", "Deleted Group 001" }));
        }
    }

    [Test]
    public async Task Range_DateRange_RestrictsWindowAndTotalAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        // Both bounds are inclusive, matching the paged reader's semantics.
        var result = await jim.Metaverse.GetDeletedMvoChangesRangeAsync(
            offset: 0, count: 10, fromDate: BaseTime.AddSeconds(4), toDate: BaseTime.AddSeconds(6));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(3));
            Assert.That(result.Results.Select(r => r.DeletedObjectDisplayName),
                Is.EqualTo(new[] { "Deleted User 006", "Deleted User 005", "Deleted User 004" }));
        }
    }

    [Test]
    public async Task Range_Search_MatchesDisplayNameCaseInsensitivelyAndRestrictsTotalAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Metaverse.GetDeletedMvoChangesRangeAsync(offset: 0, count: 10, displayNameSearch: "deleted user 004");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(r => r.DeletedObjectDisplayName), Is.EqualTo(new[] { "Deleted User 004" }));
        }
    }

    [Test]
    public async Task Range_FullWindow_MatchesPagedReaderAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var range = await jim.Metaverse.GetDeletedMvoChangesRangeAsync(offset: 0, count: 10);
        var (pagedItems, pagedTotal) = await jim.Metaverse.GetDeletedMvoChangesAsync(page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(pagedTotal));
            Assert.That(range.Results.Select(r => r.DeletedObjectDisplayName),
                Is.EqualTo(pagedItems.Select(r => r.DeletedObjectDisplayName)));
        }
    }
}
