// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// <see cref="ExpressionMatchValueIndex"/> holds what an Object Matching Rule's expression computes for each unjoined
/// object in a target (#2052), so export matching can look a Metaverse Object's values up without evaluating the
/// expression over every object for every lookup.
/// </summary>
[TestFixture]
public class ExpressionMatchValueIndexTests
{
    [Test]
    public void GetCandidates_ValueAdded_ReturnsTheObjectsInTheOrderAdded()
    {
        var index = new ExpressionMatchValueIndex(caseSensitive: true);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        index.Add("jsmith", first);
        index.Add("jsmith", second);
        index.Add("jsmith", first);

        Assert.That(index.GetCandidates("jsmith"), Is.EqualTo(new[] { first, second }));
    }

    [Test]
    public void GetCandidates_CaseInsensitive_FindsOtherCasings()
    {
        var index = new ExpressionMatchValueIndex(caseSensitive: false);
        var id = Guid.NewGuid();

        index.Add("JSmith", id);

        Assert.That(index.GetCandidates("jsmith"), Is.EqualTo(new[] { id }));
    }

    [Test]
    public void GetCandidates_CaseSensitive_DoesNotFindOtherCasings()
    {
        var index = new ExpressionMatchValueIndex(caseSensitive: true);

        index.Add("JSmith", Guid.NewGuid());

        Assert.That(index.GetCandidates("jsmith"), Is.Empty);
    }

    [Test]
    public void GetCandidates_DecimalsDifferingOnlyInScale_AreTheSameValue()
    {
        var index = new ExpressionMatchValueIndex(caseSensitive: true);
        var id = Guid.NewGuid();

        index.Add(5.0m, id);

        Assert.That(index.GetCandidates(5.00m), Is.EqualTo(new[] { id }));
    }

    [Test]
    public void Remove_ObjectIndexedUnderSeveralValues_IsGoneFromEvery()
    {
        var index = new ExpressionMatchValueIndex(caseSensitive: true);
        var claimed = Guid.NewGuid();
        var other = Guid.NewGuid();
        index.Add("a", claimed);
        index.Add("b", claimed);
        index.Add("b", other);

        index.Remove(claimed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(index.GetCandidates("a"), Is.Empty);
            Assert.That(index.GetCandidates("b"), Is.EqualTo(new[] { other }));
        }
    }

    [Test]
    public void IsComplete_AnObjectCouldNotBeChecked_IsFalseAndCountsIt()
    {
        var index = new ExpressionMatchValueIndex(caseSensitive: true);
        Assert.That(index.IsComplete, Is.True, "precondition");
        var first = Guid.NewGuid();

        index.MarkUncheckable(first, "The expression could not be evaluated.");
        index.MarkUncheckable(Guid.NewGuid(), "Another reason.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(index.IsComplete, Is.False);
            Assert.That(index.UncheckableCount, Is.EqualTo(2));
            Assert.That(index.FirstUncheckable, Is.EqualTo((first, "The expression could not be evaluated.")),
                "the first is kept, so the error can point at an object to look at");
        }
    }
}
