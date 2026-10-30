// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models;
using JIM.Web.Models.Api;
using JIM.Web.Services;
using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
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
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<ILogger<ActivitiesController>> _mockLogger = null!;
    private Mock<IActivityEtaTracker> _mockEtaTracker = null!;
    private JimApplication _application = null!;
    private ActivitiesController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>().StubConfigurationChangeSynchronisationReads();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockActivityRepo.Setup(r => r.GetChildActivityCountsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, int>());
        _mockLogger = new Mock<ILogger<ActivitiesController>>();
        _mockEtaTracker = new Mock<IActivityEtaTracker>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new ActivitiesController(_mockLogger.Object, _application, _mockEtaTracker.Object);
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
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
                It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>()))
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
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
                It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetActivitiesAsync(pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ActivityHeader>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.TotalCount, Is.EqualTo(1));
        Assert.That(response.Items.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task GetActivitiesAsync_ActivityWithChildren_ReportsItsChildActivityCountAsync()
    {
        // #2346: the list left ChildActivityCount at 0 for every Activity, so an API client could not tell which ones
        // have children to expand (Scenario 024 never found a password delivery filed beneath its queuing Activity).
        var parent = new Activity { Id = Guid.NewGuid(), TargetName = "Parent", TargetType = ActivityTargetType.PasswordSynchronisation, Created = DateTime.UtcNow, Status = ActivityStatus.Complete };
        var childless = new Activity { Id = Guid.NewGuid(), TargetName = "Childless", TargetType = ActivityTargetType.ConnectedSystemRunProfile, Created = DateTime.UtcNow, Status = ActivityStatus.Complete };
        var pagedResult = new PagedResultSet<Activity> { Results = new List<Activity> { parent, childless }, TotalResults = 2, CurrentPage = 1, PageSize = 20 };
        _mockActivityRepo.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
                It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>()))
            .ReturnsAsync(pagedResult);
        _mockActivityRepo.Setup(r => r.GetChildActivityCountsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, int> { [parent.Id] = 3 });

        var result = await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 }) as OkObjectResult;
        var items = (result?.Value as PaginatedResponse<ActivityHeader>)!.Items.ToList();

        Assert.That(items.Single(i => i.Id == parent.Id).ChildActivityCount, Is.EqualTo(3));
        Assert.That(items.Single(i => i.Id == childless.Id).ChildActivityCount, Is.EqualTo(0));
    }

    [Test]
    public async Task GetActivitiesAsync_WithActivities_ReadsThePageChildCountsInOneQueryAsync()
    {
        var first = new Activity { Id = Guid.NewGuid(), TargetName = "First", TargetType = ActivityTargetType.ConnectedSystemRunProfile, Created = DateTime.UtcNow, Status = ActivityStatus.Complete };
        var second = new Activity { Id = Guid.NewGuid(), TargetName = "Second", TargetType = ActivityTargetType.ConnectedSystemRunProfile, Created = DateTime.UtcNow, Status = ActivityStatus.Complete };
        var pagedResult = new PagedResultSet<Activity> { Results = new List<Activity> { first, second }, TotalResults = 2, CurrentPage = 1, PageSize = 20 };
        _mockActivityRepo.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
                It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>()))
            .ReturnsAsync(pagedResult);

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 });

        _mockActivityRepo.Verify(r => r.GetChildActivityCountsAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.OrderBy(id => id).SequenceEqual(new[] { first.Id, second.Id }.OrderBy(id => id)))), Times.Once);
    }

    [Test]
    public async Task GetActivitiesAsync_ActivityProducedByASchedule_MapsScheduleAttributionOntoTheHeader()
    {
        // Read parity (#1196): the portal shows which Schedule produced an Activity and which step it was, so the
        // REST list has to say the same.
        var executionId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "Full Import",
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete,
            ScheduleExecutionId = executionId,
            ScheduleStepIndex = 2,
            ScheduledByScheduleId = scheduleId,
            ScheduledByScheduleName = "Nightly Sync"
        };
        var pagedResult = new PagedResultSet<Activity>
        {
            Results = new List<Activity> { activity },
            TotalResults = 1,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockActivityRepo.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
                It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetActivitiesAsync(pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ActivityHeader>;
        var header = response!.Items.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.ScheduleExecutionId, Is.EqualTo(executionId));
            Assert.That(header.ScheduleStepIndex, Is.EqualTo(2));
            Assert.That(header.ScheduledByScheduleId, Is.EqualTo(scheduleId));
            Assert.That(header.ScheduledByScheduleName, Is.EqualTo("Nightly Sync"));
        }
    }

    [Test]
    public async Task GetActivitiesAsync_ActivityNotProducedByASchedule_LeavesScheduleAttributionNull()
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "Full Import",
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
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
                It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetActivitiesAsync(pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ActivityHeader>;
        var header = response!.Items.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.ScheduleExecutionId, Is.Null);
            Assert.That(header.ScheduleStepIndex, Is.Null);
            Assert.That(header.ScheduledByScheduleId, Is.Null);
            Assert.That(header.ScheduledByScheduleName, Is.Null);
        }
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
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
                It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        await _controller.GetActivitiesAsync(pagination, "test search");

        _mockActivityRepo.Verify(r => r.GetActivitiesAsync(1, 20, "test search", It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
                It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>()), Times.Once);
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
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
                It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 2, PageSize = 10 };
        var result = await _controller.GetActivitiesAsync(pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ActivityHeader>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Page, Is.EqualTo(2));
        Assert.That(response.PageSize, Is.EqualTo(10));
    }

    [Test]
    public async Task GetActivitiesAsync_WithTargetTypeFilter_PassesFilterToRepositoryAsync()
    {
        var pagedResult = new PagedResultSet<Activity>
        {
            Results = new List<Activity>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockActivityRepo.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
                It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>(),
                It.IsAny<IEnumerable<ActivityInitiatorType>?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var targetTypes = new List<ActivityTargetType> { ActivityTargetType.Authentication };
        await _controller.GetActivitiesAsync(pagination, targetType: targetTypes);

        _mockActivityRepo.Verify(r => r.GetActivitiesAsync(
            1, 20, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
            It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
            It.Is<IEnumerable<ActivityTargetType>?>(t => t != null && t.SequenceEqual(targetTypes)),
            It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>(),
            It.IsAny<IEnumerable<ActivityInitiatorType>?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()), Times.Once);
    }

    [Test]
    public async Task GetActivitiesAsync_WithInitiatorTypeFilter_PassesFilterToRepositoryAsync()
    {
        var pagedResult = new PagedResultSet<Activity>
        {
            Results = new List<Activity>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockActivityRepo.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
                It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>(),
                It.IsAny<IEnumerable<ActivityInitiatorType>?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var initiatorTypes = new List<ActivityInitiatorType> { ActivityInitiatorType.Anonymous };
        await _controller.GetActivitiesAsync(pagination, initiatorType: initiatorTypes);

        _mockActivityRepo.Verify(r => r.GetActivitiesAsync(
            1, 20, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
            It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
            It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>(),
            It.Is<IEnumerable<ActivityInitiatorType>?>(t => t != null && t.SequenceEqual(initiatorTypes)),
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>()), Times.Once);
    }

    #endregion

    #region GetActivitiesAsync filter query parameter tests

    // The Activities query supports considerably more filtering than the endpoint used to expose; these cover
    // every query parameter reaching the application layer intact. Arguments are read back off the mock's
    // invocation by parameter name rather than asserted through a nineteen-argument Moq expression, which
    // would be unreadable and would silently pass if the parameter order changed.

    [Test]
    public async Task GetActivitiesAsync_WithOperationFilter_PassesFilterToRepositoryAsync()
    {
        SetupAnyActivitiesQuery();

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 },
            operation: [ActivityTargetOperationType.Execute, ActivityTargetOperationType.Clear]);

        Assert.That(LastActivitiesQueryArguments()["operationFilter"],
            Is.EquivalentTo(new[] { ActivityTargetOperationType.Execute, ActivityTargetOperationType.Clear }));
    }

    [Test]
    public async Task GetActivitiesAsync_WithOutcomeFilter_PassesFilterToRepositoryAsync()
    {
        SetupAnyActivitiesQuery();

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 },
            outcome: [ActivityOutcomeType.Errors]);

        Assert.That(LastActivitiesQueryArguments()["outcomeFilter"], Is.EquivalentTo(new[] { ActivityOutcomeType.Errors }));
    }

    [Test]
    public async Task GetActivitiesAsync_WithStatusFilter_PassesFilterToRepositoryAsync()
    {
        SetupAnyActivitiesQuery();

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 },
            status: [ActivityStatus.FailedWithError]);

        Assert.That(LastActivitiesQueryArguments()["statusFilter"], Is.EquivalentTo(new[] { ActivityStatus.FailedWithError }));
    }

    [Test]
    public async Task GetActivitiesAsync_WithInitiatedById_PassesFilterToRepositoryAsync()
    {
        SetupAnyActivitiesQuery();
        var principalId = Guid.NewGuid();

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 }, initiatedById: principalId);

        Assert.That(LastActivitiesQueryArguments()["initiatedById"], Is.EqualTo(principalId));
    }

    [Test]
    public async Task GetActivitiesAsync_WithHasChildActivities_PassesFilterToRepositoryAsync()
    {
        SetupAnyActivitiesQuery();

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 }, hasChildActivities: true);

        Assert.That(LastActivitiesQueryArguments()["hasChildActivities"], Is.True);
    }

    [Test]
    public async Task GetActivitiesAsync_WithCreatedRange_PassesBothBoundsToRepositoryAsync()
    {
        SetupAnyActivitiesQuery();
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 },
            createdFrom: from, createdTo: to);

        var arguments = LastActivitiesQueryArguments();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(arguments["createdFrom"], Is.EqualTo(from));
            Assert.That(arguments["createdTo"], Is.EqualTo(to));
        }
    }

    [Test]
    public async Task GetActivitiesAsync_WithConnectedSystemFilter_PassesFilterToRepositoryAsync()
    {
        SetupAnyActivitiesQuery();

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 },
            connectedSystem: ["Contoso AD", "Fabrikam HR"]);

        Assert.That(LastActivitiesQueryArguments()["connectedSystemFilter"],
            Is.EquivalentTo(new[] { "Contoso AD", "Fabrikam HR" }));
    }

    [Test]
    public async Task GetActivitiesAsync_WithRunProfileFilter_PassesFilterToRepositoryAsync()
    {
        SetupAnyActivitiesQuery();

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 },
            runProfile: ["Full Import"]);

        Assert.That(LastActivitiesQueryArguments()["runProfileFilter"], Is.EquivalentTo(new[] { "Full Import" }));
    }

    [Test]
    public async Task GetActivitiesAsync_WithInitiatedBy_PassesFilterToRepositoryAsync()
    {
        SetupAnyActivitiesQuery();

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 }, initiatedBy: "alice");

        Assert.That(LastActivitiesQueryArguments()["initiatedByFilter"], Is.EqualTo("alice"));
    }

    [Test]
    public async Task GetActivitiesAsync_WithScheduledOnly_PassesFilterToRepositoryAsync()
    {
        SetupAnyActivitiesQuery();

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 }, scheduledOnly: false);

        Assert.That(LastActivitiesQueryArguments()["initiatedBySchedule"], Is.False);
    }

    [Test]
    public async Task GetActivitiesAsync_WithScheduleIdFilter_PassesFilterToRepositoryAsync()
    {
        SetupAnyActivitiesQuery();
        var scheduleId = Guid.NewGuid();

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 }, scheduleId: [scheduleId]);

        Assert.That(LastActivitiesQueryArguments()["scheduleFilter"], Is.EquivalentTo(new[] { scheduleId }));
    }

    [Test]
    public async Task GetActivitiesAsync_WithNoFilters_PassesNoneOfThemToRepositoryAsync()
    {
        SetupAnyActivitiesQuery();

        await _controller.GetActivitiesAsync(new PaginationRequest { Page = 1, PageSize = 20 });

        var arguments = LastActivitiesQueryArguments();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(arguments["operationFilter"], Is.Null);
            Assert.That(arguments["outcomeFilter"], Is.Null);
            Assert.That(arguments["statusFilter"], Is.Null);
            Assert.That(arguments["initiatedById"], Is.Null);
            Assert.That(arguments["hasChildActivities"], Is.Null);
            Assert.That(arguments["createdFrom"], Is.Null);
            Assert.That(arguments["createdTo"], Is.Null);
            Assert.That(arguments["connectedSystemFilter"], Is.Null);
            Assert.That(arguments["runProfileFilter"], Is.Null);
            Assert.That(arguments["initiatedByFilter"], Is.Null);
            Assert.That(arguments["initiatedBySchedule"], Is.Null);
            Assert.That(arguments["scheduleFilter"], Is.Null);
        }
    }

    /// <summary>
    /// Answers any Activities query with an empty page, so a test can assert on what the controller asked
    /// for rather than on what came back.
    /// </summary>
    private void SetupAnyActivitiesQuery()
    {
        _mockActivityRepo.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<IEnumerable<ActivityTargetOperationType>?>(), It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
                It.IsAny<IEnumerable<ActivityTargetType>?>(), It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>(),
                It.IsAny<IEnumerable<ActivityInitiatorType>?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(),
                It.IsAny<bool?>(), It.IsAny<IEnumerable<Guid>?>()))
            .ReturnsAsync(new PagedResultSet<Activity>
            {
                Results = new List<Activity>(),
                TotalResults = 0,
                CurrentPage = 1,
                PageSize = 20
            });
    }

    /// <summary>
    /// The arguments the most recent Activities query received, keyed by parameter name.
    /// </summary>
    private IReadOnlyDictionary<string, object?> LastActivitiesQueryArguments()
    {
        var invocation = _mockActivityRepo.Invocations
            .Last(i => i.Method.Name == nameof(IActivityRepository.GetActivitiesAsync));

        return invocation.Method.GetParameters()
            .Select((parameter, index) => (parameter.Name!, Argument: (object?)invocation.Arguments[index]))
            .ToDictionary(x => x.Item1, x => x.Argument);
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
    public async Task GetActivityAsync_ActivityProducedByASchedule_MapsScheduleAttributionOntoTheDetail()
    {
        var activityId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = activityId,
            TargetName = "Full Import",
            TargetType = ActivityTargetType.TrustedCertificate,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete,
            ScheduleExecutionId = executionId,
            ScheduleStepIndex = 0,
            ScheduledByScheduleId = scheduleId,
            ScheduledByScheduleName = "Nightly Sync"
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync(activity);

        var result = await _controller.GetActivityAsync(activityId) as OkObjectResult;
        var detail = result?.Value as ActivityDetailDto;

        Assert.That(detail, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail!.ScheduleExecutionId, Is.EqualTo(executionId));
            Assert.That(detail.ScheduleStepIndex, Is.EqualTo(0));
            Assert.That(detail.ScheduledByScheduleId, Is.EqualTo(scheduleId));
            Assert.That(detail.ScheduledByScheduleName, Is.EqualTo("Nightly Sync"));
        }
    }

    [Test]
    public async Task GetActivityAsync_ActivityNotProducedByASchedule_LeavesScheduleAttributionNull()
    {
        var activityId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = activityId,
            TargetName = "Full Import",
            TargetType = ActivityTargetType.TrustedCertificate,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync(activity);

        var result = await _controller.GetActivityAsync(activityId) as OkObjectResult;
        var detail = result?.Value as ActivityDetailDto;

        Assert.That(detail, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail!.ScheduleExecutionId, Is.Null);
            Assert.That(detail.ScheduleStepIndex, Is.Null);
            Assert.That(detail.ScheduledByScheduleId, Is.Null);
            Assert.That(detail.ScheduledByScheduleName, Is.Null);
        }
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

    [Test]
    public async Task GetActivityAsync_ConfigurationChangeActivity_ReportsItsChangesAsAwaitingUntilARunAppliesThemAsync()
    {
        var activityId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var appliedAt = DateTime.UtcNow.AddMinutes(-3);
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId)).ReturnsAsync(new Activity
        {
            Id = activityId, TargetType = ActivityTargetType.SynchronisationRule, Status = ActivityStatus.Complete
        });
        _mockConnectedSystemRepo.Setup(r => r.GetConfigurationChangeSynchronisationSummariesForActivityAsync(activityId, ObjectTypeAwaitingSynchronisation.MaxChangesListed))
            .ReturnsAsync(
            [
                new ConfigurationChangeSynchronisationSummary
                {
                    Id = 1, ConnectedSystemId = 3, ConnectedSystemName = "HR", ConnectedSystemObjectTypeId = 7, ObjectTypeName = "user",
                    Reason = "HR Inbound: Scope changed", RequestedAt = DateTime.UtcNow.AddHours(-1), ConfigurationChangeActivityId = activityId,
                    InitiatedByType = ActivityInitiatorType.User, InitiatedByName = "Alice Adams",
                    ClaimedByActivityId = runId, AppliedAt = appliedAt
                },
                new ConfigurationChangeSynchronisationSummary
                {
                    Id = 2, ConnectedSystemId = 3, ConnectedSystemName = "HR", ConnectedSystemObjectTypeId = 8, ObjectTypeName = "group",
                    Reason = "HR Groups: Scope changed", RequestedAt = DateTime.UtcNow.AddHours(-1), ConfigurationChangeActivityId = activityId
                }
            ]);

        var detail = (await _controller.GetActivityAsync(activityId) as OkObjectResult)?.Value as ActivityDetailDto;

        var types = detail!.ConfigurationChangeSynchronisations.ToDictionary(type => type.ObjectTypeName);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(types.Keys, Is.EquivalentTo(new[] { "user", "group" }));
            Assert.That(types["user"].IsApplied, Is.True);
            Assert.That(types["user"].AppliedAt, Is.EqualTo(appliedAt));
            Assert.That(types["user"].AppliedByActivityId, Is.EqualTo(runId));
            Assert.That(types["user"].Changes.Single().InitiatedByName, Is.EqualTo("Alice Adams"));
            Assert.That(types["user"].ObjectCount, Is.Null, "a configuration change Activity processes no objects");
            Assert.That(types["group"].IsApplied, Is.False);
            Assert.That(types["group"].AppliedAt, Is.Null);
        }
    }

    [Test]
    public async Task GetActivityAsync_SynchronisationRunThatClaimedChanges_ReportsTheObjectsItProcessedInFullAsync()
    {
        var runId = Guid.NewGuid();
        _mockActivityRepo.Setup(r => r.GetActivityAsync(runId)).ReturnsAsync(new Activity
        {
            Id = runId, TargetType = ActivityTargetType.ConnectedSystemRunProfile, ConnectedSystemId = 3,
            ConnectedSystemRunType = ConnectedSystemRunType.DeltaSynchronisation, Status = ActivityStatus.Complete
        });
        _mockConnectedSystemRepo.Setup(r => r.GetConfigurationChangeSynchronisationSummariesForActivityAsync(runId, ObjectTypeAwaitingSynchronisation.MaxChangesListed))
            .ReturnsAsync(
            [
                new ConfigurationChangeSynchronisationSummary
                {
                    Id = 1, ConnectedSystemId = 3, ConnectedSystemName = "HR", ConnectedSystemObjectTypeId = 7, ObjectTypeName = "user",
                    Reason = "HR Inbound: Scope changed", RequestedAt = DateTime.UtcNow.AddHours(-1), ClaimedByActivityId = runId
                }
            ]);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(3, 7, null)).ReturnsAsync(12_500);

        var detail = (await _controller.GetActivityAsync(runId) as OkObjectResult)?.Value as ActivityDetailDto;

        var type = detail!.ConfigurationChangeSynchronisations.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(type.ObjectCount, Is.EqualTo(12_500));
            Assert.That(type.IsApplied, Is.False, "claimed but not applied: the run has not completed its application");
        }
    }

    [Test]
    public async Task GetActivityAsync_ActivityThatCannotHaveSynchronisationChanges_ReadsNoneAsync()
    {
        var activityId = Guid.NewGuid();
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId)).ReturnsAsync(new Activity
        {
            Id = activityId, TargetType = ActivityTargetType.HistoryRetentionCleanup, Status = ActivityStatus.Complete
        });

        var detail = (await _controller.GetActivityAsync(activityId) as OkObjectResult)?.Value as ActivityDetailDto;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail!.ConfigurationChangeSynchronisations, Is.Empty);
            _mockConnectedSystemRepo.Verify(r => r.GetConfigurationChangeSynchronisationSummariesForActivityAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
        }
    }

    [Test]
    public async Task GetActivityAsync_ImportWithWatermarks_DescribesThemThroughItsConnectorAsync()
    {
        // Read parity with the portal, which says in words what range a Delta Import read rather than printing the
        // Connector's persisted data.
        var activityId = Guid.NewGuid();
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId)).ReturnsAsync(new Activity
        {
            Id = activityId,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            ConnectedSystemId = 7,
            ConnectedSystemRunType = ConnectedSystemRunType.DeltaImport,
            ImportWatermarkFrom = "{\"changeSequence\":8}",
            ImportWatermarkTo = "{\"changeSequence\":10}"
        });
        var connectedSystems = new Mock<IConnectedSystemRepository>().StubConfigurationChangeSynchronisationReads();
        connectedSystems.Setup(r => r.GetConnectedSystemCoreAsync(7, It.IsAny<bool>())).ReturnsAsync(new ConnectedSystem
        {
            Id = 7,
            Name = "SCIM clients",
            ConnectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.ScimServiceProviderConnectorName }
        });
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);

        var result = await _controller.GetActivityAsync(activityId) as OkObjectResult;
        var detail = result?.Value as ActivityDetailDto;

        Assert.That(detail?.ImportReadRange, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail!.ImportReadRange!.From?.Kind, Is.EqualTo("change sequence"));
            Assert.That(detail!.ImportReadRange!.From?.Points.Single().Number, Is.EqualTo(8));
            Assert.That(detail!.ImportReadRange!.To?.Points.Single().Number, Is.EqualTo(10));
        }
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
            TargetType = ActivityTargetType.TrustedCertificate, // Not a Run Profile activity
            Status = ActivityStatus.Complete
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(activityId))
            .ReturnsAsync(activity);

        var result = await _controller.GetActivityStatsAsync(activityId);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    #region GetActivityProgressAsync tests

    private static ActivityProgress NewInProgressActivityProgress(Guid id) => new()
    {
        ActivityId = id,
        Status = ActivityStatus.InProgress,
        Message = "Importing objects from Connected System",
        ObjectsProcessed = 250,
        ObjectsToProcess = 1000,
        Created = DateTime.UtcNow.AddMinutes(-2),
        Executed = DateTime.UtcNow.AddMinutes(-1),
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        RunType = JIM.Models.Staging.ConnectedSystemRunType.FullImport,
        OperationCounts = new Dictionary<string, long> { ["Added"] = 200, ["Updated"] = 50 },
        TotalErrors = 3
    };

    [Test]
    public async Task GetActivityProgressAsync_UnknownActivity_ReturnsNotFound()
    {
        _mockActivityRepo.Setup(r => r.GetActivityProgressAsync(It.IsAny<Guid>()))
            .ReturnsAsync((ActivityProgress?)null);

        var result = await _controller.GetActivityProgressAsync(Guid.NewGuid());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetActivityProgressAsync_InProgressActivity_ReturnsProgressWithEta()
    {
        var activityId = Guid.NewGuid();
        _mockActivityRepo.Setup(r => r.GetActivityProgressAsync(activityId))
            .ReturnsAsync(NewInProgressActivityProgress(activityId));
        _mockEtaTracker.Setup(t => t.RecordSample(activityId, 250, 1000))
            .Returns(new ActivityEtaEstimate(12.5d, 60d));

        var result = await _controller.GetActivityProgressAsync(activityId);

        var okResult = result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var dto = okResult!.Value as ActivityProgressDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Id, Is.EqualTo(activityId));
        Assert.That(dto!.Status, Is.EqualTo(ActivityStatus.InProgress));
        Assert.That(dto!.Message, Is.EqualTo("Importing objects from Connected System"));
        Assert.That(dto!.ObjectsProcessed, Is.EqualTo(250));
        Assert.That(dto!.ObjectsToProcess, Is.EqualTo(1000));
        Assert.That(dto!.PercentComplete, Is.EqualTo(25d).Within(0.001));
        Assert.That(dto!.ObjectsPerSecond, Is.EqualTo(12.5d).Within(0.001));
        Assert.That(dto!.EstimatedSecondsRemaining, Is.EqualTo(60d).Within(0.001));
        Assert.That(dto!.ElapsedSeconds, Is.EqualTo(60d).Within(5d));
        Assert.That(dto!.OperationCounts["Added"], Is.EqualTo(200));
        Assert.That(dto!.TotalErrors, Is.EqualTo(3));
        _mockEtaTracker.Verify(t => t.RecordSample(activityId, 250, 1000), Times.Once);
    }

    [Test]
    public async Task GetActivityProgressAsync_IndeterminateTotal_ReturnsNullPercentComplete()
    {
        var activityId = Guid.NewGuid();
        var progress = NewInProgressActivityProgress(activityId);
        progress.ObjectsToProcess = 0;
        _mockActivityRepo.Setup(r => r.GetActivityProgressAsync(activityId)).ReturnsAsync(progress);
        _mockEtaTracker.Setup(t => t.RecordSample(activityId, 250, 0))
            .Returns(new ActivityEtaEstimate(null, null));

        var result = await _controller.GetActivityProgressAsync(activityId);

        var okResult = result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var dto = okResult!.Value as ActivityProgressDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.PercentComplete, Is.Null);
    }

    [Test]
    public async Task GetActivityProgressAsync_CompletedActivity_ReleasesEtaStateAndRecordsNoSample()
    {
        var activityId = Guid.NewGuid();
        var progress = NewInProgressActivityProgress(activityId);
        progress.Status = ActivityStatus.Complete;
        _mockActivityRepo.Setup(r => r.GetActivityProgressAsync(activityId)).ReturnsAsync(progress);

        var result = await _controller.GetActivityProgressAsync(activityId);

        var okResult = result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var dto = okResult!.Value as ActivityProgressDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Status, Is.EqualTo(ActivityStatus.Complete));
        Assert.That(dto!.ObjectsPerSecond, Is.Null);
        Assert.That(dto!.EstimatedSecondsRemaining, Is.Null);
        _mockEtaTracker.Verify(t => t.RecordSample(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _mockEtaTracker.Verify(t => t.Remove(activityId), Times.Once);
    }

    [Test]
    public async Task GetActivityProgressAsync_QueuedActivity_NeitherRecordsNorRemovesEtaState()
    {
        var activityId = Guid.NewGuid();
        var progress = NewInProgressActivityProgress(activityId);
        progress.Status = ActivityStatus.NotSet;
        _mockActivityRepo.Setup(r => r.GetActivityProgressAsync(activityId)).ReturnsAsync(progress);

        var result = await _controller.GetActivityProgressAsync(activityId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockEtaTracker.Verify(t => t.RecordSample(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _mockEtaTracker.Verify(t => t.Remove(It.IsAny<Guid>()), Times.Never);
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
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<IEnumerable<ActivityRunProfileExecutionItemErrorType>?>(),
                It.IsAny<IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>?>()))
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
            TargetType = ActivityTargetType.ExampleDataTemplate, // Not a Run Profile activity
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
            ObjectChangeType = ObjectChangeType.Added
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
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<IEnumerable<ActivityRunProfileExecutionItemErrorType>?>(),
                It.IsAny<IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetActivityExecutionItemsAsync(activityId, pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ActivityRunProfileExecutionItemHeader>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.TotalCount, Is.EqualTo(1));
        Assert.That(response.Items.Count(), Is.EqualTo(1));
    }

    #endregion

    #region GetActivityExecutionItemAsync tests

    [Test]
    public async Task GetActivityExecutionItemAsync_WithExistingItem_ReturnsErrorMessageStackTraceAndSnapshotAsync()
    {
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Projected,
            ConnectedSystemObjectId = Guid.NewGuid(),
            MetaverseObjectId = Guid.NewGuid(),
            ExternalIdSnapshot = "EMP900083",
            DisplayNameSnapshot = "Osric Tamworth",
            ObjectTypeSnapshot = "person",
            ErrorType = ActivityRunProfileExecutionItemErrorType.GeneratedValueExhausted,
            ErrorMessage = "No free value was found for Staff Number after 1000 attempts.",
            ErrorStackTrace = "   at JIM.Application.UniqueValues.UniqueValueGenerationServer.ResolveAsync()",
            AttributeFlowCount = 1,
            OutcomeSummary = "Projected:1"
        };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);

        var result = await _controller.GetActivityExecutionItemAsync(item.Id) as OkObjectResult;
        var dto = result?.Value as ActivityRunProfileExecutionItemDetailDto;

        Assert.That(dto, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.Id, Is.EqualTo(item.Id));
            Assert.That(dto!.ActivityId, Is.EqualTo(item.ActivityId));
            Assert.That(dto!.ObjectChangeType, Is.EqualTo(ObjectChangeType.Projected));
            Assert.That(dto!.ConnectedSystemObjectId, Is.EqualTo(item.ConnectedSystemObjectId));
            Assert.That(dto!.MetaverseObjectId, Is.EqualTo(item.MetaverseObjectId));
            Assert.That(dto!.ExternalIdSnapshot, Is.EqualTo("EMP900083"));
            Assert.That(dto!.DisplayNameSnapshot, Is.EqualTo("Osric Tamworth"));
            Assert.That(dto!.ObjectTypeSnapshot, Is.EqualTo("person"));
            Assert.That(dto!.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.GeneratedValueExhausted));
            Assert.That(dto!.ErrorMessage, Is.EqualTo(item.ErrorMessage));
            Assert.That(dto!.ErrorStackTrace, Is.EqualTo(item.ErrorStackTrace));
            Assert.That(dto!.AttributeFlowCount, Is.EqualTo(1));
            Assert.That(dto!.OutcomeSummary, Is.EqualTo("Projected:1"));
        }
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_ItemWithUnresolvedReferences_ReturnsTheCountAsync()
    {
        // #959: the count travels on every item it applies to, including one whose error slot holds another error,
        // so a script reading the item learns about the references as the portal does.
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Added,
            ErrorType = ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError,
            ErrorMessage = "Failed to parse 'startDate' as DateTime.",
            UnresolvedReferenceCount = 2
        };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);

        var result = await _controller.GetActivityExecutionItemAsync(item.Id) as OkObjectResult;
        var dto = result?.Value as ActivityRunProfileExecutionItemDetailDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.UnresolvedReferenceCount, Is.EqualTo(2));
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_ItemWithUnreadableValues_ReturnsEachValueAndWhereItWasReadAsync()
    {
        // #2066: what the portal lists under Attribute Values Not Imported, so a script can see it too.
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.NotSet,
            ErrorType = ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError,
            AttributeValueErrorsJson = new ImportedAttributeValueErrors
            {
                Total = 3,
                Values =
                [
                    new ImportedAttributeValueError { AttributeName = "startDate", AttributeType = AttributeDataType.DateTime, Value = "31/02/2026", Reason = "Not a valid date and time." },
                    new ImportedAttributeValueError { AttributeName = "contractedHours", AttributeType = AttributeDataType.Number, Value = "37.5h", Reason = "Not a valid whole number." }
                ]
            }.ToJson(),
            SourceLocation = "hr-users.csv, row 4,182"
        };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);

        var result = await _controller.GetActivityExecutionItemAsync(item.Id) as OkObjectResult;
        var dto = result?.Value as ActivityRunProfileExecutionItemDetailDto;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.AttributeValueErrors.Select(v => (v.AttributeName, v.AttributeType, v.Value, v.Reason)), Is.EqualTo(new[]
            {
                ("startDate", (AttributeDataType?)AttributeDataType.DateTime, (string?)"31/02/2026", "Not a valid date and time."),
                ("contractedHours", (AttributeDataType?)AttributeDataType.Number, (string?)"37.5h", "Not a valid whole number.")
            }));
            Assert.That(dto!.AttributeValueErrorTotal, Is.EqualTo(3), "The total says when more values failed than the item keeps.");
            Assert.That(dto!.SourceLocation, Is.EqualTo("hr-users.csv, row 4,182"));
        }
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_ValuesJimCouldNotHold_SayJimFoundThemAndOlderRecordsSayTheConnectorAsync()
    {
        // #2153: JIM reports values too (dates less than a microsecond apart, both true and false), and a script must be
        // able to tell those from values the Connector could not read. A record kept before the source was recorded
        // carries no source, and every one of those was the Connector's.
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.NotSet,
            ErrorType = ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError,
            AttributeValueErrorsJson = """{"total":2,"values":[{"attributeName":"isActive","value":"true","reason":"r","source":"Jim"},{"attributeName":"startDate","value":"x","reason":"r"}]}"""
        };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);

        var dto = (await _controller.GetActivityExecutionItemAsync(item.Id) as OkObjectResult)?.Value as ActivityRunProfileExecutionItemDetailDto;

        Assert.That(dto!.AttributeValueErrors.Select(v => v.Source), Is.EqualTo(new[] { ImportAttributeValueErrorSource.Jim, ImportAttributeValueErrorSource.Connector }));
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_ItemWithNoUnreadableValues_ReturnsAnEmptyListAsync()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid(), ObjectChangeType = ObjectChangeType.Added };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);

        var dto = (await _controller.GetActivityExecutionItemAsync(item.Id) as OkObjectResult)?.Value as ActivityRunProfileExecutionItemDetailDto;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.AttributeValueErrors, Is.Empty);
            Assert.That(dto!.AttributeValueErrorTotal, Is.Null);
        }
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_WithNonExistentId_ReturnsNotFoundAsync()
    {
        var itemId = Guid.NewGuid();
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(itemId))
            .ReturnsAsync((ActivityRunProfileExecutionItem?)null);

        var result = await _controller.GetActivityExecutionItemAsync(itemId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_UsesTheLightRetrievalNotThePortalsFullGraphAsync()
    {
        // The portal's detail page loads the item's whole graph (its Connected System Object, attribute values,
        // references and linked Metaverse Object); this endpoint returns the item's own fields only, so it must
        // not pay for that load.
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);

        await _controller.GetActivityExecutionItemAsync(item.Id);

        _mockActivityRepo.Verify(r => r.GetActivityRunProfileExecutionItemDetailAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_AmbiguousMatch_ListsEachCandidateWithWhetherItExistsAndWhatIsJoinedToItAsync()
    {
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ErrorType = ActivityRunProfileExecutionItemErrorType.AmbiguousMatch
        };
        var live = Candidate(Guid.NewGuid(), "Ann Bloggs", ordinal: 0);
        var deleted = Candidate(Guid.NewGuid(), "Zoe Bloggs", ordinal: 1);
        var joinedCsoId = Guid.NewGuid();
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);
        _mockActivityRepo.Setup(r => r.GetMatchCandidatesAsync(item.Id)).ReturnsAsync([live, deleted]);
        var metaverse = new Mock<IMetaverseRepository>();
        metaverse.Setup(r => r.GetExistingMetaverseObjectTypePluralNamesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, string> { [live.CandidateId] = "People" });
        _mockRepository.Setup(r => r.Metaverse).Returns(metaverse.Object);
        var connectedSystems = new Mock<IConnectedSystemRepository>();
        connectedSystems.Setup(r => r.GetExistingConnectedSystemObjectIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([]);
        connectedSystems.Setup(r => r.GetConnectedSystemObjectsCoreByMetaverseObjectIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([new ConnectedSystemObject
            {
                Id = joinedCsoId,
                ConnectedSystemId = 7,
                ConnectedSystem = new ConnectedSystem { Id = 7, Name = "Corporate Directory" },
                Type = new ConnectedSystemObjectType { Name = "user" },
                MetaverseObjectId = live.CandidateId
            }]);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);

        var result = await _controller.GetActivityExecutionItemAsync(item.Id) as OkObjectResult;
        var dto = result?.Value as ActivityRunProfileExecutionItemDetailDto;

        Assert.That(dto, Is.Not.Null);
        var candidates = dto!.MatchCandidates;
        Assert.That(candidates.Select(c => c.CandidateId), Is.EqualTo(new[] { live.CandidateId, deleted.CandidateId }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates[0].Exists, Is.True);
            Assert.That(candidates[0].DisplayNameSnapshot, Is.EqualTo("Ann Bloggs"));
            Assert.That(candidates[0].CandidateKind, Is.EqualTo(MatchCandidateKind.MetaverseObject));
            Assert.That(candidates[0].MatchedValue, Is.EqualTo("48213"));
            Assert.That(candidates[0].ObjectMatchingRuleId, Is.EqualTo(3));
            Assert.That(candidates[0].ObjectMatchingRuleDescriptionSnapshot, Is.EqualTo("Employee ID equals employeeNumber"));
            Assert.That(candidates[0].CandidateTotal, Is.EqualTo(2));
            Assert.That(candidates[0].JoinedObjects.Select(j => j.ConnectedSystemObjectId), Is.EqualTo(new[] { joinedCsoId }));
            Assert.That(candidates[0].JoinedObjects[0].ConnectedSystemName, Is.EqualTo("Corporate Directory"));
            Assert.That(candidates[1].Exists, Is.False);
            Assert.That(candidates[1].JoinedObjects, Is.Empty);
        }
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_ItemWithAnErrorThatRecordsNoCandidates_DoesNotAskForThemAsync()
    {
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ErrorType = ActivityRunProfileExecutionItemErrorType.ObjectMatchingRuleFailed
        };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);

        var result = await _controller.GetActivityExecutionItemAsync(item.Id) as OkObjectResult;

        Assert.That((result?.Value as ActivityRunProfileExecutionItemDetailDto)?.MatchCandidates, Is.Empty);
        _mockActivityRepo.Verify(r => r.GetMatchCandidatesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_JoinedByMatching_ReturnsTheRuleAndValueItMatchedOnAsync()
    {
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Joined
        };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);
        _mockActivityRepo.Setup(r => r.GetJoinedOutcomeAsync(item.Id)).ReturnsAsync(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Joined,
            ObjectMatchingRuleId = 3,
            ObjectMatchingRuleDescriptionSnapshot = "Employee ID equals employeeNumber",
            MatchedValue = "48213"
        });

        var dto = (await _controller.GetActivityExecutionItemAsync(item.Id) as OkObjectResult)?.Value as ActivityRunProfileExecutionItemDetailDto;

        Assert.That(dto, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.JoinObjectMatchingRuleId, Is.EqualTo(3));
            Assert.That(dto.JoinObjectMatchingRuleDescriptionSnapshot, Is.EqualTo("Employee ID equals employeeNumber"));
            Assert.That(dto.JoinMatchedValue, Is.EqualTo("48213"));
        }
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_ItemThatDidNotJoin_DoesNotAskForTheJoinAsync()
    {
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Projected
        };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);

        var dto = (await _controller.GetActivityExecutionItemAsync(item.Id) as OkObjectResult)?.Value as ActivityRunProfileExecutionItemDetailDto;

        Assert.That(dto?.JoinMatchedValue, Is.Null);
        _mockActivityRepo.Verify(r => r.GetJoinedOutcomeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_WithoutInclude_LeavesOutcomesOutAndDoesNotReadThemAsync()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);

        var dto = (await _controller.GetActivityExecutionItemAsync(item.Id) as OkObjectResult)?.Value as ActivityRunProfileExecutionItemDetailDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Outcomes, Is.Null);
        _mockActivityRepo.Verify(r => r.GetSyncOutcomesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_IncludeOutcomes_ReturnsTheTreeNestedAndInOrdinalOrderAsync()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() };
        var projected = Outcome(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, ordinal: 0);
        var flow = Outcome(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, ordinal: 0, parent: projected);
        flow.DetailCount = 12;
        flow.SyncRuleName = "HR Inbound";
        var provisioned = Outcome(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, ordinal: 1, parent: projected);
        var staged = Outcome(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, ordinal: 0, parent: provisioned);
        staged.StagedChangeType = PendingExportChangeType.Create;
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);
        // Read flat and in no particular order, as the query returns them.
        _mockActivityRepo.Setup(r => r.GetSyncOutcomesAsync(item.Id)).ReturnsAsync([staged, provisioned, flow, projected]);

        var dto = (await _controller.GetActivityExecutionItemAsync(item.Id, "outcomes") as OkObjectResult)?.Value as ActivityRunProfileExecutionItemDetailDto;

        Assert.That(dto?.Outcomes, Is.Not.Null);
        var root = dto!.Outcomes!.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
            Assert.That(root.Children.Select(c => c.OutcomeType), Is.EqualTo(new[]
            {
                ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned
            }));
            Assert.That(root.Children[0].DetailCount, Is.EqualTo(12));
            Assert.That(root.Children[0].SyncRuleName, Is.EqualTo("HR Inbound"));
            Assert.That(root.Children[1].Children.Single().StagedChangeType, Is.EqualTo(PendingExportChangeType.Create));
        }
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_IncludeOutcomes_ItemWithNoOutcomes_ReturnsAnEmptyListAsync()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);
        _mockActivityRepo.Setup(r => r.GetSyncOutcomesAsync(item.Id)).ReturnsAsync([]);

        var dto = (await _controller.GetActivityExecutionItemAsync(item.Id, "Outcomes") as OkObjectResult)?.Value as ActivityRunProfileExecutionItemDetailDto;

        Assert.That(dto?.Outcomes, Is.Not.Null.And.Empty, "asked for and none recorded reads as empty, not as left out");
    }

    [TestCase("bogus")]
    [TestCase("outcomes,bogus")]
    public async Task GetActivityExecutionItemAsync_IncludeNamesSomethingUnknown_ReturnsBadRequestAsync(string include)
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);

        var result = await _controller.GetActivityExecutionItemAsync(item.Id, include);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_WithoutInclude_LeavesChangesOutAndDoesNotReadThemAsync()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() };
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);
        var metaverse = new Mock<IMetaverseRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(metaverse.Object);
        var connectedSystems = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);

        var dto = (await _controller.GetActivityExecutionItemAsync(item.Id) as OkObjectResult)?.Value as ActivityRunProfileExecutionItemDetailDto;

        Assert.That(dto?.Changes, Is.Null);
        metaverse.Verify(r => r.GetMvoChangeForExecutionItemAsync(It.IsAny<Guid>()), Times.Never);
        connectedSystems.Verify(r => r.GetCsoChangeForExecutionItemAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_IncludeChanges_ReturnsTheItemsChangesAndEachStagedExportAsync()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() };
        var mvoChange = new MvoChangeHistoryDto { Id = Guid.NewGuid(), ActivityRunProfileExecutionItemId = item.Id };
        var stagedChange = new CsoChangeHistoryDto { Id = Guid.NewGuid() };
        var staged = Outcome(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, ordinal: 0);
        staged.ConnectedSystemObjectChangeId = stagedChange.Id;
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);
        _mockActivityRepo.Setup(r => r.GetSyncOutcomesAsync(item.Id)).ReturnsAsync([staged]);
        var metaverse = new Mock<IMetaverseRepository>();
        metaverse.Setup(r => r.GetMvoChangeForExecutionItemAsync(item.Id)).ReturnsAsync(mvoChange);
        _mockRepository.Setup(r => r.Metaverse).Returns(metaverse.Object);
        var connectedSystems = new Mock<IConnectedSystemRepository>();
        connectedSystems.Setup(r => r.GetCsoChangeForExecutionItemAsync(item.Id)).ReturnsAsync((CsoChangeHistoryDto?)null);
        connectedSystems.Setup(r => r.GetCsoChangesAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { stagedChange.Id }))))
            .ReturnsAsync([stagedChange]);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);

        var dto = (await _controller.GetActivityExecutionItemAsync(item.Id, "changes") as OkObjectResult)?.Value as ActivityRunProfileExecutionItemDetailDto;

        Assert.That(dto?.Changes, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.Changes!.MetaverseObjectChange?.Id, Is.EqualTo(mvoChange.Id));
            Assert.That(dto!.Changes!.ConnectedSystemObjectChange, Is.Null);
            Assert.That(dto!.Changes!.StagedExportChanges.Select(c => c.Id), Is.EqualTo(new[] { stagedChange.Id }));
            Assert.That(dto!.Outcomes, Is.Null, "changes alone does not bring the outcome tree");
        }
    }

    [Test]
    public async Task GetActivityExecutionItemAsync_IncludeOutcomesAndChanges_LinksTheStagedExportToItsChangeAsync()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() };
        var stagedChange = new CsoChangeHistoryDto { Id = Guid.NewGuid() };
        var staged = Outcome(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, ordinal: 0);
        staged.ConnectedSystemObjectChangeId = stagedChange.Id;
        _mockActivityRepo.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(item.Id)).ReturnsAsync(item);
        _mockActivityRepo.Setup(r => r.GetSyncOutcomesAsync(item.Id)).ReturnsAsync([staged]);
        var metaverse = new Mock<IMetaverseRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(metaverse.Object);
        var connectedSystems = new Mock<IConnectedSystemRepository>();
        connectedSystems.Setup(r => r.GetCsoChangesAsync(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([stagedChange]);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);

        var dto = (await _controller.GetActivityExecutionItemAsync(item.Id, "outcomes, changes") as OkObjectResult)?.Value as ActivityRunProfileExecutionItemDetailDto;

        Assert.That(dto?.Outcomes?.Single().ConnectedSystemObjectChangeId, Is.EqualTo(stagedChange.Id));
        Assert.That(dto?.Changes?.StagedExportChanges.Single().Id, Is.EqualTo(stagedChange.Id));
    }

    private static ActivityRunProfileExecutionItemSyncOutcome Outcome(
        ActivityRunProfileExecutionItemSyncOutcomeType type, int ordinal, ActivityRunProfileExecutionItemSyncOutcome? parent = null) => new()
    {
        Id = Guid.NewGuid(),
        OutcomeType = type,
        Ordinal = ordinal,
        ParentSyncOutcomeId = parent?.Id
    };

    private static ActivityRunProfileExecutionItemMatchCandidate Candidate(Guid id, string name, int ordinal) => new()
    {
        CandidateKind = MatchCandidateKind.MetaverseObject,
        CandidateId = id,
        DisplayNameSnapshot = name,
        TypeNameSnapshot = "Person",
        MatchedValue = "48213",
        ObjectMatchingRuleId = 3,
        ObjectMatchingRuleDescriptionSnapshot = "Employee ID equals employeeNumber",
        Ordinal = ordinal,
        CandidateTotal = 2
    };

    #endregion

    #region GetChildActivitiesAsync tests

    [Test]
    public async Task GetChildActivitiesAsync_WithValidParent_ReturnsOkResult()
    {
        var parentId = Guid.NewGuid();
        var parent = new Activity
        {
            Id = parentId,
            TargetName = "Parent Activity",
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete
        };
        var pagedResult = new PagedResultSet<Activity>
        {
            Results = new List<Activity>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(parentId)).ReturnsAsync(parent);
        _mockActivityRepo.Setup(r => r.GetChildActivitiesAsync(parentId, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetChildActivitiesAsync(parentId, pagination);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetChildActivitiesAsync_WithChildren_ReturnsPaginatedResponse()
    {
        var parentId = Guid.NewGuid();
        var parent = new Activity
        {
            Id = parentId,
            TargetName = "Parent Activity",
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete
        };
        var child = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "Child Activity",
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete,
            ParentActivityId = parentId
        };
        var pagedResult = new PagedResultSet<Activity>
        {
            Results = new List<Activity> { child },
            TotalResults = 1,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(parentId)).ReturnsAsync(parent);
        _mockActivityRepo.Setup(r => r.GetChildActivitiesAsync(parentId, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetChildActivitiesAsync(parentId, pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ActivityHeader>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.TotalCount, Is.EqualTo(1));
        Assert.That(response!.Page, Is.EqualTo(1));
        Assert.That(response!.PageSize, Is.EqualTo(20));
        Assert.That(response!.Items.Count(), Is.EqualTo(1));
        Assert.That(response!.Items.Single().TargetName, Is.EqualTo("Child Activity"));
    }

    [Test]
    public async Task GetChildActivitiesAsync_ChildWithChildren_ReportsItsChildActivityCountAsync()
    {
        // #2346: a child Activity can have children of its own, which the client walks next.
        var parentId = Guid.NewGuid();
        var parent = new Activity { Id = parentId, TargetName = "Parent", TargetType = ActivityTargetType.PasswordSynchronisation, Created = DateTime.UtcNow, Status = ActivityStatus.Complete };
        var child = new Activity { Id = Guid.NewGuid(), TargetName = "Child", TargetType = ActivityTargetType.PasswordSynchronisation, Created = DateTime.UtcNow, Status = ActivityStatus.Complete, ParentActivityId = parentId };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(parentId)).ReturnsAsync(parent);
        _mockActivityRepo.Setup(r => r.GetChildActivitiesAsync(parentId, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new PagedResultSet<Activity> { Results = new List<Activity> { child }, TotalResults = 1, CurrentPage = 1, PageSize = 20 });
        _mockActivityRepo.Setup(r => r.GetChildActivityCountsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, int> { [child.Id] = 2 });

        var result = await _controller.GetChildActivitiesAsync(parentId, new PaginationRequest { Page = 1, PageSize = 20 }) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ActivityHeader>;

        Assert.That(response!.Items.Single().ChildActivityCount, Is.EqualTo(2));
    }

    [Test]
    public async Task GetChildActivitiesAsync_WithSecondPage_ReturnsSecondPageContents()
    {
        var parentId = Guid.NewGuid();
        var parent = new Activity
        {
            Id = parentId,
            TargetName = "Parent Activity",
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete
        };
        var childOnPageTwo = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "Second Page Child",
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete,
            ParentActivityId = parentId
        };
        var pagedResult = new PagedResultSet<Activity>
        {
            Results = new List<Activity> { childOnPageTwo },
            TotalResults = 15,
            CurrentPage = 2,
            PageSize = 10
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(parentId)).ReturnsAsync(parent);
        _mockActivityRepo.Setup(r => r.GetChildActivitiesAsync(parentId, 2, 10)).ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 2, PageSize = 10 };
        var result = await _controller.GetChildActivitiesAsync(parentId, pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ActivityHeader>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Page, Is.EqualTo(2));
        Assert.That(response!.TotalCount, Is.EqualTo(15));
        Assert.That(response!.Items.Single().TargetName, Is.EqualTo("Second Page Child"));
        _mockActivityRepo.Verify(r => r.GetChildActivitiesAsync(parentId, 2, 10), Times.Once);
    }

    [Test]
    public async Task GetChildActivitiesAsync_WithNonExistentParent_ReturnsNotFound()
    {
        var parentId = Guid.NewGuid();
        _mockActivityRepo.Setup(r => r.GetActivityAsync(parentId)).ReturnsAsync((Activity?)null);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetChildActivitiesAsync(parentId, pagination);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetChildActivitiesAsync_WithNoChildren_ReturnsEmptyPageNotNotFound()
    {
        var parentId = Guid.NewGuid();
        var parent = new Activity
        {
            Id = parentId,
            TargetName = "Parent Activity",
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Created = DateTime.UtcNow,
            Status = ActivityStatus.Complete
        };
        var pagedResult = new PagedResultSet<Activity>
        {
            Results = new List<Activity>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockActivityRepo.Setup(r => r.GetActivityAsync(parentId)).ReturnsAsync(parent);
        _mockActivityRepo.Setup(r => r.GetChildActivitiesAsync(parentId, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetChildActivitiesAsync(parentId, pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ActivityHeader>;

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.TotalCount, Is.EqualTo(0));
        Assert.That(response!.Items, Is.Empty);
    }

    #endregion

    #region Security audit events (#2108)

    private static Activity FailedAuthenticationActivity(string apiKeyPrefix) => new()
    {
        Id = Guid.CreateVersion7(),
        TargetType = ActivityTargetType.Authentication,
        TargetOperationType = ActivityTargetOperationType.Authenticate,
        TargetName = "Access token authentication failed",
        Message = "Access token authentication failed",
        InitiatedByType = ActivityInitiatorType.Anonymous,
        ApiKeyPrefix = apiKeyPrefix,
        ClientIpAddress = "203.0.113.7",
        SecurityEventReason = "Access token signature invalid",
        AggregationWindowStart = new DateTime(2026, 10, 11, 20, 15, 0, DateTimeKind.Utc),
        FirstSeen = new DateTime(2026, 10, 11, 20, 16, 1, DateTimeKind.Utc),
        LastSeen = new DateTime(2026, 10, 11, 20, 29, 42, DateTimeKind.Utc),
        AttemptCount = 500
    };

    private static Activity ScimRequestsActivity() => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ScimRequests,
        TargetOperationType = ActivityTargetOperationType.Receive,
        TargetName = "Entra provisioning",
        ConnectedSystemId = 7,
        Status = ActivityStatus.CompleteWithWarning,
        WarningMessage = "Latest rejection: 409 uniqueness on POST /Users",
        AggregationWindowStart = new DateTime(2026, 10, 20, 9, 15, 0, DateTimeKind.Utc),
        FirstSeen = new DateTime(2026, 10, 20, 9, 16, 0, DateTimeKind.Utc),
        LastSeen = new DateTime(2026, 10, 20, 9, 29, 0, DateTimeKind.Utc),
        TotalAdded = 120,
        TotalUpdated = 30,
        TotalDeleted = 2,
        TotalErrors = 4
    };

    [Test]
    public void ActivityDetailDto_FromEntity_ScimRequests_CarriesTheCounts()
    {
        var dto = ActivityDetailDto.FromEntity(ScimRequestsActivity());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.ScimRequests, Is.Not.Null);
            Assert.That((dto.ScimRequests!.Created, dto.ScimRequests.Updated, dto.ScimRequests.Deleted, dto.ScimRequests.Rejected),
                Is.EqualTo((120, 30, 2, 4)));
            Assert.That(dto.AggregationWindowStart, Is.EqualTo(new DateTime(2026, 10, 20, 9, 15, 0, DateTimeKind.Utc)));
        }
    }

    [Test]
    public void ActivityHeader_FromEntity_ScimRequests_CarriesTheCountsAndOtherActivitiesNone()
    {
        var scim = ActivityHeader.FromEntity(ScimRequestsActivity());
        var other = ActivityHeader.FromEntity(FailedAuthenticationActivity(apiKeyPrefix: string.Empty));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scim.ScimRequests!.Rejected, Is.EqualTo(4));
            Assert.That(other.ScimRequests, Is.Null);
        }
    }

    [Test]
    public void ActivityDetailDto_FromEntity_Import_CarriesTheWatermarksItReadBetween()
    {
        var import = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            ImportWatermarkFrom = "{\"changeSequence\":8}",
            ImportWatermarkTo = "{\"changeSequence\":10}"
        };

        var dto = ActivityDetailDto.FromEntity(import);

        Assert.That((dto.ImportWatermarkFrom, dto.ImportWatermarkTo), Is.EqualTo(("{\"changeSequence\":8}", "{\"changeSequence\":10}")));
    }

    [Test]
    public void ActivityDetailDto_FromEntity_FailedAuthentication_CarriesTheAuditDetails()
    {
        // Read parity: the portal shows why an attempt failed, where it came from and how often. A SIEM polling the
        // REST API, or Get-JIMActivity, has to be able to tell a 500-attempt forged-token spray from one expired token.
        var dto = ActivityDetailDto.FromEntity(FailedAuthenticationActivity(apiKeyPrefix: string.Empty));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.SecurityEventReason, Is.EqualTo("Access token signature invalid"));
            Assert.That(dto.ClientIpAddress, Is.EqualTo("203.0.113.7"));
            Assert.That(dto.AttemptCount, Is.EqualTo(500));
            Assert.That(dto.FirstSeen, Is.EqualTo(new DateTime(2026, 10, 11, 20, 16, 1, DateTimeKind.Utc)));
            Assert.That(dto.LastSeen, Is.EqualTo(new DateTime(2026, 10, 11, 20, 29, 42, DateTimeKind.Utc)));
            Assert.That(dto.AggregationWindowStart, Is.EqualTo(new DateTime(2026, 10, 11, 20, 15, 0, DateTimeKind.Utc)));
            // Stored as "" so the aggregation index can dedupe; "no key involved" reads as null on the wire.
            Assert.That(dto.ApiKeyPrefix, Is.Null);
        }
    }

    [Test]
    public void ActivityHeader_FromEntity_FailedAuthentication_CarriesTheAuditDetails()
    {
        // The list is what a SIEM polls, so the details must not need a second request per Activity.
        var header = ActivityHeader.FromEntity(FailedAuthenticationActivity(apiKeyPrefix: "jim_ak_7f3a"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.SecurityEventReason, Is.EqualTo("Access token signature invalid"));
            Assert.That(header.ClientIpAddress, Is.EqualTo("203.0.113.7"));
            Assert.That(header.ApiKeyPrefix, Is.EqualTo("jim_ak_7f3a"));
            Assert.That(header.AttemptCount, Is.EqualTo(500));
            Assert.That(header.FirstSeen, Is.EqualTo(new DateTime(2026, 10, 11, 20, 16, 1, DateTimeKind.Utc)));
            Assert.That(header.LastSeen, Is.EqualTo(new DateTime(2026, 10, 11, 20, 29, 42, DateTimeKind.Utc)));
            Assert.That(header.AggregationWindowStart, Is.EqualTo(new DateTime(2026, 10, 11, 20, 15, 0, DateTimeKind.Utc)));
        }
    }

    [Test]
    public void ActivityHeader_FromEntity_NotAnAuthenticationEvent_LeavesTheAuditDetailsNull()
    {
        var header = ActivityHeader.FromEntity(new Activity
        {
            Id = Guid.CreateVersion7(),
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.SecurityEventReason, Is.Null);
            Assert.That(header.ClientIpAddress, Is.Null);
            Assert.That(header.ApiKeyPrefix, Is.Null);
            Assert.That(header.AttemptCount, Is.Null);
            Assert.That(header.FirstSeen, Is.Null);
            Assert.That(header.LastSeen, Is.Null);
            Assert.That(header.AggregationWindowStart, Is.Null);
        }
    }

    #endregion

    #region Preview provenance (#134)

    [Test]
    public void ActivityDetailDto_FromEntity_ChangeInformedByAPreview_CarriesThePreviewsActivityId()
    {
        // Read parity: the portal says "Informed by a preview" on the change's Activity page, and a script that recorded
        // the preview with -PreviewActivityId has to be able to read the link back to audit it.
        var previewActivityId = Guid.CreateVersion7();
        var activity = new Activity
        {
            Id = Guid.CreateVersion7(),
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Deprovision,
            PreviewActivityId = previewActivityId
        };

        var dto = ActivityDetailDto.FromEntity(activity);

        Assert.That(dto.PreviewActivityId, Is.EqualTo(previewActivityId));
    }

    [Test]
    public void ActivityDetailDto_FromEntity_ChangeMadeWithoutAPreview_LeavesThePreviewNull()
    {
        var activity = new Activity
        {
            Id = Guid.CreateVersion7(),
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Deprovision
        };

        var dto = ActivityDetailDto.FromEntity(activity);

        Assert.That(dto.PreviewActivityId, Is.Null);
    }

    [Test]
    public void ActivityDetailDto_FromEntity_ChangeMadeOnAnOutOfDatePreview_CarriesWhenAndWhatOvertookIt()
    {
        // Read parity for #2022: the portal says the preview was out of date when the change was made, and a script
        // auditing the change has to be able to read the same.
        var overtakenAt = new DateTime(2026, 10, 8, 10, 42, 0, DateTimeKind.Utc);
        var activity = new Activity
        {
            Id = Guid.CreateVersion7(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            PreviewActivityId = Guid.CreateVersion7(),
            PreviewOvertakenAt = overtakenAt,
            PreviewOvertakenBy = "Run Profile 'Delta Import' ran on Connected System 'HR Import'"
        };

        var dto = ActivityDetailDto.FromEntity(activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.PreviewOvertakenAt, Is.EqualTo(overtakenAt));
            Assert.That(dto.PreviewOvertakenBy, Is.EqualTo(activity.PreviewOvertakenBy));
        }
    }

    #endregion

    #region Connector Space clear statistics (#1605)

    [Test]
    public void ActivityDetailDto_FromEntity_ClearActivity_CarriesEveryClearStatistic()
    {
        // Read parity: the portal's Clear Summary panel shows all three counts, so the REST API (and the
        // PowerShell module reading it) must expose them too. Before #1605 none of the three were mapped,
        // so a script could not see what a queued clear had actually done.
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Clear,
            ClearedPendingExportCount = 37,
            ClearedConnectedSystemObjectCount = 4212,
            ClearedJoinRecordCount = 4180
        };

        var dto = ActivityDetailDto.FromEntity(activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.ClearedPendingExportCount, Is.EqualTo(37));
            Assert.That(dto.ClearedConnectedSystemObjectCount, Is.EqualTo(4212));
            Assert.That(dto.ClearedJoinRecordCount, Is.EqualTo(4180),
                "the post-clear reconciliation's tracked-object count must reach the API, not just the portal");
        }
    }

    [Test]
    public void ActivityDetailDto_FromEntity_NonClearActivity_LeavesEveryClearStatisticNull()
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Execute
        };

        var dto = ActivityDetailDto.FromEntity(activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.ClearedPendingExportCount, Is.Null);
            Assert.That(dto.ClearedConnectedSystemObjectCount, Is.Null);
            Assert.That(dto.ClearedJoinRecordCount, Is.Null);
        }
    }

    #endregion

    #region Run Profile Safeguards export withheld statistics (#1618)

    [Test]
    public void ActivityDetailDto_FromEntity_ExportActivity_CarriesEveryWithheldCount()
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            ConnectedSystemRunType = ConnectedSystemRunType.Export,
            ExportCreatesWithheld = 2,
            ExportUpdatesWithheld = 0,
            ExportDeletesWithheld = 342
        };

        var dto = ActivityDetailDto.FromEntity(activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.ExportCreatesWithheld, Is.EqualTo(2));
            Assert.That(dto.ExportUpdatesWithheld, Is.EqualTo(0));
            Assert.That(dto.ExportDeletesWithheld, Is.EqualTo(342));
        }
    }

    [Test]
    public void ActivityDetailDto_FromEntity_NonExportActivity_LeavesEveryWithheldCountNull()
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            ConnectedSystemRunType = ConnectedSystemRunType.FullImport
        };

        var dto = ActivityDetailDto.FromEntity(activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.ExportCreatesWithheld, Is.Null);
            Assert.That(dto.ExportUpdatesWithheld, Is.Null);
            Assert.That(dto.ExportDeletesWithheld, Is.Null);
        }
    }

    #endregion

    #region Run Profile Safeguards detected deletions withheld statistic (#1618, Layer 2)

    [Test]
    public void ActivityDetailDto_FromEntity_FullImportActivity_CarriesDetectedDeletionsWithheld()
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            ConnectedSystemRunType = ConnectedSystemRunType.FullImport,
            DetectedDeletionsWithheld = 4120
        };

        var dto = ActivityDetailDto.FromEntity(activity);

        Assert.That(dto.DetectedDeletionsWithheld, Is.EqualTo(4120));
    }

    [Test]
    public void ActivityDetailDto_FromEntity_NonFullImportActivity_LeavesDetectedDeletionsWithheldNull()
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            ConnectedSystemRunType = ConnectedSystemRunType.Export
        };

        var dto = ActivityDetailDto.FromEntity(activity);

        Assert.That(dto.DetectedDeletionsWithheld, Is.Null);
    }

    #endregion
}
