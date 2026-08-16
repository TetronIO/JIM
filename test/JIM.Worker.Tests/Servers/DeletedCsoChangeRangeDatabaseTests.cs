// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the offset/count deleted Connected System Object change read
/// (<see cref="JIM.Application.Servers.ConnectedSystemServer.GetDeletedCsoChangesRangeAsync"/>) that backs the
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
public class DeletedCsoChangeRangeDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL deleted Connected System Object change range tests.");

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
    /// Seeds a Connected System and <paramref name="count"/> deletion change records whose ChangeTime timestamps
    /// are staggered in creation order, so the read's fixed newest-first ordering yields descending numeric label
    /// order ("EXT-010" first for a seed of 10). Each record's preserved External Id is "EXT-001", "EXT-002", ...
    /// which doubles as the search target. One non-deletion (Update) change is seeded on top with the newest
    /// timestamp of all, so any window or total that failed to restrict to deletions is caught by the assertions.
    /// Returns the Connected System's id.
    /// </summary>
    private async Task<int> SeedAsync(int count)
    {
        await using var ctx = NewContext();
        var connectedSystem = BuildSystem(ctx);
        await ctx.SaveChangesAsync();

        for (var i = 1; i <= count; i++)
            ctx.Add(BuildDeletionChange(connectedSystem.Id, i));

        ctx.Add(new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ChangeType = ObjectChangeType.Updated,
            ChangeTime = BaseTime.AddSeconds(count + 1),
            DeletedObjectExternalId = "EXT-NOT-DELETED"
        });

        await ctx.SaveChangesAsync();
        return connectedSystem.Id;
    }

    /// <summary>
    /// Seeds two Connected Systems with deletion changes in each, so the Connected System filter has something
    /// to include and something to exclude. Returns both systems' ids; the first carries "EXT-001" to "EXT-003",
    /// the second "OTHER-001" to "OTHER-002".
    /// </summary>
    private async Task<(int FirstSystemId, int SecondSystemId)> SeedTwoSystemsAsync()
    {
        await using var ctx = NewContext();
        var first = BuildSystem(ctx);
        var connectorDefinition = new ConnectorDefinition { Name = "Second Test Connector", BuiltIn = true };
        var second = new ConnectedSystem { Name = "Yellowstone", ConnectorDefinition = connectorDefinition };
        ctx.AddRange(connectorDefinition, second);
        await ctx.SaveChangesAsync();

        for (var i = 1; i <= 3; i++)
            ctx.Add(BuildDeletionChange(first.Id, i));

        for (var i = 1; i <= 2; i++)
            ctx.Add(BuildDeletionChange(second.Id, i, externalIdPrefix: "OTHER"));

        await ctx.SaveChangesAsync();
        return (first.Id, second.Id);
    }

    private static ConnectedSystem BuildSystem(JimDbContext ctx)
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        ctx.AddRange(connectorDefinition, connectedSystem);
        return connectedSystem;
    }

    private static ConnectedSystemObjectChange BuildDeletionChange(int connectedSystemId, int ordinal, string externalIdPrefix = "EXT")
    {
        return new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = BaseTime.AddSeconds(ordinal),
            DeletedObjectExternalId = $"{externalIdPrefix}-{ordinal:D3}",
            DeletedObjectDisplayName = $"Deleted Object {ordinal:D3}",
            DeletedConnectedSystemObjectId = Guid.NewGuid()
        };
    }

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    [Test]
    public async Task Range_FirstWindow_ReturnsNewestFirstSliceAndFullTotalAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetDeletedCsoChangesRangeAsync(offset: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(r => r.DeletedObjectExternalId), Is.EqualTo(new[] { "EXT-010", "EXT-009", "EXT-008" }));
        }
    }

    [Test]
    public async Task Range_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetDeletedCsoChangesRangeAsync(offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(r => r.DeletedObjectExternalId), Is.EqualTo(new[] { "EXT-007", "EXT-006", "EXT-005" }));
        }
    }

    [Test]
    public async Task Range_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetDeletedCsoChangesRangeAsync(offset: 100, count: 10);

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

        var result = await jim.ConnectedSystems.GetDeletedCsoChangesRangeAsync(offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches", which is
            // the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(r => r.DeletedObjectExternalId), Is.EqualTo(new[] { "EXT-007", "EXT-006", "EXT-005" }));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        // Skipping the count must change what the caller is told about the total and nothing else; the window itself
        // comes from the same filtered, sorted query either way.
        var counted = await jim.ConnectedSystems.GetDeletedCsoChangesRangeAsync(offset: 5, count: 4);
        var uncounted = await jim.ConnectedSystems.GetDeletedCsoChangesRangeAsync(offset: 5, count: 4, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(r => r.DeletedObjectExternalId),
                Is.EqualTo(counted.Results.Select(r => r.DeletedObjectExternalId)));
        }
    }

    [Test]
    public async Task Range_CountAboveCap_ClampsToFiveHundredAsync()
    {
        await SeedAsync(505);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetDeletedCsoChangesRangeAsync(offset: 0, count: 1000);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every matching record. The cap
            // is 500, matching the header range reads: nothing here is a person choosing a page size, and a clamp a
            // viewport could reach would silently render the shortfall as blank rows. See the constant's own comment
            // in ConnectedSystemRepository for how 500 was derived.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public async Task Range_ConnectedSystemFilter_RestrictsWindowAndTotalAsync()
    {
        var (_, secondSystemId) = await SeedTwoSystemsAsync();
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetDeletedCsoChangesRangeAsync(
            offset: 0, count: 10, connectedSystemId: secondSystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(r => r.DeletedObjectExternalId), Is.EqualTo(new[] { "OTHER-002", "OTHER-001" }));
        }
    }

    [Test]
    public async Task Range_DateRange_RestrictsWindowAndTotalAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        // Both bounds are inclusive, matching the paged reader's semantics.
        var result = await jim.ConnectedSystems.GetDeletedCsoChangesRangeAsync(
            offset: 0, count: 10, fromDate: BaseTime.AddSeconds(4), toDate: BaseTime.AddSeconds(6));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(3));
            Assert.That(result.Results.Select(r => r.DeletedObjectExternalId), Is.EqualTo(new[] { "EXT-006", "EXT-005", "EXT-004" }));
        }
    }

    [Test]
    public async Task Range_Search_MatchesExternalIdCaseInsensitivelyAndRestrictsTotalAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        // Case-insensitive over the preserved DeletedObjectExternalId string: the value the page renders and the
        // one that reliably survives the Connected System Object's deletion.
        var result = await jim.ConnectedSystems.GetDeletedCsoChangesRangeAsync(offset: 0, count: 10, externalIdSearch: "ext-004");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(r => r.DeletedObjectExternalId), Is.EqualTo(new[] { "EXT-004" }));
        }
    }

    [Test]
    public async Task Range_FullWindow_MatchesPagedReaderAsync()
    {
        await SeedAsync(10);
        var jim = NewJim();

        var range = await jim.ConnectedSystems.GetDeletedCsoChangesRangeAsync(offset: 0, count: 10);
        var (pagedItems, pagedTotal) = await jim.ConnectedSystems.GetDeletedCsoChangesAsync(page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(pagedTotal));
            Assert.That(range.Results.Select(r => r.DeletedObjectExternalId),
                Is.EqualTo(pagedItems.Select(r => r.DeletedObjectExternalId)));
        }
    }
}
