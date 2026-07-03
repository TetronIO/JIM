// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// A single row in a configuration object's change-history list: who changed it, when, why, a one-line summary of what
/// changed, and the full structured diff against the previous version (computed once server-side and carried inline so
/// the UI renders it without a second round-trip). The full snapshot is still retrieved separately on demand.
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

    /// <summary>
    /// The structured diff of this version against its predecessor. For the first version (no predecessor) it shows the
    /// whole object as created. Null only when the version carries no deserialisable snapshot.
    /// </summary>
    public ConfigurationDiff? Diff { get; set; }
}
