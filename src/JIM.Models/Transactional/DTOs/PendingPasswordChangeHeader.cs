// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// One row of the Password Synchronisation queue as the portal, the REST API and PowerShell list it (#1119,
/// requirement 21).
/// <para>
/// A Header rather than the entity, for the usual reason (list views need denormalised names, not a graph) and
/// for one that is specific to this queue: <see cref="PendingPasswordChange.EncryptedPassword"/> has no
/// representation here at all. The value never leaves the database in a DTO, an API response or a log line, and
/// the surest way to keep that true is for the type the surfaces bind to have nowhere to put it.
/// </para>
/// </summary>
public class PendingPasswordChangeHeader
{
    public Guid Id { get; set; }

    /// <summary>
    /// The identity whose password this is, and its display name, so a list can name a person rather than a Guid.
    /// </summary>
    public Guid MetaverseObjectId { get; set; }

    /// <inheritdoc cref="MetaverseObjectId"/>
    public string? MetaverseObjectDisplayName { get; set; }

    /// <summary>
    /// The plural name of the identity's Metaverse Object Type, which is what a link to that identity is built
    /// from. Carried here so a list can link a row without a second read per row.
    /// </summary>
    public string? MetaverseObjectTypePluralName { get; set; }

    public int ConnectedSystemId { get; set; }

    /// <inheritdoc cref="ConnectedSystemId"/>
    public string ConnectedSystemName { get; set; } = string.Empty;

    /// <summary>
    /// Whether that Connected System is currently taking synchronised passwords. False means the change is held
    /// rather than on its way: a configured system that is switched off accumulates queued changes, and a
    /// delivery pass steps over it until somebody switches it on.
    /// <para>
    /// Defaulted to true deliberately. The read path sets it per row, so the default only governs a header built
    /// in code, and the safe reading of one of those is the ordinary case: a change on its way to a live system.
    /// Defaulting to false would make every hand-built header report as held, which is the rarer state and the
    /// more alarming one to show by accident.
    /// </para>
    /// </summary>
    public bool ConnectedSystemTakingPasswords { get; set; } = true;

    /// <summary>
    /// Where the change came from (#1635): an administrator's explicit set of a named account, or a password
    /// propagated to every configured system. Shown as a kind chip, and what decides whether a switched-off
    /// system holds the row: a propagated change waits on the system; an explicit one does not (decision D1).
    /// </summary>
    public PendingPasswordChangeOrigin Origin { get; set; } = PendingPasswordChangeOrigin.Propagated;

    public PendingPasswordChangeStatus Status { get; set; }

    /// <summary>
    /// How the last attempt failed, and the target's own words, which is what tells an administrator where the
    /// remedy lives. Both null for a change that has not been attempted.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; set; }

    /// <inheritdoc cref="FailureReason"/>
    public string? TargetMessage { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>
    /// When the next attempt falls due, or null for a change that is due now or is no longer being attempted.
    /// Read alongside <see cref="AttemptCount"/>: neither number says much on its own, but two attempts with a
    /// retry booked is a system having a bad morning, and six with none is a system that has given up.
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastAttemptedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When an administrator cancelled the change, and who, or null where nobody has. The name is null for a
    /// cancellation made with an API key, which has no person behind it.
    /// </summary>
    public DateTime? CancelledAt { get; set; }

    /// <inheritdoc cref="CancelledAt"/>
    public string? CancelledByName { get; set; }

    /// <summary>
    /// Whether a delivery pass at <paramref name="asOf"/> would attempt this change. The list uses it to separate
    /// a change that is waiting out a backoff from one that is due and simply has not been reached yet.
    /// <para>
    /// A propagated change held for a switched-off system is never due, whatever its retry time says: a lane
    /// claims nothing propagated on that system. Answering otherwise would put "Due now" against a row nothing
    /// will attempt, beside a queue summary correctly counting it as waiting and not due. An explicit set is due
    /// on a switched-off system exactly as on a live one, because a lane claims it there (decision D1).
    /// </para>
    /// </summary>
    public bool IsDue(DateTime asOf) =>
        Status == PendingPasswordChangeStatus.Pending
        && (ConnectedSystemTakingPasswords || Origin == PendingPasswordChangeOrigin.Explicit)
        && (NextRetryAt == null || NextRetryAt <= asOf);

    /// <summary>
    /// Whether the change is waiting on somebody switching its Connected System back on rather than on JIM.
    /// Distinguished from an ordinary wait because the remedy is a person's, not a retry's. Only a propagated
    /// change is ever held; an explicit set is delivered whether or not the system is taking propagated
    /// passwords.
    /// </summary>
    public bool IsHeld =>
        Status == PendingPasswordChangeStatus.Pending
        && !ConnectedSystemTakingPasswords
        && Origin == PendingPasswordChangeOrigin.Propagated;
}
