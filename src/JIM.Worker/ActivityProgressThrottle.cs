// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Utilities;
using Log = Serilog.Log;
using ILogger = Serilog.ILogger;

namespace JIM.Worker;

/// <summary>
/// Paces a run's progress writes by time rather than by how often the run reports (#2521). While it is open, the Activity's
/// message, its counters and its steps are recorded in memory as the run reports them, and written at most once per
/// <see cref="DefaultInterval"/>, latest state first: at once when the last write was an interval or more ago, otherwise
/// once the interval has passed. Closing it writes whatever is left, so what an administrator sees is exactly where the run
/// got to however the stretch it covers ends.
/// </summary>
/// <remarks>
/// <para>
/// A Connector handing over a page at a time had the run write its progress several times per page, each a commit: about
/// 31,000 more writes for a 500,000-object Full Import in pages of 100 than for the same import in one page. Open one around
/// such a stretch; outside it, progress is written as it is reported.
/// </para>
/// <para>
/// Where the repository has a progress writer of its own (<see cref="ISyncRepository.CreateActivityProgressWriter"/>), the
/// write once the interval has passed comes from a timer, so a long Connector call or a long stretch of the run's own work
/// still shows what was last reported within the interval. Every write then goes through that writer, one at a time, and
/// each takes the state as it stands when it starts, so no write can overtake a later one. Without one nothing may write
/// from a timer (the repository's own connection may be busy), so the run's next report after the interval writes instead.
/// </para>
/// <para>
/// Progress is narration: a failed write is logged, never thrown, and what it was writing is carried by the next write.
/// </para>
/// </remarks>
public sealed class ActivityProgressThrottle : IAsyncDisposable
{
    /// <summary>
    /// The most often progress is written, and so the furthest what an administrator sees can trail the run.
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly ISyncRepository _syncRepo;
    private readonly Activity _activity;
    private readonly ActivityPhaseReporter _phases;
    private readonly IActivityProgressWriter? _writer;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly ITimer? _timer;

    /// <summary>One write at a time: each takes the state as it stands when it starts.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private readonly object _sync = new();
    private DateTimeOffset? _lastWriteAt;
    private bool _unwritten;
    private bool _timerArmed;
    private bool _closed;
    private int _writesMade;
    private int _reports;
    private readonly DateTimeOffset _openedAt;

    /// <param name="syncRepo">The run's repository; asked for a progress writer of its own, and written through without one.</param>
    /// <param name="activity">The run's Activity, whose message and counters are written.</param>
    /// <param name="phases">The run's steps; their changes are recorded here, not written, while this is open.</param>
    /// <param name="time">The clock writes are paced by; null takes the system clock. Tests drive it.</param>
    /// <param name="interval">The most often progress is written; null takes <see cref="DefaultInterval"/>.</param>
    /// <param name="logger">Where failed writes and the summary are logged; null takes the ambient logger.</param>
    public ActivityProgressThrottle(ISyncRepository syncRepo, Activity activity, ActivityPhaseReporter phases,
        TimeProvider? time = null, TimeSpan? interval = null, ILogger? logger = null)
    {
        _syncRepo = syncRepo;
        _activity = activity;
        _phases = phases;
        _time = time ?? TimeProvider.System;
        _interval = interval ?? DefaultInterval;
        _logger = logger ?? Log.ForContext<ActivityProgressThrottle>();
        _writer = syncRepo.CreateActivityProgressWriter();
        _openedAt = _time.GetUtcNow();

        if (_writer != null)
            _timer = _time.CreateTimer(_ => _ = WriteOnTimerAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _phases.BeginCoalescing(this);
    }

    /// <summary>How many writes this has made, for the summary it logs on closing.</summary>
    public int WritesMade => Volatile.Read(ref _writesMade);

    /// <summary>
    /// Records the run's progress: the message given, if any, and the Activity's counters as they now stand. Writes at once
    /// when the last write was an interval or more ago; otherwise the write waits for the interval.
    /// </summary>
    /// <param name="message">The Activity's new message, or null to keep the one it has.</param>
    public Task ReportAsync(string? message = null)
    {
        if (message != null)
            _activity.Message = message;

        return NoteChangeAsync();
    }

    /// <summary>
    /// Called by <see cref="ActivityPhaseReporter"/> once it has recorded a step change, and by <see cref="ReportAsync"/>.
    /// </summary>
    internal async Task NoteChangeAsync()
    {
        bool writeNow;
        lock (_sync)
        {
            if (_closed)
                return;

            _reports++;
            _unwritten = true;
            var now = _time.GetUtcNow();
            writeNow = _lastWriteAt is not { } last || now - last >= _interval;
            if (!writeNow)
                ArmTimer(now);
        }

        if (writeNow)
            await WriteUnwrittenAsync();
    }

    /// <summary>
    /// Writes whatever has not been written, at once. For a moment an administrator must see as it is.
    /// </summary>
    public Task FlushAsync() => WriteUnwrittenAsync();

    /// <summary>
    /// Writes whatever has not been written and stops writing: step changes after this are written as they happen again.
    /// Waits for a write already under way, so nothing this throttle writes can land after what the run writes next.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_closed)
                return;
        }

