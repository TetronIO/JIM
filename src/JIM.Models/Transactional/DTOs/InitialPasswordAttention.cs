// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// How many accounts under one Synchronisation Rule, or one Connected System, are waiting on a person over their
/// initial password.
/// <para>
/// The two counts are kept apart because they ask for different things, and a surface that merges them tells an
/// administrator a number without telling them what to do with it. Parked work is fixed where it is reported: correct
/// the Synchronisation Rule's password settings and saving releases it. Expired work cannot be fixed there at all;
/// those accounts were provisioned, never got a password, and now need one by other means. Summing them would put
/// "act here" and "act elsewhere" behind a single figure.
/// </para>
/// </summary>
public class InitialPasswordAttention
{
    /// <summary>
    /// Accounts whose target refused the password these settings produced, which JIM has stopped retrying.
    /// </summary>
    public int ParkedCount { get; init; }

    /// <summary>
    /// Accounts provisioned but never given an initial password within its time to live.
    /// </summary>
    public int ExpiredCount { get; init; }

    /// <summary>
    /// True when there is anything for a person to do. A settled rule or system renders no indicator at all, so
    /// silence stays the reward for needing no action.
    /// </summary>
    public bool NeedsAttention => ParkedCount > 0 || ExpiredCount > 0;
}
