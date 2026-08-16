// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the offset/count Metaverse Attribute header read
/// (<see cref="JIM.Application.Servers.MetaverseServer.GetMetaverseAttributeHeadersRangeAsync"/>) that backs the
/// virtualised (infinite-scroll) Schema Attributes list. The read filters names with ILIKE, which the EF Core
/// in-memory provider cannot translate, so the windowing, count-skipping and search semantics are only verifiable
/// against a real database.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseAttributeHeaderRangeDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL attribute-header-range tests.");

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
    /// Seeds <paramref name="count"/> Metaverse Attributes named "Attribute 001", "Attribute 002", ... (zero-padded
    /// so lexical order matches numeric order under the read's name sort).
    /// </summary>
    private async Task SeedAsync(int count)
    {
        await using var ctx = NewContext();
        for (var i = 1; i <= count; i++)
        {
            ctx.MetaverseAttributes.Add(new MetaverseAttribute
            {
                Name = $"Attribute {i:D3}",
                Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued
            });
        }
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds Metaverse Attributes with the given names, in the given order, for the search-filter tests.
    /// </summary>
    private async Task SeedNamedAsync(params string[] names)
    {
        await using var ctx = NewContext();
        foreach (var name in names)
        {
            ctx.MetaverseAttributes.Add(new MetaverseAttribute
            {
                Name = name,
                Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued
            });
        }
        await ctx.SaveChangesAsync();
    }

    private JimApplication CreateJim() => new(new PostgresDataRepository(NewContext()));

    [Test]
    public async Task Range_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        await SeedAsync(10);
        var jim = CreateJim();

        var result = await jim.Metaverse.GetMetaverseAttributeHeadersRangeAsync(offset: 0, count: 3, sortBy: "name", sortDescending: false);

        Assert.That(result.TotalResults, Is.EqualTo(10));
        Assert.That(result.Results.Select(r => r.Name), Is.EqualTo(new[] { "Attribute 001", "Attribute 002", "Attribute 003" }));
    }

    [Test]
    public async Task Range_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        await SeedAsync(10);
        var jim = CreateJim();

        var result = await jim.Metaverse.GetMetaverseAttributeHeadersRangeAsync(offset: 3, count: 3, sortBy: "name", sortDescending: false);

        Assert.That(result.TotalResults, Is.EqualTo(10));
        Assert.That(result.Results.Select(r => r.Name), Is.EqualTo(new[] { "Attribute 004", "Attribute 005", "Attribute 006" }));
    }

    [Test]
    public async Task Range_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        await SeedAsync(10);
        var jim = CreateJim();

        var result = await jim.Metaverse.GetMetaverseAttributeHeadersRangeAsync(offset: 100, count: 10, sortBy: "name", sortDescending: false);

        // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
        Assert.That(result.TotalResults, Is.EqualTo(10));
        Assert.That(result.Results, Is.Empty);
    }

    [Test]
    public async Task Range_CountAboveCap_ClampsToFiveHundredAsync()
    {
        await SeedAsync(505);
        var jim = CreateJim();

        var result = await jim.Metaverse.GetMetaverseAttributeHeadersRangeAsync(offset: 0, count: 1000, sortBy: "name", sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every matching attribute. The
            // cap is 500 rather than the paged reader's 100 for the same reason as the Metaverse Object header range
            // read: a virtualiser asks for however many rows the viewport needs, and a cap it can reach truncates
            // the window silently. See the cap's own comment in MetaverseRepository.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsWindowWithNoTotalAsync()
    {
        await SeedAsync(10);
        var jim = CreateJim();

        var result = await jim.Metaverse.GetMetaverseAttributeHeadersRangeAsync(
            offset: 3, count: 3, sortBy: "name", sortDescending: false, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches", which is
            // the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(r => r.Name), Is.EqualTo(new[] { "Attribute 004", "Attribute 005", "Attribute 006" }));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        await SeedAsync(10);
        var jim = CreateJim();

        // Skipping the count must change what the caller is told about the total and nothing else; the window itself
        // comes from the same filtered, sorted query either way.
        var counted = await jim.Metaverse.GetMetaverseAttributeHeadersRangeAsync(
            offset: 5, count: 4, sortBy: "name", sortDescending: true);
        var uncounted = await jim.Metaverse.GetMetaverseAttributeHeadersRangeAsync(
            offset: 5, count: 4, sortBy: "name", sortDescending: true, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(r => r.Name), Is.EqualTo(counted.Results.Select(r => r.Name)));
        }
    }

    [Test]
    public async Task Range_SearchFilter_NarrowsWindowAndTotalCaseInsensitivelyAsync()
    {
        await SeedNamedAsync("Display Name", "Email Address", "Email Alias", "Given Name");
        var jim = CreateJim();

        var result = await jim.Metaverse.GetMetaverseAttributeHeadersRangeAsync(
            offset: 0, count: 10, searchQuery: "email", sortBy: "name", sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            // The filter is the same ILIKE name match as the paged read, so "email" finds "Email ..." regardless of case.
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(r => r.Name), Is.EqualTo(new[] { "Email Address", "Email Alias" }));
        }
    }

    [Test]
    public async Task Range_FullWindow_MatchesPagedReaderAsync()
    {
        await SeedAsync(10);
        var jim = CreateJim();

        var range = await jim.Metaverse.GetMetaverseAttributeHeadersRangeAsync(offset: 0, count: 10, sortBy: "name", sortDescending: false);
        var paged = await jim.Metaverse.GetMetaverseAttributeHeadersAsync(page: 1, pageSize: 10, sortBy: "name", sortDescending: false);

        Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
        Assert.That(range.Results.Select(r => r.Name), Is.EqualTo(paged.Results.Select(r => r.Name)));
    }
}
