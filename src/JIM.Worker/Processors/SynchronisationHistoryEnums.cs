// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Processors;

/// <summary>
/// When a <see cref="SynchronisationHistoryWriter"/> runs a write it is handed (#2874). Both modes run the same
/// preparation and the same write, on the same connection; only when the run waits for it differs.
/// </summary>
public enum SynchronisationHistoryWriteMode
{
    /// <summary>
    /// The hand-off writes before it returns. The in-memory store needs it, its collections not being safe to use from
    /// two threads.
    /// </summary>
    Inline,

    /// <summary>
    /// The hand-off starts the write and returns; the run waits for it at the next barrier.
    /// </summary>
    Overlapped
}

/// <summary>
/// Where a run waits for the history write in flight (#2874), recorded on the wait's span so a run's timings show which
/// waits cost it time.
/// </summary>
public enum SynchronisationHistoryBarrier
{
    /// <summary>
    /// As a page hands its history off (#2924): the previous page's write, which ran alongside this page's load,
    /// processing and saved changes, is waited for first, so at most one write is in flight.
    /// </summary>
    PageHandOff,

    /// <summary>
    /// After the page loop, before the steps that follow it, which read what the pages recorded.
    /// </summary>
    PageLoopExit,

    /// <summary>
    /// A flush outside the page loops, handed off and waited for at once. A failure surfaces where the flush ran, so it
    /// propagates as it is rather than being attributed to a page.
    /// </summary>
    AwaitedFlush,

    /// <summary>
    /// As the run ends, however it ends.
    /// </summary>
    RunExit
}
