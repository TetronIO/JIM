// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.ExampleData.DTOs;

/// <summary>
/// Per-batch progress payload reported by the example-data persistence loop.
/// Carries enough context for callers to render a moving "what's happening" message in the
/// Activity UI: which batch we're on, how many objects have been written so far, and how
/// long the persistence phase has been running, so the server layer can derive an ETA.
/// </summary>
public class PersistenceProgress
{
    /// <summary>
    /// Total number of objects that need to be persisted across the whole operation.
    /// </summary>
    public int TotalObjects { get; init; }

    /// <summary>
    /// Cumulative number of objects persisted so far, including the batch that just completed.
    /// </summary>
    public int ObjectsPersisted { get; init; }

    /// <summary>
    /// 1-based index of the batch that just completed.
    /// </summary>
    public int BatchIndex { get; init; }

    /// <summary>
    /// Total number of batches the operation will be split across.
    /// </summary>
    public int BatchCount { get; init; }

    /// <summary>
    /// Wall-clock time spent in the persistence phase up to the end of the current batch.
    /// Callers can divide by <see cref="ObjectsPersisted"/> to derive an ETA for the remainder.
    /// </summary>
    public TimeSpan Elapsed { get; init; }
}
