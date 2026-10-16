// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The configuration test case endpoints (#2055 Phase 4): CRUD at <c>/api/v1/configuration-tests</c>, inputs left out
/// of a list unless it asks for them, the application layer's refusals as 400s, and running cases by Synchronisation
/// Rule, Connected System or id.
/// </summary>
[TestFixture]
public class ConfigurationTestsControllerTests
{
    private const int SyncRuleId = 7;
    private const int MappingId = 3;

    private Mock<IRepository> _repository = null!;
    private Mock<IConfigurationTestRepository> _testRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private Mock<IActivityRepository> _activities = null!;
    private JimApplication _application = null!;
    private ConfigurationTestsController _controller = null!;
    private SyncRule _rule = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _testRepository = new Mock<IConfigurationTestRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        _activities = new Mock<IActivityRepository>();
        var apiKeys = new Mock<IApiKeyRepository>();
        _repository.Setup(r => r.ConfigurationTests).Returns(_testRepository.Object);
        // What owns a new case holds none yet, so it has room for it.
        _testRepository.Setup(r => r.GetTestCasesForSyncRuleAsync(It.IsAny<int>())).ReturnsAsync([]);
        _testRepository.Setup(r => r.GetSimpleModeMatchingTestCasesAsync(It.IsAny<int>())).ReturnsAsync([]);
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        _repository.Setup(r => r.Activity).Returns(_activities.Object);
        _repository.Setup(r => r.ApiKeys).Returns(apiKeys.Object);
        _activities.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activities.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var csType = new ConnectedSystemObjectType
        {
            Id = 1, Name = "person",
            Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "sn", Type = AttributeDataType.Text }]
        };
        var mvType = new MetaverseObjectType
        {
            Id = 1, Name = "Person",
            Attributes = [new MetaverseAttribute { Id = 21, Name = "Display Name", Type = AttributeDataType.Text }]
        };
        _rule = new SyncRule
        {
            Id = SyncRuleId, Name = "HR Import", Direction = SyncRuleDirection.Import,
            ConnectedSystemObjectType = csType, MetaverseObjectType = mvType
        };
        var mapping = new SyncRuleMapping { Id = MappingId, SyncRuleId = SyncRuleId, TargetMetaverseAttribute = mvType.Attributes[0] };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "Upper(cs[\"sn\"])" });
        _rule.AttributeFlowRules.Add(mapping);
        _connectedSystems.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(_rule);
        _connectedSystems.Setup(r => r.GetSyncRuleMappingAsync(MappingId)).ReturnsAsync(mapping);

        _application = new JimApplication(_repository.Object);
        _controller = new ConfigurationTestsController(new Mock<ILogger<ConfigurationTestsController>>().Object, _application);

        var apiKeyId = Guid.NewGuid();
        apiKeys.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new ApiKey
        {
            Id = apiKeyId, Name = "TestApiKey", KeyHash = "hash", KeyPrefix = "test", IsEnabled = true, Created = DateTime.UtcNow
        });
        _controller.ControllerContext = new ControllerContext
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
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    #region Read

    [Test]
    public async Task GetAllAsync_BySyncRule_LeavesOutInputsUnlessAskedForAsync()
    {
        _testRepository.Setup(r => r.GetTestCasesForSyncRuleAsync(SyncRuleId)).ReturnsAsync([StoredFlowCase()]);

        var without = (List<ConfigurationTestCaseDto>)((OkObjectResult)await _controller.GetAllAsync(SyncRuleId, null, null)).Value!;
        var with = (List<ConfigurationTestCaseDto>)((OkObjectResult)await _controller.GetAllAsync(SyncRuleId, null, "inputs")).Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(without.Single().Name, Is.EqualTo("Surname upper-cased"));
            Assert.That(without.Single().CsAttributes, Is.Null);
            Assert.That(with.Single().CsAttributes!["sn"], Is.EqualTo(new[] { "Bloggs" }));
        }
    }

    [Test]
    public async Task GetAllAsync_UnknownInclude_IsABadRequestAsync()
    {
        Assert.That(await _controller.GetAllAsync(SyncRuleId, null, "everything"), Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetAllAsync_BothASyncRuleAndAConnectedSystem_IsABadRequestAsync()
    {
        Assert.That(await _controller.GetAllAsync(SyncRuleId, 5, null), Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetByIdAsync_Exists_ReturnsItWithItsInputsAsync()
    {
        _testRepository.Setup(r => r.GetTestCaseAsync(42)).ReturnsAsync(StoredFlowCase());

        var dto = (ConfigurationTestCaseDto)((OkObjectResult)await _controller.GetByIdAsync(42)).Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Id, Is.EqualTo(42));
            Assert.That(dto.CsAttributes!["sn"], Is.EqualTo(new[] { "Bloggs" }));
            Assert.That(dto.Expectation.Kind, Is.EqualTo(ConfigurationTestExpectationKind.Values));
        }
    }

    [Test]
    public async Task GetByIdAsync_DoesNotExist_ReturnsNotFoundAsync()
    {
        Assert.That(await _controller.GetByIdAsync(999), Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region Create, update, delete

    [Test]
    public async Task CreateAsync_ValidCase_StoresItAsInvariantTextAndReturnsCreatedAsync()
    {
        ConfigurationTestCase? stored = null;
        _testRepository.Setup(r => r.CreateTestCaseAsync(It.IsAny<ConfigurationTestCase>()))
            .Callback<ConfigurationTestCase>(c => { c.Id = 42; stored = c; })
            .Returns(Task.CompletedTask);

        var result = await _controller.CreateAsync(new CreateConfigurationTestCaseRequest
        {
            Name = "Surname upper-cased",
            Kind = ConfigurationTestCaseKind.AttributeFlow,
            SyncRuleMappingId = MappingId,
            CsAttributes = new Dictionary<string, object?> { ["sn"] = Json("[\"Bloggs\", 42, true]") },
            Expectation = new ConfigurationTestExpectationDto { Kind = ConfigurationTestExpectationKind.Values, Values = [Json("\"BLOGGS\"")] }
        });

        var created = (CreatedAtRouteResult)result;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(created.RouteName, Is.EqualTo("GetConfigurationTestCase"));
            Assert.That(((ConfigurationTestCaseDto)created.Value!).Id, Is.EqualTo(42));
            Assert.That(stored!.Inputs.Single().Source, Is.EqualTo(ExpressionInputSource.ConnectedSystem));
            Assert.That(stored.Inputs.Single().Values, Is.EqualTo(new[] { "Bloggs", "42", "true" }));
            Assert.That(stored.Expectation.Values, Is.EqualTo(new[] { "BLOGGS" }));
            Assert.That(stored.CreatedByName, Is.EqualTo("TestApiKey"));
        }
    }

    [Test]
    public async Task CreateAsync_CaseTheApplicationRefuses_IsABadRequestWithItsReasonAsync()
    {
        var result = await _controller.CreateAsync(new CreateConfigurationTestCaseRequest
        {
            Name = "Surname upper-cased",
            Kind = ConfigurationTestCaseKind.AttributeFlow,
            SyncRuleMappingId = MappingId,
            Expectation = new ConfigurationTestExpectationDto { Kind = ConfigurationTestExpectationKind.InScope }
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(((ApiErrorResponse)((BadRequestObjectResult)result).Value!).Message, Does.Contain("InScope"));
        }
        _testRepository.Verify(r => r.CreateTestCaseAsync(It.IsAny<ConfigurationTestCase>()), Times.Never);
    }

    private static IEnumerable<TestCaseData> RequestsOverALimit()
    {
        var tooLong = new string('x', ConfigurationTestCase.MaxValueLength + 1);
        yield return new TestCaseData((Action<CreateConfigurationTestCaseRequest>)(r => r.Description = new string('x', ConfigurationTestCase.MaxDescriptionLength + 1)), "description")
            .SetName("CreateAsync_DescriptionOverTheLimit_IsABadRequest");
        yield return new TestCaseData((Action<CreateConfigurationTestCaseRequest>)(r => r.CsAttributes =
                Enumerable.Range(0, ConfigurationTestCase.MaxInputs + 1).ToDictionary(i => $"a{i}", _ => (object?)Json("\"x\""))), "inputs")
            .SetName("CreateAsync_MoreInputsThanTheLimit_IsABadRequest");
        yield return new TestCaseData((Action<CreateConfigurationTestCaseRequest>)(r => r.CsAttributes = new Dictionary<string, object?>
            {
                ["mail"] = Json(JsonSerializer.Serialize(Enumerable.Range(0, ConfigurationTestCase.MaxValues + 1).Select(i => $"v{i}")))
            }), "mail")
            .SetName("CreateAsync_InputWithMoreValuesThanTheLimit_IsABadRequest");
        yield return new TestCaseData((Action<CreateConfigurationTestCaseRequest>)(r => r.CsAttributes = new Dictionary<string, object?>
            {
                ["sn"] = Json(JsonSerializer.Serialize(tooLong))
            }), "sn")
            .SetName("CreateAsync_InputValueOverTheLimit_IsABadRequest");
        yield return new TestCaseData((Action<CreateConfigurationTestCaseRequest>)(r => r.CsAttributes = new Dictionary<string, object?>
            {
                [new string('a', ConfigurationTestCase.MaxAttributeNameLength + 1)] = Json("\"x\"")
            }), "attribute name")
            .SetName("CreateAsync_InputAttributeNameOverTheLimit_IsABadRequest");
        yield return new TestCaseData((Action<CreateConfigurationTestCaseRequest>)(r => r.Expectation = new ConfigurationTestExpectationDto
            {
                Kind = ConfigurationTestExpectationKind.Values,
                Values = Enumerable.Range(0, ConfigurationTestCase.MaxValues + 1).Select(i => (object?)Json($"\"v{i}\"")).ToList()
            }), "expect")
            .SetName("CreateAsync_MoreExpectedValuesThanTheLimit_IsABadRequest");
        yield return new TestCaseData((Action<CreateConfigurationTestCaseRequest>)(r => r.Expectation = new ConfigurationTestExpectationDto
            {
                Kind = ConfigurationTestExpectationKind.Values,
                Values = [Json(JsonSerializer.Serialize(tooLong))]
            }), "expected value")
            .SetName("CreateAsync_ExpectedValueOverTheLimit_IsABadRequest");
    }

    [TestCaseSource(nameof(RequestsOverALimit))]
    public async Task CreateAsync_OverALimit_IsABadRequestNamingItAsync(Action<CreateConfigurationTestCaseRequest> exceed, string named)
    {
        var request = new CreateConfigurationTestCaseRequest
        {
            Name = "Surname upper-cased",
            Kind = ConfigurationTestCaseKind.AttributeFlow,
            SyncRuleMappingId = MappingId,
            CsAttributes = new Dictionary<string, object?> { ["sn"] = Json("\"Bloggs\"") },
            Expectation = new ConfigurationTestExpectationDto { Kind = ConfigurationTestExpectationKind.Values, Values = [Json("\"BLOGGS\"")] }
        };
        exceed(request);

        var result = await _controller.CreateAsync(request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(((ApiErrorResponse)((BadRequestObjectResult)result).Value!).Message, Does.Contain(named));
        }
        _testRepository.Verify(r => r.CreateTestCaseAsync(It.IsAny<ConfigurationTestCase>()), Times.Never);
    }

    [Test]
    public async Task UpdateAsync_Exists_ReplacesItsContentAndKeepsWhatItCoversAsync()
    {
        _testRepository.Setup(r => r.GetTestCaseAsync(42)).ReturnsAsync(StoredFlowCase());
        ConfigurationTestCase? updated = null;
        _testRepository.Setup(r => r.UpdateTestCaseAsync(It.IsAny<ConfigurationTestCase>()))
            .Callback<ConfigurationTestCase>(c => updated = c)
            .Returns(Task.CompletedTask);

        var result = await _controller.UpdateAsync(42, new UpdateConfigurationTestCaseRequest
        {
            Name = "Renamed",
            CsAttributes = new Dictionary<string, object?> { ["sn"] = Json("\"Smith\"") },
            Expectation = new ConfigurationTestExpectationDto { Kind = ConfigurationTestExpectationKind.Values, Values = [Json("\"SMITH\"")] }
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(updated!.Name, Is.EqualTo("Renamed"));
            Assert.That(updated.SyncRuleMappingId, Is.EqualTo(MappingId));
            Assert.That(updated.Kind, Is.EqualTo(ConfigurationTestCaseKind.AttributeFlow));
            Assert.That(updated.Inputs.Single().Values, Is.EqualTo(new[] { "Smith" }));
        }
    }

    [Test]
    public async Task UpdateAsync_OnlyAName_LeavesEverythingElseAsItIsAsync()
    {
        var stored = StoredFlowCase();
        stored.Description = "Checks the upper-casing";
        stored.RunAsOfUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        stored.Inputs =
        [
            new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", ["Bloggs"]),
            new ConfigurationTestInput(ExpressionInputSource.Metaverse, "Display Name", ["Joe"])
        ];
        _testRepository.Setup(r => r.GetTestCaseAsync(42)).ReturnsAsync(stored);
        ConfigurationTestCase? updated = null;
        _testRepository.Setup(r => r.UpdateTestCaseAsync(It.IsAny<ConfigurationTestCase>())).Callback<ConfigurationTestCase>(c => updated = c).Returns(Task.CompletedTask);

        await _controller.UpdateAsync(42, new UpdateConfigurationTestCaseRequest { Name = "Renamed" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated!.Name, Is.EqualTo("Renamed"));
            Assert.That(updated.Description, Is.EqualTo("Checks the upper-casing"));
            Assert.That(updated.RunAsOfUtc, Is.EqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(updated.Inputs, Has.Count.EqualTo(2));
            Assert.That(updated.Expectation.Values, Is.EqualTo(new[] { "BLOGGS" }));
        }
    }

    [Test]
    public async Task UpdateAsync_OneSidesInputs_ReplacesThatSideOnlyAsync()
    {
        var stored = StoredFlowCase();
        stored.Inputs =
        [
            new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", ["Bloggs"]),
            new ConfigurationTestInput(ExpressionInputSource.Metaverse, "Display Name", ["Joe"])
        ];
        _testRepository.Setup(r => r.GetTestCaseAsync(42)).ReturnsAsync(stored);
        ConfigurationTestCase? updated = null;
        _testRepository.Setup(r => r.UpdateTestCaseAsync(It.IsAny<ConfigurationTestCase>())).Callback<ConfigurationTestCase>(c => updated = c).Returns(Task.CompletedTask);

        await _controller.UpdateAsync(42, new UpdateConfigurationTestCaseRequest
        {
            CsAttributes = new Dictionary<string, object?> { ["sn"] = Json("\"Smith\""), ["givenName"] = null }
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated!.Inputs.Where(i => i.Source == ExpressionInputSource.ConnectedSystem).Select(i => i.AttributeName),
                Is.EquivalentTo(new[] { "sn", "givenName" }));
            Assert.That(updated.Inputs.Single(i => i.AttributeName == "givenName").Values, Is.Empty);
            Assert.That(updated.Inputs.Single(i => i.Source == ExpressionInputSource.Metaverse).Values, Is.EqualTo(new[] { "Joe" }));
        }
    }

    [Test]
    public async Task UpdateAsync_EmptyDescriptionAndClearRunAsOf_ClearThemAsync()
    {
        var stored = StoredFlowCase();
        stored.Description = "Checks the upper-casing";
        stored.RunAsOfUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _testRepository.Setup(r => r.GetTestCaseAsync(42)).ReturnsAsync(stored);
        ConfigurationTestCase? updated = null;
        _testRepository.Setup(r => r.UpdateTestCaseAsync(It.IsAny<ConfigurationTestCase>())).Callback<ConfigurationTestCase>(c => updated = c).Returns(Task.CompletedTask);

        await _controller.UpdateAsync(42, new UpdateConfigurationTestCaseRequest { Description = "", ClearRunAsOf = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated!.Description, Is.Null);
            Assert.That(updated.RunAsOfUtc, Is.Null);
        }
    }

    [Test]
    public async Task UpdateAsync_RunAsOfAndClearRunAsOf_IsABadRequestAsync()
    {
        _testRepository.Setup(r => r.GetTestCaseAsync(42)).ReturnsAsync(StoredFlowCase());

        var result = await _controller.UpdateAsync(42, new UpdateConfigurationTestCaseRequest { RunAsOf = DateTime.UtcNow, ClearRunAsOf = true });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateAsync_DoesNotExist_ReturnsNotFoundAsync()
    {
        var result = await _controller.UpdateAsync(999, new UpdateConfigurationTestCaseRequest
        {
            Name = "x", Expectation = new ConfigurationTestExpectationDto { Kind = ConfigurationTestExpectationKind.AnyValue }
        });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task DeleteAsync_Exists_DeletesItAsync()
    {
        _testRepository.Setup(r => r.GetTestCaseAsync(42)).ReturnsAsync(StoredFlowCase());

        var result = await _controller.DeleteAsync(42);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        _testRepository.Verify(r => r.DeleteTestCaseAsync(42), Times.Once);
    }

    [Test]
    public async Task DeleteAsync_DoesNotExist_ReturnsNotFoundAsync()
    {
        Assert.That(await _controller.DeleteAsync(999), Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region Run

    [Test]
    public async Task RunAsync_BySyncRule_RunsItsCasesAsync()
    {
        _testRepository.Setup(r => r.GetTestCasesForSyncRuleAsync(SyncRuleId)).ReturnsAsync([StoredFlowCase()]);

        var result = await _controller.RunAsync(new RunConfigurationTestsRequest { SyncRuleId = SyncRuleId });

        var results = (List<ConfigurationTestRunResultDto>)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().TestCaseId, Is.EqualTo(42));
            Assert.That(results.Single().Status, Is.EqualTo(ConfigurationTestStatus.Passed), results.Single().Summary);
            Assert.That(results.Single().Actual!.Values, Is.EqualTo(new[] { "BLOGGS" }));
        }
    }

    [Test]
    public async Task RunAsync_ByIds_RunsThoseCasesAsync()
    {
        _testRepository.Setup(r => r.GetTestCasesAsync(It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 42 }))))
            .ReturnsAsync([StoredFlowCase()]);

        var result = await _controller.RunAsync(new RunConfigurationTestsRequest { Ids = [42] });

        Assert.That(((List<ConfigurationTestRunResultDto>)((OkObjectResult)result).Value!).Single().TestCaseId, Is.EqualTo(42));
    }

    [TestCase(false, false, false)]
    [TestCase(true, true, false)]
    [TestCase(true, false, true)]
    public async Task RunAsync_NotExactlyOneSelection_IsABadRequestAsync(bool bySyncRule, bool byConnectedSystem, bool byIds)
    {
        var result = await _controller.RunAsync(new RunConfigurationTestsRequest
        {
            SyncRuleId = bySyncRule ? SyncRuleId : null,
            ConnectedSystemId = byConnectedSystem ? 5 : null,
            Ids = byIds ? [42] : null
        });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    private static ConfigurationTestCase StoredFlowCase() => new()
    {
        Id = 42,
        Name = "Surname upper-cased",
        Kind = ConfigurationTestCaseKind.AttributeFlow,
        SyncRuleMappingId = MappingId,
        Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", ["Bloggs"])],
        Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["BLOGGS"])
    };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
