// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Unit tests for <see cref="ProvenanceLogic"/> (#399): origin resolution, the fixed and usage-dependent
/// Attribute Priority source states, and attribute history Set-pairing. Pure logic, no database or mocks needed.
/// </summary>
[TestFixture]
public class ProvenanceLogicTests
{
    #region ResolveOrigin

    [Test]
    public void ResolveOrigin_NoContributingSystem_ReturnsNotRecorded()
    {
        var origin = ProvenanceLogic.ResolveOrigin(null, null, assertsNoValue: false, connectedSystemName: null, syncRuleName: null);

        Assert.That(origin!.Kind, Is.EqualTo(ValueOriginKind.NotRecorded));
        Assert.That(origin.AssertsNoValue, Is.False);
    }

    [Test]
    public void ResolveOrigin_NoContributingSystemButAssertsNoValue_ReturnsNotRecordedWithAssertion()
    {
        var origin = ProvenanceLogic.ResolveOrigin(null, null, assertsNoValue: true, connectedSystemName: null, syncRuleName: null);

        Assert.That(origin!.Kind, Is.EqualTo(ValueOriginKind.NotRecorded));
        Assert.That(origin.AssertsNoValue, Is.True);
    }

    [Test]
    public void ResolveOrigin_SystemAndRulePresent_ReturnsSynchronisationRuleOrigin()
    {
        var origin = ProvenanceLogic.ResolveOrigin(3, 7, assertsNoValue: false, connectedSystemName: "HR", syncRuleName: "HR Import");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(origin!.Kind, Is.EqualTo(ValueOriginKind.SynchronisationRule));
            Assert.That(origin!.ConnectedSystemId, Is.EqualTo(3));
            Assert.That(origin!.ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(origin!.SyncRuleId, Is.EqualTo(7));
            Assert.That(origin!.SyncRuleName, Is.EqualTo("HR Import"));
            Assert.That(origin!.SyncRuleDeleted, Is.False);
            Assert.That(origin.AssertsNoValue, Is.False);
        }
    }

    [Test]
    public void ResolveOrigin_SystemPresentButRuleDeleted_ReturnsSyncRuleDeletedWithNoName()
    {
        // ContributedBySystemId survives Synchronisation Rule deletion; ContributedBySyncRuleId is nulled and
        // MetaverseObjectAttributeValue carries no name snapshot to fall back on.
        var origin = ProvenanceLogic.ResolveOrigin(3, null, assertsNoValue: false, connectedSystemName: "Facilities", syncRuleName: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(origin!.Kind, Is.EqualTo(ValueOriginKind.SynchronisationRule));
            Assert.That(origin!.ConnectedSystemId, Is.EqualTo(3));
            Assert.That(origin!.ConnectedSystemName, Is.EqualTo("Facilities"));
            Assert.That(origin!.SyncRuleId, Is.Null);
            Assert.That(origin!.SyncRuleName, Is.Null);
            Assert.That(origin!.SyncRuleDeleted, Is.True);
        }
    }

    [Test]
    public void ResolveOrigin_SystemAssertsNoValue_SetsAssertsNoValueOnSynchronisationRuleOrigin()
    {
        var origin = ProvenanceLogic.ResolveOrigin(3, 7, assertsNoValue: true, connectedSystemName: "HR", syncRuleName: "HR Import");

        Assert.That(origin.AssertsNoValue, Is.True);
    }

    #endregion

    #region ResolveChangeValueOrigin

    [Test]
    public void ResolveChangeValueOrigin_NothingRecorded_ReturnsNull()
    {
        var origin = ProvenanceLogic.ResolveChangeValueOrigin(null, null, null, null);

        Assert.That(origin, Is.Null);
    }

