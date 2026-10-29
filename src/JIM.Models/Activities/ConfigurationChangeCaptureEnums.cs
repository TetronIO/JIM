// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// What configuration change capture found when it compared a saved configuration with the latest snapshot of the
/// same object (#2615). Callers use it to decide what the save must set in motion; the Activity carries the same
/// information only partly (a class, never "the capture was skipped").
/// </summary>
public enum ConfigurationChangeCaptureKind
{
    /// <summary>
    /// Configuration change tracking is switched off, so nothing was compared. What changed is unknown.
    /// </summary>
    NotTracked,

    /// <summary>
    /// The capture could not complete (the snapshot could not be built, or a repository or serialisation failure was
    /// swallowed, as capture is best-effort). What changed is unknown.
    /// </summary>
    Failed,

    /// <summary>
    /// There was no earlier snapshot to compare with, so the saved configuration is the object's first recorded
    /// version. A genuinely new object looks like this, and so does an old one whose earlier saves were not tracked.
    /// </summary>
    Created,

    /// <summary>
    /// The saved configuration is identical to the latest snapshot. Nothing changed, and no version was recorded.
    /// </summary>
    NoChange,

    /// <summary>
    /// The saved configuration differs from the latest snapshot; the outcome carries the change's class and the
    /// sections that changed.
    /// </summary>
    Changed
}
