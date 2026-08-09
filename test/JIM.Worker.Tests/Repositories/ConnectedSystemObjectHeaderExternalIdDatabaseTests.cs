// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.PostgresData;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL characterisation of the External Id column on the Connector Space list, i.e. the
/// <c>ExternalIdValue</c> projection in <c>ConnectedSystemRepository.GetConnectedSystemObjectHeadersAsync</c>,
/// across every anchor data type a Connected System can declare.
/// </summary>
/// <remarks>
/// <para>
/// The projection reads <c>ConnectedSystemObjectAttributeValue.StringValue</c> alone, but the import path
/// stores each imported value in the typed column matching the attribute's declared
/// <see cref="AttributeDataType"/>: a Guid anchor lands in <c>GuidValue</c>, an Int anchor in <c>IntValue</c>,
/// and only a Text anchor in <c>StringValue</c>. These tests exist to settle, with evidence rather than
/// reading, what the projection actually returns per anchor type. Issue #1286.
/// </para>
/// <para>
/// Real PostgreSQL matters because the assertion is about which typed column a value physically occupies.
/// EF Core's in-memory provider evaluates the projection over tracked CLR graphs, so a fixture that happened
/// to set both <c>StringValue</c> and <c>GuidValue</c> would pass there regardless; and a Moq-based fixture
/// (which is what the existing API-controller coverage uses) never executes the projection at all.
/// </para>
/// <para>
/// The Connected System Objects under test are built by invoking the production import writer,
/// <c>SyncImportTaskProcessor.CreateConnectedSystemObjectFromImportObject</c>, rather than by hand. Which
/// typed column an imported anchor occupies is the entire question, so the fixture must not be free to
/// answer it by construction. The method is private and the surrounding import loop needs an activity, a
/// run profile and a live sync repository to drive end to end, so it is reached reflectively; the method
/// itself touches only the Connected System and Run Profile fields, both of which are supplied for real.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemObjectHeaderExternalIdDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Connected System Object header external id tests.");

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
    public async Task GetConnectedSystemObjectHeadersAsync_GuidAnchor_ProjectsTheGuidAsTheExternalIdValueAsync()
    {
        // Arrange: an Active Directory-shaped anchor. LdapConnectorRootDse.ExternalIdDataType types
        // objectGUID as AttributeDataType.Guid for ActiveDirectory and SambaAD.
        var anchor = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        var systemId = await SeedImportedCsoAsync(
            AttributeDataType.Guid,
            attribute => attribute.GuidValues.Add(anchor),
            displayName: "Ada Lovelace");

        // Act
        var header = await GetSingleHeaderAsync(systemId);

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.DisplayName, Is.EqualTo("Ada Lovelace"),
                "The rest of the row must project normally, so any external id failure is specific to the anchor.");
            Assert.That(header.ExternalIdAttributeName, Is.EqualTo(ExternalIdAttributeName));
            Assert.That(header.ExternalIdValue, Is.EqualTo(anchor.ToString()),
                "A Guid-anchored Connected System Object must surface its anchor in the Connector Space list.");
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectHeadersAsync_IntAnchor_ProjectsTheNumberAsTheExternalIdValueAsync()
    {
        // Arrange: a SQL-shaped anchor, e.g. an integer primary key column.
        const int anchor = 40711;
        var systemId = await SeedImportedCsoAsync(
            AttributeDataType.Number,
            attribute => attribute.IntValues.Add(anchor),
            displayName: "Grace Hopper");

        // Act
        var header = await GetSingleHeaderAsync(systemId);

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.DisplayName, Is.EqualTo("Grace Hopper"));
            Assert.That(header.ExternalIdAttributeName, Is.EqualTo(ExternalIdAttributeName));
            Assert.That(header.ExternalIdValue, Is.EqualTo(anchor.ToString()),
                "An Int-anchored Connected System Object must surface its anchor in the Connector Space list.");
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectHeadersAsync_TextAnchor_ProjectsTheStringAsTheExternalIdValueAsync()
    {
        // Arrange: an OpenLDAP-shaped anchor. Rfc4512SchemaParser types entryUUID as
        // AttributeDataType.Text, so the same logical UUID is stored as a string here.
        const string anchor = "b18b3e5c-1f2c-103a-9c1a-6f0f5f1c8f5b";
        var systemId = await SeedImportedCsoAsync(
            AttributeDataType.Text,
            attribute => attribute.StringValues.Add(anchor),
            displayName: "Alan Turing");

        // Act
        var header = await GetSingleHeaderAsync(systemId);

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.DisplayName, Is.EqualTo("Alan Turing"));
            Assert.That(header.ExternalIdAttributeName, Is.EqualTo(ExternalIdAttributeName));
            Assert.That(header.ExternalIdValue, Is.EqualTo(anchor),
                "A Text-anchored Connected System Object must surface its anchor in the Connector Space list.");
        }
    }

    /// <summary>
    /// Records which typed column the production import writer puts an anchor in, per declared data type.
    /// This is the premise the three projection tests rest on; if the writer ever starts populating
    /// <c>StringValue</c> alongside the typed column, this test says so directly rather than leaving the
    /// projection tests to pass for a reason nobody stated.
    /// </summary>
    [Test]
    public async Task ImportWriter_TypedAnchors_PopulateOnlyTheColumnMatchingTheDeclaredDataTypeAsync()
    {
        // Arrange
        var guidAnchor = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        var guidSystemId = await SeedImportedCsoAsync(
            AttributeDataType.Guid, attribute => attribute.GuidValues.Add(guidAnchor), "Ada Lovelace");

        await SetUp();
        var intSystemId = await SeedImportedCsoAsync(
            AttributeDataType.Number, attribute => attribute.IntValues.Add(40711), "Grace Hopper");
        var intAnchorValue = await GetPersistedAnchorAsync(intSystemId);

        await SetUp();
        var textSystemId = await SeedImportedCsoAsync(
            AttributeDataType.Text, attribute => attribute.StringValues.Add("entry-uuid"), "Alan Turing");
        var textAnchorValue = await GetPersistedAnchorAsync(textSystemId);

        // The Guid seed was truncated away above, so re-seed it last and read it back.
        await SetUp();
        guidSystemId = await SeedImportedCsoAsync(
            AttributeDataType.Guid, attribute => attribute.GuidValues.Add(guidAnchor), "Ada Lovelace");
        var guidAnchorValue = await GetPersistedAnchorAsync(guidSystemId);

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(guidAnchorValue.GuidValue, Is.EqualTo(guidAnchor));
            Assert.That(guidAnchorValue.StringValue, Is.Null,
                "The import writer stores a Guid anchor in GuidValue only.");
            Assert.That(intAnchorValue.IntValue, Is.EqualTo(40711));
            Assert.That(intAnchorValue.StringValue, Is.Null,
                "The import writer stores an Int anchor in IntValue only.");
            Assert.That(textAnchorValue.StringValue, Is.EqualTo("entry-uuid"));
            Assert.That(textAnchorValue.GuidValue, Is.Null);
        }
    }

    #region helpers

    /// <summary>
    /// Seeds a Connected System carrying one object type with an anchor attribute of the supplied data type,
    /// and one Connected System Object built by the production import writer.
    /// </summary>
    /// <returns>The Connected System's id.</returns>
    private async Task<int> SeedImportedCsoAsync(
        AttributeDataType anchorDataType,
        Action<ConnectedSystemImportObjectAttribute> populateAnchor,
        string displayName)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = connectedSystem, Selected = true };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Name = ExternalIdAttributeName,
            ConnectedSystemObjectType = objectType,
            Type = anchorDataType,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true,
            IsExternalId = true
        });
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

        var anchorAttribute = new ConnectedSystemImportObjectAttribute { Name = ExternalIdAttributeName };
        populateAnchor(anchorAttribute);

        var importObject = new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = objectType.Name,
            Attributes =
            [
                anchorAttribute,
                new ConnectedSystemImportObjectAttribute
                {
                    Name = DisplayNameAttributeName,
                    StringValues = [displayName]
                }
            ]
        };

        var cso = CreateCsoViaProductionImportWriter(connectedSystem, objectType, importObject);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        return connectedSystem.Id;
    }

    /// <summary>
    /// Builds a Connected System Object exactly as a Full Import does, by invoking
    /// <c>SyncImportTaskProcessor.CreateConnectedSystemObjectFromImportObject</c>. See the fixture remarks
    /// for why this is reached reflectively rather than mirrored by hand.
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

        // The writer under test reads only the Connected System and the Run Profile from the processor, both
        // supplied for real below. The application, repository, sync server and sync engine are constructor
        // dependencies of the wider import loop and are never dereferenced on this path.
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
                "a hand-built Connected System Object, because which typed column the anchor lands in is the " +
                "behaviour under test.");

        var executionItem = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var cso = writer.Invoke(processor, [importObject, objectType, executionItem]) as ConnectedSystemObject;

        Assert.That(cso, Is.Not.Null,
            $"The import writer rejected the test import object: {executionItem.ErrorType} {executionItem.ErrorMessage}");

        // The writer links the execution item to the object it built. Nothing in this fixture persists
        // execution items, so break the link before EF sees the graph.
        executionItem.ConnectedSystemObject = null;
        return cso;
    }

    private async Task<ConnectedSystemObjectHeader> GetSingleHeaderAsync(int connectedSystemId)
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var page = await repository.ConnectedSystems.GetConnectedSystemObjectHeadersAsync(
            connectedSystemId, page: 1, pageSize: 10);

        Assert.That(page.Results, Has.Count.EqualTo(1), "Expected exactly one seeded Connected System Object.");
        return page.Results[0];
    }

    private async Task<ConnectedSystemObjectAttributeValue> GetPersistedAnchorAsync(int connectedSystemId)
    {
        await using var ctx = NewContext();

        var anchor = await ctx.ConnectedSystemObjectAttributeValues
            .AsNoTracking()
            .Where(av => av.ConnectedSystemObject.ConnectedSystemId == connectedSystemId &&
                         av.Attribute.Name == ExternalIdAttributeName)
            .SingleAsync();

        return anchor;
    }

    #endregion
}
