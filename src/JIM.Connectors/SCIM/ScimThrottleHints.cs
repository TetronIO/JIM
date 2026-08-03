// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Rate-limit hints read from a service provider's response headers, letting the connector slow down
/// before the provider starts rejecting calls rather than discovering the limit through 429s.
/// <para>
/// SCIM itself does not standardise rate-limit advertising, so three conventions are supported: the
/// IETF <c>RateLimit-*</c> fields, the widespread <c>X-RateLimit-*</c> variants, and the single
/// structured <c>RateLimit</c> field. Unparseable or absent values simply yield no hint; the retry
/// policy remains the backstop for an actual 429.
/// </para>
/// </summary>
public readonly record struct ScimThrottleHints(int? Limit, int? Remaining, TimeSpan? ResetAfter)
{
    /// <summary>
    /// Values above this are read as absolute Unix epoch seconds rather than a delta, since no provider
    /// advertises a reset window measured in decades. Corresponds to 2001-09-09.
    /// </summary>
    private const long EpochHeuristicThresholdSeconds = 1_000_000_000;

    /// <summary>
    /// Whether the provider advertised anything usable.
    /// </summary>
    public bool HasHints => Limit.HasValue || Remaining.HasValue || ResetAfter.HasValue;

    /// <summary>
    /// Reads rate-limit hints from a response. Never throws on malformed provider values.
    /// </summary>
    /// <param name="response">The provider's response.</param>
    /// <param name="utcNow">Current time, used to convert an absolute reset timestamp into a duration.</param>
    public static ScimThrottleHints Read(HttpResponseMessage response, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(response);

        var structured = ReadStructuredHeader(response);

        var limit = ReadInt(response, "RateLimit-Limit") ?? ReadInt(response, "X-RateLimit-Limit") ?? structured.Limit;
        var remaining = ReadInt(response, "RateLimit-Remaining") ?? ReadInt(response, "X-RateLimit-Remaining") ?? structured.Remaining;
        var resetRaw = ReadLong(response, "RateLimit-Reset") ?? ReadLong(response, "X-RateLimit-Reset") ?? structured.ResetSeconds;

        return new ScimThrottleHints(limit, remaining, ToResetDuration(resetRaw, utcNow));
    }

    /// <summary>
    /// How long to wait before issuing the next request, or null when there is no reason to pause.
    /// </summary>
    /// <param name="remainingThreshold">
    /// Pause once the advertised remaining allowance drops to this value or below. A threshold of 1
    /// means "stop when one call is left", leaving headroom for a concurrent caller.
    /// </param>
    public TimeSpan? GetPauseBeforeNextRequest(int remainingThreshold)
    {
        if (!Remaining.HasValue || Remaining.Value > remainingThreshold)
            return null;

        // Exhausted, but the provider did not say when the window resets: there is nothing meaningful
        // to wait for, so proceed and let the retry policy handle any 429 that follows.
        if (!ResetAfter.HasValue || ResetAfter.Value <= TimeSpan.Zero)
            return null;

        return ResetAfter.Value;
    }

    private static (int? Limit, int? Remaining, long? ResetSeconds) ReadStructuredHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("RateLimit", out var values))
            return (null, null, null);

        var raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
            return (null, null, null);

        int? limit = null;
        int? remaining = null;
        long? reset = null;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();

            if (key.Equals("limit", StringComparison.OrdinalIgnoreCase) && TryParseInt(value, out var parsedLimit))
                limit = parsedLimit;
            else if (key.Equals("remaining", StringComparison.OrdinalIgnoreCase) && TryParseInt(value, out var parsedRemaining))
                remaining = parsedRemaining;
            else if (key.Equals("reset", StringComparison.OrdinalIgnoreCase) && TryParseLong(value, out var parsedReset))
                reset = parsedReset;
        }

        return (limit, remaining, reset);
    }

    private static TimeSpan? ToResetDuration(long? resetValue, DateTimeOffset utcNow)
    {
        if (!resetValue.HasValue)
            return null;

        if (resetValue.Value >= EpochHeuristicThresholdSeconds)
        {
            var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetValue.Value);
            var delay = resetAt - utcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return resetValue.Value <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(resetValue.Value);
    }

    private static int? ReadInt(HttpResponseMessage response, string headerName)
    {
        return ReadHeaderValue(response, headerName) is { } raw && TryParseInt(raw, out var value) ? value : null;
    }

    private static long? ReadLong(HttpResponseMessage response, string headerName)
    {
        return ReadHeaderValue(response, headerName) is { } raw && TryParseLong(raw, out var value) ? value : null;
    }

    private static string? ReadHeaderValue(HttpResponseMessage response, string headerName)
    {
        return response.Headers.TryGetValues(headerName, out var values) ? values.FirstOrDefault()?.Trim() : null;
    }

    private static bool TryParseInt(string raw, out int value) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryParseLong(string raw, out long value) =>
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
