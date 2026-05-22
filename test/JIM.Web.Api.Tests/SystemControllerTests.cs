// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Utility;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class SystemControllerTests
{
    private static (SystemController Controller, Mock<ISystemRepository> SystemRepo, Mock<IActivityRepository> ActivityRepo)
        BuildController(int inProgressActivityCount, SystemResetResult? resetResult = null)
    {
        var mockRepository = new Mock<IRepository>();
        var mockActivityRepo = new Mock<IActivityRepository>();
        var mockSystemRepo = new Mock<ISystemRepository>();

        mockActivityRepo.Setup(a => a.GetActivitiesAsync(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<System.Guid?>(),
            It.IsAny<IEnumerable<ActivityTargetOperationType>?>(),
            It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
            It.IsAny<IEnumerable<ActivityTargetType>?>(),
            It.IsAny<IEnumerable<ActivityStatus>?>(),
            It.IsAny<bool?>()))
            .ReturnsAsync(new PagedResultSet<Activity>
            {
                TotalResults = inProgressActivityCount,
                Results = new List<Activity>()
            });

        mockSystemRepo.Setup(s => s.ResetSystemAsync())
            .ReturnsAsync(resetResult ?? new SystemResetResult());

        mockRepository.Setup(r => r.Activity).Returns(mockActivityRepo.Object);
        mockRepository.Setup(r => r.System).Returns(mockSystemRepo.Object);

        var application = new JimApplication(mockRepository.Object);
        var controller = new SystemController(NullLogger<SystemController>.Instance, application)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "test-admin")
                    }, "test"))
                }
            }
        };

        return (controller, mockSystemRepo, mockActivityRepo);
    }

    [Test]
    public async Task ResetAsync_WhenNoActivitiesInProgress_ReturnsOkWithResult()
    {
        var expectedResult = new SystemResetResult
        {
            ConnectedSystemsRemoved = 3,
            MetaverseObjectsRemoved = 42
        };
        var (controller, _, _) = BuildController(inProgressActivityCount: 0, resetResult: expectedResult);

        var result = await controller.ResetAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var ok = (OkObjectResult)result;
        Assert.That(ok.Value, Is.SameAs(expectedResult));
    }

    [Test]
    public async Task ResetAsync_WhenNoActivitiesInProgress_CallsRepositoryReset()
    {
        var (controller, systemRepo, _) = BuildController(inProgressActivityCount: 0);

        await controller.ResetAsync();

        systemRepo.Verify(s => s.ResetSystemAsync(), Times.Once);
    }

    [Test]
    public async Task ResetAsync_WhenActivitiesInProgress_Returns409()
    {
        var (controller, _, _) = BuildController(inProgressActivityCount: 2);

        var result = await controller.ResetAsync();

        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
        var conflict = (ConflictObjectResult)result;
        Assert.That(conflict.Value, Is.InstanceOf<ApiErrorResponse>());
    }

    [Test]
    public async Task ResetAsync_WhenActivitiesInProgress_DoesNotCallRepositoryReset()
    {
        var (controller, systemRepo, _) = BuildController(inProgressActivityCount: 1);

        await controller.ResetAsync();

        systemRepo.Verify(s => s.ResetSystemAsync(), Times.Never);
    }

    [Test]
    public async Task ResetAsync_FiltersActivityCheckToInProgressStatus()
    {
        var (controller, _, activityRepo) = BuildController(inProgressActivityCount: 0);

        await controller.ResetAsync();

        activityRepo.Verify(a => a.GetActivitiesAsync(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<System.Guid?>(),
            It.IsAny<IEnumerable<ActivityTargetOperationType>?>(),
            It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
            It.IsAny<IEnumerable<ActivityTargetType>?>(),
            It.Is<IEnumerable<ActivityStatus>?>(s => s != null && System.Linq.Enumerable.Contains(s, ActivityStatus.InProgress)),
            It.IsAny<bool?>()), Times.Once);
    }
}
