// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What became of one provisioned password change offered to the queue (#1697, #2160): whether it inserted a new row,
/// took over an existing one, gave its obligation to a password change already waiting for the account, or was not
/// queued because the row holds the person's own change for another Object Type.
/// </summary>
public class ProvisionedPasswordStagingOutcome
{
    /// <summary>
    /// The id of the row now in the table for this change's (Metaverse Object, Connected System) key: the inserted
    /// row, the row taken over, or the waiting change now carrying the account's first password. <see cref="Guid.Empty"/>
    /// for <see cref="ProvisionedPasswordStagingDisposition.Conflicted"/>, where nothing was written.
    /// </summary>
    public Guid RowId { get; init; }

    /// <summary>
    /// The id the change itself was offered under, before any coalescing.
    /// </summary>
    public required Guid RequestedId { get; init; }

    public required ProvisionedPasswordStagingDisposition Disposition { get; init; }

    /// <summary>
    /// The row that was there before, as it stood when the statement met it: what the Initial Password replaced, for
    /// <see cref="ProvisionedPasswordStagingDisposition.Superseded"/>, or the waiting change that now carries the
    /// account's first password, for <see cref="ProvisionedPasswordStagingDisposition.Coalesced"/>. Null otherwise.
    /// Read back from the same statement that decided, so the staging Activity describes exactly what it decided on.
    /// </summary>
    public PendingPasswordChangeOrigin? ExistingOrigin { get; init; }

    /// <inheritdoc cref="ExistingOrigin"/>
    public PendingPasswordChangeStatus? ExistingStatus { get; init; }

    /// <summary>
    /// How the existing row's last attempt failed, and the target's own words, or null where it had not failed.
    /// </summary>
    public Staging.PasswordSetFailureReason? ExistingFailureReason { get; init; }

    /// <inheritdoc cref="ExistingFailureReason"/>
    public string? ExistingTargetMessage { get; init; }

    /// <summary>
    /// The Activity of the password change the existing row carried, so a change the Initial Password replaced can be
    /// told so beneath its own record. Null where there was no existing row.
    /// </summary>
    public Guid? ExistingActivityId { get; init; }

    /// <summary>
    /// Whether the existing row was a propagated change held because Password Synchronisation is switched off on the
    /// system, which the Initial Password replaces rather than waits behind.
    /// </summary>
    public bool ExistingHeld { get; init; }

    /// <summary>
    /// Whether the existing row was an administrator's set for a Connected System Object that no longer exists or is no
    /// longer this person's.
    /// </summary>
    public bool ExistingAccountGone { get; init; }
}
