// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// Thrown by the preview path's read-only repository facade when anything attempts a write during a preview
/// (#288, PRD requirement 8). A preview must never persist; this exception converts an orchestration bug that
/// reaches for a write into a loud failure instead of a silent commit, and catching it to continue a preview
/// is never correct: the preview is broken and must fail.
/// </summary>
public class PreviewWriteAttemptedException(string memberName)
    : InvalidOperationException($"A synchronisation preview attempted a repository write via {memberName}. " +
        "Previews must never persist; this indicates a defect in the preview orchestration.")
{
    /// <summary>
    /// The repository member the preview attempted to call.
    /// </summary>
    public string MemberName { get; } = memberName;
}
