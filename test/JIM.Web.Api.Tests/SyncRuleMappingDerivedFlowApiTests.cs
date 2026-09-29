// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
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
/// Metaverse-Derived Attribute Flows over the REST API (#1750, FR 12): a save the dependency graph refuses is a 400
/// carrying the validator's message verbatim (so the portal, REST and PowerShell all report the same text), and a
/// save that goes ahead with a non-blocking warning reports it in the mapping response's <c>warnings</c> array.
/// </summary>
[TestFixture]
public class SyncRuleMappingDerivedFlowApiTests
{
    private const int PersonTypeId = 1;
    private const int HrRuleId = 1;
    private const int AdRuleId = 2;
    private const int MailNicknameMappingId = 102;

    private const string CycleMessage =
        "Saving would create a dependency cycle: Mail Nickname (Synchronisation Rule 'AD Import') reads Display Name, " +
        "which (Synchronisation Rule 'HR Import') reads Mail Nickname.";

    private readonly MetaverseAttribute _email = new() { Id = 11, Name = "Email", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private readonly MetaverseAttribute _displayName = new() { Id = 13, Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private readonly MetaverseAttribute _mailNickname = new() { Id = 14, Name = "Mail Nickname", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private readonly MetaverseAttribute _accountName = new() { Id = 10, Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private SynchronisationController _controller = null!;
    private readonly Dictionary<int, SyncRuleMapping> _mappingsById = new();
    private int _nextMappingId = 500;

    [SetUp]
    public void SetUp()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        var mockMetaverseRepo = new Mock<IMetaverseRepository>();
        var mockActivityRepo = new Mock<IActivityRepository>();
        var mockApiKeyRepo = new Mock<IApiKeyRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        mockRepository.Setup(r => r.Metaverse).Returns(mockMetaverseRepo.Object);
        mockRepository.Setup(r => r.Activity).Returns(mockActivityRepo.Object);
        mockRepository.Setup(r => r.ApiKeys).Returns(mockApiKeyRepo.Object);
        mockRepository.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());
        mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var person = new MetaverseObjectType
        {
            Id = PersonTypeId,
            Name = "Person",
            PluralName = "People",
            Attributes = [_email, _displayName, _mailNickname, _accountName]
        };
        mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(PersonTypeId, true)).ReturnsAsync(person);
        mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(_email.Id)).ReturnsAsync(_email);

        // What the database holds: HR Import derives Display Name from Mail Nickname; AD Import flows Mail Nickname
        // from the directory. Returned fresh per read, as an AsNoTracking query does.
        _mockConnectedSystemRepo
            .Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(PersonTypeId))
            .ReturnsAsync(() =>
            {
                var hr = ImportRule(HrRuleId, "HR Import", 10);
                AddExpressionMapping(hr, 101, _displayName, "mv[\"Mail Nickname\"]");
                var ad = ImportRule(AdRuleId, "AD Import", 20);
                AddExpressionMapping(ad, MailNicknameMappingId, _mailNickname, "cs[\"mailNickname\"]");
                return [hr, ad];
            });

