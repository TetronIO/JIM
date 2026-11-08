// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using Activity = JIM.Models.Activities.Activity;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Real-PostgreSQL Full Synchronisations proving that a joined Metaverse Object the run changed nothing about is neither
/// written nor has its values read back (#2831). The page flush's batch update re-reads every value of every object it is
/// given to work out what to insert and delete, so an object queued without cause cost a read of all its values (every
/// member of a large group) and a rewrite of its row. These tests read each object's row version (<c>xmin</c>) and stored
/// state back on a connection of their own, and listen to the statements the run sends, so they observe the write path
/// itself rather than what the run holds in memory (<c>UnchangedMetaverseObjectWriteWorkflowTests</c> is the fast test).
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class UnchangedMetaverseObjectWriteDatabaseTests
{
    /// <summary>
    /// The batch update's read of the stored values of the objects it is given (LoadExistingAttributeValuesAsync), whitespace
    /// collapsed. The page load reads the same table with every column, so the column list is what tells the two apart.
    /// </summary>
    private const string ValueReadStatementMarker =
        "SELECT \"Id\", \"MetaverseObjectId\", \"ContributedBySystemId\", \"ContributedBySyncRuleId\", \"ContributedByKind\", " +
        "\"ContributedByClaimType\" FROM \"MetaverseObjectAttributeValues\" WHERE \"MetaverseObjectId\" = ANY";

    private static readonly Guid AliceCsoId = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid BobCsoId = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private string _connectionString = null!;
    private int _systemId;
    private int _typeId;
    private int _externalIdAttributeId;
    private int _displayNameAttributeId;

    private ActivityListener _listener = null!;
    private volatile bool _recording;
    private readonly ConcurrentQueue<(string Name, IReadOnlyList<KeyValuePair<string, object?>> Tags)> _spans = new();

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs); a synchronisation run must too.
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL unchanged Metaverse Object write tests.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();

        // JIM's own spans, and Npgsql's span per command (its statement text in a tag), which covers the raw commands the
        // batch update sends on the EF connection as well as EF's own.
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("JIM.", StringComparison.Ordinal) || source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span =>
            {
                if (_recording)
                    _spans.Enqueue((span.DisplayName, span.TagObjects.ToList()));
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _listener?.Dispose();

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task FullSync_OneOfTwoObjectsChanged_WritesAndReadsTheValuesOfOnlyTheChangedObjectAsync()
    {
        await SeedConfigurationAsync();
        await CreateHrObjectAsync(AliceCsoId, "E1", "Alice Unchanged");
        await CreateHrObjectAsync(BobCsoId, "E2", "Bob Before");
        await RunFullSynchronisationAsync();
        var aliceMvoId = await ReadMvoIdAsync(AliceCsoId);
        var bobMvoId = await ReadMvoIdAsync(BobCsoId);
        var aliceBefore = await ReadStoredStateAsync(aliceMvoId);
        var bobBefore = await ReadStoredStateAsync(bobMvoId);

        await ImportDisplayNameChangeAsync(BobCsoId, "Bob After");
        var (activityId, spans) = await RecordAsync(RunFullSynchronisationAsync);

        var aliceAfter = await ReadStoredStateAsync(aliceMvoId);
        var bobAfter = await ReadStoredStateAsync(bobMvoId);
        await using var read = NewContext();
        var items = await read.ActivityRunProfileExecutionItems.AsNoTracking().Where(i => i.ActivityId == activityId).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(aliceAfter, Is.EqualTo(aliceBefore), "the unchanged object's row, row version, values and provenance are untouched");
            Assert.That(bobAfter.RowVersion, Is.Not.EqualTo(bobBefore.RowVersion), "the changed object's row is written");
            Assert.That(bobAfter.CachedDisplayName, Is.EqualTo("Bob After"));
            Assert.That(bobAfter.Values, Does.Contain("StringValue = Bob After").And.Not.Contain("Bob Before"), "the changed value is replaced");
            Assert.That(UpdatedMvoCounts(spans), Is.EqualTo(new[] { 1 }), "one batch update, of the changed object alone");
            Assert.That(ValueReadCount(spans), Is.EqualTo(1), "one value read, made by that batch update");
            Assert.That(items.Select(i => (i.ConnectedSystemObjectId, i.ObjectChangeType)),
                Is.EqualTo(new[] { ((Guid?)BobCsoId, ObjectChangeType.AttributeFlow) }), "one execution item, for the changed object");
            Assert.That(await read.MetaverseObjectChanges.AsNoTracking().CountAsync(c => c.MetaverseObjectId == aliceMvoId), Is.EqualTo(1),
                "the unchanged object keeps only its projection's change record");
            Assert.That(await read.MetaverseObjectChanges.AsNoTracking().CountAsync(c => c.MetaverseObjectId == bobMvoId), Is.EqualTo(2),
                "the changed object gains a change record for the Attribute Flow");
        }
    }

    [Test]
    public async Task FullSync_NothingChanged_WritesNoMetaverseObjectAndReadsNoValuesAsync()
    {
        await SeedConfigurationAsync();
        await CreateHrObjectAsync(AliceCsoId, "E1", "Alice");
        await CreateHrObjectAsync(BobCsoId, "E2", "Bob");
        await RunFullSynchronisationAsync();
        var aliceMvoId = await ReadMvoIdAsync(AliceCsoId);
        var bobMvoId = await ReadMvoIdAsync(BobCsoId);
        var aliceBefore = await ReadStoredStateAsync(aliceMvoId);
        var bobBefore = await ReadStoredStateAsync(bobMvoId);

        var (activityId, spans) = await RecordAsync(RunFullSynchronisationAsync);

        await using var read = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadStoredStateAsync(aliceMvoId), Is.EqualTo(aliceBefore), "neither object is written");
            Assert.That(await ReadStoredStateAsync(bobMvoId), Is.EqualTo(bobBefore));
            Assert.That(UpdatedMvoCounts(spans), Is.Empty, "no batch update runs");
            Assert.That(ValueReadCount(spans), Is.Zero, "and no stored values are read back for one");
            Assert.That(await read.ActivityRunProfileExecutionItems.AsNoTracking().CountAsync(i => i.ActivityId == activityId), Is.Zero,
                "the run records nothing, as before");
            Assert.That(await read.MetaverseObjectChanges.AsNoTracking().CountAsync(), Is.EqualTo(2), "only the two projections' change records");
        }
    }

    #region Harness

    private static int[] UpdatedMvoCounts(IEnumerable<(string Name, IReadOnlyList<KeyValuePair<string, object?>> Tags)> spans) =>
        spans.Where(s => s.Name == "UpdateMetaverseObjects")
            .Select(s => Convert.ToInt32(s.Tags.Single(t => t.Key == "count").Value))
            .ToArray();

    private static int ValueReadCount(IEnumerable<(string Name, IReadOnlyList<KeyValuePair<string, object?>> Tags)> spans) =>
        spans.Count(s => s.Tags.Any(t => t.Value is string text && Regex.Replace(text, @"\s+", " ").Contains(ValueReadStatementMarker, StringComparison.Ordinal)));

    private async Task<(Guid ActivityId, List<(string Name, IReadOnlyList<KeyValuePair<string, object?>> Tags)> Spans)> RecordAsync(Func<Task<Guid>> run)
    {
        _spans.Clear();
        _recording = true;
        Guid activityId;
        try
        {
            activityId = await run();
        }
        finally
        {
            _recording = false;
        }

        var spans = _spans.ToList();
        Assert.That(spans.Any(s => s.Tags.Any(t => t.Value is string text && text.Contains("SELECT", StringComparison.Ordinal))), Is.True,
            "precondition: the listener sees the statements the run sends");
        return (activityId, spans);
    }

    /// <summary>Everything the batch update writes for a Metaverse Object, as stored, with the row version.</summary>
    private sealed record StoredState(
        uint RowVersion, DateTime? LastUpdated, int TypeId, MetaverseObjectStatus Status, MetaverseObjectOrigin Origin, string? CachedDisplayName,
        DateTime? LastConnectorDisconnectedDate, ActivityInitiatorType DeletionInitiatedByType, Guid? DeletionInitiatedById,
        string? DeletionInitiatedByName, int? DeletionTriggeredBySystemId, string? DeletionTriggeredBySystemName,
        string? DeletionPolicySnapshotJson, Guid? DeletionScheduledByRunProfileExecutionItemId, string Values);

    private async Task<StoredState> ReadStoredStateAsync(Guid mvoId)
    {
        await using var ctx = NewContext();
        var mvo = await ctx.MetaverseObjects.AsNoTracking().Include(m => m.Type).SingleAsync(m => m.Id == mvoId);
        var values = await ctx.MetaverseObjectAttributeValues.AsNoTracking()
            .Where(av => av.MetaverseObject.Id == mvoId)
            .OrderBy(av => av.Id)
            .Select(av => new { av.Id, av.AttributeId, av.StringValue, av.ContributedBySystemId, av.ContributedBySyncRuleId, av.ContributedByKind, av.ContributedByClaimType })
            .ToListAsync();
        return new StoredState(mvo.xmin, mvo.LastUpdated, mvo.Type.Id, mvo.Status, mvo.Origin, mvo.CachedDisplayName,
            mvo.LastConnectorDisconnectedDate, mvo.DeletionInitiatedByType, mvo.DeletionInitiatedById, mvo.DeletionInitiatedByName,
            mvo.DeletionTriggeredBySystemId, mvo.DeletionTriggeredBySystemName, mvo.DeletionPolicySnapshotJson,
            mvo.DeletionScheduledByRunProfileExecutionItemId, string.Join(" | ", values));
    }

    private async Task<Guid> ReadMvoIdAsync(Guid csoId)
    {
        await using var ctx = NewContext();
        var mvoId = await ctx.ConnectedSystemObjects.AsNoTracking().Where(c => c.Id == csoId).Select(c => c.MetaverseObjectId).SingleAsync();
        Assert.That(mvoId, Is.Not.Null, "precondition: the first Full Synchronisation projected the object");
        return mvoId!.Value;
    }

    /// <summary>An HR system whose import rule projects each object and flows its display name, with Metaverse Object change tracking on.</summary>
    private async Task SeedConfigurationAsync()
    {
        await using var seed = NewContext();

        foreach (var (key, valueType, value) in new[]
                 {
                     (Constants.SettingKeys.SyncPageSize, ServiceSettingValueType.Integer, "100"),
                     ("ChangeTracking.CsoChanges.Enabled", ServiceSettingValueType.Boolean, "false"),
                     ("ChangeTracking.MvoChanges.Enabled", ServiceSettingValueType.Boolean, "true")
                 })
        {
            seed.ServiceSettingItems.Add(new ServiceSetting
            {
                Key = key, DisplayName = key, Category = ServiceSettingCategory.Synchronisation,
                ValueType = valueType, DefaultValue = value, Value = value
            });
        }

        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
        var system = new ConnectedSystem
        {
            Name = "HR", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };
        var type = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
        var externalId = TextAttribute(type, "employeeId", isExternalId: true);
        var displayName = TextAttribute(type, "displayName");
        type.Attributes.Add(externalId);
        type.Attributes.Add(displayName);

        var mvType = new MetaverseObjectType
        {
            Name = "Person", PluralName = "People", DeletionRule = MetaverseObjectDeletionRule.Manual
        };
        var mvDisplayName = new MetaverseAttribute
        {
            Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType]
        };

        seed.AddRange(connectorDefinition, system, type, mvType, mvDisplayName);
        await seed.SaveChangesAsync();
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = system.Id, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation });

        var rule = new SyncRule
        {
            Name = "HR Import", ConnectedSystem = system, Direction = SyncRuleDirection.Import, Enabled = true,
            ConnectedSystemObjectType = type, MetaverseObjectType = mvType, ProjectToMetaverse = true
        };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule, TargetMetaverseAttribute = mvDisplayName,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = displayName } }
        });
        seed.SyncRules.Add(rule);
        await seed.SaveChangesAsync();

        _systemId = system.Id;
        _typeId = type.Id;
        _externalIdAttributeId = externalId.Id;
        _displayNameAttributeId = displayName.Id;
    }

    private static ConnectedSystemObjectTypeAttribute TextAttribute(ConnectedSystemObjectType type, string name, bool isExternalId = false) => new()
    {
        Name = name, ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = isExternalId
    };

    /// <summary>An HR object, created through the import's own create writer.</summary>
    private async Task CreateHrObjectAsync(Guid csoId, string employeeId, string displayName)
    {
        await using var ctx = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = csoId, ConnectedSystemId = _systemId, TypeId = _typeId, ExternalIdAttributeId = _externalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow.AddHours(-2)
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _externalIdAttributeId, StringValue = employeeId });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _displayNameAttributeId, StringValue = displayName });
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync([cso]);
    }

    /// <summary>An import committing a new display name for an HR object, through the import's own update writer.</summary>
    private async Task ImportDisplayNameChangeAsync(Guid csoId, string displayName)
    {
        await using var ctx = NewContext();
        var cso = await ctx.ConnectedSystemObjects.AsNoTracking().Include(c => c.AttributeValues).SingleAsync(c => c.Id == csoId);
        var previous = cso.AttributeValues.Single(av => av.AttributeId == _displayNameAttributeId);
        var replacement = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _displayNameAttributeId, StringValue = displayName };
        cso.LastUpdated = DateTime.UtcNow;
        await new PostgresDataRepository(ctx).Sync.UpdateConnectedSystemObjectsAsync([cso], [(cso.Id, replacement)], [previous.Id]);
    }

    /// <summary>Runs one Full Synchronisation of the HR system on its own tracking context, as the Worker does.</summary>
    private async Task<Guid> RunFullSynchronisationAsync()
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_systemId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == ConnectedSystemRunType.FullSynchronisation);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = ConnectedSystemRunType.FullSynchronisation
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
            system, runProfile, activity, new CancellationTokenSource()).PerformFullSyncAsync();

        await using var read = NewContext();
        var errors = await read.ActivityRunProfileExecutionItems.AsNoTracking()
            .Where(i => i.ActivityId == activity.Id && i.ErrorType != null && i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToListAsync();
        Assert.That(errors, Is.Empty, $"the run reported errors: {string.Join("; ", errors.Select(e => e.ErrorMessage))}");
        return activity.Id;
    }

    #endregion
}
