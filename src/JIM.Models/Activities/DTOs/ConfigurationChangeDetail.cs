// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// A single configuration change in full: its metadata, the complete redacted snapshot of the object at that version,
/// and the structured diff against the immediately preceding version.
/// </summary>
public class ConfigurationChangeDetail
{
    /// <summary>
    /// The Activity that recorded the change.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// The object's version this change produced, counting from 1 for its creation.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// What the change did: Create, Update or Delete.
    /// </summary>
    public ActivityTargetOperationType Operation { get; set; }

    /// <summary>
    /// What made the change: a User, an API key, or JIM itself (System).
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; }

    /// <summary>
    /// The name of whoever made the change, as it was when they made it.
    /// </summary>
    public string? InitiatedByName { get; set; }

    /// <summary>
    /// When the change was made (UTC).
    /// </summary>
    public DateTime When { get; set; }

    /// <summary>
    /// The reason given for the change, if one was.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>The complete, redacted snapshot of the object at this version.</summary>
    public ConfigurationSnapshot Snapshot { get; set; } = null!;

    /// <summary>The structured diff against the immediately preceding version (every node added, for the first version).</summary>
    public ConfigurationDiff Diff { get; set; } = null!;
}
