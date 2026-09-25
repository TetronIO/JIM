// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// <see cref="ExportMatchCandidates"/> is the page-scoped cache the batch export-matching prefetch
/// populates: which (Metaverse Object, export Synchronisation Rule) pairs were covered, and which
/// Connected System Objects are candidates for a given (Object Matching Rule, value) pair.
/// </summary>
[TestFixture]
public class ExportMatchCandidatesTests
{
    #region Coverage

    [Test]
    public void IsCovered_NeverMarked_ReturnsFalse()
    {
        var candidates = new ExportMatchCandidates();

        Assert.That(candidates.IsCovered(Guid.NewGuid(), 1), Is.False);
    }

    [Test]
    public void IsCovered_AfterMarkCovered_ReturnsTrue()
    {
        var candidates = new ExportMatchCandidates();
        var mvoId = Guid.NewGuid();

        candidates.MarkCovered(mvoId, 1);

        Assert.That(candidates.IsCovered(mvoId, 1), Is.True);
    }

    [Test]
    public void IsCovered_DifferentSyncRuleId_ReturnsFalse()
    {
        var candidates = new ExportMatchCandidates();
        var mvoId = Guid.NewGuid();

        candidates.MarkCovered(mvoId, 1);

        Assert.That(candidates.IsCovered(mvoId, 2), Is.False);
    }

    [Test]
    public void IsCovered_DifferentMetaverseObjectId_ReturnsFalse()
    {
        var candidates = new ExportMatchCandidates();
        candidates.MarkCovered(Guid.NewGuid(), 1);

        Assert.That(candidates.IsCovered(Guid.NewGuid(), 1), Is.False);
    }

    #endregion

    #region AddCandidate / GetCandidates

    [Test]
    public void GetCandidates_NothingAdded_ReturnsEmptyList()
    {
        var candidates = new ExportMatchCandidates();

        var result = candidates.GetCandidates(1, "some-value");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void AddCandidate_MultipleForSameRuleAndValue_ReturnsInCallOrder()
    {
        var candidates = new ExportMatchCandidates();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        candidates.AddCandidate(1, "E12345", first);
        candidates.AddCandidate(1, "E12345", second);
        candidates.AddCandidate(1, "E12345", third);

        var result = candidates.GetCandidates(1, "E12345");

        Assert.That(result, Is.EqualTo(new[] { first, second, third }));
    }

    [Test]
    public void AddCandidate_DuplicateSameRuleValueAndId_IsIgnored()
    {
        var candidates = new ExportMatchCandidates();
        var csoId = Guid.NewGuid();

        candidates.AddCandidate(1, "E12345", csoId);
        candidates.AddCandidate(1, "E12345", csoId);

        var result = candidates.GetCandidates(1, "E12345");

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public void AddCandidate_DifferentValueSameRule_KeptSeparate()
    {
        var candidates = new ExportMatchCandidates();
        var forFirstValue = Guid.NewGuid();
        var forSecondValue = Guid.NewGuid();

        candidates.AddCandidate(1, "E12345", forFirstValue);
        candidates.AddCandidate(1, "E99999", forSecondValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates.GetCandidates(1, "E12345"), Is.EqualTo(new[] { forFirstValue }));
            Assert.That(candidates.GetCandidates(1, "E99999"), Is.EqualTo(new[] { forSecondValue }));
        }
    }

    [Test]
    public void AddCandidate_DifferentRuleSameValue_KeptSeparate()
    {
        var candidates = new ExportMatchCandidates();
        var forRuleOne = Guid.NewGuid();
        var forRuleTwo = Guid.NewGuid();

        candidates.AddCandidate(1, "E12345", forRuleOne);
        candidates.AddCandidate(2, "E12345", forRuleTwo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates.GetCandidates(1, "E12345"), Is.EqualTo(new[] { forRuleOne }));
            Assert.That(candidates.GetCandidates(2, "E12345"), Is.EqualTo(new[] { forRuleTwo }));
        }
    }

    #endregion

    #region Value-key equality semantics

