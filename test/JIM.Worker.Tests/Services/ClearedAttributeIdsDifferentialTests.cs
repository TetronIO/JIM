// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// <see cref="ContributorReElectionService.GetClearedAttributeIds"/> asks, of each value the Metaverse Object holds for
/// an attribute that lost a value, whether that value is among the removals, and answers it from a set of the removals
/// keyed by reference (#2666) rather than by scanning the list it was handed. The set must give the scan's answer in
/// every case, so these tests hold the two to one answer across a broad generated set of objects and pending changes:
/// removals handed over as the pending removals themselves and as copied lists, before and after they are applied,
/// removals named twice, removals that look exactly like a held value without being it, values of attributes that lost
/// nothing, and additions that re-add a removed attribute or another one.
/// </summary>
[TestFixture]
public class ClearedAttributeIdsDifferentialTests
{
    private const int Seed = 2666;
    private const int CaseCount = 20_000;
    private const int MinimumCasesPerFeature = 200;

    private static readonly int[] AttributeIds = [1, 2, 3, 4];

    // Ids a value may carry: none yet (a value not persisted), or one of a few, so distinct values often share one.
    private static readonly Guid[] ValueIds =
    [
        Guid.Empty,
        Guid.Parse("8d0f8a3e-2c5e-4b8f-9c1e-266600000001"),
        Guid.Parse("8d0f8a3e-2c5e-4b8f-9c1e-266600000002")
    ];

    private static readonly string?[] Texts = [null, "", "alpha", "Alpha", "beta"];

    private enum Feature
    {
        HandedACopy,
        HandedThePendingRemovals,
        AlreadyApplied,
        RemovalNamedTwice,
        LookAlikeRemoval,
        AttributeReAdded,
        AttributeCleared,
        AttributeKeptByAnotherValue
    }

    [Test]
    public void GetClearedAttributeIds_SetAndScan_GiveTheSameAnswerForEveryGeneratedCase()
    {
        var random = new Random(Seed);
        var disagreements = new List<string>();
        var features = Enum.GetValues<Feature>().ToDictionary(feature => feature, _ => 0);

        for (var i = 0; i < CaseCount; i++)
        {
            var (mvo, additions, removals, caseFeatures) = GenerateCase(random);
            var expected = ScanReference(mvo, additions, removals.ToList());

            var actual = ContributorReElectionService.GetClearedAttributeIds(mvo, additions, removals);

            if (!actual.SetEquals(expected))
            {
                disagreements.Add($"case {i}: expected [{string.Join(", ", expected)}] but was [{string.Join(", ", actual)}]; " +
                    $"{mvo.AttributeValues.Count} held, {removals.Count} removed, {additions.Count} added");
            }

            foreach (var feature in caseFeatures)
                features[feature]++;
            if (expected.Count > 0)
                features[Feature.AttributeCleared]++;
            if (removals.Select(av => av.AttributeId).Distinct().Any(id => !expected.Contains(id) && additions.All(av => av.AttributeId != id)))
                features[Feature.AttributeKeptByAnotherValue]++;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(disagreements, Is.Empty, string.Join(Environment.NewLine, disagreements.Take(20)));
            foreach (var (feature, count) in features)
                Assert.That(count, Is.GreaterThanOrEqualTo(MinimumCasesPerFeature), $"the generator produced too few cases with {feature}");
        }
    }

    /// <summary>
    /// A value that is not pending removal keeps its attribute from being cleared even when a removal looks exactly like
    /// it: the same attribute, the same value and the same (unpersisted, empty) id. Membership is by reference.
    /// </summary>
    [Test]
    public void GetClearedAttributeIds_RemovalLooksExactlyLikeAHeldValueThatStays_AttributeIsNotCleared()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var removed = Value(mvo, attributeId: 1, Guid.Empty, "alpha");
        var stays = Value(mvo, attributeId: 1, Guid.Empty, "alpha");
        mvo.AttributeValues.AddRange([removed, stays]);

        var cleared = ContributorReElectionService.GetClearedAttributeIds(mvo, [], [removed]);

