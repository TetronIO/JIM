// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.TestSupport;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The attribute priority endpoints' responses say which Connected System Object Types the reorder recorded as awaiting
/// synchronisation (#2650), as every Synchronisation Rule, Attribute Flow and Object Matching Rule save does (#2615). A
/// reorder changes which import rule wins the attribute for the objects of every rule it moves, so the next
/// synchronisation of each affected Connected System processes every object of that rule's type. Driven over the real
/// application server with the repositories mocked.
/// </summary>
[TestFixture]
public class MetaverseControllerAttributePriorityTests
{
    private const int MetaverseObjectTypeId = 1;
    private const int MetaverseAttributeId = 42;

    private Mock<IRepository> _repo = null!;
    private Mock<IMetaverseRepository> _mv = null!;
    private Mock<IActivityRepository> _activity = null!;
    private Mock<IApiKeyRepository> _apiKeys = null!;
    private Mock<IConnectedSystemRepository> _cs = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _mv = new Mock<IMetaverseRepository>();
        _activity = new Mock<IActivityRepository>();
        _apiKeys = new Mock<IApiKeyRepository>();
        _cs = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.Metaverse).Returns(_mv.Object);
        _repo.Setup(r => r.Activity).Returns(_activity.Object);
        _repo.Setup(r => r.ApiKeys).Returns(_apiKeys.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_cs.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());

        _activity.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
            })
            .Returns(Task.CompletedTask);
        _activity.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _mv.Setup(r => r.GetMetaverseAttributeAsync(MetaverseAttributeId, It.IsAny<bool>()))
            .ReturnsAsync(new MetaverseAttribute { Id = MetaverseAttributeId, Name = "department", Type = AttributeDataType.Text });

        _cs.Setup(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>())).Returns(Task.CompletedTask);
        _cs.Setup(r => r.CreateConfigurationChangeSynchronisationAsync(It.IsAny<ConfigurationChangeSynchronisation>())).Returns(Task.CompletedTask);

        _application = new JimApplication(_repo.Object);
        _controller = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, _application);

        var apiKeyId = Guid.NewGuid();
        var apiKey = new JIM.Models.Security.ApiKey { Id = apiKeyId, Name = "TestApiKey", KeyHash = "h", KeyPrefix = "p", IsEnabled = true, Created = DateTime.UtcNow };
        _apiKeys.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(apiKey);

        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    [Test]
    public async Task SetAttributePriorityOrderAsync_ImportRulesReordered_ReturnsEachAffectedTypeAwaitingSynchronisationAsync()
    {
        SetupTwoContributors();

        var request = new SetAttributePriorityOrderRequest
        {
            Contributors = [new() { MappingId = 20 }, new() { MappingId = 10 }]
        };

        var result = await _controller.SetAttributePriorityOrderAsync(MetaverseAttributeId, MetaverseObjectTypeId, request);

        var dto = (result as OkObjectResult)?.Value as AttributePriorityOrderDto;
        Assert.That(dto, Is.Not.Null);
        AssertBothTypesAwaitingSynchronisation(dto!);
    }

    [Test]
    public async Task SetAttributePriorityOrderAsync_OrderUnchanged_ReturnsNothingAwaitingSynchronisationAsync()
    {
        SetupTwoContributors();

        var request = new SetAttributePriorityOrderRequest
        {
            Contributors = [new() { MappingId = 10 }, new() { MappingId = 20 }]
        };

        var result = await _controller.SetAttributePriorityOrderAsync(MetaverseAttributeId, MetaverseObjectTypeId, request);

        var dto = (result as OkObjectResult)?.Value as AttributePriorityOrderDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.AwaitingSynchronisation, Is.Empty, "an order that moved nothing records nothing");
    }

    [Test]
    public async Task MoveAttributePriorityAsync_ImportRulesReordered_ReturnsEachAffectedTypeAwaitingSynchronisationAsync()
    {
        SetupTwoContributors();

        var result = await _controller.MoveAttributePriorityAsync(MetaverseAttributeId, MetaverseObjectTypeId, 20,
            new MoveAttributePriorityRequest { Position = 1 });

        var dto = (result as OkObjectResult)?.Value as AttributePriorityOrderDto;
        Assert.That(dto, Is.Not.Null);
        AssertBothTypesAwaitingSynchronisation(dto!);
    }

    [Test]
    public async Task MoveAttributePriorityAsync_MappingAlreadyAtThePosition_ReturnsNothingAwaitingSynchronisationAsync()
    {
        SetupTwoContributors();

        var result = await _controller.MoveAttributePriorityAsync(MetaverseAttributeId, MetaverseObjectTypeId, 10,
            new MoveAttributePriorityRequest { Position = 1 });

        var dto = (result as OkObjectResult)?.Value as AttributePriorityOrderDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.AwaitingSynchronisation, Is.Empty, "a move that moved nothing records nothing");
    }

    [Test]
    public async Task GetAttributePriorityOrderAsync_ReturnsNothingAwaitingSynchronisationAsync()
    {
        SetupTwoContributors();

        var result = await _controller.GetAttributePriorityOrderAsync(MetaverseAttributeId, MetaverseObjectTypeId);

        var dto = (result as OkObjectResult)?.Value as AttributePriorityOrderDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.AwaitingSynchronisation, Is.Empty, "a read records nothing");
    }

    // Two import rules, on different Object Types of different Connected Systems, contribute to the attribute: HR's
    // rule first, then the directory's. Swapping them changes which wins for the objects of both types.
    private void SetupTwoContributors()
    {
        var first = Contributor(mappingId: 10, priority: 1, ruleId: 1000, connectedSystemId: 30, connectedSystemName: "HR", objectTypeId: 70, objectTypeName: "person");
        var second = Contributor(mappingId: 20, priority: 2, ruleId: 2000, connectedSystemId: 40, connectedSystemName: "Directory", objectTypeId: 80, objectTypeName: "user");

        _cs.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, MetaverseAttributeId))
            .ReturnsAsync(() => [first, second]);
        _cs.Setup(r => r.GetSyncRuleAsync(1000)).ReturnsAsync(first.SyncRule);
        _cs.Setup(r => r.GetSyncRuleAsync(2000)).ReturnsAsync(second.SyncRule);
        _cs.Setup(r => r.GetConnectedSystemObjectCountAsync(30, 70, null)).ReturnsAsync(1200);
        _cs.Setup(r => r.GetConnectedSystemObjectCountAsync(40, 80, null)).ReturnsAsync(3400);
    }

    private static SyncRuleMapping Contributor(int mappingId, int priority, int ruleId, int connectedSystemId, string connectedSystemName, int objectTypeId, string objectTypeName)
    {
        var rule = new SyncRule
        {
            Id = ruleId,
            Name = $"{connectedSystemName} Import",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            MetaverseObjectTypeId = MetaverseObjectTypeId,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = connectedSystemName },
            ConnectedSystemObjectTypeId = objectTypeId,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = objectTypeId, Name = objectTypeName }
        };
        var mapping = new SyncRuleMapping
        {
            Id = mappingId,
            Priority = priority,
            SyncRuleId = ruleId,
            SyncRule = rule,
            TargetMetaverseAttributeId = MetaverseAttributeId,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = MetaverseAttributeId, Name = "department", Type = AttributeDataType.Text }
        };
        rule.AttributeFlowRules = [mapping];
        return mapping;
    }

    private static void AssertBothTypesAwaitingSynchronisation(AttributePriorityOrderDto dto)
    {
        Assert.That(dto.AwaitingSynchronisation, Has.Count.EqualTo(2), "one entry for each affected rule's Object Type");
        var hr = dto.AwaitingSynchronisation.Single(a => a.ConnectedSystemId == 30);
        var directory = dto.AwaitingSynchronisation.Single(a => a.ConnectedSystemId == 40);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hr.ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(hr.ObjectTypeName, Is.EqualTo("person"));
            Assert.That(hr.ObjectCount, Is.EqualTo(1200));
            Assert.That(directory.ConnectedSystemName, Is.EqualTo("Directory"));
            Assert.That(directory.ObjectTypeName, Is.EqualTo("user"));
            Assert.That(directory.ObjectCount, Is.EqualTo(3400));
        }
    }
}
