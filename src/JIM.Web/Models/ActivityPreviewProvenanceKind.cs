// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// What an Activity says about the Configuration Change Preview behind it (#134).
/// </summary>
public enum ActivityPreviewProvenanceKind
{
    /// <summary>Nothing: the change has no preview, and none was expected of it.</summary>
    NotApplicable = 0,

    /// <summary>The administrator read a preview before making the change.</summary>
    InformedByPreview = 1,

    /// <summary>A Connected System was deleted with no current preview behind the decision.</summary>
    WentAheadWithoutAPreview = 2
}
