// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;

namespace JIM.InMemoryData.Tests;

[TestFixture]
public class SyncRepositoryActivityRpeiTests
{
    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private Activity CreateActivity()
    {
        return new Activity
        {
            Id = Guid.NewGuid(),
            Status = ActivityStatus.InProgress,
            Created = DateTime.UtcNow,
            Executed = DateTime.UtcNow
        };
    }

    [Test]
    public async Task UpdateActivityAsync_StoresActivityAsync()
    {
        var activity = CreateActivity();
        _repo.SeedActivity(activity);

        activity.ObjectsProcessed = 10;
        await _repo.UpdateActivityAsync(activity);
        // No exception = success (we can't read it back directly, but it's stored)
    }

    [Test]
    public async Task UpdateActivityMessageAsync_SetsMessageAsync()
    {
        var activity = CreateActivity();
        _repo.SeedActivity(activity);

        await _repo.UpdateActivityMessageAsync(activity, "Processing page 3 of 5");
        Assert.That(activity.Message, Is.EqualTo("Processing page 3 of 5"));
    }

    [Test]
    public async Task FailActivityWithErrorAsync_String_SetsStatusAndMessageAsync()
    {
        var activity = CreateActivity();
        _repo.SeedActivity(activity);

        await _repo.FailActivityWithErrorAsync(activity, "Something went wrong");
        Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
        Assert.That(activity.ErrorMessage, Is.EqualTo("Something went wrong"));
    }

    [Test]
    public async Task FailActivityWithErrorAsync_Exception_SetsStatusAndDetailsAsync()
    {
        var activity = CreateActivity();
        _repo.SeedActivity(activity);

        try { throw new InvalidOperationException("Test exception"); }
        catch (Exception ex)
        {
            await _repo.FailActivityWithErrorAsync(activity, ex);
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(activity.ErrorMessage, Is.EqualTo("Test exception"));
            Assert.That(activity.ErrorStackTrace, Is.Not.Null);
        }
    }

    [Test]
    public async Task BulkInsertRpeisAsync_AddsRpeisAndReturnsFalseAsync()
    {
        var activityId = Guid.NewGuid();
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = activityId };

        // Returns false to tell the processor to keep RPEIs in the activity's
        // RunProfileExecutionItems collection (not the raw SQL path)
        var result = await _repo.BulkInsertRpeisAsync(new List<ActivityRunProfileExecutionItem> { rpei });
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task BulkInsertRpeisAsync_GeneratesIdWhenEmptyAsync()
    {
        var rpei = new ActivityRunProfileExecutionItem { ActivityId = Guid.NewGuid() };
        await _repo.BulkInsertRpeisAsync(new List<ActivityRunProfileExecutionItem> { rpei });
        Assert.That(rpei.Id, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task GetActivityRpeiErrorCountsAsync_CountsCorrectlyAsync()
    {
        var activityId = Guid.NewGuid();
        var rpeis = new List<ActivityRunProfileExecutionItem>
        {
            new() { Id = Guid.NewGuid(), ActivityId = activityId }, // No error
            new() { Id = Guid.NewGuid(), ActivityId = activityId, ErrorType = ActivityRunProfileExecutionItemErrorType.AmbiguousMatch },
            new() { Id = Guid.NewGuid(), ActivityId = activityId, ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError },
            new() { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() } // Different activity
        };

        await _repo.BulkInsertRpeisAsync(rpeis);

        var (totalWithErrors, totalRpeis, totalUnhandledErrors) =
            await _repo.GetActivityRpeiErrorCountsAsync(activityId);

        Assert.That(totalRpeis, Is.EqualTo(3));
        Assert.That(totalWithErrors, Is.EqualTo(2));
        Assert.That(totalUnhandledErrors, Is.EqualTo(1));
    }

    [Test]
    public async Task GetActivityRpeiErrorCountsAsync_NoRpeis_ReturnsZerosAsync()
    {
        var (totalWithErrors, totalRpeis, totalUnhandledErrors) =
            await _repo.GetActivityRpeiErrorCountsAsync(Guid.NewGuid());

        Assert.That(totalRpeis, Is.EqualTo(0));
        Assert.That(totalWithErrors, Is.EqualTo(0));
        Assert.That(totalUnhandledErrors, Is.EqualTo(0));
    }

    [Test]
    public void DetachRpeisFromChangeTracker_IsNoOp()
    {
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        Assert.DoesNotThrow(() => _repo.DetachRpeisFromChangeTracker(
            new List<ActivityRunProfileExecutionItem> { rpei }));
    }

    [Test]
    public async Task PersistRpeiCsoChangesAsync_IsNoOpAsync()
    {
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        await _repo.PersistRpeiCsoChangesAsync(new List<ActivityRunProfileExecutionItem> { rpei });
    }

    [Test]
    public async Task GetRpeisWithMvoChangeIdsForCrossPageMergeAsync_EmptyCsoIds_ReturnsEmptyAsync()
    {
        var result = await _repo.GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(
            Guid.NewGuid(), Array.Empty<Guid>());

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetRpeisWithMvoChangeIdsForCrossPageMergeAsync_RpeiWithoutMvoChange_HasNullExistingMvoChangeIdAsync()
    {
        var activityId = Guid.NewGuid();
        var csoId = Guid.NewGuid();
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.Projected,
            ConnectedSystemObjectId = csoId
        };
        await _repo.BulkInsertRpeisAsync(new List<ActivityRunProfileExecutionItem> { rpei });

        var result = await _repo.GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(
            activityId, new[] { csoId });

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Rpei.Id, Is.EqualTo(rpei.Id));
        Assert.That(result[0].Rpei.ConnectedSystemObjectId, Is.EqualTo(csoId));
        Assert.That(result[0].ExistingMvoChangeId, Is.Null);
    }

    [Test]
    public async Task GetRpeisWithMvoChangeIdsForCrossPageMergeAsync_RpeiWithMvoChange_MapsExistingMvoChangeIdAsync()
    {
        var activityId = Guid.NewGuid();
        var csoId = Guid.NewGuid();
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.Projected,
            ConnectedSystemObjectId = csoId
        };
        await _repo.BulkInsertRpeisAsync(new List<ActivityRunProfileExecutionItem> { rpei });

        var mvoChange = new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            ActivityRunProfileExecutionItemId = rpei.Id,
            ChangeType = ObjectChangeType.Projected,
            ChangeTime = DateTime.UtcNow
        };
        await _repo.CreateMetaverseObjectChangeDirectAsync(mvoChange);

