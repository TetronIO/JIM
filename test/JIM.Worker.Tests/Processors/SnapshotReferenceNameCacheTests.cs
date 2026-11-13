// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// The names a synchronisation's export change snapshots give the objects their references point at (#2939): a name
/// found is kept for the whole run, and a lookup that found nothing is kept only until the page ends or its new objects
/// are written.
/// </summary>
[TestFixture]
public class SnapshotReferenceNameCacheTests
{
    private const int SystemId = 7;

    [Test]
    public void TryGet_NameFoundThenMissesForgotten_StillAnswersWithTheName()
    {
        var cache = new SnapshotReferenceNameCache();
        var mvoId = Guid.NewGuid();
        cache.RecordFound(mvoId, SystemId, "Platform Engineers");

        cache.ForgetMisses();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGet(mvoId, SystemId, out var name), Is.True);
            Assert.That(name, Is.EqualTo("Platform Engineers"));
        }
    }

    [Test]
    public void TryGet_MissRecorded_AnswersWithNoNameUntilMissesAreForgotten()
    {
        var cache = new SnapshotReferenceNameCache();
        var mvoId = Guid.NewGuid();
        cache.RecordMiss(mvoId, SystemId);

        var answeredBefore = cache.TryGet(mvoId, SystemId, out var nameBefore);
        cache.ForgetMisses();
        var answeredAfter = cache.TryGet(mvoId, SystemId, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answeredBefore, Is.True, "a miss is known for the rest of the page");
            Assert.That(nameBefore, Is.Null);
            Assert.That(answeredAfter, Is.False, "after the page, or once its new objects are written, the object is asked about again");
        }
    }

    [Test]
    public void TryGet_SameObjectInAnotherSystem_IsNotAnswered()
    {
        var cache = new SnapshotReferenceNameCache();
        var mvoId = Guid.NewGuid();
        cache.RecordFound(mvoId, SystemId, "Platform Engineers");

        Assert.That(cache.TryGet(mvoId, SystemId + 1, out _), Is.False);
    }

    [Test]
    public void RecordFound_AfterAMissForTheSameObject_AnswersWithTheName()
    {
        var cache = new SnapshotReferenceNameCache();
        var mvoId = Guid.NewGuid();
        cache.RecordMiss(mvoId, SystemId);

        cache.RecordFound(mvoId, SystemId, "Release Managers");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGet(mvoId, SystemId, out var name), Is.True);
            Assert.That(name, Is.EqualTo("Release Managers"));
        }
    }

    [Test]
    public void RecordFound_AtCapacity_KeepsWhatItHoldsAndAddsNothingMore()
    {
        var cache = new SnapshotReferenceNameCache(capacity: 2);
        var kept = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var overflow = Guid.NewGuid();
        cache.RecordFound(kept[0], SystemId, "First");
        cache.RecordFound(kept[1], SystemId, "Second");

        cache.RecordFound(overflow, SystemId, "Third");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Count, Is.EqualTo(2));
            Assert.That(cache.IsFull, Is.True);
            Assert.That(cache.TryGet(kept[0], SystemId, out var first) && first == "First", Is.True, "lookups still work once full");
            Assert.That(cache.TryGet(kept[1], SystemId, out var second) && second == "Second", Is.True);
            Assert.That(cache.TryGet(overflow, SystemId, out _), Is.False, "a name found once full is not kept");
        }
    }

    [Test]
    public void DefaultCapacity_IsOneMillionNames()
    {
        Assert.That(new SnapshotReferenceNameCache().Capacity, Is.EqualTo(1_000_000));
    }
}
