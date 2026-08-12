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
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for SynchronisationController.GetDataFlowsAsync (#1199): the REST surface of the system-wide Data Flow view.
/// Verifies that query-string filters reach the application layer intact, and that a flow's direction-specific detail
/// (priority and "Null is a value" inbound, Enforce State outbound) survives to the response.
/// </summary>
[TestFixture]
public class SynchronisationControllerGetDataFlowsTests
{
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private DataFlowQuery? _capturedQuery;

    [SetUp]
    public void SetUp()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        var mockApiKeyRepo = new Mock<IApiKeyRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        mockRepository.Setup(r => r.ApiKeys).Returns(mockApiKeyRepo.Object);

        var mockLogger = new Mock<ILogger<SynchronisationController>>();
        var mockCredentialProtection = new Mock<ICredentialProtectionService>();
        IExpressionEvaluator expressionEvaluator = new DynamicExpressoEvaluator();

        _application = new JimApplication(mockRepository.Object);
        _controller = new SynchronisationController(mockLogger.Object, _application, expressionEvaluator, mockCredentialProtection.Object);

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

        _capturedQuery = null;
        _mockConnectedSystemRepo
            .Setup(r => r.GetDataFlowHeadersAsync(It.IsAny<DataFlowQuery>()))
            .Callback<DataFlowQuery>(q => _capturedQuery = q)
            .ReturnsAsync(BuildFlows());

        // The whole configuration's import mappings, which the contributor count is taken over.
        _mockConnectedSystemRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseObjectTypeAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<SyncRuleMapping>
            {
                new() { Id = 1, TargetMetaverseAttributeId = 500 },
                new() { Id = 2, TargetMetaverseAttributeId = 500 }
            });
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    [Test]
    public async Task GetDataFlowsAsync_NoFilters_ReturnsEveryFlowAsync()
    {
        var result = await _controller.GetDataFlowsAsync(new PaginationRequest(), new DataFlowFilterRequest()) as OkObjectResult;

        var response = result!.Value as PaginatedResponse<DataFlowHeader>;
        Assert.That(response!.Items, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task GetDataFlowsAsync_Filters_ReachTheApplicationLayerIntactAsync()
    {
        // The filters are the endpoint's whole contract; a request DTO that quietly drops one would return a
        // plausible-looking but wrong answer, which is worse than an error.
        var filter = new DataFlowFilterRequest
        {
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 11,
            ConnectedSystemObjectTypeId = 22,
            MetaverseObjectTypeId = 33,
            ConnectedSystemAttributeId = 44,
            MetaverseAttributeId = 55,
            MultipleContributorsOnly = true,
            Search = "department"
        };

        await _controller.GetDataFlowsAsync(new PaginationRequest(), filter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_capturedQuery!.Direction, Is.EqualTo(SyncRuleDirection.Import));
            Assert.That(_capturedQuery.ConnectedSystemId, Is.EqualTo(11));
            Assert.That(_capturedQuery.ConnectedSystemObjectTypeId, Is.EqualTo(22));
            Assert.That(_capturedQuery.MetaverseObjectTypeId, Is.EqualTo(33));
            Assert.That(_capturedQuery.ConnectedSystemAttributeId, Is.EqualTo(44));
            Assert.That(_capturedQuery.MetaverseAttributeId, Is.EqualTo(55));
            Assert.That(_capturedQuery.MultipleContributorsOnly, Is.True);
            Assert.That(_capturedQuery.Search, Is.EqualTo("department"));
        }
    }

    [Test]
    public async Task GetDataFlowsAsync_ImportFlow_CarriesPriorityAndContributorCountAsync()
    {
        var result = await _controller.GetDataFlowsAsync(new PaginationRequest(), new DataFlowFilterRequest()) as OkObjectResult;
        var response = result!.Value as PaginatedResponse<DataFlowHeader>;

        var importFlow = response!.Items.Single(f => f.SyncRuleMappingId == 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(importFlow.Priority, Is.EqualTo(1));
            Assert.That(importFlow.NullIsValue, Is.True);
            Assert.That(importFlow.ContributorCount, Is.EqualTo(2));
            Assert.That(importFlow.EnforceState, Is.Null);
            Assert.That(importFlow.Sources.Single().ConnectedSystemAttributeName, Is.EqualTo("dept"));
        }
    }

    [Test]
    public async Task GetDataFlowsAsync_ExportFlow_CarriesEnforceStateAndNoPriorityAsync()
    {
        var result = await _controller.GetDataFlowsAsync(new PaginationRequest(), new DataFlowFilterRequest()) as OkObjectResult;
        var response = result!.Value as PaginatedResponse<DataFlowHeader>;

        var exportFlow = response!.Items.Single(f => f.SyncRuleMappingId == 3);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exportFlow.EnforceState, Is.True);
            Assert.That(exportFlow.Priority, Is.Null);
            Assert.That(exportFlow.NullIsValue, Is.Null);
            Assert.That(exportFlow.ContributorCount, Is.Null);
            Assert.That(exportFlow.TargetConnectedSystemAttributeName, Is.EqualTo("department"));
        }
    }

    /// <summary>
    /// Two Import flows contributing the same Metaverse Attribute, and one Export flow writing it back out: the
    /// smallest set that exercises both directions and an attribute with more than one contributor.
    /// </summary>
    private static IList<DataFlowHeader> BuildFlows()
    {
        return
        [
            new DataFlowHeader
            {
                SyncRuleMappingId = 1, SyncRuleId = 1, SyncRuleName = "HR Import", SyncRuleEnabled = true,
                Direction = SyncRuleDirection.Import,
                ConnectedSystemId = 1, ConnectedSystemName = "HR System",
                ConnectedSystemObjectTypeId = 10, ConnectedSystemObjectTypeName = "Employee",
                MetaverseObjectTypeId = 100, MetaverseObjectTypeName = "Person",
                TargetMetaverseAttributeId = 500, TargetMetaverseAttributeName = "Department",
                Priority = 1, NullIsValue = true,
                Sources = [new DataFlowSource { Order = 0, ConnectedSystemAttributeId = 200, ConnectedSystemAttributeName = "dept" }]
            },
            new DataFlowHeader
            {
                SyncRuleMappingId = 2, SyncRuleId = 2, SyncRuleName = "Corp Directory Import", SyncRuleEnabled = true,
                Direction = SyncRuleDirection.Import,
                ConnectedSystemId = 2, ConnectedSystemName = "Corp Directory",
                ConnectedSystemObjectTypeId = 11, ConnectedSystemObjectTypeName = "person",
                MetaverseObjectTypeId = 100, MetaverseObjectTypeName = "Person",
                TargetMetaverseAttributeId = 500, TargetMetaverseAttributeName = "Department",
                Priority = 2, NullIsValue = false,
                Sources = [new DataFlowSource { Order = 0, ConnectedSystemAttributeId = 201, ConnectedSystemAttributeName = "department" }]
            },
            new DataFlowHeader
            {
                SyncRuleMappingId = 3, SyncRuleId = 3, SyncRuleName = "AD Export", SyncRuleEnabled = true,
                Direction = SyncRuleDirection.Export,
                ConnectedSystemId = 3, ConnectedSystemName = "Contoso AD",
                ConnectedSystemObjectTypeId = 12, ConnectedSystemObjectTypeName = "user",
                MetaverseObjectTypeId = 100, MetaverseObjectTypeName = "Person",
                TargetConnectedSystemAttributeId = 202, TargetConnectedSystemAttributeName = "department",
                EnforceState = true,
                Sources = [new DataFlowSource { Order = 0, MetaverseAttributeId = 500, MetaverseAttributeName = "Department" }]
            }
        ];
    }
}
