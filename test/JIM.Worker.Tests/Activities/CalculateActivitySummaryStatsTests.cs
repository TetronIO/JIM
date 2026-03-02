using JIM.Models.Activities;
using JIM.Models.Enums;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Tests for the Worker.CalculateActivitySummaryStats internal static method.
/// This method aggregates RunProfileExecutionItem data into granular summary stat fields
/// on the Activity for display in activity list views.
/// </summary>
[TestFixture]
public class CalculateActivitySummaryStatsTests
{
    #region Helper Methods

    /// <summary>
    /// Creates a fresh Activity with an empty RPEI list.
    /// </summary>
    private static Activity CreateActivity()
    {
        return new Activity
        {
            Id = Guid.NewGuid(),
            Created = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates an ActivityRunProfileExecutionItem with the specified change type,
    /// optionally setting error type and attribute flow count.
    /// </summary>
    private static ActivityRunProfileExecutionItem CreateRpei(
        ObjectChangeType changeType,
        ActivityRunProfileExecutionItemErrorType? errorType = null,
        int? attributeFlowCount = null)
    {
        return new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ObjectChangeType = changeType,
            ErrorType = errorType,
            AttributeFlowCount = attributeFlowCount
        };
    }

    /// <summary>
    /// Creates a sync outcome node with the specified type and optional detail count.
    /// </summary>
    private static ActivityRunProfileExecutionItemSyncOutcome CreateOutcome(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType,
        int? detailCount = null,
        string? targetEntityDescription = null)
    {
        return new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            OutcomeType = outcomeType,
            DetailCount = detailCount,
            TargetEntityDescription = targetEntityDescription
        };
    }

