// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the offset/count header read
/// (<see cref="JIM.Application.Servers.MetaverseServer.GetMetaverseObjectHeadersRangeAsync"/>) that backs the
/// virtualised (infinite-scroll) Metaverse Object list. Like the paged reader it shares, this method is hand-written
/// raw PostgreSQL (OFFSET/LIMIT over a projected header query), so the EF Core in-memory provider cannot execute it;
/// the windowing and total-count semantics are only verifiable against a real database.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectHeaderRangeDatabaseTests
{
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL header-range tests.");

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
    /// Seeds a User type and <paramref name="count"/> objects named "User 01", "User 02", ... (zero-padded so
    /// lexical order matches numeric order), then persists a criteria-free Predefined Search over the type (which
    /// matches every object of the type). Returns the search id.
    /// </summary>
    private async Task<int> SeedAsync(int count)
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        ctx.MetaverseObjectTypes.Add(type);

        for (var i = 1; i <= count; i++)
            ctx.MetaverseObjects.Add(new MetaverseObject { Type = type, CachedDisplayName = $"User {i:D2}" });

        var search = new PredefinedSearch { Name = "All Users", Uri = "all-users", MetaverseObjectType = type };
        ctx.PredefinedSearches.Add(search);
        await ctx.SaveChangesAsync();
        return search.Id;
    }

    private async Task<(JimApplication Jim, PredefinedSearch Search)> LoadAsync(int searchId)
    {
        var jim = new JimApplication(new PostgresDataRepository(NewContext()));
        var search = await jim.Search.GetPredefinedSearchAsync(searchId);
        Assert.That(search, Is.Not.Null);
        return (jim, search!);
    }

    private static readonly string DisplayName = Constants.BuiltInAttributes.DisplayName;

    [Test]
    public async Task Range_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        var searchId = await SeedAsync(10);
        var (jim, search) = await LoadAsync(searchId);

        var result = await jim.Metaverse.GetMetaverseObjectHeadersRangeAsync(search, offset: 0, count: 3, sortBy: DisplayName, sortDescending: false);

        Assert.That(result.TotalResults, Is.EqualTo(10));
        Assert.That(result.Results.Select(r => r.CachedDisplayName), Is.EqualTo(new[] { "User 01", "User 02", "User 03" }));
    }

    [Test]
    public async Task Range_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        var searchId = await SeedAsync(10);
        var (jim, search) = await LoadAsync(searchId);

        var result = await jim.Metaverse.GetMetaverseObjectHeadersRangeAsync(search, offset: 3, count: 3, sortBy: DisplayName, sortDescending: false);

        Assert.That(result.TotalResults, Is.EqualTo(10));
        Assert.That(result.Results.Select(r => r.CachedDisplayName), Is.EqualTo(new[] { "User 04", "User 05", "User 06" }));
    }

    [Test]
    public async Task Range_WindowStraddlingEnd_ReturnsRemainderAsync()
    {
        var searchId = await SeedAsync(10);
        var (jim, search) = await LoadAsync(searchId);

        var result = await jim.Metaverse.GetMetaverseObjectHeadersRangeAsync(search, offset: 9, count: 5, sortBy: DisplayName, sortDescending: false);

        Assert.That(result.TotalResults, Is.EqualTo(10));
        Assert.That(result.Results.Select(r => r.CachedDisplayName), Is.EqualTo(new[] { "User 10" }));
    }

    [Test]
    public async Task Range_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        var searchId = await SeedAsync(10);
        var (jim, search) = await LoadAsync(searchId);

        var result = await jim.Metaverse.GetMetaverseObjectHeadersRangeAsync(search, offset: 100, count: 10, sortBy: DisplayName, sortDescending: false);

        // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
        Assert.That(result.TotalResults, Is.EqualTo(10));
        Assert.That(result.Results, Is.Empty);
    }

    [Test]
    public async Task Range_FullWindow_MatchesPagedReaderAsync()
    {
        var searchId = await SeedAsync(10);
        var (jim, search) = await LoadAsync(searchId);

        var range = await jim.Metaverse.GetMetaverseObjectHeadersRangeAsync(search, offset: 0, count: 10, sortBy: DisplayName, sortDescending: false);
        var paged = await jim.Metaverse.GetMetaverseObjectHeadersPagedAsync(search, page: 1, pageSize: 10, sortBy: DisplayName, sortDescending: false);

        Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
        Assert.That(range.Results.Select(r => r.CachedDisplayName), Is.EqualTo(paged.Results.Select(r => r.CachedDisplayName)));
    }

    [Test]
    public async Task Range_CountAboveCap_ClampsToFiveHundredAsync()
    {
        var searchId = await SeedAsync(505);
        var (jim, search) = await LoadAsync(searchId);

        var result = await jim.Metaverse.GetMetaverseObjectHeadersRangeAsync(search, offset: 0, count: 1000, sortBy: DisplayName, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every matching object. The cap is
            // 500 rather than the paged reader's 100 because nothing here is a person choosing a page size: the
            // virtualiser asks for as many rows as the viewport needs, and a clamp it can reach silently renders the
            // shortfall as blank rows. See the cap's own comment in MetaverseRepository for how 500 was derived.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsWindowWithNoTotalAsync()
    {
        var searchId = await SeedAsync(10);
        var (jim, search) = await LoadAsync(searchId);

        var result = await jim.Metaverse.GetMetaverseObjectHeadersRangeAsync(
            search, offset: 3, count: 3, sortBy: DisplayName, sortDescending: false, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches", which is
            // the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(r => r.CachedDisplayName), Is.EqualTo(new[] { "User 04", "User 05", "User 06" }));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        var searchId = await SeedAsync(10);
        var (jim, search) = await LoadAsync(searchId);

        // Skipping the count must change what the caller is told about the total and nothing else; the window itself
        // comes from the same filtered, sorted query either way.
        var counted = await jim.Metaverse.GetMetaverseObjectHeadersRangeAsync(
            search, offset: 5, count: 4, sortBy: DisplayName, sortDescending: true);
        var uncounted = await jim.Metaverse.GetMetaverseObjectHeadersRangeAsync(
            search, offset: 5, count: 4, sortBy: DisplayName, sortDescending: true, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(r => r.CachedDisplayName),
                Is.EqualTo(counted.Results.Select(r => r.CachedDisplayName)));
        }
    }
}
