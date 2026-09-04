// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Utility;

/// <summary>
/// A single, shared answer to "is this count too large a share of that base?", used everywhere JIM
/// compares a count against a configured percentage threshold: the #1605 post-clear reconciliation
/// shortfall check (<c>ConnectedSystemServer.StrandedValueSweep.IsReconciliationRefused</c>) and the
/// #1618 Run Profile Safeguards Full Import deletion detection limit. Both need to agree on exactly
/// where the boundary falls, so the maths lives once.
/// </summary>
public static class ShareThreshold
{
    /// <summary>
    /// Whether <paramref name="count"/> is more than <paramref name="maxPercent"/> percent of
    /// <paramref name="baseCount"/>, decided by cross-multiplication so no rounding can move the
    /// boundary. A <paramref name="baseCount"/> of zero or less never exceeds: there is nothing to
    /// compare against, so the share is undefined rather than infinite.
    /// </summary>
    /// <param name="count">The count being checked against the share.</param>
    /// <param name="baseCount">The total the share is measured against.</param>
    /// <param name="maxPercent">The maximum allowed share, as a whole-number percentage (0 to 100).</param>
    public static bool Exceeds(long count, long baseCount, int maxPercent)
    {
        if (baseCount <= 0)
            return false;

        return count * 100 > (long)maxPercent * baseCount;
    }
}
