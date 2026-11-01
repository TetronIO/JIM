// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Scale guards for asking, of each value a large group holds, whether it is pending removal (#2664). Recall of
/// orphaned contributions and the cleared-attribute check that drives contributor re-election each ask it of every held
/// value, and answered by scanning the pending removals, so a group whose membership is replaced or emptied wholesale
/// cost its size times the removals staged. The pending removals now index themselves once they are many, and these
/// tests require the answers in time that grows with the membership, not its square.
/// </summary>
[TestFixture]
public class PendingRemovalMembershipScaleTests
{
    private const int MvoTypeId = 10;
    private const int SystemId = 5;
    private const int SyncRuleId = 7;
    private const int HeldValueCount = 50_000;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    private static readonly MetaverseAttribute DisplayName = new()
    {
        Id = 1, Name = Constants.BuiltInAttributes.DisplayName, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued
    };

    private static readonly MetaverseAttribute StaticMembers = new()
    {
        Id = 2, Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued
    };

    private static readonly MetaverseAttribute MailAliases = new()
    {
        Id = 3, Name = "Mail Aliases", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued
    };

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    /// <summary>
    /// A 50,000-member group whose source now holds no members: every member is staged for removal and nothing replaces
    /// them, so the members are cleared. The check asks, of each held value of the attribute, whether it is among the
    /// removals, which is how the synchronisation calls it before contributor re-election. Measured when #2664 was
    /// fixed, this took about 4.2 s before the fix and about 15 ms after; the bound sits well clear of both, so a
    /// return of the per-value scan fails loudly rather than flakily.
    /// </summary>
    [Test]
    public void GetClearedAttributeIds_FiftyThousandMemberGroupEmptied_FindsTheMembersClearedWithinBound()
    {
        var group = new MetaverseObject { Id = Guid.NewGuid() };
        group.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = group, Attribute = DisplayName, AttributeId = DisplayName.Id, StringValue = "All Staff"
        });
        var members = Enumerable.Range(0, HeldValueCount).Select(_ => new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = group, Attribute = StaticMembers, AttributeId = StaticMembers.Id, ReferenceValueId = Guid.NewGuid()
        }).ToList();
        group.AttributeValues.AddRange(members);
        group.PendingAttributeValueRemovals.AddRange(members);

        var stopwatch = Stopwatch.StartNew();
        var cleared = ContributorReElectionService.GetClearedAttributeIds(group, group.PendingAttributeValueAdditions, group.PendingAttributeValueRemovals);
        stopwatch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.EquivalentTo(new[] { StaticMembers.Id }), "the members are cleared and nothing else is");
            Assert.That(stopwatch.Elapsed, Is.LessThan(Bound),
                $"checking which attributes {HeldValueCount:N0} member removals clear took {stopwatch.Elapsed.TotalSeconds:N1}s; " +
                "each held member must be looked up in the pending removals, not found by a scan of them");
        }
    }

    /// <summary>
    /// The same emptied 50,000-member group, asked the way the callers that capture the pending changes before applying
    /// them ask it: a Connected System Object's obsoletion, a Synchronisation Rule's deletion recall, the Synchronised
    /// Deprovisioning preview and a scope exit's NoContributor outcome each hand over lists copied from the pending
    /// changes, not the pending removals themselves, and a copied list answers each held member's membership check by
    /// scanning itself (#2666). Measured when #2666 was fixed, this took about 4.3 s before the fix and about 10 ms after;
    /// the bound sits well clear of both, so a return of the per-value scan fails loudly rather than flakily.
    /// </summary>
    [Test]
    public void GetClearedAttributeIds_FiftyThousandMemberGroupEmptiedAndHandedACopy_FindsTheMembersClearedWithinBound()
    {
        var group = new MetaverseObject { Id = Guid.NewGuid() };
        group.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = group, Attribute = DisplayName, AttributeId = DisplayName.Id, StringValue = "All Staff"
        });
        var members = Enumerable.Range(0, HeldValueCount).Select(_ => new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = group, Attribute = StaticMembers, AttributeId = StaticMembers.Id, ReferenceValueId = Guid.NewGuid()
        }).ToList();
        group.AttributeValues.AddRange(members);
        group.PendingAttributeValueRemovals.AddRange(members);
        var additions = group.PendingAttributeValueAdditions.ToList();
        var removals = group.PendingAttributeValueRemovals.ToList();

        var stopwatch = Stopwatch.StartNew();
        var cleared = ContributorReElectionService.GetClearedAttributeIds(group, additions, removals);
        stopwatch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.EquivalentTo(new[] { StaticMembers.Id }), "the members are cleared and nothing else is");
            Assert.That(stopwatch.Elapsed, Is.LessThan(Bound),
                $"checking which attributes {HeldValueCount:N0} member removals, handed over as a copied list, clear took " +
                $"{stopwatch.Elapsed.TotalSeconds:N1}s; each held member must be looked up in the removals, not found by a scan of them");
        }
    }

    /// <summary>
    /// A person whose 50,000 Mail Aliases this synchronisation's live mapping has just replaced wholesale, so all
    /// 50,000 held aliases are pending removal when recall looks for contributions whose mapping is gone. The mapping
    /// is live, so nothing is recalled; the recall asks, of each value the system contributed, whether it is already
    /// pending removal. Measured when #2664 was fixed, this took about 4 s before the fix and about 5 ms after; the
    /// bound sits well clear of both, so a return of the per-value scan fails loudly rather than flakily.
    /// </summary>
    [Test]
    public void RecallOrphanedContributions_FiftyThousandValuesReplacedByALiveMapping_RecallsNothingWithinBound()
    {
        var mapping = new SyncRuleMapping { Id = SyncRuleId, SyncRuleId = SyncRuleId, TargetMetaverseAttribute = MailAliases, Priority = 1, Enabled = true };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = 200, Order = 1 });
        var syncRule = new SyncRule
        {
            Id = SyncRuleId, ConnectedSystemId = SystemId, MetaverseObjectTypeId = MvoTypeId, Direction = SyncRuleDirection.Import,
            Enabled = true, AttributeFlowRules = [mapping]
        };
        var context = new AttributePriorityContext([syncRule]);

        var person = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = MvoTypeId } };
        var aliases = Enumerable.Range(0, HeldValueCount).Select(i => new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = person, Attribute = MailAliases, AttributeId = MailAliases.Id, StringValue = $"alias-{i}@example.com",
            ContributedBySystemId = SystemId, ContributedBySyncRuleId = SyncRuleId
        }).ToList();
        person.AttributeValues.AddRange(aliases);
        person.PendingAttributeValueRemovals.AddRange(aliases);
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = SystemId, MetaverseObject = person };

        var stopwatch = Stopwatch.StartNew();
        var recalled = _engine.RecallOrphanedContributions(cso, context);
        stopwatch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recalled, Is.Empty, "the mapping is live, so nothing it contributed is orphaned");
            Assert.That(person.PendingAttributeValueRemovals, Has.Count.EqualTo(HeldValueCount), "nothing is staged a second time");
            Assert.That(stopwatch.Elapsed, Is.LessThan(Bound),
                $"recalling orphaned contributions from {HeldValueCount:N0} values, all pending removal, took {stopwatch.Elapsed.TotalSeconds:N1}s; " +
                "each contributed value must be looked up in the pending removals, not found by a scan of them");
        }
    }
}
