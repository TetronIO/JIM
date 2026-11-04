// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// A Run Profile's enabled state as it is stored, read on its own so an update can be judged against what is saved
/// rather than against the copy the caller edited.
/// </summary>
/// <param name="Enabled">Whether the stored Run Profile is enabled.</param>
/// <param name="DisabledReason">Why it is disabled; null while it is enabled.</param>
/// <param name="DisabledPartitionName">The removed Partition's name, for a Run Profile a hierarchy refresh disabled.</param>
public sealed record ConnectedSystemRunProfileEnabledState(
    bool Enabled,
    ConnectedSystemRunProfileDisabledReason? DisabledReason,
    string? DisabledPartitionName);
