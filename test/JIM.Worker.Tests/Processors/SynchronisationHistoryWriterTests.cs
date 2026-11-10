// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Exceptions;
using JIM.Worker.Processors;
using Moq;
using NUnit.Framework;
using Activity = JIM.Models.Activities.Activity;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// <see cref="SynchronisationHistoryWriter"/> (#2874): at most one write in flight, written in hand-off order, waited for
/// at a barrier, a failure found there and kept, never cancelled with the run, and every write drained before the run
/// or the writer ends. The repository is faked, each write gated on a <see cref="TaskCompletionSource{TResult}"/> the
/// test completes.
/// </summary>
[TestFixture]
public class SynchronisationHistoryWriterTests
{
    private Mock<ISyncRepository> _runRepository = null!;
    private Mock<ISyncRepository> _repository = null!;
    private Mock<ISyncRepositoryScope> _scope = null!;
    private Activity _activity = null!;
    private readonly List<SynchronisationHistoryBatch> _written = [];
    private readonly Queue<TaskCompletionSource<bool>> _gates = new();

    [SetUp]
    public void SetUp()
    {
        _written.Clear();
        _gates.Clear();
        _activity = new Activity();
        _repository = new Mock<ISyncRepository>();
        _repository.Setup(r => r.WriteSynchronisationHistoryAsync(It.IsAny<SynchronisationHistoryBatch>()))
            .Returns((SynchronisationHistoryBatch batch) =>
            {
                _written.Add(batch);
                return _gates.Count > 0 ? _gates.Dequeue().Task : Task.FromResult(true);
            });
        _scope = new Mock<ISyncRepositoryScope>();
        _scope.Setup(s => s.Repository).Returns(_repository.Object);
        _runRepository = new Mock<ISyncRepository>();
        _runRepository.Setup(r => r.OpenIsolatedScope()).Returns(_scope.Object);
    }

    private SynchronisationHistoryWriter NewWriter(SynchronisationHistoryWriteMode mode = SynchronisationHistoryWriteMode.Overlapped)
        => new(_runRepository.Object, mode, _activity, "HR");

