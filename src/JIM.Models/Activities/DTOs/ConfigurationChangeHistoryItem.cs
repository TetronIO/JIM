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

    /// <summary>The security principal's id, where one applies (User / API key); null for System or unattributed changes.</summary>
    public Guid? InitiatedById { get; set; }

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

    /// <summary>
    /// Set when the change stopped configuration test cases passing and was saved anyway, or their new results were
    /// accepted: the cases, by name and status, never their values.
    /// </summary>
    public string? ConfigurationTestGateNote { get; set; }

    /// <summary>A short description of what the change touched, for example "Scope, Attribute Flow".</summary>
    public string Summary { get; set; } = null!;

    /// <summary>
    /// The structured diff of this version against its predecessor. For the first version (no predecessor) it shows the
    /// whole object as created. Null only when the version carries no deserialisable snapshot.
    /// </summary>
    public ConfigurationDiff? Diff { get; set; }
}
