// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using JIM.Application.Diagnostics;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Exceptions;
using JIM.Utilities;
using Activity = JIM.Models.Activities.Activity;
using Serilog;

namespace JIM.Worker.Processors;

/// <summary>
/// Writes a synchronisation run's history, batch by batch, on a repository of its own (#2874): one per run, created as
/// the run starts and disposed as it ends. A batch is handed off once prepared; the run waits for it at a barrier.
/// </summary>
/// <remarks>
/// <para>
/// At most one write is in flight: a hand-off while one has not been waited for throws. Writes therefore happen in
/// hand-off order. In <see cref="SynchronisationHistoryWriteMode.Inline"/> mode the hand-off writes before it returns; in
/// <see cref="SynchronisationHistoryWriteMode.Overlapped"/> mode it starts the write and returns, and the write is never
/// cancelled with the run: the changes the batch records are already saved, so their history is completed, not
/// abandoned.
/// </para>
/// <para>
/// In the page loops the previous page's write is waited for as the next page hands its own batch off
/// (<see cref="HandOffPageAsync"/>, #2924), so it runs alongside that page's load, processing and saved changes. When that
/// wait finds the previous write failed, the next page's batch, whose changes are saved by then, is still written before
/// the run fails, so one page's fault costs that page's history alone.
/// </para>
/// <para>
/// A failed write is found at the next wait or probe and kept: every later call throws it, so nothing more is written.
/// Found at the flush that handed it off (<see cref="SynchronisationHistoryBarrier.AwaitedFlush"/>), the failure
/// propagates as it is, since the code around it already says what failed; found anywhere else, it is a
/// <see cref="SynchronisationHistoryPersistenceException"/> naming the batch's page, so it is not attributed to wherever
/// the run had got to.
/// </para>
/// </remarks>
internal sealed class SynchronisationHistoryWriter : IAsyncDisposable
{
    private readonly ISyncRepository _runRepository;
    private readonly ISyncRepositoryScope _scope;
    private readonly Activity _activity;
    private readonly string _connectedSystemName;

    private SynchronisationHistoryBatch? _batchInFlight;
    private Task<bool>? _writeInFlight;
    private SynchronisationHistoryPersistenceException? _failure;
    private SynchronisationHistoryBatch? _failedBatch;
    private bool _disposed;
    private long _writeTicks;
    private long _waitTicks;
    private readonly Dictionary<SynchronisationHistoryBarrier, long> _waitTicksByBarrier = [];
    private long _prepareTicks;
    private long _flushTicks;

    /// <param name="runRepository">The run's own repository. The writer opens a repository of its own from it
    /// (<see cref="ISyncRepository.OpenIsolatedScope"/>), which it owns and disposes, and refuses a hand-off while the
    /// run's repository holds a transaction.</param>
    /// <param name="mode">When a hand-off writes.</param>
    /// <param name="activity">The run's Activity, which records a history failure as a warning when the run fails for
    /// another reason.</param>
    /// <param name="connectedSystemName">The Connected System being synchronised, named in a failure.</param>
    public SynchronisationHistoryWriter(ISyncRepository runRepository, SynchronisationHistoryWriteMode mode, Activity activity, string connectedSystemName)
    {
        _runRepository = runRepository;
        _scope = runRepository.OpenIsolatedScope();
        Mode = mode;
        _activity = activity;
        _connectedSystemName = connectedSystemName;
    }

    /// <summary>When a hand-off writes.</summary>
    public SynchronisationHistoryWriteMode Mode { get; }

    /// <summary>The batches written and waited for.</summary>
    public int BatchesWritten { get; private set; }

    /// <summary>The time the writes took, in total.</summary>
    public TimeSpan TotalWriteTime => TimeSpan.FromTicks(Interlocked.Read(ref _writeTicks));

    /// <summary>The time the run spent waiting for writes, in total.</summary>
    public TimeSpan TotalWaitTime => TimeSpan.FromTicks(_waitTicks);

