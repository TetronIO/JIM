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
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
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
/// Saving an export Synchronisation Rule through the REST API (and so through PowerShell) reports, in the response's
/// <c>warnings</c>, a Connector-defined Attribute the rule flows alongside the attribute it is realised through, from a
/// different source (#2756). The save goes ahead.
/// </summary>
[TestFixture]
public class ConnectorDefinedAttributeOverlapApiTests
{
    private const int RuleId = 5;
    private const int ConnectedSystemId = 20;
    private const int ObjectTypeId = 7;

    private readonly MetaverseAttribute _displayName = new() { Id = 13, Name = "Display Name", Type = AttributeDataType.Text };
    private readonly MetaverseAttribute _accountName = new() { Id = 14, Name = "Account Name", Type = AttributeDataType.Text };

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private ConnectedSystemObjectType _userType = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        var metaverseRepository = new Mock<IMetaverseRepository>();
        var activityRepository = new Mock<IActivityRepository>();
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        _repository.Setup(r => r.ConfigurationTests).Returns(new EmptyConfigurationTestRepository());
        _repository.Setup(r => r.Metaverse).Returns(metaverseRepository.Object);
        _repository.Setup(r => r.Activity).Returns(activityRepository.Object);
        _repository.Setup(r => r.ApiKeys).Returns(new Mock<IApiKeyRepository>().Object);
        _repository.Setup(r => r.Tasking).Returns(new Mock<ITaskingRepository>().Object);
        _repository.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        metaverseRepository.StubExportReviewReads();
        _connectedSystemRepository.StubConfigurationChangeSynchronisationReads();
        activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var person = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People", Attributes = [_displayName, _accountName] };
        metaverseRepository.Setup(r => r.GetMetaverseObjectTypeAsync(1, It.IsAny<bool>())).ReturnsAsync(person);

        _userType = new ConnectedSystemObjectType { Id = ObjectTypeId, Name = "user", Selected = true, ConnectedSystemId = ConnectedSystemId };
        _userType.Attributes =
        [
            new ConnectedSystemObjectTypeAttribute { Id = 70, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Id = 71, Name = "mailNickname", Type = AttributeDataType.Text, Selected = true },
            new ConnectedSystemObjectTypeAttribute
            {
                Id = 72, Name = "Exchange Alias", Type = AttributeDataType.Text, Selected = true, IsConnectorDefined = true, SourceAttributeName = "mailNickname"
            }
        ];
        _connectedSystemRepository.Setup(r => r.GetObjectTypeAsync(ObjectTypeId)).ReturnsAsync(_userType);
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemHeaderAsync(ConnectedSystemId))
            .ReturnsAsync(new ConnectedSystemHeader { Id = ConnectedSystemId, Name = "AD", ConnectorName = "JIM LDAP Connector" });
        _connectedSystemRepository.Setup(r => r.GetSyncRuleAsync(RuleId)).ReturnsAsync(() => ExportRule(person));
        _connectedSystemRepository.Setup(r => r.GetSyncRuleHeadersAsync(It.IsAny<int?>(), It.IsAny<SyncRuleDirection?>()))
            .ReturnsAsync(new List<SyncRuleHeader>());
        _connectedSystemRepository.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
    }

    [Test]
    public async Task UpdateSyncRuleAsync_ConnectorDefinedAttributeAndItsSourceFromDifferentSources_Returns200WithTheWarningAsync()
    {
        var result = await BuildController().UpdateSyncRuleAsync(RuleId, new UpdateSyncRuleRequest { Description = "Directory accounts" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>(), "a warning, never a refusal");
        var body = (SyncRuleSaveResponse)((OkObjectResult)result).Value!;
        Assert.That(body.Warnings, Has.One.Contains("'Exchange Alias' is defined by the JIM LDAP Connector").And.Contains("'mailNickname'"));
    }

    private SyncRule ExportRule(MetaverseObjectType person)
    {
        var rule = new SyncRule
        {
            Id = RuleId,
            Name = "AD Accounts",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "AD" },
            ConnectedSystemObjectTypeId = ObjectTypeId,
            ConnectedSystemObjectType = _userType,
            MetaverseObjectTypeId = person.Id,
            MetaverseObjectType = person,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect
        };
        AddFlow(rule, 501, _accountName, _userType.Attributes[1]);
        AddFlow(rule, 502, _displayName, _userType.Attributes[2]);
        return rule;
    }

    private static void AddFlow(SyncRule rule, int id, MetaverseAttribute source, ConnectedSystemObjectTypeAttribute target)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Id = id * 10, Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id });
        rule.AttributeFlowRules.Add(mapping);
    }

    private SynchronisationController BuildController()
    {
        var application = new JimApplication(_repository.Object, syncRepository: new JIM.InMemoryData.SyncRepository());
        var controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);

        var apiKeyId = Guid.NewGuid();
        Mock.Get(_repository.Object.ApiKeys).Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId, Name = "TestApiKey", KeyHash = "test-hash", KeyPrefix = "test", IsEnabled = true, Created = DateTime.UtcNow
        });
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("auth_method", "api_key"),
                    new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
                    new Claim(ClaimTypes.Name, "TestApiKey")
                ], "ApiKey"))
            }
        };
        return controller;
    }
}
