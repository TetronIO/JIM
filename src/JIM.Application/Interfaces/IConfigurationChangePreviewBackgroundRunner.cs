// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Application.Interfaces;

/// <summary>
/// Runs a small configuration change preview in the host's own process, in the background, instead of queuing it
/// for JIM.Worker (#827). Implemented by JIM.Web; the application layer declares it because the dispatch decision
/// belongs beside the cost estimate that drives it, not in the caller.
///
/// The point is latency, not capacity. Most previews concern a handful of objects and finish in well under a
/// second; making one wait for the worker's next poll would put a visible pause in front of the common case for no
/// benefit. Both paths run the same orchestration and write the same rows, so nothing downstream can tell them
/// apart.
/// </summary>
public interface IConfigurationChangePreviewBackgroundRunner
{
    /// <summary>
    /// Queues a preview to run in this process. Returns immediately; progress and results reach the caller through
    /// the preview's Activity, exactly as they do for the worker path.
    /// </summary>
    void Enqueue(Guid activityId, ConfigurationChangePreviewRequest request);

    /// <summary>
    /// Cancels a preview running in this process. Returns false when this process is not running it, which is the
    /// normal answer for a preview that went to JIM.Worker; the caller then cancels it through the worker task.
    /// </summary>
    bool Cancel(Guid activityId);
}
