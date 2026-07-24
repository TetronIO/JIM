// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

public static class ActivityStatusExtensions
{
    /// <summary>
    /// True when the status is terminal: the Activity has finished (successfully or otherwise)
    /// and will not change again.
    /// </summary>
    public static bool IsTerminal(this ActivityStatus status) => status is
        ActivityStatus.Complete
        or ActivityStatus.CompleteWithWarning
        or ActivityStatus.CompleteWithError
        or ActivityStatus.FailedWithError
        or ActivityStatus.Cancelled;
}
