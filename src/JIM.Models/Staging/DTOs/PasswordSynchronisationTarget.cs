// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// A Connected System that is configured and enabled to receive synchronised passwords (#1119), flattened to
/// what fan-out needs and nothing more.
/// <para>
/// A projection rather than the configuration entity, because fan-out runs on every password change and asks the
/// same question each time: which systems take passwords, which Object Type holds their accounts, and how long a
/// queued change may wait. Loading Connected Systems to answer that would materialise a graph per change.
/// </para>
/// </summary>
public class PasswordSynchronisationTarget
{
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The system's name, so an Activity can say where a password was sent without a second query.
    /// </summary>
    public string ConnectedSystemName { get; set; } = string.Empty;

    /// <summary>
    /// The Connected System Object Type holding this system's accounts; fan-out looks for an account of this type.
    /// </summary>
    public int TargetObjectTypeId { get; set; }

    /// <summary>
    /// How long a queued change may wait for this system before it is expired rather than delivered, with JIM's
    /// default already resolved.
    /// </summary>
    public TimeSpan TimeToLive { get; set; }
}
