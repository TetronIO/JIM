// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the offset/count Pending Export header read
/// (<see cref="JIM.Application.Servers.ConnectedSystemServer.GetPendingExportHeadersRangeAsync"/>) that backs the
/// virtualised (infinite-scroll) Pending Export list. The query it shares with the paged reader leans on
/// EF.Functions.ILike, which the EF Core in-memory provider cannot execute; the windowing, count-skipping and
/// filter semantics are only verifiable against a real database.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportHeaderRangeDatabaseTests
{
    private const string CreatedSortKey = "created";

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export header range tests.");

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
    /// Seeds a Connected System and <paramref name="count"/> Pending Exports with CreatedAt timestamps staggered
    /// in creation order, so sorting by the created key ascending yields numeric label order. Each row carries a
    /// distinct LastErrorMessage "Export 001", "Export 002", ... because that is the one label the header exposes
    /// that this fixture can seed without a whole Metaverse Object or Connected System Object graph; it doubles as
    /// the search target, since the search predicate matches error messages. Returns the Connected System's id.
    /// </summary>
    private async Task<int> SeedAsync(int count)
    {
        await using var ctx = NewContext();
        var connectedSystem = BuildSystem(ctx);
        await ctx.SaveChangesAsync();

        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= count; i++)
        {
            ctx.Add(new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystem = connectedSystem,
                ChangeType = PendingExportChangeType.Create,
                Status = PendingExportStatus.Pending,
                CreatedAt = baseTime.AddSeconds(i),
                LastErrorMessage = $"Export {i:D3}"
            });
        }

        await ctx.SaveChangesAsync();
        return connectedSystem.Id;
    }

    /// <summary>
    /// Seeds a Connected System whose Pending Exports vary by status, so the status filter has something to
    /// include and something to exclude: "Export A" (Pending), "Export B" (Failed) and "Export C" (Exported).
    /// Returns the Connected System's id.
    /// </summary>
    private async Task<int> SeedMixedStatusesAsync()
    {
        await using var ctx = NewContext();
        var connectedSystem = BuildSystem(ctx);
        await ctx.SaveChangesAsync();

        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ctx.AddRange(
            new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystem = connectedSystem,
                ChangeType = PendingExportChangeType.Create,
                Status = PendingExportStatus.Pending,
                CreatedAt = baseTime.AddSeconds(1),
                LastErrorMessage = "Export A"
            },
            new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystem = connectedSystem,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Failed,
                CreatedAt = baseTime.AddSeconds(2),
                LastErrorMessage = "Export B"
            },
            new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystem = connectedSystem,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Exported,
                CreatedAt = baseTime.AddSeconds(3),
                LastErrorMessage = "Export C"
            });
        await ctx.SaveChangesAsync();
        return connectedSystem.Id;
    }

    private static ConnectedSystem BuildSystem(JimDbContext ctx)
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        ctx.AddRange(connectorDefinition, connectedSystem);
        return connectedSystem;
    }

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    [Test]
    public async Task Range_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        var systemId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetPendingExportHeadersRangeAsync(
            systemId, offset: 0, count: 3, sortBy: CreatedSortKey, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(r => r.LastErrorMessage), Is.EqualTo(new[] { "Export 001", "Export 002", "Export 003" }));
        }
    }

    [Test]
    public async Task Range_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        var systemId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetPendingExportHeadersRangeAsync(
            systemId, offset: 3, count: 3, sortBy: CreatedSortKey, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(r => r.LastErrorMessage), Is.EqualTo(new[] { "Export 004", "Export 005", "Export 006" }));
        }
    }

    [Test]
    public async Task Range_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        var systemId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetPendingExportHeadersRangeAsync(
            systemId, offset: 100, count: 10, sortBy: CreatedSortKey, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results, Is.Empty);
        }
    }

    [Test]
    public async Task Range_FullWindow_MatchesPagedReaderAsync()
    {
        var systemId = await SeedAsync(10);
        var jim = NewJim();

        var range = await jim.ConnectedSystems.GetPendingExportHeadersRangeAsync(
            systemId, offset: 0, count: 10, sortBy: CreatedSortKey, sortDescending: false);
        var paged = await jim.ConnectedSystems.GetPendingExportHeadersAsync(
            systemId, page: 1, pageSize: 10, sortBy: CreatedSortKey, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(r => r.LastErrorMessage), Is.EqualTo(paged.Results.Select(r => r.LastErrorMessage)));
        }
    }

    [Test]
    public async Task Range_CountAboveCap_ClampsToFiveHundredAsync()
    {
        var systemId = await SeedAsync(505);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetPendingExportHeadersRangeAsync(
            systemId, offset: 0, count: 1000, sortBy: CreatedSortKey, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every matching object. The cap
            // is 500 rather than the paged reader's 100 because nothing here is a person choosing a page size: the
            // virtualiser asks for as many rows as the viewport needs, and a clamp it can reach silently renders the
            // shortfall as blank rows. See the cap's own comment in ConnectedSystemRepository for how 500 was derived.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsWindowWithNoTotalAsync()
    {
        var systemId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetPendingExportHeadersRangeAsync(
            systemId, offset: 3, count: 3, sortBy: CreatedSortKey, sortDescending: false, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches", which is
            // the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(r => r.LastErrorMessage), Is.EqualTo(new[] { "Export 004", "Export 005", "Export 006" }));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        var systemId = await SeedAsync(10);
        var jim = NewJim();

        // Skipping the count must change what the caller is told about the total and nothing else; the window itself
        // comes from the same filtered, sorted query either way.
        var counted = await jim.ConnectedSystems.GetPendingExportHeadersRangeAsync(
            systemId, offset: 5, count: 4, sortBy: CreatedSortKey, sortDescending: true);
        var uncounted = await jim.ConnectedSystems.GetPendingExportHeadersRangeAsync(
            systemId, offset: 5, count: 4, sortBy: CreatedSortKey, sortDescending: true, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(r => r.LastErrorMessage),
                Is.EqualTo(counted.Results.Select(r => r.LastErrorMessage)));
        }
    }

    [Test]
    public async Task Range_StatusFilter_RestrictsWindowAndTotalAsync()
    {
        var systemId = await SeedMixedStatusesAsync();
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetPendingExportHeadersRangeAsync(
            systemId, offset: 0, count: 10, statusFilters: [PendingExportStatus.Failed],
            sortBy: CreatedSortKey, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(r => r.LastErrorMessage), Is.EqualTo(new[] { "Export B" }));
        }
    }

    [Test]
    public async Task Range_Search_MatchesErrorMessageAndRestrictsTotalAsync()
    {
        var systemId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetPendingExportHeadersRangeAsync(
            systemId, offset: 0, count: 10, searchQuery: "export 004", sortBy: CreatedSortKey, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            // Case-insensitive, matching the paged reader's search semantics over the error message.
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(r => r.LastErrorMessage), Is.EqualTo(new[] { "Export 004" }));
        }
    }
}