    [Test]
    public void ResolveChangeValueOrigin_RuleStillExistsAndSystemResolved_ReturnsSynchronisationRuleOrigin()
    {
        var origin = ProvenanceLogic.ResolveChangeValueOrigin(
            contributedBySyncRuleId: 7,
            contributedBySyncRuleName: "HR Import",
            contributedBySystemId: 3,
            contributedBySystemName: "HR");

        Assert.That(origin, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(origin!.Kind, Is.EqualTo(ValueOriginKind.SynchronisationRule));
            Assert.That(origin!.ConnectedSystemId, Is.EqualTo(3));
            Assert.That(origin!.ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(origin!.SyncRuleId, Is.EqualTo(7));
            Assert.That(origin!.SyncRuleName, Is.EqualTo("HR Import"));
            Assert.That(origin!.SyncRuleDeleted, Is.False);
        }
    }

    [Test]
    public void ResolveChangeValueOrigin_RuleSinceDeleted_ReturnsSyncRuleDeletedWithNoSystem()
    {
        // The change row's ContributedBySyncRuleId is nulled when the rule is deleted, so the caller can never
        // resolve a Connected System for it; the name snapshot on the change row survives regardless, but the
        // chip renders "rule deleted" rather than the stale name (matching ResolveOrigin's live-value semantics).
        var origin = ProvenanceLogic.ResolveChangeValueOrigin(
            contributedBySyncRuleId: null,
            contributedBySyncRuleName: "Facilities Import",
            contributedBySystemId: null,
            contributedBySystemName: null);

        Assert.That(origin, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(origin!.Kind, Is.EqualTo(ValueOriginKind.SynchronisationRule));
            Assert.That(origin!.ConnectedSystemId, Is.Null);
            Assert.That(origin!.SyncRuleId, Is.Null);
            Assert.That(origin!.SyncRuleName, Is.Null);
            Assert.That(origin!.SyncRuleDeleted, Is.True);
        }
    }

    #endregion

    #region ApplyGeneratedValue / IsGeneratedHistoryValue

    private static ValueOrigin RuleOrigin(int syncRuleId) => new()
    {
        Kind = ValueOriginKind.SynchronisationRule,
        ConnectedSystemId = 9,
        ConnectedSystemName = "HR",
        SyncRuleId = syncRuleId,
        SyncRuleName = "HR Import Users"
    };

    private static GeneratedValueOwnership Ownership(int syncRuleId = 5, string value = "E1001", string? previousValue = null, bool corrected = false,
        bool isCurrentValue = true) => new()
    {
        IsCurrentValue = isCurrentValue,
        AttributeId = 42,
        SyncRuleId = syncRuleId,
        SyncRuleMappingId = 7,
        Value = value,
        PreviousValue = previousValue,
        Corrected = corrected
    };

    [Test]
    public void ApplyGeneratedValue_ValueFromTheGeneratingRule_IsAGeneratedValueThatKeepsItsSystemAndRule()
    {
        var origin = ProvenanceLogic.ApplyGeneratedValue(RuleOrigin(5), Ownership(syncRuleId: 5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(origin.Kind, Is.EqualTo(ValueOriginKind.GeneratedValue));
            Assert.That(origin.ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(origin.SyncRuleName, Is.EqualTo("HR Import Users"));
            Assert.That(origin.Corrected, Is.False);
        }
    }

    [Test]
    public void ApplyGeneratedValue_ValueFromAnotherRule_StaysASynchronisationRuleValue()
    {
        var origin = ProvenanceLogic.ApplyGeneratedValue(RuleOrigin(6), Ownership(syncRuleId: 5));

        Assert.That(origin.Kind, Is.EqualTo(ValueOriginKind.SynchronisationRule));
    }

    [Test]
    public void ApplyGeneratedValue_NothingGenerated_ReturnsTheOriginUnchanged()
    {
        var original = RuleOrigin(5);

        Assert.That(ProvenanceLogic.ApplyGeneratedValue(original, null), Is.SameAs(original));
    }

    [Test]
    public void ApplyGeneratedValue_RuleDeleted_StaysASynchronisationRuleValue()
    {
        // A deleted rule's generated mapping cascades its assignments away, so an ownership naming no rule cannot
        // match; guard the null anyway rather than matching null to null.
        var origin = ProvenanceLogic.ApplyGeneratedValue(RuleOrigin(5) with { SyncRuleId = null, SyncRuleDeleted = true }, Ownership());

        Assert.That(origin.Kind, Is.EqualTo(ValueOriginKind.SynchronisationRule));
    }

    [Test]
    public void ApplyGeneratedValue_AssignmentHeldButNotTheCurrentValue_StaysASynchronisationRuleValue()
    {
        // A generated flow keeps its assignment while a higher-priority rule's value is in use, so an assignment
        // alone does not make the current value a Generated Value: it must be the value the object holds.
        var origin = ProvenanceLogic.ApplyGeneratedValue(RuleOrigin(5), Ownership(syncRuleId: 5, isCurrentValue: false));

        Assert.That(origin.Kind, Is.EqualTo(ValueOriginKind.SynchronisationRule));
    }

    [Test]
    public void ApplyGeneratedValue_ValueRevisedAfterACollision_IsMarkedCorrected()
    {
        var origin = ProvenanceLogic.ApplyGeneratedValue(RuleOrigin(5), Ownership(corrected: true));

        Assert.That(origin.Corrected, Is.True);
    }

    [TestCase(5, "E1001", true, TestName = "IsGeneratedHistoryValue_CurrentGeneratedValueFromTheGeneratingRule_IsTrue")]
    [TestCase(5, "E1000", true, TestName = "IsGeneratedHistoryValue_ValueItReplacedAfterACollision_IsTrue")]
    [TestCase(5, "E0042", false, TestName = "IsGeneratedHistoryValue_OtherValueFromTheSameRule_IsFalse")]
    [TestCase(6, "E1001", false, TestName = "IsGeneratedHistoryValue_SameValueFromAnotherRule_IsFalse")]
    public void IsGeneratedHistoryValue_MatchesOnTheGeneratingRuleAndAGeneratedValue(int syncRuleId, string value, bool expected)
    {
        // A rule's flow can be switched to Generated Value after it has already contributed plain values, so the
        // rule alone is not enough: the entry must also carry a value the generation actually produced.
        var entry = new AttributeHistoryEntry { Kind = AttributeHistoryChangeKind.Added, Value = value, SyncRuleId = syncRuleId };

        Assert.That(ProvenanceLogic.IsGeneratedHistoryValue(entry, Ownership(previousValue: "E1000")), Is.EqualTo(expected));
    }

    [Test]
    public void IsGeneratedHistoryValue_NothingGenerated_IsFalse()
    {
        var entry = new AttributeHistoryEntry { Kind = AttributeHistoryChangeKind.Added, Value = "E1001", SyncRuleId = 5 };

        Assert.That(ProvenanceLogic.IsGeneratedHistoryValue(entry, null), Is.False);
    }

    #endregion

    #region DetermineFixedState / NoteForFixedState

    [Test]
    public void DetermineFixedState_NotJoined_ReturnsNotJoinedRegardlessOfSourceType()
    {
        var state = ProvenanceLogic.DetermineFixedState(joined: false, SyncRuleMappingSourcesType.AttributeMapping);

        Assert.That(state, Is.EqualTo(AttributeSourceState.NotJoined));
    }

    [TestCase(SyncRuleMappingSourcesType.AdvancedMapping)]
    [TestCase(SyncRuleMappingSourcesType.NotSet)]
    public void DetermineFixedState_JoinedButNotEvaluableSourceType_ReturnsNotEvaluated(SyncRuleMappingSourcesType sourceType)
    {
        var state = ProvenanceLogic.DetermineFixedState(joined: true, sourceType);

        Assert.That(state, Is.EqualTo(AttributeSourceState.NotEvaluated));
    }

    [TestCase(SyncRuleMappingSourcesType.AttributeMapping)]
    [TestCase(SyncRuleMappingSourcesType.ExpressionMapping)]
    [TestCase(SyncRuleMappingSourcesType.GeneratedMapping)]
    public void DetermineFixedState_JoinedAndEvaluableSourceType_ReturnsNull(SyncRuleMappingSourcesType sourceType)
    {
        var state = ProvenanceLogic.DetermineFixedState(joined: true, sourceType);

        Assert.That(state, Is.Null);
    }

    [Test]
    public void NoteForFixedState_EvaluableSourceType_ReturnsNull()
    {
        Assert.That(ProvenanceLogic.NoteForFixedState(SyncRuleMappingSourcesType.AttributeMapping), Is.Null);
    }

    #endregion

    #region DetermineUsageState

    [Test]
    public void DetermineUsageState_IsCurrentContributor_ReturnsInUseEvenWithNoCandidateValues()
    {
        var state = ProvenanceLogic.DetermineUsageState(isCurrentContributor: true, candidateValueCount: 0);

        Assert.That(state, Is.EqualTo(AttributeSourceState.InUse));
    }

    [Test]
    public void DetermineUsageState_NotContributorWithValues_ReturnsOutranked()
    {
        var state = ProvenanceLogic.DetermineUsageState(isCurrentContributor: false, candidateValueCount: 2);

        Assert.That(state, Is.EqualTo(AttributeSourceState.Outranked));
    }

    [Test]
    public void DetermineUsageState_NotContributorWithNoValues_ReturnsNoValue()
    {
        var state = ProvenanceLogic.DetermineUsageState(isCurrentContributor: false, candidateValueCount: 0);

        Assert.That(state, Is.EqualTo(AttributeSourceState.NoValue));
    }

    #endregion

    #region PairAttributeHistory

    private static MetaverseAttributeHistoryRawEntry Row(Guid changeId, ValueChangeType type, string value, DateTime changeTime, int? syncRuleId = null, string? syncRuleName = null)
    {
        return new MetaverseAttributeHistoryRawEntry
        {
            ChangeId = changeId,
            ValueChangeType = type,
            DisplayValue = value,
            SyncRuleId = syncRuleId,
            SyncRuleName = syncRuleName,
            Change = new ProvenanceChange { ChangeTime = changeTime }
        };
    }

    [Test]
    public void PairAttributeHistory_SingleValuedRemoveThenAddInSameChange_PairsIntoOneSetEntry()
    {
        var changeId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var rows = new List<MetaverseAttributeHistoryRawEntry>
        {
            Row(changeId, ValueChangeType.Add, "New Name", now, syncRuleId: 5, syncRuleName: "HR Import"),
            Row(changeId, ValueChangeType.Remove, "Old Name", now)
        };

        var (entries, truncated) = ProvenanceLogic.PairAttributeHistory(rows, AttributePlurality.SingleValued, cap: 50);

        Assert.That(entries, Has.Count.EqualTo(1));
        var entry = entries[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Kind, Is.EqualTo(AttributeHistoryChangeKind.Set));
            Assert.That(entry.Value, Is.EqualTo("New Name"));
            Assert.That(entry.PreviousValue, Is.EqualTo("Old Name"));
            Assert.That(entry.SyncRuleId, Is.EqualTo(5));
            Assert.That(entry.SyncRuleName, Is.EqualTo("HR Import"));
            Assert.That(truncated, Is.False);
        }
    }

    [Test]
    public void PairAttributeHistory_SingleValuedAddOnly_ProducesAddedEntryNotSet()
    {
        var changeId = Guid.NewGuid();
        var rows = new List<MetaverseAttributeHistoryRawEntry>
        {
            Row(changeId, ValueChangeType.Add, "First Value", DateTime.UtcNow)
        };

        var (entries, _) = ProvenanceLogic.PairAttributeHistory(rows, AttributePlurality.SingleValued, cap: 50);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Kind, Is.EqualTo(AttributeHistoryChangeKind.Added));
    }

    [Test]
    public void PairAttributeHistory_MultiValuedAttribute_NeverPairsEvenWithOneAddAndOneRemove()
    {
        // Multi-valued attributes can legitimately add one value and remove a different one in the same
        // change; that is two independent facts, not a replacement, so pairing must not apply.
        var changeId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var rows = new List<MetaverseAttributeHistoryRawEntry>
        {
            Row(changeId, ValueChangeType.Add, "Group A", now),
            Row(changeId, ValueChangeType.Remove, "Group B", now)
        };

        var (entries, _) = ProvenanceLogic.PairAttributeHistory(rows, AttributePlurality.MultiValued, cap: 50);

        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries.Select(e => e.Kind), Is.EquivalentTo(new[] { AttributeHistoryChangeKind.Added, AttributeHistoryChangeKind.Removed }));
    }

