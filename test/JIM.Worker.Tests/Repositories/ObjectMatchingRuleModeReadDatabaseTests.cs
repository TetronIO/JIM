// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of <c>SyncRepository.GetObjectMatchingRuleModeAsync</c>, the read Sync Preview makes to
/// take the join decision the run takes (#2176). Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ObjectMatchingRuleModeReadDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Object Matching Rule mode read tests.");

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
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [TestCase(ObjectMatchingRuleMode.ConnectedSystem)]
    [TestCase(ObjectMatchingRuleMode.SyncRule)]
    public async Task GetObjectMatchingRuleModeAsync_StoredSystem_ReturnsItsModeAsync(ObjectMatchingRuleMode mode)
    {
        int systemId;
        await using (var ctx = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Corporate Directory", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = mode };
            ctx.AddRange(connectorDefinition, system);
            await ctx.SaveChangesAsync();
            systemId = system.Id;
        }

        await using var readCtx = NewContext();
        Assert.That(await new PostgresDataRepository(readCtx).Sync.GetObjectMatchingRuleModeAsync(systemId), Is.EqualTo(mode));
    }

    [Test]
    public async Task GetObjectMatchingRuleModeAsync_UnknownSystem_ReturnsNullAsync()
    {
        await using var ctx = NewContext();
        Assert.That(await new PostgresDataRepository(ctx).Sync.GetObjectMatchingRuleModeAsync(int.MaxValue), Is.Null);
    }
}
