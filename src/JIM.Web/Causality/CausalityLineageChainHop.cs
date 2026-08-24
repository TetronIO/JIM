// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;

namespace JIM.Web.Causality;

/// <summary>
/// A chain cohort prepared for rendering as a lineage card (#1495): the snapshotted sentence and
/// attribution from <see cref="CausalityCauseWording"/>, the derived run kind, the link to the item
/// that recorded the cause, and the cohort's members for in-place expansion. Everything here is
/// precomputed so the builder stays the single owner of the projection rules and the view renders
/// without deriving anything.
/// </summary>
public sealed class CausalityLineageChainHop
{
    /// <summary>
    /// The cohort this card speaks for, kept for its counts, nouns and disclosure labels.
    /// </summary>
    public required CausalChainCohort Cohort { get; init; }

    /// <summary>
    /// The hop's sentence, composed from the cohort's snapshots.
    /// </summary>
    public required IReadOnlyList<CausalityCauseSentencePart> SentenceParts { get; init; }

    /// <summary>
    /// Why the causes happened, or null where no reason was recorded.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Whether the card should render the cohort's Connected System as a chip (false where the
    /// sentence already names the system).
    /// </summary>
    public bool ShowConnectedSystemChip { get; init; }

    /// <summary>
    /// The kind of run that recorded the cause ("Import run", "Synchronisation run", "Export run"),
    /// derived from the hop's seam; null where the seam does not pin one.
    /// </summary>
    public string? RunKind { get; init; }

    /// <summary>
    /// Link to the Run Profile Execution Item that recorded the cause, for a sole-cause cohort.
    /// Null where the cohort speaks for several (each member carries its own link), where no item
    /// was recorded, or where the item is the very page being read.
    /// </summary>
    public string? ActivityItemHref { get; init; }

    /// <summary>
    /// When the cohort's effect was recorded (the earliest member where they differ), for card
    /// ordering and the card's timestamp.
    /// </summary>
    public DateTime Occurred { get; init; }

    /// <summary>
    /// The individual causes, for in-place expansion of a plural cohort. Empty for a sole-cause
    /// cohort, which is already named in its sentence.
    /// </summary>
    public IReadOnlyList<CausalityLineageChainHopMember> Members { get; init; } = [];
}