    /// <summary>The time the run spent waiting for writes at one barrier, in total (#2895).</summary>
    /// <param name="barrier">The barrier.</param>
    public TimeSpan WaitTime(SynchronisationHistoryBarrier barrier) =>
        TimeSpan.FromTicks(_waitTicksByBarrier.GetValueOrDefault(barrier));

    /// <summary>The time the run spent preparing history for the writer, in total (#2895).</summary>
    public TimeSpan TotalPrepareTime => TimeSpan.FromTicks(_prepareTicks);

    /// <summary>The time the run spent flushing page history on its own thread, preparation included, in total (#2895).</summary>
    public TimeSpan TotalFlushTime => TimeSpan.FromTicks(_flushTicks);

    /// <summary>
    /// Adds the time the run took to prepare a batch of history to the totals the end-of-run line reports (#2895).
    /// </summary>
    public void RecordPreparation(TimeSpan elapsed) => _prepareTicks += elapsed.Ticks;

    /// <summary>
    /// Adds the time the run took to flush a page's history on its own thread to the totals the end-of-run line
    /// reports (#2895).
    /// </summary>
    public void RecordFlush(TimeSpan elapsed) => _flushTicks += elapsed.Ticks;

    /// <summary>
    /// Writes the run's history totals to the log as the run ends: the batches written, the time writing them, the time
    /// waited for them in all and at each barrier, and the time preparing and flushing them on the run's own thread.
    /// </summary>
    /// <param name="connectedSystemName">The Connected System synchronised.</param>
    public void LogRunTotals(string connectedSystemName)
    {
        if (BatchesWritten == 0)
            return;

        var hidden = TotalWriteTime - TotalWaitTime;
        Log.Information("SynchronisationHistoryWriter: {Mode} history for Connected System {ConnectedSystemName}: {Batches} batches, " +
            "{WriteSeconds:F1}s writing, {WaitSeconds:F1}s waited for ({PageHandOffWaitSeconds:F1}s at page hand-offs, " +
            "{PageLoopExitWaitSeconds:F1}s at page loop exits, {AwaitedFlushWaitSeconds:F1}s at awaited flushes), " +
            "{HiddenSeconds:F1}s hidden behind the run's own work; {PrepareSeconds:F1}s preparing, {FlushSeconds:F1}s flushing pages",
            Mode, LogSanitiser.Sanitise(connectedSystemName), BatchesWritten,
            TotalWriteTime.TotalSeconds, TotalWaitTime.TotalSeconds,
            WaitTime(SynchronisationHistoryBarrier.PageHandOff).TotalSeconds,
            WaitTime(SynchronisationHistoryBarrier.PageLoopExit).TotalSeconds,
            WaitTime(SynchronisationHistoryBarrier.AwaitedFlush).TotalSeconds,
            Math.Max(0, hidden.TotalSeconds), TotalPrepareTime.TotalSeconds, TotalFlushTime.TotalSeconds);
    }

