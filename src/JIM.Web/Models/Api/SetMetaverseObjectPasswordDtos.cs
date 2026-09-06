// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// Sets a person's password (#1119, #1635): the one operation behind Set Password on every surface, aimed either
/// at the accounts the caller names or at every Connected System configured for Password Synchronisation.
/// <para>
/// Both target modes go through the same queue and the same Password Delivery Service; what differs is only where
/// the password is aimed, and so what the sensible defaults are. Named accounts are the reset case (an
/// administrator chose the password for somebody), so the password expires at next sign-in and the caller waits
/// briefly for the outcome. No accounts named is the event case (the person's password changed, and every
/// configured system should end up holding it), so expiry is left to each system and the call returns on enqueue.
/// </para>
/// </summary>
public class SetMetaverseObjectPasswordRequest
{
    /// <summary>
    /// The password. Encrypted before it is stored, held only until it is delivered, never logged, never returned,
    /// and never recorded on an Activity.
    /// </summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The accounts to set the password on, as Connected System Object ids, or omitted to propagate the password to
    /// every Connected System configured for Password Synchronisation in which the person has an account.
    /// <para>
    /// Every id must be one of this person's accounts, in a system whose Connector can set passwords, and at most
    /// one per Connected System. Named accounts are delivered to even where the system's Password Synchronisation
    /// is switched off: the caller named the account, which is the decision that switch exists to make. An empty
    /// list is refused rather than read as "nowhere".
    /// </para>
    /// </summary>
    public IReadOnlyList<Guid>? ConnectedSystemObjectIds { get; set; }

    /// <summary>
    /// What should happen to the password once each target has it. Omitted defaults by target mode: with
    /// <see cref="ConnectedSystemObjectIds"/>, <see cref="PasswordExpiryBehaviour.RequireChangeAtNextSignIn"/>,
    /// the right default for a password somebody else chose; without them,
    /// <see cref="PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy"/>, because a password the person chose
    /// should not demand they choose another at next sign-in.
    /// </summary>
    public PasswordExpiryBehaviour? ExpiryBehaviour { get; set; }

    /// <summary>
    /// Whether to enable each named account as its password is set. Omitted leaves the accounts as they are.
    /// Accepted only with <see cref="ConnectedSystemObjectIds"/>: a propagated password never enables an account,
    /// because it reaches accounts an administrator may have disabled on purpose.
    /// </summary>
    public bool? EnableAccount { get; set; }

    /// <summary>
    /// How many seconds to wait for delivery before answering, 0 to 30. Omitted defaults by target mode: 10 with
    /// <see cref="ConnectedSystemObjectIds"/>, so a caller resetting a password is told what each account did with
    /// it; 0 without, because a propagated change goes to every configured system and the caller usually has no
    /// reason to be held while it does. With a wait, the response is <c>200</c> once every target has settled
    /// (set, retrying, parked, held) or <c>202</c> with what is known when the time runs out; delivery continues
    /// either way.
    /// </summary>
    [Range(MinimumWaitSeconds, MaximumWaitSeconds)]
    public int? Wait { get; set; }

    public const int MinimumWaitSeconds = 0;

    /// <summary>
    /// Long enough for a target having a bad moment to answer, short enough that a caller held for it is not left
    /// wondering whether the request is still alive.
    /// </summary>
    public const int MaximumWaitSeconds = 30;

    /// <summary>
    /// How long a caller who named accounts waits when they do not say (decision D6).
    /// </summary>
    public const int DefaultWaitSecondsForNamedAccounts = 10;
}

/// <summary>
/// What was queued, where, and how far each target has got. Never carries the password or anything derived from it.
/// </summary>
public class SetMetaverseObjectPasswordResponse
{
    /// <summary>
    /// The Activity recording the change. It is the durable record, outliving the queue, and is where the
    /// per-system outcomes appear once delivery has been attempted.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// Which way the change was aimed: <c>Explicit</c> at the accounts the caller named, or <c>Propagated</c> to
    /// every Connected System configured for Password Synchronisation.
    /// </summary>
    public PendingPasswordChangeOrigin Origin { get; set; }