        Assert.That(cleared, Is.Empty, "the value that stays is not the one removed, however alike they are");
    }

    /// <summary>
    /// The scan <see cref="ContributorReElectionService.GetClearedAttributeIds"/> used before #2666, kept as written. It
    /// is handed a copied list, whose membership check is by reference because the values do not override equality.
    /// </summary>
    private static HashSet<int> ScanReference(
        MetaverseObject mvo,
        IReadOnlyCollection<MetaverseObjectAttributeValue> additions,
        IReadOnlyCollection<MetaverseObjectAttributeValue> removals)
    {
        if (removals.Count == 0)
            return new HashSet<int>();

        var reAddedAttributeIds = additions.Select(av => av.AttributeId).ToHashSet();
        return removals
            .Select(av => av.AttributeId)
            .Distinct()
            .Where(attributeId => !reAddedAttributeIds.Contains(attributeId) &&
                                  !mvo.AttributeValues.Any(av => av.AttributeId == attributeId && !removals.Contains(av)))
            .ToHashSet();
    }

    private static (MetaverseObject Mvo, List<MetaverseObjectAttributeValue> Additions, IReadOnlyCollection<MetaverseObjectAttributeValue> Removals,
        List<Feature> Features) GenerateCase(Random random)
    {
        var features = new List<Feature>();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };

        // Mostly a handful of values; now and then enough that the pending removals index themselves.
        var heldCount = random.Next(4) == 0 ? random.Next(30, 80) : random.Next(0, 10);
        for (var i = 0; i < heldCount; i++)
            mvo.AttributeValues.Add(RandomValue(random, mvo));

        var removals = new List<MetaverseObjectAttributeValue>();
        foreach (var held in mvo.AttributeValues.Where(_ => random.Next(3) == 0))
        {
            removals.Add(held);
            if (random.Next(8) == 0)
            {
                removals.Add(held);
                features.Add(Feature.RemovalNamedTwice);
            }
        }

        // A removal the object does not hold: sometimes the very image of a value it does, sometimes of an attribute it
        // holds nothing of.
        if (random.Next(3) == 0)
        {
            var lookAlike = mvo.AttributeValues.Count > 0 && random.Next(2) == 0
                ? LookAlike(mvo, mvo.AttributeValues[random.Next(mvo.AttributeValues.Count)])
                : RandomValue(random, mvo);
            removals.Insert(random.Next(removals.Count + 1), lookAlike);
            features.Add(Feature.LookAlikeRemoval);
        }

        var additions = Enumerable.Range(0, random.Next(0, 3)).Select(_ => RandomValue(random, mvo)).ToList();
        if (additions.Any(added => removals.Any(removed => removed.AttributeId == added.AttributeId)))
            features.Add(Feature.AttributeReAdded);

        // The check is made before the pending changes are applied and after; applying takes the removals off the object.
        if (random.Next(3) == 0)
        {
            mvo.AttributeValues.RemoveEach(removals);
            features.Add(Feature.AlreadyApplied);
        }

        if (random.Next(2) == 0)
        {
            mvo.PendingAttributeValueRemovals.AddRange(removals);
            features.Add(Feature.HandedThePendingRemovals);
            return (mvo, additions, mvo.PendingAttributeValueRemovals, features);
        }

        features.Add(Feature.HandedACopy);
        return (mvo, additions, removals, features);
    }

    private static MetaverseObjectAttributeValue RandomValue(Random random, MetaverseObject mvo) =>
        Value(mvo, AttributeIds[random.Next(AttributeIds.Length)], ValueIds[random.Next(ValueIds.Length)], Texts[random.Next(Texts.Length)]);

    private static MetaverseObjectAttributeValue LookAlike(MetaverseObject mvo, MetaverseObjectAttributeValue held) =>
        Value(mvo, held.AttributeId, held.Id, held.StringValue);

    private static MetaverseObjectAttributeValue Value(MetaverseObject mvo, int attributeId, Guid id, string? text) => new()
    {
        Id = id,
        MetaverseObject = mvo,
        Attribute = new MetaverseAttribute { Id = attributeId, Name = $"attribute {attributeId}" },
        AttributeId = attributeId,
        StringValue = text
    };
}
