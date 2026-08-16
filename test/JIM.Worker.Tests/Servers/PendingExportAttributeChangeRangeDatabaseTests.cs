// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the offset/count single-attribute Pending Export change range read
/// (<see cref="JIM.Application.Servers.ConnectedSystemServer.GetPendingExportAttributeChangesRangeAsync"/>) that
/// backs a virtualised (infinite-scroll) multi-valued attribute on a Pending Export. Its search predicate is
/// two <c>EF.Functions.ILike</c> clauses the EF Core in-memory provider cannot execute, so the search, and its
/// interaction with the window and the total, are only verifiable here.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportAttributeChangeRangeDatabaseTests
{
    private const string MemberAttributeName = "member";

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export attribute change range tests.");

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
    public async Task Range_MidWindow_ReturnsCorrectSliceAndFullTotalAsync()
    {
        var pendingExportId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(c => c.StringValue),
                Is.EqualTo(new[] { "CN=Member 004", "CN=Member 005", "CN=Member 006" }));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        var pendingExportId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches".
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(c => c.StringValue),
                Is.EqualTo(new[] { "CN=Member 004", "CN=Member 005", "CN=Member 006" }));
        }
    }

    [Test]
    public async Task Range_Search_IsCaseInsensitiveAndRestrictsTotalAsync()
    {
        var pendingExportId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 0, count: 10, searchText: "cn=MEMBER 004");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(c => c.StringValue), Is.EqualTo(new[] { "CN=Member 004" }));
        }
    }

    [Test]
    public async Task Range_SearchOnAnUnresolvedReference_MatchesThatColumnTooAsync()
    {
        var pendingExportId = await SeedAsync(3, unresolvedReferenceFor: 2);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 0, count: 10, searchText: "UNRESOLVED");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Single().UnresolvedReferenceValue, Is.EqualTo("CN=Unresolved 002"));
        }
    }

    [Test]
    public async Task Range_ConsecutiveWindows_PartitionTheChangesExactlyAsync()
    {
        var pendingExportId = await SeedAsync(20);
        var jim = NewJim();

        var first = await jim.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 0, count: 10);
        var second = await jim.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 10, count: 10);

        var seen = first.Results.Select(c => c.Id).Concat(second.Results.Select(c => c.Id)).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(seen, Has.Count.EqualTo(20));
            Assert.That(seen.Distinct().Count(), Is.EqualTo(20));
        }
    }

    [Test]
    public async Task Range_FullWindow_MatchesPagedReaderAsync()
    {
        var pendingExportId = await SeedAsync(10);
        var jim = NewJim();

        var range = await jim.ConnectedSystems.GetPendingExportAttributeChangesRangeAsync(
            pendingExportId, MemberAttributeName, offset: 0, count: 10);
        var paged = await jim.ConnectedSystems.GetPendingExportAttributeChangesPagedAsync(
            pendingExportId, MemberAttributeName, page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(c => c.Id), Is.EqualTo(paged.Results.Select(c => c.Id)));
        }
    }

    /// <summary>
    /// Seeds a Pending Export with <paramref name="count"/> changes to one multi-valued "member" attribute,
    /// with ids assigned in seeding order so the read's id order yields numeric value order. When
    /// <paramref name="unresolvedReferenceFor"/> is given, that ordinal's change carries an unresolved reference
    /// instead of a plain string value. Returns the Pending Export's id.
    /// </summary>
    private async Task<Guid> SeedAsync(int count, int? unresolvedReferenceFor = null)
    {
        await using var ctx = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = connectedSystem, Selected = true };
        var memberAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = MemberAttributeName,
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            Selected = true
        };
        objectType.Attributes.Add(memberAttribute);
        ctx.AddRange(connectorDefinition, connectedSystem, objectType);
        await ctx.SaveChangesAsync();

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ChangeType = PendingExportChangeType.Update
        };
        ctx.PendingExports.Add(pendingExport);

        for (var i = 1; i <= count; i++)
        {
            var isUnresolved = unresolvedReferenceFor == i;
            ctx.Add(new PendingExportAttributeValueChange
            {
                Id = new Guid($"00000000-0000-0000-0000-{i:D12}"),
                PendingExportId = pendingExport.Id,
                Attribute = memberAttribute,
                StringValue = isUnresolved ? null : $"CN=Member {i:D3}",
                UnresolvedReferenceValue = isUnresolved ? $"CN=Unresolved {i:D3}" : null,
                ChangeType = PendingExportAttributeChangeType.Add
            });
        }

        await ctx.SaveChangesAsync();
        return pendingExport.Id;
    }
}
