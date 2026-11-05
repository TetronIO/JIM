// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Which queued Exchange changes an operation applies to (#2021, Phase 6b): the queue list, and the retry that runs
/// over it. One type serves both, as the Password Synchronisation queue's filter does, so "retry the changes shown"
/// acts on exactly the changes the list shows, search text included (#2725).
/// </summary>
public class PendingExchangeChangeFilter
{
    /// <summary>Restrict to one Connected System, or null for every system.</summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// Restrict to changes standing in one state, as their follow-ups read: Queued, Held, Delivering, Retrying, Parked,
    /// Refused or NotMade. Null for every state. A state reached only once a change has left the queue (Delivered, Withdrawn,
    /// Replaced) matches nothing.
    /// </summary>
    public ExchangeChangeState? State { get; set; }

    /// <summary>Restrict to changes whose last attempt failed this way, or null for every kind.</summary>
    public ExchangeChangeFailureKind? FailureKind { get; set; }

    /// <summary>Restrict to one identity's changes.</summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>Restrict to one account's change: the queue holds at most one per account.</summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// Restrict to these changes, for a row's action. Combines with the other members, so "this change, if it is still
    /// parked" is expressible: an action taken on a stale page must not act on a row that has since moved on.
    /// </summary>
    public IReadOnlyCollection<Guid>? Ids { get; set; }

    /// <summary>Free-text search over the identity's display name and the Connected System's name.</summary>
    public string? SearchText { get; set; }

    /// <summary>Whether this filter names specific changes rather than a whole set.</summary>
    public bool TargetsSpecificChanges => Ids is { Count: > 0 };
}
