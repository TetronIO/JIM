// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Search;

/// <summary>
/// Resolves a relative date/time criterion ("now plus/minus N units") to a concrete UTC boundary.
/// Pure and deterministic: the caller supplies "now" so the result is unit-testable and so a single
/// evaluation pass (a sync run, or a search execution) can resolve the boundary once and reuse it.
///
/// Rules (see the relative-date PRD): <see cref="RelativeDateDirection.FromNow"/> adds, <see cref="RelativeDateDirection.Ago"/>
/// subtracts; month and year arithmetic is calendar-correct (it clamps short months, e.g. 31 Mar minus one
/// month is 28/29 Feb); every unit except <see cref="RelativeDateUnit.Hours"/> is rounded down to midnight UTC
/// (whole-day rounding), while Hours keeps exact-instant precision. The result is always UTC-kinded.
/// Shared by the scoping evaluator (in-memory comparison) and the search query translator (resolve-before-query).
/// </summary>
public static class RelativeDateResolver
{
    /// <summary>
    /// Resolves a relative offset to a concrete UTC <see cref="DateTime"/>.
    /// </summary>
    /// <param name="count">The number of units to offset by. Must be zero or positive (direction carries the sign).</param>
    /// <param name="unit">The unit of the offset.</param>
    /// <param name="direction">Whether the offset is in the past (Ago) or the future (FromNow).</param>
    /// <param name="nowUtc">The current time, in UTC. The caller passes this so resolution is deterministic.</param>
    /// <returns>The resolved boundary as a UTC <see cref="DateTime"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count"/> is negative or the unit is unsupported.</exception>
    public static DateTime Resolve(int count, RelativeDateUnit unit, RelativeDateDirection direction, DateTime nowUtc)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Relative date count must be zero or positive; use the direction to offset into the past or future.");

        var signedCount = direction == RelativeDateDirection.Ago ? -count : count;

        var result = unit switch
        {
            RelativeDateUnit.Hours => nowUtc.AddHours(signedCount),
            RelativeDateUnit.Days => nowUtc.AddDays(signedCount),
            RelativeDateUnit.Weeks => nowUtc.AddDays(signedCount * 7),
            RelativeDateUnit.Months => nowUtc.AddMonths(signedCount),
            RelativeDateUnit.Years => nowUtc.AddYears(signedCount),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unsupported relative date unit.")
        };

        // Whole-day rounding for every unit except Hours, which keeps instant precision.
        // DateTime.Date drops the Kind, so re-assert UTC on the final value.
        if (unit != RelativeDateUnit.Hours)
            result = result.Date;

        return DateTime.SpecifyKind(result, DateTimeKind.Utc);
    }

    /// <summary>
    /// Renders a relative offset in plain language, for example "7 days from now" or "30 days ago".
    /// Used by criterion <c>ToString()</c> (the UI chips) and the editor's live preview so the wording is consistent.
    /// </summary>
    public static string Describe(int count, RelativeDateUnit unit, RelativeDateDirection direction)
    {
        var unitText = unit.ToString().ToLowerInvariant();
        if (count == 1 && unitText.EndsWith('s'))
            unitText = unitText[..^1];

        var directionText = direction == RelativeDateDirection.FromNow ? "from now" : "ago";
        return $"{count} {unitText} {directionText}";
    }
}
