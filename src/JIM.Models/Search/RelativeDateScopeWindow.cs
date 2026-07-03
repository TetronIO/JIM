// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Search;

/// <summary>
/// Computes the window of date attribute values whose truth-value for a relative-date scoping criterion
/// could have flipped as the clock advanced from one instant to another. Used by the Temporal Scope
/// Reconciler (issue #892) to turn a relative criterion into an indexed candidate range query.
///
/// A relative criterion compares an object's stored date value against a moving boundary
/// <c>B(t) = RelativeDateResolver.Resolve(count, unit, direction, t)</c>. Because the comparison operator
/// only flips its result when the stored value crosses <c>B(t)</c>, and <c>B</c> is monotonic
/// non-decreasing in <c>t</c>, every object whose result changed as the clock moved from
/// <paramref name="afterUtc"/> to <paramref name="nowUtc"/> has its stored value in
/// <c>(B(afterUtc), B(nowUtc)]</c>. Resolving the boundary at both instants (rather than adding a raw
/// offset) makes whole-day rounding fall out correctly: with day-or-coarser units the boundary only moves
/// at midnight, so the window is empty between midnights.
///
/// The window is deliberately operator-agnostic: it identifies the objects the boundary crossed, not the
/// direction of the flip. The reconciler's in-memory full evaluation makes the final in/out-of-scope
/// decision, so a superset here is safe.
/// </summary>
public static class RelativeDateScopeWindow
{
    /// <summary>
    /// Resolves the candidate value window <c>(Lower, Upper]</c> for a relative-date criterion.
    /// </summary>
    /// <param name="count">The criterion's relative offset magnitude (zero or positive).</param>
    /// <param name="unit">The criterion's relative unit.</param>
    /// <param name="direction">The criterion's relative direction (Ago / FromNow).</param>
    /// <param name="afterUtc">The previous evaluation instant (the reconciler watermark). Null means a bootstrap
    /// sweep with no lower bound, so every object whose value is at or before the upper bound is a candidate.</param>
    /// <param name="nowUtc">The current evaluation instant, in UTC.</param>
    /// <returns>The exclusive lower bound (null when bootstrapping) and inclusive upper bound on the stored value.</returns>
    public static (DateTime? Lower, DateTime Upper) Resolve(int count, RelativeDateUnit unit, RelativeDateDirection direction, DateTime? afterUtc, DateTime nowUtc)
    {
        var upper = RelativeDateResolver.Resolve(count, unit, direction, nowUtc);
        DateTime? lower = afterUtc.HasValue
            ? RelativeDateResolver.Resolve(count, unit, direction, afterUtc.Value)
            : null;
        return (lower, upper);
    }
}
