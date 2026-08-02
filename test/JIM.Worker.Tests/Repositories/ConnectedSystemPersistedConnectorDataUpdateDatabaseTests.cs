// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL tests for the targeted persisted connector data write. The watermark write must touch ONLY
/// the PersistedConnectorData column: routing it through the graph-marking update path meant that runtime-only
/// setting-value instances on the in-memory Connected System (a Setting navigation with no FK scalar) were
/// written back with SettingId 0, failing export runs on a foreign key violation the first time a connector
/// returned close-time state (the #230 domain controller pin establishment path). The in-memory provider
/// enforces no foreign keys, so only a real-PostgreSQL test can prove this class of fault fixed.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemPersistedConnectorDataUpdateDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL persisted connector data tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .Options);

    [Test]
    public async Task UpdateConnectedSystemPersistedConnectorDataAsync_SystemCarriesSettingValueWithoutFkScalar_WritesWatermarkAndLeavesSettingRowsUntouchedAsync()
    {
        // Arrange: a persisted Connected System whose definition has one setting and one stored value.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        int systemId;
        int settingId;
        int settingValueId;

        await using (var arrangeCtx = NewContext())
        {
            var repo = new PostgresDataRepository(arrangeCtx);
            var definition = new ConnectorDefinition { Name = $"test-def-{suffix}" };
            var setting = new ConnectorDefinitionSetting { Name = "Host" };
            definition.Settings.Add(setting);
            var system = new ConnectedSystem { Name = $"test-system-{suffix}", ConnectorDefinition = definition };
            var value = new ConnectedSystemSettingValue { Setting = setting, StringValue = "dc1.example.local", ConnectedSystem = system };

            arrangeCtx.ConnectorDefinitions.Add(definition);
            arrangeCtx.ConnectorDefinitionSettings.Add(setting);
            arrangeCtx.ConnectedSystems.Add(system);
            arrangeCtx.ConnectedSystemSettingValues.Add(value);
            await arrangeCtx.SaveChangesAsync();

            systemId = system.Id;
            settingId = setting.Id;
            settingValueId = value.Id;
            _ = repo; // repository constructed against the arrange context is not needed further
        }

        // Act: load the system fresh, then poison the in-memory instance the way the worker's export path
        // composes it (a setting value whose Setting navigation is unset and whose FK scalar is 0), and
        // write the watermark through the targeted update.
        await using (var actCtx = NewContext())
        {
            var repo = new PostgresDataRepository(actCtx);
            var system = await actCtx.ConnectedSystems
                .Include(cs => cs.SettingValues)
                .SingleAsync(cs => cs.Id == systemId);

            foreach (var sv in system.SettingValues)
            {
                // The FK is a shadow property, so nulling the navigation on a detached instance is
                // exactly the poisoned shape the worker's export path carried: marking such an
                // instance Modified resolves the shadow FK to 0.
                sv.Setting = null!;
            }

            await repo.ConnectedSystems.UpdateConnectedSystemPersistedConnectorDataAsync(systemId, "{\"watermark\":42}");
        }

        // Assert: the watermark landed and the setting value row is untouched.
        await using var assertCtx = NewContext();
        var persisted = await assertCtx.ConnectedSystems.SingleAsync(cs => cs.Id == systemId);
        var persistedValue = await assertCtx.ConnectedSystemSettingValues
            .Include(sv => sv.Setting)
            .SingleAsync(sv => sv.Id == settingValueId);

        Assert.That(persisted.PersistedConnectorData, Is.EqualTo("{\"watermark\":42}"));
        Assert.That(persistedValue.Setting, Is.Not.Null);
        Assert.That(persistedValue.Setting!.Id, Is.EqualTo(settingId));
        Assert.That(persistedValue.StringValue, Is.EqualTo("dc1.example.local"));
    }

    [Test]
    public async Task UpdateConnectedSystemPersistedConnectorDataAsync_NullValue_ClearsTheColumnAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        int systemId;

        await using (var arrangeCtx = NewContext())
        {
            var definition = new ConnectorDefinition { Name = $"test-def2-{suffix}" };
            var system = new ConnectedSystem { Name = $"test-system2-{suffix}", ConnectorDefinition = definition, PersistedConnectorData = "old" };
            arrangeCtx.ConnectorDefinitions.Add(definition);
            arrangeCtx.ConnectedSystems.Add(system);
            await arrangeCtx.SaveChangesAsync();
            systemId = system.Id;
        }

        await using (var actCtx = NewContext())
        {
            var repo = new PostgresDataRepository(actCtx);
            await repo.ConnectedSystems.UpdateConnectedSystemPersistedConnectorDataAsync(systemId, null);
        }

        await using var assertCtx = NewContext();
        var persisted = await assertCtx.ConnectedSystems.SingleAsync(cs => cs.Id == systemId);
        Assert.That(persisted.PersistedConnectorData, Is.Null);
    }
}
