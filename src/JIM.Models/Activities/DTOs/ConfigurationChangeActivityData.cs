// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// A data-tier projection of a configuration-change Activity, including the raw snapshot JSON. Used internally by the
/// application layer to build change-history headers and diffs; not returned directly to callers.
/// </summary>
public class ConfigurationChangeActivityData
{
    public Guid ActivityId { get; set; }

    public int Version { get; set; }

    public ActivityTargetOperationType Operation { get; set; }

    public ActivityInitiatorType InitiatedByType { get; set; }

    public string? InitiatedByName { get; set; }

    public DateTime When { get; set; }

    public string? Reason { get; set; }

    /// <summary>The stored configuration snapshot document (jsonb), deserialised by the application layer.</summary>
    public string? SnapshotJson { get; set; }
}