        var result = await _repo.GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(
            activityId, new[] { csoId });

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ExistingMvoChangeId, Is.EqualTo(mvoChange.Id));
    }

    [Test]
    public async Task GetRpeisWithMvoChangeIdsForCrossPageMergeAsync_IncludesSyncOutcomesAsync()
    {
        var activityId = Guid.NewGuid();
        var csoId = Guid.NewGuid();
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.Projected,
            ConnectedSystemObjectId = csoId
        };
        var rootOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            ActivityRunProfileExecutionItemId = rpei.Id,
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            Ordinal = 0
        };
        var childOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            ActivityRunProfileExecutionItemId = rpei.Id,
            ParentSyncOutcomeId = rootOutcome.Id,
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            Ordinal = 0,
            DetailCount = 3
        };
        rpei.SyncOutcomes.Add(rootOutcome);
        rpei.SyncOutcomes.Add(childOutcome);
        await _repo.BulkInsertRpeisAsync(new List<ActivityRunProfileExecutionItem> { rpei });

        var result = await _repo.GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(
            activityId, new[] { csoId });

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Rpei.SyncOutcomes, Has.Count.EqualTo(2));
        Assert.That(result[0].Rpei.SyncOutcomes.Any(o => !o.ParentSyncOutcomeId.HasValue), Is.True,
            "Root outcome should be present.");
        Assert.That(result[0].Rpei.SyncOutcomes.Any(o => o.ParentSyncOutcomeId == rootOutcome.Id), Is.True,
            "Child outcome should be present and linked to the root.");
    }

    [Test]
    public async Task GetRpeisWithMvoChangeIdsForCrossPageMergeAsync_FiltersOutDifferentActivityAsync()
    {
        var targetActivityId = Guid.NewGuid();
        var otherActivityId = Guid.NewGuid();
        var csoId = Guid.NewGuid();

        var targetRpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = targetActivityId,
            ObjectChangeType = ObjectChangeType.Projected,
            ConnectedSystemObjectId = csoId
        };
        var otherRpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = otherActivityId,
            ObjectChangeType = ObjectChangeType.Projected,
            ConnectedSystemObjectId = csoId
        };
        await _repo.BulkInsertRpeisAsync(new List<ActivityRunProfileExecutionItem> { targetRpei, otherRpei });

        var result = await _repo.GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(
            targetActivityId, new[] { csoId });

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Rpei.Id, Is.EqualTo(targetRpei.Id));
    }
}
