using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Worker.Processors;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Tests for SyncOutcomeBuilder — the static helper that builds outcome tree nodes on RPEIs.
/// </summary>
[TestFixture]
public class SyncOutcomeBuilderTests
{
    private ActivityRunProfileExecutionItem CreateRpei(ObjectChangeType changeType = ObjectChangeType.Projected)
    {
        return new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = changeType
        };
    }

    #region AddRootOutcome

    [Test]
    public void AddRootOutcome_CreatesOutcomeAndAddsToRpeiAsync()
    {
        var rpei = CreateRpei();

        var outcome = SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected);

        Assert.That(rpei.SyncOutcomes, Has.Count.EqualTo(1));
        Assert.That(rpei.SyncOutcomes[0], Is.SameAs(outcome));
        Assert.That(outcome.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
        Assert.That(outcome.ParentSyncOutcome, Is.Null);
        Assert.That(outcome.ParentSyncOutcomeId, Is.Null);
        Assert.That(outcome.Ordinal, Is.EqualTo(0));
    }

    [Test]
    public void AddRootOutcome_SetsOptionalFields()
    {
        var rpei = CreateRpei();
        var targetId = Guid.NewGuid();

        var outcome = SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            targetEntityId: targetId,
            targetEntityDescription: "John Smith",
            detailCount: 12,
            detailMessage: "Projected to MVO");

        Assert.That(outcome.TargetEntityId, Is.EqualTo(targetId));
        Assert.That(outcome.TargetEntityDescription, Is.EqualTo("John Smith"));
        Assert.That(outcome.DetailCount, Is.EqualTo(12));
        Assert.That(outcome.DetailMessage, Is.EqualTo("Projected to MVO"));
    }

    [Test]
    public void AddRootOutcome_MultipleRoots_IncreasesOrdinal()
    {
        var rpei = CreateRpei();

        var root1 = SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        var root2 = SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        var root3 = SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);

        Assert.That(root1.Ordinal, Is.EqualTo(0));
        Assert.That(root2.Ordinal, Is.EqualTo(1));
        Assert.That(root3.Ordinal, Is.EqualTo(2));
        Assert.That(rpei.SyncOutcomes, Has.Count.EqualTo(3));
    }

    #endregion

    #region AddChildOutcome

    [Test]
    public void AddChildOutcome_CreatesChildUnderParent()
    {
        var rpei = CreateRpei();
        var root = SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected);

        var child = SyncOutcomeBuilder.AddChildOutcome(rpei, root,
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            detailCount: 5);

        Assert.That(child.ParentSyncOutcome, Is.SameAs(root));
        Assert.That(child.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow));
        Assert.That(child.DetailCount, Is.EqualTo(5));
        Assert.That(child.Ordinal, Is.EqualTo(0));
        Assert.That(root.Children, Has.Count.EqualTo(1));
        Assert.That(root.Children[0], Is.SameAs(child));
        // Child is also in the flat SyncOutcomes list
        Assert.That(rpei.SyncOutcomes, Has.Count.EqualTo(2));
        Assert.That(rpei.SyncOutcomes, Does.Contain(child));
    }

    [Test]
    public void AddChildOutcome_MultipleChildren_IncreasesOrdinal()
    {
        var rpei = CreateRpei();
        var root = SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected);

        var child1 = SyncOutcomeBuilder.AddChildOutcome(rpei, root,
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        var child2 = SyncOutcomeBuilder.AddChildOutcome(rpei, root,
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            targetEntityDescription: "AD");
        var child3 = SyncOutcomeBuilder.AddChildOutcome(rpei, root,
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            targetEntityDescription: "LDAP");

        Assert.That(child1.Ordinal, Is.EqualTo(0));
        Assert.That(child2.Ordinal, Is.EqualTo(1));
        Assert.That(child3.Ordinal, Is.EqualTo(2));
        Assert.That(root.Children, Has.Count.EqualTo(3));
    }

    [Test]
    public void AddChildOutcome_NestedChildren_ThreeLevelsDeep()
    {
        var rpei = CreateRpei();
        var root = SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        var attrFlow = SyncOutcomeBuilder.AddChildOutcome(rpei, root,
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            detailCount: 12);
        var pendingExport = SyncOutcomeBuilder.AddChildOutcome(rpei, attrFlow,
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            targetEntityDescription: "AD");

        Assert.That(root.Children, Has.Count.EqualTo(1));
        Assert.That(attrFlow.Children, Has.Count.EqualTo(1));
        Assert.That(pendingExport.ParentSyncOutcome, Is.SameAs(attrFlow));
        Assert.That(rpei.SyncOutcomes, Has.Count.EqualTo(3));
    }

    #endregion

    #region BuildOutcomeSummary

    [Test]
    public void BuildOutcomeSummary_EmptyOutcomes_NoSummary()
    {
        var rpei = CreateRpei();

        SyncOutcomeBuilder.BuildOutcomeSummary(rpei);

        Assert.That(rpei.OutcomeSummary, Is.Null);
    }

    [Test]
    public void BuildOutcomeSummary_SingleRootOutcome_GeneratesSummary()
    {
        var rpei = CreateRpei();
        SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected);

        SyncOutcomeBuilder.BuildOutcomeSummary(rpei);

        Assert.That(rpei.OutcomeSummary, Is.EqualTo("Projected:1"));
    }

    [Test]
    public void BuildOutcomeSummary_MultipleRootOutcomes_CountsByType()
    {
        var rpei = CreateRpei();
        SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);
        SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);

        SyncOutcomeBuilder.BuildOutcomeSummary(rpei);

        // Ordered by enum value: Projected comes before PendingExportCreated
        Assert.That(rpei.OutcomeSummary, Is.EqualTo("Projected:1,PendingExportCreated:2"));
    }

    [Test]
    public void BuildOutcomeSummary_ExcludesChildOutcomes()
    {
        var rpei = CreateRpei();
        var root = SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        SyncOutcomeBuilder.AddChildOutcome(rpei, root,
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, detailCount: 5);
        SyncOutcomeBuilder.AddChildOutcome(rpei, root,
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);

        SyncOutcomeBuilder.BuildOutcomeSummary(rpei);

        // Only the root outcome should appear in the summary
        Assert.That(rpei.OutcomeSummary, Is.EqualTo("Projected:1"));
    }

    [Test]
    public void BuildOutcomeSummary_ComplexTree_OnlyCountsRoots()
    {
        var rpei = CreateRpei();
        // Root 1: Projected with children
        var root1 = SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected, detailCount: 12);
        SyncOutcomeBuilder.AddChildOutcome(rpei, root1,
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, detailCount: 12);
        // Root 2: Disconnected
        var root2 = SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected, detailCount: 3);
        SyncOutcomeBuilder.AddChildOutcome(rpei, root2,
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, detailCount: 3);

        SyncOutcomeBuilder.BuildOutcomeSummary(rpei);

        Assert.That(rpei.OutcomeSummary, Is.EqualTo("Projected:1,Disconnected:1"));
    }

    #endregion

    #region All Outcome Types

    [Test]
    public void AddRootOutcome_AllSyncOutcomeTypes_CanBeCreated()
    {
        var rpei = CreateRpei();

        foreach (var outcomeType in Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>())
        {
            var outcome = SyncOutcomeBuilder.AddRootOutcome(rpei, outcomeType);
            Assert.That(outcome.OutcomeType, Is.EqualTo(outcomeType));
        }

        var totalTypes = Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>().Length;
        Assert.That(rpei.SyncOutcomes, Has.Count.EqualTo(totalTypes));
    }

    #endregion
}
