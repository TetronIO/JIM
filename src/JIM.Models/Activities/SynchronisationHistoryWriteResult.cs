// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// A synchronisation history write that has completed and been waited for (#2874).
/// </summary>
/// <param name="Batch">The batch written.</param>
/// <param name="ItemsWrittenOutsideTracking">Whether the store wrote the items outside any change tracking, so the run
/// releases them; false when it keeps them on the Activity (the in-memory store, for test assertions).</param>
public sealed record SynchronisationHistoryWriteResult(SynchronisationHistoryBatch Batch, bool ItemsWrittenOutsideTracking);
