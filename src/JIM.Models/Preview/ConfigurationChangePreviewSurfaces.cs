// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Preview;

/// <summary>
/// Maps a preview surface onto the Activity vocabulary. A preview run is an Activity, and an Activity records the
/// configuration object it targeted in a per-type column selected by its <see cref="ActivityTargetType"/>; without
/// this mapping a preview Activity would attach to nothing and be unfindable from the object it previewed.
/// </summary>
public static class ConfigurationChangePreviewSurfaces
{
    /// <summary>
    /// The Activity target type a preview of <paramref name="surface"/> is recorded under.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The surface is <see cref="ConfigurationChangePreviewSurface.NotSet"/>, or is a value with no mapping. Both
    /// mean a caller has gone wrong in a way that would otherwise produce an orphaned Activity, so this throws
    /// rather than answering a plausible default.
    /// </exception>
    public static ActivityTargetType ToActivityTargetType(ConfigurationChangePreviewSurface surface) => surface switch
    {
        ConfigurationChangePreviewSurface.SynchronisationRule => ActivityTargetType.SynchronisationRule,
        ConfigurationChangePreviewSurface.SynchronisationRuleScope => ActivityTargetType.SynchronisationRule,
        ConfigurationChangePreviewSurface.SynchronisationRuleAttributeFlow => ActivityTargetType.SynchronisationRule,
        ConfigurationChangePreviewSurface.SynchronisationRuleBehaviour => ActivityTargetType.SynchronisationRule,
        ConfigurationChangePreviewSurface.ConnectedSystem => ActivityTargetType.ConnectedSystem,
        ConfigurationChangePreviewSurface.MetaverseObjectType => ActivityTargetType.MetaverseObjectType,
        ConfigurationChangePreviewSurface.MetaverseAttribute => ActivityTargetType.MetaverseAttribute,
        ConfigurationChangePreviewSurface.ObjectMatching => ActivityTargetType.ObjectMatchingRule,
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface,
            "Preview surface has no Activity target type. Add it here when adding the surface.")
    };
}
