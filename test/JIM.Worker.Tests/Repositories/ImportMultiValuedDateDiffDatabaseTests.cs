// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL proof that a multi-valued date imports once and then stays put (#2153 S5). The in-memory repository
/// keeps 100ns ticks and the import's own instances, so only a value that has been through PostgreSQL (microseconds,
/// read back with another Kind) shows whether the import diff, keyed canonically, finds a re-import unchanged. Opt-in
/// via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ImportMultiValuedDateDiffDatabaseTests
{
    private static readonly MethodInfo UpdateMethod = typeof(JIM.Worker.Processors.SyncImportTaskProcessor).GetMethod(
        "UpdateConnectedSystemObjectFromImportObject", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("UpdateConnectedSystemObjectFromImportObject not found via reflection.");

    /// <summary>Seventh-digit values, the last instant of 1999 (which Npgsql alone would round into 2000), and a whole second.</summary>
    private static readonly DateTime[] Dates =
    [
        new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234567),
        new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(-5),
        new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc)
    ];

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL import diff tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task UpdateFromImport_SameSubMicrosecondDatesInAnotherOrder_StagesNothingAsync()
    {
        var (systemId, csoId) = await SeedObjectHoldingDatesAsync(Dates);

        var (cso, objectType) = await HydrateAsync(systemId, csoId);
        var reimport = ImportOf(objectType, [Dates[2], Dates[0], Dates[1], Dates[0]]);
        Diff(reimport, cso, objectType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.PendingAttributeValueAdditions, Is.Empty, "A value and its re-imported self are the same value.");
            Assert.That(cso.PendingAttributeValueRemovals, Is.Empty);
        }
    }

    [Test]
    public async Task UpdateFromImport_SingleSeventhDigitDateReimported_StagesNothingAsync()
    {
        // The single-valued fast path, against a value PostgreSQL has floored to the microsecond and hands back in
        // another Kind.
        var (systemId, csoId) = await SeedObjectHoldingDatesAsync([Dates[0]]);

        var (cso, objectType) = await HydrateAsync(systemId, csoId);
        Diff(ImportOf(objectType, [Dates[0]]), cso, objectType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.PendingAttributeValueAdditions, Is.Empty);
            Assert.That(cso.PendingAttributeValueRemovals, Is.Empty);
        }
    }

    [Test]
    public async Task UpdateFromImport_OneDateReplaced_RemovesAndAddsOnlyThatDateAsync()
    {
        var (systemId, csoId) = await SeedObjectHoldingDatesAsync(Dates);
        var replacement = new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc).AddTicks(7);

        var (cso, objectType) = await HydrateAsync(systemId, csoId);
        Diff(ImportOf(objectType, [Dates[0], Dates[2], replacement]), cso, objectType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.PendingAttributeValueRemovals.Select(v => DateTimeAttributeValue.Key(v.DateTimeValue!.Value)),
                Is.EqualTo(new[] { DateTimeAttributeValue.Key(Dates[1]) }));
            Assert.That(cso.PendingAttributeValueAdditions.Select(v => v.DateTimeValue),
                Is.EqualTo(new DateTime?[] { DateTimeAttributeValue.ToCanonical(replacement) }));
        }
    }

    private async Task<(int SystemId, Guid CsoId)> SeedObjectHoldingDatesAsync(IEnumerable<DateTime> dates)
    {
        int systemId, typeId, attributeId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Yellowstone HR", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            csType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Name = "pwdFailureTime", ConnectedSystemObjectType = csType, Type = AttributeDataType.DateTime,
                AttributePlurality = AttributePlurality.MultiValued, Selected = true
            });
            seed.AddRange(connectorDefinition, system, csType);
            await seed.SaveChangesAsync();
            systemId = system.Id;
            typeId = csType.Id;
            attributeId = csType.Attributes[0].Id;
        }

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = systemId, TypeId = typeId,
            Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined, Created = DateTime.UtcNow
        };
        foreach (var date in dates)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, DateTimeValue = date });

        await using (var writeContext = NewContext())
            await new PostgresDataRepository(writeContext).Sync.CreateConnectedSystemObjectsAsync([cso]);

        return (systemId, cso.Id);
    }

    private async Task<(ConnectedSystemObject Cso, ConnectedSystemObjectType ObjectType)> HydrateAsync(int systemId, Guid csoId)
    {
        // The import hydrates a page's objects with this method before diffing them.
        var readContext = NewContext();
        var cso = (await new PostgresDataRepository(readContext).Sync.GetConnectedSystemObjectsByIdsAsync(systemId, [csoId])).Single();
        return (cso, cso.Type);
    }

    private static ConnectedSystemImportObject ImportOf(ConnectedSystemObjectType objectType, List<DateTime> dates)
    {
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = objectType.Name,
            Attributes = [new ConnectedSystemImportObjectAttribute { Name = "pwdFailureTime", DateTimeValues = dates }]
        };

        // As the import does, before anything reads the values.
        ImportValueNormaliser.Normalise(importObject, ImportValueNormaliser.BuildDateAndBooleanLookup(objectType), new ImportValueNormalisationSummary());
        return importObject;
    }

    private static void Diff(ConnectedSystemImportObject importObject, ConnectedSystemObject cso, ConnectedSystemObjectType objectType) =>
        UpdateMethod.Invoke(null, [importObject, cso, objectType, new ActivityRunProfileExecutionItem(), null]);
}
