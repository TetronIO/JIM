// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Staging;
using JIM.Models.Transactional.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// A password change for an identity, to be synchronised to every Connected System that takes synchronised
/// passwords and in which the identity has an account (#1119).
/// <para>
/// Distinct from setting a password on chosen accounts. That operation applies a password the caller chose to
/// whichever accounts they name, immediately, and reports per-account success or failure. This one records that the person's password
/// has changed and returns; delivery happens on its own clock, with retries, so a directory being unavailable
/// delays the password rather than losing it.
/// </para>
/// </summary>
public class SynchroniseMetaverseObjectPasswordRequest
{
    /// <summary>
    /// The password. Encrypted before it is stored, never logged, never returned, and never recorded on an
    /// Activity.
    /// </summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// What should happen to the password once each target has it. Omit for
    /// <see cref="PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy"/>, which is the right default for a
    /// password the person chose: demanding they choose another one at next sign-in would defeat the point of
    /// synchronising the one they just set.
    /// </summary>
    public PasswordExpiryBehaviour? ExpiryBehaviour { get; set; }
}

/// <summary>
/// What was queued, and where. Never carries the password or anything derived from it.
/// </summary>
public class SynchroniseMetaverseObjectPasswordResponse
{
    /// <summary>
    /// The Activity recording the change. It is the durable record, outliving the queue rows, and is where the
    /// per-system outcomes appear once delivery has been attempted.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// One entry per Connected System the change was queued for.
    /// </summary>
    public IReadOnlyList<SynchroniseMetaverseObjectPasswordTarget> Targets { get; set; } = [];

    /// <summary>
    /// True where no Connected System takes synchronised passwords for this identity, so nothing was queued.
    /// Reported explicitly rather than as an empty list alone: silence here would let a caller believe a password
    /// propagated when nothing was even recorded (requirement 14).
    /// </summary>
    public bool QueuedForNoSystems => Targets.Count == 0;

    public static SynchroniseMetaverseObjectPasswordResponse FromResult(PasswordQueueResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new SynchroniseMetaverseObjectPasswordResponse
        {
            ActivityId = result.ActivityId,
            Targets = result.Targets.Select(t => new SynchroniseMetaverseObjectPasswordTarget
            {
                ConnectedSystemId = t.ConnectedSystemId,
                ConnectedSystemName = t.ConnectedSystemName,
                ConnectedSystemObjectId = t.ConnectedSystemObjectId
            }).ToList()
        };
    }
}

/// <summary>
/// One Connected System the password change was queued for.
/// </summary>
public class SynchroniseMetaverseObjectPasswordTarget
{
    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = string.Empty;

    /// <summary>
    /// The account the password is aimed at, or null where the identity has no account in this system yet. A
    /// change with no account is queued rather than refused, bounded by its time to live, so the
    /// provisioning-then-password race resolves itself when the account appears.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }
}
