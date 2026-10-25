// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Operations;

namespace JIM.Worker;

/// <summary>
/// Decides, from successive memory readings, when the Worker warns about, stops, or gives up on its running work.
/// The operating system stops a process that runs its host (or container) out of memory without a word, part-way
/// through whatever it was doing; the guard acts first so the reason reaches the Activity and the logs.
/// </summary>
/// <remarks>
/// The thresholds are <see cref="MemoryPressure"/>'s, shared with the service health report. A warning goes out at
/// <see cref="MemoryPressure.WarningShare"/> whoever holds the memory, since the run is at risk either way. A stop
/// needs the Worker to hold most of what is in use (<see cref="MemoryPressure.DominantShare"/>): when something else
/// is filling memory, stopping a synchronisation frees nothing, and the operating system would not pick the Worker.
/// After a stop, the guard waits for the tasks to end; if memory is still at the stop share once
/// <see cref="StopGrace"/> has passed, or reaches <see cref="MemoryPressure.AbortShare"/> sooner (a task blocked
/// inside one call cannot see its cancellation), it aborts. State lives only as long as some task is running.
/// </remarks>
internal sealed class WorkerMemoryGuard
{
    /// <summary>How long stopped tasks are given to end before the guard gives up on them.</summary>
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(30);

    private bool _warned;
    private DateTime? _stoppedAt;

    internal MemoryGuardAction Evaluate(MemoryReading reading, bool tasksRunning, DateTime now)
    {
        if (!tasksRunning)
        {
            _warned = false;
            _stoppedAt = null;
            return MemoryGuardAction.None;
        }

        if (reading.UsedFraction is not { } share)
            return MemoryGuardAction.None;

        var workerHoldsMost = MemoryPressure.ProcessIsDominant(reading);

        if (_stoppedAt is { } stoppedAt)
        {
            var stopNotTakingEffect = share >= MemoryPressure.AbortShare
                                      || (now - stoppedAt >= StopGrace && share >= MemoryPressure.StopShare);
            return workerHoldsMost && stopNotTakingEffect ? MemoryGuardAction.Abort : MemoryGuardAction.None;
        }

        if (share >= MemoryPressure.StopShare && workerHoldsMost)
        {
            _stoppedAt = now;
            return MemoryGuardAction.Stop;
        }

        if (share >= MemoryPressure.WarningShare && !_warned)
        {
            _warned = true;
            return MemoryGuardAction.Warn;
        }

        return MemoryGuardAction.None;
    }
}
