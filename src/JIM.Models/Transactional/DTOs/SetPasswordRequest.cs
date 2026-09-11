// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Models.Staging;

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// One request to set a person's password (#1635): the single operation behind Set Password in the portal, the
/// REST API and PowerShell, with the target mode as a parameter rather than as a second verb.
/// <para>
/// The two modes are one operation because they share everything that matters: the queue, the retry policy,
/// coalescing, the Activity shape, and the person's password history. What differs is only where the password
/// is aimed. <see cref="Targets"/> null means every Connected System configured for Password Synchronisation
/// (the event case: somebody's password changed and JIM carries it); a list of Connected System Object ids means
/// exactly those accounts (the reset case: an administrator named them).
/// </para>
/// <para>
/// <b>The password value goes into the queue encrypted and nowhere else.</b> It is never logged, never written
/// to an Activity and never returned. Callers must hold it no longer than the call.
/// </para>
/// </summary>
public class SetPasswordRequest
{
    /// <summary>
    /// The person whose password this is.
    /// </summary>
    public required Guid MetaverseObjectId { get; init; }

    /// <summary>
    /// The person's display name, for the Activity. Never the password.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// The password to set. Encrypted before it is written and never logged, never returned, and never put on an
    /// Activity; it exists in cleartext only for as long as the operation runs.
    /// </summary>
    public required string Password { get; init; }

    /// <summary>
    /// The Connected System Objects to set the password on, or null to propagate it to every Connected System
    /// configured for Password Synchronisation.
    /// <para>
    /// Every id named must be an account joined to <see cref="MetaverseObjectId"/> in a Connected System whose
    /// Connector can set passwords, and at most one per Connected System, because the queue holds one change per
    /// person per system. An empty list is refused rather than read as "nowhere": a caller that meant every
    /// system passes null, and one that meant nothing has nothing to ask for.
    /// </para>
    /// </summary>
    public IReadOnlyList<Guid>? Targets { get; init; }

    /// <summary>
    /// What should happen to the password once set. Required rather than defaulted, because the sensible default
    /// differs by circumstance: an administrator setting a password on somebody's behalf usually requires a
    /// change at next sign-in, whereas a password the person chose themselves must not be expired on arrival.
    /// The surfaces choose; this does not choose for them.
    /// </summary>
    public required PasswordExpiryBehaviour ExpiryBehaviour { get; init; }

    /// <summary>
    /// Whether to enable each account as its password is set, or null to leave it as it is. Honoured only when
    /// <see cref="Targets"/> names the accounts: a propagated password never enables an account, because it
    /// reaches accounts an administrator may have disabled on purpose.
    /// </summary>
    public bool? EnableAccount { get; init; }

    /// <summary>
    /// The administrator making the change, for attribution. Exactly one of this and
    /// <see cref="InitiatedByApiKey"/> is set; an Activity attributed to neither is refused.
    /// </summary>
    public MetaverseObject? InitiatedBy { get; init; }

    /// <summary>
    /// The API key an automation authenticated with, for attribution. See <see cref="InitiatedBy"/>.
    /// </summary>
    public ApiKey? InitiatedByApiKey { get; init; }

    /// <summary>
    /// Which way the request is aimed, derived from <see cref="Targets"/>: named accounts are an explicit set,
    /// no list is a propagated change.
    /// </summary>
    public PendingPasswordChangeOrigin Origin =>
        Targets == null ? PendingPasswordChangeOrigin.Propagated : PendingPasswordChangeOrigin.Explicit;
}
