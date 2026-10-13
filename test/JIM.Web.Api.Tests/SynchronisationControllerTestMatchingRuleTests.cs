// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using InMemorySyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The Object Matching tester over REST (#2055): <c>POST connected-systems/{id}/matching-rules/{ruleId}/test</c> and
/// <c>POST sync-rules/{id}/matching-rules/{ruleId}/test</c> work out what a saved rule would compare for sample values or
/// a real Connected System Object, and optionally what those values find in the Metaverse. The evaluation itself is
/// proven by <c>ObjectMatchingTesterTests</c>; these pin the API's shape.
/// </summary>
[TestFixture]
public class SynchronisationControllerTestMatchingRuleTests
{
    private const int ConnectedSystemId = 3;
    private const int ObjectTypeId = 1;
    private const int RuleId = 40;
    private const int SyncRuleId = 7;
    private static readonly Guid CsoId = Guid.Parse("0c2a7a3e-1d2b-4c5e-9f60-718293a4b5c6");

    private JimApplication _application = null!;
    private InMemorySyncRepository _syncRepository = null!;
    private ConnectedSystemObjectType _csoType = null!;
    private MetaverseObjectType _person = null!;
    private MetaverseAttribute _employeeId = null!;

    [SetUp]
    public void SetUp()
    {
        _application = new JimApplication(new Mock<IRepository>().Object);
        _syncRepository = new InMemorySyncRepository();
        _employeeId = new MetaverseAttribute { Id = 21, Name = "Employee ID", Type = AttributeDataType.Text };
        _person = new MetaverseObjectType { Id = 1, Name = "Person", Attributes = [_employeeId] };
        _csoType = new ConnectedSystemObjectType
        {
            Id = ObjectTypeId, Name = "person", ConnectedSystemId = ConnectedSystemId,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "employeeNumber", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "hireDate", Type = AttributeDataType.DateTime }
            ]
        };
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    private ObjectMatchingRule Rule(bool syncRuleMode = false) => new()
    {
        Id = RuleId,
        ConnectedSystemObjectType = syncRuleMode ? null : _csoType,
        ConnectedSystemObjectTypeId = syncRuleMode ? null : ObjectTypeId,
        MetaverseObjectType = syncRuleMode ? null : _person,
        MetaverseObjectTypeId = syncRuleMode ? null : _person.Id,
        SyncRuleId = syncRuleMode ? SyncRuleId : null,
        TargetMetaverseAttribute = _employeeId,
        TargetMetaverseAttributeId = _employeeId.Id,
        Sources = [new ObjectMatchingRuleSource { Expression = "Replace(cs[\"employeeNumber\"], \"EMP-\", \"\")" }]
    };

    private SynchronisationController Controller(Action<Mock<IConnectedSystemRepository>>? arrange = null)
    {
        var connectedSystems = new Mock<IConnectedSystemRepository>();
        connectedSystems.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(new ConnectedSystem { Id = ConnectedSystemId, Name = "HR" });
        connectedSystems.Setup(r => r.GetObjectTypeAsync(ObjectTypeId)).ReturnsAsync(_csoType);
        connectedSystems.Setup(r => r.GetObjectMatchingRuleAsync(RuleId)).ReturnsAsync(Rule());
        var syncRule = new SyncRule
        {
            Id = SyncRuleId, Name = "HR Inbound", Direction = SyncRuleDirection.Import, ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectType = _csoType, ConnectedSystemObjectTypeId = ObjectTypeId, MetaverseObjectType = _person
        };
        syncRule.ObjectMatchingRules.Add(Rule(syncRuleMode: true));
        connectedSystems.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(syncRule);
        arrange?.Invoke(connectedSystems);
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);
        repository.Setup(r => r.Metaverse).Returns(new Mock<IMetaverseRepository>().Object);

        _application.Dispose();
        _application = new JimApplication(repository.Object, syncRepository: _syncRepository);
        return new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, _application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new List<Claim> { new("sub", Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Administrator") }, "TestAuth"))
                }
            }
        };
    }

    private MetaverseObject SeedIdentity(string employeeId)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _person };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = mvo, Attribute = _employeeId, AttributeId = _employeeId.Id, StringValue = employeeId });
        _syncRepository.SeedMetaverseObject(mvo);
        return mvo;
    }

    private static Dictionary<string, object?> Json(string json) => JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

    private static TestMatchingRuleResponse Ok(IActionResult result)
    {
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        return (TestMatchingRuleResponse)((OkObjectResult)result).Value!;
    }

    [Test]
    public async Task TestConnectedSystemMatchingRuleAsync_Samples_ReturnsTheMatchValuesAsync()
    {
        var response = Ok(await Controller().TestConnectedSystemMatchingRuleAsync(ConnectedSystemId, RuleId,
            new TestMatchingRuleRequest { Attributes = Json("{\"employeeNumber\": \"EMP-48213\"}") }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.True);
            Assert.That(response.Outcome, Is.EqualTo(MatchValueResolutionKind.Values));
            Assert.That(response.ValueLines, Is.EqualTo(new[] { "48213" }));
            Assert.That(response.ValueType, Is.EqualTo(AttributeDataType.Text));
            Assert.That(response.Inputs.Single().Attribute, Is.EqualTo("employeeNumber"));
            Assert.That(response.Lookup, Is.Null);
        }
    }

    [Test]
    public async Task TestConnectedSystemMatchingRuleAsync_LookUp_ReportsTheIdentityAJoinWouldFindAsync()
    {
        var identity = SeedIdentity("48213");

        var response = Ok(await Controller().TestConnectedSystemMatchingRuleAsync(ConnectedSystemId, RuleId,
            new TestMatchingRuleRequest { Attributes = Json("{\"employeeNumber\": \"EMP-48213\"}"), LookUp = true }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Lookup!.Outcome, Is.EqualTo(ObjectMatchingLookupOutcome.WouldJoin));
            Assert.That(response.Lookup.MetaverseObjectId, Is.EqualTo(identity.Id));
        }
    }

    [Test]
    public async Task TestConnectedSystemMatchingRuleAsync_TwoIdentities_ListsTheCandidatesAsync()
    {
        SeedIdentity("48213");
        SeedIdentity("48213");

        var response = Ok(await Controller().TestConnectedSystemMatchingRuleAsync(ConnectedSystemId, RuleId,
            new TestMatchingRuleRequest { Attributes = Json("{\"employeeNumber\": \"48213\"}"), LookUp = true }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Lookup!.Outcome, Is.EqualTo(ObjectMatchingLookupOutcome.Ambiguous));
            Assert.That(response.Lookup.Candidates, Has.Count.EqualTo(2));
            Assert.That(response.Lookup.CandidateTotal, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task TestSyncRuleMatchingRuleAsync_LooksUpAgainstTheSynchronisationRulesMetaverseObjectTypeAsync()
    {
        var identity = SeedIdentity("48213");

        var response = Ok(await Controller().TestSyncRuleMatchingRuleAsync(SyncRuleId, RuleId,
            new TestMatchingRuleRequest { Attributes = Json("{\"employeeNumber\": \"48213\"}"), LookUp = true }));

        Assert.That(response.Lookup!.MetaverseObjectId, Is.EqualTo(identity.Id));
    }

    [Test]
    public async Task TestConnectedSystemMatchingRuleAsync_ConnectedSystemObject_LoadsItsValuesAsync()
    {
        var cso = new ConnectedSystemObject { Id = CsoId, ConnectedSystemId = ConnectedSystemId, TypeId = ObjectTypeId };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 11, Attribute = _csoType.Attributes[0], StringValue = "EMP-1001" });
        var controller = Controller(connectedSystems =>
            connectedSystems.Setup(r => r.GetConnectedSystemObjectAsync(ConnectedSystemId, CsoId)).ReturnsAsync(cso));

        var response = Ok(await controller.TestConnectedSystemMatchingRuleAsync(ConnectedSystemId, RuleId,
            new TestMatchingRuleRequest { ConnectedSystemObjectId = CsoId }));

        Assert.That(response.ValueLines, Is.EqualTo(new[] { "1001" }));
    }

    [Test]
    public async Task TestConnectedSystemMatchingRuleAsync_UnconvertibleSample_IsNotValidAsync()
    {
        var response = Ok(await Controller().TestConnectedSystemMatchingRuleAsync(ConnectedSystemId, RuleId,
            new TestMatchingRuleRequest { Attributes = Json("{\"employeeNumber\": [\"1\", \"2\"]}") }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.False);
            Assert.That(response.Outcome, Is.Null);
            Assert.That(response.InputErrors!.Single(), Does.Contain("single-valued"));
        }
    }

    [Test]
    public async Task TestConnectedSystemMatchingRuleAsync_RuleOfAnotherConnectedSystem_IsNotFoundAsync()
    {
        var controller = Controller(connectedSystems =>
            connectedSystems.Setup(r => r.GetConnectedSystemCoreAsync(99, It.IsAny<bool>())).ReturnsAsync(new ConnectedSystem { Id = 99 }));

        var result = await controller.TestConnectedSystemMatchingRuleAsync(99, RuleId, new TestMatchingRuleRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task TestConnectedSystemMatchingRuleAsync_UnknownConnectedSystemObject_IsNotFoundAsync()
    {
        var result = await Controller().TestConnectedSystemMatchingRuleAsync(ConnectedSystemId, RuleId,
            new TestMatchingRuleRequest { ConnectedSystemObjectId = CsoId });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task TestSyncRuleMatchingRuleAsync_UnknownRule_IsNotFoundAsync()
    {
        var result = await Controller().TestSyncRuleMatchingRuleAsync(SyncRuleId, 99, new TestMatchingRuleRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }
}