        var adRule = ImportRule(AdRuleId, "AD Import", 20);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(AdRuleId)).ReturnsAsync(adRule);
        _mappingsById[MailNicknameMappingId] = AddExpressionMapping(adRule, MailNicknameMappingId, _mailNickname, "cs[\"mailNickname\"]");

        _mockConnectedSystemRepo
            .Setup(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()))
            .Returns((SyncRuleMapping m) =>
            {
                m.Id = _nextMappingId++;
                _mappingsById[m.Id] = m;
                return Task.CompletedTask;
            });
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingAsync(It.IsAny<int>()))
            .Returns((int id) => Task.FromResult(_mappingsById.GetValueOrDefault(id)));
        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingForUpdateAsync(It.IsAny<int>()))
            .Returns((int id) => Task.FromResult(_mappingsById.GetValueOrDefault(id)));
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _mockConnectedSystemRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<SyncRuleMapping>());

        var application = new JimApplication(mockRepository.Object, syncRepository: new JIM.InMemoryData.SyncRepository());
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);

        var apiKeyId = Guid.NewGuid();
        mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        });
        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey")) };
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private static SyncRule ImportRule(int id, string name, int connectedSystemId) => new()
    {
        Id = id,
        Name = name,
        Direction = SyncRuleDirection.Import,
        ConnectedSystemId = connectedSystemId,
        MetaverseObjectTypeId = PersonTypeId,
        ConnectedSystemObjectTypeId = 7
    };

    private static SyncRuleMapping AddExpressionMapping(SyncRule rule, int id, MetaverseAttribute target, string expression)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Id = id * 10, Order = 0, Expression = expression });
        rule.AttributeFlowRules.Add(mapping);
        return mapping;
    }

    private static CreateSyncRuleMappingRequest CreateRequest(int targetId, string expression) => new()
    {
        TargetMetaverseAttributeId = targetId,
        Sources = [new CreateSyncRuleMappingSourceRequest { Order = 0, Expression = expression }]
    };

    // ---- POST (create) ----

    [Test]
    public async Task CreateSyncRuleMappingAsync_SelfReference_Returns400WithTheValidatorMessageAsync()
    {
        var result = await _controller.CreateSyncRuleMappingAsync(AdRuleId, CreateRequest(_email.Id, "mv[\"Email\"] + \"x\""));

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var body = (ApiErrorResponse)((BadRequestObjectResult)result).Value!;
        Assert.That(body.Message, Is.EqualTo("Saving would create a dependency cycle: Email (Synchronisation Rule 'AD Import') reads Email."));
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_DerivedFlowCallingNow_Returns201WithTheWarningAsync()
    {
        var result = await _controller.CreateSyncRuleMappingAsync(AdRuleId,
            CreateRequest(_email.Id, "mv[\"Display Name\"] + FormatDate(Now(), \"yyyy\")"));

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        var dto = (SyncRuleMappingDto)((CreatedAtRouteResult)result).Value!;
        Assert.That(dto.Warnings, Is.EqualTo(new[]
        {
            "The Attribute Flow to Email (Synchronisation Rule 'AD Import') derives its value from Metaverse attributes and calls Now(), " +
            "which returns a different value each time it is evaluated; the value will change on every synchronisation and can cause repeated exports."
        }));
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_OrdinaryFlow_Returns201WithAnEmptyWarningsArrayAsync()
    {
        var result = await _controller.CreateSyncRuleMappingAsync(AdRuleId, CreateRequest(_email.Id, "cs[\"mail\"]"));

        var dto = (SyncRuleMappingDto)((CreatedAtRouteResult)result).Value!;
        Assert.That(dto.Warnings, Is.Not.Null.And.Empty);
    }

    // ---- PATCH (settings update) ----

    [Test]
    public async Task UpdateSyncRuleMappingAsync_ExpressionClosingACycle_Returns400WithTheValidatorMessageAsync()
    {
        var result = await _controller.UpdateSyncRuleMappingAsync(AdRuleId, MailNicknameMappingId,
            new UpdateSyncRuleMappingRequest { Expression = "mv[\"Display Name\"]" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var body = (ApiErrorResponse)((BadRequestObjectResult)result).Value!;
        Assert.That(body.Message, Is.EqualTo(CycleMessage));
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_DerivedFlowCallingRandom_Returns200WithTheWarningAsync()
    {
        var result = await _controller.UpdateSyncRuleMappingAsync(AdRuleId, MailNicknameMappingId,
            new UpdateSyncRuleMappingRequest { Expression = "mv[\"Account Name\"] + RandomPassword(4, false)" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dto = (SyncRuleMappingDto)((OkObjectResult)result).Value!;
        Assert.That(dto.Warnings, Has.Count.EqualTo(1));
        Assert.That(dto.Warnings[0], Does.Contain("calls RandomPassword()"));
    }

    // ---- GET ----

    [Test]
    public async Task GetSyncRuleMappingAsync_OrdinaryRead_CarriesNoWarningsAsync()
    {
        var result = await _controller.GetSyncRuleMappingAsync(AdRuleId, MailNicknameMappingId);

        var dto = (SyncRuleMappingDto)((OkObjectResult)result).Value!;
        Assert.That(dto.Warnings, Is.Not.Null.And.Empty, "warnings describe a save, so a read never carries any");
    }
}
