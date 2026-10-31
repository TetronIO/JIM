// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// A Metaverse Object's effective values for an attribute: what it holds, less what is pending removal (#2664). Each
/// writer reads them, stages its removals, then reads them again to take over provenance on what survived, and each
/// read asked of every held value whether the pending removals list held it, by scanning that list. A group whose
/// membership is replaced wholesale stages a removal per member, so the second read cost the membership times the
/// removals.
/// </summary>
[TestFixture]
public class SyncEngineEffectiveAttributeValuesTests
{
    private const int HeldMemberCount = 50_000;
    private const int SurvivingMemberCount = 2;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    private static readonly MetaverseAttribute StaticMembers = new()
    {
        Id = 100, Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued
    };

    private static readonly ConnectedSystemObjectTypeAttribute Member = new()
    {
        Id = 200, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued
    };

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    /// <summary>
    /// A 50,000-member group whose source replaces all but two of its members. The reference writer stages a removal
    /// per departed member, then takes over provenance on the two survivors, reading the effective values with every
    /// removal pending. Measured when #2664 was fixed, this took about 4.5 s before the fix and under 0.1 s after; the
    /// bound sits well clear of both, so a return of the per-value scan fails loudly rather than flakily.
    /// </summary>
    [Test]
    public void FlowInboundAttributes_FiftyThousandMemberGroupReplacedWholesale_TakesOverProvenanceWithinBound()
    {
        var group = new MetaverseObject { Id = Guid.NewGuid() };
        var heldMemberIds = Enumerable.Range(0, HeldMemberCount).Select(_ => Guid.NewGuid()).ToList();
        group.AttributeValues.AddRange(heldMemberIds.Select(memberId => new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = group, Attribute = StaticMembers, AttributeId = StaticMembers.Id,
            ReferenceValueId = memberId, ContributedBySystemId = 5, ContributedBySyncRuleId = 6
        }));

