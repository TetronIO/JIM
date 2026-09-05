// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Which queued password changes an operation applies to: the queue list, and the retry and cancel actions that
/// run over it (#1119, requirement 22).
/// <para>
/// One type serves all three deliberately. The queue page's most useful action is "retry everything shown", and
/// the honest way to express that is the filter the reader is already looking at, not the page of identifiers
/// they happen to have scrolled to. Passing identifiers instead would silently act on a page rather than a
/// selection, and would need every matching row materialised first to act on a large one.
/// </para>
/// </summary>
public class PendingPasswordChangeFilter
{
    /// <summary>
    /// Restrict to one Connected System, or null for every system.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// Restrict to one status, or null for every status.
    /// <para>
    /// <see cref="PendingPasswordChangeStatus.Pending"/> is wider than the enum value: it also returns a change the
    /// Password Delivery Service has claimed (<see cref="PendingPasswordChangeStatus.Delivering"/>), because from the
    /// administrator's side both are waiting and the queue summary counts them together (#1635). Ask for Delivering
    /// by name to see only those. Every other status matches exactly.
    /// </para>
    /// </summary>
    public PendingPasswordChangeStatus? Status { get; set; }

    /// <summary>
    /// Restrict to changes whose last attempt failed this way, or null for every reason. Only meaningful
    /// alongside a status that has been attempted; a pending change that has never been tried has no reason.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; set; }

    /// <summary>
    /// Restrict to one identity, for the Metaverse Object's own view of what it is owed.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// Restrict to these specific changes, for a row action or a checkbox selection. Combines with the other
    /// members rather than replacing them, so "these three, if they are still parked" is expressible: an
    /// administrator acting on what a stale page showed them must not act on a row that has since moved on.
    /// </summary>
    public IReadOnlyCollection<Guid>? Ids { get; set; }

    /// <summary>
    /// Free-text search over the identity's display name and the Connected System's name.
    /// </summary>
    public string? SearchText { get; set; }

    /// <summary>
    /// Whether this filter names specific changes rather than a whole set. Retry and cancel report differently
    /// for the two: acting on a named row that has moved on is worth telling the administrator about, whereas a
    /// set that matched nothing is simply an empty set.
    /// </summary>
    public bool TargetsSpecificChanges => Ids is { Count: > 0 };
}
