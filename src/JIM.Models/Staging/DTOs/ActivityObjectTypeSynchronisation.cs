// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// What an Activity has to do with one Connected System Object Type that a synchronisation processes in full (#2615). For
/// a configuration change Activity, the changes it saved to that type and whether a synchronisation has applied them yet.
/// For a synchronisation run, the changes it claimed, the number of objects of the type it processed because of them, and
/// whether it applied them.
/// </summary>
public class ActivityObjectTypeSynchronisation
{
    /// <summary>
    /// The Connected System whose synchronisation applies the changes.
    /// </summary>
    public int ConnectedSystemId { get; init; }

    /// <summary>
    /// The name of that Connected System.
    /// </summary>
    public string ConnectedSystemName { get; init; } = string.Empty;

    /// <summary>
    /// The Connected System Object Type whose objects are processed in full.
    /// </summary>
    public int ConnectedSystemObjectTypeId { get; init; }

    /// <summary>
    /// The name of that Object Type.
    /// </summary>
    public string ObjectTypeName { get; init; } = string.Empty;

    /// <summary>
    /// For a synchronisation run, how many Connected System Objects of the type exist, which is how many the run processed
    /// because of the changes. Null for a configuration change Activity. Read when the Activity is, so it is the count now
    /// rather than a figure the run stored.
    /// </summary>
    public int? ObjectCount { get; init; }

    /// <summary>
    /// The changes, oldest first, capped at <see cref="ObjectTypeAwaitingSynchronisation.MaxChangesListed"/> across the
    /// Activity.
    /// </summary>
    public IReadOnlyList<ConfigurationChangeSynchronisationSummary> Changes { get; init; } = [];

    /// <summary>
    /// Whether every one of the changes has been applied by a synchronisation. False while any is awaiting one, and for an
    /// empty list.
    /// </summary>
    public bool IsApplied => Changes.Count > 0 && Changes.All(change => change.IsApplied);

    /// <summary>
    /// When the last of the changes was applied (UTC). Null unless <see cref="IsApplied"/>.
    /// </summary>
    public DateTime? AppliedAt => IsApplied ? Changes.Max(change => change.AppliedAt) : null;

    /// <summary>
    /// The synchronisation run that applied the changes. Null unless <see cref="IsApplied"/>, and null once that run's
    /// Activity has been removed.
    /// </summary>
    public Guid? AppliedByActivityId => IsApplied
        ? Changes.OrderByDescending(change => change.AppliedAt).Select(change => change.ClaimedByActivityId).FirstOrDefault()
        : null;

    /// <summary>
    /// Where the changes a synchronisation run claimed stand, given how the run ended. A run applies the changes it claimed
    /// as it completes, whether or not individual objects had errors, and a run that failed or was cancelled applies none
    /// of them and leaves its claim for the next run to take over.
    /// </summary>
    /// <param name="runStatus">The status of the synchronisation run's Activity.</param>
    public ConfigurationChangeApplication GetApplication(ActivityStatus runStatus)
    {
        if (IsApplied)
            return ConfigurationChangeApplication.Applied;

        return runStatus is ActivityStatus.FailedWithError or ActivityStatus.Cancelled
            ? ConfigurationChangeApplication.NotApplied
            : ConfigurationChangeApplication.InProgress;
    }
}
