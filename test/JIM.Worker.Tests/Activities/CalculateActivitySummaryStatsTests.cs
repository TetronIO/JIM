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
        Assert.That(activity.TotalProvisioned, Is.EqualTo(0));
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
        Assert.That(activity.TotalProvisioned, Is.EqualTo(0));
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
        Assert.That(activity.TotalProvisioned, Is.EqualTo(0));
        Assert.That(activity.TotalExported, Is.EqualTo(0));
        Assert.That(activity.TotalDeprovisioned, Is.EqualTo(0));
    }

    [Test]
    public void CalculateActivitySummaryStats_SyncRun_AbsorbedAttributeFlows_IncludedInTotal()
    {
        // Arrange - A Joined RPEI that also caused 3 attribute flows (absorbed into the join)
        // should count as 1 Joined AND 3 AttributeFlows
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Joined, attributeFlowCount: 3),
            CreateRpei(ObjectChangeType.Joined, attributeFlowCount: 2));

        // Assert
        Assert.That(activity.TotalJoined, Is.EqualTo(2));
        // Absorbed flows: 3 + 2 = 5 (no standalone AttributeFlow RPEIs)
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(5));
    }

    #endregion

    #region Export Stats

    [Test]
    public void CalculateActivitySummaryStats_ExportRun_CountsProvisionedExportedDeprovisioned()
    {
        // Arrange
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Provisioned),
            CreateRpei(ObjectChangeType.Provisioned),
            CreateRpei(ObjectChangeType.Exported),
            CreateRpei(ObjectChangeType.Exported),
            CreateRpei(ObjectChangeType.Exported),
            CreateRpei(ObjectChangeType.Deprovisioned));

        // Assert
        Assert.That(activity.TotalProvisioned, Is.EqualTo(2));
        Assert.That(activity.TotalExported, Is.EqualTo(3));
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
    public void CalculateActivitySummaryStats_MixedAbsorbedAndStandaloneFlows_BothCounted()
    {
        // Arrange - Both standalone AttributeFlow RPEIs and absorbed flows
        // (via AttributeFlowCount on Joined/Projected RPEIs) contribute to TotalAttributeFlows
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            // 3 standalone attribute flow RPEIs
            CreateRpei(ObjectChangeType.AttributeFlow),
            CreateRpei(ObjectChangeType.AttributeFlow),
            CreateRpei(ObjectChangeType.AttributeFlow),
            // A Joined RPEI that also caused 4 absorbed attribute flows
            CreateRpei(ObjectChangeType.Joined, attributeFlowCount: 4),
            // A Projected RPEI that also caused 2 absorbed attribute flows
            CreateRpei(ObjectChangeType.Projected, attributeFlowCount: 2),
            // A Disconnected RPEI that also caused 1 absorbed attribute flow (attribute removal)
            CreateRpei(ObjectChangeType.Disconnected, attributeFlowCount: 1));

        // Assert
        // Standalone: 3, Absorbed: 4 + 2 + 1 = 7, Total = 10
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(10));

        // Verify the primary types are still counted correctly
        Assert.That(activity.TotalJoined, Is.EqualTo(1));
        Assert.That(activity.TotalProjected, Is.EqualTo(1));
        Assert.That(activity.TotalDisconnected, Is.EqualTo(1));
    }

    [Test]
    public void CalculateActivitySummaryStats_CrossPageReferenceResolution_UsesAttributeFlowCount()
    {
        // Arrange - Cross-page reference resolution creates AttributeFlow RPEIs with
        // AttributeFlowCount set to the actual number of reference changes resolved.
        // These should use the count (not just count as 1 each).
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            // 2 standalone AttributeFlow RPEIs without count (within-page, each counts as 1)
            CreateRpei(ObjectChangeType.AttributeFlow),
            CreateRpei(ObjectChangeType.AttributeFlow),
            // Cross-page resolution RPEIs with actual reference change counts
            CreateRpei(ObjectChangeType.AttributeFlow, attributeFlowCount: 15),
            CreateRpei(ObjectChangeType.AttributeFlow, attributeFlowCount: 8),
            // A Joined RPEI with absorbed flows
            CreateRpei(ObjectChangeType.Joined, attributeFlowCount: 3));

        // Assert
        // Standalone without count: 2, Cross-page with count: 15 + 8 = 23, Absorbed: 3, Total = 28
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(28));
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

    #region Source Deletion Scenarios

    [Test]
    public void CalculateActivitySummaryStats_SourceDeletion_CountsBothDisconnectedAndDeleted()
    {
        // Arrange - When a joined CSO is obsoleted during sync, two RPEIs are produced:
        // 1. Disconnected (CSO-MVO join broken)
        // 2. Deleted (CSO removed from staging)
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Disconnected),
            CreateRpei(ObjectChangeType.Deleted));

        // Assert - Both stats should be counted
        Assert.That(activity.TotalDisconnected, Is.EqualTo(1));
        Assert.That(activity.TotalDeleted, Is.EqualTo(1));
    }

    [Test]
    public void CalculateActivitySummaryStats_SourceDeletionWithAttributeRemovals_CountsAbsorbedFlows()
    {
        // Arrange - Disconnected RPEI with attribute removals (contributed attributes recalled)
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            CreateRpei(ObjectChangeType.Disconnected, attributeFlowCount: 3),
            CreateRpei(ObjectChangeType.Deleted));

        // Assert
        Assert.That(activity.TotalDisconnected, Is.EqualTo(1));
        Assert.That(activity.TotalDeleted, Is.EqualTo(1));
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(3), "Absorbed attribute removals should be counted");
    }

    [Test]
    public void CalculateActivitySummaryStats_MultipleSourceDeletions_CountsAllPairs()
    {
        // Arrange - Multiple objects deleted from source, each producing Disconnected + Deleted
        var activity = CreateActivity();
        AddRpeisAndCalculate(activity,
            // Object 1 deletion
            CreateRpei(ObjectChangeType.Disconnected),
            CreateRpei(ObjectChangeType.Deleted),
            // Object 2 deletion
            CreateRpei(ObjectChangeType.Disconnected, attributeFlowCount: 2),
            CreateRpei(ObjectChangeType.Deleted),
            // Object 3 deletion
            CreateRpei(ObjectChangeType.Disconnected),
            CreateRpei(ObjectChangeType.Deleted));

        // Assert
        Assert.That(activity.TotalDisconnected, Is.EqualTo(3));
        Assert.That(activity.TotalDeleted, Is.EqualTo(3));
        Assert.That(activity.TotalAttributeFlows, Is.EqualTo(2), "Only absorbed flows from one Disconnected RPEI");
    }

    #endregion
}
