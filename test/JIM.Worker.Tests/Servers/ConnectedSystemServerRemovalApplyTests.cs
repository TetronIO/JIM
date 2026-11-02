// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Utility;
using JIM.PostgresData;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Applying an import's pending value removals to a Connected System Object (#2664). The import diff finds the values a
/// group no longer holds in one pass (#988), so a group whose membership is replaced wholesale stages a removal per
/// member; each removal then scanned every value the object held to find its match, so applying them cost the
/// membership times the removals: about 2.5 billion comparisons for a 50,000-member group. These tests reach the apply
/// step through <see cref="JIM.Application.Servers.ConnectedSystemServer.LinkUpdateChangeRecords"/>, the import's own
/// public entry to it, whose only other work is recording the change, in time that grows with the number of changes.
/// </summary>
[TestFixture]
public class ConnectedSystemServerRemovalApplyTests
{
    private const int HeldMemberCount = 50_000;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    private JimApplication _jim = null!;
    private ConnectedSystemObjectTypeAttribute _externalId = null!;
    private ConnectedSystemObjectTypeAttribute _member = null!;
    private ConnectedSystemObjectType _groupType = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _jim = new JimApplication(new PostgresDataRepository(new Mock<JimDbContext>().Object));
        _externalId = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        _member = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        _groupType = new ConnectedSystemObjectType { Id = 1, Name = "group", Attributes = [_externalId, _member] };
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    /// <summary>
    /// A 50,000-member group whose import replaces its whole membership with two new members. Most members are
    /// persisted and carry an Id, so they are removed by Id; every tenth was added earlier in the run and carries none,
    /// so it is removed by reference. Measured when #2664 was fixed, this took about 24 s before the fix and under
    /// 0.1 s after, change record included; the bound sits well clear of both, so a return of the per-removal scan
    /// fails loudly rather than flakily.
    /// </summary>
    [Test]
    public void LinkUpdateChangeRecords_FiftyThousandMemberGroupLosingEveryMember_AppliesTheRemovalsWithinBound()
    {
        var group = Group();
        var held = Enumerable.Range(0, HeldMemberCount).Select(i => Member(group, i, persisted: i % 10 != 0)).ToList();
        group.AttributeValues.AddRange(held);
        group.PendingAttributeValueRemovals.AddRange(held);
        var joined = new[] { Member(group, HeldMemberCount, persisted: false), Member(group, HeldMemberCount + 1, persisted: false) };
        group.PendingAttributeValueAdditions.AddRange(joined);
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ConnectedSystemObject = group };

