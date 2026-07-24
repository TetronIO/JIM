// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using JIM.Web.Models;

namespace JIM.Web.Services;

/// <summary>
/// Default <see cref="IActivityEtaTracker"/>: keeps a small, bounded window of progress samples
/// per Activity and computes the rate over that window, so the estimate reflects recent
/// throughput rather than the whole run (run phases differ wildly in speed). Thread-safe; all
/// time is read from the injected <see cref="TimeProvider"/> for testability.
/// </summary>
public sealed class ActivityEtaTracker(TimeProvider timeProvider, int maxTrackedActivities = ActivityEtaTracker.DefaultMaxTrackedActivities) : IActivityEtaTracker
{
    /// <summary>
    /// Upper bound on concurrently tracked Activities; a safety net against unbounded growth if
    /// consumers never call <see cref="Remove"/> (for example an API poller that stops polling).
    /// </summary>
    public const int DefaultMaxTrackedActivities = 512;

    /// <summary>
    /// Samples older than this are trimmed (while at least two remain) so the rate tracks recent
    /// throughput.
    /// </summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(2);

    private const int MaxSamplesPerActivity = 30;

    private readonly ConcurrentDictionary<Guid, ActivitySampleWindow> _windows = new();

    /// <inheritdoc />
    public ActivityEtaEstimate RecordSample(Guid activityId, int objectsProcessed, int objectsToProcess)
    {
        var now = timeProvider.GetUtcNow();
        var window = _windows.GetOrAdd(activityId, _ => new ActivitySampleWindow());
        var estimate = window.RecordSample(now, objectsProcessed, objectsToProcess);
        EvictOverCap(activityId);
        return estimate;
    }

    /// <inheritdoc />
    public void Remove(Guid activityId)
    {
        _windows.TryRemove(activityId, out _);
    }

    /// <summary>
    /// Evicts the least-recently-touched windows while over the cap, never evicting the Activity
    /// that was just touched.
    /// </summary>
    private void EvictOverCap(Guid justTouched)
    {
        while (_windows.Count > maxTrackedActivities)
        {
            var lruCandidates = _windows
                .Where(w => w.Key != justTouched)
                .OrderBy(w => w.Value.LastTouched)
                .Take(1)
                .ToList();
            if (lruCandidates.Count == 0)
                return;
            _windows.TryRemove(lruCandidates[0].Key, out _);
        }
    }

    private sealed class ActivitySampleWindow
    {
        private readonly Lock _lock = new();
        private readonly Queue<(DateTimeOffset Time, int Processed)> _samples = new();
        private int _lastProcessed;
        private int _lastTotal;

        /// <summary>
        /// Read without the lock by the eviction sweep; stale reads only make LRU ordering
        /// approximate, which is acceptable for a safety-net eviction.
        /// </summary>
        public DateTimeOffset LastTouched { get; private set; }

        public ActivityEtaEstimate RecordSample(DateTimeOffset now, int objectsProcessed, int objectsToProcess)
        {
            lock (_lock)
            {
                LastTouched = now;

                // A processed-count decrease or total change means the run moved to a new phase
                // that reuses the counters; the old samples describe a different workload.
                if (_samples.Count > 0 && (objectsProcessed < _lastProcessed || objectsToProcess != _lastTotal))
                    _samples.Clear();

                _lastProcessed = objectsProcessed;
                _lastTotal = objectsToProcess;
                _samples.Enqueue((now, objectsProcessed));

                // Trim by count, then by age; always retain at least two samples so slow phases
                // (sampling less often than the window) still produce an estimate.
                while (_samples.Count > MaxSamplesPerActivity)
                    _samples.Dequeue();
                while (_samples.Count > 2 && now - _samples.Peek().Time > RateWindow)
                    _samples.Dequeue();

                if (_samples.Count < 2)
                    return new ActivityEtaEstimate(null, null);

                var oldest = _samples.Peek();
                var elapsedSeconds = (now - oldest.Time).TotalSeconds;
                if (elapsedSeconds <= 0)
                    return new ActivityEtaEstimate(null, null);

                var objectsPerSecond = (objectsProcessed - oldest.Processed) / elapsedSeconds;
                double? estimatedSecondsRemaining = null;
                if (objectsPerSecond > 0 && objectsToProcess > 0 && objectsToProcess >= objectsProcessed)
                    estimatedSecondsRemaining = (objectsToProcess - objectsProcessed) / objectsPerSecond;

                return new ActivityEtaEstimate(objectsPerSecond, estimatedSecondsRemaining);
            }
        }
    }
}
