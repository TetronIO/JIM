using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class CsoDetailAndPaginationTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ILogger<SynchronisationController>> _mockLogger = null!;
    private Mock<ICredentialProtectionService> _mockCredentialProtection = null!;
    private IExpressionEvaluator _expressionEvaluator = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();
        _expressionEvaluator = new DynamicExpressoEvaluator();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(_mockLogger.Object, _application, _expressionEvaluator, _mockCredentialProtection.Object);

        var apiKeyId = Guid.NewGuid();
        var testApiKey = new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };

        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(testApiKey);

        var claims = new List<Claim>
        {
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "TestApiKey")
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        var principal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    #region GetConnectedSystemObjectAsync (detail endpoint with CappedMva)

    [Test]
    public async Task GetConnectedSystemObjectAsync_WithValidId_ReturnsOkWithDetailResultAsync()
    {
        var csId = 1;
        var csoId = Guid.NewGuid();
        var cso = CreateTestCso(csoId, csId);
        var detailResult = new CsoDetailResult
        {
            ConnectedSystemObject = cso,
            AttributeValueTotalCounts = new Dictionary<string, int>
            {
                { "member", 10247 },
                { "displayName", 1 }
            }
        };

        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemObjectDetailAsync(csId, csoId, CsoAttributeLoadStrategy.CappedMva))
            .ReturnsAsync(detailResult);

        var result = await _controller.GetConnectedSystemObjectAsync(csId, csoId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var dto = okResult.Value as ConnectedSystemObjectDetailDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Id, Is.EqualTo(csoId));
        Assert.That(dto.AttributeValueSummaries, Is.Not.Null);
        Assert.That(dto.AttributeValueSummaries!.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task GetConnectedSystemObjectAsync_WithInvalidId_ReturnsNotFoundAsync()
    {
        var csId = 1;
        var csoId = Guid.NewGuid();

        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemObjectDetailAsync(csId, csoId, CsoAttributeLoadStrategy.CappedMva))
            .ReturnsAsync((CsoDetailResult?)null);

        var result = await _controller.GetConnectedSystemObjectAsync(csId, csoId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetConnectedSystemObjectAsync_UsesCappedMvaStrategyAsync()
    {
        var csId = 1;
        var csoId = Guid.NewGuid();

        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemObjectDetailAsync(csId, csoId, CsoAttributeLoadStrategy.CappedMva))
            .ReturnsAsync((CsoDetailResult?)null);

        await _controller.GetConnectedSystemObjectAsync(csId, csoId);

        _mockConnectedSystemRepo.Verify(
            r => r.GetConnectedSystemObjectDetailAsync(csId, csoId, CsoAttributeLoadStrategy.CappedMva),
            Times.Once);
    }

    #endregion

    #region GetAttributeValuesPagedAsync (paginated endpoint)

    [Test]
    public async Task GetAttributeValuesPagedAsync_WithValidRequest_ReturnsOkWithPaginatedResponseAsync()
    {
        var csId = 1;
        var csoId = Guid.NewGuid();
        var attributeName = "member";
        var memberAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "member", AttributePlurality = AttributePlurality.MultiValued };

        var values = Enumerable.Range(0, 3).Select(i => new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = memberAttr,
            StringValue = $"CN=User{i}"
        }).ToList();

        _mockConnectedSystemRepo
            .Setup(r => r.GetAttributeValuesPagedAsync(csoId, attributeName, 1, 50, null))
            .ReturnsAsync(new PagedResultSet<ConnectedSystemObjectAttributeValue>
            {
                Results = values,
                TotalResults = 10247,
                CurrentPage = 1,
                PageSize = 50
            });

        var result = await _controller.GetAttributeValuesPagedAsync(csId, csoId, attributeName);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var response = okResult.Value as PaginatedResponse<ConnectedSystemObjectAttributeValueDto>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.TotalCount, Is.EqualTo(10247));
        Assert.That(response.Page, Is.EqualTo(1));
        Assert.That(response.PageSize, Is.EqualTo(50));
        Assert.That(response.Items.Count(), Is.EqualTo(3));
    }

    [Test]
    public async Task GetAttributeValuesPagedAsync_WithSearchText_PassesSearchToRepositoryAsync()
    {
        var csoId = Guid.NewGuid();
        var attributeName = "member";

        _mockConnectedSystemRepo
            .Setup(r => r.GetAttributeValuesPagedAsync(csoId, attributeName, 1, 50, "smith"))
            .ReturnsAsync(new PagedResultSet<ConnectedSystemObjectAttributeValue>
            {
                Results = new List<ConnectedSystemObjectAttributeValue>(),
                TotalResults = 0,
                CurrentPage = 1,
                PageSize = 50
            });

        await _controller.GetAttributeValuesPagedAsync(1, csoId, attributeName, search: "smith");

        _mockConnectedSystemRepo.Verify(
            r => r.GetAttributeValuesPagedAsync(csoId, attributeName, 1, 50, "smith"),
            Times.Once);
    }

    [Test]
    public async Task GetAttributeValuesPagedAsync_WithCustomPagination_PassesCorrectParametersAsync()
    {
        var csoId = Guid.NewGuid();
        var attributeName = "member";

        _mockConnectedSystemRepo
            .Setup(r => r.GetAttributeValuesPagedAsync(csoId, attributeName, 3, 25, null))
            .ReturnsAsync(new PagedResultSet<ConnectedSystemObjectAttributeValue>
            {
                Results = new List<ConnectedSystemObjectAttributeValue>(),
                TotalResults = 0,
                CurrentPage = 3,
                PageSize = 25
            });

        await _controller.GetAttributeValuesPagedAsync(1, csoId, attributeName, page: 3, pageSize: 25);

        _mockConnectedSystemRepo.Verify(
            r => r.GetAttributeValuesPagedAsync(csoId, attributeName, 3, 25, null),
            Times.Once);
    }

    [Test]
    public async Task GetAttributeValuesPagedAsync_WithPageSizeOver100_ClampsTo100Async()
    {
        var csoId = Guid.NewGuid();
        var attributeName = "member";

        _mockConnectedSystemRepo
            .Setup(r => r.GetAttributeValuesPagedAsync(csoId, attributeName, 1, 100, null))
            .ReturnsAsync(new PagedResultSet<ConnectedSystemObjectAttributeValue>
            {
                Results = new List<ConnectedSystemObjectAttributeValue>(),
                TotalResults = 0,
                CurrentPage = 1,
                PageSize = 100
            });

        await _controller.GetAttributeValuesPagedAsync(1, csoId, attributeName, pageSize: 500);

        _mockConnectedSystemRepo.Verify(
            r => r.GetAttributeValuesPagedAsync(csoId, attributeName, 1, 100, null),
            Times.Once);
    }

    [Test]
    public async Task GetAttributeValuesPagedAsync_WithNegativePage_DefaultsTo1Async()
    {
        var csoId = Guid.NewGuid();
        var attributeName = "member";

        _mockConnectedSystemRepo
            .Setup(r => r.GetAttributeValuesPagedAsync(csoId, attributeName, 1, 50, null))
            .ReturnsAsync(new PagedResultSet<ConnectedSystemObjectAttributeValue>
            {
                Results = new List<ConnectedSystemObjectAttributeValue>(),
                TotalResults = 0,
                CurrentPage = 1,
                PageSize = 50
            });

        await _controller.GetAttributeValuesPagedAsync(1, csoId, attributeName, page: -1);

        _mockConnectedSystemRepo.Verify(
            r => r.GetAttributeValuesPagedAsync(csoId, attributeName, 1, 50, null),
            Times.Once);
    }

    #endregion

    #region ConnectedSystemObjectDetailDto.FromDetailResult

    [Test]
    public void FromDetailResult_WithCappedMva_PopulatesAttributeValueSummaries()
    {
        var csoId = Guid.NewGuid();
        var cso = CreateTestCso(csoId, 1);
        var memberAttr = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "member", AttributePlurality = AttributePlurality.MultiValued };

        // Add 5 member values (simulating capped from 10247)
        for (var i = 0; i < 5; i++)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                Attribute = memberAttr,
                StringValue = $"CN=User{i}"
            });
        }

        var detailResult = new CsoDetailResult
        {
            ConnectedSystemObject = cso,
            AttributeValueTotalCounts = new Dictionary<string, int>
            {
                { "displayName", 1 },
                { "member", 10247 }
            }
        };

        var dto = ConnectedSystemObjectDetailDto.FromDetailResult(detailResult);

        Assert.That(dto.AttributeValueSummaries, Is.Not.Null);
        Assert.That(dto.AttributeValueSummaries!.Count, Is.EqualTo(2));

        var memberSummary = dto.AttributeValueSummaries.Single(s => s.AttributeName == "member");
        Assert.That(memberSummary.TotalCount, Is.EqualTo(10247));
        Assert.That(memberSummary.ReturnedCount, Is.EqualTo(5));
        Assert.That(memberSummary.HasMore, Is.True);

        var displayNameSummary = dto.AttributeValueSummaries.Single(s => s.AttributeName == "displayName");
        Assert.That(displayNameSummary.TotalCount, Is.EqualTo(1));
        Assert.That(displayNameSummary.ReturnedCount, Is.EqualTo(1));
        Assert.That(displayNameSummary.HasMore, Is.False);
    }

    [Test]
    public void FromDetailResult_WithEmptyTotalCounts_DoesNotPopulateSummaries()
    {
        var cso = CreateTestCso(Guid.NewGuid(), 1);
        var detailResult = new CsoDetailResult
        {
            ConnectedSystemObject = cso,
            AttributeValueTotalCounts = new Dictionary<string, int>()
        };

        var dto = ConnectedSystemObjectDetailDto.FromDetailResult(detailResult);

        Assert.That(dto.AttributeValueSummaries, Is.Null);
    }

    [Test]
    public void FromDetailResult_MapsEntityPropertiesCorrectly()
    {
        var csoId = Guid.NewGuid();
        var cso = CreateTestCso(csoId, 1);
        var detailResult = new CsoDetailResult
        {
            ConnectedSystemObject = cso,
            AttributeValueTotalCounts = new Dictionary<string, int>()
        };

        var dto = ConnectedSystemObjectDetailDto.FromDetailResult(detailResult);

        Assert.That(dto.Id, Is.EqualTo(csoId));
        Assert.That(dto.ConnectedSystemId, Is.EqualTo(1));
        Assert.That(dto.TypeName, Is.EqualTo("User"));
        Assert.That(dto.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
    }

    [Test]
    public void FromDetailResult_SummariesAreSortedByAttributeName()
    {
        var cso = CreateTestCso(Guid.NewGuid(), 1);
        var detailResult = new CsoDetailResult
        {
            ConnectedSystemObject = cso,
            AttributeValueTotalCounts = new Dictionary<string, int>
            {
                { "zebra", 5 },
                { "alpha", 3 },
                { "member", 100 }
            }
        };

        var dto = ConnectedSystemObjectDetailDto.FromDetailResult(detailResult);

        Assert.That(dto.AttributeValueSummaries, Is.Not.Null);
        var names = dto.AttributeValueSummaries!.Select(s => s.AttributeName).ToList();
        Assert.That(names, Is.EqualTo(new List<string> { "alpha", "member", "zebra" }));
    }

    #endregion

    #region CsoAttributeLoadStrategy enum

    [Test]
    public void CsoAttributeLoadStrategy_HasExpectedValues()
    {
        Assert.That(Enum.IsDefined(typeof(CsoAttributeLoadStrategy), CsoAttributeLoadStrategy.All));
        Assert.That(Enum.IsDefined(typeof(CsoAttributeLoadStrategy), CsoAttributeLoadStrategy.CappedMva));
        Assert.That((int)CsoAttributeLoadStrategy.All, Is.EqualTo(0));
        Assert.That((int)CsoAttributeLoadStrategy.CappedMva, Is.EqualTo(1));
    }

    #endregion

    private ConnectedSystemObject CreateTestCso(Guid id, int connectedSystemId)
    {
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "displayName",
            AttributePlurality = AttributePlurality.SingleValued
        };

        var cso = new ConnectedSystemObject
        {
            Id = id,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "TestCS" },
            Type = new ConnectedSystemObjectType { Id = 1, Name = "User" },
            TypeId = 1,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined,
            Created = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Attribute = displayNameAttr,
                    StringValue = "Test User"
                }
            }
        };

        return cso;
    }
}
