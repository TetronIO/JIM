// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL cover for the server paths that must remove Partition watermarks (#2715): clearing the Connector
/// Space (Decision 4: an empty Connector Space has no baseline) and deselecting a Partition (Decision 5: a Partition
/// selected again later must not resume from a watermark taken before its objects were obsoleted while it was out of
/// scope). The contexts are <c>NoTracking</c>, as JIM.Web's are, because that is where the deselection is saved from.
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemPartitionWatermarkServerDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Partition watermark server tests.");

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
        TestUtilities.SetEnvironmentVariables();
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public async Task ClearConnectedSystemObjectsAsync_PartitionsHoldWatermarks_RemovesThemAsync()
    {
        var (systemId, _, _) = await SeedWithWatermarksAsync();

        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(systemId);
        }

        Assert.That(await ReadPartitionIdsAsync(systemId), Is.Empty,
            "an empty Connector Space has no baseline, so no Partition may keep a watermark");
    }

    [Test]
    public async Task UpdateConnectedSystemPartitionAsync_PartitionDeselected_RemovesItsWatermarkAsync()
    {
        var (systemId, corp, partners) = await SeedWithWatermarksAsync();

        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            var partition = (await jim.ConnectedSystems.GetConnectedSystemPartitionAsync(partners))!;
            partition.Selected = false;
            await jim.ConnectedSystems.UpdateConnectedSystemPartitionAsync(partition, systemId, Administrator());
        }

        Assert.That(await ReadPartitionIdsAsync(systemId), Is.EqualTo(new[] { corp }),
            "only the deselected Partition loses its watermark");
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_PartitionDeselected_RemovesItsWatermarkAsync()
    {
        var (systemId, corp, partners) = await SeedWithWatermarksAsync();

        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            var system = (await jim.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
            system.Partitions!.Single(p => p.Id == partners).Selected = false;
            await jim.ConnectedSystems.UpdateConnectedSystemAsync(system, Administrator());
        }

        Assert.That(await ReadPartitionIdsAsync(systemId), Is.EqualTo(new[] { corp }),
            "only the deselected Partition loses its watermark");
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_EveryPartitionStillSelected_KeepsEveryWatermarkAsync()
    {
        var (systemId, corp, partners) = await SeedWithWatermarksAsync();

        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            var system = (await jim.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
            system.Description = "An unrelated change";
            await jim.ConnectedSystems.UpdateConnectedSystemAsync(system, Administrator());
        }

        Assert.That(await ReadPartitionIdsAsync(systemId), Is.EquivalentTo(new[] { corp, partners }));
    }

    #region Helpers

    private async Task<(int SystemId, int Corp, int Partners)> SeedWithWatermarksAsync()
    {
        int systemId, corpId, partnersId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var setting = new ConnectorDefinitionSetting { Name = "Host", Type = ConnectedSystemSettingType.String };
            connectorDefinition.Settings.Add(setting);
            var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
            system.SettingValues.Add(new ConnectedSystemSettingValue { ConnectedSystem = system, Setting = setting, StringValue = "directory.example.org" });
            var corp = new ConnectedSystemPartition { ConnectedSystem = system, ExternalId = "dc=corp,dc=local", Name = "corp.local", Selected = true };
            var partners = new ConnectedSystemPartition { ConnectedSystem = system, ExternalId = "dc=partners,dc=local", Name = "partners.local", Selected = true };
            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemPartitions.AddRange(corp, partners);
            await seed.SaveChangesAsync();
            (systemId, corpId, partnersId) = (system.Id, corp.Id, partners.Id);
        }

        await using (var ctx = NewContext())
        {
            await new PostgresDataRepository(ctx).ConnectedSystems.RecordImportWatermarksAsync(systemId, null,
                new Dictionary<int, string> { [corpId] = "corp-1", [partnersId] = "partners-1" }, [], DateTime.UtcNow);
        }

        Assert.That(await ReadPartitionIdsAsync(systemId), Is.EquivalentTo(new[] { corpId, partnersId }), "precondition: both Partitions hold a watermark");
        return (systemId, corpId, partnersId);
    }

    /// <summary>The administrator saving the change, as the portal attributes it.</summary>
    private static MetaverseObject Administrator() => new() { Id = Guid.NewGuid() };

    private async Task<List<int>> ReadPartitionIdsAsync(int systemId)
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystemPartitionWatermarks.Where(w => w.ConnectedSystemId == systemId)
            .OrderBy(w => w.PartitionId).Select(w => w.PartitionId).ToListAsync();
    }

    #endregion
}