    /// <summary>
    /// Creates an RPEI with the specified change type and sync outcomes attached.
    /// </summary>
    private static ActivityRunProfileExecutionItem CreateRpeiWithOutcomes(
        ObjectChangeType changeType,
        ActivityRunProfileExecutionItemSyncOutcome rootOutcome,
        ActivityRunProfileExecutionItemErrorType? errorType = null)
    {
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ObjectChangeType = changeType,
            ErrorType = errorType
        };
        rootOutcome.ActivityRunProfileExecutionItemId = rpei.Id;
        rpei.SyncOutcomes.Add(rootOutcome);
        return rpei;
    }

    /// <summary>
    /// Adds RPEIs to an activity and then invokes CalculateActivitySummaryStats.
    /// </summary>
    private static void AddRpeisAndCalculate(Activity activity, params ActivityRunProfileExecutionItem[] rpeis)
    {
        foreach (var rpei in rpeis)
        {
            rpei.Activity = activity;
            rpei.ActivityId = activity.Id;
            activity.RunProfileExecutionItems.Add(rpei);
        }

        Worker.CalculateActivitySummaryStats(activity);
    }

    #endregion

    #region Empty RPEIs

    [Test]
    public void CalculateActivitySummaryStats_EmptyRpeis_AllStatsZero()
    {
        // Arrange
        var activity = CreateActivity();

        // Act
        Worker.CalculateActivitySummaryStats(activity);

        // Assert - Import stats
        Assert.That(activity.TotalAdded, Is.EqualTo(0));
        Assert.That(activity.TotalUpdated, Is.EqualTo(0));
        Assert.That(activity.TotalDeleted, Is.EqualTo(0));

        // Assert - Sync stats
        Assert.That(activity.TotalProjected, Is.EqualTo(0));
        Assert.That(activity.TotalJoined, Is.EqualTo(0));
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(0));
        Assert.That(activity.TotalDisconnected, Is.EqualTo(0));
        Assert.That(activity.TotalDisconnectedOutOfScope, Is.EqualTo(0));
        Assert.That(activity.TotalOutOfScopeRetainJoin, Is.EqualTo(0));
        Assert.That(activity.TotalDriftCorrections, Is.EqualTo(0));

        // Assert - Export stats
        Assert.That(activity.TotalExported, Is.EqualTo(0));
        Assert.That(activity.TotalDeprovisioned, Is.EqualTo(0));

        // Assert - Direct creation stats
        Assert.That(activity.TotalCreated, Is.EqualTo(0));

        // Assert - Pending export stats
        Assert.That(activity.TotalPendingExports, Is.EqualTo(0));

        // Assert - Error stats
        Assert.That(activity.TotalErrors, Is.EqualTo(0));
    }

    #endregion

    #region Import Stats

    [Test]
    public void CalculateActivitySummaryStats_ImportRun_CountsAddedUpdatedDeleted()
    {
        // Arrange
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Added),
            CreateRpei(ObjectChangeType.Added),
            CreateRpei(ObjectChangeType.Added),
            CreateRpei(ObjectChangeType.Updated),
            CreateRpei(ObjectChangeType.Updated),
            CreateRpei(ObjectChangeType.Deleted));

        // Assert
        Assert.That(activity.TotalAdded, Is.EqualTo(3));
        Assert.That(activity.TotalUpdated, Is.EqualTo(2));
        Assert.That(activity.TotalDeleted, Is.EqualTo(1));

        // Verify other categories remain zero
        Assert.That(activity.TotalProjected, Is.EqualTo(0));
        Assert.That(activity.TotalJoined, Is.EqualTo(0));
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(0));
        Assert.That(activity.TotalErrors, Is.EqualTo(0));
    }

    #endregion

    #region Sync Stats

    [Test]
    public void CalculateActivitySummaryStats_SyncRun_CountsProjectedJoinedFlowsDisconnected()
    {
        // Arrange
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Projected),
            CreateRpei(ObjectChangeType.Projected),
            CreateRpei(ObjectChangeType.Joined),
            CreateRpei(ObjectChangeType.Joined),
            CreateRpei(ObjectChangeType.Joined),
            CreateRpei(ObjectChangeType.AttributeFlow),
            CreateRpei(ObjectChangeType.AttributeFlow),
            CreateRpei(ObjectChangeType.AttributeFlow),
            CreateRpei(ObjectChangeType.AttributeFlow),
            CreateRpei(ObjectChangeType.Disconnected));

        // Assert
        Assert.That(activity.TotalProjected, Is.EqualTo(2));
        Assert.That(activity.TotalJoined, Is.EqualTo(3));
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(4));
        Assert.That(activity.TotalDisconnected, Is.EqualTo(1));

        // Verify import and export categories remain zero
        Assert.That(activity.TotalAdded, Is.EqualTo(0));
        Assert.That(activity.TotalUpdated, Is.EqualTo(0));
        Assert.That(activity.TotalDeleted, Is.EqualTo(0));
        Assert.That(activity.TotalExported, Is.EqualTo(0));
        Assert.That(activity.TotalDeprovisioned, Is.EqualTo(0));
    }

    [Test]
    public void CalculateActivitySummaryStats_SyncRun_AbsorbedAttributeFlows_NotDoubleCountedWithJoins()
    {
        // Arrange - Joined RPEIs with attribute flows should NOT be counted in TotalAttributeFlows
        // because joins inherently include attribute flow and are already counted in TotalJoined
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Joined, attributeFlowCount: 3),
            CreateRpei(ObjectChangeType.Joined, attributeFlowCount: 2));

        // Assert
        Assert.That(activity.TotalJoined, Is.EqualTo(2));
        // Attribute flows absorbed into joins are not double-counted
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(0));
    }

    #endregion

    #region Export Stats

    [Test]
    public void CalculateActivitySummaryStats_ExportRun_CountsExportedDeprovisioned()
    {
        // Arrange
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Exported),
            CreateRpei(ObjectChangeType.Exported),
            CreateRpei(ObjectChangeType.Exported),
            CreateRpei(ObjectChangeType.Exported),
            CreateRpei(ObjectChangeType.Exported),
            CreateRpei(ObjectChangeType.Deprovisioned));

        // Assert
        Assert.That(activity.TotalExported, Is.EqualTo(5));
        Assert.That(activity.TotalDeprovisioned, Is.EqualTo(1));

        // Verify import and sync categories remain zero
        Assert.That(activity.TotalAdded, Is.EqualTo(0));
        Assert.That(activity.TotalProjected, Is.EqualTo(0));
        Assert.That(activity.TotalJoined, Is.EqualTo(0));
    }

    #endregion

    #region Error Stats

    [Test]
    public void CalculateActivitySummaryStats_Errors_CountedCorrectly()
    {
        // Arrange - RPEIs with various error types should all be counted, except NotSet
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            // Successful items (no error or NotSet error)
            CreateRpei(ObjectChangeType.Added),
            CreateRpei(ObjectChangeType.Added, errorType: ActivityRunProfileExecutionItemErrorType.NotSet),
            // Error items - each distinct error type
            CreateRpei(ObjectChangeType.Added, errorType: ActivityRunProfileExecutionItemErrorType.AmbiguousMatch),
            CreateRpei(ObjectChangeType.Updated, errorType: ActivityRunProfileExecutionItemErrorType.UnhandledError),
            CreateRpei(ObjectChangeType.Joined, errorType: ActivityRunProfileExecutionItemErrorType.UnresolvedReference),
            CreateRpei(ObjectChangeType.Exported, errorType: ActivityRunProfileExecutionItemErrorType.DuplicateObject));

        // Assert
        Assert.That(activity.TotalErrors, Is.EqualTo(4));

        // Verify the non-error counts are still correct
        Assert.That(activity.TotalAdded, Is.EqualTo(3), "All 3 Added RPEIs counted regardless of error status.");
        Assert.That(activity.TotalUpdated, Is.EqualTo(1));
        Assert.That(activity.TotalJoined, Is.EqualTo(1));
        Assert.That(activity.TotalExported, Is.EqualTo(1));
    }

    [Test]
    public void CalculateActivitySummaryStats_Errors_NullErrorType_NotCountedAsError()
    {
        // Arrange - RPEI with null ErrorType should not be counted as an error
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Added, errorType: null));

        // Assert
        Assert.That(activity.TotalErrors, Is.EqualTo(0));
        Assert.That(activity.TotalAdded, Is.EqualTo(1));
    }

    #endregion

    #region Direct Creation Stats

    [Test]
    public void CalculateActivitySummaryStats_DirectCreation_CountedInTotalCreated()
    {
        // Arrange
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Created),
            CreateRpei(ObjectChangeType.Created),
            CreateRpei(ObjectChangeType.Created));

        // Assert
        Assert.That(activity.TotalCreated, Is.EqualTo(3));

        // Verify that Created is not confused with Added (import) or Projected (sync)
        Assert.That(activity.TotalAdded, Is.EqualTo(0));
        Assert.That(activity.TotalProjected, Is.EqualTo(0));
    }

    #endregion

    #region Edge Case Types

    [Test]
    public void CalculateActivitySummaryStats_AllEdgeCaseTypes_CountedCorrectly()
    {
        // Arrange - Each edge case type should map to its correct field
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.DriftCorrection),
            CreateRpei(ObjectChangeType.DriftCorrection),
            CreateRpei(ObjectChangeType.DisconnectedOutOfScope),
            CreateRpei(ObjectChangeType.DisconnectedOutOfScope),
            CreateRpei(ObjectChangeType.DisconnectedOutOfScope),
            CreateRpei(ObjectChangeType.OutOfScopeRetainJoin),
            CreateRpei(ObjectChangeType.PendingExport),
            CreateRpei(ObjectChangeType.PendingExport),
            CreateRpei(ObjectChangeType.PendingExport),
            CreateRpei(ObjectChangeType.PendingExport));

        // Assert
        Assert.That(activity.TotalDriftCorrections, Is.EqualTo(2));
        Assert.That(activity.TotalDisconnectedOutOfScope, Is.EqualTo(3));
        Assert.That(activity.TotalOutOfScopeRetainJoin, Is.EqualTo(1));
        Assert.That(activity.TotalPendingExports, Is.EqualTo(4));

        // Verify that these are not conflated with basic Disconnected
        Assert.That(activity.TotalDisconnected, Is.EqualTo(0));
    }

    #endregion

    #region Mixed Attribute Flow Counting

    [Test]
    public void CalculateActivitySummaryStats_MixedAbsorbedAndStandaloneFlows_OnlyStandaloneCounted()
    {
        // Arrange - Only standalone AttributeFlow RPEIs contribute to TotalAttributeFlows.
        // Attribute flows absorbed into Joined/Projected/Disconnected RPEIs are NOT counted
        // because those change types are already counted in their own stats.
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            // 3 standalone attribute flow RPEIs (3 objects)
            CreateRpei(ObjectChangeType.AttributeFlow),
            CreateRpei(ObjectChangeType.AttributeFlow),
            CreateRpei(ObjectChangeType.AttributeFlow),
            // Joined/Projected/Disconnected RPEIs with absorbed flows — NOT counted in TotalAttributeFlows
            CreateRpei(ObjectChangeType.Joined, attributeFlowCount: 4),
            CreateRpei(ObjectChangeType.Projected, attributeFlowCount: 2),
            CreateRpei(ObjectChangeType.Disconnected, attributeFlowCount: 1));

        // Assert
        // Only 3 standalone AttributeFlow RPEIs counted (absorbed flows excluded)
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(3));

        // Verify the primary types are still counted correctly
        Assert.That(activity.TotalJoined, Is.EqualTo(1));
        Assert.That(activity.TotalProjected, Is.EqualTo(1));
        Assert.That(activity.TotalDisconnected, Is.EqualTo(1));
    }

    [Test]
    public void CalculateActivitySummaryStats_CrossPageReferenceResolution_CountsStandaloneOnly()
    {
        // Arrange - Cross-page reference resolution creates standalone AttributeFlow RPEIs.
        // These are counted. Absorbed flows on Joined RPEIs are not.
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            // 2 standalone AttributeFlow RPEIs without count (within-page, 2 objects)
            CreateRpei(ObjectChangeType.AttributeFlow),
            CreateRpei(ObjectChangeType.AttributeFlow),
            // Cross-page resolution RPEIs — standalone AttributeFlow (2 objects)
            CreateRpei(ObjectChangeType.AttributeFlow, attributeFlowCount: 15),
            CreateRpei(ObjectChangeType.AttributeFlow, attributeFlowCount: 8),
            // A Joined RPEI with absorbed flows — NOT counted in TotalAttributeFlows
            CreateRpei(ObjectChangeType.Joined, attributeFlowCount: 3));

        // Assert
        // 4 standalone AttributeFlow RPEIs counted (joined excluded)
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(4));
        Assert.That(activity.TotalJoined, Is.EqualTo(1));
    }

    [Test]
    public void CalculateActivitySummaryStats_AbsorbedFlows_ZeroAttributeFlowCount_NotCounted()
    {
        // Arrange - RPEIs with AttributeFlowCount of 0 or null should not contribute
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Joined, attributeFlowCount: 0),
            CreateRpei(ObjectChangeType.Joined, attributeFlowCount: null),
            CreateRpei(ObjectChangeType.AttributeFlow));

        // Assert - Only the standalone AttributeFlow RPEI should be counted
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(1));
        Assert.That(activity.TotalJoined, Is.EqualTo(2));
    }

    #endregion

    #region Outcome-Based Stats — Import

    [Test]
    public void CalculateActivitySummaryStats_WithOutcomes_ImportRun_DerivedFromOutcomes()
    {
        // Arrange - When RPEIs have sync outcomes, stats are derived from outcome nodes
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpeiWithOutcomes(ObjectChangeType.Added,
                CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded)),
            CreateRpeiWithOutcomes(ObjectChangeType.Added,
                CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded)),
            CreateRpeiWithOutcomes(ObjectChangeType.Updated,
                CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated)),
            CreateRpeiWithOutcomes(ObjectChangeType.Deleted,
                CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted)));

        // Assert - Stats derived from outcome nodes
        Assert.That(activity.TotalAdded, Is.EqualTo(2));
        Assert.That(activity.TotalUpdated, Is.EqualTo(1));
        Assert.That(activity.TotalDeleted, Is.EqualTo(1));
    }

    #endregion

    #region Outcome-Based Stats — Sync

    [Test]
    public void CalculateActivitySummaryStats_WithOutcomes_SyncRun_CountsFromOutcomeTree()
    {
        // Arrange - Sync RPEI with a full outcome tree:
        // Projected -> AttributeFlow -> PendingExportCreated (AD) + PendingExportCreated (LDAP)
        var activity = CreateActivity();
        var attrFlowOutcome = CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, detailCount: 12);
        attrFlowOutcome.Children.Add(CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
        attrFlowOutcome.Children.Add(CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));

        var projectedOutcome = CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        projectedOutcome.Children.Add(attrFlowOutcome);

        AddRpeisAndCalculate(activity,
            CreateRpeiWithOutcomes(ObjectChangeType.Projected, projectedOutcome));

        // Assert - All outcome types counted including nested children
        Assert.That(activity.TotalProjected, Is.EqualTo(1));
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(1));
        Assert.That(activity.TotalPendingExports, Is.EqualTo(2));
    }

    [Test]
    public void CalculateActivitySummaryStats_WithOutcomes_MultipleRpeis_AggregatesAcrossAll()
    {
        // Arrange - Multiple RPEIs with outcomes, stats aggregate across all
        var activity = CreateActivity();

        // RPEI 1: Projected -> AttributeFlow -> PendingExportCreated x2
        var attrFlow1 = CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        attrFlow1.Children.Add(CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
        attrFlow1.Children.Add(CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
        var projected = CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        projected.Children.Add(attrFlow1);

        // RPEI 2: Joined -> AttributeFlow
        var attrFlow2 = CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        var joined = CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.Joined);
        joined.Children.Add(attrFlow2);

        // RPEI 3: Disconnected -> AttributeFlow
        var attrFlow3 = CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        var disconnected = CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected);
        disconnected.Children.Add(attrFlow3);

        AddRpeisAndCalculate(activity,
            CreateRpeiWithOutcomes(ObjectChangeType.Projected, projected),
            CreateRpeiWithOutcomes(ObjectChangeType.Joined, joined),
            CreateRpeiWithOutcomes(ObjectChangeType.Disconnected, disconnected));

        // Assert
        Assert.That(activity.TotalProjected, Is.EqualTo(1));
        Assert.That(activity.TotalJoined, Is.EqualTo(1));
        Assert.That(activity.TotalDisconnected, Is.EqualTo(1));
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(3), "Each RPEI has one AttributeFlow outcome");
        Assert.That(activity.TotalPendingExports, Is.EqualTo(2), "Only the projected RPEI has pending exports");
    }

    [Test]
    public void CalculateActivitySummaryStats_WithOutcomes_DisconnectedOutOfScope_CountedFromOutcomes()
    {
        // Arrange
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpeiWithOutcomes(ObjectChangeType.DisconnectedOutOfScope,
                CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope)),
            CreateRpeiWithOutcomes(ObjectChangeType.DisconnectedOutOfScope,
                CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope)));

        // Assert
        Assert.That(activity.TotalDisconnectedOutOfScope, Is.EqualTo(2));
        Assert.That(activity.TotalDisconnected, Is.EqualTo(0));
    }

    [Test]
    public void CalculateActivitySummaryStats_WithOutcomes_SourceDeletion_SingleRpeiWithBothOutcomes()
    {
        // Arrange - Source deletion produces a single Disconnected RPEI with both
        // Disconnected and CsoDeleted as sibling root outcomes (one-RPEI-per-CSO rule)
        var activity = CreateActivity();
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Disconnected
        };
        var disconnectedOutcome = CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected);
        disconnectedOutcome.ActivityRunProfileExecutionItemId = rpei.Id;
        rpei.SyncOutcomes.Add(disconnectedOutcome);
        var csoDeletedOutcome = CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted);
        csoDeletedOutcome.ActivityRunProfileExecutionItemId = rpei.Id;
        rpei.SyncOutcomes.Add(csoDeletedOutcome);

        AddRpeisAndCalculate(activity, rpei);

        // Assert - Disconnected counted from outcome, CsoDeleted counted from outcome
        Assert.That(activity.TotalDisconnected, Is.EqualTo(1));
        Assert.That(activity.TotalDeleted, Is.EqualTo(1), "CsoDeleted outcome should count towards TotalDeleted");
    }

    #endregion

    #region Outcome-Based Stats — Export

    [Test]
    public void CalculateActivitySummaryStats_WithOutcomes_ExportRun_CountsFromOutcomes()
    {
        // Arrange
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpeiWithOutcomes(ObjectChangeType.Exported,
                CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.Exported)),
            CreateRpeiWithOutcomes(ObjectChangeType.Exported,
                CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.Exported)),
            CreateRpeiWithOutcomes(ObjectChangeType.Deprovisioned,
                CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned)));

        // Assert
        Assert.That(activity.TotalExported, Is.EqualTo(2));
        Assert.That(activity.TotalDeprovisioned, Is.EqualTo(1));
    }

    #endregion

    #region Outcome-Based Stats — RPEI-Only Types

    [Test]
    public void CalculateActivitySummaryStats_WithOutcomes_RpeiOnlyTypes_StillCountedFromRpeis()
    {
        // Arrange - OutOfScopeRetainJoin, DriftCorrection, Created have no outcome equivalents
        // and must always be counted from RPEIs, even when other RPEIs have outcomes
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            // RPEI with outcomes (triggers outcome-based path)
            CreateRpeiWithOutcomes(ObjectChangeType.Projected,
                CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.Projected)),
            // RPEIs without outcome equivalents
            CreateRpei(ObjectChangeType.OutOfScopeRetainJoin),
            CreateRpei(ObjectChangeType.DriftCorrection),
            CreateRpei(ObjectChangeType.DriftCorrection),
            CreateRpei(ObjectChangeType.Created));

        // Assert - RPEI-only types always counted from RPEIs
        Assert.That(activity.TotalOutOfScopeRetainJoin, Is.EqualTo(1));
        Assert.That(activity.TotalDriftCorrections, Is.EqualTo(2));
        Assert.That(activity.TotalCreated, Is.EqualTo(1));

        // Assert - Outcome-based type derived from outcomes
        Assert.That(activity.TotalProjected, Is.EqualTo(1));
    }

    [Test]
    public void CalculateActivitySummaryStats_WithOutcomes_Errors_StillCountedFromRpeis()
    {
        // Arrange - Errors are always per-RPEI, not per-outcome
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpeiWithOutcomes(ObjectChangeType.Projected,
                CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.Projected),
                errorType: ActivityRunProfileExecutionItemErrorType.UnhandledError),
            CreateRpeiWithOutcomes(ObjectChangeType.Joined,
                CreateOutcome(ActivityRunProfileExecutionItemSyncOutcomeType.Joined)));

        // Assert
        Assert.That(activity.TotalErrors, Is.EqualTo(1));
        Assert.That(activity.TotalProjected, Is.EqualTo(1));
        Assert.That(activity.TotalJoined, Is.EqualTo(1));
    }

    #endregion

    #region Source Deletion Scenarios

    [Test]
    public void CalculateActivitySummaryStats_SourceDeletion_CountsDisconnectedOnly()
    {
        // Arrange - When a joined CSO is obsoleted during sync, a single Disconnected RPEI is produced
        // with both Disconnected and CsoDeleted outcomes (one-RPEI-per-CSO rule)
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Disconnected));

        // Assert - Only Disconnected stat is counted (CsoDeleted is an outcome, not a separate RPEI)
        Assert.That(activity.TotalDisconnected, Is.EqualTo(1));
        Assert.That(activity.TotalDeleted, Is.EqualTo(0));
    }

    [Test]
    public void CalculateActivitySummaryStats_SourceDeletionWithAttributeRemovals_CountsAbsorbedFlows()
    {
        // Arrange - Disconnected RPEI with attribute removals (contributed attributes recalled)
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Disconnected, attributeFlowCount: 3));

        // Assert
        Assert.That(activity.TotalDisconnected, Is.EqualTo(1));
        Assert.That(activity.TotalDeleted, Is.EqualTo(0));
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(0), "Attribute recalls on disconnection are not standalone attribute flows");
    }

    [Test]
    public void CalculateActivitySummaryStats_MultipleSourceDeletions_CountsAll()
    {
        // Arrange - Multiple objects deleted from source, each producing a single Disconnected RPEI
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Disconnected),
            CreateRpei(ObjectChangeType.Disconnected, attributeFlowCount: 2),
            CreateRpei(ObjectChangeType.Disconnected));

        // Assert
        Assert.That(activity.TotalDisconnected, Is.EqualTo(3));
        Assert.That(activity.TotalDeleted, Is.EqualTo(0));
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(0), "Attribute recalls on disconnection are not standalone attribute flows");
    }

    #endregion
}
