// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Reflection;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage for a Decimal anchor across the three repository reads the import path
/// depends on (#1283): the Connected System Object lookup index, deletion detection's set of known
/// anchors, and the single-object fetch that obsoletes one.
/// </summary>
/// <remarks>
/// <para>
/// Oracle's <c>NUMBER</c> is discovered as <see cref="AttributeDataType.Decimal"/>, so a sequence-backed
/// primary key (the ordinary case on that provider) arrives as a decimal. None of these three reads
/// considered decimals: the lookup key came out null so every import created a duplicate, deletion
/// detection threw a bare <c>ArgumentOutOfRangeException</c>, and no fetch overload existed at all.
/// </para>
/// <para>
/// Real PostgreSQL matters for two distinct reasons. The first is the same one the sibling anchor
/// fixture documents: which typed column a value physically occupies is the question, and the
/// in-memory provider evaluates projections over tracked CLR graphs, so a fixture free to populate
/// several columns would pass regardless. The second is specific to decimals: whether
/// <c>4200.00</c> stored in the database matches <c>4200</c> supplied by an import is a question about
/// PostgreSQL <c>numeric</c> comparison semantics and EF's translation of it, which no in-memory fake
/// can answer. That is asserted here rather than assumed.
/// </para>
/// <para>
/// Objects are built by invoking the production import writer for the reason the sibling fixture gives:
/// which column an imported anchor lands in must not be decided by the test.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class DecimalExternalIdDatabaseTests
{
    private const string ExternalIdAttributeName = "anchor";
    private const string DisplayNameAttributeName = "displayName";

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Decimal external id tests.");

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
    public async Task GetAllCsoImportStateLookupAsync_DecimalAnchor_IndexesTheObjectAsync()
    {
        // Arrange: an Oracle-shaped sequence-backed key.
        var anchor = decimal.Parse("4200", CultureInfo.InvariantCulture);
        var (systemId, objectTypeId, anchorAttributeId) = await SeedImportedCsoAsync(anchor, "Ada Lovelace");
        _ = objectTypeId;

        // Act
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var lookup = await repository.ConnectedSystems.GetAllCsoImportStateLookupAsync(systemId);

        // Assert
        Assert.That(lookup, Is.Not.Empty,
            "A Decimal-anchored Connected System Object must be indexed. An object missing from this lookup is " +
            "indistinguishable from a new one, so the next import creates a duplicate and reports nothing.");
        Assert.That(lookup.ContainsKey($"cso:{systemId}:{anchorAttributeId}:4200"), Is.True,
            $"Expected the canonical key; the lookup held: {string.Join(", ", lookup.Keys)}");
    }

    [Test]
    public async Task GetAllCsoImportStateLookupAsync_DecimalAnchorCarryingScale_KeysCanonicallyAsync()
    {
        // Arrange: the same numeric anchor, stored with a scale. NUMBER(10,2) round-trips as 4200.00.
        var anchor = decimal.Parse("4200.00", CultureInfo.InvariantCulture);
        var (systemId, _, anchorAttributeId) = await SeedImportedCsoAsync(anchor, "Grace Hopper");

        // Act
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var lookup = await repository.ConnectedSystems.GetAllCsoImportStateLookupAsync(systemId);

        // Assert: the key must not carry the scale, or the same object keys two different ways
        // depending on what the database handed back.
        Assert.That(lookup.ContainsKey($"cso:{systemId}:{anchorAttributeId}:4200"), Is.True,
            $"Expected a scale-independent key; the lookup held: {string.Join(", ", lookup.Keys)}");
    }

    [Test]
    public async Task GetAllExternalIdAttributeValuesOfTypeDecimalAsync_ReturnsEveryAnchorAsync()
    {
        // Arrange: deletion detection compares this set against what the import returned. If it comes
        // back empty for a Decimal-anchored Object Type, every object looks deleted.
        var (systemId, objectTypeId, _) = await SeedImportedCsosAsync(
            (decimal.Parse("10", CultureInfo.InvariantCulture), "Ada Lovelace"),
            (decimal.Parse("20.5", CultureInfo.InvariantCulture), "Grace Hopper"));

        // Act
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var anchors = await repository.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeDecimalAsync(systemId, objectTypeId);

        // Assert
        Assert.That(anchors, Has.Count.EqualTo(2));
        Assert.That(anchors, Does.Contain(decimal.Parse("10", CultureInfo.InvariantCulture)));
        Assert.That(anchors, Does.Contain(decimal.Parse("20.5", CultureInfo.InvariantCulture)));
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_DecimalAnchorSuppliedWithoutScale_MatchesTheStoredValueAsync()
    {
        // Arrange: stored carrying a scale, looked up without one. This is the pairing that decides
        // whether deletion detection can find the object it has just decided is absent, and it is a
        // question about PostgreSQL numeric comparison, not about C# decimal equality.
        var stored = decimal.Parse("4200.00", CultureInfo.InvariantCulture);
        var (systemId, _, anchorAttributeId) = await SeedImportedCsoAsync(stored, "Katherine Johnson");

        // Act
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var cso = await repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(
            systemId, anchorAttributeId, decimal.Parse("4200", CultureInfo.InvariantCulture));

        // Assert
        Assert.That(cso, Is.Not.Null,
            "A stored 4200.00 must be found by a supplied 4200, or an object is obsoleted by a scale difference alone.");
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_DecimalAnchorThatDoesNotExist_ReturnsNullAsync()
    {
        // The negative case matters as much: matching must not be so loose that a different anchor matches.
        var (systemId, _, anchorAttributeId) = await SeedImportedCsoAsync(
            decimal.Parse("4200", CultureInfo.InvariantCulture), "Ada Lovelace");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var cso = await repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(
            systemId, anchorAttributeId, decimal.Parse("4201", CultureInfo.InvariantCulture));

        Assert.That(cso, Is.Null);
    }

    private async Task<(int SystemId, int ObjectTypeId, int AnchorAttributeId)> SeedImportedCsoAsync(decimal anchor, string displayName)
    {
        return await SeedImportedCsosAsync((anchor, displayName));
    }

    /// <returns>The Connected System's id, its Object Type's id, and the anchor attribute's id.</returns>
    private async Task<(int SystemId, int ObjectTypeId, int AnchorAttributeId)> SeedImportedCsosAsync(
        params (decimal Anchor, string DisplayName)[] objects)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = connectedSystem, Selected = true };
        var anchorAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = ExternalIdAttributeName,
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Decimal,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true,
            IsExternalId = true
        };
        objectType.Attributes.Add(anchorAttribute);
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Name = DisplayNameAttributeName,
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true
        });
        seed.AddRange(connectorDefinition, connectedSystem, objectType);
        await seed.SaveChangesAsync();

        foreach (var (anchor, displayName) in objects)
        {
            var importObject = new ConnectedSystemImportObject
            {
                ChangeType = ObjectChangeType.NotSet,
                ObjectType = objectType.Name,
                Attributes =
                [
                    new ConnectedSystemImportObjectAttribute
                    {
                        Name = ExternalIdAttributeName,
                        DecimalValues = [anchor]
                    },
                    new ConnectedSystemImportObjectAttribute
                    {
                        Name = DisplayNameAttributeName,
                        StringValues = [displayName]
                    }
                ]
            };

            seed.Add(CreateCsoViaProductionImportWriter(connectedSystem, objectType, importObject));
            await seed.SaveChangesAsync();
        }

        return (connectedSystem.Id, objectType.Id, anchorAttribute.Id);
    }

    /// <summary>
    /// Builds a Connected System Object exactly as a Full Import does, by invoking
    /// <c>SyncImportTaskProcessor.CreateConnectedSystemObjectFromImportObject</c>. Which typed column the
    /// anchor lands in is part of what is under test, so the fixture must not decide it.
    /// </summary>
    private static ConnectedSystemObject CreateCsoViaProductionImportWriter(
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType objectType,
        ConnectedSystemImportObject importObject)
    {
        var runProfile = new ConnectedSystemRunProfile
        {
            Name = "Full Import",
            RunType = ConnectedSystemRunType.FullImport,
            ConnectedSystemId = connectedSystem.Id
        };
        var workerTask = TestUtilities.CreateTestWorkerTask(new Activity(), initiatedBy: null);
        using var cancellationTokenSource = new CancellationTokenSource();

        var processor = new SyncImportTaskProcessor(
            null!,
            null!,
            null!,
            new SyncEngine(),
            new MockFileConnector(),
            connectedSystem,
            runProfile,
            workerTask,
            cancellationTokenSource);

        const string writerName = "CreateConnectedSystemObjectFromImportObject";
        var writer = typeof(SyncImportTaskProcessor).GetMethod(writerName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{writerName} was not found. If it has been renamed or its signature has " +
                "changed, update this fixture to invoke the current production import writer; do not replace it with " +
                "a hand-built Connected System Object, because which typed column the anchor lands in is part of the " +
                "behaviour under test.");

        var executionItem = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var cso = writer.Invoke(processor, [importObject, objectType, executionItem]) as ConnectedSystemObject;

        Assert.That(cso, Is.Not.Null,
            $"The import writer rejected the test import object: {executionItem.ErrorType} {executionItem.ErrorMessage}");

        executionItem.ConnectedSystemObject = null;
        return cso;
    }
}
