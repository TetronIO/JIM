// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the per-Connected-System Password Synchronisation state read that drives the
/// Connected Systems list indicator (#1119, requirement 26).
/// <para>
/// Against a real provider by construction. The query left-joins each Connected System to a configuration row
/// that may not exist, through a correlated subselect projecting a nullable bool, and reads a flag off a
/// Connector Definition navigation that may be absent. The in-memory provider resolves navigations from its own
/// tracked graph rather than by translating a join, so a system with no configuration and a system whose
/// configuration failed to translate look identical there.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c>
/// fixtures. Do NOT run this fixture outside the sanctioned scratch-database workflow: <c>SetUp</c> TRUNCATEs
/// every table.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PasswordSynchronisationStateDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Password Synchronisation state tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
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
    /// Seeds a Connected System, optionally with a Password Synchronisation configuration, and returns its id.
    /// </summary>
    private async Task<int> SeedSystemAsync(string name, bool connectorSupportsPasswordSet, bool? enabled)
    {
        await using var ctx = NewContext();

        var connector = new ConnectorDefinition
        {
            Name = $"{name} Connector",
            SupportsPasswordSet = connectorSupportsPasswordSet
        };
        ctx.ConnectorDefinitions.Add(connector);
        await ctx.SaveChangesAsync();

        var system = new ConnectedSystem { Name = name, ConnectorDefinitionId = connector.Id };
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();

        if (enabled.HasValue)
        {
            // A real Object Type, because the configuration's target is a foreign key: a placeholder id is
            // rejected by the database, which is one of the things this fixture exists to run against.
            var objectType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "User" };
            ctx.ConnectedSystemObjectTypes.Add(objectType);
            await ctx.SaveChangesAsync();

            ctx.ConnectedSystemPasswordSynchronisations.Add(new ConnectedSystemPasswordSynchronisation
            {
                ConnectedSystemId = system.Id,
                Enabled = enabled.Value,
                TargetObjectTypeId = objectType.Id
            });
            await ctx.SaveChangesAsync();
        }

        return system.Id;
    }

    [Test]
    public async Task GetPasswordSynchronisationStatesAsync_ReportsEachStateFromTheDatabaseAsync()
    {
        var enabled = await SeedSystemAsync("Corporate AD", connectorSupportsPasswordSet: true, enabled: true);
        var disabled = await SeedSystemAsync("Contractor LDAP", connectorSupportsPasswordSet: true, enabled: false);
        var unconfigured = await SeedSystemAsync("HR SQL", connectorSupportsPasswordSet: true, enabled: null);
        var unsupported = await SeedSystemAsync("Legacy File", connectorSupportsPasswordSet: false, enabled: null);

        await using var ctx = NewContext();
        var states = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetPasswordSynchronisationStatesAsync([enabled, disabled, unconfigured, unsupported]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(states[enabled], Is.EqualTo(PasswordSynchronisationState.Enabled));
            Assert.That(states[disabled], Is.EqualTo(PasswordSynchronisationState.Disabled),
                "switched off is not the same as never configured: changes keep accumulating for it");
            Assert.That(states[unconfigured], Is.EqualTo(PasswordSynchronisationState.NotConfigured));
            Assert.That(states[unsupported], Is.EqualTo(PasswordSynchronisationState.NotSupported));
        }
    }

    /// <summary>
    /// A Connector Definition can lose its password-set capability on upgrade while a configuration somebody made
    /// against the old one is still stored. Reporting that system as Enabled would promise delivery that cannot
    /// happen, so the capability wins.
    /// </summary>
    [Test]
    public async Task GetPasswordSynchronisationStatesAsync_ConfiguredButUnsupported_ReportsNotSupportedAsync()
    {
        var id = await SeedSystemAsync("Legacy Unix", connectorSupportsPasswordSet: false, enabled: true);

        await using var ctx = NewContext();
        var states = await new PostgresDataRepository(ctx).ConnectedSystems.GetPasswordSynchronisationStatesAsync([id]);

        Assert.That(states[id], Is.EqualTo(PasswordSynchronisationState.NotSupported));
    }

    [Test]
    public async Task GetPasswordSynchronisationStatesAsync_NamesNoSystems_ReadsNothingAsync()
    {
        await using var ctx = NewContext();
        var states = await new PostgresDataRepository(ctx).ConnectedSystems.GetPasswordSynchronisationStatesAsync([]);

        Assert.That(states, Is.Empty);
    }

    /// <summary>
    /// Only the systems asked for come back. A list page passes the ids of the rows it holds, and an answer
    /// covering systems it did not ask about would be silently wrong the day the list is filtered.
    /// </summary>
    [Test]
    public async Task GetPasswordSynchronisationStatesAsync_AnswersOnlyForTheSystemsNamedAsync()
    {
        var asked = await SeedSystemAsync("Corporate AD", connectorSupportsPasswordSet: true, enabled: true);
        await SeedSystemAsync("HR SQL", connectorSupportsPasswordSet: true, enabled: true);

        await using var ctx = NewContext();
        var states = await new PostgresDataRepository(ctx).ConnectedSystems.GetPasswordSynchronisationStatesAsync([asked]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(states, Has.Count.EqualTo(1));
            Assert.That(states.ContainsKey(asked), Is.True);
        }
    }
}
