// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for the orphaned-contribution recall (#1533): a Metaverse Object attribute value whose
/// provenance names an Attribute Flow mapping that has been DELETED must be staged for removal when its
/// contributing Connected System Object is re-evaluated. A DISABLED mapping (or Synchronisation Rule) is the
/// deliberate contrast (#1537): the flow is dormant, not gone, so its contributed values stay in place until a
/// surviving contributor takes them over or the flow is re-enabled or deleted. Values stamped by other systems,
/// values with no rule provenance, and values with a live mapping are never touched. No mocking, no database.
/// </summary>
public class SyncEngineOrphanedContributionRecallTests
{
    private const int MvoTypeId = 10;
    private const int SystemId = 5;
    private const int OtherSystemId = 9;

    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    private static MetaverseAttribute DeptAttr() => new() { Id = 100, Name = "department", Type = AttributeDataType.Text };

    private static MetaverseAttribute TitleAttr() => new() { Id = 101, Name = "jobTitle", Type = AttributeDataType.Text };

    /// <summary>An import rule with a single text mapping to <paramref name="target"/>.</summary>
    private static SyncRule ImportRule(int syncRuleId, MetaverseAttribute target, bool ruleEnabled = true, bool mappingEnabled = true, int csoAttrId = 200)
    {
        var mapping = new SyncRuleMapping
        {
            Id = syncRuleId, SyncRuleId = syncRuleId, TargetMetaverseAttribute = target, Priority = 1, Enabled = mappingEnabled
        };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = csoAttrId, Order = 1 });
        return new SyncRule
        {
            Id = syncRuleId,
            ConnectedSystemId = SystemId,
            MetaverseObjectTypeId = MvoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = ruleEnabled,
            AttributeFlowRules = [mapping]
        };
    }

    private static MetaverseObject MvoWithType() => new() { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = MvoTypeId } };

    private static ConnectedSystemObject CsoJoinedTo(MetaverseObject mvo, int connectedSystemId = SystemId) =>
        new() { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = connectedSystemId, MetaverseObject = mvo };

    private static MetaverseObjectAttributeValue SeedValue(
        MetaverseObject mvo, MetaverseAttribute attr, string? value, int? syncRuleId, int? systemId, bool nullValue = false)
    {
        var av = new MetaverseObjectAttributeValue
        {
            Attribute = attr,
            AttributeId = attr.Id,
            StringValue = value,
            NullValue = nullValue,
            ContributedBySyncRuleId = syncRuleId,
            ContributedBySystemId = systemId
        };
        mvo.AttributeValues.Add(av);
        return av;
    }

    [Test]
    public void RecallOrphanedContributions_MappingDeleted_StagesValueForRemoval()
    {
        // The contributor cache is built from a rule that no longer carries a department mapping (it was
        // deleted); the incumbent value still names that rule, so nothing asserts it any more.
        var dept = DeptAttr();
        var title = TitleAttr();
        var ruleWithoutDeptMapping = ImportRule(syncRuleId: 1, title);
        var context = new AttributePriorityContext(new[] { ruleWithoutDeptMapping });

        var mvo = MvoWithType();
        var orphan = SeedValue(mvo, dept, "Engineering", syncRuleId: 1, systemId: SystemId);
        var cso = CsoJoinedTo(mvo);

        var recalled = _engine.RecallOrphanedContributions(cso, context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recalled, Has.Count.EqualTo(1), "the orphaned value must be reported as recalled");
            Assert.That(mvo.PendingAttributeValueRemovals, Does.Contain(orphan), "the orphaned value must be staged for removal");
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "recall stages removals only; re-election is the caller's pass");
        }
    }

    [Test]
    public void RecallOrphanedContributions_MappingDisabled_LeavesValueInPlace()
    {
        // A disabled mapping (#1485) is dormant, not gone (#1537): the administrator has paused the flow and
        // may re-enable it, so its contributed value stays in place. It leaves the contributor cache (so a
        // surviving contributor still takes the attribute over), but with no survivor the value is retained.
        var dept = DeptAttr();
        var rule = ImportRule(syncRuleId: 1, dept, mappingEnabled: false);
        var context = new AttributePriorityContext(new[] { rule });

        var mvo = MvoWithType();
        SeedValue(mvo, dept, "Engineering", syncRuleId: 1, systemId: SystemId);
        var cso = CsoJoinedTo(mvo);

        var recalled = _engine.RecallOrphanedContributions(cso, context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recalled, Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "a disabled mapping's contribution must be retained, not recalled");
        }
    }

    [Test]
    public void RecallOrphanedContributions_RuleDisabled_LeavesValueInPlace()
    {
        // Disabling a whole Synchronisation Rule is the same dormant statement at rule scope (#1537): the
        // rule's contributions leave the cache (survivor takeover is unaffected, as Scenario 14's
        // DisabledRuleNoOpinion proves), but a sole contributor's values are retained, not cleared.
        var dept = DeptAttr();
        var disabledRule = ImportRule(syncRuleId: 1, dept, ruleEnabled: false);
        var context = new AttributePriorityContext(new[] { disabledRule });

        var mvo = MvoWithType();
        SeedValue(mvo, dept, "Engineering", syncRuleId: 1, systemId: SystemId);
        var cso = CsoJoinedTo(mvo);

        var recalled = _engine.RecallOrphanedContributions(cso, context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recalled, Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "a disabled rule's contribution must be retained, not recalled");
        }
    }

    [Test]
    public void RecallOrphanedContributions_LiveMapping_LeavesValueInPlace()
    {
        var dept = DeptAttr();
        var rule = ImportRule(syncRuleId: 1, dept);
        var context = new AttributePriorityContext(new[] { rule });

        var mvo = MvoWithType();
        SeedValue(mvo, dept, "Engineering", syncRuleId: 1, systemId: SystemId);
        var cso = CsoJoinedTo(mvo);

        var recalled = _engine.RecallOrphanedContributions(cso, context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recalled, Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "a value whose mapping is alive must not be recalled");
        }
    }

    [Test]
    public void RecallOrphanedContributions_ValueContributedByAnotherSystem_LeftAlone()
    {
        // Another system's stale value is that system's business: its own run recalls it. Recalling it here
        // would let one system silently clear another system's contribution.
        var dept = DeptAttr();
        var context = new AttributePriorityContext(new[] { ImportRule(syncRuleId: 1, TitleAttr()) });

        var mvo = MvoWithType();
        SeedValue(mvo, dept, "Engineering", syncRuleId: 2, systemId: OtherSystemId);
        var cso = CsoJoinedTo(mvo);

        var recalled = _engine.RecallOrphanedContributions(cso, context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recalled, Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "another system's value must never be recalled by this system's run");
        }
    }

    [Test]
    public void RecallOrphanedContributions_ValueWithoutRuleProvenance_LeftAlone()
    {
        // No Synchronisation Rule stamp means internally managed data, a pre-provenance value, or a deleted
        // rule's ON DELETE SET NULL. None of those may be cleared on the strength of a cache miss.
        var dept = DeptAttr();
        var context = new AttributePriorityContext(new[] { ImportRule(syncRuleId: 1, TitleAttr()) });

        var mvo = MvoWithType();
        SeedValue(mvo, dept, "Engineering", syncRuleId: null, systemId: SystemId);
        var cso = CsoJoinedTo(mvo);

        _engine.RecallOrphanedContributions(cso, context);

        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "a value with no rule provenance must not be recalled");
    }

    [Test]
    public void RecallOrphanedContributions_ValueAlreadyPendingRemoval_NotDuplicated()
    {
        var dept = DeptAttr();
        var context = new AttributePriorityContext(new[] { ImportRule(syncRuleId: 1, TitleAttr()) });

        var mvo = MvoWithType();
        var orphan = SeedValue(mvo, dept, "Engineering", syncRuleId: 1, systemId: SystemId);
        mvo.PendingAttributeValueRemovals.Add(orphan);
        var cso = CsoJoinedTo(mvo);

        var recalled = _engine.RecallOrphanedContributions(cso, context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recalled, Is.Empty, "a value already staged for removal is not recalled again");
            Assert.That(mvo.PendingAttributeValueRemovals.Count(av => av == orphan), Is.EqualTo(1), "no duplicate removal may be staged");
        }
    }

    [Test]
    public void RecallOrphanedContributions_AssertedNullMarkerFromDeadMapping_Recalled()
    {
        // An asserted-null marker (#91) carries provenance like any value; when its mapping dies, the
        // assertion dies with it and the marker must be recalled so it stops blocking lower contributors.
        var dept = DeptAttr();
        var context = new AttributePriorityContext(new[] { ImportRule(syncRuleId: 1, TitleAttr()) });

        var mvo = MvoWithType();
        var marker = SeedValue(mvo, dept, value: null, syncRuleId: 1, systemId: SystemId, nullValue: true);
        var cso = CsoJoinedTo(mvo);

        _engine.RecallOrphanedContributions(cso, context);

        Assert.That(mvo.PendingAttributeValueRemovals, Does.Contain(marker), "a dead mapping's asserted-null marker must be recalled");
    }

    [Test]
    public void RecallOrphanedContributions_MvoTypeNotLoaded_RecallsNothing()
    {
        // Without the Metaverse Object Type the cache cannot be keyed, so no liveness claim can be made;
        // recalling on unknown liveness would risk clearing legitimate values.
        var dept = DeptAttr();
        var context = new AttributePriorityContext(new[] { ImportRule(syncRuleId: 1, TitleAttr()) });

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        SeedValue(mvo, dept, "Engineering", syncRuleId: 1, systemId: SystemId);
        var cso = CsoJoinedTo(mvo);

        var recalled = _engine.RecallOrphanedContributions(cso, context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recalled, Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "no recall may happen when mapping liveness cannot be evaluated");
        }
    }
}
