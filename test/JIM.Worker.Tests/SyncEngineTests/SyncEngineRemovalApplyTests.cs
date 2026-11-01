// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Utility;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Applying a synchronisation's pending value removals to a Metaverse Object (#2664). A group whose membership is
/// replaced wholesale stages a removal per member, and each removal scanned the values the object held to find its
/// match, so applying them cost the membership times the removals. These tests require the removals to be applied in
/// one pass, with exactly the result applying them one at a time gave.
/// </summary>
[TestFixture]
public class SyncEngineRemovalApplyTests
{
    private const int HeldMemberCount = 50_000;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    private static readonly MetaverseAttribute DisplayName = new()
    {
        Id = 1, Name = Constants.BuiltInAttributes.DisplayName, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued
    };

    private static readonly MetaverseAttribute StaticMembers = new()
    {
        Id = 2, Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued
    };

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    /// <summary>
    /// A 50,000-member group whose synchronisation replaces its whole membership with two new members. The removals are
    /// staged last member first, the order in which finding each one by a scan from the front costs the most. Measured
    /// when #2664 was fixed, this took about 4.5 s before the fix and about 25 ms after; the bound sits well clear of
    /// both, so a return of the per-removal scan fails loudly rather than flakily.
    /// </summary>
    [Test]
    public void ApplyPendingAttributeChanges_FiftyThousandMemberGroupLosingEveryMember_AppliesTheRemovalsWithinBound()
    {
        var group = new MetaverseObject { Id = Guid.NewGuid() };
        group.AttributeValues.Add(Text(group, DisplayName, "All Staff"));
        var held = Enumerable.Range(0, HeldMemberCount).Select(_ => Member(group)).ToList();
        group.AttributeValues.AddRange(held);
        group.PendingAttributeValueRemovals.AddRange(Enumerable.Reverse(held));
        var joined = new[] { Member(group), Member(group) };
        group.PendingAttributeValueAdditions.AddRange(joined);

        var stopwatch = Stopwatch.StartNew();
        _engine.ApplyPendingAttributeChanges(group);
        stopwatch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.AttributeValues.Where(v => v.AttributeId == StaticMembers.Id), Is.EqualTo(joined),
                "the group holds exactly its two new members");
            Assert.That(group.CachedDisplayName, Is.EqualTo("All Staff"));
            Assert.That(group.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(group.PendingAttributeValueAdditions, Is.Empty);
            Assert.That(stopwatch.Elapsed, Is.LessThan(Bound),
                $"applying {HeldMemberCount:N0} member removals to a group holding {HeldMemberCount:N0} members took {stopwatch.Elapsed.TotalSeconds:N1}s; " +
                "the removals must be applied in one pass over the group's values, not a scan of them per removal");
        }
    }

    /// <summary>
    /// The apply step against applying each removal in turn, which is what it replaced, over random objects either side of
    /// the size at which the values are indexed. The objects hold members and Display Name values, a value held twice, and
    /// distinct values sharing an Id. The removals name held values more than once, name values the object does not hold
    /// (including ones carrying a held value's Id, which a removal by reference must leave alone), and name values being
    /// added in the same call, which go after the removals. Every trial must leave the same values, by reference and in
    /// the same order, and the Display Name they give.
    /// </summary>
    [Test]
    public void ApplyPendingAttributeChanges_AnyValuesAndPendingChanges_LeavesWhatApplyingEachRemovalInTurnLeft()
    {
        var random = new Random(2664);
        for (var trial = 0; trial < 1_000; trial++)
        {
            var mvo = new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Before" };
            var held = new List<MetaverseObjectAttributeValue>();
            var heldCount = random.Next(ReferenceIndexedList<MetaverseObjectAttributeValue>.IndexThreshold * 3);
            for (var i = 0; i < heldCount; i++)
                held.Add(HeldValue(random, mvo, held, i));
            mvo.AttributeValues.AddRange(held);

            var additions = Enumerable.Range(0, random.Next(6)).Select(i => Addition(random, mvo, held, heldCount + i)).ToList();
            var removals = Enumerable.Range(0, random.Next(heldCount + 6)).Select(i => Removal(random, mvo, held, additions, heldCount + 100 + i)).ToList();
            mvo.PendingAttributeValueAdditions.AddRange(additions);
            mvo.PendingAttributeValueRemovals.AddRange(removals);
            var expected = ApplyEachRemovalInTurn(mvo.AttributeValues, additions, removals);
            var expectedName = additions.Count == 0 && removals.Count == 0 ? "Before" : ObjectNaming.MetaverseNameFrom(expected);

            _engine.ApplyPendingAttributeChanges(mvo);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(mvo.AttributeValues.Count, Is.EqualTo(expected.Count), $"trial {trial}: how many values are left");
                Assert.That(SameReferencesInOrder(mvo.AttributeValues, expected), Is.True, $"trial {trial}: the same values, by reference, in the same order");
                Assert.That(mvo.CachedDisplayName, Is.EqualTo(expectedName), $"trial {trial}");
                Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, $"trial {trial}");
                Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, $"trial {trial}");
            }
        }
    }

    // The apply step as it stood before #2664: each removal in turn takes the first occurrence of what it names, by
    // reference, and then the additions are added.
    private static List<MetaverseObjectAttributeValue> ApplyEachRemovalInTurn(
        IEnumerable<MetaverseObjectAttributeValue> held,
        List<MetaverseObjectAttributeValue> additions,
        List<MetaverseObjectAttributeValue> removals)
    {
        var values = new List<MetaverseObjectAttributeValue>(held);
        foreach (var removal in removals)
            values.Remove(removal);
        values.AddRange(additions);
        return values;
    }

    // Mostly members, sometimes a Display Name; now and then a distinct value carrying another held value's Id, or a
    // held value held a second time.
    private static MetaverseObjectAttributeValue HeldValue(Random random, MetaverseObject mvo, List<MetaverseObjectAttributeValue> held, int number)
    {
        var kind = random.Next(10);
        if (kind == 8 && held.Count > 0)
            return WithIdOf(held[random.Next(held.Count)], mvo);
        if (kind == 9 && held.Count > 0)
            return held[random.Next(held.Count)];
        return kind < 2 ? Text(mvo, DisplayName, $"Name {number}") : Member(mvo);
    }

    // Mostly new members, sometimes a new Display Name, now and then a value already held, added a second time.
    private static MetaverseObjectAttributeValue Addition(Random random, MetaverseObject mvo, List<MetaverseObjectAttributeValue> held, int number)
    {
        var kind = random.Next(6);
        if (kind == 0 && held.Count > 0)
            return held[random.Next(held.Count)];
        return kind == 1 ? Text(mvo, DisplayName, $"Name {number}") : Member(mvo);
    }

    // Mostly held values, so some are named more than once; otherwise a value being added in the same call, a value the
    // object does not hold, or a value the object does not hold carrying a held value's Id.
    private static MetaverseObjectAttributeValue Removal(
        Random random,
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> held,
        List<MetaverseObjectAttributeValue> additions,
        int number)
    {
        var kind = random.Next(20);
        if (kind < 12 && held.Count > 0)
            return held[random.Next(held.Count)];
        if (kind < 15 && additions.Count > 0)
            return additions[random.Next(additions.Count)];
        if (kind < 18 || held.Count == 0)
            return kind % 2 == 0 ? Text(mvo, DisplayName, $"Name {number}") : Member(mvo);
        return WithIdOf(held[random.Next(held.Count)], mvo);
    }

    private static bool SameReferencesInOrder<T>(IReadOnlyList<T> actual, IReadOnlyList<T> expected) where T : class =>
        actual.Count == expected.Count && Enumerable.Range(0, expected.Count).All(i => ReferenceEquals(actual[i], expected[i]));

    private static MetaverseObjectAttributeValue WithIdOf(MetaverseObjectAttributeValue value, MetaverseObject mvo) => new()
    {
        Id = value.Id, MetaverseObject = mvo, Attribute = value.Attribute, AttributeId = value.AttributeId,
        StringValue = value.StringValue, ReferenceValueId = value.ReferenceValueId
    };

    private static MetaverseObjectAttributeValue Member(MetaverseObject group) => new()
    {
        Id = Guid.NewGuid(), MetaverseObject = group, Attribute = StaticMembers, AttributeId = StaticMembers.Id, ReferenceValueId = Guid.NewGuid()
    };

    private static MetaverseObjectAttributeValue Text(MetaverseObject mvo, MetaverseAttribute attribute, string value) => new()
    {
        Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = attribute, AttributeId = attribute.Id, StringValue = value
    };
}
