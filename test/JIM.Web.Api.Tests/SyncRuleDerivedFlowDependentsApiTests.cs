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
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
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
/// Metaverse-Derived Attribute Flows' REST surfaces (#1750, plan Phase 6): every response to a change that can take
/// away an attribute's last contributor names the derived flows it leaves with a missing input
/// (<c>dependentDerivedFlows</c>, FR 3; warn, never block), the mapping deletion answers 200 with a body rather than
/// 204, a Synchronisation Rule update carries <c>warnings</c>, and mapping reads carry <c>derived</c> step facts.
/// </summary>
[TestFixture]
public class SyncRuleDerivedFlowDependentsApiTests
{
    private const int PersonTypeId = 1;
    private const int HrRuleId = 1;
    private const int AdRuleId = 2;
    private const int DisplayNameMappingId = 101;
    private const int MailNicknameMappingId = 102;
    private const int RegionMappingId = 103;

    private readonly MetaverseAttribute _displayName = new() { Id = 13, Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private readonly MetaverseAttribute _mailNickname = new() { Id = 14, Name = "Mail Nickname", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private readonly MetaverseAttribute _region = new() { Id = 15, Name = "Region", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private MetaverseObjectType _person = null!;
    private string _displayNameExpression = "mv[\"Mail Nickname\"]";

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        var mockMetaverseRepo = new Mock<IMetaverseRepository>();
        var mockActivityRepo = new Mock<IActivityRepository>();
        var mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(mockApiKeyRepo.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(new Mock<ITaskingRepository>().Object);
        mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _person = new MetaverseObjectType { Id = PersonTypeId, Name = "Person", PluralName = "People", Attributes = [_displayName, _mailNickname, _region] };
        mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(PersonTypeId, It.IsAny<bool>())).ReturnsAsync(_person);

        // What the database holds, fresh per read as an AsNoTracking query returns it: HR Import (Connected System 10)
        // derives Display Name from Mail Nickname; AD Import (Connected System 20) flows Mail Nickname and Region.
        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(PersonTypeId)).ReturnsAsync(() => [HrRule(), AdRule()]);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(HrRuleId)).ReturnsAsync(() => HrRule());
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(AdRuleId)).ReturnsAsync(() => AdRule());
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => HrRule().AttributeFlowRules.Concat(AdRule().AttributeFlowRules).SingleOrDefault(m => m.Id == id));
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingForUpdateAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => AdRule().AttributeFlowRules.SingleOrDefault(m => m.Id == id));
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingsAsync(HrRuleId)).ReturnsAsync(() => HrRule().AttributeFlowRules);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemNamesAsync()).ReturnsAsync(new Dictionary<int, string> { [10] = "HR", [20] = "AD" });
        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _mockConnectedSystemRepo.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>())).ReturnsAsync(new Dictionary<int, int>());
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _mockConnectedSystemRepo.Setup(r => r.DeleteSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockConnectedSystemRepo.Setup(r => r.DeleteSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
    }

    private SynchronisationController BuildController()
    {
        _mockRepository.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        var application = new JimApplication(_mockRepository.Object, syncRepository: new JIM.InMemoryData.SyncRepository());
        var controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);

        var apiKeyId = Guid.NewGuid();
        var mockApiKeyRepo = Mock.Get(_mockRepository.Object.ApiKeys);
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
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey")) }
        };
        return controller;
    }

    private SyncRule HrRule()
    {
        var rule = Rule(HrRuleId, "HR Import", 10);
        AddExpressionMapping(rule, DisplayNameMappingId, _displayName, _displayNameExpression);
        return rule;
    }

    private SyncRule AdRule()
    {
        var rule = Rule(AdRuleId, "AD Import", 20);
        AddExpressionMapping(rule, MailNicknameMappingId, _mailNickname, "cs[\"mailNickname\"]");
        AddExpressionMapping(rule, RegionMappingId, _region, "cs[\"region\"]");
        return rule;
    }

    private SyncRule Rule(int id, string name, int connectedSystemId) => new()
    {
        Id = id,
        Name = name,
        Direction = SyncRuleDirection.Import,
        Enabled = true,
        ConnectedSystemId = connectedSystemId,
        ConnectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = connectedSystemId == 10 ? "HR" : "AD" },
        ConnectedSystemObjectTypeId = 7,
        ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 7, Name = "user", Selected = true },
        MetaverseObjectTypeId = PersonTypeId,
        MetaverseObjectType = _person,
        InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect,
        OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect
    };

    private static void AddExpressionMapping(SyncRule rule, int id, MetaverseAttribute target, string expression)
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
    }

    private static void AssertDisplayNameDependant(List<DependentDerivedFlow> dependants)
    {
        Assert.That(dependants, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependants[0].MappingId, Is.EqualTo(DisplayNameMappingId));
            Assert.That(dependants[0].TargetMetaverseAttributeName, Is.EqualTo("Display Name"));
            Assert.That(dependants[0].SyncRuleId, Is.EqualTo(HrRuleId));
            Assert.That(dependants[0].SyncRuleName, Is.EqualTo("HR Import"));
            Assert.That(dependants[0].ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(dependants[0].MissingInputs.Single().MetaverseAttributeName, Is.EqualTo("Mail Nickname"));
            Assert.That(dependants[0].MissingInputs.Single().Indirect, Is.False);
            Assert.That(dependants[0].MissingInputs.Single().Via, Is.Empty);
        }
    }

    // ---- DELETE mapping ----

    [Test]
    public async Task DeleteSyncRuleMappingAsync_LastContributorOfADerivedInput_Returns200NamingTheDependantAsync()
    {
        var result = await BuildController().DeleteSyncRuleMappingAsync(AdRuleId, MailNicknameMappingId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>(), "a dependant warns, it never blocks");
        var body = (SyncRuleMappingDeletionResponse)((OkObjectResult)result).Value!;
        AssertDisplayNameDependant(body.DependentDerivedFlows);
        _mockConnectedSystemRepo.Verify(r => r.DeleteSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Once);
    }

    // ---- PATCH mapping ----

    [Test]
    public async Task UpdateSyncRuleMappingAsync_DisablingTheLastContributor_Returns200NamingTheDependantAsync()
    {
        var result = await BuildController().UpdateSyncRuleMappingAsync(AdRuleId, MailNicknameMappingId, new UpdateSyncRuleMappingRequest { Enabled = false });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dto = (SyncRuleMappingDto)((OkObjectResult)result).Value!;
        AssertDisplayNameDependant(dto.DependentDerivedFlows);
    }

    // ---- GET mapping(s) ----

    [Test]
    public async Task GetSyncRuleMappingAsync_DerivedFlow_CarriesItsStepAndInputsAsync()
    {
        var result = await BuildController().GetSyncRuleMappingAsync(HrRuleId, DisplayNameMappingId);

        var dto = (SyncRuleMappingDto)((OkObjectResult)result).Value!;
        Assert.That(dto.Derived, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Derived!.Step, Is.EqualTo(2));
            Assert.That(dto.Derived!.StepCount, Is.EqualTo(2));
            Assert.That(dto.Derived!.MetaverseInputs, Is.EqualTo(new[] { "Mail Nickname" }));
            Assert.That(dto.DependentDerivedFlows, Is.Not.Null.And.Empty, "a read describes no change");
        }
    }

    [Test]
    public async Task GetSyncRuleMappingAsync_OrdinaryFlow_CarriesNoDerivedInfoAsync()
    {
        var result = await BuildController().GetSyncRuleMappingAsync(AdRuleId, MailNicknameMappingId);

        var dto = (SyncRuleMappingDto)((OkObjectResult)result).Value!;
        Assert.That(dto.Derived, Is.Null);
    }

    [Test]
    public async Task GetSyncRuleMappingsAsync_RuleHostingADerivedFlow_CarriesItsStepAsync()
    {
        var result = await BuildController().GetSyncRuleMappingsAsync(HrRuleId);

        var dtos = ((IEnumerable<SyncRuleMappingDto>)((OkObjectResult)result).Value!).ToList();
        Assert.That(dtos.Single().Derived?.Step, Is.EqualTo(2));
    }

    // ---- PUT Synchronisation Rule ----

    [Test]
    public async Task UpdateSyncRuleAsync_DisablingTheRuleHoldingTheLastContributor_Returns200NamingTheDependantAsync()
    {
        var result = await BuildController().UpdateSyncRuleAsync(AdRuleId, new UpdateSyncRuleRequest { Enabled = false });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var body = (SyncRuleSaveResponse)((OkObjectResult)result).Value!;
        AssertDisplayNameDependant(body.DependentDerivedFlows);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.Warnings, Is.Not.Null.And.Empty);
            Assert.That(body.Id, Is.EqualTo(AdRuleId), "the header is carried in full");
            Assert.That(body.Name, Is.EqualTo("AD Import"));
            Assert.That(body.ConnectedSystemName, Is.EqualTo("AD"));
        }
    }

    [Test]
    public async Task UpdateSyncRuleAsync_RuleHostingANonRepeatableDerivedFlow_Returns200WithTheWarningAsync()
    {
        _displayNameExpression = "mv[\"Mail Nickname\"] + FormatDate(Now(), \"yyyy\")";

        var result = await BuildController().UpdateSyncRuleAsync(HrRuleId, new UpdateSyncRuleRequest { Description = "HR people" });

        var body = (SyncRuleSaveResponse)((OkObjectResult)result).Value!;
        Assert.That(body.Warnings, Has.Count.EqualTo(1));
        Assert.That(body.Warnings[0], Does.Contain("calls Now()"));
        Assert.That(body.DependentDerivedFlows, Is.Empty);
    }

    // ---- DELETE Synchronisation Rule ----

    [Test]
    public async Task DeleteSyncRuleAsync_KeepChosen_Returns200NamingTheDependantAsync()
    {
        var result = await BuildController().DeleteSyncRuleAsync(AdRuleId, keepContributedValues: true);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var body = (SyncRuleDeletionResponse)((OkObjectResult)result).Value!;
        AssertDisplayNameDependant(body.DependentDerivedFlows);
    }

    // ---- The response shape ----

    [Test]
    public void SyncRuleSaveResponse_FromSave_CarriesEveryHeaderProperty()
    {
        var rule = AdRule();
        rule.Description = "Directory people";

        var response = SyncRuleSaveResponse.FromSave(rule, rule);
        var header = SyncRuleHeader.FromEntity(rule);

        foreach (var property in typeof(SyncRuleHeader).GetProperties())
            Assert.That(property.GetValue(response), Is.EqualTo(property.GetValue(header)), property.Name);
    }
}
