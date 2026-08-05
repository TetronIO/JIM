// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// One reason a target gave for refusing an initial password, and how many accounts it is holding up.
/// <para>
/// Grouped by the message rather than listed per account, because the administrator reading this is fixing a setting,
/// not an account. Four hundred accounts refused for one reason is one problem; four hundred rows is the same problem
/// with the shape taken out of it.
/// </para>
/// </summary>
public class InitialPasswordRejection
{
    /// <summary>
    /// What the target said, as close to verbatim as JIM can carry it.
    /// <para>
    /// Presented unaltered even where it is barely readable: a directory's rejection code is the one thing that
    /// identifies the fault precisely enough to search for, and paraphrasing it would take that away. The Connector
    /// has already removed anything resembling the password before it reaches here.
    /// </para>
    /// </summary>
    public string? TargetMessage { get; init; }

    /// <summary>
    /// How JIM classified the refusal, for surfaces that want to say something about it in their own words.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; init; }

    /// <summary>
    /// How many accounts are parked on this reason.
    /// </summary>
    public int AccountCount { get; init; }

    /// <summary>
    /// The earliest attempt that produced this reason, so an administrator can tell a fault that arrived this
    /// morning from one that has been sitting there since the rule was written.
    /// </summary>
    public DateTime? FirstSeenAt { get; init; }
}
