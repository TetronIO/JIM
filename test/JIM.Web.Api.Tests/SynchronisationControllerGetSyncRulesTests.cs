// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging.DTOs;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for SynchronisationController.GetSyncRulesAsync. Verifies the list endpoint reads the
/// lightweight Header tier and honours the Connected System, Direction, Action type, Status and
/// search filters, so the REST surface narrows a Synchronisation Rule list exactly as the portal does.
/// </summary>
[TestFixture]
public class SynchronisationControllerGetSyncRulesTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
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
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockConnectedSystemRepo.StubConfigurationChangeSynchronisationReads();
        _mockMetaverseRepo.StubExportReviewReads();
        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();
        _expressionEvaluator = new DynamicExpressoEvaluator();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(_mockLogger.Object, _application, _expressionEvaluator, _mockCredentialProtection.Object);

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

        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleHeadersAsync(null, null))
            .ReturnsAsync(BuildHeaders());
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    /// <summary>
    /// Four rules spanning both directions, two Connected Systems, all three action types and both
    /// statuses, so every facet has something to include and something to exclude.
    /// </summary>
    private static IList<SyncRuleHeader> BuildHeaders()
    {
        return
        [
            new SyncRuleHeader
            {
                Id = 1, Name = "HR Inbound", ConnectedSystemId = 1, ConnectedSystemName = "HR System",
                ConnectedSystemObjectTypeName = "Employee", MetaverseObjectTypeName = "Person",
                Direction = SyncRuleDirection.Import, ProjectToMetaverse = true, Enabled = true
            },
            new SyncRuleHeader
            {
                Id = 2, Name = "HR Inbound Contractors", ConnectedSystemId = 1, ConnectedSystemName = "HR System",
                ConnectedSystemObjectTypeName = "Contractor", MetaverseObjectTypeName = "Person",
                Direction = SyncRuleDirection.Import, ProjectToMetaverse = false, Enabled = false
            },
            new SyncRuleHeader
            {
                Id = 3, Name = "AD Outbound", ConnectedSystemId = 2, ConnectedSystemName = "Contoso AD",
                ConnectedSystemObjectTypeName = "user", MetaverseObjectTypeName = "Person",
                Direction = SyncRuleDirection.Export, ProvisionToConnectedSystem = true, Enabled = true
            },
            new SyncRuleHeader
            {
                Id = 4, Name = "AD Outbound Groups", ConnectedSystemId = 2, ConnectedSystemName = "Contoso AD",
                ConnectedSystemObjectTypeName = "group", MetaverseObjectTypeName = "Group",
                Direction = SyncRuleDirection.Export, ProvisionToConnectedSystem = false, Enabled = true
            }
        ];
    }

    private static List<int> IdsFrom(IActionResult result)
    {
        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null, "Expected a 200 OK result.");
        var response = ok!.Value as PaginatedResponse<SyncRuleHeader>;
        Assert.That(response, Is.Not.Null, "Expected a paginated Synchronisation Rule header response.");
        return response!.Items.Select(i => i.Id).ToList();
    }

    [Test]
    public async Task GetSyncRulesAsync_NoFilters_ReturnsEveryRuleAsync()
    {
        var result = await _controller.GetSyncRulesAsync(new PaginationRequest(), new SyncRuleFilterRequest());

        Assert.That(IdsFrom(result), Is.EquivalentTo(new[] { 1, 2, 3, 4 }));
    }

    /// <summary>
    /// The list endpoint must read the Header tier rather than materialising every rule's full
    /// object graph; loading Attribute Flows and Object Matching Rules to render a list is wasted work.
    /// </summary>
    [Test]
    public async Task GetSyncRulesAsync_ReadsHeaderTierNotFullEntityGraphAsync()
    {
        await _controller.GetSyncRulesAsync(new PaginationRequest(), new SyncRuleFilterRequest());

        _mockConnectedSystemRepo.Verify(r => r.GetSyncRuleHeadersAsync(null, null), Times.Once);
        _mockConnectedSystemRepo.Verify(r => r.GetSyncRulesAsync(It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// What each rule on the page is waiting on comes from one read per kind of rule, however many rules the page holds.
    /// </summary>
    [Test]
    public async Task GetSyncRulesAsync_ImportAndExportRules_ReportWhatTheyAreWaitingOnWithOneReadEachAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync([new ObjectTypeAwaitingSynchronisation { ConnectedSystemId = 1, ConnectedSystemObjectTypeId = 0, ChangeCount = 3 }]);
        _mockMetaverseRepo.Setup(r => r.GetExportReviewPendingCountsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new Dictionary<int, int> { [0] = 12 });

        var result = await _controller.GetSyncRulesAsync(new PaginationRequest(), new SyncRuleFilterRequest()) as OkObjectResult;
        var rules = ((PaginatedResponse<SyncRuleHeader>)result!.Value!).Items.ToDictionary(rule => rule.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules[1].AwaitingSynchronisationChangeCount, Is.EqualTo(3), "an import rule on the waiting type");
            Assert.That(rules[3].ExportReviewPendingCount, Is.EqualTo(12), "an export rule on a Metaverse Object Type with flagged objects");
            Assert.That(rules[3].AwaitingSynchronisationChangeCount, Is.Zero);
            Assert.That(rules[1].ExportReviewPendingCount, Is.Zero);
            _mockConnectedSystemRepo.Verify(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>()), Times.Once);
            _mockMetaverseRepo.Verify(r => r.GetExportReviewPendingCountsAsync(It.IsAny<IReadOnlyCollection<int>>()), Times.Once);
        }
    }

    [Test]
    public async Task GetSyncRulesAsync_FilteredToExportRules_DoesNotReadChangesAwaitingSynchronisationAsync()
    {
        await _controller.GetSyncRulesAsync(new PaginationRequest(), new SyncRuleFilterRequest { Directions = [SyncRuleDirection.Export] });

        _mockConnectedSystemRepo.Verify(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>()), Times.Never);
    }

    [Test]
    public async Task GetSyncRuleAsync_ImportRuleOfAWaitingType_ReportsHowManyChangesAreWaitingAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(5)).ReturnsAsync(new SyncRule
        {
            Id = 5, Name = "HR Inbound", Direction = SyncRuleDirection.Import, ConnectedSystemId = 1, ConnectedSystemObjectTypeId = 9,
            MetaverseObjectTypeId = 4
        });
        _mockConnectedSystemRepo.Setup(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync([new ObjectTypeAwaitingSynchronisation { ConnectedSystemId = 1, ConnectedSystemObjectTypeId = 9, ChangeCount = 2 }]);

        var result = await _controller.GetSyncRuleAsync(5) as OkObjectResult;

        Assert.That(((SyncRuleHeader)result!.Value!).AwaitingSynchronisationChangeCount, Is.EqualTo(2));
    }

    [Test]
    public async Task GetSyncRulesAsync_ConnectedSystemIdsFilter_ReturnsOnlyThoseSystemsRulesAsync()
    {
        var filter = new SyncRuleFilterRequest { ConnectedSystemIds = [2] };

        var result = await _controller.GetSyncRulesAsync(new PaginationRequest(), filter);

        Assert.That(IdsFrom(result), Is.EquivalentTo(new[] { 3, 4 }));
    }

    [Test]
    public async Task GetSyncRulesAsync_DirectionsFilter_ReturnsOnlyThatDirectionAsync()
    {
        var filter = new SyncRuleFilterRequest { Directions = [SyncRuleDirection.Import] };

        var result = await _controller.GetSyncRulesAsync(new PaginationRequest(), filter);

        Assert.That(IdsFrom(result), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task GetSyncRulesAsync_ActionTypesFilter_ReturnsOnlyThatActionAsync()
    {
        var filter = new SyncRuleFilterRequest { ActionTypes = [SyncRuleActionType.Provisions] };

        var result = await _controller.GetSyncRulesAsync(new PaginationRequest(), filter);

        Assert.That(IdsFrom(result), Is.EquivalentTo(new[] { 3 }));
    }

    [Test]
    public async Task GetSyncRulesAsync_FlowOnlyActionTypeFilter_ReturnsRulesThatNeitherProjectNorProvisionAsync()
    {
        var filter = new SyncRuleFilterRequest { ActionTypes = [SyncRuleActionType.FlowOnly] };

        var result = await _controller.GetSyncRulesAsync(new PaginationRequest(), filter);

        Assert.That(IdsFrom(result), Is.EquivalentTo(new[] { 2, 4 }));
    }

    [Test]
    public async Task GetSyncRulesAsync_StatusesFilter_ReturnsOnlyThatStatusAsync()
    {
        var filter = new SyncRuleFilterRequest { Statuses = [SyncRuleStatus.Disabled] };

        var result = await _controller.GetSyncRulesAsync(new PaginationRequest(), filter);

        Assert.That(IdsFrom(result), Is.EquivalentTo(new[] { 2 }));
    }

    [Test]
    public async Task GetSyncRulesAsync_SearchNarrowsWithinTheFacetResultsAsync()
    {
        var filter = new SyncRuleFilterRequest
        {
            Directions = [SyncRuleDirection.Export],
            Search = "groups"
        };

        var result = await _controller.GetSyncRulesAsync(new PaginationRequest(), filter);

        Assert.That(IdsFrom(result), Is.EquivalentTo(new[] { 4 }));
    }

    [Test]
    public async Task GetSyncRulesAsync_FacetsCombineWithAndAsync()
    {
        var filter = new SyncRuleFilterRequest
        {
            ConnectedSystemIds = [1],
            Statuses = [SyncRuleStatus.Enabled]
        };

        var result = await _controller.GetSyncRulesAsync(new PaginationRequest(), filter);

        Assert.That(IdsFrom(result), Is.EquivalentTo(new[] { 1 }));
    }

    [Test]
    public async Task GetSyncRulesAsync_FilterExcludingEverything_ReturnsAnEmptyPageAsync()
    {
        var filter = new SyncRuleFilterRequest { ConnectedSystemIds = [99] };

        var result = await _controller.GetSyncRulesAsync(new PaginationRequest(), filter);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var response = ok!.Value as PaginatedResponse<SyncRuleHeader>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Items, Is.Empty);
        Assert.That(response!.TotalCount, Is.Zero);
    }
}
