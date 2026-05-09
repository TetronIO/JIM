// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Utility;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class MetaverseControllerObjectsTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<ILogger<MetaverseController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockLogger = new Mock<ILogger<MetaverseController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new MetaverseController(_mockLogger.Object, _application);
    }

    #region GetObjectsAsync tests

    [Test]
    public async Task GetObjectsAsync_ReturnsOkResult()
    {
        var pagedResult = new PagedResultSet<MetaverseObjectHeader>
        {
            Results = new List<MetaverseObjectHeader>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetObjectsAsync(pagination);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetObjectsAsync_WithObjects_ReturnsPaginatedResponse()
    {
        var displayNameAttr = new MetaverseAttribute { Id = 1, Name = Constants.BuiltInAttributes.DisplayName };
        var header = new MetaverseObjectHeader
        {
            Id = Guid.NewGuid(),
            Created = DateTime.UtcNow,
            TypeId = 1,
            TypeName = "User",
            Status = MetaverseObjectStatus.Normal,
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new MetaverseObjectAttributeValue
                {
                    Attribute = displayNameAttr,
                    StringValue = "John Doe"
                }
            }
        };
        var pagedResult = new PagedResultSet<MetaverseObjectHeader>
        {
            Results = new List<MetaverseObjectHeader> { header },
            TotalResults = 1,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetObjectsAsync(pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<MetaverseObjectHeaderDto>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.TotalCount, Is.EqualTo(1));
        Assert.That(response.Items.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task GetObjectsAsync_WithObjectTypeFilter_PassesTypeIdToRepository()
    {
        var pagedResult = new PagedResultSet<MetaverseObjectHeader>
        {
            Results = new List<MetaverseObjectHeader>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        await _controller.GetObjectsAsync(pagination, objectTypeId: 5);

        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectsAsync(1, 20, 5, It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Test]
    public async Task GetObjectsAsync_WithSearch_PassesSearchToRepository()
    {
        var pagedResult = new PagedResultSet<MetaverseObjectHeader>
        {
            Results = new List<MetaverseObjectHeader>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        await _controller.GetObjectsAsync(pagination, search: "john");

        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectsAsync(1, 20, It.IsAny<int?>(), "john", It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Test]
    public async Task GetObjectsAsync_WithPagination_RespectsPageAndPageSize()
    {
        var pagedResult = new PagedResultSet<MetaverseObjectHeader>
        {
            Results = new List<MetaverseObjectHeader>(),
            TotalResults = 100,
            CurrentPage = 3,
            PageSize = 15
        };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 3, PageSize = 15 };
        var result = await _controller.GetObjectsAsync(pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<MetaverseObjectHeaderDto>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Page, Is.EqualTo(3));
        Assert.That(response.PageSize, Is.EqualTo(15));
    }

    [Test]
    public async Task GetObjectsAsync_MapsHeaderFieldsCorrectly()
    {
        var objectId = Guid.NewGuid();
        var created = DateTime.UtcNow.AddDays(-1);

        // Create attributes - DisplayName goes to dedicated property, others to Attributes dictionary
        var displayNameAttr = new MetaverseAttribute { Id = 1, Name = Constants.BuiltInAttributes.DisplayName };
        var firstNameAttr = new MetaverseAttribute { Id = 2, Name = Constants.BuiltInAttributes.FirstName };
        var lastNameAttr = new MetaverseAttribute { Id = 3, Name = Constants.BuiltInAttributes.LastName };
        var emailAttr = new MetaverseAttribute { Id = 4, Name = Constants.BuiltInAttributes.Email };

        var header = new MetaverseObjectHeader
        {
            Id = objectId,
            Created = created,
            TypeId = 2,
            TypeName = "Group",
            Status = MetaverseObjectStatus.Obsolete,
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new MetaverseObjectAttributeValue { Attribute = displayNameAttr, StringValue = "Test Group" },
                new MetaverseObjectAttributeValue { Attribute = firstNameAttr, StringValue = "First" },
                new MetaverseObjectAttributeValue { Attribute = lastNameAttr, StringValue = "Last" },
                new MetaverseObjectAttributeValue { Attribute = emailAttr, StringValue = "group@test.com" }
            }
        };
        var pagedResult = new PagedResultSet<MetaverseObjectHeader>
        {
            Results = new List<MetaverseObjectHeader> { header },
            TotalResults = 1,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetObjectsAsync(pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<MetaverseObjectHeaderDto>;
        var dto = response?.Items.First();

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Id, Is.EqualTo(objectId));
        Assert.That(dto.Created, Is.EqualTo(created));
        Assert.That(dto.TypeId, Is.EqualTo(2));
        Assert.That(dto.TypeName, Is.EqualTo("Group"));
        Assert.That(dto.Status, Is.EqualTo(MetaverseObjectStatus.Obsolete));
        Assert.That(dto.DisplayName, Is.EqualTo("Test Group"));
        // Additional attributes are in the Attributes dictionary (DisplayName is excluded as it has its own property)
        Assert.That(dto.Attributes, Contains.Key(Constants.BuiltInAttributes.FirstName));
        Assert.That(dto.Attributes[Constants.BuiltInAttributes.FirstName], Is.EqualTo("First"));
        Assert.That(dto.Attributes, Contains.Key(Constants.BuiltInAttributes.LastName));
        Assert.That(dto.Attributes[Constants.BuiltInAttributes.LastName], Is.EqualTo("Last"));
        Assert.That(dto.Attributes, Contains.Key(Constants.BuiltInAttributes.Email));
        Assert.That(dto.Attributes[Constants.BuiltInAttributes.Email], Is.EqualTo("group@test.com"));
    }

    [Test]
    public async Task GetObjectsAsync_WithEmptyResults_ReturnsEmptyList()
    {
        var pagedResult = new PagedResultSet<MetaverseObjectHeader>
        {
            Results = new List<MetaverseObjectHeader>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var result = await _controller.GetObjectsAsync(pagination) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<MetaverseObjectHeaderDto>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Items.Count(), Is.EqualTo(0));
        Assert.That(response.TotalCount, Is.EqualTo(0));
    }

    [Test]
    public async Task GetObjectsAsync_WithTypeAndSearch_PassesBothToRepository()
    {
        var pagedResult = new PagedResultSet<MetaverseObjectHeader>
        {
            Results = new List<MetaverseObjectHeader>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        await _controller.GetObjectsAsync(pagination, objectTypeId: 3, search: "admin");

        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectsAsync(1, 20, 3, "admin", It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Test]
    public async Task GetObjectsAsync_WithAttributes_PassesAttributesToRepository()
    {
        var pagedResult = new PagedResultSet<MetaverseObjectHeader>
        {
            Results = new List<MetaverseObjectHeader>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var requestedAttributes = new[] { "FirstName", "LastName", "Email" };
        await _controller.GetObjectsAsync(pagination, attributes: requestedAttributes);

        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectsAsync(
            1, 20, It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.Is<IEnumerable<string>?>(attrs => attrs != null && attrs.Contains("FirstName") && attrs.Contains("LastName") && attrs.Contains("Email")),
            It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Test]
    public async Task GetObjectsAsync_WithWildcardAttribute_PassesWildcardToRepository()
    {
        var pagedResult = new PagedResultSet<MetaverseObjectHeader>
        {
            Results = new List<MetaverseObjectHeader>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(pagedResult);

        var pagination = new PaginationRequest { Page = 1, PageSize = 20 };
        var requestedAttributes = new[] { "*" };
        await _controller.GetObjectsAsync(pagination, attributes: requestedAttributes);

        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectsAsync(
            1, 20, It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.Is<IEnumerable<string>?>(attrs => attrs != null && attrs.Contains("*")),
            It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
    }

    #endregion

    #region GetObjectsCountAsync tests

    [Test]
    public async Task GetObjectsCountAsync_ReturnsOkWithIntAsync()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsCountAsync(
                It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(42);

        var result = await _controller.GetObjectsCountAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        Assert.That(okResult.Value, Is.EqualTo(42));
    }

    [Test]
    public async Task GetObjectsCountAsync_WithObjectTypeFilter_PassesTypeIdToRepositoryAsync()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsCountAsync(
                It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(10);

        await _controller.GetObjectsCountAsync(objectTypeId: 5);

        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectsCountAsync(
            5, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Test]
    public async Task GetObjectsCountAsync_WithSearch_PassesSearchToRepositoryAsync()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsCountAsync(
                It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(3);

        await _controller.GetObjectsCountAsync(search: "john");

        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectsCountAsync(
            It.IsAny<int?>(), "john", It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Test]
    public async Task GetObjectsCountAsync_WithAttributeFilter_PassesFilterToRepositoryAsync()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsCountAsync(
                It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(7);

        await _controller.GetObjectsCountAsync(filterAttributeName: "Department", filterAttributeValue: "IT");

        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectsCountAsync(
            It.IsAny<int?>(), It.IsAny<string?>(), "Department", "IT"), Times.Once);
    }

    [Test]
    public async Task GetObjectsCountAsync_NoFilters_ReturnsZeroForEmptyMetaverseAsync()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectsCountAsync(
                It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(0);

        var result = await _controller.GetObjectsCountAsync() as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.EqualTo(0));
    }

    #endregion

    #region GetObjectChangeHistoryAsync tests

    [Test]
    public async Task GetObjectChangeHistoryAsync_UnknownId_ReturnsNotFoundAsync()
    {
        var id = Guid.NewGuid();
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectHeaderAsync(id))
            .ReturnsAsync((MetaverseObjectHeader?)null);

        var result = await _controller.GetObjectChangeHistoryAsync(id, new PaginationRequest { Page = 1, PageSize = 50 });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockMetaverseRepo.Verify(r => r.GetMvoChangeHistoryAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task GetObjectChangeHistoryAsync_KnownId_ReturnsPaginatedResponseAsync()
    {
        var id = Guid.NewGuid();
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectHeaderAsync(id))
            .ReturnsAsync(new MetaverseObjectHeader { Id = id, TypeId = 1, TypeName = "User", TypePluralName = "Users" });

        var dtoRows = new List<MvoChangeHistoryDto>
        {
            new MvoChangeHistoryDto { Id = Guid.NewGuid(), ChangeTime = DateTime.UtcNow, InitiatedByName = "Admin" },
            new MvoChangeHistoryDto { Id = Guid.NewGuid(), ChangeTime = DateTime.UtcNow.AddMinutes(-1), InitiatedByName = "Sync Engine" }
        };
        _mockMetaverseRepo.Setup(r => r.GetMvoChangeHistoryAsync(id, 1, 50))
            .ReturnsAsync((dtoRows, 2));

        var result = await _controller.GetObjectChangeHistoryAsync(id, new PaginationRequest { Page = 1, PageSize = 50 }) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<MvoChangeHistoryDto>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Items.Count(), Is.EqualTo(2));
        Assert.That(response.TotalCount, Is.EqualTo(2));
        Assert.That(response.Page, Is.EqualTo(1));
        Assert.That(response.PageSize, Is.EqualTo(50));
    }

    [Test]
    public async Task GetObjectChangeHistoryAsync_PassesPaginationToRepositoryAsync()
    {
        var id = Guid.NewGuid();
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectHeaderAsync(id))
            .ReturnsAsync(new MetaverseObjectHeader { Id = id, TypeId = 1, TypeName = "User", TypePluralName = "Users" });
        _mockMetaverseRepo.Setup(r => r.GetMvoChangeHistoryAsync(id, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<MvoChangeHistoryDto>(), 0));

        await _controller.GetObjectChangeHistoryAsync(id, new PaginationRequest { Page = 3, PageSize = 25 });

        _mockMetaverseRepo.Verify(r => r.GetMvoChangeHistoryAsync(id, 3, 25), Times.Once);
    }

    [Test]
    public async Task GetObjectChangeHistoryAsync_EmptyHistory_ReturnsZeroTotalCountAsync()
    {
        var id = Guid.NewGuid();
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectHeaderAsync(id))
            .ReturnsAsync(new MetaverseObjectHeader { Id = id, TypeId = 1, TypeName = "User", TypePluralName = "Users" });
        _mockMetaverseRepo.Setup(r => r.GetMvoChangeHistoryAsync(id, 1, 50))
            .ReturnsAsync((new List<MvoChangeHistoryDto>(), 0));

        var result = await _controller.GetObjectChangeHistoryAsync(id, new PaginationRequest { Page = 1, PageSize = 50 }) as OkObjectResult;
        var response = result?.Value as PaginatedResponse<MvoChangeHistoryDto>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.TotalCount, Is.EqualTo(0));
        Assert.That(response.Items.Count(), Is.EqualTo(0));
    }

    #endregion
}
