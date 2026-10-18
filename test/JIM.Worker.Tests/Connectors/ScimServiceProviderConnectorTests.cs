// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.SCIM.ServiceProvider;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Scim.Schema;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The SCIM 2.0 Service Provider Connector: JIM's own SCIM endpoint is the Connected System, so the Connector reads the
/// store the endpoint writes rather than calling out. Its schema is the store's definitions, a Full Import reads every
/// live resource, and a Delta Import reads the changes between the watermark the last import reached and the store's
/// committed sequence when this one began, tombstones arriving as deletions.
/// </summary>
[TestFixture]
public class ScimServiceProviderConnectorTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    #region capabilities
    [Test]
    public void Capabilities_ImportOnly_FullAndDeltaWithScimSchemaStandard()
    {
        var connector = new ScimServiceProviderConnector();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.SupportsFullImport, Is.True);
            Assert.That(connector.SupportsDeltaImport, Is.True);
            Assert.That(connector.SupportsExport, Is.False);
            Assert.That(connector.SupportsUserSelectedExternalId, Is.False);
            Assert.That(connector.SchemaStandard, Is.EqualTo(AttributeStandard.Scim));
            Assert.That(connector.GetSettings(), Is.Empty);
        }
    }
    #endregion

    #region schema
    [Test]
    public async Task GetSchemaAsync_CoreDefinitions_ObjectTypesAreTheResourceTypesAnchoredOnIdAsync()
    {
        var connector = Connector(new FakeScimResourceStore());

        var schema = await connector.GetSchemaAsync([], Logger);

        Assert.That(schema.ObjectTypes.Select(o => o.Name), Is.EquivalentTo(new[] { "User", "Group" }));
        var user = schema.ObjectTypes.Single(o => o.Name == "User");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(user.RecommendedExternalIdAttribute?.Name, Is.EqualTo(ScimCommonAttributes.Id));
            Assert.That(user.Attributes.Select(a => a.Name), Does.Contain("userName"));
            Assert.That(schema.ObjectTypes.Single(o => o.Name == "Group").Attributes.Single(a => a.Name == "members").Type,
                Is.EqualTo(AttributeDataType.Reference));
        }
    }

    [Test]
    public async Task GetSchemaAsync_WriteOnlyAndLocation_LeftOutAsync()
    {
        var connector = Connector(new FakeScimResourceStore());

        var schema = await connector.GetSchemaAsync([], Logger);

        var names = schema.ObjectTypes.Single(o => o.Name == "User").Attributes.Select(a => a.Name).ToList();
        using (Assert.EnterMultipleScope())
        {
            // A client writes password and nothing ever reads it back; the store holds no base URL to form a location from.
            Assert.That(names, Does.Not.Contain("password"));
            Assert.That(names, Does.Not.Contain("meta.location"));
            Assert.That(names, Does.Contain("meta.lastModified"));
        }
    }

    [Test]
    public void GetSchemaAsync_NoStore_Throws()
    {
        var connector = new ScimServiceProviderConnector();

        Assert.That(() => connector.GetSchemaAsync([], Logger), Throws.InvalidOperationException);
    }
    #endregion

    #region full import
    [Test]
    public async Task ImportAsync_FullImport_ReadsEveryLiveResourceAcrossPagesAndReturnsTheHorizonAsync()
    {
        var store = new FakeScimResourceStore();
        var alice = store.AddLive("User", """{"userName":"alice","displayName":"Alice"}""");
        store.AddLive("User", """{"userName":"bob"}""");
        store.AddLive("User", """{"userName":"carol"}""");
        store.Tombstone(store.AddLive("User", """{"userName":"dave"}"""));
        var horizon = store.State!.LastChangeSequence;

        var results = await RunAsync(Connector(store), store, FullImport(pageSize: 2));

        var objects = results.SelectMany(r => r.ImportObjects).ToList();
        Assert.That(results, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects.Select(o => Text(o, "userName")), Is.EquivalentTo(new[] { "alice", "bob", "carol" }));
            Assert.That(objects.Select(o => o.ChangeType), Has.All.EqualTo(ObjectChangeType.Added));
            Assert.That(objects.Select(o => o.ObjectType), Has.All.EqualTo("User"));
            Assert.That(Text(objects.Single(o => Text(o, "userName") == "alice"), "id"), Is.EqualTo(alice.Id.ToString("D")));
            Assert.That(store.Reads.Select(q => q.ChangedAfter), Has.All.Null);
            Assert.That(results[0].PersistedConnectorData, Is.Null);
            Assert.That(results[0].PaginationTokens, Is.Not.Empty);
            Assert.That(Watermark(results), Is.EqualTo(horizon));
        }
    }

    [Test]
    public async Task ImportAsync_FullImport_ReferencesImportedAsTheTargetIdsAsync()
    {
        var store = new FakeScimResourceStore();
        var alice = store.AddLive("User", """{"userName":"alice"}""");
        var bob = store.AddLive("User", """{"userName":"bob"}""");
        var group = store.AddLive("Group", """{"displayName":"Engineers"}""");
        store.Refer(group, "members", alice);
        store.Refer(group, "members", bob);

        var results = await RunAsync(Connector(store), store, FullImport());

        var imported = results.SelectMany(r => r.ImportObjects).Single(o => o.ObjectType == "Group");
        Assert.That(imported.Attributes.Single(a => a.Name == "members").ReferenceValues,
            Is.EquivalentTo(new[] { alice.Id.ToString("D"), bob.Id.ToString("D") }));
    }

    [Test]
    public async Task ImportAsync_FullImport_OnlySelectedObjectTypesAndAttributesAsync()
    {
        var store = new FakeScimResourceStore();
        store.AddLive("User", """{"userName":"alice","displayName":"Alice"}""");
        store.AddLive("Group", """{"displayName":"Engineers"}""");
        var connectedSystem = ConnectedSystem(store, selected: ["User"]);
        connectedSystem.ObjectTypes!.Single().Attributes =
        [
            new ConnectedSystemObjectTypeAttribute { Name = "id", Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "userName", Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "displayName", Selected = false }
        ];

        var results = await RunAsync(Connector(store), connectedSystem, FullImport());

        var imported = results.SelectMany(r => r.ImportObjects).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.ObjectType, Is.EqualTo("User"));
            Assert.That(imported.Attributes.Select(a => a.Name), Is.EquivalentTo(new[] { "id", "userName" }));
        }
    }

    [Test]
    public async Task ImportAsync_EmptyStore_CompletesWithWatermarkZeroAsync()
    {
        var store = new FakeScimResourceStore();

        var results = await RunAsync(Connector(store), store, FullImport());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.SelectMany(r => r.ImportObjects), Is.Empty);
            Assert.That(Watermark(results), Is.Zero);
        }
    }
    #endregion

    #region delta import
    [Test]
    public async Task ImportAsync_DeltaImport_ReadsTheChangesAfterTheWatermarkWithTombstonesAsDeletionsAsync()
    {
        var store = new FakeScimResourceStore();
        store.AddLive("User", """{"userName":"alice"}""");
        var bob = store.AddLive("User", """{"userName":"bob"}""");
        var watermark = store.State!.LastChangeSequence;
        store.AddLive("User", """{"userName":"carol"}""");
        store.Tombstone(bob);
        var horizon = store.State.LastChangeSequence;

        var results = await RunAsync(Connector(store), store, DeltaImport(), State(watermark));

        var objects = results.SelectMany(r => r.ImportObjects).ToList();
        var deleted = objects.Single(o => o.ChangeType == ObjectChangeType.Deleted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects.Where(o => o.ChangeType == ObjectChangeType.Added).Select(o => Text(o, "userName")), Is.EquivalentTo(new[] { "carol" }));
            Assert.That(deleted.ObjectType, Is.EqualTo("User"));
            Assert.That(deleted.Attributes.Select(a => a.Name), Is.EquivalentTo(new[] { "id" }));
            Assert.That(Text(deleted, "id"), Is.EqualTo(bob.Id.ToString("D")));
            Assert.That(store.Reads.Select(q => (q.ChangedAfter, q.ChangedUpTo)), Has.All.EqualTo(((long?)watermark, (long?)horizon)));
            Assert.That(results.Single().WarningMessage, Is.Null);
            Assert.That(Watermark(results), Is.EqualTo(horizon));
        }
    }

    [Test]
    public async Task ImportAsync_DeltaImport_HorizonHeldFromTheFirstPageAsync()
    {
        var store = new FakeScimResourceStore();
        store.AddLive("User", """{"userName":"alice"}""");
        store.AddLive("User", """{"userName":"bob"}""");
        store.AddLive("User", """{"userName":"carol"}""");
        var horizon = store.State!.LastChangeSequence;

        // A write committed mid-run is the next import's: reading it now would leave the watermark claiming less than was read.
        var results = await RunAsync(Connector(store), store, DeltaImport(pageSize: 2), State(0),
            afterPage: page => { if (page == 1) store.AddLive("User", """{"userName":"dave"}"""); });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Reads.Select(q => q.ChangedUpTo), Has.All.EqualTo((long?)horizon));
            Assert.That(results.SelectMany(r => r.ImportObjects).Select(o => Text(o, "userName")), Does.Not.Contain("dave"));
            Assert.That(Watermark(results), Is.EqualTo(horizon));
        }
    }

    [Test]
    public async Task ImportAsync_DeltaImportWithoutWatermark_ReadsEverythingWithAWarningAsync()
    {
        var store = new FakeScimResourceStore();
        store.AddLive("User", """{"userName":"alice"}""");
        var horizon = store.State!.LastChangeSequence;

        var results = await RunAsync(Connector(store), store, DeltaImport(), persistedConnectorData: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Reads.Select(q => q.ChangedAfter), Has.All.Null);
            Assert.That(results[0].WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(results[0].WarningMessage, Is.Not.Null);
            Assert.That(results.SelectMany(r => r.ImportObjects).Select(o => Text(o, "userName")), Is.EquivalentTo(new[] { "alice" }));
            Assert.That(Watermark(results), Is.EqualTo(horizon));
        }
    }

    [Test]
    public void ImportAsync_DeltaImportWithWatermarkBelowThePurgeFloor_FailsAsync()
    {
        var store = new FakeScimResourceStore();
        for (var i = 0; i < 6; i++)
            store.AddLive("User", $$"""{"userName":"user{{i}}"}""");
        store.State!.TombstonePurgeFloor = 5;

        // Deletions after the watermark may have been purged already, so reading on would miss them silently.
        Assert.That(() => RunAsync(Connector(store), store, DeltaImport(), State(4)),
            Throws.InstanceOf<CannotPerformDeltaImportException>().With.Message.Contains("purged").And.Message.Contains("Full Import"));
        Assert.That(store.Reads, Is.Empty);
    }

    [Test]
    public async Task ImportAsync_DeltaImportWithWatermarkAtThePurgeFloor_ReadsAsync()
    {
        var store = new FakeScimResourceStore();
        for (var i = 0; i < 6; i++)
            store.AddLive("User", $$"""{"userName":"user{{i}}"}""");
        store.State!.TombstonePurgeFloor = 5;

        var results = await RunAsync(Connector(store), store, DeltaImport(), State(5));

        Assert.That(results.SelectMany(r => r.ImportObjects).Select(o => Text(o, "userName")), Is.EquivalentTo(new[] { "user5" }));
    }

    [Test]
    public void ImportAsync_DeltaImportWithWatermarkAheadOfTheStore_FailsAsync()
    {
        var store = new FakeScimResourceStore();
        store.AddLive("User", """{"userName":"alice"}""");

        Assert.That(() => RunAsync(Connector(store), store, DeltaImport(), State(7)),
            Throws.InstanceOf<CannotPerformDeltaImportException>());
    }
    #endregion

    #region purge
    [Test]
    public async Task ImportAsync_WithPreviousWatermark_PurgesTombstonesItHasReadAndAWeekOldAsync()
    {
        var store = new FakeScimResourceStore();
        store.AddLive("User", """{"userName":"alice"}""");
        store.AddLive("User", """{"userName":"bob"}""");

        await RunAsync(Connector(store), store, DeltaImport(pageSize: 1), State(1));

        Assert.That(store.Purges, Is.EqualTo(new[] { (1L, Now.AddDays(-7)) }));
    }

    [Test]
    public async Task ImportAsync_WithoutPreviousWatermark_PurgesNothingAsync()
    {
        var store = new FakeScimResourceStore();
        store.AddLive("User", """{"userName":"alice"}""");

        await RunAsync(Connector(store), store, FullImport(), persistedConnectorData: null);

        Assert.That(store.Purges, Is.Empty);
    }
    #endregion

    #region store
    [Test]
    public void ImportAsync_NoStore_Throws()
    {
        var connector = new ScimServiceProviderConnector();
        var store = new FakeScimResourceStore();

        Assert.That(() => RunAsync(connector, store, FullImport()), Throws.InvalidOperationException);
    }

    [Test]
    public void ImportAsync_UnreadablePersistedData_FallsBackWithAWarningAsync()
    {
        var store = new FakeScimResourceStore();
        store.AddLive("User", """{"userName":"alice"}""");

        Assert.That(async () =>
        {
            var results = await RunAsync(Connector(store), store, DeltaImport(), "not json");
            Assert.That(results[0].WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
        }, Throws.Nothing);
    }
    #endregion

    #region helpers
    private static ScimServiceProviderConnector Connector(FakeScimResourceStore store)
    {
        var connector = new ScimServiceProviderConnector { UtcNow = () => Now };
        connector.SetScimResourceStore(store);
        return connector;
    }

    private static ConnectedSystem ConnectedSystem(FakeScimResourceStore store, string[]? selected = null) =>
        new()
        {
            Name = "SCIM Service Provider",
            SettingValues = [],
            ObjectTypes = (selected ?? store.ResourceTypes.Select(t => t.Name).ToArray())
                .Select(name => new ConnectedSystemObjectType { Name = name, Selected = true })
                .ToList()
        };

    private static ConnectedSystemRunProfile FullImport(int pageSize = 100) =>
        new() { Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, PageSize = pageSize };

    private static ConnectedSystemRunProfile DeltaImport(int pageSize = 100) =>
        new() { Name = "Delta Import", RunType = ConnectedSystemRunType.DeltaImport, PageSize = pageSize };

    private static string State(long watermark) => new ScimServiceProviderImportState { ChangeSequence = watermark }.Serialise();

    private static long? Watermark(IEnumerable<ConnectedSystemImportResult> results) =>
        ScimServiceProviderImportState.Read(ScimImportRunner.PersistedConnectorData(results), Logger)?.ChangeSequence;

    private static string? Text(ConnectedSystemImportObject importObject, string attribute) =>
        importObject.Attributes.SingleOrDefault(a => a.Name == attribute)?.StringValues.SingleOrDefault();

    private static Task<List<ConnectedSystemImportResult>> RunAsync(ScimServiceProviderConnector connector, FakeScimResourceStore store,
        ConnectedSystemRunProfile runProfile, string? persistedConnectorData = null, Action<int>? afterPage = null) =>
        RunAsync(connector, ConnectedSystem(store), runProfile, persistedConnectorData, afterPage);

    /// <summary>
    /// Drives the Connector the way JIM does: open once, ask for pages until no pagination tokens come back, close.
    /// </summary>
    private static async Task<List<ConnectedSystemImportResult>> RunAsync(ScimServiceProviderConnector connector, ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile, string? persistedConnectorData = null, Action<int>? afterPage = null)
    {
        connector.OpenImportConnection(connectedSystem.SettingValues!, persistedConnectorData, Logger);
        var results = new List<ConnectedSystemImportResult>();
        var tokens = new List<ConnectedSystemPaginationToken>();
        try
        {
            for (var page = 1; page <= 20; page++)
            {
                var result = await connector.ImportAsync(connectedSystem, runProfile, tokens, persistedConnectorData, Logger, CancellationToken.None,
                    new RecordingConnectorProgress());
                results.Add(result);
                afterPage?.Invoke(page);
                if (result.PaginationTokens.Count == 0)
                    return results;
                tokens = result.PaginationTokens;
            }
        }
        finally
        {
            connector.CloseImportConnection();
        }

        Assert.Fail("The import did not finish within 20 pages.");
        return results;
    }
    #endregion
}
