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
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
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
/// The Scope tester over REST (#2055): <c>POST sync-rules/{id}/scoping-criteria/test</c> evaluates a Synchronisation
/// Rule's scoping criteria against sample values or a real object, as of a chosen instant, by the evaluation
/// synchronisation runs. The evaluation itself is proven by <c>ScopeTesterTests</c>; these pin the API's shape.
/// </summary>
[TestFixture]
public class SynchronisationControllerTestScopeTests
{
    private const int SyncRuleId = 7;
    private const int ConnectedSystemId = 3;
    private static readonly Guid CsoId = Guid.Parse("0c2a7a3e-1d2b-4c5e-9f60-718293a4b5c6");
    private static readonly Guid MvoId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    private static readonly DateTime AsOf = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private JimApplication _application = null!;
    private ConnectedSystemObjectType _csoType = null!;
    private MetaverseObjectType _mvType = null!;

    [SetUp]
    public void SetUp()
    {
        _application = new JimApplication(new Mock<IRepository>().Object);
        _csoType = new ConnectedSystemObjectType
        {
            Id = 1, Name = "person",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "department", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "leaveDate", Type = AttributeDataType.DateTime }
            ]
        };
        _mvType = new MetaverseObjectType
        {
            Id = 1, Name = "Person",
            Attributes = [new MetaverseAttribute { Id = 21, Name = "Department", Type = AttributeDataType.Text }]
        };
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    /// <summary>
    /// A controller whose repository holds one Synchronisation Rule scoped to the Finance department, leaving scope 30
    /// days after its leave date (import), or to the Finance department of the Metaverse Object (export).
    /// </summary>
    private SynchronisationController Controller(SyncRuleDirection direction = SyncRuleDirection.Import,
        Action<Mock<IConnectedSystemRepository>, Mock<IMetaverseRepository>>? arrange = null)
    {
        var rule = new SyncRule
        {
            Id = SyncRuleId, Name = "HR Import", Direction = direction, ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectType = _csoType, ConnectedSystemObjectTypeId = _csoType.Id, MetaverseObjectType = _mvType
        };
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        if (direction == SyncRuleDirection.Export)
        {
            group.Criteria.Add(new SyncRuleScopingCriteria
            {
                MetaverseAttribute = _mvType.Attributes[0], ComparisonType = SearchComparisonType.Equals, StringValue = "Finance"
            });
        }
        else
        {
            group.Criteria.Add(new SyncRuleScopingCriteria
            {
                ConnectedSystemAttribute = _csoType.Attributes[0], ComparisonType = SearchComparisonType.Equals, StringValue = "Finance"
            });
            group.Criteria.Add(new SyncRuleScopingCriteria
            {
                ConnectedSystemAttribute = _csoType.Attributes[1], ComparisonType = SearchComparisonType.GreaterThan,
                ValueMode = DateCriteriaValueMode.Relative, RelativeCount = 30, RelativeUnit = RelativeDateUnit.Days,
                RelativeDirection = RelativeDateDirection.Ago
            });
        }
        rule.ObjectScopingCriteriaGroups.Add(group);

        var connectedSystems = new Mock<IConnectedSystemRepository>();
        connectedSystems.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(rule);
        var metaverse = new Mock<IMetaverseRepository>();
        arrange?.Invoke(connectedSystems, metaverse);
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);
        repository.Setup(r => r.Metaverse).Returns(metaverse.Object);

        _application.Dispose();
        _application = new JimApplication(repository.Object);
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

    private static Dictionary<string, object?> Json(string json) => JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

    private static TestScopeResponse Ok(IActionResult result)
    {
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        return (TestScopeResponse)((OkObjectResult)result).Value!;
    }

    [Test]
    public async Task TestScopeAsync_SampleValues_ReturnsTheOutcomeExplanationAndNextChangeAsync()
    {
        var response = Ok(await Controller().TestScopeAsync(SyncRuleId,
            new TestScopeRequest { Attributes = Json("{\"department\": \"Finance\", \"leaveDate\": \"2026-11-01\"}"), AsOf = AsOf }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.True);
            Assert.That(response.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
            Assert.That(response.EvaluatedAt, Is.EqualTo(AsOf));
            Assert.That(response.Explanation!.Criteria.Select(c => c.Outcome), Is.All.EqualTo(ScopingCriterionOutcome.Met));
            Assert.That(response.NextChange!.At, Is.EqualTo(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(response.NextChange.Outcome, Is.EqualTo(ScopingRuleOutcome.OutOfScope));
            Assert.That(response.Inputs.Select(i => (i.Attribute, i.Type)),
                Is.EqualTo(new[] { ("department", AttributeDataType.Text), ("leaveDate", AttributeDataType.DateTime) }));
            Assert.That(response.Inputs[1].ValueLines, Is.EqualTo(new[] { "2026-11-01" }));
        }
    }

    [Test]
    public async Task TestScopeAsync_NoAsOf_EvaluatesAtTheCurrentTimeAsync()
    {
        var before = DateTime.UtcNow;

        var response = Ok(await Controller().TestScopeAsync(SyncRuleId, new TestScopeRequest { Attributes = Json("{\"department\": \"Finance\"}") }));

        Assert.That(response.EvaluatedAt, Is.GreaterThanOrEqualTo(before));
    }

    [Test]
    public async Task TestScopeAsync_UnconvertibleSample_IsNotValidAndListsTheInputErrorAsync()
    {
        var response = Ok(await Controller().TestScopeAsync(SyncRuleId,
            new TestScopeRequest { Attributes = Json("{\"leaveDate\": \"soon\"}"), AsOf = AsOf }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.False);
            Assert.That(response.Outcome, Is.Null);
            Assert.That(response.InputErrors!.Single(), Does.Contain("soon"));
        }
    }

    [Test]
    public async Task TestScopeAsync_ConnectedSystemObject_LoadsItsValuesAndASampleReplacesOneAsync()
    {
        var cso = new ConnectedSystemObject { Id = CsoId, TypeId = _csoType.Id, ConnectedSystemId = ConnectedSystemId };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 11, Attribute = _csoType.Attributes[0], StringValue = "Sales" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 12, Attribute = _csoType.Attributes[1], DateTimeValue = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) });
        var controller = Controller(arrange: (connectedSystems, _) =>
            connectedSystems.Setup(r => r.GetConnectedSystemObjectAsync(ConnectedSystemId, CsoId)).ReturnsAsync(cso));

        var loaded = Ok(await controller.TestScopeAsync(SyncRuleId, new TestScopeRequest { ConnectedSystemObjectId = CsoId, AsOf = AsOf }));
        var edited = Ok(await controller.TestScopeAsync(SyncRuleId,
            new TestScopeRequest { ConnectedSystemObjectId = CsoId, AsOf = AsOf, Attributes = Json("{\"department\": \"Finance\"}") }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded.Outcome, Is.EqualTo(ScopingRuleOutcome.OutOfScope));
            Assert.That(loaded.Inputs[0].ValueLines, Is.EqualTo(new[] { "Sales" }));
            Assert.That(edited.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
        }
    }

    [Test]
    public async Task TestScopeAsync_MetaverseObjectForAnExportRule_LoadsItsValuesAsync()
    {
        var mvo = new MetaverseObject { Id = MvoId, Type = _mvType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { AttributeId = 21, Attribute = _mvType.Attributes[0], StringValue = "Finance" });
        var controller = Controller(SyncRuleDirection.Export, (_, metaverse) =>
            metaverse.Setup(r => r.GetMetaverseObjectAsync(MvoId)).ReturnsAsync(mvo));

        var response = Ok(await controller.TestScopeAsync(SyncRuleId, new TestScopeRequest { MetaverseObjectId = MvoId, AsOf = AsOf }));

        Assert.That(response.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
    }

    [Test]
    public async Task TestScopeAsync_ConnectedSystemObjectForAnExportRule_IsABadRequestAsync()
    {
        var result = await Controller(SyncRuleDirection.Export).TestScopeAsync(SyncRuleId, new TestScopeRequest { ConnectedSystemObjectId = CsoId });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task TestScopeAsync_MetaverseObjectForAnImportRule_IsABadRequestAsync()
    {
        var result = await Controller().TestScopeAsync(SyncRuleId, new TestScopeRequest { MetaverseObjectId = MvoId });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task TestScopeAsync_UnknownConnectedSystemObject_IsNotFoundAsync()
    {
        var result = await Controller().TestScopeAsync(SyncRuleId, new TestScopeRequest { ConnectedSystemObjectId = CsoId });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task TestScopeAsync_UnknownSyncRule_IsNotFoundAsync()
    {
        var result = await Controller().TestScopeAsync(99, new TestScopeRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }
}
