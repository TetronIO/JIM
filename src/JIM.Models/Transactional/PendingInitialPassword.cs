// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// An account JIM has provisioned and still owes an initial password to.
/// <para>
/// This exists because a password that could not be set must never fail the export that created the account.
/// The account is already there. Marking the export failed would have JIM retry the <i>create</i>, which either
/// duplicates the object or errors for ever, and would misreport a successful provisioning run as a failed one.
/// The password therefore carries its own outcome, and this is where it lives.
/// </para>
/// <para>
/// A row exists only while there is something outstanding. Success deletes it, so the table is a work list
/// rather than a history and stays proportional to what is wrong rather than to how many accounts JIM has ever
/// created.
/// </para>
/// <para>
/// <b>No password value is ever stored here, or anywhere else.</b> The password is generated at the moment of
/// delivery, handed to the Connector, and forgotten. What this records is that one is owed, how many times JIM
/// has tried, and what the target said when it refused.
/// </para>
/// </summary>
public class PendingInitialPassword
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The account awaiting its password.
    /// </summary>
    public ConnectedSystemObject ConnectedSystemObject { get; set; } = null!;

    public Guid ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// Denormalised from the account so that "what is outstanding on this Connected System?" is answerable
    /// without joining, which is what the Connected System's needs-attention indicator asks on every page load.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The Synchronisation Rule whose configuration governs this delivery, and whose generator settings are
    /// re-read on every attempt so that changing them takes effect on the next try.
    /// <para>
    /// Null once the rule has been deleted. There is then no configuration to generate from, so the delivery
    /// can no longer be attempted and the record is only of historical interest.
    /// </para>
    /// </summary>
    public SyncRule? SyncRule { get; set; }

    public int? SyncRuleId { get; set; }

    public PendingInitialPasswordStatus Status { get; set; } = PendingInitialPasswordStatus.Pending;

    /// <summary>
    /// How the Connector classified the most recent failure, which is what decides whether JIM tries again.
    /// Null before the first attempt.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; set; }

    /// <summary>
    /// What the target said, as close to verbatim as JIM can carry it.
    /// <para>
    /// This is the single most useful thing an administrator can be shown, because the reason a directory
    /// refuses a password is a property of that directory's policy and not something JIM can work out. The
    /// Connector has already removed anything resembling the password before it reaches here.
    /// </para>
    /// </summary>
    public string? TargetMessage { get; set; }

    public int AttemptCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastAttemptedAt { get; set; }

    /// <summary>
    /// When JIM should stop trying and record an expiry.
    /// <para>
    /// An initial password exists to get somebody into an account they have just been given. Weeks later that
    /// purpose has passed, and an account still waiting for one needs a person to look at it rather than another
    /// automatic attempt.
    /// </para>
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}