        var stopwatch = Stopwatch.StartNew();
        _jim.ConnectedSystems.LinkUpdateChangeRecords([group], [rpei], changeTrackingEnabled: true);
        stopwatch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet).Or.Null, rpei.ErrorMessage);
            Assert.That(group.AttributeValues.Where(v => v.AttributeId == _member.Id), Is.EqualTo(joined),
                "the group holds exactly its two new members");
            Assert.That(group.AttributeValues.Count(v => v.AttributeId == _externalId.Id), Is.EqualTo(1), "the External ID is untouched");
            Assert.That(rpei.ConnectedSystemObjectChange?.AttributeChanges.SelectMany(c => c.ValueChanges).Count(v => v.ValueChangeType == ValueChangeType.Remove),
                Is.EqualTo(HeldMemberCount), "every departed member is recorded as removed");
            Assert.That(stopwatch.Elapsed, Is.LessThan(Bound),
                $"applying {HeldMemberCount:N0} member removals to a group holding {HeldMemberCount:N0} members took {stopwatch.Elapsed.TotalSeconds:N1}s; " +
                "the removals must be applied in one pass over the group's values, not a scan of them per removal");
        }
    }

    /// <summary>
    /// The apply step against applying each removal in turn, which is what it replaced, over random groups either side of
    /// the size at which the values are indexed. The groups hold persisted values (removed by Id) and unpersisted ones
    /// (removed by reference), values sharing an Id, and a value held twice. The removals name held values more than
    /// once, take back values added in the same call, and name values the group does not hold, including ones carrying a
    /// held value's Id. Every trial must leave the same values, by reference and in the same order, and record the same
    /// change.
    /// </summary>
    [Test]
    public void LinkUpdateChangeRecords_AnyValuesAndPendingChanges_LeavesWhatApplyingEachRemovalInTurnLeft()
    {
        var random = new Random(2664);
        for (var trial = 0; trial < 1_000; trial++)
        {
            var group = Group();
            var held = new List<ConnectedSystemObjectAttributeValue>();
            var heldCount = random.Next(ReferenceIndexedList<ConnectedSystemObjectAttributeValue>.IndexThreshold * 3);
            for (var i = 0; i < heldCount; i++)
                held.Add(HeldValue(random, group, held, i));
            group.AttributeValues.AddRange(held);

            var additions = Enumerable.Range(0, random.Next(6)).Select(i => Addition(random, group, held, heldCount + i)).ToList();
            var removals = Enumerable.Range(0, random.Next(heldCount + 6)).Select(i => Removal(random, group, held, additions, heldCount + 100 + i)).ToList();
            group.PendingAttributeValueAdditions.AddRange(additions);
            group.PendingAttributeValueRemovals.AddRange(removals);
            var expected = ApplyEachRemovalInTurn(group.AttributeValues, additions, removals);
            var expectedRecord = additions.Select(a => (ValueChangeType.Add, a.UnresolvedReferenceValue))
                .Concat(removals.Select(r => (ValueChangeType.Remove, r.UnresolvedReferenceValue)))
                .ToList();
            var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ConnectedSystemObject = group };

            _jim.ConnectedSystems.LinkUpdateChangeRecords([group], [rpei], changeTrackingEnabled: true);

            var recorded = rpei.ConnectedSystemObjectChange?.AttributeChanges.SelectMany(c => c.ValueChanges).Select(v => (v.ValueChangeType, v.StringValue)).ToList() ?? [];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet).Or.Null, $"trial {trial}: {rpei.ErrorMessage}");
                Assert.That(group.AttributeValues.Count, Is.EqualTo(expected.Count), $"trial {trial}: how many values are left");
                Assert.That(SameReferencesInOrder(group.AttributeValues, expected), Is.True, $"trial {trial}: the same values, by reference, in the same order");
                Assert.That(recorded, Is.EqualTo(expectedRecord), $"trial {trial}: the change records every addition and removal staged");
                Assert.That(group.PendingAttributeValueAdditions, Is.Empty, $"trial {trial}");
                Assert.That(group.PendingAttributeValueRemovals, Is.Empty, $"trial {trial}");
            }
        }
    }

    // The apply step as it stood before #2664: the additions, then each removal in turn, by reference when it carries no
    // Id and otherwise every value carrying its Id.
    private static List<ConnectedSystemObjectAttributeValue> ApplyEachRemovalInTurn(
        IEnumerable<ConnectedSystemObjectAttributeValue> held,
        List<ConnectedSystemObjectAttributeValue> additions,
        List<ConnectedSystemObjectAttributeValue> removals)
    {
        var values = new List<ConnectedSystemObjectAttributeValue>(held);
        values.AddRange(additions);
        foreach (var removal in removals)
        {
            if (removal.Id == Guid.Empty)
                values.Remove(removal);
            else
                values.RemoveAll(av => av.Id == removal.Id);
        }
        return values;
    }

    // Mostly persisted and unpersisted members; now and then a distinct value carrying another held value's Id, or a
    // held value held a second time.
    private ConnectedSystemObjectAttributeValue HeldValue(Random random, ConnectedSystemObject group, List<ConnectedSystemObjectAttributeValue> held, int number)
    {
        var kind = random.Next(10);
        if (kind == 8 && held.Count > 0)
            return Member(group, number, held[random.Next(held.Count)].Id);
        if (kind == 9 && held.Count > 0)
            return held[random.Next(held.Count)];
        return Member(group, number, persisted: kind < 5);
    }

    // Mostly new members, persisted or not; now and then a distinct value carrying a held value's Id.
    private ConnectedSystemObjectAttributeValue Addition(Random random, ConnectedSystemObject group, List<ConnectedSystemObjectAttributeValue> held, int number)
    {
        if (random.Next(5) == 0 && held.Count > 0)
            return Member(group, number, held[random.Next(held.Count)].Id);
        return Member(group, number, persisted: random.Next(3) == 0);
    }

    // Mostly held values, so some are named more than once; otherwise a value added in the same call, a value the group
    // does not hold (persisted or not), or a value the group does not hold carrying a held value's Id.
    private ConnectedSystemObjectAttributeValue Removal(
        Random random,
        ConnectedSystemObject group,
        List<ConnectedSystemObjectAttributeValue> held,
        List<ConnectedSystemObjectAttributeValue> additions,
        int number)
    {
        var kind = random.Next(20);
        if (kind < 12 && held.Count > 0)
            return held[random.Next(held.Count)];
        if (kind < 15 && additions.Count > 0)
            return additions[random.Next(additions.Count)];
        if (kind < 18 || held.Count == 0)
            return Member(group, number, persisted: kind % 2 == 0);
        return Member(group, number, held[random.Next(held.Count)].Id);
    }

    private static bool SameReferencesInOrder<T>(IReadOnlyList<T> actual, IReadOnlyList<T> expected) where T : class =>
        actual.Count == expected.Count && Enumerable.Range(0, expected.Count).All(i => ReferenceEquals(actual[i], expected[i]));

    private ConnectedSystemObject Group()
    {
        var group = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            TypeId = _groupType.Id,
            Type = _groupType,
            ExternalIdAttributeId = _externalId.Id
        };
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = group, Attribute = _externalId, AttributeId = _externalId.Id, GuidValue = Guid.NewGuid()
        });
        return group;
    }

    private ConnectedSystemObjectAttributeValue Member(ConnectedSystemObject group, int number, bool persisted) =>
        Member(group, number, persisted ? Guid.NewGuid() : Guid.Empty);

    private ConnectedSystemObjectAttributeValue Member(ConnectedSystemObject group, int number, Guid id) => new()
    {
        Id = id,
        ConnectedSystemObject = group,
        Attribute = _member,
        AttributeId = _member.Id,
        ReferenceValueId = Guid.NewGuid(),
        UnresolvedReferenceValue = $"CN=Member {number},OU=Users,DC=example,DC=local"
    };
}
