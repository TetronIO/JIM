using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Utility;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class ActivitiesControllerTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<ILogger<ActivitiesController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private ActivitiesController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockLogger = new Mock<ILogger<ActivitiesController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new ActivitiesController(_mockLogger.Object, _application);
    }

    #region GetActivitiesAsync tests

    [Test]
    public async Task GetActivitiesAsync_ReturnsOkResult()
    {
        var pagedResult = new PagedResultSet<Activity>
        {
            Results = new List<Activity>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockActivityRepo.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetActivitiesAsync(pagination);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetActivitiesAsync_WithActivities_ReturnsPaginatedResponse()
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "Test Activity",
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete
        };
        var pagedResult = new PagedResultSet<Activity>
        {
            Results = new List<Activity> { activity },
            TotalResults = 1,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockActivityRepo.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetActivitiesAsync(pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ActivityHeader>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.TotalCount, Is.EqualTo(1));
        Assert.That(response.Items.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task GetActivitiesAsync_WithSearch_PassesSearchToRepository()
    {
        var pagedResult = new PagedResultSet<Activity>
        {
            Results = new List<Activity>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockActivityRepo.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        await _controller.GetActivitiesAsync(pagination, "test search");

        _mockActivityRepo.Verify(r => r.GetActivitiesAsync(1, 20, "test search", It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>()), Times.Once);
    }

    [Test]
    public async Task GetActivitiesAsync_WithPagination_RespectsPageAndPageSize()
    {
        var pagedResult = new PagedResultSet<Activity>
        {
            Results = new List<Activity>(),
            TotalResults = 100,
            CurrentPage = 2,
            PageSize = 10
        };
        _mockActivityRepo.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 2, PageSize = 10 };
        var result = await _controller.GetActivitiesAsync(pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ActivityHeader>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Page, Is.EqualTo(2));
        Assert.That(response.PageSize, Is.EqualTo(10));
    }

    #endregion

    #region GetActivityAsync tests

    [Test]
    public async Task GetActivityAsync_WithValidId_ReturnsOkResult()
    {
        var activityId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = activityId,
            TargetName = "Test Activity",
            TargetType = ActivityTargetType.TrustedCertificate,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync(activity);

        var result = await _controller.GetActivityAsync(activityId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetActivityAsync_WithValidId_ReturnsActivityDetail()
    {
        var activityId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = activityId,
            TargetName = "Test Activity",
            TargetType = ActivityTargetType.TrustedCertificate,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync(activity);

        var result = await _controller.GetActivityAsync(activityId) as OkObjectResult;
        var detail = result?.Value as ActivityDetailDto;

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Id, Is.EqualTo(activityId));
        Assert.That(detail.TargetName, Is.EqualTo("Test Activity"));
    }

    [Test]
    public async Task GetActivityAsync_WithNonExistentId_ReturnsNotFound()
    {
        var activityId = Guid.NewGuid();
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync((Activity?)null);

        var result = await _controller.GetActivityAsync(activityId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetActivityAsync_WithRunProfileActivity_IncludesExecutionStats()
    {
        var activityId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = activityId,
            TargetName = "Full Import",
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete
        };
        var stats = new ActivityRunProfileExecutionStats
        {
            ActivityId = activityId,
            TotalObjectChangeCount = 100,
            TotalCsoAdds = 50,
            TotalCsoUpdates = 40,
            TotalCsoDeletes = 5,
            TotalObjectErrors = 5
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync(activity);
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionStatsAsync(activityId))
            .ReturnsAsync(stats);

        var result = await _controller.GetActivityAsync(activityId) as OkObjectResult;
        var detail = result?.Value as ActivityDetailDto;

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.ExecutionStats, Is.Not.Null);
        Assert.That(detail.ExecutionStats!.TotalObjectChangeCount, Is.EqualTo(100));
    }

    #endregion

    #region GetActivityStatsAsync tests

    [Test]
    public async Task GetActivityStatsAsync_WithValidRunProfileActivity_ReturnsOkResult()
    {
        var activityId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = activityId,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Status = ActivityStatus.Complete
        };
        var stats = new ActivityRunProfileExecutionStats
        {
            ActivityId = activityId,
            TotalObjectChangeCount = 50,
            TotalCsoAdds = 50,
            TotalObjectErrors = 0
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync(activity);
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionStatsAsync(activityId))
            .ReturnsAsync(stats);

        var result = await _controller.GetActivityStatsAsync(activityId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetActivityStatsAsync_WithNonExistentId_ReturnsNotFound()
    {
        var activityId = Guid.NewGuid();
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync((Activity?)null);

        var result = await _controller.GetActivityStatsAsync(activityId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetActivityStatsAsync_WithNonRunProfileActivity_ReturnsBadRequest()
    {
        var activityId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = activityId,
            TargetType = ActivityTargetType.TrustedCertificate, // Not a run profile activity
            Status = ActivityStatus.Complete
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync(activity);

        var result = await _controller.GetActivityStatsAsync(activityId);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    #region GetActivityExecutionItemsAsync tests

    [Test]
    public async Task GetActivityExecutionItemsAsync_WithValidRunProfileActivity_ReturnsOkResult()
    {
        var activityId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = activityId,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Status = ActivityStatus.Complete
        };
        var pagedResult = new PagedResultSet<ActivityRunProfileExecutionItemHeader>
        {
            Results = new List<ActivityRunProfileExecutionItemHeader>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync(activity);
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemHeadersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<IEnumerable<ObjectChangeType>?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetActivityExecutionItemsAsync(activityId, pagination);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetActivityExecutionItemsAsync_WithNonExistentId_ReturnsNotFound()
    {
        var activityId = Guid.NewGuid();
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync((Activity?)null);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetActivityExecutionItemsAsync(activityId, pagination);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetActivityExecutionItemsAsync_WithNonRunProfileActivity_ReturnsBadRequest()
    {
        var activityId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = activityId,
            TargetType = ActivityTargetType.DataGenerationTemplate, // Not a run profile activity
            Status = ActivityStatus.Complete
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync(activity);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetActivityExecutionItemsAsync(activityId, pagination);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetActivityExecutionItemsAsync_ReturnsPaginatedResponse()
    {
        var activityId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = activityId,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Status = ActivityStatus.Complete
        };
        var item = new ActivityRunProfileExecutionItemHeader
        {
            Id = Guid.NewGuid(),
            DisplayName = "Test Object",
            ConnectedSystemObjectType = "User",
            ObjectChangeType = ObjectChangeType.Add
        };
        var pagedResult = new PagedResultSet<ActivityRunProfileExecutionItemHeader>
        {
            Results = new List<ActivityRunProfileExecutionItemHeader> { item },
            TotalResults = 1,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync(activity);
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemHeadersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<IEnumerable<ObjectChangeType>?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetActivityExecutionItemsAsync(activityId, pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ActivityRunProfileExecutionItemHeader>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.TotalCount, Is.EqualTo(1));
        Assert.That(response.Items.Count(), Is.EqualTo(1));
    }

    #endregion
}
