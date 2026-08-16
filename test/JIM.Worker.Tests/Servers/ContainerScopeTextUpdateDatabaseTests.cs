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
/// Real-PostgreSQL verification that Container Scope stated as text (#1255 Advanced Mode) actually reaches the
/// database, from a host configured the way the portal and the REST API are.
/// </summary>
/// <remarks>
/// The REST tests around this endpoint mock the repository, so they prove the controller calls the server and not
/// that a row changes. That gap matters here more than most: three of JIM's four hosts run the DbContext
/// <c>NoTracking</c> (JIM.Web, JIM.Scheduler, and <c>JimDbContext</c>'s own default), and a mutating path that
/// assumes it was handed a tracked entity does nothing at all from those hosts while reporting success. Applying a
/// scope writes a flag per Container across a loaded graph, which is exactly the shape that fails that way, and the
/// in-memory provider tracks by default so the whole unit suite passes regardless.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other real-database fixtures; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ContainerScopeTextUpdateDatabaseTests
{
    private string _connectionString = null!;

    // NoTracking, matching JIM.Web (JIM.Web/Program.cs) and JimDbContext's own default. A tracking context hides
    // the whole class of fault this fixture exists to guard.
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Container Scope text tests.");

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

    [Test]
    public async Task ApplyContainerScopeTextAsync_PersistsTheSelectionAndTheExclusionAsync()
    {
        var (connectedSystemId, initiatorId) = await SeedConnectedSystemAsync();

        await using (var applyContext = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(applyContext));
            var result = await jim.ConnectedSystems.ApplyContainerScopeTextAsync(
                connectedSystemId,
                """
                include OU=Corp,DC=example,DC=local
                exclude OU=Service Accounts,OU=Corp,DC=example,DC=local
                """,
                await InitiatorAsync(applyContext, initiatorId));

            Assert.That(result?.Applied, Is.True, "the text is valid, so it must have been applied");
        }

        await using var verify = NewContext();
        var corp = await verify.ConnectedSystemContainers.SingleAsync(c => c.ExternalId == "OU=Corp,DC=example,DC=local");
        var serviceAccounts = await verify.ConnectedSystemContainers.SingleAsync(c => c.ExternalId == "OU=Service Accounts,OU=Corp,DC=example,DC=local");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.Selected, Is.True, "a scope that reports success and writes no row is the silent no-op this fixture guards");
            Assert.That(corp.Excluded, Is.False);
            Assert.That(serviceAccounts.Excluded, Is.True);
            Assert.That(serviceAccounts.Selected, Is.False);
        }
    }

    [Test]
    public async Task ApplyContainerScopeTextAsync_OmittingAContainer_ClearsItsStoredSelectionAsync()
    {
        // The whole-scope rule has to survive the round trip to the database: a Container the text no longer names
        // must come back deselected, not left set from the previous text.
        var (connectedSystemId, initiatorId) = await SeedConnectedSystemAsync();

        await using (var first = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(first));
            await jim.ConnectedSystems.ApplyContainerScopeTextAsync(
                connectedSystemId, "include OU=Sales,OU=Corp,DC=example,DC=local", await InitiatorAsync(first, initiatorId));
        }

        await using (var second = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(second));
            await jim.ConnectedSystems.ApplyContainerScopeTextAsync(
                connectedSystemId, "include OU=Corp,DC=example,DC=local", await InitiatorAsync(second, initiatorId));
        }

        await using var verify = NewContext();
        var sales = await verify.ConnectedSystemContainers.SingleAsync(c => c.ExternalId == "OU=Sales,OU=Corp,DC=example,DC=local");
        var corp = await verify.ConnectedSystemContainers.SingleAsync(c => c.ExternalId == "OU=Corp,DC=example,DC=local");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sales.Selected, Is.False, "the second text does not name OU=Sales, so it states nothing about it");
            Assert.That(corp.Selected, Is.True);
        }
    }

    [Test]
    public async Task ApplyContainerScopeTextAsync_ARefusedText_LeavesTheStoredScopeUntouchedAsync()
    {
        var (connectedSystemId, initiatorId) = await SeedConnectedSystemAsync();

        await using (var first = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(first));
            await jim.ConnectedSystems.ApplyContainerScopeTextAsync(
                connectedSystemId, "include OU=Corp,DC=example,DC=local", await InitiatorAsync(first, initiatorId));
        }

        await using (var refused = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(refused));
            var result = await jim.ConnectedSystems.ApplyContainerScopeTextAsync(
                connectedSystemId,
                """
                include OU=Sales,OU=Corp,DC=example,DC=local
                exclude OU=Contractors,DC=example,DC=local
                """,
                await InitiatorAsync(refused, initiatorId));

            Assert.That(result?.Applied, Is.False, "line 2 names no Container");
        }

        await using var verify = NewContext();
        var corp = await verify.ConnectedSystemContainers.SingleAsync(c => c.ExternalId == "OU=Corp,DC=example,DC=local");
        var sales = await verify.ConnectedSystemContainers.SingleAsync(c => c.ExternalId == "OU=Sales,OU=Corp,DC=example,DC=local");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.Selected, Is.True, "the stored scope the refused text would have replaced still stands");
            Assert.That(sales.Selected, Is.False, "and the refused text's own first line reached no row");
        }
    }

    [Test]
    public async Task GetContainerScopeTextAsync_ReadsBackWhatWasAppliedAsync()
    {
        // The round trip that makes the text safe to edit and re-send: what a read returns has to be what the
        // previous write left behind, resolved from rows rather than from anything the write kept in memory.
        var (connectedSystemId, initiatorId) = await SeedConnectedSystemAsync();

        await using (var apply = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(apply));
            await jim.ConnectedSystems.ApplyContainerScopeTextAsync(
                connectedSystemId,
                """
                include one-level OU=Corp,DC=example,DC=local
                include OU=Sales,OU=Corp,DC=example,DC=local
                """,
                await InitiatorAsync(apply, initiatorId));
        }

        await using var read = NewContext();
        var readJim = new JimApplication(new PostgresDataRepository(read));
        var text = await readJim.ConnectedSystems.GetContainerScopeTextAsync(connectedSystemId);

        Assert.That(text, Is.EqualTo(
            """
            include one-level OU=Corp,DC=example,DC=local
            include OU=Sales,OU=Corp,DC=example,DC=local
            """));
    }

    /// <summary>
    /// The administrator the change is attributed to, read on the context that will perform the save.
    /// </summary>
    private static async Task<MetaverseObject> InitiatorAsync(JimDbContext context, Guid initiatorId) =>
        await context.MetaverseObjects.SingleAsync(o => o.Id == initiatorId);

    /// <summary>
    /// A Connected System with one partition holding OU=Corp, and OU=Sales plus OU=Service Accounts beneath it.
    /// </summary>
    private async Task<(int ConnectedSystemId, Guid InitiatorId)> SeedConnectedSystemAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition
        {
            Name = "Test LDAP Connector",
            SupportsPartitions = true,
            SupportsPartitionContainers = true
        };

        var setting = new ConnectorDefinitionSetting { Name = "Host", Type = ConnectedSystemSettingType.String };
        connectorDefinition.Settings.Add(setting);

        var connectedSystem = new ConnectedSystem
        {
            Name = "Test Directory",
            ConnectorDefinition = connectorDefinition,
            SettingValues = [new ConnectedSystemSettingValue { Setting = setting, StringValue = "directory.example.local" }]
        };

        var sales = new ConnectedSystemContainer { Name = "Sales", ExternalId = "OU=Sales,OU=Corp,DC=example,DC=local" };
        var serviceAccounts = new ConnectedSystemContainer { Name = "Service Accounts", ExternalId = "OU=Service Accounts,OU=Corp,DC=example,DC=local" };
        var corp = new ConnectedSystemContainer { Name = "Corp", ExternalId = "OU=Corp,DC=example,DC=local" };
        corp.AddChildContainer(sales);
        corp.AddChildContainer(serviceAccounts);

        var partition = new ConnectedSystemPartition
        {
            Name = "example.local",
            ExternalId = "DC=example,DC=local",
            Selected = true,
            ConnectedSystem = connectedSystem,
            Containers = [corp]
        };
        corp.Partition = partition;

        // Applying a scope is a configuration change, and JIM records one against a security principal or not at
        // all, so the fixture has to carry an administrator for the Activity to be attributed to.
        var userType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        var initiator = new MetaverseObject { Type = userType, CachedDisplayName = "Test Administrator" };

        seed.ConnectedSystems.Add(connectedSystem);
        seed.ConnectedSystemPartitions.Add(partition);
        seed.MetaverseObjectTypes.Add(userType);
        seed.MetaverseObjects.Add(initiator);
        await seed.SaveChangesAsync();

        return (connectedSystem.Id, initiator.Id);
    }
}
