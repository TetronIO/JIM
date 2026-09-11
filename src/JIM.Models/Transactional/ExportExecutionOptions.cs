// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// Options for controlling export execution behaviour.
/// </summary>
public class ExportExecutionOptions
{
    /// <summary>
    /// Number of exports to process in each batch.
    /// Default is 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of batches to process concurrently.
    /// Each parallel batch gets its own DbContext and connector instance.
    /// Default is 1 (sequential processing). Set higher to enable parallel batch export.
    /// </summary>
    public int MaxParallelism { get; set; } = 1;

    /// <summary>
    /// Run Profile Safeguards (#1618): the maximum number of creates this run may attempt.
    /// Null means no limit. Copied from the Run Profile's <see cref="Staging.ConnectedSystemRunProfile.MaxCreates"/>.
    /// </summary>
    public int? MaxCreates { get; set; }

    /// <summary>
    /// Run Profile Safeguards (#1618): the maximum number of updates this run may attempt.
    /// Null means no limit. Copied from the Run Profile's <see cref="Staging.ConnectedSystemRunProfile.MaxUpdates"/>.
    /// </summary>
    public int? MaxUpdates { get; set; }

    /// <summary>
    /// Run Profile Safeguards (#1618): the maximum number of deletes this run may attempt.
    /// Null means no limit. Copied from the Run Profile's <see cref="Staging.ConnectedSystemRunProfile.MaxDeletes"/>.
    /// </summary>
    public int? MaxDeletes { get; set; }
}
