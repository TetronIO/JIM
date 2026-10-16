// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// How many reference values across a Connected System's Connector Space are unresolved, and how many Connected System
/// Objects hold them (#959). What the Connected System page's signpost shows.
/// </summary>
public class ConnectedSystemUnresolvedReferenceTotals
{
    /// <summary>How many unresolved reference values the Connected System's objects hold between them.</summary>
    public int ReferenceCount { get; set; }

    /// <summary>How many Connected System Objects hold at least one unresolved reference value.</summary>
    public int ObjectCount { get; set; }
}
