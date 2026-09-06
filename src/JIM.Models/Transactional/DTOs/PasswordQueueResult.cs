// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What queueing a password change produced (#1119, #1635): where it was aimed, and the Activity that records it.
/// </summary>
public class PasswordQueueResult
{
    /// <summary>
    /// The Activity recording the change. Present even when nothing was queued, because requirement 14 makes a
    /// change that reached no system an explicit recorded outcome rather than silence.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// Which way the change was aimed (#1635): at the accounts the caller named, or at every Connected System
    /// configured for Password Synchronisation. The same Activity shape and the same queue either way; a caller
    /// showing the result uses this to say "set" or "propagated".
    /// </summary>
    public PendingPasswordChangeOrigin Origin { get; set; }

    /// <summary>
    /// The Connected Systems the change was queued for.
    /// </summary>
    public IReadOnlyList<PasswordQueueTargetOutcome> Targets { get; set; } = [];

    /// <summary>
    /// Whether the change reached no system at all. For a propagated change: none is configured for Password
    /// Synchronisation. An explicit change always names at least one account, so this is false for one.
    /// </summary>
    public bool NoTargets => Targets.Count == 0;
}

/// <summary>
/// One Connected System a password change was queued for.
/// </summary>
public class PasswordQueueTargetOutcome
{
    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this system is currently taking synchronised passwords. For a propagated change, false means the
    /// change was queued and is being held: a configured system that is switched off accumulates rather than
    /// discards, and enabling it delivers what accumulated. Reported so a caller can tell "on its way" from
    /// "waiting for somebody to switch the system on", which are the same thing to the queue and very different
    /// to an administrator. An explicit change is delivered either way (decision D1); the flag is still reported
    /// so a dialog can say the system is paused for propagation.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The account the change is aimed at, or null where the identity has no account in this system yet.
    /// <para>
    /// Null is an ordinary outcome rather than a failure: the change waits, bounded by its time to live, and
    /// delivery re-resolves the account each attempt, so a password arriving before provisioning resolves itself.
    /// </para>
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }
}
