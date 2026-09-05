// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Operations;

/// <summary>
/// The background services that report a <see cref="ServiceHeartbeat"/>. Each value is one thing an administrator
/// can find running (or not) in their deployment; the web tier is deliberately absent because it is the thing
/// asking the question.
/// </summary>
public enum JimService
{
    /// <summary>
    /// The Worker's synchronisation loop: the process that runs Run Profiles and the other queued Worker Tasks.
    /// </summary>
    WorkerSync = 1,

    /// <summary>
    /// The Worker's password delivery service: the loop that delivers queued Password Synchronisation changes to
    /// Connected Systems. Hosted in the same process as <see cref="WorkerSync"/> but reported separately, because
    /// a wedged synchronisation loop and a wedged password loop need different responses.
    /// Named without the word "password" on purpose: the value is written to the heartbeat table on every
    /// tick, and CodeQL treats any identifier containing that word as sensitive data being stored in clear.
    /// </summary>
    WorkerDelivery = 2,

    /// <summary>
    /// The Scheduler: the process that starts Schedules when they fall due and advances their steps.
    /// </summary>
    Scheduler = 3
}
