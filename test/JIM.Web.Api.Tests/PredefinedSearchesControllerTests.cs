// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Search.DTOs;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the admin Predefined Searches management API.
/// </summary>
[TestFixture]
public class PredefinedSearchesControllerTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISearchRepository> _mockSearchRepo = null!;
    private Mock<ILogger<PredefinedSearchesController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private PredefinedSearchesController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSearchRepo = new Mock<ISearchRepository>();
        _mockRepository.Setup(r => r.Search).Returns(_mockSearchRepo.Object);
        _mockLogger = new Mock<ILogger<PredefinedSearchesController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new PredefinedSearchesController(_mockLogger.Object, _application);
    }

    private static PredefinedSearch BuildSearch(int id, bool isEnabled) => new()
    {
        Id = id,
        Name = "People",
        Uri = "people",
        IsEnabled = isEnabled,
        MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" }
    };

    [Test]
    public async Task GetAllAsync_ReturnsAllHeadersIncludingDisabledAsync()
    {
        var headers = new List<PredefinedSearchHeader>
        {
            new() { Id = 1, Name = "People", Uri = "people", IsEnabled = true, MetaverseObjectTypeName = "Person" },
            new() { Id = 2, Name = "Security Groups", Uri = "security-groups", IsEnabled = false, MetaverseObjectTypeName = "Group" }
        };
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchHeadersAsync()).ReturnsAsync(headers);

        var result = await _controller.GetAllAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        Assert.That(okResult.Value, Is.InstanceOf<IList<PredefinedSearchHeader>>());
        var value = (IList<PredefinedSearchHeader>)okResult.Value!;
        Assert.That(value.Count, Is.EqualTo(2));
        Assert.That(value.Any(h => !h.IsEnabled), Is.True, "Disabled searches must be included for admin discovery.");
    }

    [Test]
    public async Task UpdateAsync_WithIsEnabledTrue_MutatesAndPersistsEntityAsync()
    {
        var existing = BuildSearch(42, isEnabled: false);
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(42)).ReturnsAsync(existing);

        var result = await _controller.UpdateAsync(42, new UpdatePredefinedSearchRequest { IsEnabled = true });

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(existing.IsEnabled, Is.True);
        _mockSearchRepo.Verify(r => r.UpdatePredefinedSearchAsync(existing), Times.Once);
    }

    [Test]
    public async Task UpdateAsync_WithIsEnabledFalse_MutatesAndPersistsEntityAsync()
    {
        var existing = BuildSearch(42, isEnabled: true);
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(42)).ReturnsAsync(existing);

        var result = await _controller.UpdateAsync(42, new UpdatePredefinedSearchRequest { IsEnabled = false });

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(existing.IsEnabled, Is.False);
        _mockSearchRepo.Verify(r => r.UpdatePredefinedSearchAsync(existing), Times.Once);
    }

    [Test]
    public async Task UpdateAsync_WithNullFields_LeavesEntityUntouchedAsync()
    {
        var existing = BuildSearch(42, isEnabled: true);
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(42)).ReturnsAsync(existing);

        var result = await _controller.UpdateAsync(42, new UpdatePredefinedSearchRequest());

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(existing.IsEnabled, Is.True, "IsEnabled was not provided in the request and must not be mutated.");
        _mockSearchRepo.Verify(r => r.UpdatePredefinedSearchAsync(existing), Times.Once);
    }

    [Test]
    public async Task UpdateAsync_WithUnknownId_ReturnsNotFoundAndDoesNotSaveAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(999)).ReturnsAsync((PredefinedSearch?)null);

        var result = await _controller.UpdateAsync(999, new UpdatePredefinedSearchRequest { IsEnabled = true });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockSearchRepo.Verify(r => r.UpdatePredefinedSearchAsync(It.IsAny<PredefinedSearch>()), Times.Never);
    }
}
