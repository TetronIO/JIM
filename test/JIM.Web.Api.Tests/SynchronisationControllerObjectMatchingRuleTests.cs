// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST create endpoints for Object Matching Rules refuse a rule whose scope the Connected System's matching
/// mode would never consult (#1569). Before this, POST sync-rules/{id}/matching-rules happily created a
/// Synchronisation Rule scoped rule on a system in simple matching mode; the rule was silently inert and
/// synchronisation joined nothing, with no error anywhere. The refusal is a 400 naming the active mode and the
/// remedy, which is also what the PowerShell cmdlets surface. These tests also pin that the application layer's
/// InvalidDataException reaches the caller as a 400 rather than an unhandled 500.
/// </summary>
[TestFixture]
public class SynchronisationControllerObjectMatchingRuleTests
{
    private const int ConnectedSystemId = 2;
    private const int CsoTypeId = 9;
    private const int MvoTypeId = 3;
    private const int EmployeeIdAttributeId = 101;
    private const int EmployeeIdMetaverseAttributeId = 201;
    private const int SyncRuleId = 40;

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private ConnectedSystem _connectedSystem = null!;
    private ConnectedSystemObjectType _csoType = null!;
    private SyncRule _syncRule = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();

        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);

        // Saves read the configuration test cases covering what they save (the save gate, #2055 Phase 4b).
        _repository.Setup(r => r.ConfigurationTests).Returns(new EmptyConfigurationTestRepository());
        _repository.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _repository.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repository.Setup(r => r.ApiKeys).Returns(_apiKeyRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _connectedSystemRepo.Setup(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);

        _csoType = new ConnectedSystemObjectType
        {
            Id = CsoTypeId,
            Name = "User",
            ConnectedSystemId = ConnectedSystemId,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = EmployeeIdAttributeId, Name = "employeeID", Type = AttributeDataType.Text }
            ]
        };

        _connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "HR",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem,
            ObjectTypes = [_csoType]
        };
        _csoType.ConnectedSystem = _connectedSystem;

        _syncRule = new SyncRule
        {
            Id = SyncRuleId,
            Name = "Import Users",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystem = _connectedSystem,
            ConnectedSystemObjectTypeId = CsoTypeId,
            ConnectedSystemObjectType = _csoType,
            MetaverseObjectTypeId = MvoTypeId
        };

        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(() => _connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(() => _syncRule);

        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(MvoTypeId, false)).ReturnsAsync(
            new MetaverseObjectType { Id = MvoTypeId, Name = "Person" });
        _metaverseRepo.Setup(r => r.GetMetaverseAttributesAsync(It.IsAny<bool>())).ReturnsAsync(
        [
            new MetaverseAttribute { Id = EmployeeIdMetaverseAttributeId, Name = "Employee ID", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued }
        ]);

        _application = new JimApplication(_repository.Object);
        _controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, _application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object);

        var apiKeyId = Guid.NewGuid();
        _apiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        });

        var identity = new ClaimsIdentity(
        [
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "TestApiKey")
        ], "ApiKey");

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task CreateSyncRuleObjectMatchingRule_SystemInSimpleMode_ReturnsBadRequestNamingTheModeAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem;

        var result = await _controller.CreateSyncRuleObjectMatchingRuleAsync(SyncRuleId, SyncRuleScopedRequest());

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>(),
            "a Synchronisation Rule scoped rule is never consulted in simple matching mode and must be refused");
        var error = ((BadRequestObjectResult)result).Value as ApiErrorResponse;
        Assert.That(error!.Message, Does.Contain("simple matching mode"));
        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Never);
    }

    [Test]
    public async Task CreateSyncRuleObjectMatchingRule_SystemInAdvancedMode_CreatesTheRuleAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;

        var result = await _controller.CreateSyncRuleObjectMatchingRuleAsync(SyncRuleId, SyncRuleScopedRequest());

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Once);
    }

    [Test]
    public async Task CreateObjectMatchingRule_SystemInAdvancedMode_ReturnsBadRequestNamingTheModeAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;

        var result = await _controller.CreateObjectMatchingRuleAsync(ConnectedSystemId, TypeScopedRequest());

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>(),
            "a type-scoped rule is never consulted in advanced matching mode and must be refused");
        var error = ((BadRequestObjectResult)result).Value as ApiErrorResponse;
        Assert.That(error!.Message, Does.Contain("advanced matching mode"));
        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Never);
    }

    [Test]
    public async Task CreateObjectMatchingRule_SystemInSimpleMode_CreatesTheRuleAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem;

        var result = await _controller.CreateObjectMatchingRuleAsync(ConnectedSystemId, TypeScopedRequest());

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Once);
    }

    [Test]
    public async Task CreateObjectMatchingRule_CaseSensitiveOmitted_IgnoresCaseAsync()
    {
        // The portal always created rules that ignore case while the REST API (and so PowerShell) defaulted to
        // respecting it, so the same rule joined differently depending on which surface made it (#2051).
        var created = CaptureCreatedRule();

        await _controller.CreateObjectMatchingRuleAsync(ConnectedSystemId, TypeScopedRequest());

        Assert.That(created(), Is.Not.Null);
        Assert.That(created()!.CaseSensitive, Is.False, "an Object Matching Rule ignores case unless asked not to");
    }

    [Test]
    public async Task CreateSyncRuleObjectMatchingRule_CaseSensitiveOmitted_IgnoresCaseAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        var created = CaptureCreatedRule();

        await _controller.CreateSyncRuleObjectMatchingRuleAsync(SyncRuleId, SyncRuleScopedRequest());

        Assert.That(created(), Is.Not.Null);
        Assert.That(created()!.CaseSensitive, Is.False, "an Object Matching Rule ignores case unless asked not to");
    }

    [Test]
    public async Task CreateObjectMatchingRule_CaseSensitiveOmittedOnACaseExactSource_RespectsCaseAsync()
    {
        // The default follows the source attribute's declaration (#2051): an identifier the Connected System
        // compares exactly is matched exactly.
        _csoType.Attributes.Single(a => a.Id == EmployeeIdAttributeId).CaseExact = true;
        var created = CaptureCreatedRule();

        await _controller.CreateObjectMatchingRuleAsync(ConnectedSystemId, TypeScopedRequest());

        Assert.That(created()!.CaseSensitive, Is.True);
    }

    [Test]
    public async Task CreateSyncRuleObjectMatchingRule_CaseSensitiveOmittedOnACaseExactSource_RespectsCaseAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        _csoType.Attributes.Single(a => a.Id == EmployeeIdAttributeId).CaseExact = true;
        var created = CaptureCreatedRule();

        await _controller.CreateSyncRuleObjectMatchingRuleAsync(SyncRuleId, SyncRuleScopedRequest());

        Assert.That(created()!.CaseSensitive, Is.True);
    }

    [Test]
    public async Task CreateObjectMatchingRule_CaseSensitiveFalseOnACaseExactSource_StoresFalseAsync()
    {
        // An explicit answer always wins over the derived default.
        _csoType.Attributes.Single(a => a.Id == EmployeeIdAttributeId).CaseExact = true;
        var created = CaptureCreatedRule();
        var request = TypeScopedRequest();
        request.CaseSensitive = false;

        await _controller.CreateObjectMatchingRuleAsync(ConnectedSystemId, request);

        Assert.That(created()!.CaseSensitive, Is.False);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CreateObjectMatchingRule_CaseSensitiveSupplied_StoresItAsync(bool caseSensitive)
    {
        var created = CaptureCreatedRule();
        var request = TypeScopedRequest();
        request.CaseSensitive = caseSensitive;

        await _controller.CreateObjectMatchingRuleAsync(ConnectedSystemId, request);

        Assert.That(created()!.CaseSensitive, Is.EqualTo(caseSensitive));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CreateSyncRuleObjectMatchingRule_CaseSensitiveSupplied_StoresItAsync(bool caseSensitive)
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        var created = CaptureCreatedRule();
        var request = SyncRuleScopedRequest();
        request.CaseSensitive = caseSensitive;

        await _controller.CreateSyncRuleObjectMatchingRuleAsync(SyncRuleId, request);

        Assert.That(created()!.CaseSensitive, Is.EqualTo(caseSensitive));
    }

    [Test]
    public async Task SwitchObjectMatchingMode_ApiKeyAuthenticated_SwitchesInsteadOfFailingAttributionAsync()
    {
        // The endpoint's auth gate accepts an API key, but it previously handed the application layer a null user,
        // and activity attribution (rightly) refuses an unattributed Activity. Switch-JIMMatchingMode therefore
        // never worked under API key authentication, which is how automation authenticates.
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, true, true)).ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);

        var result = await _controller.SwitchObjectMatchingModeAsync(ConnectedSystemId,
            new SwitchObjectMatchingModeRequest { Mode = ObjectMatchingRuleMode.SyncRule });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var switchResult = (JIM.Models.Staging.DTOs.ObjectMatchingModeSwitchResult)((OkObjectResult)result).Value!;
        Assert.That(switchResult.Success, Is.True);
        Assert.That(switchResult.NewMode, Is.EqualTo(ObjectMatchingRuleMode.SyncRule));
    }

    #region Expression sources (#2052)

    [Test]
    public async Task CreateObjectMatchingRule_ExpressionSource_CreatesTheRuleAndReturnsTheExpressionAsync()
    {
        var created = CaptureCreatedRule();
        var request = TypeScopedRequest();
        request.Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, Expression = PrefixedEmployeeId }];

        var result = await _controller.CreateObjectMatchingRuleAsync(ConnectedSystemId, request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        var dto = (ObjectMatchingRuleDto)((CreatedAtRouteResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(created()!.Sources[0].Expression, Is.EqualTo(PrefixedEmployeeId));
            Assert.That(created()!.Sources[0].ConnectedSystemAttributeId, Is.Null);
            Assert.That(dto.Sources[0].Expression, Is.EqualTo(PrefixedEmployeeId));
            Assert.That(dto.Sources[0].ConnectedSystemAttributeId, Is.Null);
        }
    }

    [Test]
    public async Task CreateSyncRuleObjectMatchingRule_ExpressionSource_CreatesTheRuleAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        var created = CaptureCreatedRule();
        var request = SyncRuleScopedRequest();
        request.Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, Expression = PrefixedEmployeeId }];

        var result = await _controller.CreateSyncRuleObjectMatchingRuleAsync(SyncRuleId, request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        Assert.That(created()!.Sources[0].Expression, Is.EqualTo(PrefixedEmployeeId));
    }

    [Test]
    public async Task CreateObjectMatchingRule_SourceWithAnAttributeAndAnExpression_ReturnsBadRequestAsync()
    {
        var request = TypeScopedRequest();
        request.Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, ConnectedSystemAttributeId = EmployeeIdAttributeId, Expression = PrefixedEmployeeId }];

        var result = await _controller.CreateObjectMatchingRuleAsync(ConnectedSystemId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(((ApiErrorResponse)((BadRequestObjectResult)result).Value!).Message, Does.Contain("not both"));
        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Never);
    }

    [Test]
    public async Task CreateObjectMatchingRule_SourceWithNeitherAnAttributeNorAnExpression_ReturnsBadRequestNamingBothAsync()
    {
        var request = TypeScopedRequest();
        request.Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, Expression = "  " }];

        var result = await _controller.CreateObjectMatchingRuleAsync(ConnectedSystemId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(((ApiErrorResponse)((BadRequestObjectResult)result).Value!).Message,
            Does.Contain("ConnectedSystemAttributeId").And.Contain("Expression"));
    }

    [Test]
    public async Task CreateObjectMatchingRule_ExpressionTheApplicationRefuses_ReturnsBadRequestWithItsReasonAsync()
    {
        var request = TypeScopedRequest();
        request.Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, Expression = "Trim(cs[\"employeeID\"]) + Trim(mv[\"Department\"])" }];

        var result = await _controller.CreateObjectMatchingRuleAsync(ConnectedSystemId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(((ApiErrorResponse)((BadRequestObjectResult)result).Value!).Message, Does.Contain("mv[\"Department\"]"));
    }

    [Test]
    public async Task UpdateObjectMatchingRule_SourcesReplacedWithAnExpression_StoresTheExpressionAsync()
    {
        var stored = StoredTypeScopedRule();
        _connectedSystemRepo.Setup(r => r.GetObjectMatchingRuleAsync(stored.Id)).ReturnsAsync(stored);
        ObjectMatchingRule? updated = null;
        _connectedSystemRepo.Setup(r => r.UpdateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()))
            .Callback<ObjectMatchingRule>(rule => updated = rule).Returns(Task.CompletedTask);

        var result = await _controller.UpdateObjectMatchingRuleAsync(ConnectedSystemId, stored.Id, new UpdateObjectMatchingRuleRequest
        {
            Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, Expression = PrefixedEmployeeId }]
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated!.Sources, Has.Count.EqualTo(1));
            Assert.That(updated!.Sources[0].Expression, Is.EqualTo(PrefixedEmployeeId));
            Assert.That(updated!.Sources[0].ConnectedSystemAttributeId, Is.Null);
        }
    }

    [Test]
    public async Task UpdateSyncRuleObjectMatchingRule_SourcesReplacedWithAnExpression_StoresTheExpressionAsync()
    {
        _connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        var stored = StoredTypeScopedRule();
        stored.ConnectedSystemObjectTypeId = null;
        stored.ConnectedSystemObjectType = null;
        stored.MetaverseObjectTypeId = null;
        stored.SyncRuleId = SyncRuleId;
        _connectedSystemRepo.Setup(r => r.GetObjectMatchingRuleAsync(stored.Id)).ReturnsAsync(stored);
        ObjectMatchingRule? updated = null;
        _connectedSystemRepo.Setup(r => r.UpdateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()))
            .Callback<ObjectMatchingRule>(rule => updated = rule).Returns(Task.CompletedTask);

        var result = await _controller.UpdateSyncRuleObjectMatchingRuleAsync(SyncRuleId, stored.Id, new UpdateObjectMatchingRuleRequest
        {
            Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, Expression = PrefixedEmployeeId }]
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(updated!.Sources[0].Expression, Is.EqualTo(PrefixedEmployeeId));
    }

    #region Save gate (#2055 Phase 4b)

    [Test]
    public async Task UpdateObjectMatchingRule_ChangeStopsATestCasePassing_Returns409NamingTheCaseAndSavesNothingAsync()
    {
        var stored = GatedSimpleModeRule();

        var result = await _controller.UpdateObjectMatchingRuleAsync(ConnectedSystemId, stored.Id, new UpdateObjectMatchingRuleRequest
        {
            Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, Expression = PrefixedEmployeeId }]
        });

        var conflict = result as ConflictObjectResult;
        Assert.That(conflict, Is.Not.Null, result.GetType().Name);
        var body = (ConfigurationTestGateResponse)conflict!.Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.Message, Does.Contain("Matches on employee ID"));
            Assert.That(body.Regressions.Single().Result.TestCaseName, Is.EqualTo("Matches on employee ID"));
            Assert.That(body.Regressions.Single().CanAccept, Is.True);
            Assert.That(body.Regressions.Single().AcceptedExpectation!.Values, Is.EqualTo(new[] { "E12345" }));
        }
        _connectedSystemRepo.Verify(r => r.UpdateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Never);
    }

    [Test]
    public async Task UpdateObjectMatchingRule_SaveAnyway_SavesAsync()
    {
        var stored = GatedSimpleModeRule();

        var result = await _controller.UpdateObjectMatchingRuleAsync(ConnectedSystemId, stored.Id, new UpdateObjectMatchingRuleRequest
        {
            Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, Expression = PrefixedEmployeeId }],
            SaveAnyway = true
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _connectedSystemRepo.Verify(r => r.UpdateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Once);
    }

    [Test]
    public async Task UpdateObjectMatchingRule_AcceptNewResults_RewritesTheCaseAndSavesAsync()
    {
        var stored = GatedSimpleModeRule();
        ConfigurationTestCase? rewritten = null;
        _testRepository.Setup(r => r.UpdateTestCaseAsync(It.IsAny<ConfigurationTestCase>()))
            .Callback<ConfigurationTestCase>(c => rewritten = c).Returns(Task.CompletedTask);

        var result = await _controller.UpdateObjectMatchingRuleAsync(ConnectedSystemId, stored.Id, new UpdateObjectMatchingRuleRequest
        {
            Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, Expression = PrefixedEmployeeId }],
            AcceptNewResults = true
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(rewritten!.Expectation.Values, Is.EqualTo(new[] { "E12345" }));
    }

    private Mock<IConfigurationTestRepository> _testRepository = null!;

    /// <summary>
    /// A Simple mode rule matching on employee ID as it is, with a passing test case covering it.
    /// </summary>
    private ObjectMatchingRule GatedSimpleModeRule()
    {
        var stored = StoredTypeScopedRule();
        _connectedSystemRepo.Setup(r => r.GetObjectMatchingRuleAsync(stored.Id)).ReturnsAsync(stored);
        // A fresh instance each read, as an untracked read gives: the stored rule, whatever the edit did to the one above.
        _connectedSystemRepo.Setup(r => r.GetObjectMatchingRuleUntrackedAsync(stored.Id)).ReturnsAsync(() => StoredTypeScopedRule());
        _connectedSystemRepo.Setup(r => r.GetObjectTypeAsync(CsoTypeId)).ReturnsAsync(_csoType);
        _connectedSystemRepo.Setup(r => r.UpdateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);

        _testRepository = new Mock<IConfigurationTestRepository>();
        _testRepository.Setup(r => r.GetSimpleModeMatchingTestCasesAsync(ConnectedSystemId)).ReturnsAsync(
        [
            new ConfigurationTestCase
            {
                Id = 5,
                Name = "Matches on employee ID",
                Kind = ConfigurationTestCaseKind.ObjectMatchingRule,
                ObjectMatchingRuleId = stored.Id,
                Inputs = [new ConfigurationTestInput(null, "employeeID", ["12345"])],
                Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["12345"])
            }
        ]);
        _testRepository.Setup(r => r.GetTestCasesForConnectedSystemAsync(It.IsAny<int>())).ReturnsAsync([]);
        _testRepository.Setup(r => r.GetTestCasesForSyncRuleAsync(It.IsAny<int>())).ReturnsAsync([]);
        _repository.Setup(r => r.ConfigurationTests).Returns(_testRepository.Object);
        return stored;
    }

    #endregion

    private const string PrefixedEmployeeId = "\"E\" + Trim(cs[\"employeeID\"])";

    private ObjectMatchingRule StoredTypeScopedRule() => new()
    {
        Id = 77,
        Order = 0,
        ConnectedSystemObjectTypeId = CsoTypeId,
        ConnectedSystemObjectType = _csoType,
        MetaverseObjectTypeId = MvoTypeId,
        TargetMetaverseAttributeId = EmployeeIdMetaverseAttributeId,
        TargetMetaverseAttribute = new MetaverseAttribute { Id = EmployeeIdMetaverseAttributeId, Name = "Employee ID", Type = AttributeDataType.Text },
        Sources = [new ObjectMatchingRuleSource { Id = 1, Order = 0, ConnectedSystemAttributeId = EmployeeIdAttributeId }]
    };

    #endregion

    private Func<ObjectMatchingRule?> CaptureCreatedRule()
    {
        ObjectMatchingRule? created = null;
        _connectedSystemRepo.Setup(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()))
            .Callback<ObjectMatchingRule>(rule => created = rule)
            .Returns(Task.CompletedTask);
        return () => created;
    }

    private static CreateSyncRuleObjectMatchingRuleRequest SyncRuleScopedRequest() => new()
    {
        TargetMetaverseAttributeId = EmployeeIdMetaverseAttributeId,
        Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, ConnectedSystemAttributeId = EmployeeIdAttributeId }]
    };

    private static CreateObjectMatchingRuleRequest TypeScopedRequest() => new()
    {
        ConnectedSystemObjectTypeId = CsoTypeId,
        MetaverseObjectTypeId = MvoTypeId,
        TargetMetaverseAttributeId = EmployeeIdMetaverseAttributeId,
        Sources = [new CreateObjectMatchingRuleSourceRequest { Order = 0, ConnectedSystemAttributeId = EmployeeIdAttributeId }]
    };
}
