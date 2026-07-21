// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// Identifies a single <see cref="ActivityStatCounter"/> row: the counted Activity, the stat
/// dimension, and the dimension-specific key. Used as the dictionary key when calculating and
/// upserting counter deltas from a persistence batch.
/// </summary>
public readonly record struct ActivityStatCounterKey(Guid ActivityId, ActivityStatDimension Dimension, string Key);
