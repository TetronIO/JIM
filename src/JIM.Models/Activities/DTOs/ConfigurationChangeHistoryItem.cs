// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// A single row in a configuration object's change-history list: who changed it, when, why, and a one-line summary of
/// what changed (computed by diffing against the previous version). The full snapshot and diff are retrieved separately.
/// </summary>
public class ConfigurationChangeHistoryItem
{
    public Guid ActivityId { get; set; }

    public int Version { get; set; }

    public ActivityTargetOperationType Operation { get; set; }

    public ActivityInitiatorType InitiatedByType { get; set; }

    /// <summary>The security principal's id, where one applies (User / API key); null for System or unattributed changes.</summary>
    public Guid? InitiatedById { get; set; }

    public string? InitiatedByName { get; set; }

    public DateTime When { get; set; }

    public string? Reason { get; set; }

    /// <summary>A short, human-readable description of what changed in this version, e.g. "Scope, Attribute Flow".</summary>
    public string Summary { get; set; } = null!;
}