    /// <summary>
    /// One entry per Connected System the change was queued for.
    /// </summary>
    public IReadOnlyList<SetMetaverseObjectPasswordTarget> Targets { get; set; } = [];

    /// <summary>
    /// True where a propagated change found no Connected System configured for Password Synchronisation, so nothing
    /// was queued. Reported explicitly rather than as an empty list alone: silence here would let a caller believe a
    /// password propagated when nothing was even recorded (requirement 14). Always false for named accounts, which
    /// are refused rather than queued for nothing.
    /// </summary>
    public bool QueuedForNoSystems => Targets.Count == 0;

    /// <summary>
    /// True when no target is still Queued or Delivering: every one has been set, is retrying on its own clock, is
    /// parked waiting on a person, is held behind a switched-off system, or has expired. A waited request answers
    /// <c>200</c> when this is true and <c>202</c> when it is not. A change queued for no systems is settled.
    /// </summary>
    public bool Settled { get; set; }

    /// <summary>
    /// Builds the response from what was queued and where each target stands (#1635). The queue result is the
    /// authority on which systems the change reached and the enqueue-time facts about each (enabled, account); the
    /// outcomes overlay the delivery state. A target the outcomes do not mention has not moved, so it reads Queued.
    /// </summary>
    public static SetMetaverseObjectPasswordResponse FromResult(PasswordQueueResult result, PasswordChangeOutcomes? outcomes)
    {
        ArgumentNullException.ThrowIfNull(result);

        var outcomeBySystem = (outcomes?.Targets ?? []).ToDictionary(o => o.ConnectedSystemId);

        return new SetMetaverseObjectPasswordResponse
        {
            ActivityId = result.ActivityId,
            Origin = result.Origin,
            Settled = outcomes?.IsSettled ?? result.NoTargets,
            Targets = result.Targets.Select(t =>
            {
                outcomeBySystem.TryGetValue(t.ConnectedSystemId, out var outcome);
                return new SetMetaverseObjectPasswordTarget
                {
                    ConnectedSystemId = t.ConnectedSystemId,
                    ConnectedSystemName = t.ConnectedSystemName,
                    Enabled = t.Enabled,
                    ConnectedSystemObjectId = t.ConnectedSystemObjectId,
                    State = outcome?.State ?? PasswordChangeTargetState.Queued,
                    NextAttemptAt = outcome?.NextAttemptAt,
                    Message = outcome?.Message,
                    AttemptCount = outcome?.AttemptCount ?? 0,
                    FailureReason = outcome?.FailureReason
                };
            }).ToList()
        };
    }
}

/// <summary>
/// One Connected System the password change was queued for.
/// </summary>
public class SetMetaverseObjectPasswordTarget
{
    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this system is currently taking propagated passwords. For a propagated change, false means the change
    /// is queued and held: a configured system that is switched off accumulates rather than discards, and enabling
    /// it delivers what accumulated. A named account is delivered to either way; the flag is still reported so a
    /// caller can say the system is paused for propagation.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The account the password is aimed at, or null where the person has no account in this system yet. A
    /// propagated change with no account is queued rather than refused, bounded by its time to live, so the
    /// provisioning-then-password race resolves itself when the account appears.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// Where delivery to this system stands: Queued, Delivering, Set, Retrying, Parked, Held, Expired or Cancelled.
    /// Read once when the change is recorded (so it is usually Queued), or as of the end of the wait when one was
    /// asked for.
    /// </summary>
    public PasswordChangeTargetState State { get; set; }

    /// <summary>
    /// When the next delivery attempt falls due (UTC), for a target that is Retrying; null otherwise.
    /// </summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>
    /// The target's own words on the most recent outcome, or JIM's where the target gave none: why it refused, or
    /// that the password was set. Null for a target nothing has been said about yet.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// How many delivery attempts this change has had against this system.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Why the most recent attempt failed, where it did: Transient, ConfigurationFault, PolicyRejection,
    /// TargetObjectNotFound or UnsupportedOperation. Null before any attempt, once the password is set, and
    /// after the queue row has gone. The portal chooses its remedy guidance from this; a script can do the same.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; set; }
}