    /// <summary>
    /// Hands a prepared batch to the writer: writes it before returning (<see cref="SynchronisationHistoryWriteMode.Inline"/>)
    /// or starts writing it (<see cref="SynchronisationHistoryWriteMode.Overlapped"/>). A failure is not thrown here; the
    /// next wait or probe finds it.
    /// </summary>
    /// <exception cref="SynchronisationHistoryPersistenceException">An earlier write failed.</exception>
    /// <exception cref="InvalidOperationException">A write is in flight that has not been waited for, or the run's
    /// repository holds a transaction.</exception>
    public async Task HandOffAsync(SynchronisationHistoryBatch batch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
        if (_writeInFlight != null)
            throw new InvalidOperationException(
                "SynchronisationHistoryWriter: a history write is still in flight; wait for it before handing off the next batch.");

        // The write runs on a connection of its own. Waited for while the run's connection holds a transaction, it could
        // wait for ever on rows that transaction has written and not committed (the rows a deletion wrote, say).
        if (_runRepository.HasOpenTransaction)
            throw new InvalidOperationException(
                "SynchronisationHistoryWriter: the run's repository holds a transaction; commit it before handing off history.");

        _batchInFlight = batch;
        if (Mode == SynchronisationHistoryWriteMode.Overlapped)
        {
            // CancellationToken.None: the batch's changes are already saved, so its history must be completed.
            _writeInFlight = Task.Run(() => WriteAsync(batch), CancellationToken.None);
            return;
        }

        _writeInFlight = WriteAsync(batch);
        await ((Task)_writeInFlight).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Hands a page's prepared batch off (#2924): waits for the previous page's write first
    /// (<see cref="SynchronisationHistoryBarrier.PageHandOff"/>), so at most one write is in flight, then starts this one.
    /// The previous page's write has run alongside this page's load, processing and saved changes.
    /// </summary>
    /// <remarks>
    /// When the wait finds the previous page's write failed, this page has already saved its changes, so its history is
    /// still written, here and waited for (one write in flight), unless it refers to rows only the failed write would
    /// have written (<see cref="SynchronisationHistoryBatch.FindDependencyOn"/>). The run then fails with the previous
    /// page's failure as its cause, the message saying what became of this page's history, and its own failure kept beside
    /// it when its write failed too. Either way nothing more is written.
    /// </remarks>
    /// <param name="batch">The page's batch, prepared and released.</param>
    /// <param name="onWritten">Called for each write that has succeeded and been waited for here (the previous page's;
    /// this page's, when written after the previous page's failure), so the caller counts its totals; never for a
    /// failed write.</param>
    /// <exception cref="SynchronisationHistoryPersistenceException">The previous page's write failed, or an earlier
    /// one did.</exception>
    public async Task HandOffPageAsync(SynchronisationHistoryBatch batch, Action<SynchronisationHistoryWriteResult> onWritten)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Not ThrowIfFaulted: a previous write that has failed already, unwaited for, is found by the wait below, so this
        // page's history is still written.
        if (_failure != null)
            throw _failure;

        SynchronisationHistoryWriteResult? previous;
        try
        {
            previous = await WaitAsync(SynchronisationHistoryBarrier.PageHandOff);
        }
        catch (SynchronisationHistoryPersistenceException failure) when (ReferenceEquals(failure, _failure) && _failedBatch != null)
        {
            throw await WriteNextPageAfterFailureAsync(batch, _failedBatch, failure, onWritten);
        }

        if (previous != null)
            onWritten(previous);
        await HandOffAsync(batch);
    }

    /// <summary>
    /// Writes the batch of the page that found the previous page's failure, and replaces the kept failure with one saying
    /// what became of it, which it returns for the caller to throw.
    /// </summary>
    private async Task<SynchronisationHistoryPersistenceException> WriteNextPageAfterFailureAsync(
        SynchronisationHistoryBatch batch,
        SynchronisationHistoryBatch failedBatch,
        SynchronisationHistoryPersistenceException failure,
        Action<SynchronisationHistoryWriteResult> onWritten)
    {
        var outcome = SynchronisationHistoryNextPageOutcome.Written;
        Exception? nextPageFailure = null;
        string? detail = null;

        var dependency = batch.FindDependencyOn(failedBatch);
        if (dependency != null)
        {
            outcome = SynchronisationHistoryNextPageOutcome.NotWrittenDependsOnFailedPage;
            detail = $"{dependency} of page {failedBatch.Page}";
        }
        else
        {
            // Waited for at once, so still one write in flight at most. The run's repository holds no transaction here
            // (the page's changes are committed), so the write cannot wait on the run.
            var write = WriteAsync(batch);
            await ((Task)write).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (write.IsCompletedSuccessfully)
            {
                BatchesWritten++;
                onWritten(new SynchronisationHistoryWriteResult(batch, write.Result));
            }
            else
            {
                outcome = SynchronisationHistoryNextPageOutcome.WriteFailed;
                nextPageFailure = write.IsCanceled
                    ? new OperationCanceledException("The synchronisation history write was cancelled.")
                    : write.Exception!.InnerExceptions.Count == 1 ? write.Exception.InnerExceptions[0] : write.Exception;
                detail = nextPageFailure.Message;
            }
        }

        var message = failure.Message + " " + SynchronisationHistoryPersistenceException.BuildNextPageMessage(batch.Page, batch.TotalPages, outcome, detail);
        _failure = new SynchronisationHistoryPersistenceException(message, failure.InnerException!, failure.Page, failure.TotalPages,
            failure.ConnectedSystemName, batch.Page, outcome, nextPageFailure);

        if (nextPageFailure != null)
            Log.Error(nextPageFailure, "SynchronisationHistoryWriter: {Message}", _failure.Message);
        else
            Log.Error("SynchronisationHistoryWriter: {Message}", _failure.Message);

        return _failure;
    }

