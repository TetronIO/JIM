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
/// Real-PostgreSQL verification of the offset/count Pending Export attribute-change range read
/// (<see cref="JIM.Application.Servers.ConnectedSystemServer.GetAllPendingExportChangesRangeAsync"/>) that backs
/// the virtualised (infinite-scroll) Pending Export grid on a Connected System Object detail page. Its search
/// predicate is three <c>EF.Functions.ILike</c> clauses the EF Core in-memory provider cannot execute, so the
/// search, and its interaction with the window and the total, are only verifiable here. The context is
/// NoTracking, matching JIM.Web's configuration.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportChangeRangeDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export change range tests.");

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
        var pendingExportId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(c => c.Attribute.Name),
                Is.EqualTo(new[] { "attribute001", "attribute002", "attribute003" }));
        }
    }

    [Test]
    public async Task Range_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        var pendingExportId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(c => c.Attribute.Name),
                Is.EqualTo(new[] { "attribute004", "attribute005", "attribute006" }));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        var pendingExportId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches".
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(c => c.Attribute.Name),
                Is.EqualTo(new[] { "attribute004", "attribute005", "attribute006" }));
        }
    }

    [Test]
    public async Task Range_SearchOnAttributeName_IsCaseInsensitiveAndRestrictsTotalAsync()
    {
        var pendingExportId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 0, count: 10, searchText: "ATTRIBUTE004");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(c => c.Attribute.Name), Is.EqualTo(new[] { "attribute004" }));
        }
    }

    [Test]
    public async Task Range_SearchOnValue_MatchesTheStoredStringValueAsync()
    {
        var pendingExportId = await SeedAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 0, count: 10, searchText: "value 007");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(c => c.Attribute.Name), Is.EqualTo(new[] { "attribute007" }));
        }
    }

    [Test]
    public async Task Range_ConsecutiveWindows_PartitionTheMatchSetExactlyAsync()
    {
        // A multi-valued attribute puts every change under one attribute name, so the sort key ties for all
        // twenty rows and only the id tie-break keeps consecutive windows from repeating and skipping rows.
        var pendingExportId = await SeedMultiValuedAsync(20);
        var jim = NewJim();

        var first = await jim.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 0, count: 10);
        var second = await jim.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 10, count: 10);

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

        var range = await jim.ConnectedSystems.GetAllPendingExportChangesRangeAsync(
            pendingExportId, offset: 0, count: 10);
        var paged = await jim.ConnectedSystems.GetAllPendingExportChangesPagedAsync(
            pendingExportId, page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(c => c.Id), Is.EqualTo(paged.Results.Select(c => c.Id)));
        }
    }

    /// <summary>
    /// Seeds a Pending Export with <paramref name="count"/> attribute changes, one per attribute named
    /// "attribute001", "attribute002", ... each holding "value 001", "value 002", ... Returns the Pending
    /// Export's id.
    /// </summary>
    private async Task<Guid> SeedAsync(int count)
    {
        await using var ctx = NewContext();
        var (pendingExport, objectType) = await SeedPendingExportAsync(ctx);

        for (var i = 1; i <= count; i++)
        {
            var attribute = NewAttribute(objectType, $"attribute{i:D3}");
            ctx.Add(attribute);
            ctx.Add(NewChange(pendingExport.Id, attribute, $"value {i:D3}"));
        }

        await ctx.SaveChangesAsync();
        return pendingExport.Id;
    }

    /// <summary>
    /// Seeds a Pending Export whose <paramref name="count"/> changes all belong to one multi-valued attribute,
    /// so every row shares the attribute-name sort key.
    /// </summary>
    private async Task<Guid> SeedMultiValuedAsync(int count)
    {
        await using var ctx = NewContext();
        var (pendingExport, objectType) = await SeedPendingExportAsync(ctx);

        var attribute = NewAttribute(objectType, "member", AttributePlurality.MultiValued);
        ctx.Add(attribute);
        for (var i = 1; i <= count; i++)
            ctx.Add(NewChange(pendingExport.Id, attribute, $"CN=Member {i:D3}"));

        await ctx.SaveChangesAsync();
        return pendingExport.Id;
    }

    private static async Task<(PendingExport PendingExport, ConnectedSystemObjectType ObjectType)> SeedPendingExportAsync(JimDbContext ctx)
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = connectedSystem, Selected = true };
        ctx.AddRange(connectorDefinition, connectedSystem, objectType);
        await ctx.SaveChangesAsync();

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ChangeType = PendingExportChangeType.Update
        };
        ctx.PendingExports.Add(pendingExport);
        await ctx.SaveChangesAsync();
        return (pendingExport, objectType);
    }

    private static ConnectedSystemObjectTypeAttribute NewAttribute(
        ConnectedSystemObjectType objectType,
        string name,
        AttributePlurality plurality = AttributePlurality.SingleValued) => new()
    {
        Name = name,
        ConnectedSystemObjectType = objectType,
        Type = AttributeDataType.Text,
        AttributePlurality = plurality,
        Selected = true
    };

    private static PendingExportAttributeValueChange NewChange(
        Guid pendingExportId,
        ConnectedSystemObjectTypeAttribute attribute,
        string value) => new()
    {
        Id = Guid.NewGuid(),
        PendingExportId = pendingExportId,
        Attribute = attribute,
        StringValue = value,
        ChangeType = PendingExportAttributeChangeType.Add
    };
}
