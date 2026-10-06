// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What Collision Remediation did with one rejected export (Unique Value Generation, #242, release 4), carried on its
/// <see cref="ProcessedExportItem"/> so the export run records it: a <c>GeneratedValueRemediated</c> outcome for a
/// remediated value, or a <c>GeneratedValueCollisionUnresolved</c> error for one that needs a decision. Absent when the
/// rejection was reported as an ordinary export error.
/// </summary>
public sealed class GeneratedValueCollisionOutcome
{
    public required GeneratedValueCollisionHandling Handling { get; init; }

    /// <summary>
    /// The generated attribute's name: the Metaverse attribute in import mode, the Connected System attribute in
    /// export mode.
    /// </summary>
    public required string AttributeName { get; init; }

    /// <summary>
    /// The value the Connected System rejected.
    /// </summary>
    public required string RejectedValue { get; init; }

    /// <summary>
    /// The value Collision Remediation issued in its place; null when the assignment needs a decision.
    /// </summary>
    public string? NewValue { get; init; }

    /// <summary>
    /// One plain sentence describing what happened, for the execution item: the error message when a decision is
    /// needed, the correction otherwise.
    /// </summary>
    public required string Message { get; init; }
}
