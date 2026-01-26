using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class HistoryControllerDeletedObjectsTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<ILogger<HistoryController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private HistoryController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockLogger = new Mock<ILogger<HistoryController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new HistoryController(_mockLogger.Object, _application);
    }

    #region GetDeletedCsosAsync tests

    [Test]
    public async Task GetDeletedCsosAsync_ReturnsOkResultAsync()
    {
        SetupEmptyCsoDeletedChanges();
        SetupEmptyConnectedSystemHeaders();

        var result = await _controller.GetDeletedCsosAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetDeletedCsosAsync_WithNoResults_ReturnsEmptyListAsync()
    {
        SetupEmptyCsoDeletedChanges();
        SetupEmptyConnectedSystemHeaders();

        var result = await _controller.GetDeletedCsosAsync() as OkObjectResult;
        var response = result?.Value as DeletedObjectsPagedResponse<DeletedCsoResponse>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Items, Is.Empty);
        Assert.That(response.TotalCount, Is.EqualTo(0));
    }

    [Test]
    public async Task GetDeletedCsosAsync_WithResults_ReturnsMappedItemsAsync()
    {
        var changeId = Guid.NewGuid();
        var changeTime = DateTime.UtcNow.AddHours(-1);
        var csoType = new ConnectedSystemObjectType { Id = 1, Name = "User" };

        var changes = new List<ConnectedSystemObjectChange>
        {
            new()
            {
                Id = changeId,
                ChangeTime = changeTime,
                ChangeType = ObjectChangeType.Deleted,
                ConnectedSystemId = 1,
                DeletedObjectExternalId = "EMP001",
                DeletedObjectDisplayName = "John Smith",
                DeletedObjectType = csoType,
                InitiatedByType = ActivityInitiatorType.ApiKey,
                InitiatedByName = "Integration Key"
            }
        };

        _mockCsRepo.Setup(r => r.GetDeletedCsoChangesAsync(
                It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((changes, 1));

        _mockCsRepo.Setup(r => r.GetConnectedSystemHeadersAsync())
            .ReturnsAsync(new List<ConnectedSystemHeader>
            {
                new() { Id = 1, Name = "HR CSV", ConnectorName = "CSV" }
            });

        var result = await _controller.GetDeletedCsosAsync() as OkObjectResult;
        var response = result?.Value as DeletedObjectsPagedResponse<DeletedCsoResponse>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Items.Count, Is.EqualTo(1));
        Assert.That(response.TotalCount, Is.EqualTo(1));

        var item = response.Items[0];
        Assert.That(item.Id, Is.EqualTo(changeId));
        Assert.That(item.ExternalId, Is.EqualTo("EMP001"));
        Assert.That(item.DisplayName, Is.EqualTo("John Smith"));
        Assert.That(item.ObjectTypeName, Is.EqualTo("User"));
        Assert.That(item.ConnectedSystemId, Is.EqualTo(1));
        Assert.That(item.ConnectedSystemName, Is.EqualTo("HR CSV"));
        Assert.That(item.ChangeTime, Is.EqualTo(changeTime));
        Assert.That(item.InitiatedByType, Is.EqualTo("ApiKey"));
        Assert.That(item.InitiatedByName, Is.EqualTo("Integration Key"));
    }

    [Test]
    public async Task GetDeletedCsosAsync_WithConnectedSystemFilter_PassesFilterToRepositoryAsync()
    {
        SetupEmptyCsoDeletedChanges();
        SetupEmptyConnectedSystemHeaders();

        await _controller.GetDeletedCsosAsync(connectedSystemId: 42);

        _mockCsRepo.Verify(r => r.GetDeletedCsoChangesAsync(
            42, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task GetDeletedCsosAsync_WithExternalIdSearch_PassesSearchToRepositoryAsync()
    {
        SetupEmptyCsoDeletedChanges();
        SetupEmptyConnectedSystemHeaders();

        await _controller.GetDeletedCsosAsync(externalIdSearch: "EMP001");

        _mockCsRepo.Verify(r => r.GetDeletedCsoChangesAsync(
            It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            "EMP001", It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task GetDeletedCsosAsync_WithWhitespaceSearch_PassesNullToRepositoryAsync()
    {
        SetupEmptyCsoDeletedChanges();
        SetupEmptyConnectedSystemHeaders();

        await _controller.GetDeletedCsosAsync(externalIdSearch: "   ");

        _mockCsRepo.Verify(r => r.GetDeletedCsoChangesAsync(
            It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            null, It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task GetDeletedCsosAsync_WithDateRange_PassesDatesToRepositoryAsync()
    {
        SetupEmptyCsoDeletedChanges();
        SetupEmptyConnectedSystemHeaders();

        var fromDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toDate = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        await _controller.GetDeletedCsosAsync(fromDate: fromDate, toDate: toDate);

        _mockCsRepo.Verify(r => r.GetDeletedCsoChangesAsync(
            It.IsAny<int?>(), fromDate, toDate,
            It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task GetDeletedCsosAsync_WithPagination_RespectsPageAndPageSizeAsync()
    {
        SetupEmptyCsoDeletedChanges();
        SetupEmptyConnectedSystemHeaders();

        await _controller.GetDeletedCsosAsync(page: 3, pageSize: 25);

        _mockCsRepo.Verify(r => r.GetDeletedCsoChangesAsync(
            It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), 3, 25), Times.Once);
    }

    [Test]
    public async Task GetDeletedCsosAsync_WithExcessivePageSize_ClampsTo1000Async()
    {
        SetupEmptyCsoDeletedChanges();
        SetupEmptyConnectedSystemHeaders();

        await _controller.GetDeletedCsosAsync(pageSize: 5000);

        _mockCsRepo.Verify(r => r.GetDeletedCsoChangesAsync(
            It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<int>(), 1000), Times.Once);
    }

    [Test]
    public async Task GetDeletedCsosAsync_WithZeroPage_ClampsTo1Async()
    {
        SetupEmptyCsoDeletedChanges();
        SetupEmptyConnectedSystemHeaders();

        await _controller.GetDeletedCsosAsync(page: 0);

        _mockCsRepo.Verify(r => r.GetDeletedCsoChangesAsync(
            It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), 1, It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task GetDeletedCsosAsync_ResolvesConnectedSystemNamesAsync()
    {
        var changes = new List<ConnectedSystemObjectChange>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeTime = DateTime.UtcNow,
                ChangeType = ObjectChangeType.Deleted,
                ConnectedSystemId = 2,
                DeletedObjectExternalId = "EMP002",
                InitiatedByType = ActivityInitiatorType.NotSet
            }
        };

        _mockCsRepo.Setup(r => r.GetDeletedCsoChangesAsync(
                It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((changes, 1));

        _mockCsRepo.Setup(r => r.GetConnectedSystemHeadersAsync())
            .ReturnsAsync(new List<ConnectedSystemHeader>
            {
                new() { Id = 1, Name = "HR CSV", ConnectorName = "CSV" },
                new() { Id = 2, Name = "LDAP Directory", ConnectorName = "LDAP" }
            });

        var result = await _controller.GetDeletedCsosAsync() as OkObjectResult;
        var response = result?.Value as DeletedObjectsPagedResponse<DeletedCsoResponse>;

        Assert.That(response!.Items[0].ConnectedSystemName, Is.EqualTo("LDAP Directory"));
    }

    [Test]
    public async Task GetDeletedCsosAsync_WithUnknownConnectedSystem_ReturnsNullNameAsync()
    {
        var changes = new List<ConnectedSystemObjectChange>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeTime = DateTime.UtcNow,
                ChangeType = ObjectChangeType.Deleted,
                ConnectedSystemId = 999,
                InitiatedByType = ActivityInitiatorType.NotSet
            }
        };

        _mockCsRepo.Setup(r => r.GetDeletedCsoChangesAsync(
                It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((changes, 1));

        SetupEmptyConnectedSystemHeaders();

        var result = await _controller.GetDeletedCsosAsync() as OkObjectResult;
        var response = result?.Value as DeletedObjectsPagedResponse<DeletedCsoResponse>;

        Assert.That(response!.Items[0].ConnectedSystemName, Is.Null);
    }

    #endregion

    #region GetDeletedMvosAsync tests

    [Test]
    public async Task GetDeletedMvosAsync_ReturnsOkResultAsync()
    {
        SetupEmptyMvoDeletedChanges();

        var result = await _controller.GetDeletedMvosAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetDeletedMvosAsync_WithNoResults_ReturnsEmptyListAsync()
    {
        SetupEmptyMvoDeletedChanges();

        var result = await _controller.GetDeletedMvosAsync() as OkObjectResult;
        var response = result?.Value as DeletedObjectsPagedResponse<DeletedMvoResponse>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Items, Is.Empty);
        Assert.That(response.TotalCount, Is.EqualTo(0));
    }

    [Test]
    public async Task GetDeletedMvosAsync_WithResults_ReturnsMappedItemsAsync()
    {
        var changeId = Guid.NewGuid();
        var changeTime = DateTime.UtcNow.AddHours(-2);
        var mvoType = new MetaverseObjectType { Id = 1, Name = "User" };

        var changes = new List<MetaverseObjectChange>
        {
            new()
            {
                Id = changeId,
                ChangeTime = changeTime,
                ChangeType = ObjectChangeType.Deleted,
                DeletedObjectDisplayName = "Jane Doe",
                DeletedObjectType = mvoType,
                DeletedObjectTypeId = 1,
                InitiatedByType = ActivityInitiatorType.User,
                InitiatedByName = "admin"
            }
        };

        _mockMetaverseRepo.Setup(r => r.GetDeletedMvoChangesAsync(
                It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((changes, 1));

        var result = await _controller.GetDeletedMvosAsync() as OkObjectResult;
        var response = result?.Value as DeletedObjectsPagedResponse<DeletedMvoResponse>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Items.Count, Is.EqualTo(1));
        Assert.That(response.TotalCount, Is.EqualTo(1));

        var item = response.Items[0];
        Assert.That(item.Id, Is.EqualTo(changeId));
        Assert.That(item.DisplayName, Is.EqualTo("Jane Doe"));
        Assert.That(item.ObjectTypeName, Is.EqualTo("User"));
        Assert.That(item.ObjectTypeId, Is.EqualTo(1));
        Assert.That(item.ChangeTime, Is.EqualTo(changeTime));
        Assert.That(item.InitiatedByType, Is.EqualTo("User"));
        Assert.That(item.InitiatedByName, Is.EqualTo("admin"));
    }

    [Test]
    public async Task GetDeletedMvosAsync_WithObjectTypeFilter_PassesFilterToRepositoryAsync()
    {
        SetupEmptyMvoDeletedChanges();

        await _controller.GetDeletedMvosAsync(objectTypeId: 5);

        _mockMetaverseRepo.Verify(r => r.GetDeletedMvoChangesAsync(
            5, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task GetDeletedMvosAsync_WithDisplayNameSearch_PassesSearchToRepositoryAsync()
    {
        SetupEmptyMvoDeletedChanges();

        await _controller.GetDeletedMvosAsync(displayNameSearch: "Jane");

        _mockMetaverseRepo.Verify(r => r.GetDeletedMvoChangesAsync(
            It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            "Jane", It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task GetDeletedMvosAsync_WithWhitespaceSearch_PassesNullToRepositoryAsync()
    {
        SetupEmptyMvoDeletedChanges();

        await _controller.GetDeletedMvosAsync(displayNameSearch: "  ");

        _mockMetaverseRepo.Verify(r => r.GetDeletedMvoChangesAsync(
            It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            null, It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task GetDeletedMvosAsync_WithDateRange_PassesDatesToRepositoryAsync()
    {
        SetupEmptyMvoDeletedChanges();

        var fromDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toDate = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        await _controller.GetDeletedMvosAsync(fromDate: fromDate, toDate: toDate);

        _mockMetaverseRepo.Verify(r => r.GetDeletedMvoChangesAsync(
            It.IsAny<int?>(), fromDate, toDate,
            It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task GetDeletedMvosAsync_WithPagination_RespectsPageAndPageSizeAsync()
    {
        SetupEmptyMvoDeletedChanges();

        await _controller.GetDeletedMvosAsync(page: 2, pageSize: 100);

        _mockMetaverseRepo.Verify(r => r.GetDeletedMvoChangesAsync(
            It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), 2, 100), Times.Once);
    }

    [Test]
    public async Task GetDeletedMvosAsync_WithExcessivePageSize_ClampsTo1000Async()
    {
        SetupEmptyMvoDeletedChanges();

        await _controller.GetDeletedMvosAsync(pageSize: 2000);

        _mockMetaverseRepo.Verify(r => r.GetDeletedMvoChangesAsync(
            It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<int>(), 1000), Times.Once);
    }

    [Test]
    public async Task GetDeletedMvosAsync_WithNegativePage_ClampsTo1Async()
    {
        SetupEmptyMvoDeletedChanges();

        await _controller.GetDeletedMvosAsync(page: -1);

        _mockMetaverseRepo.Verify(r => r.GetDeletedMvoChangesAsync(
            It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), 1, It.IsAny<int>()), Times.Once);
    }

    [Test]
    public async Task GetDeletedMvosAsync_MapsInitiatorTypeNotSet_ToUnknownAsync()
    {
        var changes = new List<MetaverseObjectChange>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeTime = DateTime.UtcNow,
                ChangeType = ObjectChangeType.Deleted,
                InitiatedByType = ActivityInitiatorType.NotSet
            }
        };

        _mockMetaverseRepo.Setup(r => r.GetDeletedMvoChangesAsync(
                It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((changes, 1));

        var result = await _controller.GetDeletedMvosAsync() as OkObjectResult;
        var response = result?.Value as DeletedObjectsPagedResponse<DeletedMvoResponse>;

        Assert.That(response!.Items[0].InitiatedByType, Is.EqualTo("Unknown"));
    }

    [Test]
    public async Task GetDeletedMvosAsync_PaginationMetadata_IsCorrectAsync()
    {
        var changes = new List<MetaverseObjectChange>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeTime = DateTime.UtcNow,
                ChangeType = ObjectChangeType.Deleted,
                InitiatedByType = ActivityInitiatorType.NotSet
            }
        };

        _mockMetaverseRepo.Setup(r => r.GetDeletedMvoChangesAsync(
                It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((changes, 150));

        var result = await _controller.GetDeletedMvosAsync(page: 3, pageSize: 25) as OkObjectResult;
        var response = result?.Value as DeletedObjectsPagedResponse<DeletedMvoResponse>;

        Assert.That(response!.Page, Is.EqualTo(3));
        Assert.That(response.PageSize, Is.EqualTo(25));
        Assert.That(response.TotalCount, Is.EqualTo(150));
    }

    #endregion

    #region Setup Helpers

    private void SetupEmptyCsoDeletedChanges()
    {
        _mockCsRepo.Setup(r => r.GetDeletedCsoChangesAsync(
                It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<ConnectedSystemObjectChange>(), 0));
    }

    private void SetupEmptyConnectedSystemHeaders()
    {
        _mockCsRepo.Setup(r => r.GetConnectedSystemHeadersAsync())
            .ReturnsAsync(new List<ConnectedSystemHeader>());
    }

    private void SetupEmptyMvoDeletedChanges()
    {
        _mockMetaverseRepo.Setup(r => r.GetDeletedMvoChangesAsync(
                It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((new List<MetaverseObjectChange>(), 0));
    }

    #endregion
}
