// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Where a Connected System's latest preview of one kind stands: its deletion impact, as the delete dialog and the Danger
/// Zone state it (#134), or a Full Synchronisation, as the Connected System page states it (#1530).
/// </summary>
public enum ConnectedSystemPreviewStatus
{
    /// <summary>No preview of this kind has been run for the system.</summary>
    NotPreviewed = 0,

    /// <summary>A preview is still evaluating.</summary>
    Running = 1,

    /// <summary>A preview finished and nothing that could change its answer has happened since.</summary>
    Current = 2,

    /// <summary>A preview finished, but data or configuration has moved since it started.</summary>
    Stale = 3,

    /// <summary>The latest preview failed, was cancelled, or was blocked before evaluating anything.</summary>
    DidNotFinish = 4
}
