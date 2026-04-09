// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

public enum ActivityStatus
{
    NotSet = 0,
    InProgress = 1,
    Complete = 2,
    CompleteWithWarning = 3,
    CompleteWithError = 4,
    FailedWithError = 5,
    Cancelled = 6
}
