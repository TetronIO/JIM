// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Connectors;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Scim;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.Scim;
using JIM.Scim.Service;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Reading the SCIM journal back (#124, D17): a resource's accepted writes in order, optionally within a change
/// sequence range, and from a Connected System Object's change record, the requests behind that change: the
/// resource's entries between the watermark the import started from and the one it reached.
/// </summary>
/// <remarks>
/// Real PostgreSQL and a no-tracking context, as JIM.Web runs; opt-in via the <c>JIM_TEST_RESET_*</c> environment
/// variables like the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScimJournalReadDatabaseTests
{
    private string _connectionString = null!;
    private ApiKey _administrator = null!;
    private int _systemId;

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SCIM journal read tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        await WithJimAsync(jim => jim.Seeding.ApplyBuiltInConfigurationAsync());

        await using (var ctx = NewContext())
        {
            _administrator = new ApiKey { Id = Guid.NewGuid(), Name = "Administrator", KeyHash = "hash", KeyPrefix = "jim_ak_test" };
            ctx.ApiKeys.Add(_administrator);
            await ctx.SaveChangesAsync();
        }

        await SetFlagAsync(true);
        var definition = await WithJimAsync(jim => jim.ConnectedSystems.GetConnectorDefinitionAsync(ConnectorConstants.ScimServiceProviderConnectorName));
        var connectedSystem = new ConnectedSystem { Name = "Entra provisioning", ConnectorDefinitionId = definition!.Id };
        await WithJimAsync(jim => jim.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, _administrator));
        _systemId = connectedSystem.Id;
    }

    [Test]
    public async Task GetJournalAsync_AResourcesWritesInOrderAndNoOtherResourcesAsync()
    {
        var alice = await CreateUserAsync("alice");
        await CreateUserAsync("bob");
        await PatchDisplayNameAsync(alice, "Alice Example");

        var entries = await WithJimAsync(jim => jim.Scim.GetJournalAsync(_systemId, alice, afterSequence: null, upToSequence: null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Select(e => e.Operation), Is.EqualTo(new[] { ScimStoreOperation.Created, ScimStoreOperation.Patched }));
            Assert.That(entries.All(e => e.ResourceId == alice), Is.True);
            Assert.That(entries[0].ChangeSequence, Is.LessThan(entries[1].ChangeSequence));
            Assert.That(entries[1].ChangedAttributes, Does.Contain("displayName"));
            Assert.That(entries[0].ApiKeyName, Is.EqualTo(_administrator.Name));
        }
    }

    [Test]
    public async Task GetJournalAsync_SequenceRange_AfterIsExclusiveUpToIsInclusiveAsync()
    {
        var alice = await CreateUserAsync("alice");
        await PatchDisplayNameAsync(alice, "One");
        await PatchDisplayNameAsync(alice, "Two");
        var all = await WithJimAsync(jim => jim.Scim.GetJournalAsync(_systemId, alice, null, null));

        var middle = await WithJimAsync(jim => jim.Scim.GetJournalAsync(_systemId, alice, all[0].ChangeSequence, all[1].ChangeSequence));

        Assert.That(middle.Select(e => e.ChangeSequence), Is.EqualTo(new[] { all[1].ChangeSequence }));
    }

    [Test]
    public async Task GetJournalAsync_FlagOff_RefusedAsync()
    {
        await SetFlagAsync(false);

        Assert.That(() => WithJimAsync(jim => jim.Scim.GetJournalAsync(_systemId, Guid.NewGuid(), null, null)), Throws.InstanceOf<FeatureDisabledException>());
    }

    [Test]
    public async Task GetRequestsBehindChangeAsync_TheResourcesWritesWithinTheImportsRangeAsync()
    {
        var alice = await CreateUserAsync("alice");
        var before = await StateAsync();
        await PatchDisplayNameAsync(alice, "Alice One");
        await CreateUserAsync("bob");
        await PatchDisplayNameAsync(alice, "Alice Two");
        var reached = await StateAsync();
        await PatchDisplayNameAsync(alice, "After the import");
        var changeId = await RecordImportChangeAsync(alice, from: before, to: reached);

        var behind = await WithJimAsync(jim => jim.Scim.GetRequestsBehindChangeAsync(changeId));

        Assert.That(behind, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(behind!.ConnectedSystemId, Is.EqualTo(_systemId));
            Assert.That(behind.ResourceId, Is.EqualTo(alice));
            Assert.That((behind.AfterSequence, behind.UpToSequence), Is.EqualTo((before, reached)));
            Assert.That(behind.Entries, Has.Count.EqualTo(2), "the two patches the import read, not the create before it or the patch after");
            Assert.That(behind.Entries.All(e => e.Operation == ScimStoreOperation.Patched), Is.True);
        }
    }

    [Test]
    public async Task GetRequestsBehindChangeAsync_FirstImport_ReadsFromTheStartAsync()
    {
        var alice = await CreateUserAsync("alice");
        var reached = await StateAsync();
        var changeId = await RecordImportChangeAsync(alice, from: null, to: reached);

        var behind = await WithJimAsync(jim => jim.Scim.GetRequestsBehindChangeAsync(changeId));

        Assert.That(behind!.Entries.Select(e => e.Operation), Is.EqualTo(new[] { ScimStoreOperation.Created }));
    }

    [Test]
    public async Task GetRequestsBehindChangeAsync_ImportReachedNoWatermark_NullAsync()
    {
        var alice = await CreateUserAsync("alice");
        var changeId = await RecordImportChangeAsync(alice, from: null, to: null);

        Assert.That(await WithJimAsync(jim => jim.Scim.GetRequestsBehindChangeAsync(changeId)), Is.Null);
    }

    [Test]
    public async Task GetRequestsBehindChangeAsync_UnknownChange_NullAsync()
    {
        Assert.That(await WithJimAsync(jim => jim.Scim.GetRequestsBehindChangeAsync(Guid.NewGuid())), Is.Null);
    }

    #region helpers

    private async Task<Guid> CreateUserAsync(string userName)
    {
        var body = new JsonObject { ["schemas"] = new JsonArray(ScimUrns.User), ["userName"] = userName };
        var result = await WithJimAsync(jim => jim.Scim.CreateAsync(Context(), "/Users", body));
        Assert.That(result.StatusCode, Is.EqualTo(201), result.Error?.Detail);
        return Guid.Parse(result.Resource!["id"]!.GetValue<string>());
    }

    private async Task PatchDisplayNameAsync(Guid id, string displayName)
    {
        var operations = new List<JIM.Scim.Messages.ScimPatchOperation>
        {
            new() { Op = "replace", Path = "displayName", Value = JsonValue.Create(displayName) }
        };
        var result = await WithJimAsync(jim => jim.Scim.PatchAsync(Context(), "/Users", ScimReferenceEntries.Format(id), operations, ifMatch: null));
        Assert.That(result.StatusCode, Is.EqualTo(200), result.Error?.Detail);
    }

    private async Task<long> StateAsync() => (await WithJimAsync(jim => jim.Scim.GetStoreStateAsync(_systemId)))!.LastChangeSequence;

    /// <summary>
    /// Records what an import leaves behind for a change: its Activity with the watermarks it read from and reached,
    /// the execution item naming the resource, and the Connected System Object change.
    /// </summary>
    private async Task<Guid> RecordImportChangeAsync(Guid resourceId, long? from, long? to)
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            TargetName = "Delta Import",
            ConnectedSystemId = _systemId,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System",
            ImportWatermarkFrom = from.HasValue ? $"{{\"changeSequence\":{from.Value}}}" : null,
            ImportWatermarkTo = to.HasValue ? $"{{\"changeSequence\":{to.Value}}}" : null
        };
        ctx.Activities.Add(activity);
        var item = new ActivityRunProfileExecutionItem
        {
            Activity = activity,
            ObjectChangeType = ObjectChangeType.Updated,
            ExternalIdSnapshot = ScimReferenceEntries.Format(resourceId)
        };
        ctx.ActivityRunProfileExecutionItems.Add(item);
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = _systemId,
            ActivityRunProfileExecutionItem = item,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Updated,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System"
        };
        ctx.ConnectedSystemObjectChanges.Add(change);
        await ctx.SaveChangesAsync();
        return change.Id;
    }

    private ScimRequestContext Context() => new(_systemId, "https://jim.example/scim/v2", _administrator.Id, _administrator.Name, "203.0.113.9");

    private Task SetFlagAsync(bool enabled) =>
        WithJimAsync(jim => jim.FeatureFlags.SetFeatureFlagAsync(FeatureFlagCatalogue.ScimServiceProvider.Key, enabled, _administrator, allowInDevelopment: true));

    private async Task<T> WithJimAsync<T>(Func<JimApplication, Task<T>> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        return await call(jim);
    }

    private async Task WithJimAsync(Func<JimApplication, Task> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        await call(jim);
    }

    #endregion
}
