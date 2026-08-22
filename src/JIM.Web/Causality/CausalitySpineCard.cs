// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One event on a spine column (#1495): either one of this run's own sync outcomes (wrapping the
/// <see cref="CausalityEvent"/> so attribute rows, links, tones and the drawer keep working), or a
/// hop of the causal chain. Exactly one of <see cref="Event"/> and <see cref="Hop"/> is set.
/// </summary>
public sealed class CausalitySpineCard
{
    /// <summary>
    /// This run's own event, where the card is one; null for a chain card.
    /// </summary>
    public CausalityEvent? Event { get; init; }

    /// <summary>
    /// The chain hop, where the card is one; null for this run's own events.
    /// </summary>
    public CausalitySpineChainHop? Hop { get; init; }

    /// <summary>
    /// Whether this card is one of this run's own events (rendered primary) rather than an earlier
    /// run's (rendered subdued).
    /// </summary>
    public bool IsThisRun => Event != null;

    /// <summary>
    /// When a chain card's effect was recorded, for ordering; null for this run's cards, which
    /// always order after the chain (causes precede their effects).
    /// </summary>
    public DateTime? Occurred { get; init; }
}
