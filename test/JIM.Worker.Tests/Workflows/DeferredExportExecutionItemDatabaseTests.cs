// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors;
using JIM.Connectors.Mock;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;
using Activity = JIM.Models.Activities.Activity;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Real-PostgreSQL export whose every change waits on the deferred pass (#2852): each deferred batch's Run Profile
/// Execution Items, with their change records, outcomes and causal edges, are written as the batch completes, as the
/// first pass's are, rather than all at once when the run ends. Runs the real export processor, sequentially and with
/// parallel batches on contexts of their own, as the Worker does, through the Mock Call Connector. Parallel batches
/// write those items on their own contexts, at the same time (#2902), so the fixture also proves they write exactly
/// what one worker writes, on either pass, and that a batch whose items cannot be written fails the run.
/// </summary>
/// <remarks>
/// At Scale500k25kGroups the deferred pass's items were written in one statement at the step's last moment (a 19.1 s
/// COPY of the change values), and held in memory, 3.7 million change objects, for the whole pass. Opt-in via the
/// <c>JIM_TEST_RESET_*</c> environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class DeferredExportExecutionItemDatabaseTests
{
    /// <summary>Three batches of the Worker's 100.</summary>
    private const int UserCount = 250;

    private const string ManagerDn = "CN=Manager,OU=People,DC=glitterband,DC=local";
    private const string Failure = "The directory went away";

    private string _connectionString = null!;
    private int _systemId;
    private Guid _managerCsoId;
    private Dictionary<Guid, (int Index, Guid CsoId, Guid MvoId)> _usersByExportId = [];
    private readonly Dictionary<string, string?> _savedDbEnvVars = [];

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL deferred export execution item tests.");
        _connectionString = connectionString!;

        // A batch's items are written over parallel connections once there are enough of them, and those connections are
        // built from the JIM_DB_* variables rather than the context: pointed at the test database, so the Worker's own
        // write path runs. A non-default port goes in the host, as BuildConnectionString has no port of its own.
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        SetDbEnvVar(Constants.Config.DatabaseHostname, builder.Port == 5432 ? builder.Host! : $"{builder.Host}:{builder.Port}");
        SetDbEnvVar(Constants.Config.DatabaseName, builder.Database!);
        SetDbEnvVar(Constants.Config.DatabaseUsername, builder.Username!);
        SetDbEnvVar(Constants.Config.DatabasePassword, builder.Password!);

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (var (name, value) in _savedDbEnvVars)
            Environment.SetEnvironmentVariable(name, value);
    }

    private void SetDbEnvVar(string name, string value)
    {
        _savedDbEnvVars[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    /// <summary>
    /// Every deferred batch's items are written as it completes: by the time the last batch is sent, an earlier batch's
    /// items are already in the database. What is written is exactly what writing them at the end wrote: one item per
    /// export, its outcome, its causal edge and its change record, and the Activity's counts.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    public async Task Export_EveryChangeDeferred_WritesEachBatchsItemsAsTheBatchCompletesAsync(int maxExportParallelism)
    {
        await SeedAsync(maxExportParallelism);
        var persistedWhenSent = new ConcurrentQueue<int>();

        var activity = await ExportAsync(_ =>
        {
            persistedWhenSent.Enqueue(CountPersistedItems());
            return ConnectedSystemExportResult.Succeeded();
        });

        var items = await ReadItemsAsync(activity.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedWhenSent, Has.Count.EqualTo(UserCount));
            Assert.That(persistedWhenSent.Max(), Is.GreaterThanOrEqualTo(100),
                "the last batch is sent once an earlier batch's items are in the database");
            Assert.That(items, Is.EqualTo(ExpectedItems(failedFrom: null)), "one item per export, as written at the end of the run");
            Assert.That((activity.TotalExported, activity.TotalErrors, activity.TotalPendingExports), Is.EqualTo((UserCount, 0, 0)));
        }
    }

    /// <summary>
    /// A deferred batch that fails after the batches before it were written leaves their items in the database as they
    /// were, and writes its own failure once: one item per export, none lost and none written twice.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    public async Task Export_LastDeferredBatchFails_KeepsTheEarlierBatchesItemsAndWritesEachOnceAsync(int maxExportParallelism)
    {
        await SeedAsync(maxExportParallelism);
        var persistedWhenSent = new ConcurrentQueue<int>();

        var activity = await ExportAsync(pe =>
        {
            persistedWhenSent.Enqueue(CountPersistedItems());
            return _usersByExportId[pe.Id].Index >= 200 ? throw new InvalidOperationException(Failure) : ConnectedSystemExportResult.Succeeded();
        });

        var items = await ReadItemsAsync(activity.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedWhenSent.Max(), Is.GreaterThanOrEqualTo(100),
                "the failing batch is sent once an earlier batch's items are in the database");
            Assert.That(items, Is.EqualTo(ExpectedItems(failedFrom: 200)), "one item per export, the failed batch's reporting its failure");
            // A failed attempt is recorded as an export that failed, so it is counted as exported and as an error.
            Assert.That((activity.TotalExported, activity.TotalErrors, activity.TotalPendingExports), Is.EqualTo((UserCount, UserCount - 200, 0)));
        }
    }

    /// <summary>
    /// #2902: four workers, each writing its batches' items on its own context at the same time, write exactly what one
    /// worker writes: the same items, outcomes, causal edges and change records, the same stat counters and the same
    /// Activity totals, on the first pass and the deferred pass alike, failures included.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Export_FourWorkers_WritesExactlyWhatOneWorkerWritesAsync(bool deferred)
    {
        ConnectedSystemExportResult Answer(PendingExport pe) => _usersByExportId[pe.Id].Index % 9 == 0
            ? ConnectedSystemExportResult.Failed("The directory refused the change")
            : ConnectedSystemExportResult.Succeeded();

        await SeedAsync(maxExportParallelism: 1, deferred);
        var oneWorker = await ReadRunAsync((await ExportAsync(Answer)).Id);

        await PostgresTestDatabase.ResetAsync(_connectionString);
        await SeedAsync(maxExportParallelism: 4, deferred);
        var fourWorkers = await ReadRunAsync((await ExportAsync(Answer)).Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(oneWorker.Items, Has.Count.EqualTo(UserCount));
            Assert.That(oneWorker.Items.Count(item => item.Contains("The directory refused the change")), Is.EqualTo(28),
                "every ninth change was refused");
            Assert.That(fourWorkers.Items, Is.EqualTo(oneWorker.Items), "the same items, outcomes, causal edges and change records");
            Assert.That(fourWorkers.Counters, Is.EqualTo(oneWorker.Counters), "the same stat counters");
            Assert.That(fourWorkers.Totals, Is.EqualTo(oneWorker.Totals), "the same Activity totals");
        }
    }

    /// <summary>
    /// #2902: a batch whose execution items cannot be written fails the run, with the error on the Activity, and leaves
    /// none of its items behind; the other batches' items are written whole or not at all. With parallel batches the
    /// failure was taken for a failed attempt at a batch whose results were already recorded, and the run completed
    /// without the batch's items.
    /// </summary>
    [TestCase(1)]
    [TestCase(4)]
    public async Task Export_ABatchsItemsCannotBeWritten_FailsTheRunAndLeavesNoneOfThemAsync(int maxExportParallelism)
    {
        const string refusal = "The execution items could not be written";
        await SeedAsync(maxExportParallelism, deferred: false);
        await ExecuteSqlAsync(
            $@"CREATE FUNCTION jim_test_refuse_execution_item() RETURNS trigger AS $$ BEGIN
                 IF NEW.""DisplayNameSnapshot"" = '{UserName(150)}' THEN RAISE EXCEPTION '{refusal}'; END IF;
                 RETURN NEW;
               END $$ LANGUAGE plpgsql;
               CREATE TRIGGER jim_test_refuse_execution_item BEFORE INSERT ON ""ActivityRunProfileExecutionItems""
                 FOR EACH ROW EXECUTE FUNCTION jim_test_refuse_execution_item();");

        Activity? activity = null;
        try
        {
            var thrown = Assert.CatchAsync(async () => await ExportAsync(_ => ConnectedSystemExportResult.Succeeded(), started => activity = started));
            Assert.That(thrown!.Message, Does.Contain(refusal));
        }
        finally
        {
            await ExecuteSqlAsync(@"DROP TRIGGER jim_test_refuse_execution_item ON ""ActivityRunProfileExecutionItems"";
                                    DROP FUNCTION jim_test_refuse_execution_item();");
        }

        await using var ctx = NewContext();
        var persisted = await ctx.Activities.AsNoTracking().SingleAsync(a => a.Id == activity!.Id);
        var written = (await ReadItemsAsync(activity!.Id)).Select(item => int.Parse(item[5..8])).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(persisted.ErrorMessage, Does.Contain(refusal), "the failure is recorded on the Activity");
            Assert.That(written.Where(index => index is >= 100 and < 200), Is.Empty, "none of the refused batch's items are written");
            Assert.That(written.Count(index => index < 100), Is.EqualTo(100).Or.EqualTo(0), "the first batch's items are written whole or not at all");
            Assert.That(written.Count(index => index >= 200), Is.EqualTo(50).Or.EqualTo(0), "the last batch's items are written whole or not at all");
        }
    }

    #region Harness

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs); an export run must too.
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    /// <summary>
    /// One Connected System of users named by DN, a manager joined to a Metaverse Object, and <see cref="UserCount"/>
    /// users each with an Update queued by its Metaverse Object: a display name, and the manager by the manager's
    /// Metaverse Object id, as a synchronisation stages a reference whose object it has not looked up. Every export
    /// therefore waits on the deferred pass, which resolves the manager to its DN. Not <paramref name="deferred"/>, each
    /// Update carries the display name alone, and is written by the first pass.
    /// </summary>
    private async Task SeedAsync(int maxExportParallelism, bool deferred = true)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition, MaxExportParallelism = maxExportParallelism };
        var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType type, bool externalId = false, bool secondaryExternalId = false)
        {
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = name, ConnectedSystemObjectType = userType, Type = type, AttributePlurality = AttributePlurality.SingleValued,
                Selected = true, IsExternalId = externalId, IsSecondaryExternalId = secondaryExternalId
            };
            userType.Attributes.Add(attribute);
            return attribute;
        }
        var objectGuid = Attribute("objectGUID", AttributeDataType.Text, externalId: true);
        var dn = Attribute("distinguishedName", AttributeDataType.Text, secondaryExternalId: true);
        var displayName = Attribute("displayName", AttributeDataType.Text);
        var manager = Attribute("manager", AttributeDataType.Reference);
        var person = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        seed.AddRange(connectorDefinition, system, userType, person);
        await seed.SaveChangesAsync();
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile { ConnectedSystemId = system.Id, Name = "Export", RunType = ConnectedSystemRunType.Export });

        ConnectedSystemObject NewUser(string userDn, MetaverseObject mvo)
        {
            var cso = new ConnectedSystemObject
            {
                ConnectedSystemId = system.Id, TypeId = userType.Id, ExternalIdAttributeId = objectGuid.Id, SecondaryExternalIdAttributeId = dn.Id,
                Status = ConnectedSystemObjectStatus.Normal, MetaverseObjectId = mvo.Id, JoinType = ConnectedSystemObjectJoinType.Provisioned
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, AttributeId = objectGuid.Id, StringValue = Guid.NewGuid().ToString() });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, AttributeId = dn.Id, StringValue = userDn });
            return cso;
        }

        var managerMvo = new MetaverseObject { Type = person };
        var userMvos = Enumerable.Range(0, UserCount).Select(_ => new MetaverseObject { Type = person }).ToList();
        seed.AddRange(userMvos.Prepend(managerMvo));
        await seed.SaveChangesAsync();

        var managerCso = NewUser(ManagerDn, managerMvo);
        var userCsos = userMvos.Select((mvo, i) => NewUser(UserDn(i), mvo)).ToList();
        seed.AddRange(userCsos.Prepend(managerCso));
        await seed.SaveChangesAsync();

        var queuedAt = DateTime.UtcNow.AddHours(-1);
        _usersByExportId = [];
        for (var i = 0; i < UserCount; i++)
        {
            var export = new PendingExport
            {
                Id = Guid.NewGuid(), ConnectedSystemId = system.Id, ConnectedSystemObjectId = userCsos[i].Id, SourceMetaverseObjectId = userMvos[i].Id,
                ChangeType = PendingExportChangeType.Update, Status = PendingExportStatus.Pending, HasUnresolvedReferences = deferred,
                CreatedAt = queuedAt.AddMilliseconds(i)
            };
            export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = displayName.Id, ChangeType = PendingExportAttributeChangeType.Update, StringValue = UserName(i)
            });
            if (deferred)
            {
                export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(), AttributeId = manager.Id, ChangeType = PendingExportAttributeChangeType.Update,
                    UnresolvedReferenceValue = managerMvo.Id.ToString()
                });
            }
            seed.PendingExports.Add(export);
            _usersByExportId[export.Id] = (i, userCsos[i].Id, userMvos[i].Id);
        }
        await seed.SaveChangesAsync();

        _systemId = system.Id;
        _managerCsoId = managerCso.Id;
    }

    private static string UserDn(int index) => $"CN=User {index:D3},OU=People,DC=glitterband,DC=local";

    private static string UserName(int index) => $"User {index:D3}";

    /// <summary>
    /// Runs one export as the Worker does: on its own tracking context, with each parallel batch on a context of its own
    /// and a Connector of its own, each answering every change through <paramref name="answer"/>.
    /// </summary>
    /// <param name="answer">How every change is answered.</param>
    /// <param name="started">Told of the run's Activity once it exists, for a test whose export throws.</param>
    private async Task<Activity> ExportAsync(Func<PendingExport, ConnectedSystemExportResult> answer, Action<Activity>? started = null)
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, repository.Sync);
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_systemId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == ConnectedSystemRunType.Export);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = ConnectedSystemRunType.Export
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        started?.Invoke(activity);

        var workerTask = new SynchronisationWorkerTask(system.Id, runProfile.Id) { Id = Guid.NewGuid(), Status = WorkerTaskStatus.Processing, Activity = activity };
        var processor = new SyncExportTaskProcessor(new SyncServer(jim), jim.SyncRepository,
            new MockCallConnector().WithConnectedSystemExportResultFactory(answer), system, runProfile, workerTask, new CancellationTokenSource(),
            syncRepoFactory: () =>
            {
                var batchJim = new JimApplication(new PostgresDataRepository(NewContext(trackAll: true)));
                return new SyncRepositoryScope(batchJim.SyncRepository, batchJim);
            },
            connectorFactory: new AnsweringConnectorFactory(answer));
        await processor.PerformExportAsync();
        return activity;
    }

    private sealed class AnsweringConnectorFactory(Func<PendingExport, ConnectedSystemExportResult> answer) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) =>
            new MockCallConnector().WithConnectedSystemExportResultFactory(answer);
    }

    /// <summary>
    /// What a run wrote: its items as <see cref="ReadItemsAsync"/> reads them, its stat counters, and its Activity's
    /// totals as persisted.
    /// </summary>
    private async Task<(List<string> Items, List<string> Counters, string Totals)> ReadRunAsync(Guid activityId)
    {
        var items = await ReadItemsAsync(activityId);

        await using var ctx = NewContext();
        var counters = (await ctx.ActivityStatCounters.AsNoTracking().Where(c => c.ActivityId == activityId).ToListAsync())
            .Select(c => $"{c.Dimension}/{c.Key}={c.Count}")
            .Order(StringComparer.Ordinal)
            .ToList();
        var a = await ctx.Activities.AsNoTracking().SingleAsync(x => x.Id == activityId);
        var totals = $"exported={a.TotalExported} deprovisioned={a.TotalDeprovisioned} pending={a.TotalPendingExports} errors={a.TotalErrors} " +
                     $"added={a.TotalAdded} updated={a.TotalUpdated} deleted={a.TotalDeleted} created={a.TotalCreated} " +
                     $"processed={a.ObjectsProcessed}/{a.ObjectsToProcess}";
        return (items, counters, totals);
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>How many execution items the export has written so far, read on a connection of the test's own.</summary>
    private int CountPersistedItems()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(@"SELECT count(*) FROM ""ActivityRunProfileExecutionItems""", connection);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Every execution item the export wrote, with what hangs off it, as one line each in the users' order: what it says
    /// happened, its error, what it names, its outcomes, its causal edges and its change record.
    /// </summary>
    private async Task<List<string>> ReadItemsAsync(Guid activityId)
    {
        await using var ctx = NewContext();
        var items = await ctx.ActivityRunProfileExecutionItems.AsNoTracking()
            .Where(i => i.ActivityId == activityId)
            .Include(i => i.SyncOutcomes)
            .ToListAsync();
        var itemIds = items.Select(i => i.Id).ToList();
        var edgesByItem = (await ctx.CausalEdges.AsNoTracking().Where(e => itemIds.Contains(e.EffectRunProfileExecutionItemId)).ToListAsync())
            .ToLookup(e => e.EffectRunProfileExecutionItemId);
        var changes = await ctx.ConnectedSystemObjectChanges.AsNoTracking()
            .Where(c => c.ActivityRunProfileExecutionItemId != null && itemIds.Contains(c.ActivityRunProfileExecutionItemId.Value))
            .Include(c => c.AttributeChanges).ThenInclude(a => a.ValueChanges)
            .ToListAsync();
        var changeByItem = changes.ToLookup(c => c.ActivityRunProfileExecutionItemId!.Value);
        var userByCso = _usersByExportId.Values.ToDictionary(u => u.CsoId);
        var exportByCso = _usersByExportId.ToDictionary(pair => pair.Value.CsoId, pair => pair.Key);

        string Name(Guid? id) => id == null ? "-" : id == _managerCsoId ? "manager" : userByCso.TryGetValue(id.Value, out var u) ? $"user {u.Index:D3}" : id.ToString()!;

        return items
            .OrderBy(i => i.ConnectedSystemObjectId.HasValue && userByCso.TryGetValue(i.ConnectedSystemObjectId.Value, out var u) ? u.Index : -1)
            .Select(i =>
            {
                var user = userByCso[i.ConnectedSystemObjectId!.Value];
                var outcomes = string.Join(",", i.SyncOutcomes.Select(o => $"{o.OutcomeType}:{o.DetailCount}").Order());
                var edges = string.Join(",", edgesByItem[i.Id].Select(e =>
                    $"{e.EdgeType}/{e.ReasonCode} mvo={(e.CauseMetaverseObjectId == user.MvoId ? "own" : e.CauseMetaverseObjectId?.ToString() ?? "-")} " +
                    $"pe={(e.CausePendingExportId == exportByCso[user.CsoId] ? "own" : e.CausePendingExportId?.ToString() ?? "-")} '{e.CauseDisplayName}' cs={e.ConnectedSystemName}").Order());
                var change = string.Join(";", changeByItem[i.Id].Select(c => $"{c.ChangeType} " + string.Join(" ", c.AttributeChanges
                    .OrderBy(a => a.AttributeName)
                    .Select(a => $"{a.AttributeName}=[" + string.Join(",", a.ValueChanges.Select(v =>
                        $"{v.ValueChangeType}:{v.StringValue}:{Name(v.ReferenceValueId)}:stub={v.IsPendingExportStub}:unresolved={v.IsUnresolvedReference}").Order()) + "]"))));
                return $"{Name(i.ConnectedSystemObjectId)} | {i.ObjectChangeType} | {i.ErrorType} | {i.ErrorMessage} | {i.DisplayNameSnapshot} | " +
                       $"pe={(i.PendingExportId == null ? "-" : "set")} | {outcomes} | {edges} | {change}";
            })
            .ToList();
    }

    /// <summary>
    /// The items an export of every user writes, those from <paramref name="failedFrom"/> on reporting a failed batch.
    /// </summary>
    private List<string> ExpectedItems(int? failedFrom) => Enumerable.Range(0, UserCount).Select(i =>
    {
        var change = $"Exported displayName=[Add:{UserName(i)}:-:stub=False:unresolved=False] manager=[Add:{ManagerDn}:manager:stub=True:unresolved=False]";
        return i >= failedFrom
            ? $"user {i:D3} | Exported | UnhandledError | Export failed: Batch export failed: {Failure} | {UserName(i)} | pe=- | Exported:2 |  | {change}"
            : $"user {i:D3} | Exported | NotSet |  | {UserName(i)} | pe=- | Exported:2 | " +
              $"PendingExportQueueingCausedExportExecution/ExportUpdateStaged mvo=own pe=own '{UserName(i)}' cs=Glitterband EMEA | {change}";
    }).ToList();

    #endregion
}