    private TaskCompletionSource<bool> Gate()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates.Enqueue(gate);
        return gate;
    }

    private static SynchronisationHistoryBatch Batch(int page, int totalPages = 9) => new()
    {
        Items = [new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() }],
        ItemIdsWrittenWithDeletion = new HashSet<Guid>(),
        Outcomes = [],
        OutcomeChangeRecords = [],
        ItemChangeRecords = [],
        CausalEdges = [],
        MatchCandidates = [],
        MetaverseObjectChanges = [],
        MetaverseObjectChangeAppends = [],
        CounterDeltas = new Dictionary<ActivityStatCounterKey, long>(),
        Page = page,
        TotalPages = totalPages
    };

    [Test]
    public async Task HandOffAsync_WhileWriteInFlight_ThrowsAsync()
    {
        await using var writer = NewWriter();
        var gate = Gate();
        await writer.HandOffAsync(Batch(1));

        Assert.That(() => writer.HandOffAsync(Batch(2)), Throws.InvalidOperationException.With.Message.Contain("in flight"));

        gate.SetResult(true);
        await writer.WaitAsync(SynchronisationHistoryBarrier.PageStateFlush);
    }

    [Test]
    public async Task HandOffAsync_RunRepositoryHoldsATransaction_ThrowsAndWritesNothingAsync()
    {
        // A write on its own connection, waited for while the run's connection holds uncommitted rows it needs (the rows
        // a deletion's transaction wrote, say), would wait for ever.
        await using var writer = NewWriter(SynchronisationHistoryWriteMode.Inline);
        _runRepository.Setup(r => r.HasOpenTransaction).Returns(true);

        Assert.That(() => writer.HandOffAsync(Batch(1)), Throws.InvalidOperationException.With.Message.Contain("transaction"));
        Assert.That(_written, Is.Empty);
    }

    [Test]
    public async Task Constructor_OpensItsOwnRepositoryAndDisposeReleasesItAsync()
    {
        var writer = NewWriter();
        await writer.HandOffAsync(Batch(1));
        await writer.WaitAsync(SynchronisationHistoryBarrier.PageStateFlush);

        await writer.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            _runRepository.Verify(r => r.OpenIsolatedScope(), Times.Once);
            _runRepository.Verify(r => r.WriteSynchronisationHistoryAsync(It.IsAny<SynchronisationHistoryBatch>()), Times.Never,
                "the run's own repository writes no history");
            _scope.Verify(s => s.Dispose(), Times.Once);
        }
    }

    [Test]
    public async Task HandOffAsync_Overlapped_ReturnsBeforeTheWriteCompletesAsync()
    {
        await using var writer = NewWriter();
        var gate = Gate();

        await writer.HandOffAsync(Batch(1));

        Assert.That(() => _written.Count, Is.EqualTo(1).After(5000, 10), "the write started");
        var wait = writer.WaitAsync(SynchronisationHistoryBarrier.PageStateFlush);
        Assert.That(wait.IsCompleted, Is.False, "the wait lasts as long as the write");
        gate.SetResult(true);
        Assert.That((await wait)!.Batch.Page, Is.EqualTo(1));
    }

    [Test]
    public async Task HandOffAsync_Inline_WritesBeforeReturningAsync()
    {
        await using var writer = NewWriter(SynchronisationHistoryWriteMode.Inline);
        var gate = Gate();

        var handOff = writer.HandOffAsync(Batch(1));

        Assert.That(handOff.IsCompleted, Is.False, "the hand-off lasts as long as the write");
        gate.SetResult(true);
        await handOff;
        var wait = writer.WaitAsync(SynchronisationHistoryBarrier.AwaitedFlush);
        Assert.That(wait.IsCompleted, Is.True, "nothing is left to wait for");
        Assert.That((await wait)!.ItemsWrittenOutsideTracking, Is.True);
    }

    [Test]
    public async Task WaitAsync_NothingHandedOff_ReturnsNullAsync()
    {
        await using var writer = NewWriter();

        Assert.That(await writer.WaitAsync(SynchronisationHistoryBarrier.PageLoopExit), Is.Null);
    }

    [Test]
    public async Task HandOffAsync_SeveralBatches_WritesThemInHandOffOrderAsync()
    {
        await using var writer = NewWriter();

        for (var page = 1; page <= 3; page++)
        {
            await writer.HandOffAsync(Batch(page));
            await writer.WaitAsync(SynchronisationHistoryBarrier.PageStateFlush);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_written.Select(b => b.Page), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(writer.BatchesWritten, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task WaitAsync_WriteFailed_ThrowsAHistoryFailureNamingThePageAndStaysFaultedAsync()
    {
        await using var writer = NewWriter();
        var gate = Gate();
        await writer.HandOffAsync(Batch(4, 12));
        var cause = new TimeoutException("the write timed out");
        gate.SetException(cause);

        var failure = Assert.ThrowsAsync<SynchronisationHistoryPersistenceException>(() => writer.WaitAsync(SynchronisationHistoryBarrier.PageStateFlush));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Page, Is.EqualTo(4));
            Assert.That(failure.TotalPages, Is.EqualTo(12));
            Assert.That(failure.ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(failure.InnerException, Is.SameAs(cause));
            Assert.That(failure.Message, Is.EqualTo(SynchronisationHistoryPersistenceException.BuildMessage(4, 12, "HR")));
            Assert.That(Assert.ThrowsAsync<SynchronisationHistoryPersistenceException>(() => writer.HandOffAsync(Batch(5))), Is.SameAs(failure),
                "nothing more is written once a write has failed");
            Assert.That(Assert.ThrowsAsync<SynchronisationHistoryPersistenceException>(() => writer.WaitAsync(SynchronisationHistoryBarrier.PageLoopExit)),
                Is.SameAs(failure));
            Assert.That(Assert.Throws<SynchronisationHistoryPersistenceException>(writer.ThrowIfFaulted), Is.SameAs(failure));
            Assert.That(_written, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task WaitAsync_AwaitedFlushFailed_RethrowsTheFailureAsItIsAndThenStaysFaultedAsync()
    {
        // An awaited flush's failure surfaces in the flush that caused it, so the context there already says what failed;
        // only later calls, made elsewhere, need the history failure that names the batch.
        await using var writer = NewWriter(SynchronisationHistoryWriteMode.Inline);
        var gate = Gate();
        var cause = new InvalidOperationException("the database refused the write");
        gate.SetException(cause);
        await writer.HandOffAsync(Batch(0, 0));

        var thrown = Assert.ThrowsAsync<InvalidOperationException>(() => writer.WaitAsync(SynchronisationHistoryBarrier.AwaitedFlush));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(cause));
            Assert.That(writer.ThrowIfFaulted, Throws.InstanceOf<SynchronisationHistoryPersistenceException>().With.InnerException.SameAs(cause));
        }
    }

    [Test]
    public async Task ThrowIfFaulted_WriteStillRunning_ReturnsAtOnceAsync()
    {
        await using var writer = NewWriter();
        var gate = Gate();
        await writer.HandOffAsync(Batch(1));

        var watch = Stopwatch.StartNew();
        Assert.That(writer.ThrowIfFaulted, Throws.Nothing);
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)), "the probe never waits for the write");

        gate.SetResult(true);
        await writer.WaitAsync(SynchronisationHistoryBarrier.PageStateFlush);
    }

    [Test]
    public async Task ThrowIfFaulted_WriteHasFailed_ThrowsTheHistoryFailureWithoutBeingWaitedForAsync()
    {
        await using var writer = NewWriter();
        var gate = Gate();
        await writer.HandOffAsync(Batch(3));
        gate.SetException(new TimeoutException("the write timed out"));

        Assert.That(writer.ThrowIfFaulted, Throws.InstanceOf<SynchronisationHistoryPersistenceException>().With.Property("Page").EqualTo(3).After(5000, 10));
    }

    [Test]
    public async Task HandOffAsync_RunCancelled_TheWriteStillCompletesAsync()
    {
        // A page's changes are saved before its history is handed off, so its history is completed, not abandoned.
        using var run = new CancellationTokenSource();
        await using var writer = NewWriter();
        var gate = Gate();
        await writer.HandOffAsync(Batch(2));

        await run.CancelAsync();
        gate.SetResult(true);

        var result = await writer.WaitAsync(SynchronisationHistoryBarrier.PageLoopExit);
        Assert.That(result!.Batch.Page, Is.EqualTo(2));
    }

    [Test]
    public async Task DrainOnExitAsync_NoPrimaryFailure_ThrowsTheHistoryFailureAsync()
    {
        await using var writer = NewWriter();
        Gate().SetException(new TimeoutException("the write timed out"));
        await writer.HandOffAsync(Batch(6));

        Assert.That(() => writer.DrainOnExitAsync(null), Throws.InstanceOf<SynchronisationHistoryPersistenceException>().With.Property("Page").EqualTo(6));
    }

    [Test]
    public async Task DrainOnExitAsync_RunCancelled_ThrowsTheHistoryFailureRatherThanEndingAsCancelledAsync()
    {
        await using var writer = NewWriter();
        Gate().SetException(new TimeoutException("the write timed out"));
        await writer.HandOffAsync(Batch(6));

        Assert.That(() => writer.DrainOnExitAsync(new OperationCanceledException()), Throws.InstanceOf<SynchronisationHistoryPersistenceException>(),
            "the run lost history, so it fails rather than cancels");
    }

    [Test]
    public async Task DrainOnExitAsync_AnotherFailureEndedTheRun_LeavesItToPropagateAndRecordsTheHistoryFailureAsAWarningAsync()
    {
        await using var writer = NewWriter();
        Gate().SetException(new TimeoutException("the write timed out"));
        await writer.HandOffAsync(Batch(6, 7));

        Assert.That(() => writer.DrainOnExitAsync(new InvalidOperationException("the run's own failure")), Throws.Nothing,
            "the run's own failure is what the caller rethrows, with its detail intact");
        Assert.That(_activity.WarningMessage, Is.EqualTo(SynchronisationHistoryPersistenceException.BuildMessage(6, 7, "HR")));
    }

    [Test]
    public async Task DrainOnExitAsync_WriteSucceeded_ReturnsAsync()
    {
        await using var writer = NewWriter();
        var gate = Gate();
        await writer.HandOffAsync(Batch(1));
        var drain = writer.DrainOnExitAsync(null);

        Assert.That(drain.IsCompleted, Is.False, "the drain waits for the write in flight");
        gate.SetResult(true);
        Assert.That(() => drain, Throws.Nothing);
    }

    [Test]
    public async Task DrainOnExitAsync_WriteInFlightSucceeds_ReturnsItSoItsTotalsCanBeCountedAsync()
    {
        // A run ending on another failure while a page's history is in flight still wrote that history, so its totals
        // belong on the Activity.
        await using var writer = NewWriter();
        await writer.HandOffAsync(Batch(4));

        var drained = await writer.DrainOnExitAsync(new InvalidOperationException("the run's own failure"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(drained?.Batch.Page, Is.EqualTo(4));
            Assert.That(await writer.DrainOnExitAsync(null), Is.Null, "nothing is drained twice");
        }
    }

    [Test]
    public async Task DrainOnExitAsync_TheHistoryFailureEndedTheRun_RecordsNoSecondWarningAsync()
    {
        // The failure ending the run is the history failure itself, found at a barrier: it is the error reported, so it
        // is not reported again as a warning.
        await using var writer = NewWriter();
        Gate().SetException(new TimeoutException("the write timed out"));
        await writer.HandOffAsync(Batch(2, 3));
        var failure = Assert.ThrowsAsync<SynchronisationHistoryPersistenceException>(() => writer.WaitAsync(SynchronisationHistoryBarrier.PageStateFlush));

        Assert.That(() => writer.DrainOnExitAsync(failure), Throws.Nothing);
        Assert.That(_activity.WarningMessage, Is.Null.Or.Empty);
    }

    [Test]
    public async Task DrainOnExitAsync_AnAwaitedFlushFailureEndedTheRun_RecordsNoSecondWarningAsync()
    {
        // An awaited flush's failure propagates as it is, and the code around it may wrap it; either way it is the
        // history failure, already the error reported.
        await using var writer = NewWriter(SynchronisationHistoryWriteMode.Inline);
        var cause = new InvalidOperationException("the database refused the write");
        Gate().SetException(cause);
        await writer.HandOffAsync(Batch(0, 0));
        Assert.ThrowsAsync<InvalidOperationException>(() => writer.WaitAsync(SynchronisationHistoryBarrier.AwaitedFlush));

        Assert.That(() => writer.DrainOnExitAsync(new SyncPersistenceException("the page's changes were not saved", cause, 0, 0, "HR")), Throws.Nothing);
        Assert.That(_activity.WarningMessage, Is.Null.Or.Empty);
    }

    [Test]
    public async Task HandOffAndWait_EmitTheirSpansWithTheirTagsAsync()
    {
        var spans = new List<System.Diagnostics.Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "JIM.Sync",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span => { lock (spans) spans.Add(span); }
        };
        ActivitySource.AddActivityListener(listener);
        await using var writer = NewWriter();

        await writer.HandOffAsync(Batch(5));
        await writer.WaitAsync(SynchronisationHistoryBarrier.PageStateFlush);

        System.Diagnostics.Activity Span(string name)
        {
            lock (spans)
                return spans.Single(s => s.OperationName == name && Equals(s.GetTagItem("page"), 5));
        }

        var write = Span("SyncHistoryWrite");
        var wait = Span("SyncHistoryWait");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(write.GetTagItem("mode"), Is.EqualTo(nameof(SynchronisationHistoryWriteMode.Overlapped)));
            Assert.That(write.GetTagItem("rpeiCount"), Is.EqualTo(1));
            Assert.That(wait.GetTagItem("barrier"), Is.EqualTo(nameof(SynchronisationHistoryBarrier.PageStateFlush)));
            Assert.That(wait.GetTagItem("writeWasRunning"), Is.Not.Null);
            Assert.That(wait.GetTagItem("waitedMs"), Is.Not.Null);
        }
    }

    /// <summary>
    /// #2895: the run's end-of-run line gives the time waited at each barrier, so it is kept per barrier, and the
    /// barriers' totals add up to the total waited for.
    /// </summary>
    [Test]
    public async Task WaitAsync_AtDifferentBarriers_KeepsTheTimeWaitedPerBarrierAsync()
    {
        await using var writer = NewWriter();

        var gate = Gate();
        await writer.HandOffAsync(Batch(1));
        var waitAtPageState = writer.WaitAsync(SynchronisationHistoryBarrier.PageStateFlush);
        await Task.Delay(40);
        gate.SetResult(true);
        await waitAtPageState;

        await writer.HandOffAsync(Batch(2));
        await writer.WaitAsync(SynchronisationHistoryBarrier.PageLoopExit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.WaitTime(SynchronisationHistoryBarrier.PageStateFlush), Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(30)));
            Assert.That(writer.WaitTime(SynchronisationHistoryBarrier.PageLoopExit), Is.LessThan(TimeSpan.FromMilliseconds(30)),
                "a write already complete is barely waited for");
            Assert.That(writer.WaitTime(SynchronisationHistoryBarrier.AwaitedFlush), Is.EqualTo(TimeSpan.Zero), "nothing waited there");
            Assert.That(writer.TotalWaitTime, Is.EqualTo(
                writer.WaitTime(SynchronisationHistoryBarrier.PageStateFlush)
                + writer.WaitTime(SynchronisationHistoryBarrier.PageLoopExit)
                + writer.WaitTime(SynchronisationHistoryBarrier.AwaitedFlush)));
        }
    }

    /// <summary>
    /// #2895: the run times each history preparation and each page flush on its own thread and records them here, so the
    /// end-of-run line gives their totals beside the writes and waits.
    /// </summary>
    [Test]
    public async Task RecordPreparationAndFlush_SeveralTimes_SumsEachAsync()
    {
        await using var writer = NewWriter();

        writer.RecordPreparation(TimeSpan.FromMilliseconds(3));
        writer.RecordPreparation(TimeSpan.FromMilliseconds(4));
        writer.RecordFlush(TimeSpan.FromMilliseconds(10));
        writer.RecordFlush(TimeSpan.FromMilliseconds(12));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.TotalPrepareTime, Is.EqualTo(TimeSpan.FromMilliseconds(7)));
            Assert.That(writer.TotalFlushTime, Is.EqualTo(TimeSpan.FromMilliseconds(22)));
        }
    }

    [Test]
    public async Task DisposeAsync_WriteInFlight_WaitsForItThenReleasesTheScopeAsync()
    {
        var writer = NewWriter();
        var gate = Gate();
        await writer.HandOffAsync(Batch(1));

        var dispose = writer.DisposeAsync().AsTask();

        Assert.That(dispose.IsCompleted, Is.False, "disposal waits for the write in flight");
        _scope.Verify(s => s.Dispose(), Times.Never);
        gate.SetResult(true);
        await dispose;
        _scope.Verify(s => s.Dispose(), Times.Once);
    }
}
