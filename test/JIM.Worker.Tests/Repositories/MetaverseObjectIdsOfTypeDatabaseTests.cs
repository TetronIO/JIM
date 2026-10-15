// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the id read a Temporal Scope Reconciliation full pass batches behind (#2054): every
/// Metaverse Object of the type, and none of another.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectIdsOfTypeDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task GetMetaverseObjectIdsOfTypeAsync_ReturnsEveryObjectOfTheTypeOnlyAsync()
    {
        int userTypeId;
        List<Guid> users;
        await using (var ctx = NewContext())
        {
            var user = new MetaverseObjectType { Name = "User", PluralName = "Users" };
            var group = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
            ctx.MetaverseObjectTypes.AddRange(user, group);
            var userObjects = Enumerable.Range(0, 3).Select(_ => new MetaverseObject { Type = user }).ToList();
            ctx.MetaverseObjects.AddRange(userObjects);
            ctx.MetaverseObjects.Add(new MetaverseObject { Type = group });
            await ctx.SaveChangesAsync();
            userTypeId = user.Id;
            users = userObjects.Select(o => o.Id).ToList();
        }

        await using var readContext = NewContext();
        var ids = await new PostgresDataRepository(readContext).Metaverse.GetMetaverseObjectIdsOfTypeAsync(userTypeId);

        Assert.That(ids, Is.EquivalentTo(users));
    }
}
