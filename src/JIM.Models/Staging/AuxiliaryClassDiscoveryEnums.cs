// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// How much of a Connected System an auxiliary class discovery run reads.
/// </summary>
/// <remarks>
/// There is deliberately no percentage option. An LDAP paged search returns entries in server order, so taking a
/// representative fraction of a population would mean walking all of it anyway; a percentage would cost what a full
/// scan costs while sounding cheaper, and would report a number nobody could act on.
/// </remarks>
public enum AuxiliaryClassDiscoveryScope
{
    NotSet = 0,

    /// <summary>
    /// Reads up to a fixed number of entries per structural Object Type. Fast, and enough to find the auxiliary
    /// classes a population uses consistently; it cannot prove a class is unused.
    /// </summary>
    QuickSample = 1,

    /// <summary>
    /// Reads every entry in scope, requesting only objectClass. Definitive, and the only scope whose answer of
    /// "this class is not in use" means anything.
    /// </summary>
    FullScan = 2
}

/// <summary>
/// Where an auxiliary class discovery run has got to.
/// </summary>
/// <remarks>
/// Persisted rather than held in the portal's circuit, so that an administrator who navigates away, reloads, or
/// comes back tomorrow sees the same answer, and so a second run cannot be started while one is in flight.
/// </remarks>
public enum AuxiliaryClassDiscoveryStatus
{
    NotSet = 0,

    /// <summary>Queued or reading; results so far are partial.</summary>
    InProgress = 1,

    /// <summary>Finished reading everything its scope called for. Results are complete for that scope.</summary>
    Complete = 2,

    /// <summary>Stopped by an administrator. Results are partial and are kept, since a partial answer still names real classes.</summary>
    Cancelled = 3,

    /// <summary>Stopped by an error. <see cref="AuxiliaryClassDiscoveryRun.ErrorMessage"/> says what went wrong.</summary>
    Failed = 4
}
