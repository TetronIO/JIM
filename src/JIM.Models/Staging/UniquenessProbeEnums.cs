// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What a live uniqueness probe found for one candidate value (Unique Value Generation, #242, release 3). Pinned by
/// ordinal: append new members, never renumber.
/// </summary>
public enum UniquenessProbeOutcome
{
    /// <summary>
    /// The Connected System already holds the value, so it is taken.
    /// </summary>
    Found = 0,

    /// <summary>
    /// The search did not return the value. Advisory: the probe proves the search could see values JIM knows are
    /// there, not that nothing it cannot see holds the value (plan decision 14).
    /// </summary>
    NotFound = 1,

    /// <summary>
    /// The probe could not tell: the Connected System could not be reached or refused the search, or the search did
    /// not return the control value JIM knows is there. Generation then relies on JIM's own records.
    /// </summary>
    CouldNotDetermine = 2
}
