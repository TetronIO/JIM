// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// The aggregate queries that group every row under one constant key (<c>GroupBy(_ =&gt; 1)</c>) to compute several counts
/// or a minimum and maximum in one round trip. Such a query returns one row, or none, so it must be read with
/// <c>SingleOrDefault</c>: read with <c>FirstOrDefault</c>, EF Core pushes the grouping into a subquery, finds no filter or
/// ordering above it, and logs warning 10103 ("may lead to unpredictable results") the first time the query runs in a
/// process. The Worker logged it on completing a Full Synchronisation, which reads as a fault in the run when there is
/// none. The warning is raised while the query is compiled, so an empty database proves it either way.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConstantGroupAggregateQueryDatabaseTests
{
    private string _connectionString = null!;

    /// <summary>A context on which warning 10103 throws, so a query that would log it fails the test.</summary>
    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .ConfigureWarnings(w => w
            .Ignore(RelationalEventId.PendingModelChangesWarning)
            .Throw(CoreEventId.FirstWithoutOrderByAndFilterWarning))
        .Options);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL constant-group aggregate query tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [Test]
    public async Task GetActivityRpeiErrorCountsAsync_ConstantGroupAggregate_DoesNotLogFirstWithoutOrderByAsync()
    {
        await using var ctx = NewContext();

        var counts = await new PostgresDataRepository(ctx).Activity.GetActivityRpeiErrorCountsAsync(Guid.NewGuid());

        Assert.That(counts, Is.EqualTo(new ActivityRpeiErrorCounts(0, 0, 0, 0)));
    }

    [Test]
    public async Task GetCsoChangeDateRangeAsync_ConstantGroupAggregate_DoesNotLogFirstWithoutOrderByAsync()
    {
        await using var ctx = NewContext();

        var range = await new PostgresDataRepository(ctx).ChangeHistory.GetCsoChangeDateRangeAsync([Guid.NewGuid()]);

        Assert.That(range, Is.Null);
    }

    [Test]
    public async Task GetMvoChangeDateRangeAsync_ConstantGroupAggregate_DoesNotLogFirstWithoutOrderByAsync()
    {
        await using var ctx = NewContext();

        var range = await new PostgresDataRepository(ctx).ChangeHistory.GetMvoChangeDateRangeAsync([Guid.NewGuid()]);

        Assert.That(range, Is.Null);
    }

    [Test]
    public async Task GetActivityDateRangeAsync_ConstantGroupAggregate_DoesNotLogFirstWithoutOrderByAsync()
    {
        await using var ctx = NewContext();

        var range = await new PostgresDataRepository(ctx).ChangeHistory.GetActivityDateRangeAsync([Guid.NewGuid()]);

        Assert.That(range, Is.Null);
    }
}
