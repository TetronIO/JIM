using System;
using System.Linq;
using System.Threading.Tasks;
using JIM.Data;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Integration tests for ActivitiesRepository.GetActivityRunProfileExecutionStatsAsync()
/// and GetActivityRunProfileExecutionItemHeadersAsync(). These verify the dual-path
/// database query logic: outcome-based stats derivation vs legacy RPEI ObjectChangeType fallback.
/// Uses EF Core InMemory database to exercise real EF queries without requiring PostgreSQL.
/// </summary>
[TestFixture]
public class ActivityOutcomeStatsIntegrationTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        // Set environment variables needed by JIM (even though they won't be used with in-memory DB)
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseHostname, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseName, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseUsername, "dummy");
        Environment.SetEnvironmentVariable(Constants.Config.DatabasePassword, "dummy");

        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .EnableSensitiveDataLogging()
            .Options;

        _dbContext = new JimDbContext(options);
        _repository = new PostgresDataRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext?.Dispose();
    }

    #region Helper Methods

    private async Task<Activity> CreateActivityAsync(int objectsProcessed = 0, int pendingExportsConfirmed = 0)
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Status = ActivityStatus.Complete,
            Created = DateTime.UtcNow,
            ObjectsProcessed = objectsProcessed,
            PendingExportsConfirmed = pendingExportsConfirmed
        };
        _dbContext.Activities.Add(activity);
        await _dbContext.SaveChangesAsync();
        return activity;
    }

    private async Task<ActivityRunProfileExecutionItem> CreateRpeiAsync(
        Activity activity,
        ObjectChangeType changeType,
        ActivityRunProfileExecutionItemErrorType? errorType = null,
        NoChangeReason? noChangeReason = null,
        string? outcomeSummary = null)
    {
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activity.Id,
            ObjectChangeType = changeType,
            ErrorType = errorType,
            NoChangeReason = noChangeReason,
            OutcomeSummary = outcomeSummary
        };
        _dbContext.ActivityRunProfileExecutionItems.Add(rpei);
        await _dbContext.SaveChangesAsync();
        return rpei;
    }

    private async Task<ActivityRunProfileExecutionItemSyncOutcome> CreateOutcomeAsync(
        ActivityRunProfileExecutionItem rpei,
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType,
        ActivityRunProfileExecutionItemSyncOutcome? parent = null,
        int ordinal = 0,
        int? detailCount = null,
        string? targetEntityDescription = null)
    {
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            ActivityRunProfileExecutionItemId = rpei.Id,
            OutcomeType = outcomeType,
            ParentSyncOutcomeId = parent?.Id,
            Ordinal = ordinal,
            DetailCount = detailCount,
            TargetEntityDescription = targetEntityDescription
        };
        _dbContext.ActivityRunProfileExecutionItemSyncOutcomes.Add(outcome);
        await _dbContext.SaveChangesAsync();
        return outcome;
    }

    #endregion

    #region Test 1: Import outcome-based stats

    [Test]
    public async Task GetStats_ImportOutcomeBased_DerivesCsoStatsFromOutcomesAsync()
    {
        // Arrange: Activity with 10 objects processed
        var activity = await CreateActivityAsync(objectsProcessed: 10);

        // 3 Added RPEIs with CsoAdded outcomes
        for (var i = 0; i < 3; i++)
        {
            var rpei = await CreateRpeiAsync(activity, ObjectChangeType.Added);
            await CreateOutcomeAsync(rpei, ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded, detailCount: 15);
        }

        // 2 Updated RPEIs with CsoUpdated outcomes
        for (var i = 0; i < 2; i++)
        {
            var rpei = await CreateRpeiAsync(activity, ObjectChangeType.Updated);
            await CreateOutcomeAsync(rpei, ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, detailCount: 5);
        }

        // 1 Deleted RPEI with DeletionDetected outcome
        var deletedRpei = await CreateRpeiAsync(activity, ObjectChangeType.Deleted);
        await CreateOutcomeAsync(deletedRpei, ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected);

        // 1 Updated RPEI with CsoUpdated + ExportConfirmed child (confirming import)
        var confirmedRpei = await CreateRpeiAsync(activity, ObjectChangeType.Updated);
        var confirmedRoot = await CreateOutcomeAsync(confirmedRpei, ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated);
        await CreateOutcomeAsync(confirmedRpei, ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed, parent: confirmedRoot, ordinal: 1);

        // 1 Updated RPEI with CsoUpdated + ExportFailed child
        var failedRpei = await CreateRpeiAsync(activity, ObjectChangeType.Updated);
        var failedRoot = await CreateOutcomeAsync(failedRpei, ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated);
        await CreateOutcomeAsync(failedRpei, ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed, parent: failedRoot, ordinal: 1);

        // Act
        var stats = await _repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);

        // Assert
        Assert.That(stats.TotalCsoAdds, Is.EqualTo(3));
        Assert.That(stats.TotalCsoUpdates, Is.EqualTo(4)); // 2 + 1 confirmed + 1 failed
        Assert.That(stats.TotalCsoDeletes, Is.EqualTo(1)); // DeletionDetected counts towards deletes
        Assert.That(stats.TotalObjectChangeCount, Is.EqualTo(8)); // Total RPEIs
        Assert.That(stats.TotalObjectsProcessed, Is.EqualTo(10));
        Assert.That(stats.TotalUnchanged, Is.EqualTo(2)); // 10 - 8

        // All sync/export stats should be zero
        Assert.That(stats.TotalProjections, Is.EqualTo(0));
        Assert.That(stats.TotalJoins, Is.EqualTo(0));
        Assert.That(stats.TotalExported, Is.EqualTo(0));
        Assert.That(stats.TotalDeprovisioned, Is.EqualTo(0));
    }

    #endregion

    #region Test 2: Legacy fallback (no outcomes)

    [Test]
    public async Task GetStats_LegacyFallback_DerivesFromRpeiObjectChangeTypeAsync()
    {
        // Arrange: Activity with RPEIs but NO outcomes — triggers legacy fallback path
        var activity = await CreateActivityAsync(objectsProcessed: 10);

        // Import RPEIs
        for (var i = 0; i < 3; i++)
            await CreateRpeiAsync(activity, ObjectChangeType.Added);
        for (var i = 0; i < 2; i++)
            await CreateRpeiAsync(activity, ObjectChangeType.Updated);
        await CreateRpeiAsync(activity, ObjectChangeType.Deleted);

        // Sync RPEIs
        await CreateRpeiAsync(activity, ObjectChangeType.Projected);
        await CreateRpeiAsync(activity, ObjectChangeType.Joined);

        // Act
        var stats = await _repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);

        // Assert: stats derived from ObjectChangeType counts
        Assert.That(stats.TotalCsoAdds, Is.EqualTo(3));
        Assert.That(stats.TotalCsoUpdates, Is.EqualTo(2));
        Assert.That(stats.TotalCsoDeletes, Is.EqualTo(1));
        Assert.That(stats.TotalProjections, Is.EqualTo(1));
        Assert.That(stats.TotalJoins, Is.EqualTo(1));
        Assert.That(stats.TotalObjectChangeCount, Is.EqualTo(8));

        // Outcome-only concepts are zero in legacy mode
        Assert.That(stats.TotalProvisioned, Is.EqualTo(0));
        Assert.That(stats.TotalPendingExports, Is.EqualTo(0));
    }

    #endregion

    #region Test 3: Sync nested tree

    [Test]
    public async Task GetStats_SyncNestedTree_CountsAllOutcomeNodesAsync()
    {
        // Arrange: Projected→AttributeFlow→Provisioned→PendingExportCreated
        //          Joined→AttributeFlow→PendingExportCreated
        var activity = await CreateActivityAsync();

        // RPEI 1: Projected tree
        var rpei1 = await CreateRpeiAsync(activity, ObjectChangeType.Projected);
        var projected = await CreateOutcomeAsync(rpei1, ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        var af1 = await CreateOutcomeAsync(rpei1, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, parent: projected, detailCount: 12);
        var prov = await CreateOutcomeAsync(rpei1, ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, parent: af1, targetEntityDescription: "AD");
        await CreateOutcomeAsync(rpei1, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, parent: prov, ordinal: 0, targetEntityDescription: "System A");
        await CreateOutcomeAsync(rpei1, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, parent: af1, ordinal: 1, targetEntityDescription: "System B");

        // RPEI 2: Joined tree
        var rpei2 = await CreateRpeiAsync(activity, ObjectChangeType.Joined);
        var joined = await CreateOutcomeAsync(rpei2, ActivityRunProfileExecutionItemSyncOutcomeType.Joined);
        var af2 = await CreateOutcomeAsync(rpei2, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, parent: joined, detailCount: 8);
        await CreateOutcomeAsync(rpei2, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, parent: af2, targetEntityDescription: "System A");

        // Act
        var stats = await _repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);

        // Assert
        Assert.That(stats.TotalProjections, Is.EqualTo(1));
        Assert.That(stats.TotalJoins, Is.EqualTo(1));
        Assert.That(stats.TotalAttributeFlows, Is.EqualTo(2)); // One per RPEI tree
        Assert.That(stats.TotalProvisioned, Is.EqualTo(1));
        Assert.That(stats.TotalPendingExports, Is.EqualTo(3)); // 3 PendingExportCreated outcomes
        Assert.That(stats.TotalObjectChangeCount, Is.EqualTo(2)); // 2 RPEIs
    }

    #endregion

    #region Test 4: Disconnection scenarios

    [Test]
    public async Task GetStats_DisconnectionScenarios_CountsAllDisconnectionTypesAsync()
    {
        // Arrange
        var activity = await CreateActivityAsync();

        // RPEI 1: Disconnected + CsoDeleted siblings
        var rpei1 = await CreateRpeiAsync(activity, ObjectChangeType.Disconnected);
        await CreateOutcomeAsync(rpei1, ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected, ordinal: 0);
        await CreateOutcomeAsync(rpei1, ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted, ordinal: 1);

        // RPEI 2: DisconnectedOutOfScope
        var rpei2 = await CreateRpeiAsync(activity, ObjectChangeType.DisconnectedOutOfScope);
        await CreateOutcomeAsync(rpei2, ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope);

        // RPEI 3: Disconnected → MvoDeleted → Deprovisioned
        var rpei3 = await CreateRpeiAsync(activity, ObjectChangeType.Disconnected);
        var disc3 = await CreateOutcomeAsync(rpei3, ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected);
        var mvoDel = await CreateOutcomeAsync(rpei3, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted, parent: disc3);
        await CreateOutcomeAsync(rpei3, ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned, parent: mvoDel);

        // Act
        var stats = await _repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);

        // Assert
        Assert.That(stats.TotalDisconnections, Is.EqualTo(2)); // 2 Disconnected outcomes
        Assert.That(stats.TotalDisconnectedOutOfScope, Is.EqualTo(1));
        Assert.That(stats.TotalCsoDeletes, Is.EqualTo(1)); // CsoDeleted counted in deletes
        Assert.That(stats.TotalDeprovisioned, Is.EqualTo(1));
    }

    #endregion

    #region Test 5: Drift correction

    [Test]
    public async Task GetStats_DriftCorrection_CountedFromOutcomesAsync()
    {
        // Arrange: 2 drift corrections, each with a PendingExportCreated child
        var activity = await CreateActivityAsync();

        for (var i = 0; i < 2; i++)
        {
            var rpei = await CreateRpeiAsync(activity, ObjectChangeType.DriftCorrection);
            var drift = await CreateOutcomeAsync(rpei, ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection);
            await CreateOutcomeAsync(rpei, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, parent: drift);
        }

        // Act
        var stats = await _repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);

        // Assert
        Assert.That(stats.TotalDriftCorrections, Is.EqualTo(2));
        Assert.That(stats.TotalPendingExports, Is.EqualTo(2));
    }

    #endregion

    #region Test 6: Export outcomes

    [Test]
    public async Task GetStats_ExportOutcomes_CountsExportedAndDeprovisionedAsync()
    {
        // Arrange
        var activity = await CreateActivityAsync();

        for (var i = 0; i < 3; i++)
        {
            var rpei = await CreateRpeiAsync(activity, ObjectChangeType.Exported);
            await CreateOutcomeAsync(rpei, ActivityRunProfileExecutionItemSyncOutcomeType.Exported, detailCount: 8);
        }

        var deprovRpei = await CreateRpeiAsync(activity, ObjectChangeType.Deprovisioned);
        await CreateOutcomeAsync(deprovRpei, ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned);

        // Act
        var stats = await _repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);

        // Assert
        Assert.That(stats.TotalExported, Is.EqualTo(3));
        Assert.That(stats.TotalDeprovisioned, Is.EqualTo(1));

        // Import/sync stats should be zero
        Assert.That(stats.TotalCsoAdds, Is.EqualTo(0));
        Assert.That(stats.TotalProjections, Is.EqualTo(0));
    }

    #endregion

    #region Test 7: Multi-system provisioning

    [Test]
    public async Task GetStats_MultiSystemProvisioning_CountsEachTargetSystemAsync()
    {
        // Arrange: 1 RPEI with Projected → AttributeFlow → 3×(Provisioned → PendingExportCreated)
        var activity = await CreateActivityAsync();

        var rpei = await CreateRpeiAsync(activity, ObjectChangeType.Projected);
        var projected = await CreateOutcomeAsync(rpei, ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        var af = await CreateOutcomeAsync(rpei, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, parent: projected, detailCount: 10);

        var systems = new[] { "AD", "LDAP", "SCIM" };
        for (var i = 0; i < systems.Length; i++)
        {
            var prov = await CreateOutcomeAsync(rpei, ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
                parent: af, ordinal: i, targetEntityDescription: systems[i]);
            await CreateOutcomeAsync(rpei, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
                parent: prov, targetEntityDescription: systems[i]);
        }

        // Act
        var stats = await _repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);

        // Assert: each target system counts separately
        Assert.That(stats.TotalProjections, Is.EqualTo(1));
        Assert.That(stats.TotalAttributeFlows, Is.EqualTo(1));
        Assert.That(stats.TotalProvisioned, Is.EqualTo(3)); // One per target system
        Assert.That(stats.TotalPendingExports, Is.EqualTo(3)); // One PendingExportCreated per system
        Assert.That(stats.TotalObjectChangeCount, Is.EqualTo(1)); // Still just 1 RPEI
    }

    #endregion

    #region Test 8: RPEI-only types alongside outcomes

    [Test]
    public async Task GetStats_RpeiOnlyTypes_AlwaysDerivedFromRpeisAsync()
    {
        // Arrange: mix of RPEIs with outcomes (triggers outcome path) and RPEI-only types
        var activity = await CreateActivityAsync(objectsProcessed: 20);

        // 1 Projected RPEI with outcome (triggers hasOutcomes = true)
        var projRpei = await CreateRpeiAsync(activity, ObjectChangeType.Projected);
        await CreateOutcomeAsync(projRpei, ActivityRunProfileExecutionItemSyncOutcomeType.Projected);

        // RPEI-only types — no outcome equivalent, always counted from RPEIs
        for (var i = 0; i < 2; i++)
            await CreateRpeiAsync(activity, ObjectChangeType.OutOfScopeRetainJoin);

        await CreateRpeiAsync(activity, ObjectChangeType.Created);

        // NoChange RPEIs with reasons
        for (var i = 0; i < 3; i++)
            await CreateRpeiAsync(activity, ObjectChangeType.NoChange, noChangeReason: NoChangeReason.MvoNoAttributeChanges);
        for (var i = 0; i < 2; i++)
            await CreateRpeiAsync(activity, ObjectChangeType.NoChange, noChangeReason: NoChangeReason.CsoAlreadyCurrent);

        // PendingExport RPEI (legacy type — outcome path uses PendingExportCreated outcomes instead)
        await CreateRpeiAsync(activity, ObjectChangeType.PendingExport);

        // Act
        var stats = await _repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);

        // Assert: outcome-derived stat
        Assert.That(stats.TotalProjections, Is.EqualTo(1));

        // RPEI-only types always from RPEIs
        Assert.That(stats.TotalOutOfScopeRetainJoin, Is.EqualTo(2));
        Assert.That(stats.TotalCreated, Is.EqualTo(1));
        Assert.That(stats.TotalMvoNoAttributeChanges, Is.EqualTo(3));
        Assert.That(stats.TotalCsoAlreadyCurrent, Is.EqualTo(2));

        // PendingExport: outcome path uses PendingExportCreated outcomes (0 here), not RPEI ObjectChangeType
        Assert.That(stats.TotalPendingExports, Is.EqualTo(0));

        Assert.That(stats.TotalObjectChangeCount, Is.EqualTo(10)); // All 10 RPEIs
        Assert.That(stats.TotalObjectsProcessed, Is.EqualTo(20));
    }

    #endregion

    #region Test 9: Error counting per-RPEI

    [Test]
    public async Task GetStats_ErrorCounting_AlwaysPerRpeiRegardlessOfOutcomesAsync()
    {
        // Arrange: RPEIs with various error types, all with outcomes
        var activity = await CreateActivityAsync();

        // Errors that should be counted
        var rpei1 = await CreateRpeiAsync(activity, ObjectChangeType.Projected,
            errorType: ActivityRunProfileExecutionItemErrorType.UnhandledError);
        await CreateOutcomeAsync(rpei1, ActivityRunProfileExecutionItemSyncOutcomeType.Projected);

        var rpei2 = await CreateRpeiAsync(activity, ObjectChangeType.Joined,
            errorType: ActivityRunProfileExecutionItemErrorType.AmbiguousMatch);
        await CreateOutcomeAsync(rpei2, ActivityRunProfileExecutionItemSyncOutcomeType.Joined);

        var rpei3 = await CreateRpeiAsync(activity, ObjectChangeType.Updated,
            errorType: ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed);
        await CreateOutcomeAsync(rpei3, ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated);

        var rpei4 = await CreateRpeiAsync(activity, ObjectChangeType.Updated,
            errorType: ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed);
        await CreateOutcomeAsync(rpei4, ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated);

        // Non-errors (NotSet and null) — should NOT be counted
        var rpei5 = await CreateRpeiAsync(activity, ObjectChangeType.Added,
            errorType: ActivityRunProfileExecutionItemErrorType.NotSet);
        await CreateOutcomeAsync(rpei5, ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded);

        var rpei6 = await CreateRpeiAsync(activity, ObjectChangeType.Added, errorType: null);
        await CreateOutcomeAsync(rpei6, ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded);

        // Act
        var stats = await _repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);

        // Assert: errors counted per-RPEI regardless of outcomes
        Assert.That(stats.TotalObjectErrors, Is.EqualTo(4));
        Assert.That(stats.ErrorTypeCounts[ActivityRunProfileExecutionItemErrorType.UnhandledError], Is.EqualTo(1));
        Assert.That(stats.ErrorTypeCounts[ActivityRunProfileExecutionItemErrorType.AmbiguousMatch], Is.EqualTo(1));
        Assert.That(stats.ErrorTypeCounts[ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed], Is.EqualTo(1));
        Assert.That(stats.ErrorTypeCounts[ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed], Is.EqualTo(1));

        // Pending export reconciliation stats derived from error types
        Assert.That(stats.TotalPendingExportsRetrying, Is.EqualTo(1));
        Assert.That(stats.TotalPendingExportsFailed, Is.EqualTo(1));

        // Outcome-derived stats should still be correct
        Assert.That(stats.TotalProjections, Is.EqualTo(1));
        Assert.That(stats.TotalJoins, Is.EqualTo(1));
        Assert.That(stats.TotalCsoAdds, Is.EqualTo(2));
        Assert.That(stats.TotalCsoUpdates, Is.EqualTo(2));
    }

    #endregion

    #region Test 10: OutcomeSummary on item headers

    [Test]
    public async Task GetItemHeaders_OutcomeSummary_AppearsOnHeadersAsync()
    {
        // Arrange: 2 RPEIs — one with OutcomeSummary, one without (legacy)
        var activity = await CreateActivityAsync();

        var rpei1 = await CreateRpeiAsync(activity, ObjectChangeType.Projected,
            outcomeSummary: "Projected:1,AttributeFlow:12,PendingExportCreated:2");
        await CreateOutcomeAsync(rpei1, ActivityRunProfileExecutionItemSyncOutcomeType.Projected);

        await CreateRpeiAsync(activity, ObjectChangeType.Added, outcomeSummary: null);

        // Act
        var result = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersAsync(
            activity.Id, page: 1, pageSize: 20);

        // Assert
        Assert.That(result.TotalResults, Is.EqualTo(2));

        var itemWithSummary = result.Results.Single(r => r.ObjectChangeType == ObjectChangeType.Projected);
        Assert.That(itemWithSummary.OutcomeSummary, Is.EqualTo("Projected:1,AttributeFlow:12,PendingExportCreated:2"));

        var itemWithoutSummary = result.Results.Single(r => r.ObjectChangeType == ObjectChangeType.Added);
        Assert.That(itemWithoutSummary.OutcomeSummary, Is.Null);
    }

    #endregion
}