    [Test]
    public void AddCandidate_StringValuesDifferByCaseOnly_AreDistinctKeys()
    {
        // Value keys use default object.Equals: strings compare ordinally, so callers relying on
        // case-insensitive matching must lower-case (or otherwise normalise) the value before keying.
        var candidates = new ExportMatchCandidates();
        var forLower = Guid.NewGuid();
        var forUpper = Guid.NewGuid();

        candidates.AddCandidate(1, "esmith", forLower);
        candidates.AddCandidate(1, "ESMITH", forUpper);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates.GetCandidates(1, "esmith"), Is.EqualTo(new[] { forLower }));
            Assert.That(candidates.GetCandidates(1, "ESMITH"), Is.EqualTo(new[] { forUpper }));
        }
    }

    [Test]
    public void AddCandidate_BoxedIntValuesEqualByValue_ShareTheSameKey()
    {
        var candidates = new ExportMatchCandidates();
        var csoId = Guid.NewGuid();

        candidates.AddCandidate(1, (object)42, csoId);

        // A distinct boxed instance carrying the same int value must still find the same key.
        var lookupValue = int.Parse("42");
        Assert.That(candidates.GetCandidates(1, lookupValue), Is.EqualTo(new[] { csoId }));
    }

    [Test]
    public void AddCandidate_BoxedGuidValuesEqualByValue_ShareTheSameKey()
    {
        var candidates = new ExportMatchCandidates();
        var guidValue = Guid.NewGuid();
        var csoId = Guid.NewGuid();

        candidates.AddCandidate(1, guidValue, csoId);

        var lookupValue = Guid.Parse(guidValue.ToString());
        Assert.That(candidates.GetCandidates(1, lookupValue), Is.EqualTo(new[] { csoId }));
    }

    [Test]
    public void AddCandidate_DecimalValuesDifferOnlyInScale_ShareTheSameKey()
    {
        // Matches PostgreSQL's scale-insensitive numeric equality (5.0 = 5.00).
        var candidates = new ExportMatchCandidates();
        var csoId = Guid.NewGuid();

        candidates.AddCandidate(1, 5.0m, csoId);

        Assert.That(candidates.GetCandidates(1, 5.00m), Is.EqualTo(new[] { csoId }));
    }

    #endregion

    #region Remove

    [Test]
    public void Remove_CandidatePresentInOneList_RemovesIt()
    {
        var candidates = new ExportMatchCandidates();
        var csoId = Guid.NewGuid();
        candidates.AddCandidate(1, "E12345", csoId);

        candidates.Remove(csoId);

        Assert.That(candidates.GetCandidates(1, "E12345"), Is.Empty);
    }

    [Test]
    public void Remove_CandidatePresentAcrossMultipleRulesAndValues_RemovesFromAll()
    {
        var candidates = new ExportMatchCandidates();
        var csoId = Guid.NewGuid();
        var otherCsoId = Guid.NewGuid();

        candidates.AddCandidate(1, "E12345", csoId);
        candidates.AddCandidate(1, "E12345", otherCsoId);
        candidates.AddCandidate(2, "different-value", csoId);

        candidates.Remove(csoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates.GetCandidates(1, "E12345"), Is.EqualTo(new[] { otherCsoId }));
            Assert.That(candidates.GetCandidates(2, "different-value"), Is.Empty);
        }
    }

    [Test]
    public void Remove_IdNeverAdded_DoesNotThrowAndLeavesOthersUnchanged()
    {
        var candidates = new ExportMatchCandidates();
        var csoId = Guid.NewGuid();
        candidates.AddCandidate(1, "E12345", csoId);

        Assert.That(() => candidates.Remove(Guid.NewGuid()), Throws.Nothing);
        Assert.That(candidates.GetCandidates(1, "E12345"), Is.EqualTo(new[] { csoId }));
    }

    [Test]
    public void Remove_NoCandidatesAddedAtAll_DoesNotThrow()
    {
        var candidates = new ExportMatchCandidates();

        Assert.That(() => candidates.Remove(Guid.NewGuid()), Throws.Nothing);
    }

    #endregion
}
