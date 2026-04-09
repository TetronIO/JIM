// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Worker.Models;

/// <summary>
/// Exception thrown when a join operation fails during sync processing.
/// Carries the error type and message for recording in the activity execution item.
/// </summary>
public class SyncJoinException : Exception
{
    /// <summary>
    /// The type of error that occurred during the join attempt.
    /// </summary>
    public ActivityRunProfileExecutionItemErrorType ErrorType { get; }

    public SyncJoinException(ActivityRunProfileExecutionItemErrorType errorType, string message)
        : base(message)
    {
        ErrorType = errorType;
    }
}
