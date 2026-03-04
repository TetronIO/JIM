using JIM.Models.Activities;
using JIM.Models.Enums;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Tests for the ActivityRunProfileExecutionItemSyncOutcome model and related enums.
/// Verifies tree structure, OutcomeSummary population, and enum coverage.
/// </summary>
[TestFixture]
public class SyncOutcomeTests
{
    #region Entity Model

    [Test]
    public void SyncOutcome_NewInstance_HasDefaultValues()
    {
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome();

        Assert.That(outcome.Id, Is.EqualTo(Guid.Empty));
        Assert.That(outcome.ActivityRunProfileExecutionItemId, Is.EqualTo(Guid.Empty));
        Assert.That(outcome.ParentSyncOutcomeId, Is.Null);
        Assert.That(outcome.ParentSyncOutcome, Is.Null);
        Assert.That(outcome.Children, Is.Empty);
        Assert.That(outcome.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded));
        Assert.That(outcome.TargetEntityId, Is.Null);
        Assert.That(outcome.TargetEntityDescription, Is.Null);
        Assert.That(outcome.DetailCount, Is.Null);
        Assert.That(outcome.DetailMessage, Is.Null);
        Assert.That(outcome.Ordinal, Is.EqualTo(0));
    }

    [Test]
    public void SyncOutcome_TreeStructure_ParentChildRelationship()
    {
        // Arrange - build a simple tree: root -> child1, child2
        var root = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            Ordinal = 0
        };

        var child1 = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            ParentSyncOutcome = root,
            ParentSyncOutcomeId = root.Id,
            DetailCount = 12,
            Ordinal = 0
        };

        var child2 = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            ParentSyncOutcome = root,
            ParentSyncOutcomeId = root.Id,
            TargetEntityDescription = "AD",
            Ordinal = 1
        };

        root.Children.Add(child1);
        root.Children.Add(child2);

        // Assert
        Assert.That(root.Children, Has.Count.EqualTo(2));
        Assert.That(root.ParentSyncOutcomeId, Is.Null, "Root should have no parent");
        Assert.That(child1.ParentSyncOutcomeId, Is.EqualTo(root.Id));
        Assert.That(child2.ParentSyncOutcomeId, Is.EqualTo(root.Id));
    }

    [Test]
    public void SyncOutcome_DeepTree_ThreeLevelsDeep()
    {
        // Arrange - root -> attributeFlow -> pendingExportCreated
        var root = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            TargetEntityDescription = "John Smith"
        };

        var attrFlow = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            ParentSyncOutcomeId = root.Id,
            DetailCount = 12
        };
        root.Children.Add(attrFlow);

        var pendingExport1 = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            ParentSyncOutcomeId = attrFlow.Id,
            TargetEntityDescription = "AD"
        };

        var pendingExport2 = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            ParentSyncOutcomeId = attrFlow.Id,
            TargetEntityDescription = "LDAP"
        };

        attrFlow.Children.Add(pendingExport1);
        attrFlow.Children.Add(pendingExport2);

        // Assert
        Assert.That(root.Children, Has.Count.EqualTo(1));
        Assert.That(root.Children[0].Children, Has.Count.EqualTo(2));
        Assert.That(root.Children[0].Children[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
        Assert.That(root.Children[0].Children[1].TargetEntityDescription, Is.EqualTo("LDAP"));
    }

    #endregion

    #region RPEI SyncOutcomes Collection

    [Test]
    public void Rpei_SyncOutcomes_DefaultsToEmptyList()
    {
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Projected
        };

        Assert.That(rpei.SyncOutcomes, Is.Not.Null);
        Assert.That(rpei.SyncOutcomes, Is.Empty);
    }

    [Test]
    public void Rpei_OutcomeSummary_IsNullByDefault()
    {
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Projected
        };

        Assert.That(rpei.OutcomeSummary, Is.Null);
    }

    [Test]
    public void Rpei_OutcomeSummary_CanStoreFormattedString()
    {
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Projected,
            OutcomeSummary = "Projected:1,AttributeFlow:12,PendingExportCreated:2"
        };

        Assert.That(rpei.OutcomeSummary, Is.EqualTo("Projected:1,AttributeFlow:12,PendingExportCreated:2"));
    }

    #endregion

    #region Enum Coverage

    [Test]
    public void SyncOutcomeType_ContainsAllExpectedValues()
    {
        var values = Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>();

        // Import outcomes
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded));
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated));
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted));

        // Import outcomes — confirming import
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed));
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed));

        // Sync outcomes — inbound
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.Joined));
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow));
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected));
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope));
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted));

        // Sync outcomes — outbound
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned));
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));

        // Export outcomes
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.Exported));
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned));
    }

    [Test]
    public void SyncOutcomeTrackingLevel_ContainsAllExpectedValues()
    {
        var values = Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel>();

        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None));
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Standard));
        Assert.That(values, Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed));
        Assert.That(values, Has.Length.EqualTo(3));
    }

    [Test]
    public void SyncOutcomeTrackingLevel_None_IsZero()
    {
        // None should be 0 so it can be used as a "disabled" sentinel value
        Assert.That((int)ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None, Is.EqualTo(0));
    }

    #endregion
}
