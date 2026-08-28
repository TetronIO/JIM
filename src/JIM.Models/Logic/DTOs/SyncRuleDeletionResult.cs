// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.DTOs;

/// <summary>
/// The outcome of a Synchronisation Rule deletion request (#1537): whether the deletion completed
/// synchronously or a value recall was queued to run first, and the scale of the rule's contributed values
/// at decision time, so callers can build responses (the REST 202 tracking DTO, portal snackbar) without a
/// second query.
/// </summary>
public class SyncRuleDeletionResult
{
    /// <summary>
    /// True when the rule was disabled and a <see cref="Tasking.DeleteSyncRuleWorkerTask"/> was queued to
    /// recall its contributed values and then delete it; false when the rule was deleted synchronously
    /// (keep chosen, or no contributed values).
    /// </summary>
    public bool RecallQueued { get; set; }

    /// <summary>
    /// The queued recall task's Activity id when <see cref="RecallQueued"/> is true, so progress can be
    /// monitored from Operations; null when the deletion completed synchronously.
    /// </summary>
    public Guid? RecallActivityId { get; set; }

    /// <summary>
    /// How many Metaverse attribute values the rule contributed at decision time.
    /// </summary>
    public int AffectedValueCount { get; set; }

    /// <summary>
    /// How many distinct Metaverse Objects held at least one of those values at decision time.
    /// </summary>
    public int AffectedObjectCount { get; set; }
}
