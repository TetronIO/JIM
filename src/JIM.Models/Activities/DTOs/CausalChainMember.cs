// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// One cause within a cohort: a single object or event that contributed to the effect, and whatever caused it
/// in turn (#1223).
/// </summary>
public class CausalChainMember
{
    /// <summary>
    /// The Metaverse Object that was the cause, where the cause is identified by object. Routinely already
    /// deleted, which is the point: <see cref="DisplayName"/> is what keeps it nameable.
    /// </summary>
    public Guid? MetaverseObjectId { get; init; }

    /// <summary>
    /// The Connected System Object that was the cause, where the cause is identified by object.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; init; }

    /// <summary>
    /// The Pending Export whose execution was the cause. Identifies which export cycle a confirmation
    /// confirms; the row itself is deleted on confirmation.
    /// </summary>
    public Guid? PendingExportId { get; init; }

    /// <summary>
    /// The Run Profile Execution Item that recorded the causing event, where one did. This is what the walk
    /// follows upward, and what the UI links to.
    /// </summary>
    public Guid? RunProfileExecutionItemId { get; init; }

    /// <summary>
    /// The outcome node that was the causing event, where one was recorded.
    /// </summary>
    public Guid? SyncOutcomeId { get; init; }

    /// <summary>
    /// How the cause was named when the edge was written. The only name available once the object is gone.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// When the effect was recorded. The cause's own timestamp lives on the record it points at, which may be
    /// long purged, so this is the only time the chain can state with certainty.
    /// </summary>
    public DateTime Occurred { get; init; }

    /// <summary>
    /// What happened when the walk tried to continue past this cause. Never a gap and never an exception: an
    /// unresolvable ancestor is an explicit state the UI renders, not an absence it has to interpret.
    /// </summary>
    /// <remarks>
    /// Settable rather than init-only because the walk resolves it a level later than it builds the member:
    /// whether a cause can be followed is only known once its own level has been queried.
    /// </remarks>
    public CausalChainResolution Resolution { get; set; }

    /// <summary>
    /// What caused this cause, grouped into cohorts. Populated only when
    /// <see cref="Resolution"/> is <see cref="CausalChainResolution.Resolved"/>.
    /// </summary>
    public List<CausalChainCohort> Causes { get; init; } = [];
}
