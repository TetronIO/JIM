// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Connectors.SCIM;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The retry policy decides whether a failed SCIM call is worth repeating and how long to wait.
/// It returns decisions rather than sleeping, so every case here runs without real delay.
/// </summary>
[TestFixture]
public class ScimRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Zero jitter keeps the delay arithmetic deterministic; jitter has its own test below.
    /// </summary>
    private static ScimRetryPolicy CreatePolicy(int maxRetries = 3, int baseDelayMs = 1000, int maxDelaySeconds = 300)
    {
        return new ScimRetryPolicy(maxRetries, TimeSpan.FromMilliseconds(baseDelayMs), TimeSpan.FromSeconds(maxDelaySeconds), jitterFactor: 0);
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(statusCode);
        if (retryAfter != null)
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    #region transient classification

    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.ServiceUnavailable)]
    [TestCase(HttpStatusCode.GatewayTimeout)]
    [TestCase(HttpStatusCode.BadGateway)]
    [TestCase(HttpStatusCode.RequestTimeout)]
    public void EvaluateResponse_TransientStatus_Retries(HttpStatusCode statusCode)
    {
        using var response = CreateResponse(statusCode);

        var decision = CreatePolicy().EvaluateResponse(response, attempt: 1, Now);

        Assert.That(decision.ShouldRetry, Is.True, $"{statusCode} should be treated as transient.");
    }

    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.Forbidden)]
    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.Conflict)]
    [TestCase(HttpStatusCode.InternalServerError)]
    [TestCase(HttpStatusCode.NotImplemented)]
    public void EvaluateResponse_PermanentStatus_DoesNotRetry(HttpStatusCode statusCode)
    {
        using var response = CreateResponse(statusCode);

        var decision = CreatePolicy().EvaluateResponse(response, attempt: 1, Now);

        Assert.That(decision.ShouldRetry, Is.False, $"{statusCode} should not be retried.");
    }

    [Test]
    public void EvaluateResponse_SuccessStatus_DoesNotRetry()
    {
        using var response = CreateResponse(HttpStatusCode.OK);

        var decision = CreatePolicy().EvaluateResponse(response, attempt: 1, Now);

        Assert.That(decision.ShouldRetry, Is.False);
    }

    [Test]
    public void EvaluateResponse_AttemptsExhausted_DoesNotRetry()
    {
        using var response = CreateResponse(HttpStatusCode.TooManyRequests);

        var decision = CreatePolicy(maxRetries: 3).EvaluateResponse(response, attempt: 3, Now);

        Assert.Multiple(() =>
        {
            Assert.That(decision.ShouldRetry, Is.False);
            Assert.That(decision.Reason, Does.Contain("attempt").IgnoreCase);
        });
    }

    #endregion

    #region Retry-After handling

    [Test]
    public void EvaluateResponse_RetryAfterDeltaSeconds_UsesProviderDelay()
    {
        using var response = CreateResponse(HttpStatusCode.TooManyRequests, "42");

        var decision = CreatePolicy().EvaluateResponse(response, attempt: 1, Now);

        Assert.Multiple(() =>
        {
            Assert.That(decision.ShouldRetry, Is.True);
            Assert.That(decision.Delay, Is.EqualTo(TimeSpan.FromSeconds(42)));
        });
    }

    [Test]
    public void EvaluateResponse_RetryAfterHttpDate_UsesDelayUntilThatInstant()
    {
        var retryAt = Now.AddSeconds(90);
        using var response = CreateResponse(HttpStatusCode.ServiceUnavailable, retryAt.ToString("R"));

        var decision = CreatePolicy().EvaluateResponse(response, attempt: 1, Now);

        Assert.Multiple(() =>
        {
            Assert.That(decision.ShouldRetry, Is.True);
            Assert.That(decision.Delay, Is.EqualTo(TimeSpan.FromSeconds(90)));
        });
    }

    [Test]
    public void EvaluateResponse_RetryAfterInThePast_RetriesImmediately()
    {
        using var response = CreateResponse(HttpStatusCode.ServiceUnavailable, Now.AddSeconds(-30).ToString("R"));

        var decision = CreatePolicy().EvaluateResponse(response, attempt: 1, Now);

        Assert.Multiple(() =>
        {
            Assert.That(decision.ShouldRetry, Is.True);
            Assert.That(decision.Delay, Is.EqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public void EvaluateResponse_RetryAfterBeyondMaximumDelay_DoesNotRetry()
    {
        // Honouring a very distant Retry-After would stall the run for as long as the provider asks.
        // Giving up and surfacing the throttle is safer than an unbounded wait.
        using var response = CreateResponse(HttpStatusCode.TooManyRequests, "3600");

        var decision = CreatePolicy(maxDelaySeconds: 300).EvaluateResponse(response, attempt: 1, Now);

        Assert.Multiple(() =>
        {
            Assert.That(decision.ShouldRetry, Is.False);
            Assert.That(decision.Reason, Does.Contain("Retry-After"));
        });
    }

    [Test]
    public void EvaluateResponse_UnparseableRetryAfter_FallsBackToBackoff()
    {
        using var response = CreateResponse(HttpStatusCode.TooManyRequests, "soon-ish");

        var decision = CreatePolicy(baseDelayMs: 1000).EvaluateResponse(response, attempt: 1, Now);

        Assert.Multiple(() =>
        {
            Assert.That(decision.ShouldRetry, Is.True);
            Assert.That(decision.Delay, Is.EqualTo(TimeSpan.FromMilliseconds(1000)));
        });
    }

    #endregion

    #region exponential backoff

    [TestCase(1, 1000)]
    [TestCase(2, 2000)]
    [TestCase(3, 4000)]
    public void EvaluateResponse_NoRetryAfter_BacksOffExponentially(int attempt, int expectedMs)
    {
        using var response = CreateResponse(HttpStatusCode.ServiceUnavailable);

        var decision = CreatePolicy(maxRetries: 5, baseDelayMs: 1000).EvaluateResponse(response, attempt, Now);

        Assert.Multiple(() =>
        {
            Assert.That(decision.ShouldRetry, Is.True);
            Assert.That(decision.Delay, Is.EqualTo(TimeSpan.FromMilliseconds(expectedMs)));
        });
    }

    [Test]
    public void EvaluateResponse_BackoffExceedsMaximum_ClampsToMaximum()
    {
        using var response = CreateResponse(HttpStatusCode.ServiceUnavailable);

        var decision = CreatePolicy(maxRetries: 20, baseDelayMs: 1000, maxDelaySeconds: 10)
            .EvaluateResponse(response, attempt: 12, Now);

        Assert.Multiple(() =>
        {
            Assert.That(decision.ShouldRetry, Is.True, "a computed backoff longer than the cap is clamped, not abandoned.");
            Assert.That(decision.Delay, Is.EqualTo(TimeSpan.FromSeconds(10)));
        });
    }

    [Test]
    public void EvaluateResponse_WithJitter_DelayStaysWithinConfiguredBounds()
    {
        var policy = new ScimRetryPolicy(3, TimeSpan.FromMilliseconds(1000), TimeSpan.FromSeconds(300), jitterFactor: 0.25);

        // Sampling repeatedly guards against a jitter implementation that drifts outside its band.
        for (var i = 0; i < 50; i++)
        {
            using var response = CreateResponse(HttpStatusCode.ServiceUnavailable);
            var decision = policy.EvaluateResponse(response, attempt: 1, Now);

            Assert.That(decision.Delay, Is.InRange(TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1250)));
        }
    }

    #endregion

    #region exception classification

    [Test]
    public void EvaluateException_HttpRequestException_Retries()
    {
        var decision = CreatePolicy().EvaluateException(new HttpRequestException("connection reset"), attempt: 1);

        Assert.That(decision.ShouldRetry, Is.True);
    }

    [Test]
    public void EvaluateException_TimeoutFromHttpClient_Retries()
    {
        // HttpClient surfaces its own timeout as a TaskCanceledException wrapping a TimeoutException.
        var timeout = new TaskCanceledException("timed out", new TimeoutException());

        var decision = CreatePolicy().EvaluateException(timeout, attempt: 1);

        Assert.That(decision.ShouldRetry, Is.True);
    }

    [Test]
    public void EvaluateException_CallerCancellation_DoesNotRetry()
    {
        // An aborting run profile must propagate immediately; retrying would ignore the cancellation.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new OperationCanceledException("run cancelled", cts.Token);

        var decision = CreatePolicy().EvaluateException(cancelled, attempt: 1);

        Assert.That(decision.ShouldRetry, Is.False);
    }

    [Test]
    public void EvaluateException_UnexpectedException_DoesNotRetry()
    {
        var decision = CreatePolicy().EvaluateException(new InvalidOperationException("bug"), attempt: 1);

        Assert.That(decision.ShouldRetry, Is.False);
    }

    #endregion
}
