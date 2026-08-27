// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What queueing a password change produced (#1119): where it was aimed, and the Activity that records it.
/// </summary>
public class PasswordQueueResult
{
    /// <summary>
    /// The Activity recording the change. Present even when nothing was queued, because requirement 14 makes a
    /// change that reached no system an explicit recorded outcome rather than silence.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// The Connected Systems the change was queued for.
    /// </summary>
    public IReadOnlyList<PasswordQueueTargetOutcome> Targets { get; set; } = [];

    /// <summary>
    /// Whether the change reached no system at all: either none is enabled for Password Synchronisation, or none
    /// of the enabled ones is one this identity has an account in and could gain one in.
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
    /// Whether this system is currently taking synchronised passwords. False means the change was queued and is
    /// being held: a configured system that is switched off accumulates rather than discards, and enabling it
    /// delivers what accumulated. Reported so a caller can tell "on its way" from "waiting for somebody to
    /// switch the system on", which are the same thing to the queue and very different to an administrator.
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
