// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// One saved configuration change that a Connected System's synchronisation applies to every object of a Connected System
/// Object Type, with the names needed to read it and who saved it (#2615): a <see cref="ConfigurationChangeSynchronisation"/>
/// row joined to the Object Type, the Connected System and the Activity that recorded the change.
/// </summary>
public class ConfigurationChangeSynchronisationSummary
{
    /// <summary>
    /// The row's identifier.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// The Connected System whose synchronisation applies the change.
    /// </summary>
    public int ConnectedSystemId { get; init; }

    /// <summary>
    /// The name of that Connected System.
    /// </summary>
    public string ConnectedSystemName { get; init; } = string.Empty;

    /// <summary>
    /// The Connected System Object Type whose objects the change can affect.
    /// </summary>
    public int ConnectedSystemObjectTypeId { get; init; }

    /// <summary>
    /// The name of that Object Type.
    /// </summary>
    public string ObjectTypeName { get; init; } = string.Empty;

    /// <summary>
    /// What was saved, as recorded when it was saved: the rule and what was done to it. Reads after the rule is gone.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// When the change was saved (UTC).
    /// </summary>
    public DateTime RequestedAt { get; init; }

    /// <summary>
    /// The Activity that recorded the saved change. Null once that Activity has been removed.
    /// </summary>
    public Guid? ConfigurationChangeActivityId { get; init; }

    /// <summary>
    /// The Synchronisation Rule the change was made to, where it was made to one and it still exists.
    /// </summary>
    public int? SyncRuleId { get; init; }

    /// <summary>
    /// The synchronisation run that claimed the change. Null while no run has.
    /// </summary>
    public Guid? ClaimedByActivityId { get; init; }

    /// <summary>
    /// When the claiming run completed and applied the change (UTC). Null means it is still awaiting synchronisation.
    /// </summary>
    public DateTime? AppliedAt { get; init; }

    /// <summary>
    /// The kind of principal that saved the change, from the Activity that recorded it. Null once that Activity has been
    /// removed.
    /// </summary>
    public ActivityInitiatorType? InitiatedByType { get; init; }

    /// <summary>
    /// The identifier of the principal that saved the change.
    /// </summary>
    public Guid? InitiatedById { get; init; }

    /// <summary>
    /// The name of the principal that saved the change, as it was when the change was saved.
    /// </summary>
    public string? InitiatedByName { get; init; }

    /// <summary>
    /// Whether the change has been applied by a synchronisation.
    /// </summary>
    public bool IsApplied => AppliedAt.HasValue;
}
