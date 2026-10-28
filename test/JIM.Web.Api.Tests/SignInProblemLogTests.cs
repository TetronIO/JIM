// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Web.Authentication;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The sign-in problems JIM has met (#2443): each distinct problem is kept once, with how often and when it was seen,
/// the log holds a bounded number of them, and each is written to the log at most every few minutes, so a stream of
/// refused tokens cannot flood it.
/// </summary>
[TestFixture]
public class SignInProblemLogTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private ManualTimeProvider _time = null!;
    private SignInProblemLog _log = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(Start);
        _log = new SignInProblemLog(_time);
    }

    [Test]
    public void Record_NewProblem_KeptOnceAndLoggedAtOnce()
    {
        var problem = _log.Record(Audience("other-api"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(problem.Setting, Is.EqualTo("JIM_SSO_API_SCOPE"));
            Assert.That(problem.Expected, Is.EqualTo("api://jim"));
            Assert.That(problem.Received, Is.EqualTo("other-api"));
            Assert.That(problem.Count, Is.EqualTo(1));
            Assert.That(problem.FirstSeen, Is.EqualTo(Start));
            Assert.That(problem.LastSeen, Is.EqualTo(Start));
            Assert.That(problem.LoggedAt, Is.EqualTo(Start));
            Assert.That(_log.GetRecent(), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Record_SameProblemAgain_CountedOnTheSameEntryAndNotLoggedAgainWithinTheInterval()
    {
        var first = _log.Record(Audience("other-api"));
        _time.Advance(SignInProblemLog.LogInterval - TimeSpan.FromSeconds(1));

        var second = _log.Record(Audience("other-api"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Id, Is.EqualTo(first.Id));
            Assert.That(second.Count, Is.EqualTo(2));
            Assert.That(second.FirstSeen, Is.EqualTo(Start));
            Assert.That(second.LastSeen, Is.EqualTo(_time.GetUtcNow()));
            Assert.That(second.LoggedAt, Is.EqualTo(Start), "not logged again within the interval");
            Assert.That(_log.GetRecent(), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Record_SameProblemAfterTheInterval_LoggedAgain()
    {
        _log.Record(Audience("other-api"));
        _time.Advance(SignInProblemLog.LogInterval);

        var again = _log.Record(Audience("other-api"));

        Assert.That(again.LoggedAt, Is.EqualTo(_time.GetUtcNow()));
    }

    [Test]
    public void Record_DifferentReceivedValue_KeptAsAnotherProblem()
    {
        var first = _log.Record(Audience("other-api"));

        var second = _log.Record(Audience("third-api"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Id, Is.Not.EqualTo(first.Id));
            Assert.That(_log.GetRecent(), Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void Record_SameValuesFromTheOtherSource_KeptAsAnotherProblem()
    {
        _log.Record(Audience("other-api"));

        _log.Record(Audience("other-api") with { Source = SignInProblemSource.Portal });

        Assert.That(_log.GetRecent(), Has.Count.EqualTo(2));
    }

    [Test]
    public void Record_MoreProblemsThanItHolds_ForgetsTheOneSeenLeastRecently()
    {
        var oldest = _log.Record(Audience("api-0"));
        for (var i = 1; i <= SignInProblemLog.Capacity; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _log.Record(Audience($"api-{i}"));
        }

        var recent = _log.GetRecent();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recent, Has.Count.EqualTo(SignInProblemLog.Capacity));
            Assert.That(recent.Select(p => p.Id), Does.Not.Contain(oldest.Id));
            Assert.That(_log.Find(oldest.Id), Is.Null);
        }
    }

    [Test]
    public void GetRecent_SeveralProblems_MostRecentlySeenFirst()
    {
        var a = _log.Record(Audience("a"));
        _time.Advance(TimeSpan.FromSeconds(1));
        var b = _log.Record(Audience("b"));
        _time.Advance(TimeSpan.FromSeconds(1));
        _log.Record(Audience("a"));

        Assert.That(_log.GetRecent().Select(p => p.Id), Is.EqualTo(new[] { a.Id, b.Id }));
    }

    [Test]
    public void Find_KnownId_ReturnsTheProblem()
    {
        var problem = _log.Record(Audience("other-api"));

        Assert.That(_log.Find(problem.Id)?.Received, Is.EqualTo("other-api"));
    }

    [Test]
    public void Find_UnknownId_ReturnsNull()
    {
        Assert.That(_log.Find(Guid.NewGuid()), Is.Null);
    }

    [Test]
    public void Record_ValueWithControlCharacters_ReplacesThem()
    {
        var problem = _log.Record(Audience("other\r\napi\u0007"));

        Assert.That(problem.Received, Is.EqualTo("other  api "));
    }

    [Test]
    public void Record_OverlongValue_ShortenedToTheLimit()
    {
        var problem = _log.Record(Audience(new string('a', SignInProblemLog.MaxValueLength * 2)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Received, Has.Length.EqualTo(SignInProblemLog.MaxValueLength));
            Assert.That(problem.Received, Does.EndWith("..."));
        }
    }

    private static SignInProblemReport Audience(string received) => new(
        SignInProblemSource.Api,
        "JIM_SSO_API_SCOPE",
        "An access token was refused for its audience.",
        "api://jim",
        received,
        "Change the setting.");

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
