// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the offset/count Connected System Object header read
/// (<see cref="JIM.Application.Servers.ConnectedSystemServer.GetConnectedSystemObjectHeadersRangeAsync"/>) that
/// backs the virtualised (infinite-scroll) Connector Space list. The query it shares with the paged reader leans
/// on EF.Functions.ILike and correlated typed-column subqueries, which the EF Core in-memory provider cannot
/// execute; the windowing, count-skipping and filter semantics are only verifiable against a real database.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemObjectHeaderRangeDatabaseTests
{
    private const string DisplayNameAttributeName = "displayName";
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Connected System Object header range tests.");

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
    /// Seeds a Connected System with one "user" object type carrying a displayName attribute (a name-candidate,
    /// so the header projection resolves it as the Display Name), plus <paramref name="count"/> Connected System
    /// Objects named "User 001", "User 002", ... with Created timestamps staggered in that order, so sorting by
    /// the created key ascending yields numeric name order. Returns the Connected System's id.
    /// </summary>
    private async Task<int> SeedAsync(int count)
    {
        await using var ctx = NewContext();
        var (connectedSystem, objectType, displayNameAttribute) = BuildSystem(ctx, "user");
        await ctx.SaveChangesAsync();

        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= count; i++)
            ctx.Add(BuildCso(connectedSystem, objectType, displayNameAttribute, $"User {i:D3}", baseTime.AddSeconds(i)));

        await ctx.SaveChangesAsync();
        return connectedSystem.Id;
    }

    /// <summary>
    /// Seeds a Connected System whose objects vary by status, object type and join type, so each filter has
    /// something to include and something to exclude: three "user" objects (User A: Normal/NotJoined,
    /// User B: Obsolete/Joined, User C: Normal/Projected) and one "group" object (Group A: Normal/NotJoined).
    /// Returns the Connected System's id and the group object type's id.
    /// </summary>
    private async Task<(int ConnectedSystemId, int GroupTypeId)> SeedMixedAsync()
    {
        await using var ctx = NewContext();
        var (connectedSystem, userType, userDisplayName) = BuildSystem(ctx, "user");
        var groupType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = connectedSystem, Selected = true };
        var groupDisplayName = new ConnectedSystemObjectTypeAttribute
        {
            Name = DisplayNameAttributeName,
            ConnectedSystemObjectType = groupType,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true
        };
        groupType.Attributes.Add(groupDisplayName);
        ctx.Add(groupType);
        await ctx.SaveChangesAsync();

        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var userA = BuildCso(connectedSystem, userType, userDisplayName, "User A", baseTime.AddSeconds(1));
        var userB = BuildCso(connectedSystem, userType, userDisplayName, "User B", baseTime.AddSeconds(2));
        userB.Status = ConnectedSystemObjectStatus.Obsolete;
        userB.JoinType = ConnectedSystemObjectJoinType.Joined;
        var userC = BuildCso(connectedSystem, userType, userDisplayName, "User C", baseTime.AddSeconds(3));
        userC.JoinType = ConnectedSystemObjectJoinType.Projected;
        var groupA = BuildCso(connectedSystem, groupType, groupDisplayName, "Group A", baseTime.AddSeconds(4));

        ctx.AddRange(userA, userB, userC, groupA);
        await ctx.SaveChangesAsync();
        return (connectedSystem.Id, groupType.Id);
    }

    private static (ConnectedSystem ConnectedSystem, ConnectedSystemObjectType ObjectType, ConnectedSystemObjectTypeAttribute DisplayNameAttribute) BuildSystem(
        JimDbContext ctx, string objectTypeName)
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = objectTypeName, ConnectedSystem = connectedSystem, Selected = true };
        var displayNameAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = DisplayNameAttributeName,
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true
        };
        objectType.Attributes.Add(displayNameAttribute);
        ctx.AddRange(connectorDefinition, connectedSystem, objectType);
        return (connectedSystem, objectType, displayNameAttribute);
    }

    private static ConnectedSystemObject BuildCso(
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType objectType,
        ConnectedSystemObjectTypeAttribute displayNameAttribute,
        string displayName,
        DateTime created)
    {
        return new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = connectedSystem,
            Type = objectType,
            Created = created,
            AttributeValues =
            [
                new ConnectedSystemObjectAttributeValue { Attribute = displayNameAttribute, StringValue = displayName }
            ]
        };
    }

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    [Test]
    public async Task Range_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        var systemId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            systemId, offset: 0, count: 3, sortBy: CreatedSortKey, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(r => r.DisplayName), Is.EqualTo(new[] { "User 001", "User 002", "User 003" }));
        }
    }

    [Test]
    public async Task Range_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        var systemId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            systemId, offset: 3, count: 3, sortBy: CreatedSortKey, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(r => r.DisplayName), Is.EqualTo(new[] { "User 004", "User 005", "User 006" }));
        }
    }

    [Test]
    public async Task Range_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        var systemId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
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

        var range = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            systemId, offset: 0, count: 10, sortBy: CreatedSortKey, sortDescending: false);
        var paged = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersAsync(
            systemId, page: 1, pageSize: 10, sortBy: CreatedSortKey, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(r => r.DisplayName), Is.EqualTo(paged.Results.Select(r => r.DisplayName)));
        }
    }

    [Test]
    public async Task Range_CountAboveCap_ClampsToFiveHundredAsync()
    {
        var systemId = await SeedAsync(505);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
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

        var result = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            systemId, offset: 3, count: 3, sortBy: CreatedSortKey, sortDescending: false, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches", which is
            // the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(r => r.DisplayName), Is.EqualTo(new[] { "User 004", "User 005", "User 006" }));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        var systemId = await SeedAsync(10);
        var jim = NewJim();

        // Skipping the count must change what the caller is told about the total and nothing else; the window itself
        // comes from the same filtered, sorted query either way.
        var counted = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            systemId, offset: 5, count: 4, sortBy: CreatedSortKey, sortDescending: true);
        var uncounted = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            systemId, offset: 5, count: 4, sortBy: CreatedSortKey, sortDescending: true, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(r => r.DisplayName),
                Is.EqualTo(counted.Results.Select(r => r.DisplayName)));
        }
    }

    [Test]
    public async Task Range_StatusFilter_RestrictsWindowAndTotalAsync()
    {
        var (systemId, _) = await SeedMixedAsync();
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            systemId, offset: 0, count: 10, sortBy: CreatedSortKey, sortDescending: false,
            statusFilter: [ConnectedSystemObjectStatus.Obsolete]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(r => r.DisplayName), Is.EqualTo(new[] { "User B" }));
        }
    }

    [Test]
    public async Task Range_ObjectTypeFilter_RestrictsWindowAndTotalAsync()
    {
        var (systemId, groupTypeId) = await SeedMixedAsync();
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            systemId, offset: 0, count: 10, sortBy: CreatedSortKey, sortDescending: false,
            objectTypeFilter: [groupTypeId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(r => r.DisplayName), Is.EqualTo(new[] { "Group A" }));
        }
    }

    [Test]
    public async Task Range_JoinTypeFilter_RestrictsWindowAndTotalAsync()
    {
        var (systemId, _) = await SeedMixedAsync();
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            systemId, offset: 0, count: 10, sortBy: CreatedSortKey, sortDescending: false,
            joinTypeFilter: [ConnectedSystemObjectJoinType.Projected]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(r => r.DisplayName), Is.EqualTo(new[] { "User C" }));
        }
    }

    [Test]
    public async Task Range_Search_MatchesDisplayNameAndRestrictsTotalAsync()
    {
        var (systemId, _) = await SeedMixedAsync();
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            systemId, offset: 0, count: 10, searchQuery: "user", sortBy: CreatedSortKey, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            // Case-insensitive, matching the paged reader's search semantics over the name-candidate attributes.
            Assert.That(result.TotalResults, Is.EqualTo(3));
            Assert.That(result.Results.Select(r => r.DisplayName), Is.EqualTo(new[] { "User A", "User B", "User C" }));
        }
    }
}
