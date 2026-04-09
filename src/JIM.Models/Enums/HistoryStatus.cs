// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Enums;

public enum HistoryStatus
{
    NotSet = 0,
    InProgress = 1,
    Complete = 2,
    CompleteWithError = 3,
    FailedWithError = 4
}