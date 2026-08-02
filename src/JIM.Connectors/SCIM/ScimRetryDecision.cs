// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM;

/// <summary>
/// The outcome of asking <see cref="ScimRetryPolicy"/> whether a failed call should be repeated.
/// </summary>
/// <param name="ShouldRetry">Whether the caller should wait <paramref name="Delay"/> and try again.</param>
/// <param name="Delay">How long to wait before the next attempt. <see cref="TimeSpan.Zero"/> means retry immediately.</param>
/// <param name="Reason">A short, loggable explanation of the decision. Never contains provider-supplied text.</param>
public readonly record struct ScimRetryDecision(bool ShouldRetry, TimeSpan Delay, string Reason)
{
    /// <summary>
    /// A decision not to retry, carrying the reason for the run's logs and Activity.
    /// </summary>
    public static ScimRetryDecision DoNotRetry(string reason) => new(false, TimeSpan.Zero, reason);

    /// <summary>
    /// A decision to retry after the given delay.
    /// </summary>
    public static ScimRetryDecision RetryAfter(TimeSpan delay, string reason) => new(true, delay, reason);
}
