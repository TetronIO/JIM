// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// One of the objects an ambiguous match found (#2052): a Metaverse Object an import could have joined, or (on export)
/// a Connected System Object provisioning could have claimed. JIM never chooses between them, so the Activity item
/// lists every candidate, up to <see cref="MaxRecorded"/>, for the administrator to resolve.
/// </summary>
/// <remarks>
/// What is recorded about the candidate is <see cref="MatchCandidateSnapshot"/>, shared with a Configuration Change
/// Preview's rows so the two read the same. Stored as <see cref="CausalEdge"/> stores its cause side: a cascading
/// foreign key to the Activity item, and snapshot scalars for the candidate with no foreign key.
/// </remarks>
public class ActivityRunProfileExecutionItemMatchCandidate : MatchCandidateSnapshot
{
    /// <summary>The most candidates recorded on one item; <see cref="MatchCandidateSnapshot.CandidateTotal"/> carries the full count.</summary>
    public const int MaxRecorded = 10;

    /// <summary>
    /// Whether an Activity item with this error records candidates, so a reader asks for them only where there can be
    /// some.
    /// </summary>
    public static bool AreRecordedFor(ActivityRunProfileExecutionItemErrorType? errorType) =>
        errorType is ActivityRunProfileExecutionItemErrorType.AmbiguousMatch or ActivityRunProfileExecutionItemErrorType.AmbiguousProvisioningMatch;

    /// <summary>Assigned in code so the raw-SQL insert does not round-trip database-generated keys.</summary>
    public Guid Id { get; set; }

    /// <summary>The Activity item reporting the ambiguity. Cascades on delete.</summary>
    public Guid ActivityRunProfileExecutionItemId { get; set; }

    /// <summary>Foreign key navigation for <see cref="ActivityRunProfileExecutionItemId"/>.</summary>
    public ActivityRunProfileExecutionItem ActivityRunProfileExecutionItem { get; set; } = null!;
}
