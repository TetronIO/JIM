// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// One Connected System Object Type that a saved configuration change makes the Connected System's next synchronisation,
/// Delta or Full, process in full: every object of the type, not only the changed ones. States what that costs: how many
/// objects of the type there are, and about how long processing them takes at the speed of the Connected System's last
/// Full Synchronisation.
/// </summary>
/// <remarks>
/// Stated before a save, in the acknowledgement the administrator gives, and again in the response to a save made through
/// the REST API (#2615).
/// </remarks>
public class AwaitingSynchronisationEstimate
{
    /// <summary>
    /// The Connected System whose next synchronisation processes the type.
    /// </summary>
    public int ConnectedSystemId { get; init; }

    /// <summary>
    /// The name of that Connected System.
    /// </summary>
    public string ConnectedSystemName { get; init; } = string.Empty;

    /// <summary>
    /// The Connected System Object Type whose objects will all be processed.
    /// </summary>
    public int ConnectedSystemObjectTypeId { get; init; }

    /// <summary>
    /// The name of that Object Type.
    /// </summary>
    public string ObjectTypeName { get; init; } = string.Empty;

    /// <summary>
    /// How many Connected System Objects of the type the next synchronisation will process.
    /// </summary>
    public int ObjectCount { get; init; }

    /// <summary>
    /// About how long processing them takes at the speed of the Connected System's last Full Synchronisation. Null when it
    /// has never completed one, since there is then no speed to estimate from.
    /// </summary>
    public TimeSpan? EstimatedDuration { get; init; }
}
