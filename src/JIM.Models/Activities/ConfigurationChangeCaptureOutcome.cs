// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// What configuration change capture decided about a save (#2615): whether anything changed, how consequential the
/// change is, and which sections of the configuration it touched. Returned by the capture so the save can act on the
/// result without re-deriving it from the Activity, whose class stays unset for a create and for a capture that did
/// not run.
/// </summary>
/// <param name="Kind">What the capture found.</param>
/// <param name="Class">How consequential the change is. <see cref="ConfigurationChangeClass.NotClassified"/> unless
/// <paramref name="Kind"/> is <see cref="ConfigurationChangeCaptureKind.Changed"/>, and also when a changed property
/// has no classification.</param>
/// <param name="ChangedSections">The labels of the top-level sections of the configuration that changed (for example
/// "Scope" or "Enabled"), for readable reasons. Empty unless <paramref name="Kind"/> is
/// <see cref="ConfigurationChangeCaptureKind.Changed"/>.</param>
public sealed record ConfigurationChangeCaptureOutcome(
    ConfigurationChangeCaptureKind Kind,
    ConfigurationChangeClass Class,
    IReadOnlyList<string> ChangedSections)
{
    /// <summary>Tracking is off; nothing was compared.</summary>
    public static ConfigurationChangeCaptureOutcome NotTracked { get; } = new(ConfigurationChangeCaptureKind.NotTracked, ConfigurationChangeClass.NotClassified, []);

    /// <summary>The capture could not complete.</summary>
    public static ConfigurationChangeCaptureOutcome Failed { get; } = new(ConfigurationChangeCaptureKind.Failed, ConfigurationChangeClass.NotClassified, []);

    /// <summary>There was no earlier snapshot to compare with.</summary>
    public static ConfigurationChangeCaptureOutcome Created { get; } = new(ConfigurationChangeCaptureKind.Created, ConfigurationChangeClass.NotClassified, []);

    /// <summary>The saved configuration is identical to the latest snapshot.</summary>
    public static ConfigurationChangeCaptureOutcome NoChange { get; } = new(ConfigurationChangeCaptureKind.NoChange, ConfigurationChangeClass.NotClassified, []);

    /// <summary>The saved configuration differs from the latest snapshot.</summary>
    public static ConfigurationChangeCaptureOutcome Changed(ConfigurationChangeClass changeClass, IReadOnlyList<string> changedSections) =>
        new(ConfigurationChangeCaptureKind.Changed, changeClass, changedSections);
}
