// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// How many of each mark a guarded clear (<see cref="ConnectedSystemObjectMarkClear"/>) actually cleared. A requested
/// clear that did not apply was skipped because the row had been written since the synchronisation loaded it.
/// </summary>
/// <param name="SynchronisationPendingCleared">Objects whose synchronisation-pending mark was cleared.</param>
/// <param name="DerivedInputChangePendingCleared">Objects whose derived-input mark was cleared.</param>
public readonly record struct ConnectedSystemObjectMarkClearResult(int SynchronisationPendingCleared, int DerivedInputChangePendingCleared);