        _timer?.Dispose();
        await _writeGate.WaitAsync();
        try
        {
            await WriteUnwrittenCoreAsync();

            // The last chance to write what the stretch reached: if the writer of its own failed, the run's own connection
            // is free now, and nothing after this would write the steps that changed only within the stretch.
            if (_writer != null && _unwritten)
                await WriteUnwrittenCoreAsync(throughRepository: true);

            lock (_sync)
                _closed = true;
            _phases.EndCoalescing(this);
        }
        finally
        {
            _writeGate.Release();
        }

        _logger.Information("ActivityProgressThrottle: Wrote the progress of Activity {ActivityId} {Writes:N0} time(s) for {Reports:N0} report(s) over {Elapsed}",
            _activity.Id, WritesMade, _reports, _time.GetUtcNow() - _openedAt);
    }

    private void ArmTimer(DateTimeOffset now)
    {
        if (_timer == null || _timerArmed || _lastWriteAt is not { } last)
            return;

        var due = last + _interval - now;
        _timer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
        _timerArmed = true;
    }

    private async Task WriteOnTimerAsync()
    {
        lock (_sync)
            _timerArmed = false;

        try
        {
            await WriteUnwrittenAsync();
        }
        catch (Exception ex)
        {
            // Nothing awaits a timer's write, so nothing may escape it.
            _logger.Warning(ex, "ActivityProgressThrottle: A timed write of Activity {ActivityId}'s progress failed. The run continues", _activity.Id);
        }
    }

    private async Task WriteUnwrittenAsync()
    {
        await _writeGate.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (_closed)
                    return;
            }

            await WriteUnwrittenCoreAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Writes the state as it stands, if anything is unwritten. Called holding <see cref="_writeGate"/>.
    /// </summary>
    /// <param name="throughRepository">Write through the run's repository even where there is a writer of its own; only
    /// from the run's own flow, never from the timer.</param>
    private async Task WriteUnwrittenCoreAsync(bool throughRepository = false)
    {
        IReadOnlyList<ActivityPhase> phaseChanges;
        ActivityProgressWrite progress;
        lock (_sync)
        {
            if (!_unwritten)
                return;

            _unwritten = false;
            _lastWriteAt = _time.GetUtcNow();
            var (changed, copies) = _phases.TakeUnwrittenPhaseChanges();
            phaseChanges = changed;
            progress = new ActivityProgressWrite(_activity.Id, _activity.Message, _activity.ObjectsProcessed, _activity.ObjectsToProcess, copies);
        }

        try
        {
            if (_writer != null && !throughRepository)
            {
                await _writer.WriteAsync(progress);
            }
            else
            {
                await _syncRepo.UpdateActivityMessageAsync(_activity, progress.Message ?? string.Empty);
                if (progress.Phases.Count > 0)
                    await _syncRepo.SaveActivityPhasesAsync(progress.Phases);
            }

            Interlocked.Increment(ref _writesMade);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Losing a write is a cosmetic loss; failing the run over it would not be. What it was writing is carried by the
            // next write.
            _phases.ReturnUnwrittenPhaseChanges(phaseChanges);
            lock (_sync)
            {
                _unwritten = true;
                ArmTimer(_time.GetUtcNow());
            }

            _logger.Warning(ex, "ActivityProgressThrottle: Failed to write Activity {ActivityId}'s progress ({Message}). The run continues",
                _activity.Id, LogSanitiser.Sanitise(progress.Message));
        }
    }
}
