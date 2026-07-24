// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Verifies the lightweight Activity progress read (#202): a scalar projection of the Activity's
/// progress fields plus an operation-type breakdown derived from the Activity's stat counter rows
/// (#1078), without materialising Run Profile Execution Items.
/// </summary>
[TestFixture]
public class ActivityProgressReadTests
{
    private Guid _activityId;
    private Guid _otherActivityId;
    private List<Activity> _activities = null!;
    private List<ActivityStatCounter> _counters = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _activityId = Guid.NewGuid();
        _otherActivityId = Guid.NewGuid();

        _activities =
        [
            new Activity
            {
                Id = _activityId,
                TargetType = ActivityTargetType.ConnectedSystemRunProfile,
                ConnectedSystemRunType = ConnectedSystemRunType.FullImport,
                Status = ActivityStatus.InProgress,
                Message = "Importing objects from Connected System",
                ObjectsProcessed = 145,
                ObjectsToProcess = 500,
                Created = new DateTime(2026, 7, 24, 10, 0, 0, DateTimeKind.Utc),
                Executed = new DateTime(2026, 7, 24, 10, 0, 5, DateTimeKind.Utc)
            },
            new Activity
            {
                Id = _otherActivityId,
                TargetType = ActivityTargetType.ConnectedSystemRunProfile,
                Status = ActivityStatus.InProgress,
                ObjectsProcessed = 1,
                ObjectsToProcess = 1
            }
        ];

        _counters =
        [
            NewCounter(_activityId, ActivityStatDimension.ObjectChangeType, (int)ObjectChangeType.Added, 100),
            NewCounter(_activityId, ActivityStatDimension.ObjectChangeType, (int)ObjectChangeType.Updated, 40),
            NewCounter(_activityId, ActivityStatDimension.ObjectChangeType, (int)ObjectChangeType.Deleted, 5),
            NewCounter(_activityId, ActivityStatDimension.ErrorType, 1, 2),
            NewCounter(_activityId, ActivityStatDimension.ErrorType, 2, 1),
            // Non-operation dimensions must not surface in the operation counts.
            new ActivityStatCounter
            {
                ActivityId = _activityId,
                Dimension = ActivityStatDimension.ObjectTypeName,
                Key = "User",
                Count = 145
            },
            // Another Activity's counters must not bleed into this Activity's progress.
            NewCounter(_otherActivityId, ActivityStatDimension.ObjectChangeType, (int)ObjectChangeType.Added, 999)
        ];
    }

    private static ActivityStatCounter NewCounter(Guid activityId, ActivityStatDimension dimension, int key, long count) => new()
    {
        ActivityId = activityId,
        Dimension = dimension,
        Key = key.ToString(),
        Count = count
    };

    private JimApplication BuildApplication()
    {
        var mockDbContext = new Mock<JimDbContext>();
        mockDbContext.Setup(db => db.Activities).Returns(_activities.BuildMockDbSet().Object);
        mockDbContext.Setup(db => db.ActivityStatCounters).Returns(_counters.BuildMockDbSet().Object);
        return new JimApplication(new PostgresDataRepository(mockDbContext.Object));
    }

    [Test]
    public async Task GetActivityProgressAsync_UnknownActivity_ReturnsNullAsync()
    {
        using var jim = BuildApplication();

        var progress = await jim.Activities.GetActivityProgressAsync(Guid.NewGuid());

        Assert.That(progress, Is.Null);
    }

    [Test]
    public async Task GetActivityProgressAsync_InProgressActivity_ReturnsScalarProgressFieldsAsync()
    {
        using var jim = BuildApplication();

        var progress = await jim.Activities.GetActivityProgressAsync(_activityId);

        Assert.That(progress, Is.Not.Null);
        Assert.That(progress!.ActivityId, Is.EqualTo(_activityId));
        Assert.That(progress!.Status, Is.EqualTo(ActivityStatus.InProgress));
        Assert.That(progress!.Message, Is.EqualTo("Importing objects from Connected System"));
        Assert.That(progress!.ObjectsProcessed, Is.EqualTo(145));
        Assert.That(progress!.ObjectsToProcess, Is.EqualTo(500));
        Assert.That(progress!.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystemRunProfile));
        Assert.That(progress!.RunType, Is.EqualTo(ConnectedSystemRunType.FullImport));
        Assert.That(progress!.Executed, Is.EqualTo(new DateTime(2026, 7, 24, 10, 0, 5, DateTimeKind.Utc)));
    }

    [Test]
    public async Task GetActivityProgressAsync_ActivityWithCounters_ReturnsOperationBreakdownAsync()
    {
        using var jim = BuildApplication();

        var progress = await jim.Activities.GetActivityProgressAsync(_activityId);

        Assert.That(progress, Is.Not.Null);
        Assert.That(progress!.OperationCounts, Has.Count.EqualTo(3));
        Assert.That(progress!.OperationCounts[nameof(ObjectChangeType.Added)], Is.EqualTo(100));
        Assert.That(progress!.OperationCounts[nameof(ObjectChangeType.Updated)], Is.EqualTo(40));
        Assert.That(progress!.OperationCounts[nameof(ObjectChangeType.Deleted)], Is.EqualTo(5));
        Assert.That(progress!.TotalErrors, Is.EqualTo(3));
    }

    [Test]
    public async Task GetActivityProgressAsync_ActivityWithNoCounters_ReturnsEmptyOperationCountsAsync()
    {
        _counters.Clear();
        using var jim = BuildApplication();

        var progress = await jim.Activities.GetActivityProgressAsync(_activityId);

        Assert.That(progress, Is.Not.Null);
        Assert.That(progress!.OperationCounts, Is.Empty);
        Assert.That(progress!.TotalErrors, Is.EqualTo(0));
    }

    [Test]
    public async Task GetActivityProgressAsync_ExecutionNotStarted_ReturnsNullExecutedAsync()
    {
        _activities.Single(a => a.Id == _activityId).Executed = default;
        using var jim = BuildApplication();

        var progress = await jim.Activities.GetActivityProgressAsync(_activityId);

        Assert.That(progress, Is.Not.Null);
        Assert.That(progress!.Executed, Is.Null);
    }
}
