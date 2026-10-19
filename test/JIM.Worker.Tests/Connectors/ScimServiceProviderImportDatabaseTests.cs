// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Application;
using JIM.Application.Services;
using JIM.Connectors.SCIM.ServiceProvider;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.Scim;
using JIM.Scim.Service;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The SCIM 2.0 Service Provider end to end below HTTP (#124, Phase 3): what a SCIM client writes through the server is
/// what the Connector imports, through the real store and its seam. A Full Import, then a Delta Import that sees a new
/// User, a changed Group membership and a deletion, then the purge of the tombstone the Delta Import read, and the
/// failure of a Delta Import that would have needed it.
/// </summary>
/// <remarks>
/// Real PostgreSQL and a no-tracking context, as JIM.Web runs; opt-in via the <c>JIM_TEST_RESET_*</c> environment
/// variables like the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScimServiceProviderImportDatabaseTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    private string _connectionString = null!;
    private int _systemId;
    private ApiKey _key = null!;

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SCIM Service Provider import tests.");

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
        await PostgresTestDatabase.ResetAsync(_connectionString);

        await using var ctx = NewContext();
        var definition = new ConnectorDefinition { Name = "scim-service-provider-import-tests" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();
        var system = new ConnectedSystem { Name = "Entra provisioning", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.Add(system);
        _key = new ApiKey { Id = Guid.NewGuid(), Name = "Entra provisioning", KeyHash = "hash", KeyPrefix = "jim_ak_test" };
        ctx.ApiKeys.Add(_key);
        await ctx.SaveChangesAsync();
        _systemId = system.Id;

        await WithJimAsync(jim => jim.Scim.EnsureCoreDefinitionsAsync(_systemId));
    }

    [Test]
    public async Task FullThenDeltaImport_ReadsWhatClientsWroteIncludingDeletionsAsync()
    {
        var alice = await CreateAsync("/Users", User("alice"));
        var bob = await CreateAsync("/Users", User("bob"));
        var engineers = await CreateAsync("/Groups", Group("Engineers", alice, bob));

        var full = await ImportAsync(ConnectedSystemRunType.FullImport, persistedConnectorData: null);

        var fullObjects = full.SelectMany(r => r.ImportObjects).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fullObjects.Select(o => Text(o, "id")), Is.EquivalentTo(new[] { alice, bob, engineers }.Select(Format)));
            Assert.That(Members(fullObjects.Single(o => o.ObjectType == "Group")), Is.EquivalentTo(new[] { Format(alice), Format(bob) }));
        }

        // Deleting bob also takes him out of Engineers, so the Group changes too.
        Assert.That((await WithJimAsync(jim => jim.Scim.DeleteAsync(Context(), "/Users", Format(bob), ifMatch: null))).StatusCode, Is.EqualTo(204));
        var carol = await CreateAsync("/Users", User("carol"));

        var delta = await ImportAsync(ConnectedSystemRunType.DeltaImport, Watermark(full));

        var deltaObjects = delta.SelectMany(r => r.ImportObjects).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltaObjects.Where(o => o.ChangeType == ObjectChangeType.Deleted).Select(o => Text(o, "id")), Is.EqualTo(new[] { Format(bob) }));
            Assert.That(deltaObjects.Where(o => o.ChangeType == ObjectChangeType.Added).Select(o => Text(o, "id")),
                Is.EquivalentTo(new[] { Format(carol), Format(engineers) }));
            Assert.That(Members(deltaObjects.Single(o => o.ObjectType == "Group")), Is.EqualTo(new[] { Format(alice) }));
            Assert.That(Sequence(delta), Is.GreaterThan(Sequence(full)!.Value));
        }
    }

    [Test]
    public async Task DeltaImport_PurgesTombstonesAnEarlierImportReadAndAnOlderWatermarkThenFailsAsync()
    {
        var alice = await CreateAsync("/Users", User("alice"));
        var full = await ImportAsync(ConnectedSystemRunType.FullImport, persistedConnectorData: null);
        await WithJimAsync(jim => jim.Scim.DeleteAsync(Context(), "/Users", Format(alice), ifMatch: null));
        var delta = await ImportAsync(ConnectedSystemRunType.DeltaImport, Watermark(full));
        Assert.That(delta.SelectMany(r => r.ImportObjects).Single().ChangeType, Is.EqualTo(ObjectChangeType.Deleted), "precondition");

        // A week on, the next import purges the tombstone the last one read...
        await ImportAsync(ConnectedSystemRunType.DeltaImport, Watermark(delta), utcNow: DateTime.UtcNow.AddDays(8));

        await using (var ctx = NewContext())
        {
            var state = await ctx.ScimStoreStates.SingleAsync(s => s.ConnectedSystemId == _systemId);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await ctx.ScimStoreResources.CountAsync(r => r.ConnectedSystemId == _systemId), Is.Zero);
                Assert.That(state.TombstonePurgeFloor, Is.EqualTo(Sequence(delta)));
            }
        }

        // ...so a Delta Import still at the watermark before that deletion could no longer see it, and refuses to run.
        Assert.That(() => ImportAsync(ConnectedSystemRunType.DeltaImport, Watermark(full)), Throws.InstanceOf<CannotPerformDeltaImportException>());
    }

    private async Task<List<ConnectedSystemImportResult>> ImportAsync(ConnectedSystemRunType runType, string? persistedConnectorData, DateTime? utcNow = null)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        var connector = new ScimServiceProviderConnector { UtcNow = () => utcNow ?? DateTime.UtcNow };
        connector.SetScimResourceStore(new ScimResourceStoreService(jim, _systemId));
        var connectedSystem = new ConnectedSystem
        {
            Id = _systemId,
            Name = "Entra provisioning",
            ObjectTypes = [new ConnectedSystemObjectType { Name = "User", Selected = true }, new ConnectedSystemObjectType { Name = "Group", Selected = true }]
        };
        var runProfile = new ConnectedSystemRunProfile { Name = runType.ToString(), RunType = runType, PageSize = 2 };

        connector.OpenImportConnection([], persistedConnectorData, Logger);
        var results = new List<ConnectedSystemImportResult>();
        var tokens = new List<ConnectedSystemPaginationToken>();
        try
        {
            do
            {
                var result = await connector.ImportAsync(connectedSystem, runProfile, tokens, persistedConnectorData, Logger, CancellationToken.None,
                    new RecordingConnectorProgress());
                results.Add(result);
                tokens = result.PaginationTokens;
            } while (tokens.Count > 0 && results.Count < 20);
        }
        finally
        {
            connector.CloseImportConnection();
        }

        return results;
    }

    private async Task<Guid> CreateAsync(string endpoint, JsonObject body)
    {
        var result = await WithJimAsync(jim => jim.Scim.CreateAsync(Context(), endpoint, body));
        Assert.That(result.StatusCode, Is.EqualTo(201), result.Error?.Detail);
        return Guid.Parse(result.Resource!["id"]!.GetValue<string>());
    }

    private ScimRequestContext Context() => new(_systemId, "https://jim.example/scim/v2", _key.Id, _key.Name, "203.0.113.9");

    private static JsonObject User(string userName) =>
        new() { ["schemas"] = new JsonArray(ScimUrns.User), ["userName"] = userName, ["displayName"] = userName };

    private static JsonObject Group(string displayName, params Guid[] members) => new()
    {
        ["schemas"] = new JsonArray(ScimUrns.Group),
        ["displayName"] = displayName,
        ["members"] = new JsonArray(members.Select(m => (JsonNode)new JsonObject { ["value"] = Format(m) }).ToArray())
    };

    private static string Format(Guid id) => ScimReferenceEntries.Format(id);

    private static string? Watermark(List<ConnectedSystemImportResult> results) => ScimImportRunner.PersistedConnectorData(results);

    private static long? Sequence(List<ConnectedSystemImportResult> results) =>
        ScimServiceProviderImportState.Read(Watermark(results), Logger)?.ChangeSequence;

    private static string? Text(ConnectedSystemImportObject importObject, string attribute) =>
        importObject.Attributes.SingleOrDefault(a => a.Name == attribute)?.StringValues.SingleOrDefault();

    private static List<string> Members(ConnectedSystemImportObject group) =>
        group.Attributes.SingleOrDefault(a => a.Name == "members")?.ReferenceValues ?? [];

    private async Task<T> WithJimAsync<T>(Func<JimApplication, Task<T>> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        return await call(jim);
    }
}
