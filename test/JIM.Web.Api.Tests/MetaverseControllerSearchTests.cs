// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Search;
using JIM.Models.Utility;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Predefined Search API endpoint, specifically the IsEnabled filtering behaviour.
/// </summary>
[TestFixture]
public class MetaverseControllerSearchTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<ISearchRepository> _mockSearchRepo = null!;
    private Mock<ILogger<MetaverseController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockSearchRepo = new Mock<ISearchRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Search).Returns(_mockSearchRepo.Object);
        _mockLogger = new Mock<ILogger<MetaverseController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new MetaverseController(_mockLogger.Object, _application);
    }

    #region SearchObjectsAsync - IsEnabled filtering

    [Test]
    public async Task SearchObjectsAsync_DisabledSearch_ReturnsNotFoundAsync()
    {
        var disabledSearch = new PredefinedSearch
        {
            Id = 1,
            Name = "All Users",
            Uri = "users",
            IsEnabled = false,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" }
        };

        _mockSearchRepo
            .Setup(r => r.GetPredefinedSearchAsync("users"))
            .ReturnsAsync(disabledSearch);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.SearchObjectsAsync("users", pagination);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task SearchObjectsAsync_EnabledSearch_ReturnsOkAsync()
    {
        var enabledSearch = new PredefinedSearch
        {
            Id = 1,
            Name = "All Users",
            Uri = "users",
            IsEnabled = true,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" }
        };

        _mockSearchRepo
            .Setup(r => r.GetPredefinedSearchAsync("users"))
            .ReturnsAsync(enabledSearch);

        var pagedResult = new PagedResultSet<MetaverseObjectHeader>
        {
            Results = new List<MetaverseObjectHeader>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockMetaverseRepo
            .Setup(r => r.GetMetaverseObjectHeadersPagedAsync(
                It.IsAny<PredefinedSearch>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.SearchObjectsAsync("users", pagination);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task SearchObjectsAsync_WithUnknownHasAttribute_ReturnsEmptyResultWithoutQueryingAsync()
    {
        var enabledSearch = new PredefinedSearch
        {
            Id = 1,
            Name = "All Users",
            Uri = "users",
            IsEnabled = true,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" }
        };
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync("users")).ReturnsAsync(enabledSearch);
        // The named attribute does not exist.
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((MetaverseAttribute?)null);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.SearchObjectsAsync("users", pagination, hasAttribute: "noSuchAttr");

        // An unrecognised attribute yields an empty page, and the object query is never run.
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var payload = (PaginatedResponse<MetaverseObjectHeaderDto>)((OkObjectResult)result).Value!;
        Assert.That(payload.TotalCount, Is.EqualTo(0));
        Assert.That(payload.Items, Is.Empty);
        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectHeadersPagedAsync(
            It.IsAny<PredefinedSearch>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int?>()), Times.Never);
    }

    [Test]
    public async Task SearchObjectsAsync_WithKnownHasAttribute_PassesResolvedAttributeIdToQueryAsync()
    {
        var enabledSearch = new PredefinedSearch
        {
            Id = 1,
            Name = "All Users",
            Uri = "users",
            IsEnabled = true,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" }
        };
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync("users")).ReturnsAsync(enabledSearch);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new MetaverseAttribute { Id = 42, Name = "costCentre" });

        var pagedResult = new PagedResultSet<MetaverseObjectHeader>
        {
            Results = new List<MetaverseObjectHeader>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockMetaverseRepo
            .Setup(r => r.GetMetaverseObjectHeadersPagedAsync(
                It.IsAny<PredefinedSearch>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.SearchObjectsAsync("users", pagination, hasAttribute: "costCentre");

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        // The resolved attribute id (42) is threaded into the presence-filtered query.
        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectHeadersPagedAsync(
            It.IsAny<PredefinedSearch>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(), 42), Times.Once);
    }

    [Test]
    public async Task SearchObjectsAsync_NonExistentSearch_ReturnsNotFoundAsync()
    {
        _mockSearchRepo
            .Setup(r => r.GetPredefinedSearchAsync("nonexistent"))
            .ReturnsAsync((PredefinedSearch?)null);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.SearchObjectsAsync("nonexistent", pagination);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region PredefinedSearch IsEnabled default value

    [Test]
    public void PredefinedSearch_IsEnabled_DefaultsToTrue()
    {
        var search = new PredefinedSearch();

        Assert.That(search.IsEnabled, Is.True);
    }

    #endregion
}
