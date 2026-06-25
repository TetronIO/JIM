// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using JIM.Application.Servers;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Verifies the value-tracking semantics the example data generator depends on, and that they remain correct under the
/// parallel generation loop. The store replaced a List + global-lock + linear-scan approach that was O(n^2) in the number
/// of generated objects; these tests pin the exact behaviour that must be preserved (unique-int suffixing, single-use
/// value reservation, sequential numbering) and prove the lock-free implementation does not hand out duplicates when
/// many threads hit the same key concurrently.
/// </summary>
[TestFixture]
public class ExampleDataValueTrackerStoreTests
{
    private const int ObjectTypeId = 1;
    private const int AttributeId = 10;

    private ExampleDataValueTrackerStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new ExampleDataValueTrackerStore();
    }

    #region NextUniqueIntSuffix

    [Test]
    public void NextUniqueIntSuffix_FirstUseOfBaseValue_ReturnsOne()
    {
        // 1 is the caller's signal to render the value with no suffix (the value is unique so far).
        Assert.That(_store.NextUniqueIntSuffix(ObjectTypeId, AttributeId, "joe.bloggs@panoply.local"), Is.EqualTo(1));
    }

    [Test]
    public void NextUniqueIntSuffix_RepeatedBaseValue_IncrementsFromTwo()
    {
        const string baseValue = "joe.bloggs@panoply.local";

        Assert.That(_store.NextUniqueIntSuffix(ObjectTypeId, AttributeId, baseValue), Is.EqualTo(1));
        Assert.That(_store.NextUniqueIntSuffix(ObjectTypeId, AttributeId, baseValue), Is.EqualTo(2));
        Assert.That(_store.NextUniqueIntSuffix(ObjectTypeId, AttributeId, baseValue), Is.EqualTo(3));
    }

    [Test]
    public void NextUniqueIntSuffix_DifferentBaseValues_TrackedIndependently()
    {
        Assert.That(_store.NextUniqueIntSuffix(ObjectTypeId, AttributeId, "ada.lovelace@panoply.local"), Is.EqualTo(1));
        Assert.That(_store.NextUniqueIntSuffix(ObjectTypeId, AttributeId, "grace.hopper@panoply.local"), Is.EqualTo(1));
    }

    [Test]
    public void NextUniqueIntSuffix_SameBaseValueDifferentAttribute_TrackedIndependently()
    {
        const string baseValue = "shared.value";

        Assert.That(_store.NextUniqueIntSuffix(ObjectTypeId, AttributeId, baseValue), Is.EqualTo(1));
        Assert.That(_store.NextUniqueIntSuffix(ObjectTypeId, AttributeId + 1, baseValue), Is.EqualTo(1));
    }

    [Test]
    public void NextUniqueIntSuffix_SameBaseValueDifferentObjectType_TrackedIndependently()
    {
        const string baseValue = "shared.value";

        Assert.That(_store.NextUniqueIntSuffix(ObjectTypeId, AttributeId, baseValue), Is.EqualTo(1));
        Assert.That(_store.NextUniqueIntSuffix(ObjectTypeId + 1, AttributeId, baseValue), Is.EqualTo(1));
    }

    #endregion

    #region TryReserveValue

    [Test]
    public void TryReserveValue_FirstReservation_ReturnsTrue()
    {
        Assert.That(_store.TryReserveValue(ObjectTypeId, AttributeId, "unique-value"), Is.True);
    }

    [Test]
    public void TryReserveValue_DuplicateReservation_ReturnsFalse()
    {
        Assert.That(_store.TryReserveValue(ObjectTypeId, AttributeId, "dup"), Is.True);
        Assert.That(_store.TryReserveValue(ObjectTypeId, AttributeId, "dup"), Is.False);
    }

    [Test]
    public void TryReserveValue_SameValueDifferentAttribute_BothSucceed()
    {
        Assert.That(_store.TryReserveValue(ObjectTypeId, AttributeId, "v"), Is.True);
        Assert.That(_store.TryReserveValue(ObjectTypeId, AttributeId + 1, "v"), Is.True);
    }

    #endregion

    #region NextSequential

    [Test]
    public void NextSequential_FirstCall_ReturnsSeed()
    {
        Assert.That(_store.NextSequential(ObjectTypeId, AttributeId, seed: 100001), Is.EqualTo(100001));
    }

    [Test]
    public void NextSequential_SubsequentCalls_IncrementByOne()
    {
        Assert.That(_store.NextSequential(ObjectTypeId, AttributeId, seed: 100001), Is.EqualTo(100001));
        Assert.That(_store.NextSequential(ObjectTypeId, AttributeId, seed: 100001), Is.EqualTo(100002));
        Assert.That(_store.NextSequential(ObjectTypeId, AttributeId, seed: 100001), Is.EqualTo(100003));
    }

    [Test]
    public void NextSequential_SeedHonouredOnlyOnFirstCall()
    {
        // The seed seeds the counter on first use; later calls increment regardless of any seed passed.
        Assert.That(_store.NextSequential(ObjectTypeId, AttributeId, seed: 5), Is.EqualTo(5));
        Assert.That(_store.NextSequential(ObjectTypeId, AttributeId, seed: 999), Is.EqualTo(6));
    }

    [Test]
    public void NextSequential_DifferentAttributes_Independent()
    {
        Assert.That(_store.NextSequential(ObjectTypeId, AttributeId, seed: 1), Is.EqualTo(1));
        Assert.That(_store.NextSequential(ObjectTypeId, AttributeId + 1, seed: 50), Is.EqualTo(50));
        Assert.That(_store.NextSequential(ObjectTypeId, AttributeId, seed: 1), Is.EqualTo(2));
    }

    #endregion

    #region Concurrency (the reason the store exists: correct without a global lock)

    [Test]
    public void TryReserveValue_ManyThreadsSameValue_ExactlyOneSucceeds()
    {
        var successes = 0;

        Parallel.For(0, 1000, _ =>
        {
            if (_store.TryReserveValue(ObjectTypeId, AttributeId, "contended"))
                Interlocked.Increment(ref successes);
        });

        Assert.That(successes, Is.EqualTo(1), "a single value must be reservable exactly once even under contention");
    }

    [Test]
    public void NextSequential_ManyThreadsSameAttribute_ProducesContiguousUniqueSet()
    {
        const int count = 10000;
        var results = new ConcurrentBag<int>();

        Parallel.For(0, count, _ => results.Add(_store.NextSequential(ObjectTypeId, AttributeId, seed: 1)));

        // No duplicates, and the assigned integers form the contiguous range [1, count].
        Assert.That(results.Distinct().Count(), Is.EqualTo(count), "sequential numbering must not hand out duplicates under contention");
        Assert.That(results.Min(), Is.EqualTo(1));
        Assert.That(results.Max(), Is.EqualTo(count));
    }

    [Test]
    public void NextUniqueIntSuffix_ManyThreadsSameBase_ProducesUniqueSuffixes()
    {
        const int count = 10000;
        var results = new ConcurrentBag<int>();

        Parallel.For(0, count, _ => results.Add(_store.NextUniqueIntSuffix(ObjectTypeId, AttributeId, "collision")));

        // Each caller must receive a distinct suffix so the rendered values stay unique; the set is the range [1, count].
        Assert.That(results.Distinct().Count(), Is.EqualTo(count), "unique-int suffixing must not hand out duplicate suffixes under contention");
        Assert.That(results.Min(), Is.EqualTo(1));
        Assert.That(results.Max(), Is.EqualTo(count));
    }

    #endregion
}