    [Test]
    public void PairAttributeHistory_MultiValuedBatchChangeWithSeveralAddsAndRemoves_EmitsOneEntryPerRow()
    {
        var changeId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var rows = new List<MetaverseAttributeHistoryRawEntry>
        {
            Row(changeId, ValueChangeType.Add, "Group A", now),
            Row(changeId, ValueChangeType.Add, "Group B", now),
            Row(changeId, ValueChangeType.Remove, "Group C", now)
        };

        var (entries, _) = ProvenanceLogic.PairAttributeHistory(rows, AttributePlurality.MultiValued, cap: 50);

        Assert.That(entries, Has.Count.EqualTo(3));
    }

    [Test]
    public void PairAttributeHistory_MoreEntriesThanCap_TruncatesAndReportsTruncated()
    {
        var rows = new List<MetaverseAttributeHistoryRawEntry>();
        for (var i = 0; i < 5; i++)
            rows.Add(Row(Guid.NewGuid(), ValueChangeType.Add, $"Value {i}", DateTime.UtcNow.AddMinutes(-i)));

        var (entries, truncated) = ProvenanceLogic.PairAttributeHistory(rows, AttributePlurality.SingleValued, cap: 3);

        Assert.That(entries, Has.Count.EqualTo(3));
        Assert.That(truncated, Is.True);
    }

    [Test]
    public void PairAttributeHistory_NewestChangeFirstOrderIsPreserved()
    {
        var newer = Guid.NewGuid();
        var older = Guid.NewGuid();
        var rows = new List<MetaverseAttributeHistoryRawEntry>
        {
            Row(newer, ValueChangeType.Add, "Newer", DateTime.UtcNow),
            Row(older, ValueChangeType.Add, "Older", DateTime.UtcNow.AddDays(-1))
        };

        var (entries, _) = ProvenanceLogic.PairAttributeHistory(rows, AttributePlurality.SingleValued, cap: 50);

        Assert.That(entries.Select(e => e.Value), Is.EqualTo(new[] { "Newer", "Older" }));
    }

    #endregion
}
