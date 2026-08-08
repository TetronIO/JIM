// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Decides whether a failed SCIM call is worth repeating and how long to wait first.
/// <para>
/// The policy returns a <see cref="ScimRetryDecision"/> rather than sleeping, which keeps the waiting
/// (and its cancellation) with the caller and makes every rule here unit-testable without real delays.
/// It mirrors the intent of <c>LdapConnector.ExecuteWithRetry</c>/<c>IsTransientError</c>, adapted to
/// HTTP semantics: a service provider's <c>Retry-After</c> is authoritative when present, and
/// exponential backoff with jitter applies otherwise.
/// </para>
/// </summary>
public class ScimRetryPolicy
{
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly double _jitterFactor;

    /// <param name="maxRetries">Maximum retry attempts after the initial call.</param>
    /// <param name="baseDelay">The first backoff delay; subsequent attempts double it.</param>
    /// <param name="maxDelay">
    /// Upper bound on any single wait. A computed backoff longer than this is clamped, but a
    /// <c>Retry-After</c> beyond it abandons the retry instead: the provider has told us it will not be
    /// ready for longer than we are willing to stall the run, so surfacing the throttle beats hanging.
    /// </param>
    /// <param name="jitterFactor">
    /// Fraction of the computed backoff added as random jitter (0.2 means up to 20% extra), spreading
    /// retries so parallel exports do not resynchronise into a thundering herd. Zero disables jitter.
    /// </param>
    public ScimRetryPolicy(int maxRetries, TimeSpan baseDelay, TimeSpan maxDelay, double jitterFactor = 0.2)
    {
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetries), "Maximum retries cannot be negative.");
        if (baseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseDelay), "Base delay cannot be negative.");
        if (maxDelay < baseDelay)
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "Maximum delay cannot be shorter than the base delay.");
        if (jitterFactor is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(jitterFactor), "Jitter factor must be between 0 and 1.");

        _maxRetries = maxRetries;
        _baseDelay = baseDelay;
        _maxDelay = maxDelay;
        _jitterFactor = jitterFactor;
    }

    /// <summary>
    /// Evaluates a response that the caller has judged unsuccessful.
    /// </summary>
    /// <param name="response">The provider's response.</param>
    /// <param name="attempt">The 1-based number of attempts already made.</param>
    /// <param name="utcNow">Current time, used to resolve an HTTP-date <c>Retry-After</c>.</param>
    public ScimRetryDecision EvaluateResponse(HttpResponseMessage response, int attempt, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsSuccessStatusCode)
            return ScimRetryDecision.DoNotRetry("The response was successful.");

        if (!IsTransientStatusCode(response.StatusCode))
            return ScimRetryDecision.DoNotRetry($"HTTP {(int)response.StatusCode} is not a transient failure.");

        if (attempt >= _maxRetries)
            return ScimRetryDecision.DoNotRetry($"All {_maxRetries} retry attempts have been used.");

        var retryAfter = ReadRetryAfter(response, utcNow);
        if (retryAfter.HasValue)
        {
            if (retryAfter.Value > _maxDelay)
                return ScimRetryDecision.DoNotRetry(
                    $"The provider's Retry-After of {retryAfter.Value.TotalSeconds:F0}s exceeds the maximum delay of {_maxDelay.TotalSeconds:F0}s.");

            // A provider-supplied delay is honoured exactly; adding jitter to it would undercut the
            // very window the provider asked us to wait for.
            return ScimRetryDecision.RetryAfter(retryAfter.Value,
                $"HTTP {(int)response.StatusCode}; honouring the provider's Retry-After.");
        }

        return ScimRetryDecision.RetryAfter(CalculateBackoff(attempt),
            $"HTTP {(int)response.StatusCode} is transient; backing off.");
    }

    /// <summary>
    /// Evaluates an exception raised while sending the request.
    /// </summary>
    /// <param name="exception">The exception thrown by the HTTP stack.</param>
    /// <param name="attempt">The 1-based number of attempts already made.</param>
    public ScimRetryDecision EvaluateException(Exception exception, int attempt)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (!IsTransientException(exception))
            return ScimRetryDecision.DoNotRetry($"{exception.GetType().Name} is not a transient failure.");

        if (attempt >= _maxRetries)
            return ScimRetryDecision.DoNotRetry($"All {_maxRetries} retry attempts have been used.");

        return ScimRetryDecision.RetryAfter(CalculateBackoff(attempt), $"{exception.GetType().Name} is transient; backing off.");
    }

    /// <summary>
    /// Status codes worth repeating. Note the deliberate omissions: 500 and 501 are not retried,
    /// because a genuine server-side fault or an unimplemented operation will not resolve itself
    /// within a run, and repeating it only delays the error reaching the administrator. 502 is
    /// included because reverse proxies in front of SCIM providers emit it for transient upstream
    /// blips rather than for a settled failure.
    /// </summary>
    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.RequestTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.GatewayTimeout => true,
            _ => false
        };
    }

    /// <summary>
    /// Network-level faults worth repeating. Caller-driven cancellation is deliberately excluded:
    /// an aborting run profile must propagate rather than grind through retries.
    /// </summary>
    private static bool IsTransientException(Exception exception)
    {
        return exception switch
        {
            HttpRequestException => true,
            TimeoutException => true,
            // HttpClient surfaces its own timeout as a cancellation wrapping a TimeoutException;
            // a cancellation without that inner exception came from the caller's token.
            OperationCanceledException canceled => canceled.InnerException is TimeoutException,
            _ => false
        };
    }

    /// <summary>
    /// Reads <c>Retry-After</c> in either RFC 9110 form: delta-seconds, or an HTTP date.
    /// Returns null when the header is absent or unparseable, so the caller falls back to backoff.
    /// A date already in the past yields <see cref="TimeSpan.Zero"/> rather than a negative delay.
    /// </summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset utcNow)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return null;

        var raw = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(raw))
            return null;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var retryAt))
        {
            var delay = retryAt - utcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    /// <summary>
    /// Exponential backoff (base, 2x, 4x, ...) clamped to the maximum delay, plus optional jitter.
    /// </summary>
    private TimeSpan CalculateBackoff(int attempt)
    {
        // Compute in doubles so a large attempt number saturates rather than overflowing ticks.
        var exponentialMs = _baseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var cappedMs = Math.Min(exponentialMs, _maxDelay.TotalMilliseconds);
        var jitterMs = _jitterFactor > 0 ? cappedMs * _jitterFactor * NextUnitDouble() : 0;

        return TimeSpan.FromMilliseconds(cappedMs + jitterMs);
    }

    /// <summary>
    /// A random value in [0, 1). Sourced from <see cref="RandomNumberGenerator"/> so no code path in
    /// JIM reaches for <c>System.Random</c>, even for a non-security-sensitive value such as jitter.
    /// </summary>
    private static double NextUnitDouble()
    {
        const int precision = 1_000_000;
        return RandomNumberGenerator.GetInt32(precision) / (double)precision;
    }
}
