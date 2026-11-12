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
/// A synchronisation's page loop writes each page's history while the next page loads, is processed and saves its
/// changes (#2874, #2924), on PostgreSQL, where the run chooses that mode itself: the next page loads, and saves its
/// changes, before the previous page's history commits, and waits for it only as it hands its own history off; a failed
/// write fails the run naming its page, leaving no history of that page and no change beyond the next page, whose history
/// is still written; and a cancelled run still completes the history it handed off.
/// </summary>
/// <remarks>
/// <para>
/// Eight HR people over pages of two, each projected to a Metaverse Object with its change record. Every history write is
/// held back by a lock on the execution item table taken from the test's own connection, which every history write needs
/// and nothing else in the run writes to; the run's own state writes go on regardless. One page's write alone is held back
/// by a trigger on the Metaverse Object change values that waits on an advisory lock the test holds, for a person carrying
/// a marked name, and made to fail by the same trigger for another marked name.
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
    private const int People = 8;
    private const int TotalPages = 4;
    private const string FailMarker = "History Refused";
    private const string HoldMarker = "History Held";

    /// <summary>The advisory lock a marked page's history write waits on while the test holds it.</summary>
    private const long HistoryHoldLock = 2924;
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

    private JimDbContext NewContext(bool trackAll = false, string? connectionString = null)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(connectionString ?? _connectionString)
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
    /// While page 1's history is held back, page 2 saves its changes, and waits for page 1's history only as it hands its
    /// own off (#2924): page 2's Metaverse Objects exist while page 1's history has yet to commit, and page 2's history
    /// is not written until it has.
    /// </summary>
    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    public async Task PageLoop_NextPageSavesItsChangesWhilePreviousHistoryIsWrittenAsync(ConnectedSystemRunType runType)
    {
        await ArrangeAsync();
        var (_, _, _, run) = await CreateRunAsync(runType);

        Task runTask;
        long metaverseObjectsWhileWaiting;
        long pageOneItemsWhileWaiting;
        long pageTwoItemsWhileWaiting;
        bool waiting;
        await using (await HoldHistoryWritesAsync())
        {
            runTask = Task.Run(run);
            waiting = await WaitForAsync(() => RunningWait(SynchronisationHistoryBarrier.PageHandOff, page: 1) != null, runTask);
            metaverseObjectsWhileWaiting = await CountMetaverseObjectsAsync();
            pageOneItemsWhileWaiting = await CountItemsAsync(PageIds(1));
            pageTwoItemsWhileWaiting = await CountItemsAsync(PageIds(2));
            Assert.That(runTask.IsCompleted, Is.False, "the run is still waiting");
        }

        await runTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(waiting, Is.True, "page 2 waits for page 1's history as it hands its own off");
            Assert.That(metaverseObjectsWhileWaiting, Is.EqualTo(2 * PageSize),
                "page 2 saved its Metaverse Objects while page 1's history was being written");
            Assert.That(pageOneItemsWhileWaiting, Is.Zero, "page 1's history had not committed");
            Assert.That(pageTwoItemsWhileWaiting, Is.Zero, "page 2's history is handed off only once page 1's is written");
            Assert.That(await CountMetaverseObjectsAsync(), Is.EqualTo(People));
            Assert.That(await CountItemsAsync(Enumerable.Range(1, People).Select(HrId).ToArray()), Is.EqualTo(People));
        }
    }

    /// <summary>
    /// The connections a run holds while a page's history is written alongside the next page's parallel bulk writes stay
    /// within the write parallelism plus the run's own two contexts (#2924): the history write runs on its own context's
    /// connection alone, inside its one transaction, so the page's parallel writers keep the whole parallelism.
    /// </summary>
    [Test]
    public async Task PageLoop_HistoryWriteAlongsideParallelPageWrites_StaysWithinTheConnectionBudgetAsync()
    {
        const int writeParallelism = 4;
        await ArrangeAsync();
        await ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS budget_log (pid int, backends int);
            TRUNCATE budget_log;
            CREATE OR REPLACE FUNCTION log_budget() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
                INSERT INTO budget_log SELECT pg_backend_pid(), count(*)::int FROM pg_stat_activity
                    WHERE datname = current_database() AND application_name = 'jim-2924-budget';
                RETURN NULL;
            END $fn$;
            DROP TRIGGER IF EXISTS log_budget ON "MetaverseObjectAttributeValues";
            CREATE TRIGGER log_budget AFTER INSERT ON "MetaverseObjectAttributeValues" FOR EACH STATEMENT EXECUTE FUNCTION log_budget();
            """);
        try
        {
            var (_, _, _, run) = await CreateRunAsync(ConnectedSystemRunType.FullSynchronisation, writeParallelism: writeParallelism);

            Task runTask;
            await using (await HoldHistoryWritesAsync())
            {
                runTask = Task.Run(run);
                // Page 2 has saved its changes, in parallel, while page 1's history write holds its connection.
                Assert.That(await WaitForAsync(() => RunningWait(SynchronisationHistoryBarrier.PageHandOff, page: 1) != null, runTask), Is.True);
            }

            await runTask;

            var writers = await ScalarAsync("SELECT count(DISTINCT pid) FROM budget_log");
            var peak = await ScalarAsync("SELECT coalesce(max(backends), 0)::bigint FROM budget_log");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(writers, Is.GreaterThan(1), "precondition: the pages' values were written across parallel connections");
                Assert.That(peak, Is.LessThanOrEqualTo(writeParallelism + 2),
                    "the page's parallel writers, the run's own connection and the history write's, and nothing more");
            }
        }
        finally
        {
            await ExecuteAsync("""DROP TRIGGER IF EXISTS log_budget ON "MetaverseObjectAttributeValues"; DROP TABLE IF EXISTS budget_log;""");
        }
    }

    /// <summary>
    /// A history write that fails fails the run, naming the page whose history it was (#2924). The next page has saved its
    /// changes by the time the failure is found, as it hands its own history off; that history is still written, so one
    /// page's history is lost, as before the overlap. None of the failed page's history is left, no page beyond the next
    /// saves anything, only the written pages' totals count, the watermark does not move, and nothing is left running or
    /// unobserved.
    /// </summary>
    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    public async Task PageLoop_HistoryWriteFails_WritesTheNextPagesHistoryThenFailsNamingThePageAsync(ConnectedSystemRunType runType)
    {
        await ArrangeAsync(refused: [3]);
        await InstallHistoryTriggerAsync();
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => unobserved.Enqueue(e.Exception);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var (_, activity, _, run) = await CreateRunAsync(runType, recordFailureAsTheWorkerDoes: true);

            // Page 2's write is held until page 3 is waiting for it at its hand-off, so the failure is found there and
            // not by the check before page 3 loads.
            Task runTask;
            await using (await HoldMarkedHistoryAsync())
            {
                runTask = Task.Run(run);
                Assert.That(await WaitForAsync(() => RunningWait(SynchronisationHistoryBarrier.PageHandOff, page: 2) != null, runTask), Is.True,
                    "page 3 reached its hand-off while page 2's write was held");
            }

            var failure = Assert.ThrowsAsync<SynchronisationHistoryPersistenceException>(() => runTask);
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
                Assert.That(failure.NextPage, Is.EqualTo(3));
                Assert.That(failure.NextPageOutcome, Is.EqualTo(SynchronisationHistoryNextPageOutcome.Written));
                Assert.That(recorded.Status, Is.EqualTo(ActivityStatus.FailedWithError));
                Assert.That(recorded.ErrorMessage, Does.StartWith(SynchronisationHistoryPersistenceException.BuildMessage(2, TotalPages, "HR")));
                Assert.That(recorded.ErrorMessage, Does.Contain(SynchronisationHistoryPersistenceException.BuildNextPageMessage(
                    3, TotalPages, SynchronisationHistoryNextPageOutcome.Written, null)));

                Assert.That(await CountItemsAsync(PageIds(1)), Is.EqualTo(PageSize), "page 1's history is complete");
                Assert.That(await CountItemsAsync(PageIds(2)), Is.Zero, "none of page 2's history is left");
                Assert.That(await CountItemsAsync(PageIds(3)), Is.EqualTo(PageSize), "page 3's history was written after page 2's failed");
                Assert.That(await CountItemsAsync(PageIds(4)), Is.Zero);
                Assert.That(await CountMetaverseObjectChangesAsync(), Is.EqualTo(2 * PageSize), "pages 1 and 3's change records, none of page 2's");
                Assert.That(await CountMetaverseObjectsAsync(), Is.EqualTo(3 * PageSize), "pages 1 to 3 saved their changes; page 4 did not");
                Assert.That(activity.TotalProjected, Is.EqualTo(2 * PageSize), "only pages 1 and 3's totals were applied");
                Assert.That(recorded.TotalProjected, Is.EqualTo(2 * PageSize));
                Assert.That(system.LastSyncCompletedAt, Is.Null, "the watermark does not move");

                Assert.That(writesStartedWhenTheRunEnded, Is.EqualTo(3), "pages 1, 2 and 3 were written; nothing after the failure");
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
    /// When the next page's history fails to write too, the run fails naming both pages, the first as its cause (#2924):
    /// neither page's history is left, no page beyond the next saves anything, and neither page's totals count.
    /// </summary>
    [Test]
    public async Task PageLoop_HistoryWriteFailsAndSoDoesTheNextPages_FailsNamingBothPagesAsync()
    {
        await ArrangeAsync(refused: [3, 5]);
        await InstallHistoryTriggerAsync();
        var (_, activity, _, run) = await CreateRunAsync(ConnectedSystemRunType.FullSynchronisation, recordFailureAsTheWorkerDoes: true);

        Task runTask;
        await using (await HoldMarkedHistoryAsync())
        {
            runTask = Task.Run(run);
            Assert.That(await WaitForAsync(() => RunningWait(SynchronisationHistoryBarrier.PageHandOff, page: 2) != null, runTask), Is.True);
        }

        var failure = Assert.ThrowsAsync<SynchronisationHistoryPersistenceException>(() => runTask);

        await using var read = NewContext();
        var recorded = await read.Activities.AsNoTracking().SingleAsync(a => a.Id == activity.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Page, Is.EqualTo(2), "page 2's failure, found first, is the cause");
            Assert.That(failure.InnerException, Is.InstanceOf<PostgresException>());
            Assert.That(failure.NextPage, Is.EqualTo(3));
            Assert.That(failure.NextPageOutcome, Is.EqualTo(SynchronisationHistoryNextPageOutcome.WriteFailed));
            Assert.That(failure.NextPageFailure, Is.InstanceOf<PostgresException>(), "page 3's failure is kept too");
            Assert.That(recorded.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(recorded.ErrorMessage, Does.StartWith(SynchronisationHistoryPersistenceException.BuildMessage(2, TotalPages, "HR")));
            Assert.That(recorded.ErrorMessage, Does.Contain($"Page 3 of {TotalPages} had saved its changes when the failure was found; " +
                "its history could not be written either"));

            Assert.That(await CountItemsAsync(PageIds(1)), Is.EqualTo(PageSize));
            Assert.That(await CountItemsAsync(PageIds(2)), Is.Zero);
            Assert.That(await CountItemsAsync(PageIds(3)), Is.Zero);
            Assert.That(await CountMetaverseObjectsAsync(), Is.EqualTo(3 * PageSize), "no page beyond page 3 saved its changes");
            Assert.That(activity.TotalProjected, Is.EqualTo(PageSize), "only page 1's totals were applied");
        }
    }

    /// <summary>
    /// A run cancelled during page 2 while page 1's history is held back waits for it as page 2 hands its own off: the
    /// history is complete, and the run ends as a cancelled run does, without failing and without moving the watermark.
    /// </summary>
    [Test]
    public async Task PageLoop_CancelledWhileHistoryWriteHeld_WaitsAtTheHandOffAndCompletesThatHistoryAsync()
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
            var waiting = await WaitForAsync(() => RunningWait(SynchronisationHistoryBarrier.PageHandOff, page: 1) != null, runTask);
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
    /// A run cancelled during page 2, whose history is then held back, waits for it as the page loop ends: the history is
    /// complete, and the run ends as a cancelled run does.
    /// </summary>
    [Test]
    public async Task PageLoop_CancelledWhileLastHistoryWriteHeld_WaitsAtTheLoopExitAndCompletesThatHistoryAsync()
    {
        await ArrangeAsync(held: [3]);
        await InstallHistoryTriggerAsync();
        var (processor, activity, cancellation, run) = await CreateRunAsync(ConnectedSystemRunType.FullSynchronisation);
        processor.OnConnectedSystemObjectProcessedInPass2 = cso =>
        {
            if (cso.Id == PageIds(2)[0])
                cancellation.Cancel();
        };

        Task runTask;
        await using (await HoldMarkedHistoryAsync())
        {
            runTask = Task.Run(run);
            var waiting = await WaitForAsync(() => RunningWait(SynchronisationHistoryBarrier.PageLoopExit, page: 2) != null, runTask);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(waiting, Is.True, "the cancelled run waits for page 2's history as the page loop ends");
                Assert.That(runTask.IsCompleted, Is.False);
            }
        }

        Assert.That(() => runTask, Throws.Nothing, "a cancelled run ends without failing");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountItemsAsync([PageIds(2)[0]]), Is.EqualTo(1), "page 2's history is complete");
            Assert.That(await CountItemsAsync(PageIds(3)), Is.Zero);
            Assert.That(activity.TotalProjected, Is.EqualTo(PageSize + 1));
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
    /// Eight HR people, projected with their names and employee ids; pages of two; Metaverse Object change tracking on.
    /// The people numbered in <paramref name="refused"/> carry the name the history trigger refuses, and those in
    /// <paramref name="held"/> the name it holds back while the test holds the advisory lock
    /// (<see cref="InstallHistoryTriggerAsync"/>).
    /// </summary>
    private async Task ArrangeAsync(int[]? refused = null, int[]? held = null)
    {
        string NameOf(int employee) =>
            refused?.Contains(employee) == true ? FailMarker
            : held?.Contains(employee) == true ? HoldMarker
            : $"Person {employee:D2}";

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
                StringValue = NameOf(employee)
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
        ConnectedSystemRunType runType, bool recordFailureAsTheWorkerDoes = false, int? writeParallelism = null)
    {
        // With a write parallelism, the run's every connection carries an application name the budget test counts by, and
        // every bulk write of the run's own goes parallel across up to that many connections.
        var connectionString = writeParallelism == null
            ? _connectionString
            : new NpgsqlConnectionStringBuilder(_connectionString) { ApplicationName = "jim-2924-budget" }.ConnectionString;
        var ctx = NewContext(trackAll: true, connectionString);
        var repository = new PostgresDataRepository(ctx);
        if (writeParallelism != null)
        {
            var sync = (JIM.PostgresData.Repositories.SyncRepository)repository.Sync;
            sync.ConnectionStringForParallelWrites = connectionString;
            sync.WriteParallelismOverride = writeParallelism;
            sync.ParallelWriteRowThreshold = 1;
            sync.ParallelWriteRowsPerConnection = 1;
        }

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

    /// <summary>
    /// A trigger on the Metaverse Object change values that, for a person carrying <see cref="HoldMarker"/>, waits on the
    /// advisory lock <see cref="HoldMarkedHistoryAsync"/> holds, and for one carrying <see cref="FailMarker"/> waits on it
    /// too and then refuses the row, failing that page's history write. Only the history write inserts those rows.
    /// </summary>
    private Task InstallHistoryTriggerAsync() => ExecuteAsync(
        $$"""
        CREATE OR REPLACE FUNCTION refuse_marked_history() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            PERFORM pg_advisory_xact_lock({{HistoryHoldLock}});
            IF NEW."StringValue" = '{{FailMarker}}' THEN
                RAISE EXCEPTION 'test refuses the history of %', NEW."StringValue";
            END IF;
            RETURN NEW;
        END $fn$;
        CREATE TRIGGER refuse_marked_history BEFORE INSERT ON "MetaverseObjectChangeAttributeValues"
            FOR EACH ROW WHEN (NEW."StringValue" IN ('{{FailMarker}}', '{{HoldMarker}}')) EXECUTE FUNCTION refuse_marked_history();
        """);

    /// <summary>
    /// Holds back the history write of every page carrying a marked person (<see cref="InstallHistoryTriggerAsync"/>) until
    /// disposed; no other write is held.
    /// </summary>
    private async Task<IAsyncDisposable> HoldMarkedHistoryAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand($"SELECT pg_advisory_lock({HistoryHoldLock})", connection))
            await command.ExecuteNonQueryAsync();
        return new HeldAdvisoryLock(connection);
    }

    private sealed class HeldAdvisoryLock(NpgsqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Released explicitly: a pooled connection keeps its session's locks until it is next reset.
            await using (var command = new NpgsqlCommand($"SELECT pg_advisory_unlock({HistoryHoldLock})", connection))
                await command.ExecuteNonQueryAsync();
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
