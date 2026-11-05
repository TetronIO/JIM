// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL cover for the Connected System-level Delta Import watermark on a Connector Space clear (#2724): the
/// SQL, SCIM 2.0 Client and SCIM 2.0 Service Provider Connectors keep their watermark in the Connected System's
/// persisted connector data, so a clear that left it in place had the next Delta Import read only the changes after it,
/// and never rebuild the objects the clear removed. The LDAP Connector keeps its watermarks per Partition (#2715) and its
/// persisted connector data is its record of the directory (the pinned domain controller among it), which a clear keeps.
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemImportWatermarkClearDatabaseTests
{
    private const string LdapRecord = """{"DnsHostName":"dc1.corp.local","DirectoryType":2,"PinnedDirectoryServer":"dc1.corp.local"}""";

    private string _connectionString = null!;

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.NoTracking)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(tracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL import watermark clear tests.");

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

    private static IEnumerable<TestCaseData> ConnectorsKeepingTheirWatermarkInPersistedData()
    {
        yield return new TestCaseData(ConnectorConstants.SqlConnectorName,
                """{"Mode":"ChangeLogTable","ObjectTypes":{"Person":{"Value":"42","Type":"LongNumber"}},"RelatedTables":{}}""")
            .SetArgDisplayNames("SQL");
        yield return new TestCaseData(ConnectorConstants.ScimClientConnectorName,
                """{"watermark":"2026-11-01T09:00:00+00:00","capturedAt":"2026-11-01T09:05:00+00:00"}""")
            .SetArgDisplayNames("SCIM 2.0 Client");
        yield return new TestCaseData(ConnectorConstants.ScimServiceProviderConnectorName, """{"changeSequence":8}""")
            .SetArgDisplayNames("SCIM 2.0 Service Provider");
    }

    [TestCaseSource(nameof(ConnectorsKeepingTheirWatermarkInPersistedData))]
    public async Task ClearConnectedSystemObjectsAsync_ConnectorKeepsItsWatermarkInPersistedData_RemovesItAsync(string connectorName, string watermark)
    {
        var systemId = await SeedAsync(connectorName, watermark);

        ClearConnectedSystemResult result;
        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            result = await jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(systemId);
        }

        Assert.That(await ReadPersistedConnectorDataAsync(systemId), Is.Null,
            "an empty Connector Space has no baseline, so the next Delta Import must start from no watermark and read every object");
        Assert.That(result.ImportWatermarkRemoved, Is.True, "the clear's Activity must be able to say the watermark went");
    }

    [Test]
    public async Task ClearConnectedSystemObjectsAsync_LdapConnectorRecord_KeepsItAsync()
    {
        var systemId = await SeedAsync(ConnectorConstants.LdapConnectorName, LdapRecord, partitionWatermarks: ["corp-1", "partners-1"]);

        ClearConnectedSystemResult result;
        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            result = await jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(systemId);
        }

        Assert.That(await ReadPersistedConnectorDataAsync(systemId), Is.EqualTo(LdapRecord),
            "the LDAP Connector's record of the directory holds no Delta Import position (its watermarks are per Partition), so a clear keeps it, the pinned domain controller included");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ImportWatermarkRemoved, Is.False);
            Assert.That(result.PartitionWatermarksRemoved, Is.EqualTo(2), "each Partition's watermark goes, and the clear says how many");
            Assert.That(await CountPartitionWatermarksAsync(systemId), Is.Zero);
        }
    }

    [Test]
    public async Task ClearConnectedSystemObjectsAsync_WorkerTrackingContextSavesAfterwards_WatermarkStaysRemovedAsync()
    {
        var systemId = await SeedAsync(ConnectorConstants.ScimServiceProviderConnectorName, """{"changeSequence":8}""");

        // As the Worker runs a clear: the Connected System is already tracked, and the Activity is saved afterwards on the
        // same context. A tracked instance left holding the old value must not write it back.
        await using (var ctx = NewContext(QueryTrackingBehavior.TrackAll))
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            var tracked = (await jim.ConnectedSystems.GetConnectedSystemCoreAsync(systemId, withChangeTracking: true))!;
            await jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(systemId);
            tracked.Description = "Saved after the clear";
            await ctx.SaveChangesAsync();
        }

        Assert.That(await ReadPersistedConnectorDataAsync(systemId), Is.Null);
    }

    [Test]
    public async Task ClearConnectedSystemObjectsAsync_NoPersistedConnectorData_LeavesItEmptyAsync()
    {
        var systemId = await SeedAsync(ConnectorConstants.SqlConnectorName, persistedConnectorData: null);

        ClearConnectedSystemResult result;
        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            result = await jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(systemId);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadPersistedConnectorDataAsync(systemId), Is.Null);
            Assert.That(result.ImportWatermarkRemoved, Is.False, "there was no watermark to remove");
            Assert.That(result.DescribeDeltaImportReset(), Is.Null);
        }
    }

    #region Helpers

    private async Task<int> SeedAsync(string connectorName, string? persistedConnectorData, string[]? partitionWatermarks = null)
    {
        Dictionary<int, string> watermarks;
        int systemId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = connectorName, BuiltIn = true };
            var system = new ConnectedSystem { Name = "Source", ConnectorDefinition = connectorDefinition, PersistedConnectorData = persistedConnectorData };
            var partitions = (partitionWatermarks ?? [])
                .Select((value, index) => (Value: value, Partition: new ConnectedSystemPartition
                {
                    ConnectedSystem = system, ExternalId = $"dc=partition{index},dc=local", Name = $"partition{index}.local", Selected = true
                }))
                .ToList();
            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemPartitions.AddRange(partitions.Select(p => p.Partition));
            await seed.SaveChangesAsync();
            systemId = system.Id;
            watermarks = partitions.ToDictionary(p => p.Partition.Id, p => p.Value);
        }

        if (watermarks.Count > 0)
        {
            await using var ctx = NewContext();
            await new PostgresDataRepository(ctx).ConnectedSystems.RecordImportWatermarksAsync(systemId, null, watermarks, [], DateTime.UtcNow);
        }

        return systemId;
    }

    private async Task<int> CountPartitionWatermarksAsync(int systemId)
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystemPartitionWatermarks.CountAsync(w => w.ConnectedSystemId == systemId);
    }

    private async Task<string?> ReadPersistedConnectorDataAsync(int systemId)
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystems.Where(cs => cs.Id == systemId).Select(cs => cs.PersistedConnectorData).SingleAsync();
    }

    #endregion
}
