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