        var groupCso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = group };
        var joinedMemberIds = Enumerable.Range(0, HeldMemberCount - SurvivingMemberCount).Select(_ => Guid.NewGuid()).ToList();
        groupCso.AttributeValues.AddRange(heldMemberIds.Take(SurvivingMemberCount).Concat(joinedMemberIds).Select(memberId => new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = groupCso, Attribute = Member, AttributeId = Member.Id,
            ReferenceValueId = Guid.NewGuid(), ResolvedReferenceMetaverseObjectId = memberId
        }));

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = StaticMembers, SyncRuleId = 7 };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = Member.Id, ConnectedSystemAttribute = Member, Order = 1 });
        var syncRule = new SyncRule { Id = 7, AttributeFlowRules = [mapping] };
        var groupType = new ConnectedSystemObjectType { Id = 1, Attributes = [Member] };

        var stopwatch = Stopwatch.StartNew();
        var errors = _engine.FlowInboundAttributes(groupCso, syncRule, [groupType]);
        stopwatch.Stop();

        var survivors = group.AttributeValues.Take(SurvivingMemberCount).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(group.PendingAttributeValueRemovals, Is.EqualTo(group.AttributeValues.Skip(SurvivingMemberCount)),
                "every departed member is staged for removal, in the order the group held them");
            Assert.That(group.PendingAttributeValueAdditions.Select(v => v.ReferenceValueId), Is.EqualTo(joinedMemberIds.Select(id => (Guid?)id)),
                "every new member is staged for addition");
            Assert.That(survivors.Select(v => v.ContributedBySyncRuleId), Is.All.EqualTo(7), "the survivors are now this rule's");
            Assert.That(group.PendingAttributeValueRemovals.Select(v => v.ContributedBySyncRuleId), Is.All.EqualTo(6),
                "the departed members keep the provenance they had");
            Assert.That(stopwatch.Elapsed, Is.LessThan(Bound),
                $"flowing a wholesale replacement of a {HeldMemberCount:N0}-member group took {stopwatch.Elapsed.TotalSeconds:N1}s; " +
                "reading the group's effective values must look each held member up in the pending removals, not scan them per member");
        }
    }

    /// <summary>
    /// The effective values against the lazy filter they replaced, which scanned the pending removals afresh for each
    /// value it reached, over random objects whose pending removals sit either side of the size at which they are
    /// indexed. The objects hold the attribute's values, other attributes' values and values held twice; the removals
    /// name held values, other attributes' values and values not held. While the values are read, the removals change
    /// as callers change them, mostly by growing: the value just read is appended (as the typed writer does), or a
    /// value still to be read, a value not held, or a batch that takes the removals across the threshold. Now and then
    /// the last few are dropped, or all are cleared, or the list is replaced by another. Every trial must yield the
    /// same values, by reference and in the same order.
    /// </summary>
    [Test]
    public void GetEffectiveAttributeValues_RemovalsChangingWhileTheValuesAreRead_YieldsWhatScanningTheRemovalsYielded()
    {
        var random = new Random(2664);
        var description = new MetaverseAttribute { Id = 101, Name = "Description", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        for (var trial = 0; trial < 2_000; trial++)
        {
            var values = new List<MetaverseObjectAttributeValue>();
            var valueCount = random.Next(PendingRemovalLookup.IndexThreshold * 4);
            for (var i = 0; i < valueCount; i++)
                values.Add(HeldValue(random, values, description));
            var strangers = Enumerable.Range(0, 20).Select(_ => MemberValue()).ToList();
            var removals = Enumerable.Range(0, random.Next(PendingRemovalLookup.IndexThreshold * 3))
                .Select(_ => Pick(random, values, strangers))
                .ToList();
            var steps = Enumerable.Range(0, valueCount).Select(_ => NextStep(random, values, strangers)).ToList();

            var (expected, expectedRemovals) = Read(EffectiveValuesByScan, values, removals, steps);
            var (actual, actualRemovals) = Read(SyncEngine.GetEffectiveAttributeValues, values, removals, steps);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual.Count, Is.EqualTo(expected.Count), $"trial {trial}: how many values were yielded");
                Assert.That(SameReferencesInOrder(actual, expected), Is.True, $"trial {trial}: the same values, by reference, in the same order");
                Assert.That(SameReferencesInOrder(actualRemovals, expectedRemovals), Is.True, $"trial {trial}: the same removals afterwards");
            }
        }
    }

    // GetEffectiveAttributeValues as it stood before #2664: a lazy filter that scans the pending removals, as they
    // stand when it reaches each value, by reference (the value type does not override Equals).
    private static IEnumerable<MetaverseObjectAttributeValue> EffectiveValuesByScan(MetaverseObject mvo, int attributeId)
        => mvo.AttributeValues.Where(av => av.AttributeId == attributeId && !mvo.PendingAttributeValueRemovals.Contains(av));

    // Reads the effective values of a fresh object holding these values and removals, changing its removals after each
    // value is read as the step for that position says.
    private static (List<MetaverseObjectAttributeValue> Yielded, List<MetaverseObjectAttributeValue> Removals) Read(
        Func<MetaverseObject, int, IEnumerable<MetaverseObjectAttributeValue>> effectiveValues,
        List<MetaverseObjectAttributeValue> values,
        List<MetaverseObjectAttributeValue> removals,
        List<ReadStep> steps)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.AddRange(values);
        mvo.PendingAttributeValueRemovals.AddRange(removals);

        var yielded = new List<MetaverseObjectAttributeValue>();
        foreach (var value in effectiveValues(mvo, StaticMembers.Id))
        {
            var step = steps[yielded.Count];
            yielded.Add(value);
            switch (step.Kind)
            {
                case ReadStepKind.AppendTheValueRead:
                    mvo.PendingAttributeValueRemovals.Add(value);
                    break;
                case ReadStepKind.Append:
                    mvo.PendingAttributeValueRemovals.AddRange(step.Values);
                    break;
                case ReadStepKind.Clear:
                    mvo.PendingAttributeValueRemovals.Clear();
                    break;
                case ReadStepKind.Replace:
                    mvo.PendingAttributeValueRemovals = [.. step.Values, .. mvo.PendingAttributeValueRemovals];
                    break;
                case ReadStepKind.DropTheLast:
                    var dropped = Math.Min(step.DropCount, mvo.PendingAttributeValueRemovals.Count);
                    mvo.PendingAttributeValueRemovals.RemoveRange(mvo.PendingAttributeValueRemovals.Count - dropped, dropped);
                    break;
                case ReadStepKind.Nothing:
                    break;
            }
        }
        return (yielded, mvo.PendingAttributeValueRemovals);
    }

    // Mostly nothing or the typed writer's append of the value just read; otherwise an append of one value (held, still
    // to be read, or not held) or of a batch, and now and then the last few dropped, a clear, or a replacement of the
    // list.
    private static ReadStep NextStep(Random random, List<MetaverseObjectAttributeValue> values, List<MetaverseObjectAttributeValue> strangers)
    {
        var kind = random.Next(42);
        if (kind < 20)
            return new ReadStep(ReadStepKind.Nothing, []);
        if (kind < 28)
            return new ReadStep(ReadStepKind.AppendTheValueRead, []);
        if (kind < 35)
            return new ReadStep(ReadStepKind.Append, [Pick(random, values, strangers)]);
        if (kind < 38)
            return new ReadStep(ReadStepKind.Append, [.. Enumerable.Range(0, random.Next(PendingRemovalLookup.IndexThreshold + 10)).Select(_ => Pick(random, values, strangers))]);
        if (kind == 38)
            return new ReadStep(ReadStepKind.Clear, []);
        if (kind == 39)
            return new ReadStep(ReadStepKind.Replace, [.. Enumerable.Range(0, random.Next(10)).Select(_ => Pick(random, values, strangers))]);
        return new ReadStep(ReadStepKind.DropTheLast, [], random.Next(1, 12));
    }

    // Mostly a held value (of this attribute or another, so some are named more than once), otherwise one not held.
    private static MetaverseObjectAttributeValue Pick(Random random, List<MetaverseObjectAttributeValue> values, List<MetaverseObjectAttributeValue> strangers)
        => random.Next(4) != 0 && values.Count > 0 ? values[random.Next(values.Count)] : strangers[random.Next(strangers.Count)];

    // Mostly members; otherwise another attribute's value, or a value already held, held a second time.
    private static MetaverseObjectAttributeValue HeldValue(Random random, List<MetaverseObjectAttributeValue> values, MetaverseAttribute other)
    {
        var kind = random.Next(10);
        if (kind == 9 && values.Count > 0)
            return values[random.Next(values.Count)];
        if (kind >= 7)
            return new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), Attribute = other, AttributeId = other.Id, StringValue = "other" };
        return MemberValue();
    }

    private static MetaverseObjectAttributeValue MemberValue() => new()
    {
        Id = Guid.NewGuid(), Attribute = StaticMembers, AttributeId = StaticMembers.Id, ReferenceValueId = Guid.NewGuid()
    };

    private static bool SameReferencesInOrder<T>(IReadOnlyList<T> actual, IReadOnlyList<T> expected) where T : class =>
        actual.Count == expected.Count && Enumerable.Range(0, expected.Count).All(i => ReferenceEquals(actual[i], expected[i]));

    private enum ReadStepKind
    {
        Nothing,
        AppendTheValueRead,
        Append,
        DropTheLast,
        Clear,
        Replace
    }

    private sealed record ReadStep(ReadStepKind Kind, List<MetaverseObjectAttributeValue> Values, int DropCount = 0);
}
