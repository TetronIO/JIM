// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Utility;
using NUnit.Framework;

namespace JIM.Models.Tests.Utilities;

/// <summary>
/// <see cref="ReferenceIndexedList{T}"/> must behave exactly as the <see cref="List{T}"/> it replaced on the value
/// navigations, with membership by reference, both while it scans (small) and once it indexes (large).
/// </summary>
[TestFixture]
public class ReferenceIndexedListTests
{
    private const int Small = 3;
    private const int Large = ReferenceIndexedList<Item>.IndexThreshold + 5;

    // A record, so two instances can be equal by value while being different objects.
    private sealed record Item(int Number);

    private static List<Item> Items(int count) => Enumerable.Range(0, count).Select(i => new Item(i)).ToList();

    [TestCase(Small)]
    [TestCase(Large)]
    public void Add_ManyItemsWithADuplicate_KeepsOrderAndTheDuplicate(int count)
    {
        var items = Items(count);
        var list = new ReferenceIndexedList<Item>();
        list.AddRange(items);
        list.Add(items[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(list, Is.EqualTo(items.Append(items[0])));
            Assert.That(list.Count, Is.EqualTo(count + 1));
            Assert.That(list.IndexOf(items[0]), Is.Zero);
        }
    }

    [TestCase(Small)]
    [TestCase(Large)]
    public void Contains_AnEqualButDifferentInstance_ReturnsFalse(int count)
    {
        var items = Items(count);
        var list = new ReferenceIndexedList<Item>(items);
        var lookalike = items[1] with { };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lookalike, Is.EqualTo(items[1]), "the lookalike must be equal by value for this test to mean anything");
            Assert.That(list.Contains(items[1]), Is.True);
            Assert.That(list.Contains(lookalike), Is.False);
            Assert.That(list.IndexOf(lookalike), Is.EqualTo(-1));
            Assert.That(list.Remove(lookalike), Is.False);
            Assert.That(list.Count, Is.EqualTo(count));
        }
    }

    [TestCase(Small)]
    [TestCase(Large)]
    public void Remove_AnItemHeldTwice_RemovesTheFirstOccurrenceAndStillContainsIt(int count)
    {
        var items = Items(count);
        var list = new ReferenceIndexedList<Item>(items) { items[1] };

        var removedFirst = list.Remove(items[1]);
        var containsAfterFirst = list.Contains(items[1]);
        var removedSecond = list.Remove(items[1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removedFirst, Is.True);
            Assert.That(containsAfterFirst, Is.True, "one occurrence remains");
            Assert.That(removedSecond, Is.True);
            Assert.That(list.Contains(items[1]), Is.False);
            Assert.That(list, Is.EqualTo(items.Where(i => !ReferenceEquals(i, items[1]))));
        }
    }

    [TestCase(Small)]
    [TestCase(Large)]
    public void IndexerSet_ReplacesAnItem_ContainsTheNewItemAndNotTheOld(int count)
    {
        var items = Items(count);
        var list = new ReferenceIndexedList<Item>(items);
        var replacement = new Item(-1);

        list[1] = replacement;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(list[1], Is.SameAs(replacement));
            Assert.That(list.Contains(replacement), Is.True);
            Assert.That(list.Contains(items[1]), Is.False);
            Assert.That(list.Count, Is.EqualTo(count));
        }
    }

    [TestCase(Small)]
    [TestCase(Large)]
    public void InsertAndRemoveAt_KeepPositionsAndMembership(int count)
    {
        var items = Items(count);
        var list = new ReferenceIndexedList<Item>(items);
        var inserted = new Item(-1);

        list.Insert(1, inserted);
        var insertedAt = list.IndexOf(inserted);
        list.RemoveAt(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(insertedAt, Is.EqualTo(1));
            Assert.That(list[0], Is.SameAs(inserted));
            Assert.That(list.Contains(items[0]), Is.False);
            Assert.That(list.Contains(inserted), Is.True);
            Assert.That(list.Count, Is.EqualTo(count));
        }
    }

    [TestCase(Small)]
    [TestCase(Large)]
    public void RemoveAll_ByPredicate_RemovesOnlyMatchingItems(int count)
    {
        var items = Items(count);
        var list = new ReferenceIndexedList<Item>(items);

        var removed = list.RemoveAll(i => i.Number % 2 == 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(items.Count(i => i.Number % 2 == 0)));
            Assert.That(list, Is.EqualTo(items.Where(i => i.Number % 2 != 0)));
            Assert.That(items.All(i => list.Contains(i) == (i.Number % 2 != 0)), Is.True);
        }
    }

    [Test]
    public void RemoveAll_PredicateThrowsPartWay_MembershipAgreesWithWhatTheListHolds()
    {
        var items = Items(Large);
        var list = new ReferenceIndexedList<Item>(items);

        Assert.That(() => list.RemoveAll(i => i.Number < 5 || (i.Number == 20 ? throw new InvalidOperationException("part way") : false)),
            Throws.InvalidOperationException);

        Assert.That(items.All(i => list.Contains(i) == list.Any(held => ReferenceEquals(held, i))), Is.True);
    }

    [TestCase(Small)]
    [TestCase(Large)]
    public void Clear_ThenAdd_HoldsOnlyTheNewItem(int count)
    {
        var items = Items(count);
        var list = new ReferenceIndexedList<Item>(items);
        var added = new Item(-1);

        list.Clear();
        list.Add(added);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(list, Is.EqualTo(new[] { added }));
            Assert.That(items.Any(list.Contains), Is.False);
        }
    }

    [Test]
    public void Contains_Null_IsTrueOnlyWhileANullIsHeld()
    {
        var items = Items(Large);
        var list = new ReferenceIndexedList<Item>(items) { null! };

        var containsWhileHeld = list.Contains(null!);
        list.Remove(null!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(containsWhileHeld, Is.True);
            Assert.That(list.Contains(null!), Is.False);
            Assert.That(list.Count, Is.EqualTo(Large));
        }
    }

    [TestCase(Small)]
    [TestCase(Large)]
    public void RemoveEach_AnItemHeldTwiceAndNamedOnce_RemovesOnlyItsFirstOccurrence(int count)
    {
        var items = Items(count);
        var list = new ReferenceIndexedList<Item>(items) { items[1] };

        var removed = list.RemoveEach([items[1]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(list, Is.EqualTo(items.Where(i => !ReferenceEquals(i, items[1])).Append(items[1])),
                "the later occurrence stays where it was");
            Assert.That(list.Contains(items[1]), Is.True, "one occurrence remains");
        }
    }

    [TestCase(Small)]
    [TestCase(Large)]
    public void RemoveEach_AnItemNamedTwice_RemovesTwoOccurrences(int count)
    {
        var items = Items(count);
        var list = new ReferenceIndexedList<Item>(items) { items[1], items[1] };

        var removed = list.RemoveEach([items[1], items[1]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(2));
            Assert.That(list, Is.EqualTo(items.Where(i => !ReferenceEquals(i, items[1])).Append(items[1])),
                "the first two occurrences go and the third stays");
            Assert.That(list.Contains(items[1]), Is.True);
        }
    }

    [TestCase(Small)]
    [TestCase(Large)]
    public void RemoveEach_ItemsTheListDoesNotHold_RemovesNothing(int count)
    {
        var items = Items(count);
        var list = new ReferenceIndexedList<Item>(items);
        var lookalike = items[1] with { };

        var removed = list.RemoveEach([lookalike, new Item(-1), null!]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.Zero);
            Assert.That(list, Is.EqualTo(items));
            Assert.That(list.Contains(items[1]), Is.True, "an item equal by value to one named is not one named");
        }
    }

    [Test]
    public void RemoveEach_NullNamedOnceWhileTwoAreHeld_RemovesTheFirstNull()
    {
        var items = Items(Large);
        var list = new ReferenceIndexedList<Item>(items);
        list.Insert(1, null!);
        list.Add(null!);

        var removed = list.RemoveEach([null!]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(list, Is.EqualTo(items.Append(null!)));
            Assert.That(list.Contains(null!), Is.True);
        }
    }

    [Test]
    public void RemoveEach_TakesALargeListBelowTheIndexThreshold_MembershipAgreesWithWhatTheListHolds()
    {
        var items = Items(Large * 2);
        var list = new ReferenceIndexedList<Item>(items);
        var kept = items.Take(Small).ToList();

        var removed = list.RemoveEach(items.Skip(Small));
        list.Add(items[^1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(items.Count - Small));
            Assert.That(list, Is.EqualTo(kept.Append(items[^1])));
            Assert.That(items.All(i => list.Contains(i) == list.Any(held => ReferenceEquals(held, i))), Is.True);
        }
    }

    /// <summary>
    /// <see cref="ReferenceIndexedList{T}.RemoveEach"/> against removing each named item in turn, which is what it
    /// replaced (#2664), over random lists either side of the index threshold that hold duplicates and nulls, and random
    /// sequences that name items more than once, items the list does not hold, items equal by value to held ones, and
    /// null. One call can take the list across the threshold in either direction, and the operations after it rely on the
    /// index it left, so a miscounted index shows up as a wrong answer later even when the call's own result was right.
    /// </summary>
    [Test]
    public void RemoveEach_InAnySequenceOfOperations_AgreesWithRemovingEachNamedItemInTurn()
    {
        var random = new Random(2664);
        var pool = Items(ReferenceIndexedList<Item>.IndexThreshold * 3);
        var lookalikes = pool.Select(i => i with { }).ToList();
        var expected = new List<Item?>();
        var list = new ReferenceIndexedList<Item>();

        for (var step = 0; step < 20_000; step++)
        {
            // Grow and shrink in long runs, so the list crosses the threshold many times, sometimes in a single call.
            var growing = step / 500 % 2 == 0;
            var operation = random.Next(10);
            if (operation < (growing ? 2 : 6))
            {
                // A few names while growing and many while shrinking; now and then, while growing, enough to take a large
                // list below the threshold in one call.
                var nameCount = growing && random.Next(25) != 0 ? random.Next(4) : random.Next(expected.Count + 4);
                var named = Enumerable.Range(0, nameCount).Select(_ => Pick(random, expected, pool, lookalikes)).ToList();
                var expectedRemoved = RemoveEachInTurn(expected, named);
                Assert.That(list.RemoveEach(named!), Is.EqualTo(expectedRemoved), $"count removed at step {step}");
            }
            else if (operation == 6 && expected.Count > 0)
            {
                var removeAt = random.Next(expected.Count);
                expected.RemoveAt(removeAt);
                list.RemoveAt(removeAt);
            }
            else if (operation == 7)
            {
                var insertAt = random.Next(expected.Count + 1);
                var inserted = random.Next(50) == 0 ? null : pool[random.Next(pool.Count)];
                expected.Insert(insertAt, inserted);
                list.Insert(insertAt, inserted!);
            }
            else
            {
                var added = random.Next(50) == 0 ? null : pool[random.Next(pool.Count)];
                expected.Add(added);
                list.Add(added!);
            }

            Assert.That(list.Count, Is.EqualTo(expected.Count), $"count after step {step}");
            Assert.That(Enumerable.Range(0, expected.Count).All(i => ReferenceEquals(list[i], expected[i])), Is.True,
                $"the same items, by reference, in the same order after step {step}");
            var probe = random.Next(20) == 0 ? null : pool[random.Next(pool.Count)];
            Assert.That(list.Contains(probe!), Is.EqualTo(expected.Any(i => ReferenceEquals(i, probe))), $"membership after step {step}");
            Assert.That(list.IndexOf(probe!), Is.EqualTo(expected.FindIndex(i => ReferenceEquals(i, probe))), $"position after step {step}");
        }
    }

    // Names an item the list holds (so repeats are common), one from the pool it may not hold, one equal by value to a
    // pool item but a different object, or null.
    private static Item? Pick(Random random, List<Item?> held, List<Item> pool, List<Item> lookalikes)
    {
        var kind = random.Next(20);
        if (kind < 12 && held.Count > 0)
            return held[random.Next(held.Count)];
        if (kind < 17)
            return pool[random.Next(pool.Count)];
        return kind < 19 ? lookalikes[random.Next(lookalikes.Count)] : null;
    }

    // The behaviour RemoveEach replaced: each named item in turn removes the first occurrence, by reference, of what is
    // left, and an item the list does not hold removes nothing.
    private static int RemoveEachInTurn(List<Item?> list, List<Item?> named)
    {
        var removed = 0;
        for (var i = 0; i < named.Count; i++)
        {
            var item = named[i];
            var index = list.FindIndex(held => ReferenceEquals(held, item));
            if (index < 0)
                continue;
            list.RemoveAt(index);
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// Random operations across the index threshold in both directions, checked after every one against a plain list
    /// searched by reference: whatever sequence a caller performs, the two must agree on order and membership.
    /// </summary>
    [Test]
    public void AnySequenceOfOperations_AcrossTheIndexThreshold_AgreesWithAListSearchedByReference()
    {
        var random = new Random(2598);
        var pool = Items(ReferenceIndexedList<Item>.IndexThreshold * 3);
        var expected = new List<Item>();
        var list = new ReferenceIndexedList<Item>();

        for (var step = 0; step < 20_000; step++)
        {
            // Grow and shrink in long runs, so the list crosses the threshold many times.
            var growing = step / 500 % 2 == 0;
            var operation = random.Next(growing ? 10 : 5);
            var item = pool[random.Next(pool.Count)];
            switch (operation)
            {
                case 0 when expected.Count > 0:
                    var removeAt = random.Next(expected.Count);
                    expected.RemoveAt(removeAt);
                    list.RemoveAt(removeAt);
                    break;
                case 1:
                    var expectedIndex = expected.FindIndex(i => ReferenceEquals(i, item));
                    if (expectedIndex >= 0)
                        expected.RemoveAt(expectedIndex);
                    Assert.That(list.Remove(item), Is.EqualTo(expectedIndex >= 0));
                    break;
                case 2 when expected.Count > 0:
                    var setAt = random.Next(expected.Count);
                    expected[setAt] = item;
                    list[setAt] = item;
                    break;
                case 3 when random.Next(40) == 0:
                    var threshold = random.Next(pool.Count);
                    expected.RemoveAll(i => i.Number < threshold);
                    list.RemoveAll(i => i.Number < threshold);
                    break;
                case 4 when random.Next(200) == 0:
                    expected.Clear();
                    list.Clear();
                    break;
                case 5:
                    var insertAt = random.Next(expected.Count + 1);
                    expected.Insert(insertAt, item);
                    list.Insert(insertAt, item);
                    break;
                default:
                    expected.Add(item);
                    list.Add(item);
                    break;
            }

            Assert.That(list, Is.EqualTo(expected), $"order after step {step}");
            var probe = pool[random.Next(pool.Count)];
            Assert.That(list.Contains(probe), Is.EqualTo(expected.Any(i => ReferenceEquals(i, probe))), $"membership after step {step}");
            Assert.That(list.IndexOf(probe), Is.EqualTo(expected.FindIndex(i => ReferenceEquals(i, probe))), $"position after step {step}");
        }
    }
}
