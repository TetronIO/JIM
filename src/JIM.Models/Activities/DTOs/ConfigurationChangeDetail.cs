// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// A single configuration change in full: its metadata, the complete redacted snapshot of the object at that version,
/// and the structured diff against the immediately preceding version.
/// </summary>
public class ConfigurationChangeDetail
{
    public Guid ActivityId { get; set; }

    public int Version { get; set; }

    public ActivityTargetOperationType Operation { get; set; }

    public ActivityInitiatorType InitiatedByType { get; set; }

    public string? InitiatedByName { get; set; }

    public DateTime When { get; set; }

    public string? Reason { get; set; }

    /// <summary>The complete, redacted snapshot of the object at this version.</summary>
    public ConfigurationSnapshot Snapshot { get; set; } = null!;

    /// <summary>The structured diff against the immediately preceding version (every node added, for the first version).</summary>
    public ConfigurationDiff Diff { get; set; } = null!;
}
