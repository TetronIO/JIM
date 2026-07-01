// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for WorkerTasksController (issue #154, Phase 4).
/// </summary>
[TestFixture]
public class WorkerTasksControllerTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<ILogger<WorkerTasksController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private WorkerTasksController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockTaskingRepo = new Mock<ITaskingRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockLogger = new Mock<ILogger<WorkerTasksController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new WorkerTasksController(_mockLogger.Object, _application);

        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "Administrator")
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    private static List<WorkerTaskHeader> MakeHeaders(int count)
    {
        var headers = new List<WorkerTaskHeader>();
        for (var i = 0; i < count; i++)
        {
            headers.Add(new WorkerTaskHeader
            {
                Id = Guid.NewGuid(),
                Name = $"Task {i}",
                Type = "Synchronisation",
                Timestamp = DateTime.UtcNow,
                Status = WorkerTaskStatus.Queued
            });
        }
        return headers;
    }

    [Test]
    public async Task GetWorkerTasksAsync_ReturnsPaginatedHeadersAsync()
    {
        var headers = MakeHeaders(3);
        _mockTaskingRepo.Setup(r => r.GetWorkerTaskHeadersAsync()).ReturnsAsync(headers);

        var result = await _controller.GetWorkerTasksAsync(page: 1, pageSize: 50);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var response = (PaginatedResponse<WorkerTaskHeader>)((OkObjectResult)result).Value!;
        Assert.That(response.TotalCount, Is.EqualTo(3));
        Assert.That(response.Items, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task GetWorkerTasksAsync_PagesInMemoryAsync()
    {
        var headers = MakeHeaders(5);
        _mockTaskingRepo.Setup(r => r.GetWorkerTaskHeadersAsync()).ReturnsAsync(headers);

        var result = await _controller.GetWorkerTasksAsync(page: 2, pageSize: 2);

        var response = (PaginatedResponse<WorkerTaskHeader>)((OkObjectResult)result).Value!;
        Assert.That(response.TotalCount, Is.EqualTo(5));
        Assert.That(response.Items, Has.Count.EqualTo(2));
        Assert.That(response.Page, Is.EqualTo(2));
    }

    [Test]
    public async Task GetWorkerTaskAsync_Exists_ReturnsOkAsync()
    {
        var headers = MakeHeaders(2);
        var target = headers[1];
        _mockTaskingRepo.Setup(r => r.GetWorkerTaskHeadersAsync()).ReturnsAsync(headers);

        var result = await _controller.GetWorkerTaskAsync(target.Id);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var returned = (WorkerTaskHeader)((OkObjectResult)result).Value!;
        Assert.That(returned.Id, Is.EqualTo(target.Id));
    }

    [Test]
    public async Task GetWorkerTaskAsync_DoesNotExist_ReturnsNotFoundAsync()
    {
        _mockTaskingRepo.Setup(r => r.GetWorkerTaskHeadersAsync()).ReturnsAsync(new List<WorkerTaskHeader>());

        var result = await _controller.GetWorkerTaskAsync(Guid.NewGuid());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CancelWorkerTaskAsync_DoesNotExist_ReturnsNotFoundAsync()
    {
        _mockTaskingRepo.Setup(r => r.GetWorkerTaskHeadersAsync()).ReturnsAsync(new List<WorkerTaskHeader>());

        var result = await _controller.CancelWorkerTaskAsync(Guid.NewGuid());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockTaskingRepo.Verify(r => r.GetWorkerTaskAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public async Task CancelWorkerTaskAsync_QueuedTask_ReturnsAcceptedAndDeletesAsync()
    {
        var taskId = Guid.NewGuid();
        var headers = new List<WorkerTaskHeader>
        {
            new() { Id = taskId, Name = "Test", Type = "Synchronisation", Status = WorkerTaskStatus.Queued }
        };
        var task = new SynchronisationWorkerTask { Id = taskId, Status = WorkerTaskStatus.Queued };

        _mockTaskingRepo.Setup(r => r.GetWorkerTaskHeadersAsync()).ReturnsAsync(headers);
        _mockTaskingRepo.Setup(r => r.GetWorkerTaskAsync(taskId)).ReturnsAsync(task);
        _mockTaskingRepo.Setup(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>())).Returns(Task.CompletedTask);

        var result = await _controller.CancelWorkerTaskAsync(taskId);

        Assert.That(result, Is.InstanceOf<AcceptedResult>());
        _mockTaskingRepo.Verify(r => r.DeleteWorkerTaskAsync(It.Is<WorkerTask>(t => t.Id == taskId)), Times.Once);
    }

    [Test]
    public async Task CancelWorkerTaskAsync_ProcessingTask_ReturnsAcceptedAndMarksCancellationRequestedAsync()
    {
        var taskId = Guid.NewGuid();
        var headers = new List<WorkerTaskHeader>
        {
            new() { Id = taskId, Name = "Test", Type = "Synchronisation", Status = WorkerTaskStatus.Processing }
        };
        var task = new SynchronisationWorkerTask
        {
            Id = taskId,
            Status = WorkerTaskStatus.Processing,
            Activity = new Activity { Id = Guid.NewGuid(), Status = ActivityStatus.InProgress }
        };

        _mockTaskingRepo.Setup(r => r.GetWorkerTaskHeadersAsync()).ReturnsAsync(headers);
        _mockTaskingRepo.Setup(r => r.GetWorkerTaskAsync(taskId)).ReturnsAsync(task);
        _mockTaskingRepo.Setup(r => r.UpdateWorkerTaskAsync(It.IsAny<WorkerTask>())).Returns(Task.CompletedTask);

        var result = await _controller.CancelWorkerTaskAsync(taskId);

        Assert.That(result, Is.InstanceOf<AcceptedResult>());
        _mockTaskingRepo.Verify(r => r.UpdateWorkerTaskAsync(
            It.Is<WorkerTask>(t => t.Id == taskId && t.Status == WorkerTaskStatus.CancellationRequested)), Times.Once);
    }
}