    /// <summary>
    /// Waits for the write in flight, if any, and returns it once it has succeeded.
    /// </summary>
    /// <param name="barrier">Where the run is waiting, recorded on the wait's span and deciding how a failure is thrown.</param>
    /// <returns>The completed write, or null when nothing was in flight.</returns>
    /// <exception cref="SynchronisationHistoryPersistenceException">The write failed, or an earlier one did.</exception>
    public async Task<SynchronisationHistoryWriteResult?> WaitAsync(SynchronisationHistoryBarrier barrier)
    {
        if (_failure != null)
            throw _failure;
        if (_writeInFlight == null)
            return null;

        // The run's own writes go on while a write is in flight (#2924). Waited for with the run's connection inside a
        // transaction, the write could wait for ever on rows that transaction holds; every barrier is reached with the
        // run's changes committed, so this is a defect, not a state to wait out.
        if (_runRepository.HasOpenTransaction)
            throw new InvalidOperationException(
                "SynchronisationHistoryWriter: the run's repository holds a transaction; commit it before waiting for history.");

        var batch = _batchInFlight!;
        var write = _writeInFlight;
        using var span = Diagnostics.Sync.StartSpan("SyncHistoryWait")
            .SetTag("barrier", barrier.ToString())
            .SetTag("page", batch.Page)
            .SetTag("writeWasRunning", !write.IsCompleted);
        var stopwatch = Stopwatch.StartNew();
        await ((Task)write).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        stopwatch.Stop();
        _waitTicks += stopwatch.Elapsed.Ticks;
        _waitTicksByBarrier[barrier] = _waitTicksByBarrier.GetValueOrDefault(barrier) + stopwatch.Elapsed.Ticks;
        span.SetTag("waitedMs", stopwatch.Elapsed.TotalMilliseconds);

        _writeInFlight = null;
        _batchInFlight = null;
        if (write.IsCompletedSuccessfully)
        {
            BatchesWritten++;
            span.SetSuccess();
            return new SynchronisationHistoryWriteResult(batch, write.Result);
        }

        var cause = RecordFailure(batch, write);
        span.SetError(cause);
        if (barrier == SynchronisationHistoryBarrier.AwaitedFlush)
            ExceptionDispatchInfo.Capture(cause).Throw();
        throw _failure!;
    }

    /// <summary>
    /// Throws if a write has failed, without waiting for one in flight: for the run to check before it loads more.
    /// </summary>
    /// <exception cref="SynchronisationHistoryPersistenceException">A write has failed.</exception>
    public void ThrowIfFaulted()
    {
        if (_failure == null && _writeInFlight is { IsCompleted: true } write && !write.IsCompletedSuccessfully)
        {
            RecordFailure(_batchInFlight!, write);
            _writeInFlight = null;
            _batchInFlight = null;
        }

        if (_failure != null)
            throw _failure;
    }

