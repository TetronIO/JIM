// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Connectors.SCIM;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Rate-limit hints let the connector slow down before a provider starts rejecting calls, rather than
/// discovering the limit through 429s. Providers advertise them in three different conventions, so all
/// three are parsed.
/// </summary>
[TestFixture]
public class ScimThrottleHintsTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage CreateResponse(params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        foreach (var (name, value) in headers)
            response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    [Test]
    public void Read_StandardRateLimitHeaders_ParsesAllThreeFields()
    {
        using var response = CreateResponse(
            ("RateLimit-Limit", "100"),
            ("RateLimit-Remaining", "7"),
            ("RateLimit-Reset", "30"));

        var hints = ScimThrottleHints.Read(response, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hints.Limit, Is.EqualTo(100));
            Assert.That(hints.Remaining, Is.EqualTo(7));
            Assert.That(hints.ResetAfter, Is.EqualTo(TimeSpan.FromSeconds(30)));
        }
    }

    [Test]
    public void Read_LegacyPrefixedHeaders_ParsesAllThreeFields()
    {
        using var response = CreateResponse(
            ("X-RateLimit-Limit", "50"),
            ("X-RateLimit-Remaining", "0"),
            ("X-RateLimit-Reset", "15"));

        var hints = ScimThrottleHints.Read(response, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hints.Limit, Is.EqualTo(50));
            Assert.That(hints.Remaining, Is.EqualTo(0));
            Assert.That(hints.ResetAfter, Is.EqualTo(TimeSpan.FromSeconds(15)));
        }
    }

    [Test]
    public void Read_LegacyResetAsUnixEpochSeconds_ConvertsToRemainingDuration()
    {
        // Several providers send an absolute epoch timestamp rather than a delta.
        var resetAt = Now.AddSeconds(45).ToUnixTimeSeconds();
        using var response = CreateResponse(
            ("X-RateLimit-Remaining", "2"),
            ("X-RateLimit-Reset", resetAt.ToString()));

        var hints = ScimThrottleHints.Read(response, Now);

        Assert.That(hints.ResetAfter, Is.EqualTo(TimeSpan.FromSeconds(45)));
    }

    [Test]
    public void Read_StructuredRateLimitHeader_ParsesKeyedFields()
    {
        using var response = CreateResponse(("RateLimit", "limit=100, remaining=4, reset=20"));

        var hints = ScimThrottleHints.Read(response, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hints.Limit, Is.EqualTo(100));
            Assert.That(hints.Remaining, Is.EqualTo(4));
            Assert.That(hints.ResetAfter, Is.EqualTo(TimeSpan.FromSeconds(20)));
        }
    }

    [Test]
    public void Read_NoRateLimitHeaders_ReturnsEmptyHints()
    {
        using var response = CreateResponse();

        var hints = ScimThrottleHints.Read(response, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hints.Limit, Is.Null);
            Assert.That(hints.Remaining, Is.Null);
            Assert.That(hints.ResetAfter, Is.Null);
            Assert.That(hints.HasHints, Is.False);
        }
    }

    [Test]
    public void Read_UnparseableValues_AreIgnoredRatherThanThrowing()
    {
        using var response = CreateResponse(
            ("RateLimit-Remaining", "plenty"),
            ("RateLimit-Reset", "later"));

        var hints = ScimThrottleHints.Read(response, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hints.Remaining, Is.Null);
            Assert.That(hints.ResetAfter, Is.Null);
        }
    }

    [Test]
    public void GetPauseBeforeNextRequest_RemainingAtOrBelowThreshold_PausesUntilReset()
    {
        using var response = CreateResponse(("RateLimit-Remaining", "1"), ("RateLimit-Reset", "12"));
        var hints = ScimThrottleHints.Read(response, Now);

        var pause = hints.GetPauseBeforeNextRequest(remainingThreshold: 1);

        Assert.That(pause, Is.EqualTo(TimeSpan.FromSeconds(12)));
    }

    [Test]
    public void GetPauseBeforeNextRequest_PlentyRemaining_DoesNotPause()
    {
        using var response = CreateResponse(("RateLimit-Remaining", "80"), ("RateLimit-Reset", "12"));
        var hints = ScimThrottleHints.Read(response, Now);

        var pause = hints.GetPauseBeforeNextRequest(remainingThreshold: 1);

        Assert.That(pause, Is.Null);
    }

    [Test]
    public void GetPauseBeforeNextRequest_ExhaustedButNoResetAdvertised_DoesNotPauseIndefinitely()
    {
        // Without a reset hint there is nothing to wait for; the retry policy handles the 429 that follows.
        using var response = CreateResponse(("RateLimit-Remaining", "0"));
        var hints = ScimThrottleHints.Read(response, Now);

        var pause = hints.GetPauseBeforeNextRequest(remainingThreshold: 1);

        Assert.That(pause, Is.Null);
    }
}
