// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Models.Transactional;

namespace JIM.Application.Servers;

/// <summary>
/// The one gate on an export run's own database context while its batches run in parallel (#2895): only one batch at a
/// time uses that context, to take the next batch, report progress and so on. A batch's execution items and their change
/// history are written on the batch's own context, outside the gate (#2902). Every pass through it is timed, as a span
/// for the wait and a span for the hold, each tagged with its purpose,
/// and added to the run's totals (<see cref="ExportContextGateTimings"/>), because the gate is serial across workers and
/// so caps what more workers can gain.
/// </summary>
internal sealed class ExportContextGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ExportContextGateTimings _timings;

    /// <param name="timings">The run's totals, which every pass through the gate is added to.</param>
    public ExportContextGate(ExportContextGateTimings timings)
    {
        _timings = timings;
    }

    /// <summary>
    /// Waits for the gate, runs the work holding it, and releases it however the work ends. A pass whose wait is
    /// cancelled never held the gate, and is not counted.
    /// </summary>
    /// <param name="purpose">Why the gate is taken, tagged on the spans and the key of the run's totals.</param>
    /// <param name="action">The work to run holding the gate.</param>
    /// <param name="cancellationToken">Cancels the wait, not the work.</param>
    public async Task<T> RunAsync<T>(ExportContextGatePurpose purpose, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var purposeName = purpose.ToString();
        var waitStopwatch = Stopwatch.StartNew();
        using (var waitSpan = Diagnostics.Diagnostics.Sync.StartSpan("ExportContextGateWait").SetTag("purpose", purposeName))
        {
            await _semaphore.WaitAsync(cancellationToken);
            waitSpan.SetSuccess();
        }
        var waited = waitStopwatch.Elapsed;

        var heldStopwatch = Stopwatch.StartNew();
        try
        {
            using var heldSpan = Diagnostics.Diagnostics.Sync.StartSpan("ExportContextGateHeld").SetTag("purpose", purposeName);
            var answer = await action();
            heldSpan.SetSuccess();
            return answer;
        }
        finally
        {
            _semaphore.Release();
            _timings.Record(purpose, waited, heldStopwatch.Elapsed);
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
