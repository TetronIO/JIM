// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the offset/count Run Profile Execution Item header range read
/// (<see cref="JIM.Application.Servers.ActivityServer.GetActivityRunProfileExecutionItemHeadersRangeAsync"/>)
/// that backs the virtualised (infinite-scroll) execution-item grid. Its search predicate uses
/// <c>EF.Functions.ILike</c>, and its display-name and external-id sorts are correlated subqueries over the
/// Connected System Object's attribute values (the external id coalescing whichever typed column holds the
/// anchor); none of that is executable on the EF Core in-memory provider, so the window, the count-skipping
/// contract and the sort keys are only verifiable here. The context is NoTracking, matching JIM.Web's.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ActivityRunProfileExecutionItemHeaderRangeDatabaseTests
{
    private const string DisplayNameAttributeName = "displayName";
    private const string ExternalIdAttributeName = "entryUUID";
    private const string DisplayNameSortKey = "displayname";
    private const string ExternalIdSortKey = "externalid";

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL execution item header range tests.");

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

    [Test]
    public async Task Range_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        var activityId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 3, sortBy: DisplayNameSortKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(h => h.DisplayName),
                Is.EqualTo(new[] { "User 001", "User 002", "User 003" }));
        }
    }

    [Test]
    public async Task Range_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        var activityId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 3, count: 3, sortBy: DisplayNameSortKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(h => h.DisplayName),
                Is.EqualTo(new[] { "User 004", "User 005", "User 006" }));
        }
    }

    [Test]
    public async Task Range_ConsecutiveWindows_PartitionTheMatchSetExactlyAsync()
    {
        // Windows are only stable under a total order. Every item here carries the same display name, so the
        // named sort key ties for all twenty rows and only the id tie-break can keep the two windows from
        // repeating and skipping rows; PostgreSQL is free to return tied rows in any order per query.
        var activityId = await SeedAsync(20, displayNamePrefix: "Same");
        var jim = NewJim();

        var first = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 10, sortBy: DisplayNameSortKey);
        var second = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 10, count: 10, sortBy: DisplayNameSortKey);

        var seen = first.Results.Select(h => h.Id).Concat(second.Results.Select(h => h.Id)).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(seen, Has.Count.EqualTo(20));
            Assert.That(seen.Distinct().Count(), Is.EqualTo(20));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        var activityId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 3, count: 3, sortBy: DisplayNameSortKey, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches".
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(h => h.DisplayName),
                Is.EqualTo(new[] { "User 004", "User 005", "User 006" }));
        }
    }

    [Test]
    public async Task Range_SearchOnDisplayName_IsCaseInsensitiveAndRestrictsTotalAsync()
    {
        var activityId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 10, searchQuery: "user 004", sortBy: DisplayNameSortKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(h => h.DisplayName), Is.EqualTo(new[] { "User 004" }));
        }
    }

    [Test]
    public async Task Range_SearchOnExternalId_MatchesTheRenderedAnchorAsync()
    {
        var activityId = await SeedAsync(10);
        var jim = NewJim();

        // The anchor is a GUID-typed value, so this only matches if the search renders the typed column the
        // same way the External Id column does (#1286) rather than reading StringValue alone.
        var result = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 10, searchQuery: ExternalIdFor(4).ToString(), sortBy: DisplayNameSortKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(h => h.DisplayName), Is.EqualTo(new[] { "User 004" }));
        }
    }

    [Test]
    public async Task Range_SortByLiveDisplayName_OrdersByTheConnectedSystemObjectsNameAsync()
    {
        // The display names are seeded in the reverse of alphabetical order, and the snapshot columns are
        // deliberately left null, so this passes only if the correlated live-name subquery drives the sort.
        var activityId = await SeedNamedAsync(["Zoe Zeta", "Alan Alpha", "Mia Mu"]);
        var jim = NewJim();

        var result = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 10, sortBy: DisplayNameSortKey);

        Assert.That(result.Results.Select(h => h.DisplayName),
            Is.EqualTo(new[] { "Alan Alpha", "Mia Mu", "Zoe Zeta" }));
    }

    [Test]
    public async Task Range_SortByExternalId_OrdersByTheRenderedAnchorAsync()
    {
        var activityId = await SeedAsync(5);
        var jim = NewJim();

        var result = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 10, sortBy: ExternalIdSortKey);

        // The anchors are GUIDs rendered as text, so the expected order is their rendered form sorted as text.
        var expected = Enumerable.Range(1, 5).Select(i => ExternalIdFor(i).ToString()).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.That(result.Results.Select(h => h.ExternalIdValue), Is.EqualTo(expected));
    }

    [Test]
    public async Task Range_ObjectTypeFilter_RestrictsWindowAndTotalAsync()
    {
        var activityId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 10, sortBy: DisplayNameSortKey, objectTypeFilter: ["group"]);

        using (Assert.EnterMultipleScope())
        {
            // Every seeded object is of the "user" type, so filtering to "group" must empty the window and
            // the total alike; the filter has to reach the count query too, not only the window query.
            Assert.That(result.TotalResults, Is.Zero);
            Assert.That(result.Results, Is.Empty);
        }
    }

    [Test]
    public async Task Range_OutcomeTypeFilter_MatchesTheDenormalisedOutcomeSummaryAsync()
    {
        // The filter matches "<OutcomeType>:" tokens inside the denormalised OutcomeSummary via a captured-token
        // Contains, which only a relational provider translates.
        var activityId = await SeedNamedAsync(
            ["Projected One", "Joined One"],
            outcomeSummaries: ["Projected:1,AttributeFlow:3", "Joined:1"]);
        var jim = NewJim();

        var result = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 10, sortBy: DisplayNameSortKey,
            outcomeTypeFilter: [ActivityRunProfileExecutionItemSyncOutcomeType.Projected]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(h => h.DisplayName), Is.EqualTo(new[] { "Projected One" }));
        }
    }

    [Test]
    public async Task Range_FullWindow_MatchesPagedReaderAsync()
    {
        var activityId = await SeedAsync(10);
        var jim = NewJim();

        var range = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 10, sortBy: DisplayNameSortKey);
        var paged = await jim.Activities.GetActivityRunProfileExecutionItemHeadersAsync(
            activityId, page: 1, pageSize: 10, sortBy: DisplayNameSortKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(h => h.Id), Is.EqualTo(paged.Results.Select(h => h.Id)));
        }
    }

    /// <summary>
    /// The deterministic anchor value for the nth seeded object, so a test can search for one by its rendered
    /// external id without reading it back.
    /// </summary>
    private static Guid ExternalIdFor(int ordinal) => new($"00000000-0000-0000-0000-{ordinal:D12}");

    /// <summary>
    /// Seeds an Activity with <paramref name="count"/> Run Profile Execution Items, each pointing at a live
    /// Connected System Object carrying a displayName ("User 001", "User 002", ...) and a GUID-typed anchor.
    /// Returns the Activity's id.
    /// </summary>
    private async Task<Guid> SeedAsync(int count, string displayNamePrefix = "User")
    {
        var names = Enumerable.Range(1, count).Select(i => $"{displayNamePrefix} {i:D3}").ToList();
        return await SeedNamedAsync(names);
    }

    /// <summary>
    /// Seeds an Activity with one Run Profile Execution Item per name, each pointing at a live Connected System
    /// Object carrying that displayName and a GUID-typed anchor. The items' snapshot columns are left null, so
    /// any display-name or external-id value the read returns must have come from the live object.
    /// <paramref name="outcomeSummaries"/>, when given, sets each item's denormalised outcome summary in the
    /// same order.
    /// </summary>
    private async Task<Guid> SeedNamedAsync(IReadOnlyList<string> displayNames, IReadOnlyList<string>? outcomeSummaries = null)
    {
        await using var ctx = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = connectedSystem, Selected = true };
        var displayNameAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = DisplayNameAttributeName,
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true
        };
        var externalIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = ExternalIdAttributeName,
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true
        };
        objectType.Attributes.Add(displayNameAttribute);
        objectType.Attributes.Add(externalIdAttribute);
        ctx.AddRange(connectorDefinition, connectedSystem, objectType);
        await ctx.SaveChangesAsync();

        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.Complete,
            Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        for (var i = 0; i < displayNames.Count; i++)
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystem = connectedSystem,
                Type = objectType,
                ExternalIdAttributeId = externalIdAttribute.Id,
                Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
                AttributeValues =
                [
                    new ConnectedSystemObjectAttributeValue { Attribute = displayNameAttribute, StringValue = displayNames[i] },
                    new ConnectedSystemObjectAttributeValue { Attribute = externalIdAttribute, GuidValue = ExternalIdFor(i + 1) }
                ]
            };
            ctx.Add(cso);
            ctx.ActivityRunProfileExecutionItems.Add(new ActivityRunProfileExecutionItem
            {
                Id = Guid.NewGuid(),
                ActivityId = activity.Id,
                ConnectedSystemObject = cso,
                ObjectChangeType = ObjectChangeType.Updated,
                OutcomeSummary = outcomeSummaries?[i]
            });
        }

        await ctx.SaveChangesAsync();
        return activity.Id;
    }
}
