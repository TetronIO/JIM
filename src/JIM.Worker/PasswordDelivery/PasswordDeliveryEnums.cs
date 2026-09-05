// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.PasswordDelivery;

/// <summary>
/// How a lane ended, as far as the scheduler needs to know.
/// </summary>
internal enum PasswordDeliveryLaneOutcome
{
    /// <summary>
    /// The lane attempted what it claimed (or found nothing to claim). Whatever is left is retrying on its own
    /// clock or done, so the system is dispatched again whenever it is next due.
    /// </summary>
    Completed,

    /// <summary>
    /// The lane could not deliver at all and gave its claims back unattempted. The rows are due again at once,
    /// and would be claimed again at once; the scheduler holds the system off until the next safety poll, or a
    /// notification for it, whichever is sooner.
    /// </summary>
    CouldNotDeliver
}
