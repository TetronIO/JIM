// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Docs;
using JIM.Worker.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Characterises the history a Full and a Delta Synchronisation record (#2874): every execution item, outcome, change
/// record, causal edge, match candidate and stat counter, and the Activity's totals, rendered without ids or times by
/// <see cref="SynchronisationHistorySnapshot"/> and compared with a baseline captured before the history writes were
/// restructured. Moving how and when history is written must not change what is written.
/// </summary>
/// <remarks>
/// <para>
/// Pages hold three objects, so one run spans several, and the objects are chosen to reach every history writer:
/// projections and a join; Attribute Flow; provisioning, with a reference that resolves on the page and so is written
/// as a reference snapshot; a reference to an object on a later page, resolved by the cross-page step and merged into
/// the item and change record its page wrote; an object that left its Connected System, whose Metaverse Object is
/// deleted at once, which writes the departed object's item and deletion record in the deletion's own transaction,
/// deprovisions its account, and recalls the reference another account held to it (a recall item with causal edges);
/// an ambiguous match, which records its candidates; and an object flagged for export scope review, which the review
/// provisions.
/// </para>
/// <para>
/// To capture a new baseline, run with <c>JIM_CAPTURE_SYNC_HISTORY_BASELINE=1</c>; it overwrites the baseline files in
/// the source tree. A baseline captured from the code under test proves nothing, so capture only from code whose history
/// is the reference, and say so in the change that commits it. Opt-in via the <c>JIM_TEST_RESET_*</c> environment
/// variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SynchronisationHistoryEquivalenceDatabaseTests
{
    private const string CaptureVariable = "JIM_CAPTURE_SYNC_HISTORY_BASELINE";

    // Connected System Objects are paged in id order, so these ids decide the pages: the HR objects sort by employee id.
    private static Guid HrId(int employee) => Guid.Parse($"00000000-0000-0000-0000-{employee:D12}");
    private static Guid DirectoryId(int employee) => Guid.Parse($"00000000-0000-0000-0001-{employee:D12}");

    private string _connectionString = null!;
    private int _hrId;
    private int _hrTypeId;
    private int _hrEmployeeIdAttributeId;
    private int _hrDisplayNameAttributeId;
    private int _hrManagerAttributeId;
    private int _directoryId;
    private int _directoryTypeId;
    private int _directoryAccountIdAttributeId;
    private int _directoryCnAttributeId;
    private int _directoryManagerAttributeId;
    private int _mvTypeId;
    private int _mvDisplayNameAttributeId;
    private int _mvEmployeeIdAttributeId;
    private int _mvManagerAttributeId;

    private readonly Dictionary<Guid, string> _objectLabels = new();
    private readonly Dictionary<Guid, string> _activityStatuses = new();

    // How the runs write their history; null leaves it to the run, which overlaps on PostgreSQL.
    private SynchronisationHistoryWriteMode? _historyWriteMode;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL synchronisation history equivalence tests.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        _objectLabels.Clear();
        _activityStatuses.Clear();
        _historyWriteMode = null;
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    /// <summary>
    /// The history a run records over a mixed set of pages is the history recorded before the history writes were
    /// restructured, item for item and row for row, whether each page's history is written before the run goes on or
    /// while the next page loads and is processed (#2874): both modes match the one baseline, and so each other.
    /// </summary>
    [TestCase(ConnectedSystemRunType.FullSynchronisation, SynchronisationHistoryWriteMode.Inline)]
    [TestCase(ConnectedSystemRunType.FullSynchronisation, SynchronisationHistoryWriteMode.Overlapped)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation, SynchronisationHistoryWriteMode.Inline)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation, SynchronisationHistoryWriteMode.Overlapped)]
    public async Task Synchronisation_MixedPages_RecordsTheBaselineHistoryAsync(ConnectedSystemRunType runType, SynchronisationHistoryWriteMode mode)
    {
        _historyWriteMode = mode;
        await ArrangeAsync();

        await RunAsync(runType);

        var snapshot = await SnapshotAsync();
        var baselinePath = DocsCoverageSource.FullPath($"test/JIM.Worker.Tests/Workflows/Baselines/SynchronisationHistory.{runType}.txt");
        if (Environment.GetEnvironmentVariable(CaptureVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            await File.WriteAllTextAsync(baselinePath, snapshot);
            Assert.Pass($"Captured the baseline at {baselinePath}.");
        }

        Assert.That(File.Exists(baselinePath), Is.True, $"no baseline at {baselinePath}; capture one with {CaptureVariable}=1");
        var baseline = await File.ReadAllTextAsync(baselinePath);
        if (snapshot != baseline)
        {
            var actualPath = Path.Join(TestContext.CurrentContext.WorkDirectory, $"SynchronisationHistory.{runType}.actual.txt");
            await File.WriteAllTextAsync(actualPath, snapshot);
            Assert.Fail($"The history differs from the baseline at its first difference:{Environment.NewLine}{FirstDifference(baseline, snapshot)}" +
                        $"{Environment.NewLine}Whole history written to {actualPath}.");
        }
    }

    /// <summary>
    /// A history snapshot is a pure function of what was recorded: the same run twice renders the same text, so a
    /// difference against the baseline is a difference in the history, never in the ids or times a run was given.
    /// </summary>
    [Test]
    public async Task Snapshot_SameRunTwice_RendersIdenticallyAsync()
    {
        await ArrangeAsync();
        await RunAsync(ConnectedSystemRunType.FullSynchronisation);
        var first = await SnapshotAsync();

        await PostgresTestDatabase.ResetAsync(_connectionString);
        _objectLabels.Clear();
        _activityStatuses.Clear();
        await ArrangeAsync();
        await RunAsync(ConnectedSystemRunType.FullSynchronisation);
        var second = await SnapshotAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.EqualTo(first));
            Assert.That(first, Does.StartWith("Unlabelled ids: 0"), "every id in the history is one the fixture can name");
        }
    }

    #region Arrangement

    /// <summary>
    /// The state before the run under test: four people projected by a first Full Synchronisation, three of them with
    /// accounts in the Directory that name their managers, three identities with no HR object (one sought by a join, two
    /// that will collide), one flagged for export scope review, and the Directory's provisioning export rule. Then the
    /// changes the run under test processes: one person leaves, one is renamed, and six arrive.
    /// </summary>
    private async Task ArrangeAsync()
    {
        await SeedConfigurationAsync();

        // Mia manages Lee, who manages Rob; Fay is on a page of her own.
        await CreateHrPeopleAsync(created: DateTime.UtcNow.AddHours(-2),
            (10, "Mia Manager", null), (11, "Lee Leaver", 10), (12, "Rob Report", 11), (13, "Fay Flow", null));
        await RunAsync(ConnectedSystemRunType.FullSynchronisation, expectNoErrors: true);

        var mvoIds = await ReadMetaverseObjectIdsByNameAsync();
        await SeedDirectoryAccountsAsync(mvoIds);
        await SeedMetaverseObjectsAsync();
        await AddDirectoryExportRuleAsync();

        // The changes the run under test processes.
        await MarkObsoleteAsync(11);
        await RenameAsync(13, "Fay Flowed");
        await CreateHrPeopleAsync(created: DateTime.UtcNow,
            // Ned's manager, Pat, is on the next page: the cross-page step resolves it.
            (1, "Ned New", 5),
            // Kim's manager, Tom, is on the same page and new too: provisioning records the reference as a snapshot.
            (2, "Kim Kid", 3),
            (3, "Tom Team", 10),
            (5, "Pat Peer", null),
            // Jo joins the identity seeded for her; Amy matches two.
            (20, "Jo Joinee", null),
            (30, "Amy Ambiguous", null));
    }

    /// <summary>
    /// HR projects every person, flows their name, employee id and manager, and joins on employee id; a person leaving
    /// HR deletes their identity at once. The Directory's export rule is added later. Pages of three; Connected System
    /// Object and Metaverse Object change tracking on, outcomes in detail.
    /// </summary>
    private async Task SeedConfigurationAsync()
    {
        await using var seed = NewContext();

        foreach (var (key, valueType, value) in new[]
                 {
                     (Constants.SettingKeys.SyncPageSize, ServiceSettingValueType.Integer, "3"),
                     (Constants.SettingKeys.ChangeTrackingCsoChangesEnabled, ServiceSettingValueType.Boolean, "true"),
                     (Constants.SettingKeys.ChangeTrackingMvoChangesEnabled, ServiceSettingValueType.Boolean, "true")
                 })
        {
            seed.ServiceSettingItems.Add(new ServiceSetting
            {
                Key = key, DisplayName = key, Category = ServiceSettingCategory.Synchronisation,
                ValueType = valueType, DefaultValue = value, Value = value
            });
        }

        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
        var hr = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule };
        var hrType = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = hr, Selected = true };
        var hrEmployeeId = Attribute(hrType, "employeeId", AttributeDataType.Text, isExternalId: true);
        var hrDisplayName = Attribute(hrType, "displayName", AttributeDataType.Text);
        var hrManager = Attribute(hrType, "manager", AttributeDataType.Reference);
        hrType.Attributes.AddRange([hrEmployeeId, hrDisplayName, hrManager]);

        var directory = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule };
        var directoryType = new ConnectedSystemObjectType { Name = "account", ConnectedSystem = directory, Selected = true };
        var directoryAccountId = Attribute(directoryType, "accountId", AttributeDataType.Text, isExternalId: true);
        var directoryCn = Attribute(directoryType, "cn", AttributeDataType.Text);
        var directoryManager = Attribute(directoryType, "manager", AttributeDataType.Reference);
        directoryType.Attributes.AddRange([directoryAccountId, directoryCn, directoryManager]);

        var mvType = new MetaverseObjectType
        {
            Name = "Person", PluralName = "People", DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected
        };
        var mvDisplayName = MetaverseAttribute(mvType, "Display Name", AttributeDataType.Text);
        var mvEmployeeId = MetaverseAttribute(mvType, "Employee Id", AttributeDataType.Text);
        var mvManager = MetaverseAttribute(mvType, "Manager", AttributeDataType.Reference);

        seed.AddRange(connectorDefinition, hr, hrType, directory, directoryType, mvType, mvDisplayName, mvEmployeeId, mvManager);
        await seed.SaveChangesAsync();
        mvType.DeletionTriggerConnectedSystemIds = [hr.Id];
        foreach (var system in new[] { hr, directory })
        {
            seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
                { ConnectedSystemId = system.Id, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation });
            seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
                { ConnectedSystemId = system.Id, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation });
        }

        var import = new SyncRule
        {
            Name = "HR Import", ConnectedSystem = hr, Direction = SyncRuleDirection.Import, Enabled = true,
            ConnectedSystemObjectType = hrType, MetaverseObjectType = mvType, ProjectToMetaverse = true,
            InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect
        };
        foreach (var (source, target) in new[] { (hrDisplayName, mvDisplayName), (hrEmployeeId, mvEmployeeId), (hrManager, mvManager) })
        {
            import.AttributeFlowRules.Add(new SyncRuleMapping
            {
                SyncRule = import, TargetMetaverseAttribute = target,
                Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source } }
            });
        }

        import.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            Order = 0, CaseSensitive = true, SyncRule = import, MetaverseObjectType = mvType, TargetMetaverseAttribute = mvEmployeeId,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = hrEmployeeId }]
        });
        seed.SyncRules.Add(import);
        await seed.SaveChangesAsync();

        _hrId = hr.Id;
        _hrTypeId = hrType.Id;
        _hrEmployeeIdAttributeId = hrEmployeeId.Id;
        _hrDisplayNameAttributeId = hrDisplayName.Id;
        _hrManagerAttributeId = hrManager.Id;
        _directoryId = directory.Id;
        _directoryTypeId = directoryType.Id;
        _directoryAccountIdAttributeId = directoryAccountId.Id;
        _directoryCnAttributeId = directoryCn.Id;
        _directoryManagerAttributeId = directoryManager.Id;
        _mvTypeId = mvType.Id;
        _mvDisplayNameAttributeId = mvDisplayName.Id;
        _mvEmployeeIdAttributeId = mvEmployeeId.Id;
        _mvManagerAttributeId = mvManager.Id;
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name, AttributeDataType dataType, bool isExternalId = false) => new()
    {
        Name = name, ConnectedSystemObjectType = type, Type = dataType,
        AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = isExternalId
    };

    private static MetaverseAttribute MetaverseAttribute(MetaverseObjectType type, string name, AttributeDataType dataType) => new()
    {
        Name = name, Type = dataType, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = [type]
    };

    /// <summary>
    /// The Directory's export rule: provisions every person, flowing their name, employee id (the account id) and
    /// manager, and deletes the account of a deleted person.
    /// </summary>
    private async Task AddDirectoryExportRuleAsync()
    {
        await using var seed = NewContext();
        var rule = new SyncRule
        {
            Name = "Directory Export", Direction = SyncRuleDirection.Export, Enabled = true,
            ConnectedSystemId = _directoryId, ConnectedSystemObjectTypeId = _directoryTypeId, MetaverseObjectTypeId = _mvTypeId,
            ProvisionToConnectedSystem = true, OutboundDeprovisionAction = OutboundDeprovisionAction.Delete
        };
        foreach (var (source, target) in new[]
                 {
                     (_mvDisplayNameAttributeId, _directoryCnAttributeId),
                     (_mvEmployeeIdAttributeId, _directoryAccountIdAttributeId),
                     (_mvManagerAttributeId, _directoryManagerAttributeId)
                 })
        {
            rule.AttributeFlowRules.Add(new SyncRuleMapping
            {
                SyncRule = rule, TargetConnectedSystemAttributeId = target,
                Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttributeId = source } }
            });
        }

        seed.SyncRules.Add(rule);
        await seed.SaveChangesAsync();
    }

    /// <summary>People in HR, created through the import's own create writer, each with the manager named by number.</summary>
    private async Task CreateHrPeopleAsync(DateTime created, params (int Employee, string Name, int? Manager)[] people)
    {
        await using var ctx = NewContext();
        var objects = people.Select(person =>
        {
            var cso = new ConnectedSystemObject
            {
                Id = HrId(person.Employee), ConnectedSystemId = _hrId, TypeId = _hrTypeId, ExternalIdAttributeId = _hrEmployeeIdAttributeId,
                Status = ConnectedSystemObjectStatus.Normal, Created = created
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _hrEmployeeIdAttributeId, StringValue = $"E{person.Employee:D2}" });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _hrDisplayNameAttributeId, StringValue = person.Name });
            if (person.Manager is { } manager)
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _hrManagerAttributeId, ReferenceValueId = HrId(manager) });
            _objectLabels[cso.Id] = $"HR:E{person.Employee:D2}";
            return cso;
        }).ToList();
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync(objects);
    }

    /// <summary>
    /// Directory accounts for Mia, Lee and Rob, provisioned earlier and since confirmed, each naming its owner's
    /// manager's account: Rob's names Lee's, so Lee's departure recalls it.
    /// </summary>
    private async Task SeedDirectoryAccountsAsync(IReadOnlyDictionary<string, Guid> mvoIds)
    {
        await using var ctx = NewContext();
        var accounts = new[] { (10, "Mia Manager", (int?)null), (11, "Lee Leaver", 10), (12, "Rob Report", 11) }.Select(account =>
        {
            var cso = new ConnectedSystemObject
            {
                Id = DirectoryId(account.Item1), ConnectedSystemId = _directoryId, TypeId = _directoryTypeId,
                ExternalIdAttributeId = _directoryAccountIdAttributeId, Status = ConnectedSystemObjectStatus.Normal,
                Created = DateTime.UtcNow.AddHours(-1), MetaverseObjectId = mvoIds[account.Item2]
            };
            cso.RecordJoin(ConnectedSystemObjectJoinMethod.Provisioning, null, DateTime.UtcNow.AddHours(-1));
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _directoryAccountIdAttributeId, StringValue = $"E{account.Item1:D2}" });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _directoryCnAttributeId, StringValue = account.Item2 });
            if (account.Item3 is { } manager)
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _directoryManagerAttributeId, ReferenceValueId = DirectoryId(manager) });
            _objectLabels[cso.Id] = $"Directory:E{account.Item1:D2}";
            return cso;
        }).ToList();
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync(accounts);
    }

    /// <summary>
    /// Identities with no HR object: Jo, whom her HR object will join; two Amys holding the same employee id; and Rita,
    /// flagged for export scope review.
    /// </summary>
    private async Task SeedMetaverseObjectsAsync()
    {
        await using var ctx = NewContext();
        var type = await ctx.MetaverseObjectTypes.AsTracking().SingleAsync(t => t.Id == _mvTypeId);
        foreach (var (name, employeeId, flagged) in new[]
                 {
                     ("Jo Joinee", "E20", false), ("Amy Ambiguous", "E30", false), ("Amy Ambiguous (Contractor)", "E30", false),
                     ("Rita Review", "E90", true)
                 })
        {
            var mvo = new MetaverseObject { Type = type, ScopeReviewPending = flagged };
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = mvo, AttributeId = _mvDisplayNameAttributeId, StringValue = name });
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = mvo, AttributeId = _mvEmployeeIdAttributeId, StringValue = employeeId });
            ctx.MetaverseObjects.Add(mvo);
        }

        await ctx.SaveChangesAsync();
    }

    private async Task MarkObsoleteAsync(int employee)
    {
        await ExecuteAsync(
            """
            UPDATE "ConnectedSystemObjects" SET "Status" = @obsolete, "SynchronisationPending" = true, "LastUpdated" = now()
            WHERE "Id" = @id
            """,
            new NpgsqlParameter("obsolete", NpgsqlDbType.Integer) { Value = (int)ConnectedSystemObjectStatus.Obsolete },
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = HrId(employee) });
    }

    private async Task RenameAsync(int employee, string name)
    {
        await ExecuteAsync(
            """
            UPDATE "ConnectedSystemObjectAttributeValues" SET "StringValue" = @name
            WHERE "ConnectedSystemObjectId" = @id AND "AttributeId" = @attributeId;
            UPDATE "ConnectedSystemObjects" SET "SynchronisationPending" = true, "LastUpdated" = now() WHERE "Id" = @id;
            """,
            new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name },
            new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = HrId(employee) },
            new NpgsqlParameter("attributeId", NpgsqlDbType.Integer) { Value = _hrDisplayNameAttributeId });
    }

    private async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Each Metaverse Object's id by its display name.</summary>
    private async Task<Dictionary<string, Guid>> ReadMetaverseObjectIdsByNameAsync()
    {
        await using var ctx = NewContext();
        var rows = await ctx.MetaverseObjectAttributeValues.AsNoTracking()
            .Where(av => av.AttributeId == _mvDisplayNameAttributeId)
            .Select(av => new { Name = av.StringValue!, MetaverseObjectId = EF.Property<Guid>(av, "MetaverseObjectId") })
            .ToListAsync();
        return rows.ToDictionary(r => r.Name, r => r.MetaverseObjectId);
    }

    /// <summary>
    /// Runs one synchronisation of HR on its own tracking context, as the Worker does, and records the status the
    /// Worker would complete its Activity with.
    /// </summary>
    private async Task RunAsync(ConnectedSystemRunType runType, bool expectNoErrors = false)
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_hrId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == runType);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = runType
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        if (runType == ConnectedSystemRunType.FullSynchronisation)
        {
            await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
                system, runProfile, activity, new CancellationTokenSource()) { HistoryWriteModeForTests = _historyWriteMode }.PerformFullSyncAsync();
        }
        else
        {
            await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
                system, runProfile, activity, new CancellationTokenSource()) { HistoryWriteModeForTests = _historyWriteMode }.PerformDeltaSyncAsync();
        }

        var counts = await jim.Activities.GetActivityRpeiErrorCountsAsync(activity.Id);
        _activityStatuses[activity.Id] = ActivityOutcomeEvaluator.Evaluate(counts, hasWarningMessage: !string.IsNullOrEmpty(activity.WarningMessage),
            activity.ConnectedSystemRunType, activity.ObjectsProcessed).ToString();

        if (expectNoErrors)
            Assert.That(counts.TotalWithErrors, Is.Zero, "precondition: the arranging run reported no errors");
    }

    /// <summary>
    /// The history, with every object named: HR and Directory objects by employee id, identities by name, including
    /// those the run deleted.
    /// </summary>
    private async Task<string> SnapshotAsync()
    {
        await using (var ctx = NewContext())
        {
            // Live identities by name; a deleted one by the name its deletion record kept.
            foreach (var (name, id) in await ReadMetaverseObjectIdsByNameAsync())
                _objectLabels[id] = $"MVO:{name}";
            var deleted = await ctx.MetaverseObjectChanges.AsNoTracking()
                .Where(c => c.DeletedMetaverseObjectId != null)
                .Select(c => new { c.DeletedMetaverseObjectId, c.DeletedObjectDisplayName })
                .ToListAsync();
            foreach (var record in deleted)
                _objectLabels.TryAdd(record.DeletedMetaverseObjectId!.Value, $"MVO:{record.DeletedObjectDisplayName}");

            // Directory accounts the runs provisioned, by the identity they were provisioned for.
            var provisioned = await ctx.ConnectedSystemObjects.AsNoTracking()
                .Where(c => c.ConnectedSystemId == _directoryId)
                .Select(c => new { c.Id, c.MetaverseObjectId })
                .ToListAsync();
            foreach (var account in provisioned.Where(a => !_objectLabels.ContainsKey(a.Id)))
            {
                _objectLabels[account.Id] = account.MetaverseObjectId is { } owner && _objectLabels.TryGetValue(owner, out var ownerLabel)
                    ? $"Directory:for {ownerLabel}"
                    : "Directory:unowned";
            }
        }

        return await SynchronisationHistorySnapshot.CaptureAsync(_connectionString, _objectLabels, _activityStatuses);
    }

    private static string FirstDifference(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        for (var n = 0; n < Math.Max(expectedLines.Length, actualLines.Length); n++)
        {
            var e = n < expectedLines.Length ? expectedLines[n] : "<end>";
            var a = n < actualLines.Length ? actualLines[n] : "<end>";
            if (e != a)
                return $"line {n + 1}{Environment.NewLine}expected: {e}{Environment.NewLine}actual:   {a}";
        }

        return "none";
    }

    #endregion
}
