// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Shared;

/// <summary>
/// What an administrator chose when asked about running from an out-of-date preview (#2022). Cancelling closes the
/// dialog with no choice at all.
/// </summary>
public enum OutOfDatePreviewChoice
{
    /// <summary>Run the preview again for a current answer, and run nothing yet.</summary>
    PreviewAgain = 0,

    /// <summary>Run now, citing the preview; the run records that it was out of date.</summary>
    RunAnyway = 1
}
