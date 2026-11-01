// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections;
using System.Diagnostics;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Scale guards for no-net-change detection on a large multi-valued attribute (#2645). Scenario 008 at
/// Scale500k25kGroups spent 49.9 s of one Delta Synchronisation on a single 50,000-member group that gained two members
/// and lost one: every member value staged was checked against every member the target already held, so the cost grew
/// with the square of the membership. These tests stage exactly that change and require the held values to be matched
/// in time that grows with the membership, not its square.
/// </summary>
[TestFixture]
public class SyncEngineNoNetChangeScaleTests
{
    private const int HeldValueCount = 50_000;

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    /// <summary>
    /// The direct Attribute Flow branch, counted through the Connected System Object attribute cache the engine is
    /// handed: the held members are read a fixed number of times for the attribute, never once per member staged.
    /// Before #2645 each of the 50,002 values staged read and scanned all 50,000 held members (about 2.5 billion reads).
    /// </summary>
    [Test]
    public void ComputeAttributeValueChanges_FiftyThousandMemberGroupGainingTwoAndLosingOne_ReadsTheHeldMembersOnceNotOncePerMember()
    {
        var source = new MetaverseAttribute { Id = 1, Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        var target = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        var exportRule = ExportRule(target, new SyncRuleMappingSource { Id = 700, MetaverseAttribute = source, MetaverseAttributeId = source.Id });

        var groupCso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var group = new MetaverseObject { Id = Guid.NewGuid() };

        // The target holds the membership as it stood before this synchronisation: each member a resolved reference to
        // the member's own Connected System Object, joined to the member's Metaverse Object.
        var memberIds = Enumerable.Range(0, HeldValueCount).Select(_ => Guid.NewGuid()).ToList();
        var held = memberIds.Select((memberId, index) => HeldMember(groupCso, target, memberId, index)).ToList();

        // The membership as this synchronisation leaves it: the first member gone, two new members joined. Every member
        // value is among the changed values, as the synchronisation hands them over for a group whose membership moved.
        var removed = Member(group, source, memberIds[0]);
        var joined = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var current = memberIds.Skip(1).Concat(joined).Select(memberId => Member(group, source, memberId)).ToList();
        group.AttributeValues.AddRange(current);

        // A constant number of passes over the held members, whatever the number of values staged.
        const long readBudget = 2L * HeldValueCount;
        var cache = new ReadCountingLookup(held.ToLookup(value => (groupCso.Id, value.AttributeId)), readBudget);

        var stopwatch = Stopwatch.StartNew();
        var changes = _engine.ComputeAttributeValueChanges(group, exportRule, [.. current, removed], PendingExportChangeType.Update,
            existingCso: groupCso, csoAttributeCache: cache, out var alreadyCurrentCount, removedAttributes: [removed]);
        stopwatch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Select(change => (change.ChangeType, change.UnresolvedReferenceValue)), Is.EquivalentTo(new[]
            {
                (PendingExportAttributeChangeType.Add, (string?)joined[0].ToString()),
                (PendingExportAttributeChangeType.Add, (string?)joined[1].ToString()),
                (PendingExportAttributeChangeType.Remove, (string?)memberIds[0].ToString())
            }), "the two new members are added and the departed one removed; every other member is already held");
            Assert.That(alreadyCurrentCount, Is.EqualTo(HeldValueCount - 1));
            Assert.That(cache.ValuesRead, Is.LessThanOrEqualTo(readBudget),
                $"the target's {HeldValueCount:N0} held members were read {cache.ValuesRead:N0} times in {stopwatch.Elapsed.TotalSeconds:N1}s; " +
                "each staged member value must be answered from one lookup of what the target holds, not a scan of it");
        }
    }

    /// <summary>
    /// The expression branch: an expression returning a list for a multi-valued target is compared with what the target
    /// holds value by value, exactly as a direct mapping is. Bounded by time, as the expression branch reads the held
    /// values into a list of its own before comparing; the margin is generous so the test fails loudly, not flakily,
    /// if the per-value scan ever comes back (measured in a Debug build: 89 s before #2645, 0.3 s after).
    /// </summary>
    [Test]
    public void ComputeAttributeValueChanges_ExpressionListOfFiftyThousandValuesAgainstAsManyHeld_CompletesWithinBound()
    {
        var mvType = new MetaverseObjectType { Id = 1, Name = "Group" };
        var codes = new MetaverseAttribute { Id = 1, Name = "Codes", Type = AttributeDataType.Text };
        var target = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "proxyAddresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var exportRule = ExportRule(target, new SyncRuleMappingSource { Id = 700, Order = 0, Expression = "Split(mv[\"Codes\"], \";\")" });

        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var held = Enumerable.Range(0, HeldValueCount)
            .Select(index => new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = target.Id, Attribute = target, StringValue = $"smtp:user{index}@panoply.local" })
            .ToList();

        // The expression's list: every held value but the first, and two new ones.
        var wanted = Enumerable.Range(1, HeldValueCount - 1).Select(index => $"smtp:user{index}@panoply.local")
            .Concat(["smtp:new0@panoply.local", "smtp:new1@panoply.local"]);
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = codes, AttributeId = codes.Id, StringValue = string.Join(";", wanted) });

        var cache = held.ToLookup(value => (cso.Id, value.AttributeId));

        var stopwatch = Stopwatch.StartNew();
        var changes = _engine.ComputeAttributeValueChanges(mvo, exportRule, [], PendingExportChangeType.Update,
            existingCso: cso, csoAttributeCache: cache, out var alreadyCurrentCount);
        stopwatch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Select(change => (change.ChangeType, change.StringValue)), Is.EquivalentTo(new[]
            {
                (PendingExportAttributeChangeType.Add, (string?)"smtp:new0@panoply.local"),
                (PendingExportAttributeChangeType.Add, (string?)"smtp:new1@panoply.local"),
                (PendingExportAttributeChangeType.Remove, (string?)"smtp:user0@panoply.local")
            }));
            Assert.That(alreadyCurrentCount, Is.EqualTo(HeldValueCount - 1));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)),
                $"staging an expression's {HeldValueCount + 1:N0} values against {HeldValueCount:N0} held took {stopwatch.Elapsed.TotalSeconds:N1}s; " +
                "each value must be answered from one lookup of what the target holds, not a scan of it");
        }
    }

    #region Helpers

    private static SyncRule ExportRule(ConnectedSystemObjectTypeAttribute target, SyncRuleMappingSource source)
    {
        var rule = new SyncRule { Id = 7, Name = "Export", Direction = SyncRuleDirection.Export, Enabled = true };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 70,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { source }
        });
        return rule;
    }

    private static ConnectedSystemObjectAttributeValue HeldMember(ConnectedSystemObject group, ConnectedSystemObjectTypeAttribute target, Guid memberMvoId, int index)
    {
        var memberCso = new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObjectId = memberMvoId };
        return new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = group,
            AttributeId = target.Id,
            Attribute = target,
            ReferenceValue = memberCso,
            ReferenceValueId = memberCso.Id,
            UnresolvedReferenceValue = $"CN=User {index},OU=Users,DC=panoply,DC=local"
        };
    }

    private static MetaverseObjectAttributeValue Member(MetaverseObject group, MetaverseAttribute source, Guid memberMvoId) => new()
    {
        Id = Guid.NewGuid(),
        MetaverseObject = group,
        AttributeId = source.Id,
        Attribute = source,
        ReferenceValueId = memberMvoId
    };

    /// <summary>
    /// The engine's Connected System Object attribute cache, counting every held value read through it. Fails the test
    /// the moment the reads pass <c>readBudget</c>, so a return of the per-value scan reports in a second
    /// rather than after the minutes its 2.5 billion reads take.
    /// </summary>
    private sealed class ReadCountingLookup(ILookup<(Guid CsoId, int AttributeId), ConnectedSystemObjectAttributeValue> inner, long readBudget)
        : ILookup<(Guid CsoId, int AttributeId), ConnectedSystemObjectAttributeValue>
    {
        public long ValuesRead { get; private set; }

        public IEnumerable<ConnectedSystemObjectAttributeValue> this[(Guid CsoId, int AttributeId) key] => Counted(inner[key]);

        public int Count => inner.Count;

        public bool Contains((Guid CsoId, int AttributeId) key) => inner.Contains(key);

        public IEnumerator<IGrouping<(Guid CsoId, int AttributeId), ConnectedSystemObjectAttributeValue>> GetEnumerator() => inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<ConnectedSystemObjectAttributeValue> Counted(IEnumerable<ConnectedSystemObjectAttributeValue> values)
        {
            foreach (var value in values)
            {
                if (++ValuesRead > readBudget)
                    Assert.Fail($"the target's held values were read more than {readBudget:N0} times; each staged value must be answered " +
                        "from one lookup of what the target holds, not a scan of it");

                yield return value;
            }
        }
    }

    #endregion
}
