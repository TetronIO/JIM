// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;
using Activity = JIM.Models.Activities.Activity;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A synchronisation's page loop writes each page's history while the next page loads and is processed (#2874, Phase
/// 3), on PostgreSQL, where the run chooses that mode itself: the next page loads before the previous page's history
/// commits; no change of the next page is saved until it has; a failed write fails the run naming its page, leaving no
/// history of that page and no change after it; and a cancelled run still completes the history it handed off.
/// </summary>
/// <remarks>
/// <para>
/// Six HR people over pages of two, each projected to a Metaverse Object with its change record. A history write is held
/// back by a lock on the execution item table taken from the test's own connection, which every history write needs and
/// nothing else in the run writes to; the run's own state writes go on regardless. A write is made to fail by a trigger
/// on the Metaverse Object change values that refuses one marked name, which only page 2's history carries.
/// </para>
/// <para>
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SynchronisationHistoryOverlapDatabaseTests
{
    private const int PageSize = 2;
    private const int People = 6;
    private const int TotalPages = 3;
    private const string FailMarker = "History Refused";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static Guid HrId(int employee) => Guid.Parse($"00000000-0000-0000-0000-{employee:D12}");
    private static Guid[] PageIds(int page) => [HrId(page * PageSize - 1), HrId(page * PageSize)];

    private string _connectionString = null!;
    private int _hrId;
    private int _hrTypeId;
    private int _hrEmployeeIdAttributeId;
    private int _hrDisplayNameAttributeId;

    private ActivityListener _listener = null!;
    private readonly ConcurrentQueue<System.Diagnostics.Activity> _spansStarted = new();
    private readonly ConcurrentQueue<System.Diagnostics.Activity> _spansStopped = new();

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL overlapped synchronisation history tests.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        _spansStarted.Clear();
        _spansStopped.Clear();
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "JIM.Sync",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = span => _spansStarted.Enqueue(span),
            ActivityStopped = span => _spansStopped.Enqueue(span)
        };
        ActivitySource.AddActivityListener(_listener);

        await PostgresTestDatabase.ResetAsync(_connectionString);
        await ExecuteAsync("""DROP TRIGGER IF EXISTS refuse_marked_history ON "MetaverseObjectChangeAttributeValues";""");
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        _listener.Dispose();
        await ExecuteAsync("""DROP TRIGGER IF EXISTS refuse_marked_history ON "MetaverseObjectChangeAttributeValues";""");
    }

    /// <summary>
    /// While page 1's history is held back, page 2 is loaded and processed: the history write overlaps the next page's
    /// work, and is recorded on its own span under the run's, not under the page flush that handed it off.
    /// </summary>
    [Test]
    public async Task PageLoop_NextPageLoadsBeforePreviousHistoryCommitsAsync()
    {
        await ArrangeAsync();
        var (processor, activity, _, run) = await CreateRunAsync(ConnectedSystemRunType.FullSynchronisation);
        var pageTwoProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.OnConnectedSystemObjectProcessedInPass2 = cso =>
        {
            if (PageIds(2).Contains(cso.Id))
                pageTwoProcessed.TrySetResult();
        };

        Task runTask;
        long pageOneItemsWhilePageTwoProcessed;
        await using (await HoldHistoryWritesAsync())
        {
            runTask = Task.Run(run);
            var processedInTime = await Task.WhenAny(pageTwoProcessed.Task, runTask, Task.Delay(Patience)) == pageTwoProcessed.Task;
            pageOneItemsWhilePageTwoProcessed = await CountItemsAsync(PageIds(1));
            Assert.That(processedInTime, Is.True, "page 2 was processed while page 1's history was still being written");
        }

        await runTask;

        var pageOneWrite = _spansStopped.Single(s => s.OperationName == "SyncHistoryWrite" && Equals(s.GetTagItem("page"), 1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pageOneItemsWhilePageTwoProcessed, Is.Zero, "page 1's history had not committed");
            Assert.That(await CountItemsAsync(Enumerable.Range(1, People).Select(HrId).ToArray()), Is.EqualTo(People), "every page's history is written");
            Assert.That(activity.TotalProjected, Is.EqualTo(People), "every page's totals are applied once its history is written");
            Assert.That(pageOneWrite.GetTagItem("mode"), Is.EqualTo(nameof(SynchronisationHistoryWriteMode.Overlapped)),
                "on PostgreSQL the run overlaps its history writes of its own accord");
            Assert.That(pageOneWrite.Parent?.OperationName, Is.EqualTo("ProcessConnectedSystemObjects"),
                "the write is timed under the run's page loop, not the page flush it outlives");
        }
    }

    /// <summary>
    /// While page 1's history is held back, the run waits for it before saving any of page 2's changes: no Metaverse
    /// Object of page 2 exists until page 1's history has committed.
    /// </summary>
    [Test]
    public async Task PageLoop_StateFlushWaitsForPreviousHistoryAsync()
    {
        await ArrangeAsync();
        var (_, _, _, run) = await CreateRunAsync(ConnectedSystemRunType.FullSynchronisation);

        Task runTask;
        long metaverseObjectsWhileWaiting;
        await using (await HoldHistoryWritesAsync())
        {
            runTask = Task.Run(run);
            var waiting = await WaitForAsync(() => RunningWait(SynchronisationHistoryBarrier.PageStateFlush, page: 1) != null, runTask);
            // Long enough for a run that did not wait to have saved page 2's objects.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            metaverseObjectsWhileWaiting = await CountMetaverseObjectsAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(waiting, Is.True, "the run waits for page 1's history before saving page 2's changes");
                Assert.That(runTask.IsCompleted, Is.False, "the run is still waiting");
            }
        }

        await runTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(metaverseObjectsWhileWaiting, Is.EqualTo(PageSize), "only page 1's Metaverse Objects existed while its history was written");
            Assert.That(await CountMetaverseObjectsAsync(), Is.EqualTo(People));
        }
    }

    /// <summary>
    /// A history write that fails fails the run, naming the page whose history it was: none of that page's history is
    /// left, the history before it is complete, the changes of the pages up to it are saved and none after, its totals
    /// are not counted, the watermark does not move, and nothing is left running or unobserved.
    /// </summary>
    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    public async Task PageLoop_HistoryWriteFails_FailsTheRunNamingThePageAsync(ConnectedSystemRunType runType)
    {
        await ArrangeAsync(failMarkerEmployee: 3);
        await ExecuteAsync(
            $$"""
            CREATE OR REPLACE FUNCTION refuse_marked_history() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
                RAISE EXCEPTION 'test refuses the history of %', NEW."StringValue";
            END $fn$;
            CREATE TRIGGER refuse_marked_history BEFORE INSERT ON "MetaverseObjectChangeAttributeValues"
                FOR EACH ROW WHEN (NEW."StringValue" = '{{FailMarker}}') EXECUTE FUNCTION refuse_marked_history();
            """);
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => unobserved.Enqueue(e.Exception);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var (_, activity, _, run) = await CreateRunAsync(runType, recordFailureAsTheWorkerDoes: true);

            var failure = Assert.ThrowsAsync<SynchronisationHistoryPersistenceException>(() => run());
            var writesStartedWhenTheRunEnded = _spansStarted.Count(s => s.OperationName == "SyncHistoryWrite");
            var writesStoppedWhenTheRunEnded = _spansStopped.Count(s => s.OperationName == "SyncHistoryWrite");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            await using var read = NewContext();
            var recorded = await read.Activities.AsNoTracking().SingleAsync(a => a.Id == activity.Id);
            var system = await read.ConnectedSystems.AsNoTracking().SingleAsync(s => s.Id == _hrId);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(failure!.Page, Is.EqualTo(2));
                Assert.That(failure.TotalPages, Is.EqualTo(TotalPages));
                Assert.That(failure.InnerException, Is.InstanceOf<PostgresException>());
                Assert.That(recorded.Status, Is.EqualTo(ActivityStatus.FailedWithError));
                Assert.That(recorded.ErrorMessage, Does.StartWith(SynchronisationHistoryPersistenceException.BuildMessage(2, TotalPages, "HR")));

                Assert.That(await CountItemsAsync(PageIds(1)), Is.EqualTo(PageSize), "page 1's history is complete");
                Assert.That(await CountMetaverseObjectChangesAsync(), Is.EqualTo(PageSize), "page 1's change records, and none of page 2's");
                Assert.That(await CountItemsAsync(PageIds(2)), Is.Zero, "none of page 2's history is left");
                Assert.That(await CountItemsAsync(PageIds(3)), Is.Zero);
                Assert.That(await CountMetaverseObjectsAsync(), Is.EqualTo(2 * PageSize), "pages 1 and 2 saved their changes; page 3 did not");
                Assert.That(activity.TotalProjected, Is.EqualTo(PageSize), "only page 1's totals were applied");
                Assert.That(recorded.TotalProjected, Is.EqualTo(PageSize));
                Assert.That(system.LastSyncCompletedAt, Is.Null, "the watermark does not move");

                Assert.That(writesStartedWhenTheRunEnded, Is.EqualTo(2), "pages 1 and 2 were handed off; nothing after the failure");
                Assert.That(writesStoppedWhenTheRunEnded, Is.EqualTo(writesStartedWhenTheRunEnded), "no write was left running");
                Assert.That(unobserved, Is.Empty, "no write's failure went unobserved");
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    /// <summary>
    /// A run cancelled while a page's history is held back waits for it: the history is complete, and the run ends as a
    /// cancelled run does, without failing and without moving the watermark.
    /// </summary>
    [Test]
    public async Task PageLoop_CancelledWhileHistoryWriteHeld_WaitsAndCompletesThatHistoryAsync()
    {
        await ArrangeAsync();
        var (processor, activity, cancellation, run) = await CreateRunAsync(ConnectedSystemRunType.FullSynchronisation);
        processor.OnConnectedSystemObjectProcessedInPass2 = cso =>
        {
            if (cso.Id == PageIds(2)[0])
                cancellation.Cancel();
        };

        Task runTask;
        await using (await HoldHistoryWritesAsync())
        {
            runTask = Task.Run(run);
            var waiting = await WaitForAsync(() => RunningWait(SynchronisationHistoryBarrier.PageStateFlush, page: 1) != null, runTask);
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(waiting, Is.True, "the cancelled run waits for page 1's history");
                Assert.That(runTask.IsCompleted, Is.False, "the cancelled run does not end before page 1's history is written");
            }
        }

        Assert.That(() => runTask, Throws.Nothing, "a cancelled run ends without failing");

        await using var read = NewContext();
        var system = await read.ConnectedSystems.AsNoTracking().SingleAsync(s => s.Id == _hrId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cancellation.IsCancellationRequested, Is.True);
            Assert.That(await CountItemsAsync(PageIds(1)), Is.EqualTo(PageSize), "page 1's history is complete");
            Assert.That(await CountItemsAsync([PageIds(2)[0]]), Is.EqualTo(1), "what page 2 processed before cancelling is recorded too");
            Assert.That(await CountItemsAsync(PageIds(3)), Is.Zero);
            Assert.That(activity.TotalProjected, Is.EqualTo(PageSize + 1));
            Assert.That(system.LastSyncCompletedAt, Is.Null, "a cancelled run does not move the watermark");
        }
    }

    /// <summary>
    /// A batch handed to the writer reaches no Connected System Object or Metaverse Object: the writer touches nothing
    /// the run is still working on, and a page's loaded objects can be collected while its history is written.
    /// </summary>
    [Test]
    public async Task PreparedBatch_ReachesNoLiveObjectGraphAsync()
    {
        await ArrangeAsync();
        var (processor, _, _, run) = await CreateRunAsync(ConnectedSystemRunType.FullSynchronisation);
        var reached = new List<string>();
        var batches = 0;
        processor.OnHistoryHandedOffForTests = batch =>
        {
            batches++;
            reached.AddRange(LiveObjectsReachableFrom(batch).Select(path => $"page {batch.Page}: {path}"));
        };

        await run();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batches, Is.GreaterThanOrEqualTo(TotalPages));
            Assert.That(reached, Is.Empty);
        }
    }

    #region Arrangement and reads

    /// <summary>
    /// Six HR people, projected with their names and employee ids; pages of two; Metaverse Object change tracking on.
    /// The person numbered <paramref name="failMarkerEmployee"/>, if any, carries the name the failure trigger refuses.
    /// </summary>
    private async Task ArrangeAsync(int? failMarkerEmployee = null)
    {
        await using (var seed = NewContext())
        {
            foreach (var (key, valueType, value) in new[]
                     {
                         (Constants.SettingKeys.SyncPageSize, ServiceSettingValueType.Integer, PageSize.ToString()),
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
            var hrEmployeeId = NewAttribute(hrType, "employeeId", isExternalId: true);
            var hrDisplayName = NewAttribute(hrType, "displayName");
            hrType.Attributes.AddRange([hrEmployeeId, hrDisplayName]);

            var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            var mvDisplayName = NewMetaverseAttribute(mvType, "Display Name");
            var mvEmployeeId = NewMetaverseAttribute(mvType, "Employee Id");

            seed.AddRange(connectorDefinition, hr, hrType, mvType, mvDisplayName, mvEmployeeId);
            await seed.SaveChangesAsync();
            seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
                { ConnectedSystemId = hr.Id, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation });
            seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
                { ConnectedSystemId = hr.Id, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation });

            var import = new SyncRule
            {
                Name = "HR Import", ConnectedSystem = hr, Direction = SyncRuleDirection.Import, Enabled = true,
                ConnectedSystemObjectType = hrType, MetaverseObjectType = mvType, ProjectToMetaverse = true
            };
            foreach (var (source, target) in new[] { (hrDisplayName, mvDisplayName), (hrEmployeeId, mvEmployeeId) })
            {
                import.AttributeFlowRules.Add(new SyncRuleMapping
                {
                    SyncRule = import, TargetMetaverseAttribute = target,
                    Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source } }
                });
            }

            seed.SyncRules.Add(import);
            await seed.SaveChangesAsync();

            _hrId = hr.Id;
            _hrTypeId = hrType.Id;
            _hrEmployeeIdAttributeId = hrEmployeeId.Id;
            _hrDisplayNameAttributeId = hrDisplayName.Id;
        }

        await using var ctx = NewContext();
        var people = Enumerable.Range(1, People).Select(employee =>
        {
            var cso = new ConnectedSystemObject
            {
                Id = HrId(employee), ConnectedSystemId = _hrId, TypeId = _hrTypeId, ExternalIdAttributeId = _hrEmployeeIdAttributeId,
                Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _hrEmployeeIdAttributeId, StringValue = $"E{employee:D2}" });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = _hrDisplayNameAttributeId,
                StringValue = employee == failMarkerEmployee ? FailMarker : $"Person {employee:D2}"
            });
            return cso;
        }).ToList();
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync(people);
    }

    private static ConnectedSystemObjectTypeAttribute NewAttribute(ConnectedSystemObjectType type, string name, bool isExternalId = false) => new()
    {
        Name = name, ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = isExternalId
    };

    private static MetaverseAttribute NewMetaverseAttribute(MetaverseObjectType type, string name) => new()
    {
        Name = name, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = [type]
    };

    /// <summary>
    /// A synchronisation of HR on a tracking context of its own, as the Worker runs one, ready to start; the context is
    /// disposed when the run ends.
    /// </summary>
    private async Task<(SyncTaskProcessorBase Processor, Activity Activity, CancellationTokenSource Cancellation, Func<Task> Run)> CreateRunAsync(
        ConnectedSystemRunType runType, bool recordFailureAsTheWorkerDoes = false)
    {
        var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        var jim = new JimApplication(repository, syncRepository: repository.Sync);
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

        var cancellation = new CancellationTokenSource();
        if (runType == ConnectedSystemRunType.FullSynchronisation)
        {
            var full = new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
                system, runProfile, activity, cancellation);
            return (full, activity, cancellation, () => RunThenDisposeAsync(full.PerformFullSyncAsync));
        }

        var delta = new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
            system, runProfile, activity, cancellation);
        return (delta, activity, cancellation, () => RunThenDisposeAsync(delta.PerformDeltaSyncAsync));

        async Task RunThenDisposeAsync(Func<Task> run)
        {
            try
            {
                await run();
            }
            catch (Exception exception) when (recordFailureAsTheWorkerDoes)
            {
                // As the Worker records a failed run: on the run's own Activity and unit of work, then rethrown.
                await jim.Activities.FailActivityWithErrorAsync(activity, exception);
                throw;
            }
            finally
            {
                jim.Dispose();
                await ctx.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Holds every history write back until disposed: a lock on the execution item table, which each write inserts into
    /// first and nothing else in these runs writes to.
    /// </summary>
    private async Task<IAsyncDisposable> HoldHistoryWritesAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("""LOCK TABLE "ActivityRunProfileExecutionItems" IN EXCLUSIVE MODE""", connection, transaction))
            await command.ExecuteNonQueryAsync();
        return new HeldLock(connection, transaction);
    }

    private sealed class HeldLock(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>The wait at the barrier given, for the page given, while it is still waiting.</summary>
    private System.Diagnostics.Activity? RunningWait(SynchronisationHistoryBarrier barrier, int page) => _spansStarted.FirstOrDefault(s =>
        s.OperationName == "SyncHistoryWait"
        && Equals(s.GetTagItem("barrier"), barrier.ToString())
        && Equals(s.GetTagItem("page"), page)
        && !_spansStopped.Contains(s));

    /// <summary>Waits until the condition holds, or the run ends, or patience runs out; true when it held.</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, Task run)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline && !run.IsCompleted)
        {
            if (condition())
                return true;
            await Task.Delay(20);
        }

        return condition();
    }

    private Task<long> CountItemsAsync(Guid[] connectedSystemObjectIds) => ScalarAsync(
        """SELECT count(*) FROM "ActivityRunProfileExecutionItems" WHERE "ConnectedSystemObjectId" = ANY(@ids)""",
        new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = connectedSystemObjectIds });

    private Task<long> CountMetaverseObjectsAsync() => ScalarAsync("""SELECT count(*) FROM "MetaverseObjects" """);

    private Task<long> CountMetaverseObjectChangesAsync() => ScalarAsync("""SELECT count(*) FROM "MetaverseObjectChanges" """);

    private async Task<long> ScalarAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Every path from the batch, through any field, to a Connected System Object or Metaverse Object. Configuration
    /// the run shares (Synchronisation Rules, Object Types, attributes, Connected Systems) is read-only for the run, and
    /// the run's Activity is read only for its id, by the item's own scalar; neither is followed.
    /// </summary>
    private static IEnumerable<string> LiveObjectsReachableFrom(SynchronisationHistoryBatch batch)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(object Value, string Path)>();
        pending.Push((batch, nameof(SynchronisationHistoryBatch)));
        while (pending.Count > 0)
        {
            var (value, path) = pending.Pop();
            if (!visited.Add(value))
                continue;

            var type = value.GetType();
            if (value is ConnectedSystemObject or MetaverseObject)
            {
                yield return $"{path} ({type.Name})";
                continue;
            }

            if (value is SyncRule or ConnectedSystem or ConnectedSystemObjectType or ConnectedSystemObjectTypeAttribute
                or MetaverseObjectType or MetaverseAttribute or Activity)
                continue;

            if (value is IEnumerable sequence and not string)
            {
                var index = 0;
                foreach (var element in sequence.Cast<object?>().Where(IsFollowed))
                    pending.Push((element!, $"{path}[{index++}]"));
                continue;
            }

            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var next = field.GetValue(value);
                    if (IsFollowed(next))
                        pending.Push((next!, $"{path}.{field.Name}"));
                }
            }
        }

        static bool IsFollowed(object? value) => value != null && value is not string && !value.GetType().IsPrimitive && !value.GetType().IsEnum
            && value is not Guid and not DateTime and not decimal and not TimeSpan and not Type and not Delegate and not Pointer;
    }

    #endregion
}
