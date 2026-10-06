// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// Counts a preview's impact from its delta stream (#1530), for an adapter that can only count by evaluating. Fed
/// every delta of one evaluation pass, it builds the stage 2 counts that pass implies, so the framework need not
/// evaluate the population a second time to count it.
/// </summary>
public interface IPreviewImpactCounter
{
    /// <summary>
    /// Counts one delta of the stream.
    /// </summary>
    void Add(PreviewDelta delta);

    /// <summary>
    /// The counts for every delta added, largest transition first.
    /// </summary>
    List<PreviewImpactCount> Build();
}
