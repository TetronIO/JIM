// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the paged child-activity retrieval used by the Activity detail page: child activity lists can grow
/// substantially (e.g. seeding operations), so the repository must page at the data layer rather than loading
/// every child into memory.
/// </summary>
[TestFixture]
public class ChildActivityPagingTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

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
        _repository?.Dispose();
        _dbContext?.Dispose();
    }

    [Test]
    public async Task GetChildActivitiesAsync_Paged_ReturnsRequestedPageInCreatedOrderAsync()
    {
        var parent = NewActivity(daysOld: 10);
        var children = Enumerable.Range(0, 5)
            .Select(i => NewActivity(daysOld: 5, minutesOffset: i, parentActivityId: parent.Id))
            .ToList();
        _dbContext.Activities.Add(parent);
        _dbContext.Activities.AddRange(children);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetChildActivitiesAsync(parent.Id, page: 2, pageSize: 2);

        Assert.That(result.TotalResults, Is.EqualTo(5), "all five children should be counted");
        Assert.That(result.Results.Select(a => a.Id), Is.EqualTo(new[] { children[2].Id, children[3].Id }),
            "page 2 of size 2 should hold the third and fourth children in Created-ascending order");
    }

    [Test]
    public async Task GetChildActivitiesAsync_Paged_ExcludesOtherActivitiesAsync()
    {
        var parent = NewActivity(daysOld: 10);
        var otherParent = NewActivity(daysOld: 10);
        var child = NewActivity(daysOld: 5, minutesOffset: 0, parentActivityId: parent.Id);
        var otherChild = NewActivity(daysOld: 5, minutesOffset: 1, parentActivityId: otherParent.Id);
        _dbContext.Activities.AddRange(parent, otherParent, child, otherChild);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetChildActivitiesAsync(parent.Id, page: 1, pageSize: 10);

        Assert.That(result.Results.Select(a => a.Id), Is.EqualTo(new[] { child.Id }),
            "only the requested parent's children should be returned");
    }

    [Test]
    public async Task GetChildActivitiesAsync_Paged_PageBeyondRange_ReturnsEmptyAsync()
    {
        var parent = NewActivity(daysOld: 10);
        var child = NewActivity(daysOld: 5, minutesOffset: 0, parentActivityId: parent.Id);
        _dbContext.Activities.AddRange(parent, child);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetChildActivitiesAsync(parent.Id, page: 3, pageSize: 10);

        Assert.That(result.TotalResults, Is.EqualTo(0), "a page beyond the last should report no results");
        Assert.That(result.Results, Is.Empty);
    }

    private static Activity NewActivity(int daysOld, int minutesOffset = 0, Guid? parentActivityId = null) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystem,
        TargetOperationType = ActivityTargetOperationType.Update,
        InitiatedByType = ActivityInitiatorType.System,
        InitiatedByName = "System",
        ParentActivityId = parentActivityId,
        Created = DateTime.UtcNow.AddDays(-daysOld).AddMinutes(minutesOffset)
    };
}
