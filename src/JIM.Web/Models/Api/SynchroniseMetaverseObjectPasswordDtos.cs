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

    /// <summary>
    /// How many seconds to wait for delivery before answering, 0 to 30. Omit, or pass 0, to return as soon as the
    /// change is recorded, which is the right default for a propagated password: it goes to every configured system,
    /// and the caller usually has no reason to be held while it does. With a wait, the response is <c>200</c> once
    /// every target has settled (set, retrying, parked, held) or <c>202</c> with what is known when the time runs
    /// out; delivery continues either way.
    /// </summary>
    [Range(MinimumWaitSeconds, MaximumWaitSeconds)]
    public int? Wait { get; set; }

    public const int MinimumWaitSeconds = 0;

    /// <summary>
    /// Long enough for a target having a bad moment to answer, short enough that a caller held for it is not left
    /// wondering whether the request is still alive.
    /// </summary>
    public const int MaximumWaitSeconds = 30;
}

/// <summary>
/// What was queued, where, and how far each target has got. Never carries the password or anything derived from it.
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
    /// True where no Connected System is configured to take synchronised passwords, so nothing was queued.
    /// Reported explicitly rather than as an empty list alone: silence here would let a caller believe a password
    /// propagated when nothing was even recorded (requirement 14).
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
    public static SynchroniseMetaverseObjectPasswordResponse FromResult(PasswordQueueResult result, PasswordChangeOutcomes? outcomes)
    {
        ArgumentNullException.ThrowIfNull(result);

        var outcomeBySystem = (outcomes?.Targets ?? []).ToDictionary(o => o.ConnectedSystemId);

        return new SynchroniseMetaverseObjectPasswordResponse
        {
            ActivityId = result.ActivityId,
            Settled = outcomes?.IsSettled ?? result.NoTargets,
            Targets = result.Targets.Select(t =>
            {
                outcomeBySystem.TryGetValue(t.ConnectedSystemId, out var outcome);
                return new SynchroniseMetaverseObjectPasswordTarget
                {
                    ConnectedSystemId = t.ConnectedSystemId,
                    ConnectedSystemName = t.ConnectedSystemName,
                    Enabled = t.Enabled,
                    ConnectedSystemObjectId = t.ConnectedSystemObjectId,
                    State = outcome?.State ?? PasswordChangeTargetState.Queued,
                    NextAttemptAt = outcome?.NextAttemptAt,
                    Message = outcome?.Message,
                    AttemptCount = outcome?.AttemptCount ?? 0
                };
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
    /// Whether this system is currently taking synchronised passwords. False means the change is queued and
    /// held: a configured system that is switched off accumulates rather than discards, and enabling it delivers
    /// what accumulated. Reported so a caller can tell "on its way" from "waiting on somebody enabling the
    /// system", which are indistinguishable from the queue alone.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The account the password is aimed at, or null where the identity has no account in this system yet. A
    /// change with no account is queued rather than refused, bounded by its time to live, so the
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
}
