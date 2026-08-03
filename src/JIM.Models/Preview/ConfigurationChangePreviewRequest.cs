// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Preview;

/// <summary>
/// An administrator's request to find out what an unsaved configuration change would do. The caller-facing half of
/// the pair whose adapter-facing half is <see cref="PreviewContext"/>: this is what a surface submits, and the
/// framework turns it into a context once the preview's Activity exists.
///
/// They are separate types because the Activity id is the difference between them, and it cannot be known until the
/// framework has created the Activity. Folding them into one type would mean either a mutable id on the object
/// adapters read, or every caller inventing an id the framework then has to honour.
/// </summary>
public class ConfigurationChangePreviewRequest
{
    /// <summary>The surface being previewed; selects the adapter.</summary>
    public required ConfigurationChangePreviewSurface Surface { get; init; }

    /// <summary>
    /// The integer identifier of the configuration object being changed, for the surfaces keyed that way. Exactly
    /// one of this and <see cref="TargetGuidId"/> is populated.
    /// </summary>
    public int? TargetId { get; init; }

    /// <summary>The Guid identifier of the configuration object, for surfaces keyed that way.</summary>
    public Guid? TargetGuidId { get; init; }

    /// <summary>
    /// The object's name, recorded on the Activity so the preview is legible in the Activity list without a join to
    /// an object whose whole point may be that it is about to change.
    /// </summary>
    public string? TargetName { get; init; }

    /// <summary>The proposed configuration, unsaved, as the surface's own update type.</summary>
    public required object ProposedConfiguration { get; init; }

    /// <summary>
    /// The proposed configuration serialised for storage, produced the same way an Activity's configuration change
    /// snapshot is. Optional: without it the preview's result can still be read, but not explained against the
    /// proposal that produced it.
    /// </summary>
    public string? ProposedConfigurationSnapshot { get; init; }

    /// <summary>The principal asking. Recorded on the Activity, as every Activity requires.</summary>
    public ActivityInitiatorType InitiatedByType { get; init; } = ActivityInitiatorType.NotSet;

    public Guid? InitiatedById { get; init; }

    public string? InitiatedByName { get; init; }
}
