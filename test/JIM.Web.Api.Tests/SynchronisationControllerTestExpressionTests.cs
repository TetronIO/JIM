// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
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
/// Tests for SynchronisationController.TestExpression (#2179, F1-15): the REST tester returns its result as the
/// lines the portal's tester shows, one value per line in JIM's one text form, so the same expression reads the same
/// on every surface.
/// </summary>
[TestFixture]
public class SynchronisationControllerTestExpressionTests
{
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _application = new JimApplication(new Mock<IRepository>().Object);
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            _application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object)
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

    [TearDown]
    public void TearDown() => _application.Dispose();

    private TestExpressionResponse Test(string expression, Dictionary<string, object?>? mvAttributes = null) =>
        Ok(_controller.TestExpressionAsync(new TestExpressionRequest { Expression = expression, MvAttributes = mvAttributes }).GetAwaiter().GetResult());

    private static TestExpressionResponse Ok(IActionResult result)
    {
        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        return (TestExpressionResponse)ok!.Value!;
    }

    [Test]
    public void TestExpression_ListResult_ReturnsOneLinePerValue()
    {
        var response = Test("Split(\"a;b\", \";\")");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.True);
            Assert.That(response.ResultLines, Is.EqualTo(new[] { "a", "b" }));
        }
    }

    [Test]
    public void TestExpression_DateResult_ReturnsJimsDateTextForm()
    {
        var response = Test("mv[\"When\"]", new Dictionary<string, object?> { ["When"] = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc) });

        Assert.That(response.ResultLines, Is.EqualTo(new[] { "2026-03-15 10:30:00Z" }));
    }

    [Test]
    public void TestExpression_BinaryResult_DescribesTheValueRatherThanEncodingIt()
    {
        var response = Test("mv[\"Photo\"]", new Dictionary<string, object?> { ["Photo"] = new byte[] { 1, 2, 3 } });

        Assert.That(response.ResultLines, Is.EqualTo(new[] { "(Binary value, 3 bytes)" }));
    }

    [Test]
    public void TestExpression_NullResult_ReturnsNoLines()
    {
        var response = Test("mv[\"Missing\"]");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.True);
            Assert.That(response.ResultLines, Is.Empty);
        }
    }

    [Test]
    public void TestExpression_ExpressionFails_ReturnsNoLines()
    {
        var response = Test("Split(\"a,b\", \",\")[5]");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.False);
            Assert.That(response.ResultLines, Is.Null);
        }
    }

    #region With an Attribute Flow (#2055): the same pipeline synchronisation runs

    private const int SyncRuleId = 7;
    private const int MappingId = 70;

    /// <summary>
    /// An application whose repository holds one import Synchronisation Rule with one Attribute Flow, from an object
    /// type with a DateTime <c>hireDate</c>, a Text <c>givenName</c> and a multi-valued <c>mail</c>, to the target.
    /// </summary>
    private SynchronisationController ControllerWithFlow(AttributeDataType targetType,
        MissingInputBehaviour missingInputBehaviour = MissingInputBehaviour.EvaluateAnyway)
    {
        var csoType = new ConnectedSystemObjectType
        {
            Id = 1, Name = "person",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "hireDate", Type = AttributeDataType.DateTime },
                new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "givenName", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 14, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued }
            ]
        };
        var target = new MetaverseAttribute { Id = 21, Name = "Target", Type = targetType };
        var rule = new SyncRule
        {
            Id = SyncRuleId, Name = "HR Inbound", Direction = SyncRuleDirection.Import,
            ConnectedSystemObjectType = csoType, MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", Attributes = [target] }
        };
        var mapping = new SyncRuleMapping { Id = MappingId, SyncRule = rule, TargetMetaverseAttribute = target };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "cs[\"givenName\"]", MissingInputBehaviour = missingInputBehaviour });
        rule.AttributeFlowRules.Add(mapping);

        var connectedSystems = new Mock<IConnectedSystemRepository>();
        connectedSystems.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(rule);
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);

        _application.Dispose();
        _application = new JimApplication(repository.Object);
        return new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, _application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object)
        {
            ControllerContext = _controller.ControllerContext
        };
    }

    private static TestExpressionRequest FlowRequest(string expression, Dictionary<string, object?>? csAttributes = null) => new()
    {
        Expression = expression, SyncRuleId = SyncRuleId, MappingId = MappingId, CsAttributes = csAttributes
    };

    [Test]
    public async Task TestExpressionAsync_FlowWithDateInputCastInExpression_ReturnsTheFormattedDateAsync()
    {
        // Scenario 14: without the flow the sample is text and the cast fails; with it, the sample is the date the
        // Connected System Object would hold.
        var controller = ControllerWithFlow(AttributeDataType.Text);
        var json = JsonSerializer.Deserialize<Dictionary<string, object?>>("{\"hireDate\": \"2026-03-02\"}");

        var response = Ok(await controller.TestExpressionAsync(FlowRequest("FormatDate((DateTime)cs[\"hireDate\"], \"yyyy-MM-dd\")", json)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.True, response.ErrorMessage);
            Assert.That(response.Outcome, Is.EqualTo(AttributeFlowOutcome.Values));
            Assert.That(response.ValueLines, Is.EqualTo(new[] { "2026-03-02" }));
            Assert.That(response.TargetAttributeName, Is.EqualTo("Target"));
        }
    }

    [Test]
    public async Task TestExpressionAsync_FlowResultTheTargetCannotHold_IsNotValidAsync()
    {
        // The false pass: the Expression evaluates, but a Number attribute cannot hold its result.
        var controller = ControllerWithFlow(AttributeDataType.Number);

        var response = Ok(await controller.TestExpressionAsync(FlowRequest("cs[\"givenName\"]",
            new Dictionary<string, object?> { ["givenName"] = "Priya" })));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.False);
            Assert.That(response.Outcome, Is.EqualTo(AttributeFlowOutcome.Error));
            Assert.That(response.ErrorReason, Is.EqualTo(AttributeFlowErrorReason.ResultNotConvertible));
            Assert.That(response.ResultLines, Is.EqualTo(new[] { "Priya" }), "the expression's own result is still shown");
            Assert.That(response.ErrorMessage, Does.Contain("Number"));
        }
    }

    [Test]
    public async Task TestExpressionAsync_FlowWithNullInput_TreatsItAsNoValueAndAppliesMissingInputBehaviourAsync()
    {
        var controller = ControllerWithFlow(AttributeDataType.Text, MissingInputBehaviour.FailMapping);
        var json = JsonSerializer.Deserialize<Dictionary<string, object?>>("{\"givenName\": null}");

        var response = Ok(await controller.TestExpressionAsync(FlowRequest("cs[\"givenName\"]", json)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Outcome, Is.EqualTo(AttributeFlowOutcome.Error));
            Assert.That(response.ErrorReason, Is.EqualTo(AttributeFlowErrorReason.MissingInputFailedMapping));
            Assert.That(response.MissingInputs, Is.EqualTo(new[] { "cs[\"givenName\"]" }));
        }
    }

    [Test]
    public void TestExpression_JsonArraySample_ReadsAsSeveralValues()
    {
        // #2053: a JSON array is several values on the plain tester too, exactly as an Attribute Flow test reads it.
        var json = JsonSerializer.Deserialize<Dictionary<string, object?>>("{\"Groups\": [\"Users\", \"Admins\"]}");

        var response = Test("Count(mv[\"Groups\"]) + \":\" + Join(mv[\"Groups\"], \";\")", json);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.True, response.ErrorMessage);
            Assert.That(response.Result, Is.EqualTo("2:Admins;Users"));
        }
    }

    [Test]
    public async Task TestExpressionAsync_FlowWithJsonArray_GivesAMultiValuedInputSeveralValuesAsync()
    {
        var controller = ControllerWithFlow(AttributeDataType.Text);
        var json = JsonSerializer.Deserialize<Dictionary<string, object?>>("{\"mail\": [\"a@example.org\", \"b@example.org\"]}");

        var response = Ok(await controller.TestExpressionAsync(FlowRequest("cs[\"mail\"]", json)));

        // Both values reach the expression (#2053), so a single-valued target is refused rather than given one of them.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.InputErrors, Is.Null.Or.Empty);
            Assert.That(response.Outcome, Is.EqualTo(AttributeFlowOutcome.Error));
            Assert.That(response.ErrorReason, Is.EqualTo(AttributeFlowErrorReason.ResultMultiValued));
        }
    }

    [Test]
    public async Task TestExpressionAsync_FlowWithTextThatIsNotADate_ReturnsTheInputErrorAsync()
    {
        var controller = ControllerWithFlow(AttributeDataType.Text);

        var response = Ok(await controller.TestExpressionAsync(FlowRequest("cs[\"hireDate\"]",
            new Dictionary<string, object?> { ["hireDate"] = "next Tuesday" })));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsValid, Is.False);
            Assert.That(response.Outcome, Is.Null);
            Assert.That(response.InputErrors, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task TestExpressionAsync_MappingIdWithoutSyncRuleId_IsBadRequestAsync()
    {
        var result = await _controller.TestExpressionAsync(new TestExpressionRequest { Expression = "1", MappingId = MappingId });

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task TestExpressionAsync_UnknownMapping_IsNotFoundAsync()
    {
        var controller = ControllerWithFlow(AttributeDataType.Text);

        var result = await controller.TestExpressionAsync(new TestExpressionRequest { Expression = "1", SyncRuleId = SyncRuleId, MappingId = 999 });

        Assert.That(result, Is.TypeOf<NotFoundObjectResult>());
    }

    #endregion
}