    /// <summary>
    /// Waits for whatever is in flight as the run ends, however it ends. With a history failure: when the run is ending
    /// normally or because it was cancelled, the failure is thrown (a run that lost history fails rather than completes or
    /// cancels); when another failure is ending it, that failure is left to propagate, with its detail intact, and the
    /// history failure is recorded on the Activity as a warning and logged, unless that failure is the history failure
    /// itself (found at a barrier, or propagating from an awaited flush, wrapped or not), which is already the error
    /// reported.
    /// </summary>
    /// <param name="primary">The exception ending the run, or null when it is ending normally.</param>
    /// <returns>The write that was in flight, once it has succeeded, so the run can count its totals; null when nothing
    /// was in flight or it failed.</returns>
    /// <exception cref="SynchronisationHistoryPersistenceException">A write failed and nothing else is ending the run, or
    /// only its cancellation is.</exception>
    public async Task<SynchronisationHistoryWriteResult?> DrainOnExitAsync(Exception? primary)
    {
        SynchronisationHistoryWriteResult? drained = null;
        if (_writeInFlight != null)
        {
            var batch = _batchInFlight!;
            var write = _writeInFlight;
            await ((Task)write).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _writeInFlight = null;
            _batchInFlight = null;
            if (write.IsCompletedSuccessfully)
            {
                BatchesWritten++;
                drained = new SynchronisationHistoryWriteResult(batch, write.Result);
            }
            else
            {
                RecordFailure(batch, write);
            }
        }

        if (_failure == null)
            return drained;

        if (primary == null || primary is OperationCanceledException)
            throw _failure;

        if (IsOrWraps(primary, _failure) || IsOrWraps(primary, _failure.InnerException))
            return drained;

        var warning = _failure.Message;
        _activity.WarningMessage = string.IsNullOrEmpty(_activity.WarningMessage) ? warning : $"{_activity.WarningMessage}\n{warning}";
        Log.Error(_failure.InnerException, "SynchronisationHistoryWriter: {Message} The run is failing for another reason, which is the error reported: {Primary}",
            warning, LogSanitiser.Sanitise(primary.Message));
        return drained;
    }

    /// <summary>
    /// Whether the exception is the one sought, or carries it somewhere in its chain of inner exceptions.
    /// </summary>
    private static bool IsOrWraps(Exception exception, Exception? sought)
    {
        if (sought == null)
            return false;

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (ReferenceEquals(current, sought))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Waits for the write in flight, then releases the repository the writes ran on.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_writeInFlight != null)
        {
            var batch = _batchInFlight!;
            var write = _writeInFlight;
            await ((Task)write).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!write.IsCompletedSuccessfully && _failure == null)
            {
                // Nothing waited for it, so this is the only place it can be told.
                RecordFailure(batch, write);
            }
        }

        _scope.Dispose();
    }

    private async Task<bool> WriteAsync(SynchronisationHistoryBatch batch)
    {
        using var span = Diagnostics.Sync.StartSpan("SyncHistoryWrite")
            .SetTag("page", batch.Page)
            .SetTag("mode", Mode.ToString())
            .SetTag("rpeiCount", batch.Items.Count)
            .SetTag("outcomeCount", batch.Outcomes.Count)
            .SetTag("csoChangeCount", batch.OutcomeChangeRecords.Count + batch.ItemChangeRecords.Count)
            .SetTag("mvoChangeCount", batch.MetaverseObjectChanges.Count + batch.MetaverseObjectChangeAppends.Count);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var released = await _scope.Repository.WriteSynchronisationHistoryAsync(batch);
            span.SetSuccess();
            return released;
        }
        finally
        {
            Interlocked.Add(ref _writeTicks, stopwatch.Elapsed.Ticks);
        }
    }

    /// <summary>
    /// Keeps a failed write's failure, naming its batch, and returns what caused it.
    /// </summary>
    private Exception RecordFailure(SynchronisationHistoryBatch batch, Task<bool> write)
    {
        var cause = write.IsCanceled
            ? new OperationCanceledException("The synchronisation history write was cancelled.")
            : write.Exception!.InnerExceptions.Count == 1 ? write.Exception.InnerExceptions[0] : write.Exception;
        _failedBatch = batch;
        _failure = new SynchronisationHistoryPersistenceException(
            SynchronisationHistoryPersistenceException.BuildMessage(batch.Page, batch.TotalPages, _connectedSystemName),
            cause, batch.Page, batch.TotalPages, _connectedSystemName);
        Log.Error(cause, "SynchronisationHistoryWriter: {Message}", _failure.Message);
        return cause;
    }
}
